// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GitHubAuthProviderTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders
open ToolUp.AuthProviders.GitHubAuthConfig
open ToolUp.AuthProviders.GitHubOAuth

// ─── GitHub auth-provider contract tests ─────────────────────────────
//
// Drive `GitHubAuthProvider` against a stub `HttpMessageHandler` that
// impersonates GitHub's REST API — no real github.com credentials, so
// the pack runs green on any checkout. The handler routes on path:
//   GET  /user                       → identity (or 401 for a bad token)
//   GET  /user/memberships/orgs/{o}  → active membership (or 403)
//   POST /login/oauth/access_token   → code exchange
// and counts /user calls so the cache behaviour is observable.

/// Configurable stub. `userResponse` / `membershipActive` / `tokenBody`
/// let each test shape the GitHub side; `userCalls` records how many
/// `GET /user` requests reached the wire (cache verification).
type private StubGitHub
    (
        ?userStatus: HttpStatusCode,
        ?userJson: string,
        ?membershipActive: bool,
        ?membershipStatus: HttpStatusCode,
        ?tokenJson: string
    ) =
    inherit HttpMessageHandler()

    let userStatus = defaultArg userStatus HttpStatusCode.OK

    let userJson =
        defaultArg userJson """{"id":583231,"login":"octocat","name":"The Octocat","email":"octo@github.com"}"""

    let membershipActive = defaultArg membershipActive true
    let membershipStatus = defaultArg membershipStatus HttpStatusCode.OK

    let tokenJson =
        defaultArg tokenJson """{"access_token":"gho_stub","scope":"read:user","token_type":"bearer"}"""

    let mutable userCallCount = 0

    member _.UserCalls = userCallCount

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let path = request.RequestUri.AbsolutePath

        let json (status: HttpStatusCode) (body: string) =
            let resp = new HttpResponseMessage(status)
            resp.Content <- new StringContent(body)
            resp

        let resp =
            if path.EndsWith "/user" then
                Interlocked.Increment(&userCallCount) |> ignore
                json userStatus userJson
            elif path.Contains "/user/memberships/orgs/" then
                if membershipStatus <> HttpStatusCode.OK then
                    json membershipStatus """{"message":"Not a member"}"""
                else
                    json
                        HttpStatusCode.OK
                        (sprintf """{"state":"%s","role":"member"}""" (if membershipActive then "active" else "pending"))
            elif path.EndsWith "/access_token" then
                json HttpStatusCode.OK tokenJson
            else
                json HttpStatusCode.NotFound """{"message":"Not Found"}"""

        Task.FromResult resp

/// In-memory `ISecretStore` returning a fixed client secret.
type private FakeSecretStore(secret: string option) =
    interface ISecretStore with
        member _.GetSecret(_scope, _key) = async { return secret }
        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

let private mkContext () = DefaultHttpContext() :> HttpContext

let private bearerCtx (token: string) =
    let ctx = mkContext ()
    ctx.Request.Headers["Authorization"] <- StringValues("Bearer " + token)
    RequestContextBuilder.ofHttpContext ctx

let private emptyCtx () =
    mkContext () |> RequestContextBuilder.ofHttpContext

let private testConfig (handler: StubGitHub) allowedOrgs cacheTtl =
    let httpClient = new HttpClient(handler)

    let config = {
        GitHubAuthConfig.defaults with
            AllowedOrgs = allowedOrgs
            CacheTtlSeconds = cacheTtl
    }

    GitHubAuthProvider.fromConfigWith httpClient None config

let tests =
    testList "GitHubAuthProvider" [
        test "valid token resolves the GitHub identity" {
            let handler = new StubGitHub()
            let provider = testConfig handler [] 0

            let result =
                provider.ValidateRequest(bearerCtx "gho_valid") |> Async.RunSynchronously

            match result with
            | Ok user ->
                Expect.equal user.UserId "583231" "UserId is the numeric GitHub id"
                Expect.equal user.DisplayName "The Octocat" "DisplayName from name claim"
                Expect.equal user.Email (Some "octo@github.com") "Email carried through"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        test "missing token → GetUser anonymous, ValidateRequest Error" {
            let handler = new StubGitHub()
            let provider = testConfig handler [] 0

            let user = provider.GetUser(emptyCtx ()) |> Async.RunSynchronously
            Expect.isTrue (AuthenticatedUser.isAnonymous user) "no token → anonymous"

            match provider.ValidateRequest(emptyCtx ()) |> Async.RunSynchronously with
            | Error _ -> ()
            | Ok _ -> failtest "expected Error for missing token"
        }

        test "GitHub 401 → token rejected" {
            let handler =
                new StubGitHub(userStatus = HttpStatusCode.Unauthorized, userJson = """{"message":"Bad credentials"}""")

            let provider = testConfig handler [] 0

            match provider.ValidateRequest(bearerCtx "gho_bad") |> Async.RunSynchronously with
            | Error _ -> ()
            | Ok _ -> failtest "expected Error for a 401 from GitHub"

            let user = provider.GetUser(bearerCtx "gho_bad") |> Async.RunSynchronously
            Expect.isTrue (AuthenticatedUser.isAnonymous user) "lenient path → anonymous on 401"
        }

        test "org allow-list admits an active member" {
            let handler = new StubGitHub(membershipActive = true)
            let provider = testConfig handler [ "acme" ] 0

            match provider.ValidateRequest(bearerCtx "gho_member") |> Async.RunSynchronously with
            | Ok user -> Expect.equal user.UserId "583231" "member admitted"
            | Error e -> failtestf "expected Ok for active member, got %s" e
        }

        test "org allow-list rejects a non-member" {
            let handler = new StubGitHub(membershipStatus = HttpStatusCode.Forbidden)
            let provider = testConfig handler [ "acme" ] 0

            match provider.ValidateRequest(bearerCtx "gho_outsider") |> Async.RunSynchronously with
            | Error _ -> ()
            | Ok _ -> failtest "expected Error for a non-member under an org allow-list"
        }

        test "identity cache elides the second /user round-trip" {
            let handler = new StubGitHub()
            let provider = testConfig handler [] 300

            provider.ValidateRequest(bearerCtx "gho_cache")
            |> Async.RunSynchronously
            |> ignore

            provider.ValidateRequest(bearerCtx "gho_cache")
            |> Async.RunSynchronously
            |> ignore

            Expect.equal handler.UserCalls 1 "second validation served from cache"
        }

        test "TTL 0 disables the cache" {
            let handler = new StubGitHub()
            let provider = testConfig handler [] 0

            provider.ValidateRequest(bearerCtx "gho_nocache")
            |> Async.RunSynchronously
            |> ignore

            provider.ValidateRequest(bearerCtx "gho_nocache")
            |> Async.RunSynchronously
            |> ignore

            Expect.equal handler.UserCalls 2 "each request validates live when caching is off"
        }

        test "provider is cryptographically verified" {
            let handler = new StubGitHub()
            let provider = testConfig handler [] 0
            Expect.isTrue provider.IsCryptographicallyVerified "GitHub remote-token validation is not header-trust"
        }

        test "cleartext ApiBaseUrl is refused at construction" {
            let handler = new StubGitHub()
            let httpClient = new HttpClient(handler)

            let build () =
                GitHubAuthProvider.fromConfigWith httpClient None {
                    GitHubAuthConfig.defaults with
                        ApiBaseUrl = "http://api.github.com"
                }
                |> ignore

            Expect.throws build "http:// base URL must be rejected"
        }

        test "buildAuthorizeUrl carries the OAuth parameters" {
            let config = GitHubOAuthAppConfig.create "client-123" [ "read:user"; "user:email" ]
            let url = buildAuthorizeUrl config "https://app.example.com/callback" "state-xyz"

            Expect.stringContains url "client_id=client-123" "client id present"
            Expect.stringContains url "state=state-xyz" "state present"
            Expect.stringContains url "scope=read%3Auser%20user%3Aemail" "scopes url-encoded + space-joined"
            Expect.stringContains url "redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback" "redirect uri encoded"
        }

        test "exchangeCode returns the access token on success" {
            let handler = new StubGitHub()
            let httpClient = new HttpClient(handler)
            let secretStore = FakeSecretStore(Some "the-client-secret") :> ISecretStore
            let config = GitHubOAuthAppConfig.create "client-123" [ "read:user" ]

            match
                exchangeCode httpClient secretStore config "code-abc" "https://app/cb"
                |> Async.RunSynchronously
            with
            | Ok token -> Expect.equal token.AccessToken "gho_stub" "access token parsed"
            | Error e -> failtestf "expected Ok, got %s" e
        }

        test "exchangeCode surfaces a GitHub error body" {
            let handler =
                new StubGitHub(
                    tokenJson =
                        """{"error":"bad_verification_code","error_description":"The code passed is incorrect or expired."}"""
                )

            let httpClient = new HttpClient(handler)
            let secretStore = FakeSecretStore(Some "the-client-secret") :> ISecretStore
            let config = GitHubOAuthAppConfig.create "client-123" [ "read:user" ]

            match
                exchangeCode httpClient secretStore config "code-bad" "https://app/cb"
                |> Async.RunSynchronously
            with
            | Error msg -> Expect.stringContains msg "incorrect or expired" "GitHub error surfaced"
            | Ok _ -> failtest "expected Error for a bad code"
        }

        test "exchangeCode fails when the client secret is absent" {
            let handler = new StubGitHub()
            let httpClient = new HttpClient(handler)
            let secretStore = FakeSecretStore None :> ISecretStore
            let config = GitHubOAuthAppConfig.create "client-123" [ "read:user" ]

            match
                exchangeCode httpClient secretStore config "code-abc" "https://app/cb"
                |> Async.RunSynchronously
            with
            | Error _ -> ()
            | Ok _ -> failtest "expected Error when the client secret is missing"
        }
    ]
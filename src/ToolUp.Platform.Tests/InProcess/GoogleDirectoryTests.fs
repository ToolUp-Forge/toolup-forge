// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GoogleDirectoryTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders
open ToolUp.AuthProviders.GoogleDirectory
open ToolUp.AuthProviders.GoogleDirectoryAuth

// ─── Google Workspace directory companion tests ──────────────────────
//
// Drives `GoogleDirectory` against a stub `HttpMessageHandler`
// impersonating Google's three surfaces — the OAuth token endpoint, the
// Admin SDK Directory API and the Gmail API — so the pack runs green on
// any checkout with no Workspace tenant and no credentials.
//
// The service-account key is a REAL RSA key generated per test run, so
// the JWT-grant path is exercised end to end: the assertion is built,
// signed, and verified against the matching public key rather than
// stubbed out. That is the half of this companion that cannot be
// eyeballed, and it is the half that would fail silently.

// ─── Fixtures ────────────────────────────────────────────────────────

/// A generated service-account identity: the private-key PEM, the JSON
/// key file a deployment would store, and the public key the tests
/// verify signatures against.
type private FakeServiceAccount() =
    let rsa = RSA.Create 2048
    let pem = rsa.ExportPkcs8PrivateKeyPem()

    member _.ClientEmail = "toolup-directory@test-project.iam.gserviceaccount.com"
    member _.PrivateKeyPem = pem

    member this.Json =
        sprintf
            """{"type":"service_account","project_id":"test-project","client_email":%s,"private_key":%s,"client_id":"1234567890"}"""
            (JsonSerializer.Serialize this.ClientEmail)
            (JsonSerializer.Serialize pem)

    /// Verify an RS256 signature over `signingInput` against the public
    /// half of this account's key.
    member _.Verify(signingInput: byte[], signature: byte[]) =
        rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

/// In-memory `ISecretStore`. `None` models a deployment that composed
/// the companion but never stored the credential.
type private FakeSecretStore(value: string option) =
    interface ISecretStore with
        member _.GetSecret(_scope, _key) = async { return value }
        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

/// One request the stub observed, flattened to what the assertions care
/// about.
type private Observed = {
    Method: string
    Path: string
    Query: string
    Body: string
}

let private base64UrlDecode (s: string) =
    let padded =
        let t = s.Replace('-', '+').Replace('_', '/')

        match t.Length % 4 with
        | 0 -> t
        | 2 -> t + "=="
        | 3 -> t + "="
        | _ -> t

    Convert.FromBase64String padded

/// Stub transport for all three Google surfaces, routed on path.
///
///   POST …/token                                  → access token
///   GET  …/admin/directory/v1/users?query=…       → list
///   GET  …/admin/directory/v1/users/{id}          → get
///   POST …/gmail/v1/users/me/messages/send        → send
///
/// Every request is recorded, and the per-surface responses are
/// overridable so a test can shape a failure without a bespoke handler.
type private StubGoogle
    (
        ?listByName: string,
        ?listByEmail: string,
        ?listStatus: HttpStatusCode,
        ?getResponses: Map<string, HttpStatusCode * string>,
        ?sendStatus: HttpStatusCode,
        ?tokenStatus: HttpStatusCode,
        ?tokenBody: string
    ) =
    inherit HttpMessageHandler()

    let observed = ConcurrentQueue<Observed>()
    let listStatus = defaultArg listStatus HttpStatusCode.OK
    let sendStatus = defaultArg sendStatus HttpStatusCode.OK
    let tokenStatus = defaultArg tokenStatus HttpStatusCode.OK
    let getResponses = defaultArg getResponses Map.empty
    let listByName = defaultArg listByName """{"users":[]}"""
    let listByEmail = defaultArg listByEmail """{"users":[]}"""

    let tokenBody =
        defaultArg tokenBody """{"access_token":"ya29.stub","expires_in":3599,"token_type":"Bearer"}"""

    member _.Observed = observed |> Seq.toList

    member this.TokenRequests =
        this.Observed |> List.filter (fun r -> r.Path.EndsWith "/token")

    /// The decoded claim sets of every assertion the stub was handed.
    member this.Assertions =
        this.TokenRequests
        |> List.map (fun r ->
            let assertion =
                r.Body.Split '&'
                |> Array.pick (fun kv ->
                    if kv.StartsWith "assertion=" then
                        Some(Uri.UnescapeDataString(kv.Substring 10))
                    else
                        None)

            let parts = assertion.Split '.'

            let claims = Encoding.UTF8.GetString(base64UrlDecode parts[1]) |> JsonDocument.Parse

            assertion, claims.RootElement.Clone())

    override _.SendAsync(request: HttpRequestMessage, ct: CancellationToken) : Task<HttpResponseMessage> = task {
        let uri = request.RequestUri
        let path = uri.AbsolutePath

        let! body =
            if isNull request.Content then
                Task.FromResult ""
            else
                request.Content.ReadAsStringAsync ct

        // Recorded UNESCAPED: .NET's `Uri` canonicalisation is not
        // stable about which percent-escapes survive, so asserting
        // on the escaped form would be asserting on the BCL.
        let query = Uri.UnescapeDataString uri.Query

        observed.Enqueue {
            Method = request.Method.Method
            Path = path
            Query = query
            Body = body
        }

        let respond (status: HttpStatusCode) (payload: string) =
            let r = new HttpResponseMessage(status)
            r.Content <- new StringContent(payload, Encoding.UTF8, "application/json")
            r

        if path.EndsWith "/token" then
            return
                respond
                    tokenStatus
                    (if tokenStatus = HttpStatusCode.OK then
                         tokenBody
                     else
                         """{"error":"unauthorized_client","error_description":"Client is unauthorized to retrieve access tokens using this method"}""")
        elif path.EndsWith "/gmail/v1/users/me/messages/send" then
            return
                respond
                    sendStatus
                    (if sendStatus = HttpStatusCode.OK then
                         """{"id":"18f0","threadId":"18f0"}"""
                     else
                         """{"error":{"code":500,"message":"Backend Error"}}""")
        elif path.EndsWith "/admin/directory/v1/users" then
            if listStatus <> HttpStatusCode.OK then
                return respond listStatus """{"error":{"code":429,"message":"Rate Limit Exceeded"}}"""
            elif query.Contains "query=name:" then
                return respond HttpStatusCode.OK listByName
            else
                return respond HttpStatusCode.OK listByEmail
        elif path.Contains "/admin/directory/v1/users/" then
            let id = path.Substring(path.LastIndexOf '/' + 1)

            match Map.tryFind id getResponses with
            | Some(status, payload) -> return respond status payload
            | None -> return respond HttpStatusCode.NotFound """{"error":{"code":404,"message":"Not Found"}}"""
        else
            return respond HttpStatusCode.NotFound """{"error":{"code":404,"message":"unrouted"}}"""
    }

/// Token endpoint that serves the directory scope and refuses the Gmail
/// one — the exact half-configured delegation the validator's `Warning`
/// verdict exists for, and the shape a deployment lands in when it
/// pastes only the first scope into the admin console.
type private SelectiveScopeToken() =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, ct: CancellationToken) : Task<HttpResponseMessage> = task {
        let! body = request.Content.ReadAsStringAsync ct

        let assertion =
            body.Split '&'
            |> Array.pick (fun kv ->
                if kv.StartsWith "assertion=" then
                    Some(Uri.UnescapeDataString(kv.Substring 10))
                else
                    None)

        let payload = (assertion.Split '.')[1]

        let claims =
            JsonDocument.Parse(Encoding.UTF8.GetString(base64UrlDecode payload)).RootElement

        if claims.GetProperty("scope").GetString() = GmailSendScope then
            let r = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            r.Content <- new StringContent """{"error":"unauthorized_client"}"""
            return r
        else
            let r = new HttpResponseMessage(HttpStatusCode.OK)
            r.Content <- new StringContent """{"access_token":"ya29.stub","expires_in":3599}"""
            return r
    }

let private testConfig = {
    GoogleDirectoryConfig.defaults with
        TokenEndpoint = "https://stub.googleapis.test/token"
        DirectoryEndpoint = "https://stub.googleapis.test"
        GmailEndpoint = "https://stub.googleapis.test"
        Domain = "example.com"
        ImpersonatedAdmin = "directory-reader@example.com"
}

let private directoryOver (handler: StubGoogle) (secret: string option) (config: GoogleDirectoryConfig) =
    let http = new HttpClient(handler)
    GoogleDirectory.createWithClient http (FakeSecretStore secret :> ISecretStore) config

let private userJson (id: string) (email: string) (fullName: string) =
    sprintf
        """{"id":%s,"primaryEmail":%s,"name":{"fullName":%s,"givenName":"Given","familyName":"Family"}}"""
        (JsonSerializer.Serialize id)
        (JsonSerializer.Serialize email)
        (JsonSerializer.Serialize fullName)

let private usersJson (users: string list) =
    sprintf """{"users":[%s]}""" (String.Join(",", users))

let private notification = {
    Email = "invitee@example.com"
    TeamName = "Ré&search"
    InviterName = Some "Ada Lovelace"
    AppName = "ToolUp Pro"
    RedirectUrl = "https://app.example.com/"
    Role = Member
}

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "GoogleDirectory" [
        // ── The JWT-grant token exchange, on its own ──────────────────

        test "parseServiceAccountJson reads client_email, private_key and defaults token_uri" {
            let sa = FakeServiceAccount()

            match parseServiceAccountJson sa.Json with
            | Result.Ok key ->
                Expect.equal key.ClientEmail sa.ClientEmail "client_email carried through"
                Expect.equal key.PrivateKeyPem sa.PrivateKeyPem "private_key carried through"
                Expect.equal key.TokenUri DefaultTokenEndpoint "absent token_uri falls back to Google's endpoint"
            | Result.Error e -> failtestf "expected Ok, got Error %s" e
        }

        test "parseServiceAccountJson names the missing field rather than throwing" {
            match parseServiceAccountJson """{"type":"service_account","private_key":"x"}""" with
            | Result.Error e -> Expect.stringContains e "client_email" "the message names the missing field"
            | Result.Ok _ -> failtest "expected Error for a key file missing client_email"

            match parseServiceAccountJson "not json at all" with
            | Result.Error e ->
                Expect.stringContains e "could not be parsed" "unparseable JSON is a message, not a throw"
            | Result.Ok _ -> failtest "expected Error for unparseable JSON"

            match parseServiceAccountJson "" with
            | Result.Error _ -> ()
            | Result.Ok _ -> failtest "expected Error for an empty credential"
        }

        test "buildAssertion signs RS256 over the delegation claims" {
            let sa = FakeServiceAccount()
            let key = parseServiceAccountJson sa.Json |> Result.toOption |> Option.get
            let now = DateTimeOffset.FromUnixTimeSeconds 1_700_000_000L

            match buildAssertion key "admin@example.com" [ DirectoryReadonlyScope ] now with
            | Result.Error e -> failtestf "expected Ok, got Error %s" e
            | Result.Ok assertion ->
                let parts = assertion.Split '.'
                Expect.equal parts.Length 3 "a JWS compact serialisation has three parts"

                let header =
                    JsonDocument.Parse(Encoding.UTF8.GetString(base64UrlDecode parts[0])).RootElement

                Expect.equal (header.GetProperty("alg").GetString()) "RS256" "Google's JWT-bearer grant requires RS256"

                let claims =
                    JsonDocument.Parse(Encoding.UTF8.GetString(base64UrlDecode parts[1])).RootElement

                Expect.equal (claims.GetProperty("iss").GetString()) sa.ClientEmail "iss is the service account"

                Expect.equal
                    (claims.GetProperty("sub").GetString())
                    "admin@example.com"
                    "sub is the impersonated user — this is what makes it domain-wide delegation"

                Expect.equal
                    (claims.GetProperty("scope").GetString())
                    DirectoryReadonlyScope
                    "scope is the space-joined scope list"

                Expect.equal (claims.GetProperty("aud").GetString()) key.TokenUri "aud is the token endpoint"
                Expect.equal (claims.GetProperty("iat").GetInt64()) 1_700_000_000L "iat is the supplied instant"
                Expect.equal (claims.GetProperty("exp").GetInt64()) 1_700_003_600L "exp is iat + 1h, Google's ceiling"

                let signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])

                Expect.isTrue
                    (sa.Verify(signingInput, base64UrlDecode parts[2]))
                    "the signature verifies against the service account's public key"
        }

        test "buildAssertion reports an unusable private key without leaking it" {
            let key = {
                ClientEmail = "sa@example.iam.gserviceaccount.com"
                PrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nnot-a-key\n-----END PRIVATE KEY-----"
                TokenUri = DefaultTokenEndpoint
            }

            match buildAssertion key "admin@example.com" [ DirectoryReadonlyScope ] DateTimeOffset.UtcNow with
            | Result.Error e ->
                Expect.stringContains e "private key could not be loaded" "the message names the cause"
                Expect.isFalse (e.Contains "not-a-key") "the key material never reaches the error string"
            | Result.Ok _ -> failtest "expected Error for a malformed PEM"
        }

        // ── SearchUsers ───────────────────────────────────────────────

        test "SearchUsers queries display name AND email, merging and de-duplicating" {
            let sa = FakeServiceAccount()

            let handler =
                new StubGoogle(
                    listByName = usersJson [ userJson "101" "jane@example.com" "Jane Doe" ],
                    listByEmail =
                        usersJson [
                            userJson "101" "jane@example.com" "Jane Doe"
                            userJson "202" "janet@example.com" "Janet Roe"
                        ]
                )

            let directory = directoryOver handler (Some sa.Json) testConfig

            match directory.SearchUsers("jan", 10) |> Async.RunSynchronously with
            | Error e -> failtestf "expected Ok, got Error %s" e
            | Ok summaries ->
                Expect.equal (List.length summaries) 2 "the id present on both legs appears once"

                Expect.equal
                    (summaries |> List.map _.UserId)
                    [ "101"; "202" ]
                    "UserId is the Directory API id, in merged order"

                Expect.equal summaries.Head.DisplayName (Some "Jane Doe") "DisplayName is name.fullName"
                Expect.equal summaries.Head.Email (Some "jane@example.com") "Email is primaryEmail"

            let listCalls =
                handler.Observed
                |> List.filter (fun r -> r.Path.EndsWith "/admin/directory/v1/users")

            Expect.equal (List.length listCalls) 2 "Google ANDs query terms, so name and email are two calls"

            Expect.isTrue
                (listCalls |> List.exists (fun r -> r.Query.Contains "query=name:'jan'"))
                "one leg is the name: prefix term"

            Expect.isTrue
                (listCalls |> List.exists (fun r -> r.Query.Contains "query=email:'jan'"))
                "the other leg is the email: prefix term"

            Expect.isTrue
                (listCalls |> List.forall (fun r -> r.Query.Contains "domain=example.com"))
                "both legs are scoped to the configured Workspace domain"
        }

        test "SearchUsers mints a directory-scoped token impersonating the configured admin" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle()
            let directory = directoryOver handler (Some sa.Json) testConfig

            directory.SearchUsers("jane", 10) |> Async.RunSynchronously |> ignore

            let _, claims = handler.Assertions |> List.exactlyOne

            Expect.equal
                (claims.GetProperty("sub").GetString())
                "directory-reader@example.com"
                "directory reads impersonate ImpersonatedAdmin"

            Expect.equal
                (claims.GetProperty("scope").GetString())
                DirectoryReadonlyScope
                "and ask only for the read-only directory scope"
        }

        test "SearchUsers caches the token across calls" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle()
            let directory = directoryOver handler (Some sa.Json) testConfig

            directory.SearchUsers("jane", 10) |> Async.RunSynchronously |> ignore
            directory.SearchUsers("john", 10) |> Async.RunSynchronously |> ignore

            Expect.equal (List.length handler.TokenRequests) 1 "the second search reuses the cached bearer"
        }

        test "SearchUsers truncates to take and short-circuits a sub-2-character prefix" {
            let sa = FakeServiceAccount()

            let handler =
                new StubGoogle(
                    listByName =
                        usersJson [
                            userJson "1" "a@example.com" "A"
                            userJson "2" "b@example.com" "B"
                            userJson "3" "c@example.com" "C"
                        ]
                )

            let directory = directoryOver handler (Some sa.Json) testConfig

            match directory.SearchUsers("ab", 2) |> Async.RunSynchronously with
            | Ok summaries -> Expect.equal (List.length summaries) 2 "the merged result is capped at take"
            | Error e -> failtestf "expected Ok, got Error %s" e

            let before = List.length handler.Observed

            match directory.SearchUsers("a", 10) |> Async.RunSynchronously with
            | Ok summaries -> Expect.isEmpty summaries "a one-character prefix returns Ok []"
            | Error e -> failtestf "expected Ok, got Error %s" e

            Expect.equal (List.length handler.Observed) before "and does so without touching the network"
        }

        test "SearchUsers surfaces a transient Directory API failure as directory unavailable" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle(listStatus = HttpStatusCode.TooManyRequests)
            let directory = directoryOver handler (Some sa.Json) testConfig

            match directory.SearchUsers("jane", 10) |> Async.RunSynchronously with
            | Error e ->
                Expect.stringStarts e "directory unavailable:" "the error posture mirrors EntraDirectory"
                Expect.stringContains e "429" "the status is named"
            | Ok _ -> failtest "expected Error for a 429"
        }

        test "SearchUsers surfaces an absent credential as directory unavailable" {
            let handler = new StubGoogle()
            let directory = directoryOver handler None testConfig

            match directory.SearchUsers("jane", 10) |> Async.RunSynchronously with
            | Error e ->
                Expect.stringStarts e "directory unavailable:" "a missing credential is a directory failure"
                Expect.stringContains e "google_directory_service_account" "the message names the secret coordinate"
            | Ok _ -> failtest "expected Error when the secret store holds no credential"

            Expect.isEmpty handler.Observed "and nothing reaches the wire"
        }

        test "SearchUsers surfaces an ungranted delegation as directory unavailable" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle(tokenStatus = HttpStatusCode.Unauthorized)
            let directory = directoryOver handler (Some sa.Json) testConfig

            match directory.SearchUsers("jane", 10) |> Async.RunSynchronously with
            | Error e ->
                Expect.stringStarts e "directory unavailable:" "a refused grant is a directory failure"
                Expect.stringContains e "unauthorized_client" "Google's reason is preserved, not swallowed"
            | Ok _ -> failtest "expected Error when the token exchange is refused"
        }

        // ── ResolveUsers ──────────────────────────────────────────────

        test "ResolveUsers de-duplicates, resolves, and skips unknown ids" {
            let sa = FakeServiceAccount()

            let handler =
                new StubGoogle(
                    getResponses =
                        Map [
                            "101", (HttpStatusCode.OK, userJson "101" "jane@example.com" "Jane Doe")
                            "202", (HttpStatusCode.OK, userJson "202" "janet@example.com" "Janet Roe")
                        ]
                )

            let directory = directoryOver handler (Some sa.Json) testConfig

            match
                directory.ResolveUsers [ "101"; "202"; "101"; "  "; "999" ]
                |> Async.RunSynchronously
            with
            | Error e -> failtestf "expected Ok, got Error %s" e
            | Ok summaries ->
                Expect.equal (List.length summaries) 2 "the unknown id is skipped, never an error"

                Expect.equal (summaries |> List.map _.UserId |> List.sort) [ "101"; "202" ] "both known ids resolved"

                let getCalls =
                    handler.Observed
                    |> List.filter (fun r -> r.Path.Contains "/admin/directory/v1/users/")

                Expect.equal (List.length getCalls) 3 "duplicates and blanks are dropped before the fan-out"
        }

        test "ResolveUsers returns Ok [] for an empty input without touching the wire" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle()
            let directory = directoryOver handler (Some sa.Json) testConfig

            match directory.ResolveUsers [] |> Async.RunSynchronously with
            | Ok summaries -> Expect.isEmpty summaries "empty in, empty out"
            | Error e -> failtestf "expected Ok, got Error %s" e

            Expect.isEmpty handler.Observed "no token exchange, no request"
        }

        test "ResolveUsers surfaces a hard failure rather than degrading to raw ids" {
            let sa = FakeServiceAccount()

            let handler =
                new StubGoogle(
                    getResponses =
                        Map [
                            "101", (HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"Not Authorized"}}""")
                        ]
                )

            let directory = directoryOver handler (Some sa.Json) testConfig

            match directory.ResolveUsers [ "101" ] |> Async.RunSynchronously with
            | Error e ->
                Expect.stringStarts e "directory unavailable:" "a 403 is a real failure, unlike a 404"
                Expect.stringContains e "403" "the status is named"
            | Ok _ -> failtest "expected Error for a 403"
        }

        // ── NotifyInvitation ──────────────────────────────────────────

        test "NotifyInvitation with no sender is disabled, silently and without a request" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle()
            let directory = directoryOver handler (Some sa.Json) testConfig

            match directory.NotifyInvitation notification |> Async.RunSynchronously with
            | Error e ->
                Expect.stringStarts e "notification disabled:" "the invite handler swallows this and carries on"
                Expect.stringContains e "SenderUserId" "the message names the field to set"
            | Ok() -> failtest "expected the disabled Error when SenderUserId is None"

            Expect.isEmpty handler.Observed "nothing is attempted"
        }

        test "NotifyInvitation sends as the configured sender over the gmail.send scope" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle()

            let config = {
                testConfig with
                    SenderUserId = Some "invites@example.com"
            }

            let directory = directoryOver handler (Some sa.Json) config

            match directory.NotifyInvitation notification |> Async.RunSynchronously with
            | Ok() -> ()
            | Error e -> failtestf "expected Ok, got Error %s" e

            let _, claims = handler.Assertions |> List.exactlyOne

            Expect.equal
                (claims.GetProperty("sub").GetString())
                "invites@example.com"
                "the mail token impersonates the SENDER, not the directory admin"

            Expect.equal
                (claims.GetProperty("scope").GetString())
                GmailSendScope
                "and asks only for the send-only Gmail scope"

            let send =
                handler.Observed
                |> List.find (fun r -> r.Path.EndsWith "/gmail/v1/users/me/messages/send")

            Expect.equal send.Method "POST" "Gmail's send is a POST"

            let raw = JsonDocument.Parse(send.Body).RootElement.GetProperty("raw").GetString()

            let message = Encoding.UTF8.GetString(base64UrlDecode raw)

            Expect.stringContains message "From: invites@example.com" "the From: line is the configured mailbox"
            Expect.stringContains message "To: invitee@example.com" "addressed to the invitee"
            Expect.stringContains message "Subject: =?UTF-8?B?" "the subject is RFC 2047 encoded"
            Expect.stringContains message "Content-Type: text/html" "the body is the branded HTML invitation"
            Expect.stringContains message "\r\n\r\n" "headers and body are separated by CRLF CRLF"
            Expect.stringContains message "Ada Lovelace" "the inviter is named in the body"
            Expect.stringContains message "R&#233;&amp;search" "the team name is HTML-encoded into the body"
        }

        test "NotifyInvitation surfaces a Gmail failure as notification unavailable" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle(sendStatus = HttpStatusCode.InternalServerError)

            let config = {
                testConfig with
                    SenderUserId = Some "invites@example.com"
            }

            let directory = directoryOver handler (Some sa.Json) config

            match directory.NotifyInvitation notification |> Async.RunSynchronously with
            | Error e ->
                Expect.stringStarts e "notification unavailable:" "distinct from the directory error posture"
                Expect.stringContains e "500" "the status is named"
            | Ok() -> failtest "expected Error for a 500 from Gmail"
        }

        test "the directory and mail tokens are cached separately, not shared" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle()

            let config = {
                testConfig with
                    SenderUserId = Some "invites@example.com"
            }

            let directory = directoryOver handler (Some sa.Json) config

            directory.SearchUsers("jane", 10) |> Async.RunSynchronously |> ignore
            directory.NotifyInvitation notification |> Async.RunSynchronously |> ignore
            directory.SearchUsers("john", 10) |> Async.RunSynchronously |> ignore
            directory.NotifyInvitation notification |> Async.RunSynchronously |> ignore

            let subjects =
                handler.Assertions
                |> List.map (fun (_, claims) -> claims.GetProperty("sub").GetString())

            Expect.equal
                (List.sort subjects)
                [ "directory-reader@example.com"; "invites@example.com" ]
                "two subjects, one exchange each — a shared cache slot would have served the wrong impersonation"
        }

        // ── Health probe ──────────────────────────────────────────────

        test "the health probe is a live authenticated directory call" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle()
            let directory = directoryOver handler (Some sa.Json) testConfig
            let probe = GoogleDirectoryHealth.create directory

            Expect.equal probe.Name "user_directory:google_workspace" "stable probe name"
            Expect.equal probe.Kind HealthChecks.Readiness "a directory outage is a readiness concern"

            match probe.Check() |> Async.RunSynchronously with
            | HealthChecks.Healthy -> ()
            | other -> failtestf "expected Healthy, got %A" other

            Expect.isNonEmpty handler.Observed "the probe reached the wire rather than short-circuiting"
        }

        test "the health probe reports a broken credential as Unhealthy" {
            let handler = new StubGoogle()
            let directory = directoryOver handler None testConfig
            let probe = GoogleDirectoryHealth.create directory

            match probe.Check() |> Async.RunSynchronously with
            | HealthChecks.Unhealthy message ->
                Expect.stringContains message "directory unavailable" "the companion's reason is carried through"
            | other -> failtestf "expected Unhealthy, got %A" other
        }

        // ── Config validator ──────────────────────────────────────────

        test "preflight refuses an incomplete configuration" {
            let sa = FakeServiceAccount()
            let handler = new StubGoogle()
            let http = new HttpClient(handler)
            let secrets = FakeSecretStore(Some sa.Json) :> ISecretStore

            let validate config =
                let validator =
                    GoogleDirectoryConfigValidator.createWithClient http (TimeSpan.FromSeconds 5.0) secrets config

                validator.Validate() |> Async.RunSynchronously

            match validate { testConfig with Domain = "" } with
            | ConfigValidation.Error e -> Expect.stringContains e "Domain" "the missing field is named"
            | other -> failtestf "expected Error for a missing Domain, got %A" other

            match
                validate {
                    testConfig with
                        ImpersonatedAdmin = ""
                }
            with
            | ConfigValidation.Error e -> Expect.stringContains e "ImpersonatedAdmin" "the missing field is named"
            | other -> failtestf "expected Error for a missing ImpersonatedAdmin, got %A" other

            Expect.isEmpty handler.Observed "a shape failure is decided before any network call"
        }

        test "preflight refuses an absent credential and an ungranted directory delegation" {
            let sa = FakeServiceAccount()

            let validateWith (secret: string option) (tokenStatus: HttpStatusCode) =
                let handler = new StubGoogle(tokenStatus = tokenStatus)
                let http = new HttpClient(handler)

                let validator =
                    GoogleDirectoryConfigValidator.createWithClient
                        http
                        (TimeSpan.FromSeconds 5.0)
                        (FakeSecretStore secret :> ISecretStore)
                        testConfig

                validator.Validate() |> Async.RunSynchronously

            match validateWith None HttpStatusCode.OK with
            | ConfigValidation.Error e ->
                Expect.stringContains e "no service-account credential" "the missing secret is named"
            | other -> failtestf "expected Error for an absent credential, got %A" other

            match validateWith (Some sa.Json) HttpStatusCode.Unauthorized with
            | ConfigValidation.Error e ->
                Expect.stringContains e "directory scope not usable" "an ungranted delegation aborts startup"

                Expect.stringContains
                    e
                    "Domain-wide delegation"
                    "and the message says which console to fix it in — this is the failure people cannot diagnose"
            | other -> failtestf "expected Error for an ungranted delegation, got %A" other
        }

        test "preflight passes with no sender, and warns rather than aborts when only Gmail is ungranted" {
            let sa = FakeServiceAccount()

            let secrets = FakeSecretStore(Some sa.Json) :> ISecretStore

            let validate (handler: HttpMessageHandler) config =
                let validator =
                    GoogleDirectoryConfigValidator.createWithClient
                        (new HttpClient(handler))
                        (TimeSpan.FromSeconds 5.0)
                        secrets
                        config

                validator.Validate() |> Async.RunSynchronously

            match validate (new StubGoogle()) testConfig with
            | ConfigValidation.Ok -> ()
            | other -> failtestf "expected Ok with no sender configured, got %A" other

            match
                validate (new SelectiveScopeToken()) {
                    testConfig with
                        SenderUserId = Some "invites@example.com"
                }
            with
            | ConfigValidation.Warning message ->
                Expect.stringContains message "invitation email disabled" "the degradation is named"

                Expect.stringContains
                    message
                    "Directory search is unaffected"
                    "and it is explicitly not a reason to refuse to boot"
            | other -> failtestf "expected Warning for an ungranted Gmail delegation, got %A" other
        }
    ]
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GitHubAppFlowTests

open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.GitHubAppFlow
open ToolUp.Platform.Tests.Contracts

// ─── GitHub App IOAuthCredentialFlow tests ───────────────────────────
//
// Binds the shared `IOAuthCredentialFlowContract` pack to the GitHub App
// flow via a stub GitHub token/API layer + an in-memory secret store,
// then adds GitHub-specific assertions (refresh-token rotation
// persistence, grant revocation, missing-credential handling).

/// Stub for GitHub's token endpoint (`POST …/access_token`) + grant
/// revocation (`DELETE …/grant`). The token response rotates the refresh
/// token so the rotation-persistence path is observable.
type private StubGitHub() =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let path = request.RequestUri.AbsolutePath

        let resp =
            if path.EndsWith "/access_token" then
                let r = new HttpResponseMessage(HttpStatusCode.OK)

                r.Content <-
                    new StringContent(
                        """{"access_token":"gho_access","refresh_token":"ghr_rotated","expires_in":28800,"token_type":"bearer"}"""
                    )

                r
            elif path.Contains "/grant" then
                new HttpResponseMessage(HttpStatusCode.NoContent)
            else
                new HttpResponseMessage(HttpStatusCode.NotFound)

        Task.FromResult resp

/// Mutable in-memory `ISecretStore` so tests seed client credentials and
/// observe the rotated refresh token written back.
type private FakeSecretStore(seed: (string * string) list) =
    let store = ConcurrentDictionary<string, string>()
    do seed |> List.iter (fun (k, v) -> store[k] <- v)

    member _.Peek(key: string) : string option =
        match store.TryGetValue key with
        | true, v -> Some v
        | _ -> None

    interface ISecretStore with
        member _.GetSecret(_scope, key) = async {
            return
                match store.TryGetValue key with
                | true, v -> Some v
                | _ -> None
        }

        member _.SetSecret(_scope, key, value) = async {
            store[key] <- value
            return Ok()
        }

        member _.DeleteSecret(_scope, key) = async {
            store.TryRemove key |> ignore
            return Ok()
        }

        member _.ListKeys(_scope) = async { return store.Keys |> List.ofSeq }

// The contract pack's context uses ScopeId="team-scope-1", DataSourceId="ds-1".
let private seededCreds = [
    "github-client-id-ds-1", "client-abc"
    "github-client-secret-ds-1", "secret-xyz"
]

let private mkCtx () : OAuthFlowContext =
    OAuthFlowContext.forDataSource "team-scope-1" "ds-1" None

let private mkFlow (secretStore: ISecretStore) =
    let httpClient = new HttpClient(new StubGitHub())
    create httpClient secretStore (GitHubAppFlowConfig.create [ "read:user"; "repo" ])

let contractTests =
    let factory () =
        mkFlow (FakeSecretStore(seededCreds) :> ISecretStore)

    IOAuthCredentialFlowContract.tests "GitHubAppFlow" factory

let tests =
    testList "GitHubAppFlow" [
        contractTests

        test "SupportsPkce is false (GitHub has no PKCE)" {
            let flow = mkFlow (FakeSecretStore(seededCreds) :> ISecretStore)
            Expect.isFalse flow.SupportsPkce "GitHub Apps do not support PKCE"
        }

        testCaseAsync "ExchangeCode yields a refresh token and a future expiry"
        <| async {
            let flow = mkFlow (FakeSecretStore(seededCreds) :> ISecretStore)

            match! flow.ExchangeCode(mkCtx (), "the-code", "https://app/cb", None) with
            | Ok creds ->
                Expect.equal creds.RefreshToken "ghr_rotated" "refresh token captured"
                Expect.isSome creds.AccessToken "access token captured"
                Expect.isSome creds.ExpiresAt "expiry captured"
            | Error e -> failtestf "expected Ok, got %s" (OAuthError.toMessage e)
        }

        testCaseAsync "RefreshAccessToken persists the rotated refresh token"
        <| async {
            let secretStore = FakeSecretStore(seededCreds)

            let flow = mkFlow (secretStore :> ISecretStore)

            match! flow.RefreshAccessToken(mkCtx (), "old-refresh-token") with
            | Ok token ->
                Expect.equal token.Token "gho_access" "new access token returned"
                // GitHub rotates the refresh token; the flow must write the
                // new one back to the substrate's slot.
                Expect.equal
                    (secretStore.Peek "github-refresh-ds-1")
                    (Some "ghr_rotated")
                    "rotated refresh token persisted to the substrate slot"
            | Error e -> failtestf "expected Ok, got %s" (OAuthError.toMessage e)
        }

        testCaseAsync "Revoke deletes the grant and returns Ok"
        <| async {
            let flow = mkFlow (FakeSecretStore(seededCreds) :> ISecretStore)

            match! flow.Revoke(mkCtx (), "some-refresh-token") with
            | Ok() -> ()
            | Error e -> failtestf "expected Ok, got %s" (OAuthError.toMessage e)
        }

        testCaseAsync "missing client credentials → ClientCredentialMissing"
        <| async {
            let flow = mkFlow (FakeSecretStore([]) :> ISecretStore) // no seeded creds

            match! flow.ExchangeCode(mkCtx (), "the-code", "https://app/cb", None) with
            | Error(ClientCredentialMissing _) -> ()
            | Error e -> failtestf "expected ClientCredentialMissing, got %s" (OAuthError.toMessage e)
            | Ok _ -> failtest "expected Error when client credentials are absent"
        }
    ]
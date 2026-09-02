module ToolUp.Platform.Tests.InProcess.ProviderOAuthFlowTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Support

// ─── Phase 43.B — the Claude + OpenAI reference provider flows ─────
//
// Binds both shipped `IProviderOAuthFlow`s to BOTH conformance packs:
// `IOAuthCredentialFlowContract` (the OAuth half, inherited) and
// `IProviderOAuthFlowContract` (the provider-profile half). Plus the
// two refresh outcomes the generic pack cannot reach without steering
// the upstream — a successful renewal and a revoked grant.
//
// **The token endpoint is a function, not a socket.** Both flows are
// built through their `createWith` constructor, which takes the
// `OAuthTokenPost` seam. That is why this whole file runs on a fresh
// checkout with no Anthropic or OpenAI account — the phase's design
// guardrail made executable.

/// In-memory `ISecretStore` seeded with the deployment's OAuth client
/// credentials at whichever scope a pack drives.
type private SeededSecrets(flowNames: string list) =
    let entries = ConcurrentDictionary<string * string, string>()

    do
        // Both scope shapes the two packs use.
        for scopeId in [ "team-scope-1"; "team-team-1"; "s" ] do
            for flowName in flowNames do
                entries[(scopeId, ProviderOAuthFlow.clientIdKey flowName)] <- "client-id-value"
                entries[(scopeId, ProviderOAuthFlow.clientSecretKey flowName)] <- "client-secret-value"

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match entries.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            entries[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            entries.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys scopeId = async {
            return
                entries.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// A token endpoint that answers with a well-formed grant. Enough for
/// both packs' happy paths.
let private grantingPost: ProviderOAuthFlow.OAuthTokenPost =
    fun _url _fields -> async {
        return Ok """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600,"token_type":"bearer"}"""
    }

/// A token endpoint that answers the way an upstream answers a revoked
/// grant. `invalid_grant` is the case the substrate must read as
/// "reconnect", not as a transport blip.
let private rejectingPost: ProviderOAuthFlow.OAuthTokenPost =
    fun _url _fields -> async { return Ok """{"error":"invalid_grant","error_description":"refresh token revoked"}""" }

let private claudeFlow (post: ProviderOAuthFlow.OAuthTokenPost) =
    ClaudeOAuth.createWith post (SeededSecrets [ ClaudeOAuth.FlowName ] :> ISecretStore) ClaudeOAuth.defaults

let private openAiFlow (post: ProviderOAuthFlow.OAuthTokenPost) =
    OpenAIOAuth.createWith post (SeededSecrets [ OpenAIOAuth.FlowName ] :> ISecretStore) OpenAIOAuth.defaults

// ─── Contract-pack bindings ────────────────────────────────────────

let claudeOAuthContractTests =
    IOAuthCredentialFlowContract.tests "ClaudeOAuth" (fun () -> claudeFlow grantingPost :> IOAuthCredentialFlow)

let openAiOAuthContractTests =
    IOAuthCredentialFlowContract.tests "OpenAIOAuth" (fun () -> openAiFlow grantingPost :> IOAuthCredentialFlow)

let claudeProviderFlowContractTests =
    IProviderOAuthFlowContract.tests "ClaudeOAuth" (fun () -> claudeFlow grantingPost)

let openAiProviderFlowContractTests =
    IProviderOAuthFlowContract.tests "OpenAIOAuth" (fun () -> openAiFlow grantingPost)

// ─── Flow specifics + the two steered refresh outcomes ─────────────

let tests =
    let scope: StorageScope = {
        ScopeId = "team-1"
        Container = "team-team-1"
        Persist = true
    }

    testList "Phase 43.B — provider OAuth reference flows" [

        test "the reference flows name the AI provider descriptors a pasted-key entry would name" {
            Expect.equal
                (claudeFlow grantingPost).ProviderId
                ClaudeAIProvider.ProviderId
                "an OAuth-connected Claude entry and a pasted-key Claude entry must resolve through the same catalogue id"

            Expect.equal (openAiFlow grantingPost).ProviderId OpenAIProvider.ProviderId "same for OpenAI"
        }

        test "both reference flows declare PKCE" {
            Expect.isTrue (claudeFlow grantingPost).SupportsPkce "Claude accepts PKCE"
            Expect.isTrue (openAiFlow grantingPost).SupportsPkce "OpenAI accepts PKCE"
        }

        testCaseAsync "an exchange that returns no refresh_token is refused rather than bound"
        <| async {
            // A connection with no refresh token stops working within
            // the hour and cannot be recovered without re-consent.
            // Binding it would present as a successful connect.
            let post: ProviderOAuthFlow.OAuthTokenPost =
                fun _ _ -> async { return Ok """{"access_token":"at-1","expires_in":3600}""" }

            let flow = claudeFlow post

            let ctx =
                OAuthFlowContext.forCorrelation "team-scope-1" (OAuthCorrelationKey.providerEntry "anthropic")

            match! flow.ExchangeCode(ctx, "code-1", "https://example.com/cb", Some "verifier") with
            | Ok _ -> failtest "an offline-access-less grant must not be accepted"
            | Error err ->
                Expect.stringContains
                    (OAuthError.toMessage err)
                    "refresh_token"
                    "the diagnostic names the missing element so an operator fixes the consent request"
        }

        testCaseAsync "a successful scheduled refresh renews the cached token and marks the entry Healthy"
        <| async {
            let flow = claudeFlow grantingPost
            let secrets = SeededSecrets [ ClaudeOAuth.FlowName ] :> ISecretStore
            let profiles = ProviderOAuthTestSupport.memoryProviderProfile ()
            let correlation = OAuthCorrelationKey.providerEntry "anthropic"

            let credentials: OAuthCredentials = {
                RefreshToken = "rt-0"
                AccessToken = Some "at-0"
                // Already inside the lead-time window, so the first
                // dispatch actually refreshes rather than short-
                // circuiting.
                ExpiresAt = Some(DateTime.UtcNow.AddMinutes 1.0)
                IdToken = None
            }

            match!
                ProviderOAuthConnect.completeConnect
                    profiles
                    secrets
                    None
                    scope
                    "user-1"
                    flow
                    correlation
                    credentials
                    DateTime.UtcNow
            with
            | Error e -> failtestf "connect failed: %s" e
            | Ok() -> ()

            let! profile = profiles.Get scope
            let entry = profile.Value.Entries |> List.find (fun e -> e.Label = "anthropic")

            let! outcome =
                ProviderOAuthConnect.refreshEntry
                    profiles
                    secrets
                    None
                    scope
                    flow
                    entry
                    DateTime.UtcNow
                    ProviderOAuthConnect.DefaultLeadTime

            match outcome with
            | ProviderOAuthConnect.Refreshed _ -> ()
            | other -> failtestf "expected Refreshed; got %A" other

            let! cached = secrets.GetSecret(scope.Container, ProviderOAuthKeys.accessTokenKey flow.Name correlation)
            Expect.equal cached (Some "at-1") "the renewed access token replaced the cached one"

            let! after = profiles.Get scope
            let refreshed = after.Value.Entries |> List.find (fun e -> e.Label = "anthropic")
            Expect.equal refreshed.Health.Status ProviderHealthStatus.Healthy "a successful refresh clears the badge"
            Expect.equal refreshed.Health.RecentErrorCount 0 "and resets the rolling error count"
        }

        testCaseAsync "a revoked grant surfaces NeedsReauthorization on the entry"
        <| async {
            // The acceptance criterion: "a revoked refresh token
            // surfaces NeedsReauthorization in ProviderHealth". The
            // audit half rides the same call — `refreshEntry` emits
            // `OAuthRefreshFailed` when an `IAuditLog` is supplied.
            let flow = claudeFlow rejectingPost
            let secrets = SeededSecrets [ ClaudeOAuth.FlowName ] :> ISecretStore
            let profiles = ProviderOAuthTestSupport.memoryProviderProfile ()
            let audit = ProviderOAuthTestSupport.RecordingAuditLog()
            let correlation = OAuthCorrelationKey.providerEntry "anthropic"

            let credentials: OAuthCredentials = {
                RefreshToken = "rt-0"
                AccessToken = Some "at-0"
                ExpiresAt = Some(DateTime.UtcNow.AddMinutes 1.0)
                IdToken = None
            }

            match!
                ProviderOAuthConnect.completeConnect
                    profiles
                    secrets
                    None
                    scope
                    "user-1"
                    flow
                    correlation
                    credentials
                    DateTime.UtcNow
            with
            | Error e -> failtestf "connect failed: %s" e
            | Ok() -> ()

            let! profile = profiles.Get scope
            let entry = profile.Value.Entries |> List.find (fun e -> e.Label = "anthropic")

            let! outcome =
                ProviderOAuthConnect.refreshEntry
                    profiles
                    secrets
                    (Some(audit :> IAuditLog))
                    scope
                    flow
                    entry
                    DateTime.UtcNow
                    ProviderOAuthConnect.DefaultLeadTime

            match outcome with
            | ProviderOAuthConnect.NeedsReauthorization reason ->
                Expect.stringContains reason "invalid_grant" "the provider's own diagnostic is carried verbatim"
            | other -> failtestf "expected NeedsReauthorization; got %A" other

            let! after = profiles.Get scope
            let refreshed = after.Value.Entries |> List.find (fun e -> e.Label = "anthropic")

            Expect.equal
                refreshed.Health.Status
                ProviderHealthStatus.NeedsReauthorization
                "the entry's health says reconnect"

            Expect.isTrue
                (audit.Recorded
                 |> List.exists (fun e -> AuditEvent.eventTypeName e = "OAuthRefreshFailed"))
                "an audit event was emitted — a silent reauthorization requirement is one nobody acts on"
        }

        testCaseAsync "a network blip is transient, not a reauthorization demand"
        <| async {
            // The distinction matters: `NeedsReauthorization` is
            // terminal and puts a "reconnect" banner in front of the
            // user. A 503 must not do that.
            let post: ProviderOAuthFlow.OAuthTokenPost =
                fun _ _ -> async { return Error(NetworkError "connection reset") }

            let flow = claudeFlow post
            let secrets = SeededSecrets [ ClaudeOAuth.FlowName ] :> ISecretStore
            let profiles = ProviderOAuthTestSupport.memoryProviderProfile ()
            let correlation = OAuthCorrelationKey.providerEntry "anthropic"

            let credentials: OAuthCredentials = {
                RefreshToken = "rt-0"
                AccessToken = Some "at-0"
                ExpiresAt = Some(DateTime.UtcNow.AddMinutes 1.0)
                IdToken = None
            }

            let! _ =
                ProviderOAuthConnect.completeConnect
                    profiles
                    secrets
                    None
                    scope
                    "user-1"
                    flow
                    correlation
                    credentials
                    DateTime.UtcNow

            let! profile = profiles.Get scope
            let entry = profile.Value.Entries |> List.find (fun e -> e.Label = "anthropic")

            let! outcome =
                ProviderOAuthConnect.refreshEntry
                    profiles
                    secrets
                    None
                    scope
                    flow
                    entry
                    DateTime.UtcNow
                    ProviderOAuthConnect.DefaultLeadTime

            match outcome with
            | ProviderOAuthConnect.TransientFailure _ -> ()
            | other -> failtestf "expected TransientFailure; got %A" other

            let! after = profiles.Get scope
            let degraded = after.Value.Entries |> List.find (fun e -> e.Label = "anthropic")

            Expect.equal
                degraded.Health.Status
                ProviderHealthStatus.Degraded
                "a retryable failure degrades the badge; it does not demand a reconnect"
        }
    ]
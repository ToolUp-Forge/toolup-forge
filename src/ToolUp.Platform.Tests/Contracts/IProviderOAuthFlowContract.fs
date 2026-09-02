module ToolUp.Platform.Tests.Contracts.IProviderOAuthFlowContract

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets

// ─── IProviderOAuthFlow contract pack (Phase 43.B) ─────────────────
//
// Parametrised over any `IProviderOAuthFlow`. Covers the half
// `IOAuthCredentialFlowContract` does not: what happens to the
// PROVIDER PROFILE once a round-trip completes, and what the scheduled
// refresh does to an entry it cannot renew.
//
// Bind BOTH packs to a provider flow. This one deliberately does not
// re-test authorize-URL construction or PKCE — that is the OAuth half,
// and duplicating it here would mean two places to keep a security
// check in.
//
// **No network, by construction.** The connect cases feed
// `ProviderOAuthConnect.completeConnect` an `OAuthCredentials` value
// directly rather than driving the flow's `ExchangeCode`, so the
// binding contract is exercised without any upstream at all. The
// refresh cases DO call the flow's `RefreshAccessToken`, and a flow
// bound to this pack is expected to supply a stubbed token endpoint
// (`ClaudeOAuth.createWith` / `OpenAIOAuth.createWith` take one).
//
// **What is covered:**
//   - `ProviderId` / `DefaultEntryLabel` are populated (the two facts
//     the substrate needs to mint an entry).
//   - The derived refresh-token key is provider-entry-shaped and
//     distinct from the data-source-shaped key for the same id.
//   - `completeConnect` persists the refresh token AND binds an
//     `OAuthConnected` entry carrying the flow name + correlation.
//   - `completeConnect` REFUSES to convert a pasted-key entry of the
//     same label (which would orphan the user's stored API key).
//   - A reconnect preserves the user's tags and the probe's health.
//   - `refreshEntry` is a no-op on a not-yet-due entry and on a
//     pasted-key entry.
//   - `refreshEntry` flips health to `NeedsReauthorization` when no
//     refresh token is stored — the "revoked grant" shape, reachable
//     without steering the upstream.

/// Minimal in-memory `ISecretStore`. Scoped by `(scopeId, key)` so the
/// scope-isolation the substrate relies on is real in the fixture too.
type private MemorySecretStore() =
    let entries = ConcurrentDictionary<string * string, string>()

    member _.Seed(scopeId, key, value) = entries[(scopeId, key)] <- value

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

/// Minimal in-memory `IProviderProfile`, one profile per scope
/// container.
type private MemoryProviderProfile() =
    let profiles = ConcurrentDictionary<string, ProviderProfile>()

    interface IProviderProfile with
        member _.Get scope = async {
            match profiles.TryGetValue scope.Container with
            | true, p -> return Some p
            | _ -> return None
        }

        member _.Set(scope, profile) = async {
            profiles[scope.Container] <- profile
            return Ok()
        }

        member _.Clear scope = async { profiles.TryRemove scope.Container |> ignore }

        member this.ResolveEntry(scope, surface, context) = async {
            let! p = (this :> IProviderProfile).Get scope
            return p |> Option.bind (ProviderProfile.resolveEntry surface context)
        }

        member _.SetEntryHealth(scope, label, health) = async {
            match profiles.TryGetValue scope.Container with
            | true, p ->
                let updated = {
                    p with
                        Entries =
                            p.Entries
                            |> List.map (fun e -> if e.Label = label then { e with Health = health } else e)
                }

                profiles[scope.Container] <- updated
                return Ok()
            | _ -> return Ok()
        }

let tests (name: string) (factory: unit -> IProviderOAuthFlow) =

    let scope: StorageScope = {
        ScopeId = "team-1"
        Container = "team-team-1"
        Persist = true
    }

    /// The exchange's minted credentials. `ExpiresAt` sits INSIDE the
    /// refresh lead-time window on purpose: it makes the connected
    /// entry due at the next dispatch, so a refresh case reaches the
    /// behaviour it is testing rather than short-circuiting on
    /// `NotDue`. The not-due case pushes the expiry out explicitly.
    let credentials: OAuthCredentials = {
        RefreshToken = "refresh-token-value"
        AccessToken = Some "access-token-value"
        ExpiresAt = Some(DateTime.UtcNow.AddMinutes 1.0)
        IdToken = None
    }

    /// Fresh substrate per case — no state leaks between tests.
    let fixture () =
        let flow = factory ()
        let secrets = MemorySecretStore()
        let profiles = MemoryProviderProfile() :> IProviderProfile
        flow, secrets, profiles

    let connect (flow: IProviderOAuthFlow) secrets profiles label =
        ProviderOAuthConnect.completeConnect
            profiles
            (secrets :> ISecretStore)
            None
            scope
            "user-1"
            flow
            (OAuthCorrelationKey.providerEntry label)
            credentials
            DateTime.UtcNow

    testList $"{name} — IProviderOAuthFlow contract" [

        test "ProviderId and DefaultEntryLabel are populated" {
            let flow = factory ()

            Expect.isNonEmpty
                flow.ProviderId
                "ProviderId names the descriptor the minted entry carries — an empty one resolves to UnknownProvider"

            Expect.isNonEmpty flow.DefaultEntryLabel "DefaultEntryLabel is what a one-click connect names the entry"
        }

        test "the derived refresh-token key is provider-entry-shaped and distinct from the data-source key" {
            let flow = factory ()

            let providerKey =
                ProviderOAuthKeys.refreshTokenKey flow.Name (OAuthCorrelationKey.providerEntry "acme")

            let dataSourceKey =
                ProviderOAuthKeys.refreshTokenKey flow.Name (OAuthCorrelationKey.dataSource "acme")

            Expect.stringContains providerKey "acme" "the key names the subject"

            Expect.notEqual
                providerKey
                dataSourceKey
                "two subjects with the same id in different families must not collide on one secret"

            Expect.equal
                dataSourceKey
                $"{flow.Name}-refresh-acme"
                "the data-source key derivation is byte-identical to Phase 10e's — changing it would disconnect every already-connected data source"
        }

        testCaseAsync "completeConnect persists the refresh token and binds an OAuthConnected entry"
        <| async {
            let flow, secrets, profiles = fixture ()

            match! connect flow secrets profiles "anthropic" with
            | Error e -> failtestf "completeConnect failed: %s" e
            | Ok() -> ()

            let correlation = OAuthCorrelationKey.providerEntry "anthropic"
            let refreshKey = ProviderOAuthKeys.refreshTokenKey flow.Name correlation

            let! stored = (secrets :> ISecretStore).GetSecret(scope.Container, refreshKey)
            Expect.equal stored (Some credentials.RefreshToken) "the refresh token is persisted under the derived key"

            let! cachedAccess =
                (secrets :> ISecretStore)
                    .GetSecret(scope.Container, ProviderOAuthKeys.accessTokenKey flow.Name correlation)

            Expect.equal
                cachedAccess
                credentials.AccessToken
                "the access token minted by the exchange is cached, so the first consumer call pays no refresh round-trip"

            let! profile = profiles.Get scope

            match
                profile
                |> Option.bind (fun p -> p.Entries |> List.tryFind (fun e -> e.Label = "anthropic"))
            with
            | None -> failtest "no entry was bound"
            | Some entry ->
                Expect.equal entry.Origin CredentialOrigin.OAuthConnected "origin is OAuthConnected"
                Expect.equal entry.ProviderId flow.ProviderId "the entry names the flow's provider descriptor"
                Expect.equal entry.SecretKeyName refreshKey "SecretKeyName points at the persisted refresh token"

                match ProviderEntry.oauthBinding entry with
                | None -> failtest "the entry carries no binding — the refresh job would have nothing to act on"
                | Some b ->
                    Expect.equal b.FlowName flow.Name "the binding names the minting flow"
                    Expect.equal b.Correlation correlation "the binding carries the correlation key"
        }

        testCaseAsync "completeConnect refuses to convert a pasted-key entry of the same label"
        <| async {
            let flow, secrets, profiles = fixture ()

            let pasted: ProviderEntry =
                ProviderEntry.pastedKey "anthropic" flow.ProviderId None "ai-key-anthropic"

            let! _ =
                profiles.Set(
                    scope,
                    {
                        ProviderProfile.empty () with
                            Entries = [ pasted ]
                    }
                )

            match! connect flow secrets profiles "anthropic" with
            | Ok() ->
                failtest
                    "connecting over a pasted-key entry must be refused — silently replacing it orphans the user's stored API key while reporting success"
            | Error e -> Expect.stringContains e "anthropic" "the refusal names the label that is in the way"

            let! profile = profiles.Get scope

            match profile |> Option.bind (fun p -> p.Entries |> List.tryHead) with
            | None -> failtest "the pre-existing entry was removed by a refused connect"
            | Some e -> Expect.equal e.Origin CredentialOrigin.PastedKey "the pasted-key entry is untouched"
        }

        testCaseAsync "reconnecting preserves the user's tags and the entry's recorded health"
        <| async {
            let flow, secrets, profiles = fixture ()

            match! connect flow secrets profiles "anthropic" with
            | Error e -> failtestf "first connect failed: %s" e
            | Ok() -> ()

            let health = {
                LastVerifiedAt = Some(DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                RecentErrorCount = 2
                RateLimitHeadroom = Some 0.4
                Status = ProviderHealthStatus.Degraded
            }

            let! current = profiles.Get scope

            let tagged =
                current.Value.Entries
                |> List.map (fun e ->
                    if e.Label = "anthropic" then
                        {
                            e with
                                Tags = [ "premium" ]
                                Health = health
                        }
                    else
                        e)

            let! _ = profiles.Set(scope, { current.Value with Entries = tagged })

            match! connect flow secrets profiles "anthropic" with
            | Error e -> failtestf "reconnect failed: %s" e
            | Ok() -> ()

            let! after = profiles.Get scope
            let entry = after.Value.Entries |> List.find (fun e -> e.Label = "anthropic")
            Expect.equal entry.Tags [ "premium" ] "a reconnect does not discard the user's tags"
            Expect.equal entry.Health health "a reconnect does not discard the probe's recorded verdict"
        }

        testCaseAsync "refreshEntry is a no-op on an entry whose access token is not near expiry"
        <| async {
            let flow, secrets, profiles = fixture ()

            match! connect flow secrets profiles "anthropic" with
            | Error e -> failtestf "connect failed: %s" e
            | Ok() -> ()

            let correlation = OAuthCorrelationKey.providerEntry "anthropic"

            // Push the cached expiry well beyond the lead time.
            let! _ =
                (secrets :> ISecretStore)
                    .SetSecret(
                        scope.Container,
                        ProviderOAuthKeys.accessExpiryKey flow.Name correlation,
                        DateTime.UtcNow.AddHours(4.0).ToString("o")
                    )

            let! profile = profiles.Get scope
            let entry = profile.Value.Entries |> List.find (fun e -> e.Label = "anthropic")

            let! outcome =
                ProviderOAuthConnect.refreshEntry
                    profiles
                    (secrets :> ISecretStore)
                    None
                    scope
                    flow
                    entry
                    DateTime.UtcNow
                    ProviderOAuthConnect.DefaultLeadTime

            Expect.equal
                outcome
                ProviderOAuthConnect.NotDue
                "the steady-state dispatch must short-circuit — this is what keeps a five-minutely job free"
        }

        testCaseAsync "refreshEntry ignores a pasted-key entry"
        <| async {
            let flow, secrets, profiles = fixture ()

            let pasted: ProviderEntry =
                ProviderEntry.pastedKey "pasted" flow.ProviderId None "ai-key-pasted"

            let! outcome =
                ProviderOAuthConnect.refreshEntry
                    profiles
                    (secrets :> ISecretStore)
                    None
                    scope
                    flow
                    pasted
                    DateTime.UtcNow
                    ProviderOAuthConnect.DefaultLeadTime

            Expect.equal
                outcome
                ProviderOAuthConnect.NotOAuthConnected
                "a pasted-key entry has no grant to refresh and must not be touched"
        }

        testCaseAsync "refreshEntry flips health to NeedsReauthorization when the stored grant is gone"
        <| async {
            let flow, secrets, profiles = fixture ()

            match! connect flow secrets profiles "anthropic" with
            | Error e -> failtestf "connect failed: %s" e
            | Ok() -> ()

            let correlation = OAuthCorrelationKey.providerEntry "anthropic"

            // Delete the refresh token — the observable end-state of a
            // revocation, reachable without steering the upstream.
            let! _ =
                (secrets :> ISecretStore)
                    .DeleteSecret(scope.Container, ProviderOAuthKeys.refreshTokenKey flow.Name correlation)

            let! profile = profiles.Get scope
            let entry = profile.Value.Entries |> List.find (fun e -> e.Label = "anthropic")

            let! outcome =
                ProviderOAuthConnect.refreshEntry
                    profiles
                    (secrets :> ISecretStore)
                    None
                    scope
                    flow
                    entry
                    DateTime.UtcNow
                    ProviderOAuthConnect.DefaultLeadTime

            match outcome with
            | ProviderOAuthConnect.NeedsReauthorization _ -> ()
            | other -> failtestf "expected NeedsReauthorization; got %A" other

            let! after = profiles.Get scope
            let refreshed = after.Value.Entries |> List.find (fun e -> e.Label = "anthropic")

            Expect.equal
                refreshed.Health.Status
                ProviderHealthStatus.NeedsReauthorization
                "the user's dashboard must say reconnect BEFORE the next request fails"

            Expect.isSome
                (ProviderEntry.oauthBinding refreshed)
                "the binding survives, so the reconnect path knows which flow to send the user back to"
        }
    ]
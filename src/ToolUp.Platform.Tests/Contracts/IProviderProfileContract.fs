module ToolUp.Platform.Tests.Contracts.IProviderProfileContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Providers

/// Contract test list for any `IProviderProfile` implementation
/// (Phase 42.B). Factory produces a fresh, empty store per test.
/// Tests use GUID-suffixed scope containers so implementations that
/// share underlying state across factory invocations stay isolated.
///
/// Phase 43.A: this is now the conformance bar for the AI assistant's
/// live provider resolution too — `DefaultAIProviderFactory` resolves
/// against `IProviderProfile.ResolveEntry(scope, "ai.assistant",
/// None)` and reads the `"ai.platform"` surface model override
/// directly (the `IUserAIConfigStore` shim is removed). The
/// surface-default-route, stale-label→None, and surface-override
/// round-trip cases below back that path directly.
///
/// Any external implementation can validate against the same
/// conformance bar — divergence is a portability bug, not a feature
/// gap (same posture as the other SDK contract packs).
let tests (name: string) (factory: unit -> IProviderProfile) =
    let uniqueScope () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)

        {
            ScopeId = suffix
            Container = "team-" + suffix
            Persist = true
        }

    // A fixed timestamp so structural equality on round-trip is
    // deterministic (DateTime.UtcNow would differ write-vs-read).
    let ts = DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc)

    let entry label providerId : ProviderEntry = {
        Label = label
        ProviderId = providerId
        Model = Some "model-x"
        SecretKeyName = label + "-key"
        Tags = [ "fast"; "cheap" ]
        Origin = CredentialOrigin.PastedKey
        Health = ProviderHealth.unknown
        OAuthBinding = None
        UpdatedAt = ts
    }

    /// Phase 43.B — an OAuth-connected entry. `SecretKeyName` names the
    /// refresh-token key the substrate derived, and the binding carries
    /// the flow name + neutral correlation key.
    let oauthEntry label providerId flowName : ProviderEntry =
        let correlation = OAuthCorrelationKey.providerEntry label

        {
            Label = label
            ProviderId = providerId
            Model = None
            SecretKeyName = $"{flowName}-refresh-provider-entry-{label}"
            Tags = []
            Origin = CredentialOrigin.OAuthConnected
            Health = ProviderHealth.unknown
            OAuthBinding =
                Some {
                    FlowName = flowName
                    Correlation = correlation
                    ConnectedAt = ts
                }
            UpdatedAt = ts
        }

    /// A non-trivial profile exercising every field: two entries, a
    /// surface-default route + a context-specific override, a
    /// fallback chain, and a surface model override.
    let sampleProfile: ProviderProfile = {
        Entries = [ entry "primary" "anthropic"; entry "backup" "openai" ]
        Routing = [
            {
                Surface = "ai.assistant"
                Context = None
                EntryLabel = "primary"
            }
            {
                Surface = "rental.gateway"
                Context = Some "bank-42"
                EntryLabel = "backup"
            }
        ]
        Fallback = { Ordered = [ "primary"; "backup" ] }
        SurfaceModelOverrides = [ "ai.platform", "claude-sonnet-4-5" ]
        SurfaceProviderOverrides = [ "ai.platform.provider", "anthropic-claude" ]
        UpdatedAt = ts
    }

    testList $"{name} — IProviderProfile contract" [
        testCaseAsync "Get on a never-saved scope returns None"
        <| async {
            let store = factory ()
            let! p = store.Get(uniqueScope ())
            Expect.isNone p "no profile saved yet"
        }

        testCaseAsync "Set then Get round-trips the profile losslessly"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            match! store.Set(scope, sampleProfile) with
            | Error e -> failtestf "Set failed: %s" e
            | Ok() -> ()

            let! got = store.Get scope

            Expect.equal
                got
                (Some sampleProfile)
                "every field round-trips (entries, tags, origin, health, routing, fallback, overrides)"
        }

        testCaseAsync "Set overwrites a previous profile"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            let! _ = store.Set(scope, sampleProfile)

            let v2 = {
                sampleProfile with
                    Entries = [ entry "only" "anthropic" ]
            }

            let! _ = store.Set(scope, v2)
            let! got = store.Get scope
            Expect.equal got (Some v2) "latest write wins"
        }

        testCaseAsync "Clear removes a saved profile"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            let! _ = store.Set(scope, sampleProfile)
            do! store.Clear scope
            let! got = store.Get scope
            Expect.isNone got "profile gone after Clear"
        }

        testCaseAsync "Clear is idempotent on a scope with no profile"
        <| async {
            let store = factory ()
            // Must not throw; contract says Clear is idempotent.
            do! store.Clear(uniqueScope ())
        }

        testCaseAsync "ResolveEntry returns None when the scope has no profile"
        <| async {
            let store = factory ()
            let! e = store.ResolveEntry(uniqueScope (), "ai.assistant", None)
            Expect.isNone e "no profile → no entry"
        }

        testCaseAsync "ResolveEntry returns the surface-default rule's entry"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()
            let! _ = store.Set(scope, sampleProfile)

            let! e = store.ResolveEntry(scope, "ai.assistant", None)
            Expect.equal (e |> Option.map _.Label) (Some "primary") "surface default route resolves to 'primary'"
        }

        testCaseAsync "ResolveEntry: a context-specific rule wins over the surface default"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            let profile = {
                sampleProfile with
                    Routing = [
                        {
                            Surface = "ai.assistant"
                            Context = None
                            EntryLabel = "primary"
                        }
                        {
                            Surface = "ai.assistant"
                            Context = Some "ctx-1"
                            EntryLabel = "backup"
                        }
                    ]
            }

            let! _ = store.Set(scope, profile)

            let! specific = store.ResolveEntry(scope, "ai.assistant", Some "ctx-1")
            Expect.equal (specific |> Option.map _.Label) (Some "backup") "context override wins"

            let! deflt = store.ResolveEntry(scope, "ai.assistant", Some "ctx-unknown")

            Expect.equal
                (deflt |> Option.map _.Label)
                (Some "primary")
                "unknown context falls back to the surface default"
        }

        testCaseAsync "ResolveEntry: a stale EntryLabel resolves to None"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            let profile = {
                sampleProfile with
                    Routing = [
                        {
                            Surface = "ai.assistant"
                            Context = None
                            EntryLabel = "deleted-label"
                        }
                    ]
            }

            let! _ = store.Set(scope, profile)
            let! e = store.ResolveEntry(scope, "ai.assistant", None)

            Expect.isNone
                e
                "rule points at a non-existent entry → None (the None-on-stale semantics DefaultAIProviderFactory.Resolve relies on to fall back per policy)"
        }

        testCaseAsync "SetEntryHealth updates only the targeted entry, leaving others and routing intact"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()
            let! _ = store.Set(scope, sampleProfile)

            let newHealth = {
                LastVerifiedAt = Some ts
                RecentErrorCount = 3
                RateLimitHeadroom = Some 0.25
                Status = ProviderHealthStatus.Degraded
            }

            match! store.SetEntryHealth(scope, "backup", newHealth) with
            | Error e -> failtestf "SetEntryHealth failed: %s" e
            | Ok() -> ()

            let! got = store.Get scope

            match got with
            | None -> failtest "profile vanished after SetEntryHealth"
            | Some p ->
                let backup = p.Entries |> List.find (fun e -> e.Label = "backup")
                let primary = p.Entries |> List.find (fun e -> e.Label = "primary")
                Expect.equal backup.Health newHealth "targeted entry's health updated"
                Expect.equal primary.Health ProviderHealth.unknown "other entry's health untouched"
                Expect.equal p.Routing sampleProfile.Routing "routing untouched"
        }

        testCaseAsync "SetEntryHealth on an absent label is a no-op success"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()
            let! _ = store.Set(scope, sampleProfile)

            match! store.SetEntryHealth(scope, "no-such-label", ProviderHealth.unknown) with
            | Error e -> failtestf "expected Ok no-op; got Error: %s" e
            | Ok() -> ()

            let! got = store.Get scope
            Expect.equal got (Some sampleProfile) "profile unchanged on absent-label health write"
        }

        testCaseAsync "SetEntryHealth when the scope has no profile is a no-op success"
        <| async {
            let store = factory ()

            match! store.SetEntryHealth(uniqueScope (), "any", ProviderHealth.unknown) with
            | Error e -> failtestf "expected Ok no-op; got Error: %s" e
            | Ok() -> ()
        }

        // ─── Phase 43.B — the OAuth-connected entry lifecycle ────
        //
        // A store that silently drops `OAuthBinding` would look correct
        // on every case above and then present, days later, as tokens
        // that stop refreshing — the binding is what the refresh job
        // reads to know WHICH flow and WHICH correlation to refresh.
        // These cases are the conformance bar for that.

        testCaseAsync "An OAuthConnected entry round-trips its binding (flow name + correlation key)"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()
            let connected = oauthEntry "anthropic" "anthropic-claude" "claude-oauth"

            let profile = {
                ProviderProfile.empty () with
                    Entries = [ connected ]
                    UpdatedAt = ts
            }

            match! store.Set(scope, profile) with
            | Error e -> failtestf "Set failed: %s" e
            | Ok() -> ()

            let! got = store.Get scope

            match got |> Option.bind (fun p -> p.Entries |> List.tryHead) with
            | None -> failtest "the OAuth-connected entry did not round-trip"
            | Some e ->
                Expect.equal e.Origin CredentialOrigin.OAuthConnected "origin survives"

                match ProviderEntry.oauthBinding e with
                | None -> failtest "OAuthBinding was dropped — the refresh job cannot recover the flow or correlation"
                | Some b ->
                    Expect.equal b.FlowName "claude-oauth" "flow name survives"
                    Expect.equal b.Correlation.Kind OAuthCorrelationKey.ProviderEntryKind "correlation kind survives"
                    Expect.equal b.Correlation.Id "anthropic" "correlation id survives"
        }

        testCaseAsync "A PastedKey entry round-trips with no binding, alongside an OAuthConnected one"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            let profile = {
                ProviderProfile.empty () with
                    Entries = [
                        entry "pasted" "openai-gpt"
                        oauthEntry "connected" "anthropic-claude" "claude-oauth"
                    ]
                    UpdatedAt = ts
            }

            let! _ = store.Set(scope, profile)
            let! got = store.Get scope

            match got with
            | None -> failtest "profile did not round-trip"
            | Some p ->
                let pasted = p.Entries |> List.find (fun e -> e.Label = "pasted")
                let connected = p.Entries |> List.find (fun e -> e.Label = "connected")
                Expect.isNone pasted.OAuthBinding "a pasted-key entry carries no binding"

                Expect.isSome
                    (ProviderEntry.oauthBinding connected)
                    "the two origins coexist in one profile without either losing its shape"
        }

        testCaseAsync
            "SetEntryHealth flips an OAuthConnected entry to NeedsReauthorization without disturbing its binding"
        <| async {
            // This is exactly what the refresh job does when the
            // upstream rejects the stored grant. Losing the binding
            // here would make the entry unrecoverable: the reconnect
            // path reads it to know which flow to send the user back to.
            let store = factory ()
            let scope = uniqueScope ()
            let connected = oauthEntry "anthropic" "anthropic-claude" "claude-oauth"

            let! _ =
                store.Set(
                    scope,
                    {
                        ProviderProfile.empty () with
                            Entries = [ connected ]
                            UpdatedAt = ts
                    }
                )

            match!
                store.SetEntryHealth(
                    scope,
                    "anthropic",
                    {
                        LastVerifiedAt = None
                        RecentErrorCount = 1
                        RateLimitHeadroom = None
                        Status = ProviderHealthStatus.NeedsReauthorization
                    }
                )
            with
            | Error e -> failtestf "SetEntryHealth failed: %s" e
            | Ok() -> ()

            let! got = store.Get scope

            match got |> Option.bind (fun p -> p.Entries |> List.tryHead) with
            | None -> failtest "entry vanished after the health write"
            | Some e ->
                Expect.equal e.Health.Status ProviderHealthStatus.NeedsReauthorization "health flipped"
                Expect.isSome (ProviderEntry.oauthBinding e) "the binding survives the health write"
                Expect.equal e.SecretKeyName connected.SecretKeyName "the refresh-token key reference survives"
        }

        testCaseAsync "Scope isolation — a profile saved in scope A is invisible in scope B"
        <| async {
            let store = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()

            let! _ = store.Set(scopeA, sampleProfile)

            let! fromB = store.Get scopeB
            Expect.isNone fromB "scope B does not see scope A's profile"
        }
    ]
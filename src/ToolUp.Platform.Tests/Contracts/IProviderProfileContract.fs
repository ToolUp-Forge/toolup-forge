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
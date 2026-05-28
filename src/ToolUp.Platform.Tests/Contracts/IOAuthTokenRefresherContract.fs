module ToolUp.Platform.Tests.Contracts.IOAuthTokenRefresherContract

open System
open Expecto
open ToolUp.Platform

// ─── IOAuthTokenRefresher contract pack ───────────────────────────
//
// Parametrised tests for any `IOAuthTokenRefresher` implementation.
// Each test asks the factory for a fresh `(refresher, scope)` pair;
// scope is GUID-suffixed so concurrent runs against a shared substrate
// don't interfere. The factory is responsible for seeding any
// secret-store entries the implementation needs (`ClientSecretKey`,
// `RefreshTokenKey`) before returning so `RefreshNow` calls
// against a registered descriptor can locate them.
//
// Coverage targets the portable interface — register / unregister
// idempotency, identity-by-value lookup semantics, scope-pinning,
// and `RefreshNow` short-circuit on unknown descriptors. HTTP-path
// behaviour (happy refresh / invalid_grant / 5xx-transient) is
// exercised by the in-process binding against a fake
// `HttpMessageHandler`, not at the portable-interface layer.

let tests (name: string) (factory: unit -> IOAuthTokenRefresher * string) =

    let mkDescriptor scopeId provider configId : OAuthRefreshDescriptor =
        OAuthRefreshDescriptor.withDefaults
            provider
            configId
            scopeId
            $"https://example.test/{provider}/token"
            "client-id-{provider}"
            $"{provider}-client-secret-{configId}"
            $"{provider}-refresh-{configId}"

    testList $"{name} — IOAuthTokenRefresher contract" [

        // ─── Lifecycle: Register / Get / Unregister ─────────────

        testCaseAsync "Register + Get round-trips by (Provider, ConfigId)"
        <| async {
            let refresher, scope = factory ()
            let descriptor = mkDescriptor scope "google-analytics" "ga4-acct-1"

            do! refresher.RegisterDescriptor descriptor

            let! found = refresher.GetDescriptor("google-analytics", "ga4-acct-1")

            match found with
            | Some d ->
                Expect.equal d.Provider "google-analytics" "provider round-trips"
                Expect.equal d.ConfigId "ga4-acct-1" "configId round-trips"
                Expect.equal d.ScopeId scope "scope round-trips"
            | None -> failtest "Register then Get should return Some descriptor"
        }

        testCaseAsync "Get on unknown (provider, configId) returns None"
        <| async {
            let refresher, _ = factory ()

            let! found = refresher.GetDescriptor("nonexistent", "missing")
            Expect.isNone found "unknown key returns None"
        }

        testCaseAsync "Register is idempotent — re-registering same key updates the binding"
        <| async {
            let refresher, scope = factory ()
            let original = mkDescriptor scope "salesforce" "sfdc-prod"

            let updated = {
                original with
                    TokenEndpoint = "https://example.test/salesforce/token-v2"
            }

            do! refresher.RegisterDescriptor original
            do! refresher.RegisterDescriptor updated

            let! found = refresher.GetDescriptor("salesforce", "sfdc-prod")

            match found with
            | Some d -> Expect.equal d.TokenEndpoint updated.TokenEndpoint "second register wins"
            | None -> failtest "expected updated descriptor"
        }

        testCaseAsync "Unregister removes the descriptor"
        <| async {
            let refresher, scope = factory ()
            let descriptor = mkDescriptor scope "hubspot" "hs-acct"
            do! refresher.RegisterDescriptor descriptor

            do! refresher.UnregisterDescriptor("hubspot", "hs-acct")

            let! found = refresher.GetDescriptor("hubspot", "hs-acct")
            Expect.isNone found "after Unregister, Get returns None"
        }

        testCaseAsync "Unregister is idempotent — unknown (provider, configId) is a no-op"
        <| async {
            let refresher, _ = factory ()

            // Must not throw.
            do! refresher.UnregisterDescriptor("never-registered", "no-such-thing")
        }

        // ─── Identity-by-value semantics ─────────────────────────

        testCaseAsync "Different ConfigIds under the same Provider are independent"
        <| async {
            let refresher, scope = factory ()
            let d1 = mkDescriptor scope "stripe" "stripe-prod"
            let d2 = mkDescriptor scope "stripe" "stripe-test"

            do! refresher.RegisterDescriptor d1
            do! refresher.RegisterDescriptor d2

            let! f1 = refresher.GetDescriptor("stripe", "stripe-prod")
            let! f2 = refresher.GetDescriptor("stripe", "stripe-test")

            Expect.isSome f1 "first descriptor present"
            Expect.isSome f2 "second descriptor present"
            Expect.notEqual f1 f2 "descriptors are distinct"
        }

        testCaseAsync "Different Providers with same ConfigId are independent"
        <| async {
            let refresher, scope = factory ()
            let d1 = mkDescriptor scope "ga4" "shared-id"
            let d2 = mkDescriptor scope "hubspot" "shared-id"

            do! refresher.RegisterDescriptor d1
            do! refresher.RegisterDescriptor d2

            let! f1 = refresher.GetDescriptor("ga4", "shared-id")
            let! f2 = refresher.GetDescriptor("hubspot", "shared-id")

            Expect.isSome f1 "ga4 descriptor present"
            Expect.isSome f2 "hubspot descriptor present"

            match f1, f2 with
            | Some a, Some b -> Expect.notEqual a.Provider b.Provider "providers distinct"
            | _ -> failtest "both must be present"
        }

        // ─── ListDescriptors ─────────────────────────────────────

        testCaseAsync "ListDescriptors returns every registered descriptor"
        <| async {
            let refresher, scope = factory ()
            let d1 = mkDescriptor scope "ga4" "acct-1"
            let d2 = mkDescriptor scope "hubspot" "acct-2"

            do! refresher.RegisterDescriptor d1
            do! refresher.RegisterDescriptor d2

            let! list = refresher.ListDescriptors()

            let keys = list |> List.map OAuthRefreshDescriptor.key |> Set.ofList
            Expect.isTrue (keys.Contains "ga4:acct-1") "ga4:acct-1 listed"
            Expect.isTrue (keys.Contains "hubspot:acct-2") "hubspot:acct-2 listed"
        }

        testCaseAsync "ListDescriptors reflects subsequent Unregister"
        <| async {
            let refresher, scope = factory ()
            let d1 = mkDescriptor scope "ga4" "acct-1"
            do! refresher.RegisterDescriptor d1

            do! refresher.UnregisterDescriptor("ga4", "acct-1")

            let! list = refresher.ListDescriptors()
            let keys = list |> List.map OAuthRefreshDescriptor.key |> Set.ofList
            Expect.isFalse (keys.Contains "ga4:acct-1") "unregistered descriptor not listed"
        }

        // ─── RefreshNow short-circuit ────────────────────────────

        testCaseAsync "RefreshNow on unknown descriptor returns PermanentError"
        <| async {
            let refresher, _ = factory ()

            let! result = refresher.RefreshNow("never-registered", "missing")

            match result with
            | PermanentError reason -> Expect.stringContains reason "unknown descriptor" "names the missing descriptor"
            | other -> failtestf "Expected PermanentError, got %A" other
        }
    ]
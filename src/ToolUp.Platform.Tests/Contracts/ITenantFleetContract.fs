module ToolUp.Platform.Tests.Contracts.ITenantFleetContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.TenantEntity

// ─── Phase 26 ITenantFleet contract tests ───────────────────────────
//
// Parametrised contract pack for any `ITenantFleet` implementation.
// Each test asks the factory for a fresh `ITenantFleet` so tests do
// not share underlying entity-store state. Bindings (e.g. the in-
// tree `EntityStoreTenantFleet` over `BlobEntityStore`, a future
// Akka.Cluster-Sharded fleet) compose the same pack against their
// own factory.
//
// Coverage targets the documented interface contract — slug-format
// validation, slug-uniqueness within region, idempotent
// state transitions, terminal `Evicted` semantics, the bounded
// `SuggestSlugs` shape, and the six-rule portability audit's
// observable claims (identity-by-value parameters, async surface).

/// Sample provisioning request used by most tests. Tests override
/// only the fields they care about.
let private mkRequest slug ownerUserId region : ProvisioningRequest = {
    Slug = slug
    OwnerUserId = ownerUserId
    Region = region
    Tier = "free"
    DisplayName = ""
}

let private okOrFail label =
    function
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %A" label e

let tests (name: string) (factory: unit -> ITenantFleet) =

    testList $"{name} — ITenantFleet contract" [

        // ─── ProvisionTenant — slug-format validation ─────────────

        testCaseAsync "ProvisionTenant rejects an empty slug with TenantSlugInvalid"
        <| async {
            let fleet = factory ()
            let req = mkRequest "" "owner-1" "eu-west"
            let! result = fleet.ProvisionTenant req

            match result with
            | Error(TenantSlugInvalid(slug, _)) -> Expect.equal slug "" "preserves the invalid slug"
            | other -> failtestf "expected TenantSlugInvalid, got %A" other
        }

        testCaseAsync "ProvisionTenant rejects a slug with uppercase letters"
        <| async {
            let fleet = factory ()
            let req = mkRequest "TenantX" "owner-1" "eu-west"
            let! result = fleet.ProvisionTenant req

            match result with
            | Error(TenantSlugInvalid(slug, _)) -> Expect.equal slug "TenantX" "preserves the invalid slug"
            | other -> failtestf "expected TenantSlugInvalid, got %A" other
        }

        testCaseAsync "ProvisionTenant rejects a slug with leading hyphen"
        <| async {
            let fleet = factory ()
            let req = mkRequest "-tenant" "owner-1" "eu-west"
            let! result = fleet.ProvisionTenant req

            match result with
            | Error(TenantSlugInvalid _) -> ()
            | other -> failtestf "expected TenantSlugInvalid, got %A" other
        }

        // ─── ProvisionTenant — happy path ─────────────────────────

        testCaseAsync "ProvisionTenant returns a tenant with Status=Active"
        <| async {
            let fleet = factory ()
            let req = mkRequest "alpha" "owner-1" "eu-west"

            let tenant =
                okOrFail "ProvisionTenant" (Async.RunSynchronously(fleet.ProvisionTenant req))

            Expect.equal tenant.Slug "alpha" "Slug round-trips"
            Expect.equal tenant.OwnerUserId "owner-1" "OwnerUserId round-trips"
            Expect.equal tenant.Region "eu-west" "Region round-trips"
            Expect.equal tenant.Status Active "freshly provisioned tenant is Active"
            Expect.isFalse (String.IsNullOrEmpty tenant.Id) "TenantId assigned"
            do! async.Return()
        }

        testCaseAsync "ProvisionTenant defaults DisplayName to Slug when blank"
        <| async {
            let fleet = factory ()
            let req = mkRequest "beta" "owner-1" "eu-west"

            let tenant =
                okOrFail "ProvisionTenant" (Async.RunSynchronously(fleet.ProvisionTenant req))

            Expect.equal tenant.DisplayName "beta" "blank DisplayName defaults to Slug"
            do! async.Return()
        }

        testCaseAsync "ProvisionTenant preserves an explicit DisplayName"
        <| async {
            let fleet = factory ()

            let req = {
                mkRequest "gamma" "owner-1" "eu-west" with
                    DisplayName = "Gamma App"
            }

            let tenant =
                okOrFail "ProvisionTenant" (Async.RunSynchronously(fleet.ProvisionTenant req))

            Expect.equal tenant.DisplayName "Gamma App" "DisplayName preserved"
            do! async.Return()
        }

        // ─── Slug uniqueness within region ────────────────────────

        testCaseAsync "Provisioning the same (slug, region) twice returns SlugAlreadyTaken"
        <| async {
            let fleet = factory ()
            let req = mkRequest "shared-slug" "owner-1" "eu-west"
            let! first = fleet.ProvisionTenant req
            Expect.isOk first "first provision succeeds"

            let! second = fleet.ProvisionTenant req

            match second with
            | Error(SlugAlreadyTaken(slug, region)) ->
                Expect.equal slug "shared-slug" "slug preserved"
                Expect.equal region "eu-west" "region preserved"
            | other -> failtestf "expected SlugAlreadyTaken, got %A" other
        }

        testCaseAsync "Same slug in a different region is allowed"
        <| async {
            let fleet = factory ()
            let euReq = mkRequest "multi-region" "owner-1" "eu-west"
            let usReq = mkRequest "multi-region" "owner-1" "us-east"
            let! first = fleet.ProvisionTenant euReq
            Expect.isOk first "eu-west succeeds"
            let! second = fleet.ProvisionTenant usReq
            Expect.isOk second "us-east succeeds despite same slug"
        }

        // ─── GetTenant ────────────────────────────────────────────

        testCaseAsync "GetTenant returns the provisioned tenant"
        <| async {
            let fleet = factory ()
            let req = mkRequest "delta" "owner-1" "eu-west"

            let provisioned =
                okOrFail "ProvisionTenant" (Async.RunSynchronously(fleet.ProvisionTenant req))

            let! getResult = fleet.GetTenant provisioned.Id

            match getResult with
            | Ok t ->
                Expect.equal t.Id provisioned.Id "Id round-trips"
                Expect.equal t.Slug "delta" "Slug round-trips"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        testCaseAsync "GetTenant on unknown id returns UnknownTenant"
        <| async {
            let fleet = factory ()
            let! result = fleet.GetTenant "no-such-tenant"

            match result with
            | Error(UnknownTenant tid) -> Expect.equal tid "no-such-tenant" "id preserved"
            | other -> failtestf "expected UnknownTenant, got %A" other
        }

        // ─── ListTenants ──────────────────────────────────────────

        testCaseAsync "ListTenants None returns every provisioned tenant"
        <| async {
            let fleet = factory ()
            let! _ = fleet.ProvisionTenant(mkRequest "list-a" "owner-1" "eu-west")
            let! _ = fleet.ProvisionTenant(mkRequest "list-b" "owner-2" "eu-west")
            let! _ = fleet.ProvisionTenant(mkRequest "list-c" "owner-1" "us-east")

            let! all = fleet.ListTenants None
            Expect.hasLength all 3 "lists all three"
        }

        testCaseAsync "ListTenants filters by OwnerUserId"
        <| async {
            let fleet = factory ()
            let! _ = fleet.ProvisionTenant(mkRequest "owned-1a" "owner-1" "eu-west")
            let! _ = fleet.ProvisionTenant(mkRequest "owned-1b" "owner-1" "us-east")
            let! _ = fleet.ProvisionTenant(mkRequest "owned-2" "owner-2" "eu-west")

            let! owner1Tenants = fleet.ListTenants(Some "owner-1")
            Expect.hasLength owner1Tenants 2 "owner-1 has two tenants"
            Expect.all owner1Tenants (fun t -> t.OwnerUserId = "owner-1") "all entries belong to owner-1"
        }

        // ─── EvictTenant ──────────────────────────────────────────

        testCaseAsync "EvictTenant transitions the tenant to Evicted"
        <| async {
            let fleet = factory ()
            let req = mkRequest "evict-target" "owner-1" "eu-west"

            let tenant =
                okOrFail "ProvisionTenant" (Async.RunSynchronously(fleet.ProvisionTenant req))

            let! evicted = fleet.EvictTenant(tenant.Id, "operator-1", "test reason")
            Expect.isOk evicted "EvictTenant returns Ok"

            let! reread = fleet.GetTenant tenant.Id

            match reread with
            | Ok t -> Expect.equal t.Status Evicted "tenant is now Evicted"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        testCaseAsync "EvictTenant is idempotent on an already-evicted tenant"
        <| async {
            let fleet = factory ()
            let req = mkRequest "evict-idempotent" "owner-1" "eu-west"

            let tenant =
                okOrFail "ProvisionTenant" (Async.RunSynchronously(fleet.ProvisionTenant req))

            let! first = fleet.EvictTenant(tenant.Id, "operator-1", "first")
            Expect.isOk first "first eviction succeeds"

            let! second = fleet.EvictTenant(tenant.Id, "operator-1", "second")
            Expect.isOk second "second eviction returns Ok (idempotent)"
        }

        testCaseAsync "EvictTenant on unknown id returns UnknownTenant"
        <| async {
            let fleet = factory ()
            let! result = fleet.EvictTenant("ghost", "operator-1", "test")

            match result with
            | Error(UnknownTenant tid) -> Expect.equal tid "ghost" "id preserved"
            | other -> failtestf "expected UnknownTenant, got %A" other
        }

        // ─── RestartTenant ────────────────────────────────────────

        testCaseAsync "RestartTenant rejects an Evicted tenant with TenantEvicted"
        <| async {
            let fleet = factory ()
            let req = mkRequest "restart-evicted" "owner-1" "eu-west"

            let tenant =
                okOrFail "ProvisionTenant" (Async.RunSynchronously(fleet.ProvisionTenant req))

            let! _ = fleet.EvictTenant(tenant.Id, "operator-1", "test")
            let! result = fleet.RestartTenant(tenant.Id, "operator-1")

            match result with
            | Error(TenantEvicted tid) -> Expect.equal tid tenant.Id "id preserved"
            | other -> failtestf "expected TenantEvicted, got %A" other
        }

        testCaseAsync "RestartTenant on an Active tenant is idempotent"
        <| async {
            let fleet = factory ()
            let req = mkRequest "restart-active" "owner-1" "eu-west"

            let tenant =
                okOrFail "ProvisionTenant" (Async.RunSynchronously(fleet.ProvisionTenant req))

            let! result = fleet.RestartTenant(tenant.Id, "operator-1")
            Expect.isOk result "RestartTenant on Active is Ok"
        }

        // ─── IsSlugAvailable ──────────────────────────────────────

        testCaseAsync "IsSlugAvailable returns true on a fresh slug"
        <| async {
            let fleet = factory ()
            let! available = fleet.IsSlugAvailable("brand-new-slug", "eu-west")
            Expect.isTrue available "fresh slug is available"
        }

        testCaseAsync "IsSlugAvailable returns false after the slug is provisioned"
        <| async {
            let fleet = factory ()
            let! _ = fleet.ProvisionTenant(mkRequest "claimed" "owner-1" "eu-west")
            let! available = fleet.IsSlugAvailable("claimed", "eu-west")
            Expect.isFalse available "claimed slug is not available"
        }

        testCaseAsync "IsSlugAvailable returns true for the same slug in a different region"
        <| async {
            let fleet = factory ()
            let! _ = fleet.ProvisionTenant(mkRequest "cross-region" "owner-1" "eu-west")
            let! available = fleet.IsSlugAvailable("cross-region", "us-east")
            Expect.isTrue available "slug is region-scoped"
        }

        testCaseAsync "IsSlugAvailable returns false for an invalid slug format"
        <| async {
            let fleet = factory ()
            let! available = fleet.IsSlugAvailable("", "eu-west")
            Expect.isFalse available "invalid slug never reports as available"
        }

        // ─── SuggestSlugs ─────────────────────────────────────────

        testCaseAsync "SuggestSlugs returns at most 5 suggestions"
        <| async {
            let fleet = factory ()
            let! suggestions = fleet.SuggestSlugs "anything"
            Expect.isLessThanOrEqual suggestions.Length 5 "bounded by contract"
        }

        testCaseAsync "SuggestSlugs avoids slugs already taken"
        <| async {
            let fleet = factory ()
            // Take the canonical numeric-suffix candidates so the
            // suggester has to walk past them.
            let! _ = fleet.ProvisionTenant(mkRequest "seed-1" "owner-1" "")
            let! _ = fleet.ProvisionTenant(mkRequest "seed-2" "owner-1" "")

            let! suggestions = fleet.SuggestSlugs "seed"
            // Single-node default returns up to 5 free numeric variants.
            // It MUST not return seed-1 or seed-2 because those are
            // taken; the substrate may legitimately return fewer than 5.
            Expect.isFalse (suggestions |> List.contains "seed-1") "skips taken seed-1"
            Expect.isFalse (suggestions |> List.contains "seed-2") "skips taken seed-2"
        }

        // ─── Six-rule portability audit (Phase 9c, GP 12) ─────────
        //
        // These assertions back the prose audit block at the top of
        // ITenantFleet.fs with executable claims.

        testCaseAsync "Rule 1 — identity-by-value: TenantId is a string alias"
        <| async {
            // Compile-time shape: TenantId must remain a string alias
            // so framework handles cannot escape the boundary. The
            // assertion is reachable only because the runtime equality
            // works on the underlying string.
            let id: TenantId = "tenant-1"
            Expect.equal id "tenant-1" "TenantId is a string alias"
            do! async.Return()
        }

        testCaseAsync "Rule 2 — async at every boundary: all interface methods return Async<_>"
        <| async {
            let fleet = factory ()
            // Every call path below compiles only because every member
            // returns an `Async<_>`. The assertions never inspect the
            // result; the compile-time shape is the contract.
            let! _ = fleet.ProvisionTenant(mkRequest "rule-2" "owner-1" "eu-west")
            let! _ = fleet.GetTenant "rule-2"
            let! _ = fleet.ListTenants None
            let! _ = fleet.GetTenantHealth "rule-2"
            let! _ = fleet.IsSlugAvailable("rule-2", "eu-west")
            let! _ = fleet.SuggestSlugs "rule-2"
            ()
        }

        testCaseAsync "Rule 3 — errors flow as data through TenantFleetError"
        <| async {
            let fleet = factory ()
            // Every failure path surfaces a TenantFleetError case, not
            // a thrown exception, so handlers can pattern-match.
            let! invalid = fleet.ProvisionTenant(mkRequest "Invalid Slug" "owner-1" "eu-west")
            Expect.isError invalid "invalid slug returns Error data, not raises"
        }
    ]
module ToolUp.Platform.Tests.Contracts.StoreIdSanitisingContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.PermissionStore

// ─── Phase 131 — store-seam id-sanitisation contract pack ────────────
//
// Cross-store conformance bar for the `IdentitySanitiser`-at-the-store-
// seam guarantee. The InProcess `StoreIdSanitisingTests` pin the
// decorators against recording doubles (the inner store is never
// touched); this pack promotes the same invariants to a *real* backing
// store so the rejection is proven to fire BEFORE any blob write/read
// reaches the concrete store + its blob backend.
//
// Bound twice by the InProcess harness — once over an in-memory blob
// backend, once over a local-file blob backend — so the same guarantee
// is asserted against both shipped store-construction shapes.
//
// Every id-keyed write rejects a traversal id with an `Error` and never
// persists; every read with a traversal `scopeId` degrades to empty
// (the share-token read-seam defence-in-depth); benign ids round-trip
// unchanged.

/// The three id-keyed stores, already wrapped in their `Sanitising*`
/// decorators over a real concrete store + blob backend. A fresh set is
/// built per test so cross-test state never bleeds.
type SanitisedStores = {
    Team: ITeamStore
    Permission: IPermissionStore
    ShareToken: IShareTokenStore
}

type StoreFactory = unit -> SanitisedStores

/// Id shapes every id-keyed method must reject. Mirrors the
/// `IdentitySanitiser` allowlist categories (path separators, the
/// reserved platform scope, NUL / control chars, whitespace).
let private traversalIds = [
    "path traversal", "../../_platform/permissions/t"
    "reserved platform scope", "_platform"
    "backslash traversal", "..\\..\\secrets"
    "NUL byte", "team" + string '\000' + "id"
    "whitespace", "team id"
]

let private benignScope () =
    "scope-" + Guid.NewGuid().ToString("N").Substring(0, 12)

let private mkIssueRequest (scopeId: string) : ShareTokenIssueRequest = {
    ScopeId = scopeId
    ResourceKind = "form"
    ResourceId = "survey-1"
    AttributedHandle = None
    IssuedBy = "user-issuer"
    ExpiresAt = None
    UseLimit = None
    RateLimit = None
}

let tests (label: string) (factory: StoreFactory) =
    testList (sprintf "StoreIdSanitising contract — %s" label) [

        // ─── ITeamStore write seam ──────────────────────────────────

        testCaseAsync "CreateTeam rejects every traversal-shaped teamId and persists nothing"
        <| async {
            for (kind, badId) in traversalIds do
                let stores = factory ()

                match! stores.Team.CreateTeam(badId, "Evil") with
                | Error _ ->
                    let! teams = stores.Team.ListTeams()
                    Expect.isEmpty teams (sprintf "%s teamId must not create a team" kind)
                | Ok _ -> failtestf "traversal teamId (%s) must be rejected" kind
        }

        testCaseAsync "AddMember rejects a traversal-shaped userId"
        <| async {
            let stores = factory ()
            let teamId = benignScope ()
            let! _ = stores.Team.CreateTeam(teamId, "Acme")

            match! stores.Team.AddMember(teamId, "../../_platform/x", TeamRole.Member) with
            | Error _ -> ()
            | Ok() -> failtest "traversal userId must be rejected"
        }

        testCaseAsync "CreateTeam + GetTeam round-trips a benign Guid-shaped teamId"
        <| async {
            let stores = factory ()
            let teamId = Guid.NewGuid().ToString("N")

            match! stores.Team.CreateTeam(teamId, "Acme") with
            | Ok _ ->
                let! fetched = stores.Team.GetTeam(teamId)
                Expect.isSome fetched "benign teamId persists + reads back"
            | Error e -> failtestf "benign teamId should pass: %s" e
        }

        // ─── IPermissionStore write seam ────────────────────────────

        testCaseAsync "SetMemberPermissions rejects a traversal-shaped userId"
        <| async {
            let stores = factory ()

            match! stores.Permission.SetMemberPermissions("team-ok", "../../_platform/x", "ModuleA", []) with
            | Error _ -> ()
            | Ok() -> failtest "traversal userId must be rejected"
        }

        testCaseAsync "SetTeamPermissions rejects the reserved _platform teamId"
        <| async {
            let stores = factory ()

            match! stores.Permission.SetTeamPermissions("_platform", TeamPermissions.empty) with
            | Error _ -> ()
            | Ok() -> failtest "reserved _platform teamId must be rejected"
        }

        // ─── IShareTokenStore resource/scope seam ───────────────────

        testCaseAsync "Issue rejects every traversal-shaped scopeId"
        <| async {
            for (kind, badId) in traversalIds do
                let stores = factory ()

                match! stores.ShareToken.Issue(mkIssueRequest badId) with
                | Error _ -> ()
                | Ok _ -> failtestf "traversal scopeId (%s) must be rejected at Issue" kind
        }

        testCaseAsync "MarkUsed + Revoke reject a traversal-shaped scopeId / tokenId"
        <| async {
            let stores = factory ()

            match! stores.ShareToken.MarkUsed("../evil", "tok-1") with
            | Error _ -> ()
            | Ok() -> failtest "traversal scopeId must be rejected at MarkUsed"

            match! stores.ShareToken.Revoke("scope-ok", "../../_platform/x", "actor") with
            | Error _ -> ()
            | Ok() -> failtest "traversal tokenId must be rejected at Revoke"
        }

        testCaseAsync "ListByResource / ListByIssuer with a traversal scopeId degrade to empty (read-seam DiD)"
        <| async {
            let stores = factory ()

            let! byResource = stores.ShareToken.ListByResource("../../_platform", "form", "survey-1")
            Expect.isEmpty byResource "traversal scopeId must not enumerate a chosen prefix"

            let! byIssuer = stores.ShareToken.ListByIssuer("..\\..\\jobs", "user-issuer")
            Expect.isEmpty byIssuer "traversal scopeId must not enumerate a chosen prefix"
        }

        testCaseAsync "Issue + Validate + ListByIssuer round-trip a benign scopeId"
        <| async {
            let stores = factory ()
            let scopeId = benignScope ()

            match! stores.ShareToken.Issue(mkIssueRequest scopeId) with
            | Ok issued ->
                match! stores.ShareToken.Validate issued.Token with
                | Ok claim -> Expect.equal claim.ScopeId scopeId "validated claim carries the benign scope"
                | Error e -> failtestf "benign token should validate: %A" e

                let! claims = stores.ShareToken.ListByIssuer(scopeId, "user-issuer")
                Expect.isNonEmpty claims "benign scope's issued token is enumerable"
            | Error e -> failtestf "benign scopeId should issue: %A" e
        }
    ]
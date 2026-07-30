module ToolUp.Platform.Tests.InProcess.StorageScopeResolverTests

open System
open System.IO
open Microsoft.Extensions.Caching.Memory
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.TeamManagement

// ─── Shared helpers ──────────────────────────────────────────────────

let private emptyRequest: ScopeResolutionRequest = {
    User = None
    SessionId = None
    Headers = Map.empty
}

let private alice: AuthenticatedUser = {
    UserId = "alice"
    DisplayName = "Alice"
    Email = Some "alice@example.com"
    TenantId = None
    Roles = []
}

let private freshStorage () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-scope-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

// ─── AnonymousScopeResolver ──────────────────────────────────────────
//
// Not a shared contract — every resolver has a different correctness
// criterion by platform mode. Tests pin each resolver's individual
// behaviour so accidental divergence between resolver and consuming
// mode (compose's mode → resolver mapping) is caught immediately.

let private anonymousTests =
    testList "AnonymousScopeResolver" [
        testCaseAsync "Resolves to session-scoped, non-persistent"
        <| async {
            let resolver = AnonymousScopeResolver() :> IStorageScopeResolver

            let request = {
                emptyRequest with
                    SessionId = Some "abc123"
            }

            match! resolver.Resolve request with
            | Error e -> failtestf "Expected Ok; got Error: %A" e
            | Ok scope ->
                Expect.equal scope.ScopeId "abc123" "scope uses SessionId"
                Expect.equal scope.Container "session-abc123" "container matches session convention"
                Expect.isFalse scope.Persist "anonymous is non-persistent"
        }

        testCaseAsync "Generates a fresh session id when none supplied"
        <| async {
            let resolver = AnonymousScopeResolver() :> IStorageScopeResolver

            match! resolver.Resolve emptyRequest with
            | Error e -> failtestf "Expected Ok; got Error: %A" e
            | Ok scope ->
                Expect.isNotEmpty scope.ScopeId "generated id is non-empty"
                Expect.stringStarts scope.Container "session-" "still follows session convention"
                Expect.isFalse scope.Persist "still non-persistent"
        }
    ]

// ─── AuthenticatedEphemeralScopeResolver ─────────────────────────────

let private authEphemeralTests =
    testList "AuthenticatedEphemeralScopeResolver" [
        testCaseAsync "Resolves authenticated user to user-scoped, non-persistent"
        <| async {
            let resolver = AuthenticatedEphemeralScopeResolver() :> IStorageScopeResolver
            let request = { emptyRequest with User = Some alice }

            match! resolver.Resolve request with
            | Error e -> failtestf "Expected Ok; got Error: %A" e
            | Ok scope ->
                Expect.equal scope.ScopeId "alice" "scope uses user id"
                Expect.equal scope.Container "user-alice" "container matches user convention"
                Expect.isFalse scope.Persist "ephemeral mode is non-persistent"
        }

        testCaseAsync "Rejects requests without a user"
        <| async {
            let resolver = AuthenticatedEphemeralScopeResolver() :> IStorageScopeResolver

            match! resolver.Resolve emptyRequest with
            | Ok _ -> failtest "Expected Error NotAuthenticated"
            | Error e -> Expect.equal e NotAuthenticated "no user means NotAuthenticated"
        }

        testCaseAsync "Rejects anonymous users"
        <| async {
            let resolver = AuthenticatedEphemeralScopeResolver() :> IStorageScopeResolver

            let request = {
                emptyRequest with
                    User = Some AuthenticatedUser.anonymous
            }

            match! resolver.Resolve request with
            | Ok _ -> failtest "Expected Error NotAuthenticated"
            | Error e -> Expect.equal e NotAuthenticated "anonymous user means NotAuthenticated"
        }
    ]

// ─── AuthenticatedScopeResolver ──────────────────────────────────────

let private authenticatedTests =
    testList "AuthenticatedScopeResolver" [
        testCaseAsync "Resolves authenticated user to user-scoped, persistent"
        <| async {
            let resolver = AuthenticatedScopeResolver() :> IStorageScopeResolver
            let request = { emptyRequest with User = Some alice }

            match! resolver.Resolve request with
            | Error e -> failtestf "Expected Ok; got Error: %A" e
            | Ok scope ->
                Expect.equal scope.ScopeId "alice" "scope uses user id"
                Expect.equal scope.Container "user-alice" "container matches user convention"
                Expect.isTrue scope.Persist "individual mode persists"
        }

        testCaseAsync "Rejects requests without a user"
        <| async {
            let resolver = AuthenticatedScopeResolver() :> IStorageScopeResolver

            match! resolver.Resolve emptyRequest with
            | Ok _ -> failtest "Expected Error NotAuthenticated"
            | Error e -> Expect.equal e NotAuthenticated "no user means NotAuthenticated"
        }

        testCaseAsync "Rejects anonymous users"
        <| async {
            let resolver = AuthenticatedScopeResolver() :> IStorageScopeResolver

            let request = {
                emptyRequest with
                    User = Some AuthenticatedUser.anonymous
            }

            match! resolver.Resolve request with
            | Ok _ -> failtest "Expected Error NotAuthenticated"
            | Error e -> Expect.equal e NotAuthenticated "anonymous user means NotAuthenticated"
        }
    ]

// ─── TeamScopeResolver ───────────────────────────────────────────────

let private makeTeamResolver () =
    let storage = freshStorage ()
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    let teamStore = TeamStore(storage, notifications)
    let cache = new MemoryCache(MemoryCacheOptions()) :> IMemoryCache
    teamStore, new TeamScopeResolver(teamStore, cache, notifications)

/// Same shape, but the store and the resolver sit on SEPARATE
/// notification channels, so a membership write never reaches the
/// resolver's cache-eviction handler. That is the only way to reach
/// the stale-cache path from a test: `InMemoryNotificationChannel`
/// invokes subscribers inline on `Publish`, so with one shared
/// channel `RemoveMember` evicts the entry before it returns and the
/// stale window a caller would actually hit — a direct-to-store
/// removal by a script, or a second instance in a multi-node
/// deployment whose channel is in-process — cannot be reproduced.
let private makeDetachedTeamResolver () =
    let storage = freshStorage ()
    let storeChannel = InMemoryNotificationChannel(None) :> INotificationChannel
    let resolverChannel = InMemoryNotificationChannel(None) :> INotificationChannel
    let teamStore = TeamStore(storage, storeChannel)
    let cache = new MemoryCache(MemoryCacheOptions()) :> IMemoryCache
    teamStore, new TeamScopeResolver(teamStore, cache, resolverChannel)

let private seedTeam (teamStore: TeamStore) (userId: string) (teamId: string) = async {
    let! _ = teamStore.CreateTeam(teamId, teamId + "-name")
    let! _ = teamStore.AddMember(teamId, userId, Owner)
    let! _ = teamStore.SetActiveTeam(userId, teamId)
    return ()
}

/// Give `teamId` a second Owner so the first one is removable.
///
/// `RemoveMember` refuses to remove a team's LAST Owner — a revocation
/// test that seeds a sole Owner is therefore asserting against a
/// no-op. Both revocation tests below used to do exactly that and
/// passed only because `TeamStore.GetTeamMembers` was mangling every
/// `UserId` on Windows (`LocalFileStorage.List` leaked the OS path
/// separator), so `IsLastOwner` never fired. With that fixed, the
/// refusal is real on every platform and the co-owner is what makes
/// these tests exercise revocation rather than the guard. (Phase 617.)
let private addCoOwner (teamStore: TeamStore) (teamId: string) = async {
    let! _ = teamStore.AddMember(teamId, "co-owner", Owner)
    return ()
}

let private teamTests =
    testList "TeamScopeResolver" [
        testCaseAsync "Resolves authenticated team member to team-scoped, persistent"
        <| async {
            let teamStore, resolver = makeTeamResolver ()
            do! seedTeam teamStore "alice" "acme"

            let request = { emptyRequest with User = Some alice }

            match! (resolver :> IStorageScopeResolver).Resolve request with
            | Error e -> failtestf "Expected Ok; got Error: %A" e
            | Ok scope ->
                Expect.equal scope.ScopeId "acme" "scope uses team id"
                Expect.equal scope.Container "team-acme" "container matches team convention"
                Expect.isTrue scope.Persist "team mode persists"
        }

        testCaseAsync "Rejects authenticated user with no active team"
        <| async {
            let _, resolver = makeTeamResolver ()
            let request = { emptyRequest with User = Some alice }

            match! (resolver :> IStorageScopeResolver).Resolve request with
            | Ok _ -> failtest "Expected Error NoActiveTeam"
            | Error e -> Expect.equal e NoActiveTeam "user without active team yields NoActiveTeam"
        }

        testCaseAsync "Rejects requests without a user"
        <| async {
            let _, resolver = makeTeamResolver ()

            match! (resolver :> IStorageScopeResolver).Resolve emptyRequest with
            | Ok _ -> failtest "Expected Error NotAuthenticated"
            | Error e -> Expect.equal e NotAuthenticated "no user means NotAuthenticated"
        }

        testCaseAsync "Rejects anonymous users"
        <| async {
            let _, resolver = makeTeamResolver ()

            let request = {
                emptyRequest with
                    User = Some AuthenticatedUser.anonymous
            }

            match! (resolver :> IStorageScopeResolver).Resolve request with
            | Ok _ -> failtest "Expected Error NotAuthenticated"
            | Error e -> Expect.equal e NotAuthenticated "anonymous user means NotAuthenticated"
        }

        testCaseAsync "Member removed from active team resolves to NoActiveTeam after invalidation"
        <| async {
            let teamStore, resolver = makeTeamResolver ()
            do! seedTeam teamStore "alice" "acme"
            do! addCoOwner teamStore "acme"

            let request = { emptyRequest with User = Some alice }

            // Warm the cache so we exercise the invalidation path.
            let! _ = (resolver :> IStorageScopeResolver).Resolve request

            // RemoveMember clears the active-team pointer when the
            // removed team is active; the API handler also invalidates
            // the resolver cache (simulated manually here).
            match! teamStore.RemoveMember("acme", "alice") with
            | Ok() -> ()
            | Error e -> failtestf "RemoveMember must succeed for this test to mean anything: %s" e

            resolver.InvalidateUser "alice"

            match! (resolver :> IStorageScopeResolver).Resolve request with
            | Ok _ -> failtest "Expected Error after membership revocation"
            | Error NoActiveTeam -> ()
            | Error other -> failtestf "Expected NoActiveTeam; got %A" other
        }

        testCaseAsync "Stale active-team cache on a revoked member → NotTeamMember (defense in depth)"
        <| async {
            // Detached channels: the store's `Removed` event never
            // reaches the resolver, so its cached active-team entry
            // stays stale — the window this test exists to cover.
            let teamStore, resolver = makeDetachedTeamResolver ()
            do! seedTeam teamStore "alice" "acme"
            do! addCoOwner teamStore "acme"

            let request = { emptyRequest with User = Some alice }

            // Warm the cache.
            let! _ = (resolver :> IStorageScopeResolver).Resolve request

            // Remove membership but deliberately DO NOT call
            // InvalidateUser — simulates the race window between
            // TeamStore.RemoveMember and the API handler getting a
            // chance to invalidate, or a direct-to-store removal via
            // a script. The resolver's per-request membership check
            // is the braces that make this safe.
            match! teamStore.RemoveMember("acme", "alice") with
            | Ok() -> ()
            | Error e -> failtestf "RemoveMember must succeed for this test to mean anything: %s" e

            match! (resolver :> IStorageScopeResolver).Resolve request with
            | Ok _ -> failtest "Expected Error; stale cache should not grant access"
            | Error(NotTeamMember teamId) -> Expect.equal teamId "acme" "names the team that denied"
            | Error other -> failtestf "Expected NotTeamMember; got %A" other
        }

        testCaseAsync "InvalidateUser forces a re-fetch after SetActiveTeam changes"
        <| async {
            let teamStore, resolver = makeTeamResolver ()
            do! seedTeam teamStore "alice" "first-team"

            let request = { emptyRequest with User = Some alice }

            // Warm the cache.
            let! _ = (resolver :> IStorageScopeResolver).Resolve request

            // Simulate team switch (compose invokes this from PlatformApi.SetActiveTeam).
            let! _ = teamStore.CreateTeam("second-team", "second-team-name")
            let! _ = teamStore.AddMember("second-team", "alice", Owner)
            let! _ = teamStore.SetActiveTeam("alice", "second-team")
            resolver.InvalidateUser "alice"

            match! (resolver :> IStorageScopeResolver).Resolve request with
            | Error e -> failtestf "Expected Ok; got Error: %A" e
            | Ok scope ->
                Expect.equal scope.ScopeId "second-team" "cache was invalidated and re-read returned new active team"
        }

        testCaseAsync "GetActiveTeam storage failure → ScopeResolutionFailed, not silent NoActiveTeam"
        <| async {
            // A store outage must surface distinctly from "user has no
            // active team" so an operator can tell a config error from
            // an infra incident.
            let throwingStore =
                { new ITeamStore with
                    member _.CreateTeam(_, _) = async { return Error "unused" }
                    member _.DeleteTeam _ = async { return Error "unused" }
                    member _.GetTeam _ = async { return None }
                    member _.ListTeams() = async { return [] }
                    member _.AddMember(_, _, _) = async { return Error "unused" }
                    member _.RemoveMember(_, _) = async { return Error "unused" }
                    member _.ChangeMemberRole(_, _, _) = async { return Error "unused" }
                    member _.GetTeamsForUser _ = async { return [] }
                    member _.GetTeamMembers _ = async { return [] }
                    member _.GetMemberRole(_, _) = async { return None }
                    member _.GetActiveTeam _ = async { return raise (exn "blob store unreachable") }
                    member _.SetActiveTeam(_, _) = async { return Error "unused" }
                    member _.SetArchived(_, _) = async { return Error "unused" }
                    member _.PurgeTeam _ = async { return Error "unused" }
                    member _.PurgeUser _ = async { return Error "unused" }
                }

            let cache = new MemoryCache(MemoryCacheOptions()) :> IMemoryCache
            let notifications = InMemoryNotificationChannel(None) :> INotificationChannel

            let resolver =
                new TeamScopeResolver(throwingStore, cache, notifications) :> IStorageScopeResolver

            let request = { emptyRequest with User = Some alice }

            match! resolver.Resolve request with
            | Error(ScopeResolutionFailed msg) ->
                Expect.stringContains msg "unreachable" "carries the underlying store failure"
            | Error NoActiveTeam ->
                failtest "storage outage must NOT degrade to NoActiveTeam (the silent fail it used to be)"
            | other -> failtestf "Expected ScopeResolutionFailed; got %A" other
        }
    ]

// ─── Aggregated ──────────────────────────────────────────────────────

let tests =
    testList "StorageScopeResolver" [ anonymousTests; authEphemeralTests; authenticatedTests; teamTests ]
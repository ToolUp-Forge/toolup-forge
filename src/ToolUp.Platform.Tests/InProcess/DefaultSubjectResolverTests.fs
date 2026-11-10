module ToolUp.Platform.Tests.InProcess.DefaultSubjectResolverTests

open System
open System.Threading
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Hosting
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Config.NotificationChannel
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Scope

// ─── DefaultSubjectResolver ──────────────────────────────────────────
//
// Binds the SDK's default `ISubjectResolver` to the shared
// `ISubjectResolverContract` pack. The factory supplies the impl's
// private substrate (a fresh `IMemoryCache` + `InMemoryNotificationChannel`
// per resolver) so the contract's "one fresh resolver per test" promise
// holds — each gets its own cache, so no active-team answer leaks across
// scenarios. External resolver impls (a grain, an actor) bind the same
// pack from their own test file with their own factory.
//
// The resolver is the deployment's only one, so the team-cache cases the
// retired team scope resolver carried are pinned here against it: the
// membership-change subscription is taken when the hosted service STARTS
// (never in the constructor), it evicts on `ActiveTeamSet`, and a stale
// cache entry never grants a removed member its team.

let private factory: ISubjectResolverContract.ResolverFactory =
    fun surfaces teamStore ->
        let cache = new MemoryCache(MemoryCacheOptions()) :> IMemoryCache
        let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
        new DefaultSubjectResolver.DefaultSubjectResolver(surfaces, teamStore, cache, notifications) :> ISubjectResolver

let private alice: AuthenticatedUser = {
    UserId = UserId "alice"
    DisplayName = "Alice"
    Email = Some "alice@example.com"
    TenantId = None
    Roles = []
    DirectoryRoles = []
    Admission = PrincipalAdmission.Member
}

let private request: SubjectResolutionRequest = {
    User = Some alice
    SessionId = None
    SessionIdVerified = false
    Claim = None
    Headers = Map.empty
}

/// A team-surface resolver over `teamStore`, listening on `channel`.
let private teamResolver (teamStore: ITeamStore) (channel: INotificationChannel) =
    new DefaultSubjectResolver.DefaultSubjectResolver(
        [ SurfaceProfile.team ],
        Some teamStore,
        new MemoryCache(MemoryCacheOptions()),
        channel
    )

let private start (resolver: DefaultSubjectResolver.DefaultSubjectResolver) =
    (resolver :> IHostedService).StartAsync CancellationToken.None
    |> Async.AwaitTask

let private seedTeam (teamStore: ITeamStore) (teamId: string) = async {
    let! _ = teamStore.CreateTeam(TeamId teamId, teamId + "-name")
    let! _ = teamStore.AddMember(TeamId teamId, UserId "alice", Owner)
    let! _ = teamStore.SetActiveTeam(UserId "alice", TeamId teamId)
    // A second Owner, so alice is removable (the last Owner is not).
    let! _ = teamStore.AddMember(TeamId teamId, UserId "co-owner", Owner)
    return ()
}

let private activeTeamSet (channel: INotificationChannel) =
    channel.Publish(
        NotificationKind.PlatformReservedScope,
        MembershipChanged {
            TeamId = TeamId ""
            AffectedUserId = UserId "alice"
            ChangeKind = MembershipChangeKind.ActiveTeamSet
            PublishedAt = DateTime.UtcNow
        }
    )

let private teamCacheTests =
    testList "DefaultSubjectResolver — the one membership cache" [
        testCaseAsync "a started resolver evicts the active-team entry on ActiveTeamSet"
        <| async {
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel

            let teamStore =
                TeamStore(InMemoryBlobStorage(), InMemoryNotificationChannel(None)) :> ITeamStore

            do! seedTeam teamStore "first-team"
            let resolver = teamResolver teamStore channel
            do! start resolver

            let! _ = (resolver :> ISubjectResolver).Resolve request
            let! _ = teamStore.CreateTeam(TeamId "second-team", "second-team-name")
            let! _ = teamStore.AddMember(TeamId "second-team", UserId "alice", Owner)
            let! _ = teamStore.SetActiveTeam(UserId "alice", TeamId "second-team")
            do! activeTeamSet channel

            match! (resolver :> ISubjectResolver).Resolve request with
            | Ok(Subject.TeamMember(_, teamId)) -> Expect.equal teamId "second-team" "the evicted entry is re-read"
            | other -> failtestf "expected the second team; got %A" other
        }

        testCaseAsync "the constructor subscribes to nothing: before StartAsync the cache is not evicted"
        <| async {
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel

            let teamStore =
                TeamStore(InMemoryBlobStorage(), InMemoryNotificationChannel(None)) :> ITeamStore

            do! seedTeam teamStore "first-team"
            let resolver = teamResolver teamStore channel

            let! _ = (resolver :> ISubjectResolver).Resolve request
            let! _ = teamStore.CreateTeam(TeamId "second-team", "second-team-name")
            let! _ = teamStore.AddMember(TeamId "second-team", UserId "alice", Owner)
            let! _ = teamStore.SetActiveTeam(UserId "alice", TeamId "second-team")
            do! activeTeamSet channel

            match! (resolver :> ISubjectResolver).Resolve request with
            | Ok(Subject.TeamMember(_, teamId)) ->
                Expect.equal teamId "first-team" "no subscription was taken, so the cached pointer stands"
            | other -> failtestf "expected the cached first team; got %A" other
        }

        testCaseAsync "StopAsync releases the subscription"
        <| async {
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel

            let teamStore =
                TeamStore(InMemoryBlobStorage(), InMemoryNotificationChannel(None)) :> ITeamStore

            do! seedTeam teamStore "first-team"
            let resolver = teamResolver teamStore channel
            do! start resolver

            do! (resolver :> IHostedService).StopAsync CancellationToken.None |> Async.AwaitTask

            let! _ = (resolver :> ISubjectResolver).Resolve request
            let! _ = teamStore.CreateTeam(TeamId "second-team", "second-team-name")
            let! _ = teamStore.AddMember(TeamId "second-team", UserId "alice", Owner)
            let! _ = teamStore.SetActiveTeam(UserId "alice", TeamId "second-team")
            do! activeTeamSet channel

            match! (resolver :> ISubjectResolver).Resolve request with
            | Ok(Subject.TeamMember(_, teamId)) ->
                Expect.equal teamId "first-team" "a stopped resolver no longer evicts"
            | other -> failtestf "expected the cached first team; got %A" other
        }

        testCaseAsync "a stale active-team entry never grants a removed member its team"
        <| async {
            // The store publishes on its own channel, so the resolver's
            // cached entry stays stale: the per-request membership probe is
            // what refuses.
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel

            let teamStore =
                TeamStore(InMemoryBlobStorage(), InMemoryNotificationChannel(None)) :> ITeamStore

            do! seedTeam teamStore "acme"
            let resolver = teamResolver teamStore channel
            do! start resolver

            let! _ = (resolver :> ISubjectResolver).Resolve request

            match! teamStore.RemoveMember(TeamId "acme", UserId "alice") with
            | Ok() -> ()
            | Error e -> failtestf "RemoveMember must succeed for this test to mean anything: %s" e

            match! (resolver :> ISubjectResolver).Resolve request with
            | Error(SubjectResolutionError.NotTeamMember teamId) -> Expect.equal teamId (TeamId "acme") "names the team"
            | other -> failtestf "expected NotTeamMember; got %A" other
        }
    ]

let tests =
    testList "DefaultSubjectResolver" [
        ISubjectResolverContract.tests "DefaultSubjectResolver" factory
        teamCacheTests
    ]
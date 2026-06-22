module ToolUp.Platform.Tests.InProcess.TeamAdminLifecycleTests

open System
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Platform-Admin team lifecycle — ListAllTeams / Archive / Restore /
//     DeleteTeamHard, plus archived-team access enforcement ────────────
//
// Exercises the four new `TeamApi` admin methods through the inner-record
// builder `PlatformApiHandler.teamApi config ctx : TeamApi` (no
// Fable.Remoting HTTP machinery), matching `TeamCreationPolicyTests`.

// ─── Fakes / fixtures ────────────────────────────────────────────────

type private SilentAuditLog() =
    interface IAuditLog with
        member _.Record(_, _) = async { return () }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private platformContainer = "_platform"

let private ctxFor (userId: string) (teamStore: ITeamStore) (adminStore: IPlatformAdminStore) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<ITeamStore>(teamStore) |> ignore
    services.AddSingleton<IPlatformAdminStore>(adminStore) |> ignore
    services.AddSingleton<IAuditLog>(SilentAuditLog() :> IAuditLog) |> ignore
    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx

let private freshStorage () = InMemoryBlobStorage() :> IBlobStorage

let private teamStoreOver (storage: IBlobStorage) =
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    TeamStore(storage, notifications) :> ITeamStore

let private adminStoreOver (storage: IBlobStorage) =
    PlatformAdminStore.BlobBackedPlatformAdminStore(storage, SilentAuditLog() :> IAuditLog) :> IPlatformAdminStore

let private teamConfig = {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
}

/// Seed a team with the given members (userId, role) directly through the
/// store, returning the minted team id.
let private seedTeam (ts: ITeamStore) (name: string) (members: (string * TeamRole) list) = async {
    let teamId = Guid.NewGuid().ToString("N")
    let! _ = ts.CreateTeam(teamId, name)

    for (userId, role) in members do
        let! _ = ts.AddMember(teamId, userId, role)
        ()

    return teamId
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Platform-Admin team lifecycle" [

        testCaseAsync "ListAllTeams denies a non-admin caller"
        <| async {
            let storage = freshStorage ()
            let ts = teamStoreOver storage
            let adminStore = adminStoreOver storage
            let api = PlatformApiHandler.teamApi teamConfig (ctxFor "bob" ts adminStore)

            let! result = api.ListAllTeams()

            match result with
            | Error msg -> Expect.equal msg "Platform Admin required" "specific deny message"
            | Ok _ -> failtest "expected Error from non-admin caller"
        }

        testCaseAsync "ListAllTeams returns member counts + owner/admin ids for an admin"
        <| async {
            let storage = freshStorage ()
            let ts = teamStoreOver storage
            let adminStore = adminStoreOver storage
            let! _ = adminStore.AssignPlatformAdmin("_bootstrap", "alice")

            let! teamId = seedTeam ts "Engineering" [ "owner1", Owner; "admin1", Admin; "member1", Member ]

            let api = PlatformApiHandler.teamApi teamConfig (ctxFor "alice" ts adminStore)
            let! result = api.ListAllTeams()

            match result with
            | Error msg -> failtestf "expected Ok, got Error %s" msg
            | Ok summaries ->
                let team = summaries |> List.find (fun t -> t.TeamId = teamId)
                Expect.equal team.Name "Engineering" "name surfaced"
                Expect.equal team.MemberCount 3 "all three members counted"
                Expect.equal team.Owners [ "owner1" ] "owner id surfaced"
                Expect.equal team.Admins [ "admin1" ] "admin id surfaced"
                Expect.isFalse team.Archived "fresh team is not archived"
        }

        testCaseAsync
            "ArchiveTeam hides the team from members, bumps active scope, blocks re-select; RestoreTeam reverses"
        <| async {
            let storage = freshStorage ()
            let ts = teamStoreOver storage
            let adminStore = adminStoreOver storage
            let! _ = adminStore.AssignPlatformAdmin("_bootstrap", "alice")

            let! teamId = seedTeam ts "Marketing" [ "member1", Owner ]
            // member1 has it as their active team (CreateTeam path would do
            // this for the owner; seedTeam adds the membership only, so set
            // it explicitly).
            let! _ = ts.SetActiveTeam("member1", teamId)

            let adminApi = PlatformApiHandler.teamApi teamConfig (ctxFor "alice" ts adminStore)

            let memberApi =
                PlatformApiHandler.teamApi teamConfig (ctxFor "member1" ts adminStore)

            // Archive.
            let! archiveResult = adminApi.ArchiveTeam teamId
            Expect.isOk archiveResult "archive succeeds for admin"

            // Hidden from the member's team list.
            let! myTeams = memberApi.GetMyTeams()
            Expect.isEmpty myTeams "archived team filtered from GetMyTeams"

            // Active-team pointer was bumped.
            let! active = ts.GetActiveTeam "member1"
            Expect.isNone active "member is bumped to the no-active-team state on archive"

            // Re-select is rejected.
            let! reselect = memberApi.SetActiveTeam teamId

            match reselect with
            | Error msg -> Expect.equal msg "This team is archived" "archived team can't be re-selected"
            | Ok() -> failtest "expected Error re-selecting an archived team"

            // Restore reverses it.
            let! restoreResult = adminApi.RestoreTeam teamId
            Expect.isOk restoreResult "restore succeeds for admin"

            let! myTeamsAfter = memberApi.GetMyTeams()
            Expect.equal (myTeamsAfter |> List.map (fun t -> t.TeamId)) [ teamId ] "restored team is visible again"

            let! reselectAfter = memberApi.SetActiveTeam teamId
            Expect.isOk reselectAfter "restored team is selectable again"
        }

        testCaseAsync "DeleteTeamHard purges the team record and every membership row"
        <| async {
            let storage = freshStorage ()
            let ts = teamStoreOver storage
            let adminStore = adminStoreOver storage
            let! _ = adminStore.AssignPlatformAdmin("_bootstrap", "alice")

            let! teamId = seedTeam ts "Doomed" [ "owner1", Owner; "member1", Member ]

            let adminApi = PlatformApiHandler.teamApi teamConfig (ctxFor "alice" ts adminStore)
            let! deleteResult = adminApi.DeleteTeamHard teamId
            Expect.isOk deleteResult "delete succeeds for admin"

            let! gone = ts.GetTeam teamId
            Expect.isNone gone "team record is purged"

            let! ownerTeams = ts.GetTeamsForUser "owner1"
            Expect.isEmpty ownerTeams "owner's membership row is purged"

            let! memberTeams = ts.GetTeamsForUser "member1"
            Expect.isEmpty memberTeams "member's membership row is purged"
        }

        testCaseAsync "DeleteTeamHard denies a non-admin caller"
        <| async {
            let storage = freshStorage ()
            let ts = teamStoreOver storage
            let adminStore = adminStoreOver storage
            let! teamId = seedTeam ts "Safe" [ "owner1", Owner ]

            let api = PlatformApiHandler.teamApi teamConfig (ctxFor "bob" ts adminStore)
            let! result = api.DeleteTeamHard teamId

            match result with
            | Error msg -> Expect.equal msg "Platform Admin required" "specific deny message"
            | Ok() -> failtest "expected Error from non-admin caller"

            let! stillThere = ts.GetTeam teamId
            Expect.isSome stillThere "team is untouched on the deny path"
        }

        testCaseAsync "A legacy team blob with no `archived` property deserializes as not-archived"
        <| async {
            let storage = freshStorage ()
            // Hand-write a team blob in the pre-Archived shape.
            let legacyJson =
                """{"teamId":"legacy1","name":"Legacy Team","createdAt":"2020-01-01T00:00:00.0000000Z"}"""

            let! _ = storage.Upload(platformContainer, "teams/legacy1.json", Encoding.UTF8.GetBytes legacyJson)

            let ts = teamStoreOver storage
            let! team = ts.GetTeam "legacy1"

            match team with
            | Some t ->
                Expect.equal t.Name "Legacy Team" "legacy name round-trips"
                Expect.isFalse t.Archived "missing `archived` property defaults to false"
            | None -> failtest "expected the legacy blob to deserialize"
        }
    ]
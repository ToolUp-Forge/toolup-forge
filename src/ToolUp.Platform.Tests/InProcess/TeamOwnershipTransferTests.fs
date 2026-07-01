module ToolUp.Platform.Tests.InProcess.TeamOwnershipTransferTests

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 304 — TeamApi.TransferOwnership contract tests ────────────
//
// Since the 2026-06-04 Platform-Management refactor, `TeamRole.Owner`
// is set once at team-creation and is unassignable via the role
// pickers, so ownership had no exit path. `TransferOwnership` is the
// single affordance: the outgoing Owner names a current member, the
// handler promotes them to Owner and demotes the caller to Admin in a
// promote-then-demote order that never leaves the team ownerless.
//
// Handler tests use `teamApi config ctx : TeamApi` (the inner-record
// builder in `PlatformApiHandler.fs`) so the suite doesn't go through
// Fable.Remoting's HTTP machinery — matches `TeamCreationPolicyTests`.

// ─── Fakes ───────────────────────────────────────────────────────────

type private CapturingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()

    member _.Recorded = recorded |> Seq.toList

    member this.WaitForEventOfKind(eventTypeName: string, maxMs: int) : Async<AuditEvent option> = async {
        let deadline = DateTime.UtcNow.AddMilliseconds(float maxMs)

        let rec loop () = async {
            let hit =
                this.Recorded
                |> List.tryFind (fun (_, ev) -> AuditEvent.eventTypeName ev = eventTypeName)

            match hit with
            | Some(_, ev) -> return Some ev
            | None when DateTime.UtcNow < deadline ->
                do! Async.Sleep 10
                return! loop ()
            | None -> return None
        }

        return! loop ()
    }

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

// ─── Fixtures ────────────────────────────────────────────────────────

let private ctxFor
    (userId: string)
    (teamStore: ITeamStore)
    (platformAdminStore: IPlatformAdminStore)
    (auditLog: IAuditLog)
    : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<ITeamStore>(teamStore) |> ignore
    services.AddSingleton<IPlatformAdminStore>(platformAdminStore) |> ignore
    services.AddSingleton<IAuditLog>(auditLog) |> ignore

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx

let private freshTeamStore () =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    TeamStore(storage, notifications)

let private freshAdminStore (auditLog: IAuditLog) =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    PlatformAdminStore.BlobBackedPlatformAdminStore(storage, auditLog) :> IPlatformAdminStore

let private teamConfig = {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
}

/// Seed a team with an Owner plus the named `(userId, role)` members,
/// writing straight through the store so the handler tests start from a
/// known membership state.
let private seedTeam (ts: ITeamStore) (teamId: string) (ownerId: string) (members: (string * TeamRole) list) = async {
    let! _ = ts.CreateTeam(teamId, "Team")
    let! _ = ts.AddMember(teamId, ownerId, Owner)

    for uid, role in members do
        let! _ = ts.AddMember(teamId, uid, role)
        ()
}

/// Role a user holds on a team, read back through the store.
let private roleOf (ts: ITeamStore) (teamId: string) (userId: string) = ts.GetMemberRole(teamId, userId)

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 304 — TeamApi.TransferOwnership" [

        testCaseAsync "Owner transfers to a member: exactly one Owner (the target), former Owner is Admin"
        <| async {
            let auditLog = CapturingAuditLog()
            let ts = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            do! seedTeam ts "t1" "alice" [ "bob", Member ]

            let ctx = ctxFor "alice" ts adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi teamConfig ctx

            let! result = api.TransferOwnership("t1", "bob")
            Expect.equal result (Ok()) "transfer succeeds"

            let! members = ts.GetTeamMembers "t1"
            let owners = members |> List.filter (fun m -> m.Role = Owner) |> List.map _.UserId
            Expect.equal owners [ "bob" ] "exactly one Owner after transfer — the target"

            let! aliceRole = roleOf ts "t1" "alice"
            Expect.equal aliceRole (Some Admin) "former Owner demoted to Admin"
        }

        testCaseAsync "Successful transfer emits TeamOwnershipTransferred audit naming both parties"
        <| async {
            let auditLog = CapturingAuditLog()
            let ts = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            do! seedTeam ts "t1" "alice" [ "bob", Admin ]

            let ctx = ctxFor "alice" ts adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi teamConfig ctx

            let! _ = api.TransferOwnership("t1", "bob")
            let! ev = auditLog.WaitForEventOfKind("TeamOwnershipTransferred", 1000)

            match ev with
            | Some(AuditEvent.TeamOwnershipTransferred p) ->
                Expect.equal p.TeamId "t1" "audit records the team"
                Expect.equal p.FromUserId "alice" "audit records the outgoing Owner"
                Expect.equal p.ToUserId "bob" "audit records the incoming Owner"
                Expect.equal p.ActorUserId "alice" "audit records the actor"
            | Some other -> failtestf "expected TeamOwnershipTransferred, got %A" other
            | None -> failtest "TeamOwnershipTransferred audit not emitted within 1000ms"
        }

        testCaseAsync "Non-Owner caller (Admin) is rejected; roles unchanged"
        <| async {
            let auditLog = CapturingAuditLog()
            let ts = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            do! seedTeam ts "t1" "alice" [ "bob", Admin; "carol", Member ]

            // bob is an Admin, not the Owner — he must not be able to
            // transfer ownership (and NOT via a Platform-Admin bypass).
            let ctx = ctxFor "bob" ts adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi teamConfig ctx

            let! result = api.TransferOwnership("t1", "carol")

            match result with
            | Error msg -> Expect.equal msg "Only the team Owner can transfer ownership" "specific non-Owner deny"
            | Ok() -> failtest "expected Error from a non-Owner caller"

            let! aliceRole = roleOf ts "t1" "alice"
            let! carolRole = roleOf ts "t1" "carol"
            Expect.equal aliceRole (Some Owner) "original Owner unchanged on the deny path"
            Expect.equal carolRole (Some Member) "target role unchanged on the deny path"
        }

        testCaseAsync "Transfer to a non-member is rejected"
        <| async {
            let auditLog = CapturingAuditLog()
            let ts = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            do! seedTeam ts "t1" "alice" [ "bob", Member ]

            let ctx = ctxFor "alice" ts adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi teamConfig ctx

            let! result = api.TransferOwnership("t1", "stranger")

            match result with
            | Error msg ->
                Expect.equal msg "The new owner must be an existing member of the team" "non-member deny message"
            | Ok() -> failtest "expected Error transferring to a non-member"

            let! aliceRole = roleOf ts "t1" "alice"
            Expect.equal aliceRole (Some Owner) "Owner unchanged when the target isn't a member"
        }

        testCaseAsync "Transfer to self is rejected"
        <| async {
            let auditLog = CapturingAuditLog()
            let ts = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            do! seedTeam ts "t1" "alice" [ "bob", Member ]

            let ctx = ctxFor "alice" ts adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi teamConfig ctx

            let! result = api.TransferOwnership("t1", "alice")

            match result with
            | Error msg -> Expect.equal msg "You are already the Owner of this team" "self-target deny message"
            | Ok() -> failtest "expected Error transferring to self"
        }

        testCaseAsync "TransferOwnership with no team store returns the mode message"
        <| async {
            let auditLog = CapturingAuditLog()
            let ts = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            // Context WITHOUT an ITeamStore registered.
            let services = ServiceCollection()
            services.AddSingleton<IPlatformAdminStore>(adminStore) |> ignore
            services.AddSingleton<IAuditLog>(auditLog :> IAuditLog) |> ignore
            let sp = services.BuildServiceProvider() :> IServiceProvider
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.RequestServices <- sp
            ctx.Items["ToolUp.UserId"] <- box "alice"

            let api = PlatformApiHandler.teamApi teamConfig ctx
            let! result = api.TransferOwnership("t1", "bob")

            match result with
            | Error msg -> Expect.equal msg "Team management not available in this mode" "no-store mode message"
            | Ok() -> failtest "expected Error with no team store"
        }
    ]
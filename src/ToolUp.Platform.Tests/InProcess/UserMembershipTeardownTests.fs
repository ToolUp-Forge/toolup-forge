module ToolUp.Platform.Tests.InProcess.UserMembershipTeardownTests

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

// ─── Phase 545 — user-scope offboard completeness (`PurgeUser`) ───────
//
// Store-level: `TeamStore.PurgeUser` refuses a last-Owner purge (naming
// the team), strips every membership row across teams, deletes the
// active-team pointer, publishes `MembershipChanged.Removed` per
// affected team, and is idempotent. Hook-level:
// `UserMembershipTeardownLifecycle` skips non-user scopes / missing
// substrate, fails loudly on a refused purge, audits one
// `MemberRemoved` per stripped team, and sweeps the pending email
// invite via the registered `IUserDirectory`. End-to-end: a full
// `DeprovisionTenant("user-<id>")` through the tenant API handler
// leaves no membership row, no pointer, no matching invite.

// ─── Fixtures ────────────────────────────────────────────────────────

let private freshChannel () =
    InMemoryNotificationChannel(None) :> INotificationChannel

let private teamStoreOver (storage: IBlobStorage) (notifications: INotificationChannel) =
    TeamStore(storage, notifications)

let private freshTeamStore () =
    teamStoreOver (InMemoryBlobStorage() :> IBlobStorage) (freshChannel ())

/// Seed a team with the given members (userId, role) directly through
/// the store, returning the minted team id.
let private seedTeam (ts: TeamStore) (name: string) (members: (string * TeamRole) list) = async {
    let teamId = Guid.NewGuid().ToString("N")
    let! _ = ts.CreateTeam(teamId, name)

    for (userId, role) in members do
        let! _ = ts.AddMember(teamId, userId, role)
        ()

    return teamId
}

/// Audit collector recording (scopeId, event) pairs.
type private RecordingAuditLog() =
    let events = ConcurrentBag<string * AuditEvent>()
    member _.Events = events |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, event) = async { events.Add(scopeId, event) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Email-set-backed `IPendingInviteStore` stub — tracks which emails
/// hold a pending entry without constructing `PendingInviteByEmail`
/// values (only `Remove` and the presence set matter to the sweep).
type private EmailSetInviteStore(seed: string list) =
    let emails = ConcurrentDictionary<string, unit>()

    do
        for e in seed do
            emails[e] <- ()

    member _.Contains(email: string) = emails.ContainsKey email

    interface IPendingInviteStore with
        member _.Upsert(_, _) =
            failwith "EmailSetInviteStore.Upsert not used"

        member _.Remove(email) = async {
            match emails.TryRemove email with
            | true, _ -> return Ok()
            | false, _ -> return Error PendingInviteStoreError.NotFound
        }

        member _.TryConsumeForEmail _ =
            failwith "EmailSetInviteStore.TryConsumeForEmail not used"

        member _.ListAll() = async { return Ok [] }
        member _.SweepExpired() = async { return Ok 0 }

/// Fixed-map `IUserDirectory` stub resolving user ids to emails.
type private MapUserDirectory(emailsById: Map<string, string>) =
    interface IUserDirectory with
        member _.SearchUsers(_, _) = async { return Ok [] }

        member _.ResolveUsers(ids) = async {
            return
                Ok [
                    for id in ids do
                        match Map.tryFind id emailsById with
                        | Some email -> {
                            UserId = id
                            DisplayName = None
                            Email = Some email
                          }
                        | None -> ()
                ]
        }

        member _.NotifyInvitation _ = async { return Ok() }

/// Build an `IServiceProvider` for the hook from optional substrate.
let private providerWith
    (teamStore: ITeamStore option)
    (auditLog: IAuditLog option)
    (directory: IUserDirectory option)
    (invites: IPendingInviteStore option)
    : IServiceProvider =
    let services = ServiceCollection()

    teamStore
    |> Option.iter (fun s -> services.AddSingleton<ITeamStore>(s) |> ignore)

    auditLog |> Option.iter (fun a -> services.AddSingleton<IAuditLog>(a) |> ignore)

    directory
    |> Option.iter (fun d -> services.AddSingleton<IUserDirectory>(d) |> ignore)

    invites
    |> Option.iter (fun i -> services.AddSingleton<IPendingInviteStore>(i) |> ignore)

    services.BuildServiceProvider() :> IServiceProvider

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 545 — user-scope offboard (PurgeUser)" [

        // ── Store level ──────────────────────────────────────────────

        testCaseAsync "PurgeUser refuses when the user is the last Owner — the error names the team"
        <| async {
            let ts = freshTeamStore ()
            let! teamId = seedTeam ts "Solo-Owned" [ "alice", Owner; "bob", Member ]

            let! result = ts.PurgeUser "alice"

            match result with
            | Error msg ->
                Expect.stringContains msg teamId "the refusal names the blocking team"
                Expect.stringContains msg "last Owner" "the refusal states the reason"
            | Ok() -> failtest "expected Error purging the last Owner"

            // The refusal must leave the user's state untouched.
            let! teams = ts.GetTeamsForUser "alice"
            Expect.equal (teams |> List.map _.TeamId) [ teamId ] "membership row survives a refused purge"
        }

        testCaseAsync "PurgeUser strips every membership row across teams, leaving other members untouched"
        <| async {
            let ts = freshTeamStore ()
            let! team1 = seedTeam ts "One" [ "owner1", Owner; "victim", Member ]
            let! team2 = seedTeam ts "Two" [ "owner2", Owner; "victim", Admin ]

            let! result = ts.PurgeUser "victim"
            Expect.isOk result "purge succeeds for a non-last-Owner user"

            let! victimTeams = ts.GetTeamsForUser "victim"
            Expect.isEmpty victimTeams "every membership row is stripped"

            let! members1 = ts.GetTeamMembers team1
            Expect.equal (members1 |> List.map _.UserId) [ "owner1" ] "team 1 keeps its other members"

            let! members2 = ts.GetTeamMembers team2
            Expect.equal (members2 |> List.map _.UserId) [ "owner2" ] "team 2 keeps its other members"
        }

        testCaseAsync "PurgeUser deletes the active-team pointer"
        <| async {
            let ts = freshTeamStore ()
            let! teamId = seedTeam ts "Pointed" [ "owner1", Owner; "victim", Member ]
            let! _ = ts.SetActiveTeam("victim", teamId)

            let! result = ts.PurgeUser "victim"
            Expect.isOk result "purge succeeds"

            let! active = ts.GetActiveTeam "victim"
            Expect.isNone active "active-team pointer is deleted"
        }

        testCaseAsync "PurgeUser publishes MembershipChanged.Removed per affected team"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = freshChannel ()
            let ts = teamStoreOver storage notifications
            let! team1 = seedTeam ts "Pub-One" [ "owner1", Owner; "victim", Member ]
            let! team2 = seedTeam ts "Pub-Two" [ "owner2", Owner; "victim", Member ]

            let removed = ConcurrentBag<string * string>()

            let! _ =
                notifications.Subscribe(
                    NotificationKind.PlatformReservedScope,
                    fun envelope ->
                        match envelope.Notification with
                        | MembershipChanged p when p.ChangeKind = MembershipChangeKind.Removed ->
                            removed.Add(p.TeamId, p.AffectedUserId)
                        | _ -> ()
                )

            let! result = ts.PurgeUser "victim"
            Expect.isOk result "purge succeeds"

            let published = removed |> Set.ofSeq

            Expect.equal
                published
                (Set.ofList [ team1, "victim"; team2, "victim" ])
                "one Removed event per stripped team"
        }

        testCaseAsync "PurgeUser is idempotent — re-purging an already-purged user returns Ok"
        <| async {
            let ts = freshTeamStore ()
            let! _ = seedTeam ts "Once" [ "owner1", Owner; "victim", Member ]

            let! first = ts.PurgeUser "victim"
            Expect.isOk first "first purge succeeds"

            let! second = ts.PurgeUser "victim"
            Expect.isOk second "re-purge is an Ok no-op"

            // A never-seen user also purges clean.
            let! ghost = ts.PurgeUser "never-existed"
            Expect.isOk ghost "purging a user with no state returns Ok"
        }

        // ── Hook level ───────────────────────────────────────────────

        testCaseAsync "hook skips a team scope"
        <| async {
            let ts = freshTeamStore ()
            let sp = providerWith (Some(ts :> ITeamStore)) None None None
            let hook = UserMembershipTeardownLifecycle.create sp

            let! result = hook.OnDeprovisioned("team-abc", "admin-1")

            match result with
            | LifecycleHookResult.Skipped reason -> Expect.stringContains reason "not a user scope" "skip names why"
            | other -> failtestf "expected Skipped on a team scope, got %A" other
        }

        testCaseAsync "hook skips when no ITeamStore is resolvable"
        <| async {
            let sp = providerWith None None None None
            let hook = UserMembershipTeardownLifecycle.create sp

            let! result = hook.OnDeprovisioned("user-u1", "admin-1")

            match result with
            | LifecycleHookResult.Skipped reason -> Expect.stringContains reason "ITeamStore" "skip names the substrate"
            | other -> failtestf "expected Skipped with no team store, got %A" other
        }

        testCaseAsync "hook fails (not skips) when the purge is refused — last Owner"
        <| async {
            let ts = freshTeamStore ()
            let! teamId = seedTeam ts "Held" [ "victim", Owner ]
            let sp = providerWith (Some(ts :> ITeamStore)) None None None
            let hook = UserMembershipTeardownLifecycle.create sp

            let! result = hook.OnDeprovisioned("user-victim", "admin-1")

            match result with
            | LifecycleHookResult.Failed msg -> Expect.stringContains msg teamId "the failure names the blocking team"
            | other -> failtestf "expected Failed for a last-Owner refusal, got %A" other
        }

        testCaseAsync "hook purges, audits one MemberRemoved per stripped team, and sweeps the pending invite"
        <| async {
            let ts = freshTeamStore ()
            let! team1 = seedTeam ts "Audit-One" [ "owner1", Owner; "victim", Member ]
            let! team2 = seedTeam ts "Audit-Two" [ "owner2", Owner; "victim", Member ]

            let audit = RecordingAuditLog()
            let directory = MapUserDirectory(Map [ "victim", "victim@example.com" ])
            let invites = EmailSetInviteStore [ "victim@example.com" ]

            let sp =
                providerWith
                    (Some(ts :> ITeamStore))
                    (Some(audit :> IAuditLog))
                    (Some(directory :> IUserDirectory))
                    (Some(invites :> IPendingInviteStore))

            let hook = UserMembershipTeardownLifecycle.create sp
            let! result = hook.OnDeprovisioned("user-victim", "admin-1")

            Expect.equal result LifecycleHookResult.Completed "hook completes"

            let! victimTeams = ts.GetTeamsForUser "victim"
            Expect.isEmpty victimTeams "membership rows stripped"

            let removedAudits =
                audit.Events
                |> List.choose (fun (scope, e) ->
                    match e with
                    | MemberRemoved p -> Some(scope, p.TeamId, p.AffectedUserId, p.UserId)
                    | _ -> None)
                |> Set.ofList

            Expect.equal
                removedAudits
                (Set.ofList [ team1, team1, "victim", "admin-1"; team2, team2, "victim", "admin-1" ])
                "one MemberRemoved per stripped team, actor-attributed, recorded against the team scope"

            Expect.isFalse (invites.Contains "victim@example.com") "pending invite swept via the directory email"
        }

        testCaseAsync "hook completes without a sweep when no directory resolves the email"
        <| async {
            let ts = freshTeamStore ()
            let! _ = seedTeam ts "No-Dir" [ "owner1", Owner; "victim", Member ]
            let invites = EmailSetInviteStore [ "victim@example.com" ]

            let sp =
                providerWith (Some(ts :> ITeamStore)) None None (Some(invites :> IPendingInviteStore))

            let hook = UserMembershipTeardownLifecycle.create sp
            let! result = hook.OnDeprovisioned("user-victim", "admin-1")

            Expect.equal result LifecycleHookResult.Completed "no directory → sweep not applicable, purge stands"
            Expect.isTrue (invites.Contains "victim@example.com") "invite untouched — no email was resolvable"
        }

        // ── End-to-end: DeprovisionTenant("user-<id>") ───────────────

        testCaseAsync "DeprovisionTenant('user-<id>') leaves no membership row, no pointer, no matching invite"
        <| async {
            let ts = freshTeamStore ()
            let! team1 = seedTeam ts "E2E-One" [ "owner1", Owner; "victim", Member ]
            let! _ = seedTeam ts "E2E-Two" [ "owner2", Owner; "victim", Admin ]
            let! _ = ts.SetActiveTeam("victim", team1)

            let directory = MapUserDirectory(Map [ "victim", "victim@example.com" ])
            let invites = EmailSetInviteStore [ "victim@example.com" ]

            let services = ServiceCollection()

            services.AddSingleton<AccessContext>(
                {
                    AccessContext.unrestricted (AuthenticatedUser "admin-1") with
                        PlatformRole = Some PlatformRole.PlatformAdmin
                }
            )
            |> ignore

            services.AddSingleton<ServerConfig>(
                {
                    ServerConfig.defaults with
                        TenantLifecycle = EnabledTenantLifecycle
                }
            )
            |> ignore

            services.AddSingleton<ITeamStore>(ts :> ITeamStore) |> ignore
            services.AddSingleton<IUserDirectory>(directory :> IUserDirectory) |> ignore

            services.AddSingleton<IPendingInviteStore>(invites :> IPendingInviteStore)
            |> ignore

            services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) ->
                UserMembershipTeardownLifecycle.create sp)
            |> ignore

            let sp = services.BuildServiceProvider() :> IServiceProvider
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.RequestServices <- sp
            let api = PlatformTenantApiHandler.platformTenantApi ctx

            let! result = api.DeprovisionTenant("user-victim", "admin-1", "offboard requested")

            match result with
            | Error e -> failtestf "expected the offboard to run, got Error %s" e
            | Ok summary ->
                let outcome =
                    summary.Outcomes |> List.find (fun o -> o.HookName = "user-membership-teardown")

                Expect.equal outcome.Result LifecycleHookResult.Completed "the teardown hook completed"

            let! victimTeams = ts.GetTeamsForUser "victim"
            Expect.isEmpty victimTeams "no membership row survives"

            let! active = ts.GetActiveTeam "victim"
            Expect.isNone active "no active-team pointer survives"

            Expect.isFalse (invites.Contains "victim@example.com") "no matching pending invite survives"

            // A fresh sign-in resolves team-less: the store now reports no
            // teams and no role anywhere for the purged id.
            let! role = ts.GetMemberRole(team1, "victim")
            Expect.isNone role "signing in again yields no team access"
        }
    ]
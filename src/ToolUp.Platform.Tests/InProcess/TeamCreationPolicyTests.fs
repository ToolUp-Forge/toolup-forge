module ToolUp.Platform.Tests.InProcess.TeamCreationPolicyTests

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

// Deliberately NOT opening `ToolUp.Platform.ConfigValidation` here —
// its `ValidationResult` cases (`Ok | Warning | Error`) would shadow
// the standard `Result<_,_>` constructors used throughout these tests.
// `IConfigValidator` is referenced through `ConfigValidation.IConfigValidator`
// in the one place that needs it.

// ─── Phase 5f — TeamCreationPolicy + BootstrapTeam contract tests ────
//
// Covers four surfaces in one test pack:
//   1. `teamApiHandler.CreateTeam` gate behaviour under each policy /
//      caller-role combination.
//   2. `BootstrapTeam.bootstrap` idempotency + audit emission +
//      env-var handling.
//   3. `TeamCreationPolicyValidator` — wedge detection at preflight.
//   4. `BootstrapTeamValidator` — env-pair coherence at preflight.
//
// Handler tests use `teamApi config ctx : TeamApi` (the inner-record
// builder exposed in `PlatformApiHandler.fs`) so the suite doesn't go
// through Fable.Remoting's HTTP machinery — matches the
// `PlatformAdminApiHandler.platformAdminApi` testing pattern.

// ─── Fakes ───────────────────────────────────────────────────────────

/// Audit capture — same shape as `TransactionalDispatcherTests.CapturingAuditLog`.
/// `Record` enqueues immediately; the production handler calls
/// `Async.Start` on the recorder so tests poll briefly before
/// asserting (the bootstrap path awaits records directly and needs
/// no sleep).
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

/// Silent `ILogger` for bootstrap tests (a real `ConsoleLogger` would
/// flood the runner).
type private SilentLogger() =
    interface ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(_) = ()
        member _.Error(_, _) = ()

// ─── Handler test fixtures ───────────────────────────────────────────

/// Build a `DefaultHttpContext` with `ITeamStore` + `IPlatformAdminStore`
/// + `IAuditLog` registered and `ToolUp.UserId` set in `Items`.
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

let private teamConfig (policy: TeamCreationPolicy) = {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
        TeamCreationPolicy = policy
}

// ─── Env-var hygiene ─────────────────────────────────────────────────

/// Snapshot the three env vars Phase 5f reads and restore them after
/// the test runs. Tests that need a specific value set / unset call
/// `Environment.SetEnvironmentVariable` after `snapshotEnv` and the
/// restore happens via the returned disposable's `Dispose`.
type private EnvSnapshot() =
    let prior =
        [
            BootstrapTeam.initialTeamNameEnvVar
            BootstrapTeam.initialTeamIdEnvVar
            BootstrapTeam.initialAdminEnvVar
        ]
        |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)

    interface IDisposable with
        member _.Dispose() =
            prior
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

let private withEnv
    (teamName: string option)
    (teamId: string option)
    (adminUserId: string option)
    (action: unit -> Async<unit>)
    : Async<unit> =
    async {
        use _snapshot = new EnvSnapshot()
        Environment.SetEnvironmentVariable(BootstrapTeam.initialTeamNameEnvVar, Option.toObj teamName)
        Environment.SetEnvironmentVariable(BootstrapTeam.initialTeamIdEnvVar, Option.toObj teamId)
        Environment.SetEnvironmentVariable(BootstrapTeam.initialAdminEnvVar, Option.toObj adminUserId)
        do! action ()
    }

// ─── Tests ───────────────────────────────────────────────────────────

// Env-var reads cross process-global state; running these cases in
// parallel would race the `TOOLUP_INITIAL_TEAM_*` / admin env vars and
// produce intermittent failures (one test's bootstrap sees another
// test's env value). `testSequenced` serialises the whole list to
// match the posture of `OidcAudienceBindingValidatorTests`, which has
// the same constraint.
[<Tests>]
let tests =
    testSequenced
    <| testList "Phase 5f — TeamCreationPolicy + BootstrapTeam" [

        // ── Handler gate ────────────────────────────────────────────

        testCaseAsync "Admin caller under PlatformAdminOnly: CreateTeam succeeds"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            // Seed the caller as a Platform Admin.
            let! _ = adminStore.AssignPlatformAdmin("_bootstrap", "alice")
            let ctx = ctxFor "alice" teamStore adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi (teamConfig PlatformAdminOnly) ctx

            let! result = api.CreateTeam "Engineering"

            match result with
            | Ok team -> Expect.equal team.Name "Engineering" "team is created with the requested name"
            | Error msg -> failtestf "expected Ok, got Error %s" msg
        }

        testCaseAsync "Non-admin caller under PlatformAdminOnly: CreateTeam denied"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            // bob is NOT in the admin store.
            let ctx = ctxFor "bob" teamStore adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi (teamConfig PlatformAdminOnly) ctx

            let! result = api.CreateTeam "Eng"

            match result with
            | Error msg -> Expect.equal msg "Team creation requires Platform Admin" "specific deny message"
            | Ok _ -> failtest "expected Error from non-admin caller"

            // Confirm no team was minted (the gate fires before any
            // store mutation).
            let! teams = teamStore.ListTeams()
            Expect.isEmpty teams "no team created on the deny path"
        }

        testCaseAsync "Non-admin caller under AnyAuthenticatedUser: CreateTeam succeeds (pre-5f shape)"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let ctx = ctxFor "bob" teamStore adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi (teamConfig AnyAuthenticatedUser) ctx

            let! result = api.CreateTeam "Open team"

            match result with
            | Ok team -> Expect.equal team.Name "Open team" "open-membership path still allows non-admin"
            | Error msg -> failtestf "expected Ok under AnyAuthenticatedUser, got Error %s" msg
        }

        testCaseAsync "Non-admin deny emits TeamCreationDenied audit"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let ctx = ctxFor "carol" teamStore adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi (teamConfig PlatformAdminOnly) ctx

            let! _ = api.CreateTeam "Marketing"

            // Handler emits audit via `Async.Start`; poll briefly.
            let! ev = auditLog.WaitForEventOfKind("TeamCreationDenied", 1000)

            match ev with
            | Some(AuditEvent.TeamCreationDenied payload) ->
                Expect.equal payload.UserId "carol" "audit records the caller"
                Expect.equal payload.AttemptedName "Marketing" "audit records the attempted team name verbatim"
            | Some other -> failtestf "expected TeamCreationDenied, got %A" other
            | None -> failtest "TeamCreationDenied audit not emitted within 1000ms"
        }

        testCaseAsync "GetTeamCreationPolicy returns the configured value"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let ctx = ctxFor "alice" teamStore adminStore (auditLog :> IAuditLog)

            let api = PlatformApiHandler.teamApi (teamConfig AnyAuthenticatedUser) ctx
            let! policy = api.GetTeamCreationPolicy()
            Expect.equal policy AnyAuthenticatedUser "endpoint reflects ServerConfig.TeamCreationPolicy"

            let api2 = PlatformApiHandler.teamApi (teamConfig PlatformAdminOnly) ctx
            let! policy2 = api2.GetTeamCreationPolicy()
            Expect.equal policy2 PlatformAdminOnly "endpoint reflects the (default) PlatformAdminOnly value"
        }

        // ── BootstrapTeam ───────────────────────────────────────────

        testCaseAsync "BootstrapTeam: env unset → no-op"
        <| async {
            do!
                withEnv None None None (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let teamStore = freshTeamStore () :> ITeamStore

                    do! BootstrapTeam.bootstrap (SilentLogger()) auditLog teamStore

                    let! teams = teamStore.ListTeams()
                    Expect.isEmpty teams "no team created when env var is unset"

                    let capture = (auditLog :?> CapturingAuditLog).Recorded
                    Expect.isEmpty capture "no audit emitted on no-op"
                })
        }

        testCaseAsync "BootstrapTeam: env set + empty store → creates team + adds Owner + sets active"
        <| async {
            do!
                withEnv (Some "Bootstrap Co") (Some "boot0001") (Some "admin-1") (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let teamStore = freshTeamStore () :> ITeamStore

                    do! BootstrapTeam.bootstrap (SilentLogger()) auditLog teamStore

                    let! teams = teamStore.ListTeams()
                    Expect.hasLength teams 1 "exactly one team created"
                    let team = List.head teams
                    Expect.equal team.TeamId "boot0001" "team id honours TOOLUP_INITIAL_TEAM_ID"
                    Expect.equal team.Name "Bootstrap Co" "team name honours TOOLUP_INITIAL_TEAM_NAME"

                    let! adminRole = teamStore.GetMemberRole(team.TeamId, "admin-1")
                    Expect.equal adminRole (Some Owner) "bootstrap admin lands as Owner"

                    let! active = teamStore.GetActiveTeam("admin-1")
                    Expect.equal active (Some "boot0001") "bootstrap sets the admin's active team"
                })
        }

        testCaseAsync "BootstrapTeam: emits TeamCreated + MemberAdded audits under _bootstrap actor"
        <| async {
            do!
                withEnv (Some "Acme") (Some "acme0001") (Some "admin-2") (fun () -> async {
                    let auditLog = CapturingAuditLog()
                    let teamStore = freshTeamStore () :> ITeamStore

                    do! BootstrapTeam.bootstrap (SilentLogger()) (auditLog :> IAuditLog) teamStore

                    let recorded = auditLog.Recorded
                    let kinds = recorded |> List.map (fun (_, ev) -> AuditEvent.eventTypeName ev)
                    Expect.contains kinds "TeamCreated" "TeamCreated audit emitted"
                    Expect.contains kinds "MemberAdded" "MemberAdded audit emitted"

                    let teamCreatedActor =
                        recorded
                        |> List.tryPick (fun (_, ev) ->
                            match ev with
                            | TeamCreated p -> Some p.UserId
                            | _ -> None)

                    Expect.equal teamCreatedActor (Some "_bootstrap") "TeamCreated.Actor = '_bootstrap'"

                    let memberAddedActor =
                        recorded
                        |> List.tryPick (fun (_, ev) ->
                            match ev with
                            | MemberAdded p -> Some(p.UserId, p.AffectedUserId, p.Role)
                            | _ -> None)

                    Expect.equal
                        memberAddedActor
                        (Some("_bootstrap", "admin-2", "Owner"))
                        "MemberAdded records actor=_bootstrap, affected=admin-2, role=Owner"
                })
        }

        testCaseAsync "BootstrapTeam: idempotent — second invocation no-ops"
        <| async {
            do!
                withEnv (Some "Once Only") None (Some "admin-3") (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let teamStore = freshTeamStore () :> ITeamStore

                    do! BootstrapTeam.bootstrap (SilentLogger()) auditLog teamStore
                    let! firstRun = teamStore.ListTeams()
                    Expect.hasLength firstRun 1 "first invocation creates one team"

                    let auditCountAfterFirst = (auditLog :?> CapturingAuditLog).Recorded.Length

                    // Second invocation with the env var STILL SET —
                    // bootstrap must no-op because the store is no
                    // longer empty.
                    do! BootstrapTeam.bootstrap (SilentLogger()) auditLog teamStore

                    let! secondRun = teamStore.ListTeams()
                    Expect.hasLength secondRun 1 "second invocation is a no-op (store still has exactly one team)"

                    let auditCountAfterSecond = (auditLog :?> CapturingAuditLog).Recorded.Length

                    Expect.equal auditCountAfterSecond auditCountAfterFirst "no audit emitted on second invocation"
                })
        }

        testCaseAsync "BootstrapTeam: non-empty store → no-op (even with env var set)"
        <| async {
            do!
                withEnv (Some "Should Not Create") None (Some "admin-4") (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let teamStore = freshTeamStore ()
                    // Pre-populate the store with an unrelated team.
                    let! _ = teamStore.CreateTeam("preexisting", "Pre-existing")

                    do! BootstrapTeam.bootstrap (SilentLogger()) auditLog (teamStore :> ITeamStore)

                    let! teams = (teamStore :> ITeamStore).ListTeams()
                    Expect.hasLength teams 1 "still exactly one team (the pre-existing one)"
                    Expect.equal (List.head teams).TeamId "preexisting" "the existing team is untouched"
                })
        }

        testCaseAsync "BootstrapTeam: team name set but admin env var unset → no-op (loud-log failsafe)"
        <| async {
            do!
                withEnv (Some "Half Configured") None None (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let teamStore = freshTeamStore () :> ITeamStore

                    do! BootstrapTeam.bootstrap (SilentLogger()) auditLog teamStore

                    let! teams = teamStore.ListTeams()
                    Expect.isEmpty teams "no team created when admin env var is missing"
                })
        }

        // ── TeamCreationPolicyValidator ─────────────────────────────

        testCaseAsync "TeamCreationPolicyValidator: Team mode + PlatformAdminOnly + empty admins + no env → Error"
        <| async {
            do!
                withEnv None None None (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let adminStore = freshAdminStore auditLog
                    let config = teamConfig PlatformAdminOnly

                    let v =
                        TeamCreationPolicyValidator.TeamCreationPolicyValidator(config, adminStore)
                        :> ConfigValidation.IConfigValidator

                    let! result = v.Validate()

                    match result with
                    | ConfigValidation.Error msg ->
                        Expect.stringContains msg "PlatformAdminOnly" "message names the policy"

                        Expect.stringContains
                            msg
                            BootstrapTeam.initialAdminEnvVar
                            "message names the bootstrap admin env var"
                    | other -> failtestf "expected Error, got %A" other
                })
        }

        testCaseAsync "TeamCreationPolicyValidator: env var set → Ok (bootstrap will seed)"
        <| async {
            do!
                withEnv None None (Some "admin-5") (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let adminStore = freshAdminStore auditLog

                    let v =
                        TeamCreationPolicyValidator.TeamCreationPolicyValidator(
                            teamConfig PlatformAdminOnly,
                            adminStore
                        )
                        :> ConfigValidation.IConfigValidator

                    let! result = v.Validate()
                    Expect.equal result ConfigValidation.Ok "env var present unblocks startup"
                })
        }

        testCaseAsync "TeamCreationPolicyValidator: admin already exists → Ok"
        <| async {
            do!
                withEnv None None None (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let adminStore = freshAdminStore auditLog
                    let! _ = adminStore.AssignPlatformAdmin("_bootstrap", "preseeded-admin")

                    let v =
                        TeamCreationPolicyValidator.TeamCreationPolicyValidator(
                            teamConfig PlatformAdminOnly,
                            adminStore
                        )
                        :> ConfigValidation.IConfigValidator

                    let! result = v.Validate()
                    Expect.equal result ConfigValidation.Ok "non-empty admin list unblocks startup"
                })
        }

        testCaseAsync "TeamCreationPolicyValidator: AnyAuthenticatedUser policy → Ok (gate is off)"
        <| async {
            do!
                withEnv None None None (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let adminStore = freshAdminStore auditLog

                    let v =
                        TeamCreationPolicyValidator.TeamCreationPolicyValidator(
                            teamConfig AnyAuthenticatedUser,
                            adminStore
                        )
                        :> ConfigValidation.IConfigValidator

                    let! result = v.Validate()
                    Expect.equal result ConfigValidation.Ok "open-membership policy bypasses the wedge check"
                })
        }

        testCaseAsync "TeamCreationPolicyValidator: Anonymous mode → Ok (no ITeamStore registered)"
        <| async {
            do!
                withEnv None None None (fun () -> async {
                    let auditLog = CapturingAuditLog() :> IAuditLog
                    let adminStore = freshAdminStore auditLog

                    let config = {
                        ServerConfig.defaults with
                            Surfaces = Surfaces.anonymous
                            TeamCreationPolicy = PlatformAdminOnly
                    }

                    let v =
                        TeamCreationPolicyValidator.TeamCreationPolicyValidator(config, adminStore)
                        :> ConfigValidation.IConfigValidator

                    let! result = v.Validate()
                    Expect.equal result ConfigValidation.Ok "non-team-scoped mode bypasses the gate"
                })
        }

        // ── BootstrapTeamValidator ──────────────────────────────────

        testCaseAsync "BootstrapTeamValidator: team-name set + admin unset → Error"
        <| async {
            do!
                withEnv (Some "Half Boot") None None (fun () -> async {
                    let v =
                        BootstrapTeamValidator.BootstrapTeamValidator() :> ConfigValidation.IConfigValidator

                    let! result = v.Validate()

                    match result with
                    | ConfigValidation.Error msg ->
                        Expect.stringContains
                            msg
                            BootstrapTeam.initialTeamNameEnvVar
                            "message names the team-name env var"

                        Expect.stringContains msg BootstrapTeam.initialAdminEnvVar "message names the admin env var"
                    | other -> failtestf "expected Error, got %A" other
                })
        }

        testCaseAsync "BootstrapTeamValidator: both env vars set → Ok"
        <| async {
            do!
                withEnv (Some "Full Boot") None (Some "admin-7") (fun () -> async {
                    let v =
                        BootstrapTeamValidator.BootstrapTeamValidator() :> ConfigValidation.IConfigValidator

                    let! result = v.Validate()
                    Expect.equal result ConfigValidation.Ok "both env vars present passes the check"
                })
        }

        testCaseAsync "BootstrapTeamValidator: team-name unset → Ok (regardless of admin env)"
        <| async {
            do!
                withEnv None None (Some "admin-8") (fun () -> async {
                    let v =
                        BootstrapTeamValidator.BootstrapTeamValidator() :> ConfigValidation.IConfigValidator

                    let! result = v.Validate()

                    Expect.equal
                        result
                        ConfigValidation.Ok
                        "no team-name = bootstrap not requested = nothing to validate"
                })
        }

        // ── Validator metadata smoke ────────────────────────────────

        test "Validator metadata is well-formed" {
            let auditLog = CapturingAuditLog() :> IAuditLog
            let adminStore = freshAdminStore auditLog

            let v1 =
                TeamCreationPolicyValidator.TeamCreationPolicyValidator(teamConfig PlatformAdminOnly, adminStore)
                :> ConfigValidation.IConfigValidator

            Expect.equal v1.Name "team-creation-policy-wedge" "stable identifier (policy validator)"
            Expect.isGreaterThan v1.Timeout.TotalMilliseconds 0.0 "non-zero timeout (policy validator)"

            let v2 =
                BootstrapTeamValidator.BootstrapTeamValidator() :> ConfigValidation.IConfigValidator

            Expect.equal v2.Name "bootstrap-team-env-pair" "stable identifier (bootstrap validator)"
            Expect.isGreaterThan v2.Timeout.TotalMilliseconds 0.0 "non-zero timeout (bootstrap validator)"
        }
    ]

// ─── Phase 225 — team-create integrity ───────────────────────────────
//
// Collision guard (CreateTeam fails closed on an existing id), atomic
// rollback (a failed owner-membership write leaves no orphan team), and
// server-side name validation.

/// `ITeamStore` decorator whose `AddMember` always fails — drives the
/// create-path rollback (`DeleteTeam`) when the owner write fails. Every
/// other method delegates to a real store.
type private AddMemberFailingStore(inner: ITeamStore) =
    interface ITeamStore with
        member _.AddMember(_, _, _) = async { return Error "forced add-member failure" }
        member _.CreateTeam(teamId, name) = inner.CreateTeam(teamId, name)
        member _.DeleteTeam(teamId) = inner.DeleteTeam(teamId)
        member _.GetTeam(teamId) = inner.GetTeam(teamId)
        member _.ListTeams() = inner.ListTeams()
        member _.RemoveMember(teamId, userId) = inner.RemoveMember(teamId, userId)

        member _.ChangeMemberRole(teamId, userId, role) =
            inner.ChangeMemberRole(teamId, userId, role)

        member _.GetTeamsForUser(userId) = inner.GetTeamsForUser(userId)
        member _.GetTeamMembers(teamId) = inner.GetTeamMembers(teamId)
        member _.GetMemberRole(teamId, userId) = inner.GetMemberRole(teamId, userId)
        member _.GetActiveTeam(userId) = inner.GetActiveTeam(userId)
        member _.SetActiveTeam(userId, teamId) = inner.SetActiveTeam(userId, teamId)

[<Tests>]
let integrityTests =
    testList "Phase 225 — team-create integrity" [

        testCaseAsync "CreateTeam fails closed on an existing team id (no overwrite)"
        <| async {
            let store = freshTeamStore ()

            let! first = store.CreateTeam("dup-id", "Original")
            Expect.isOk first "first create with this id succeeds"

            let! second = store.CreateTeam("dup-id", "Impostor")

            match second with
            | Error msg -> Expect.stringContains msg "already exists" "duplicate id is rejected"
            | Ok _ -> failtest "expected Error on a colliding team id"

            // The original team's metadata must be untouched — a silent
            // overwrite would co-tenant two teams onto one partition.
            let! fetched = (store :> ITeamStore).GetTeam("dup-id")
            Expect.equal (fetched |> Option.map _.Name) (Some "Original") "original team metadata is preserved"
        }

        testCaseAsync "DeleteTeam removes the team record"
        <| async {
            let store = freshTeamStore ()
            let! _ = store.CreateTeam("del-id", "Temp")

            let! deleted = store.DeleteTeam("del-id")
            Expect.isOk deleted "delete succeeds"

            let! fetched = store.GetTeam("del-id")
            Expect.isNone fetched "team blob is gone after delete"
        }

        testCaseAsync "Owner-write failure rolls back the team (no orphan, no TeamCreated audit)"
        <| async {
            let auditLog = CapturingAuditLog()
            let realStore = freshTeamStore () :> ITeamStore
            let failing = AddMemberFailingStore(realStore) :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let! _ = adminStore.AssignPlatformAdmin("_bootstrap", "alice")
            let ctx = ctxFor "alice" failing adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi (teamConfig PlatformAdminOnly) ctx

            let! result = api.CreateTeam "Doomed"

            match result with
            | Error msg -> Expect.stringContains msg "forced add-member failure" "the owner-write error surfaces"
            | Ok _ -> failtest "expected Error when the owner-membership write fails"

            // The just-created team blob must have been rolled back.
            let! teams = realStore.ListTeams()
            Expect.isEmpty teams "no orphan team left behind after rollback"

            // TeamCreated is only emitted once the team is whole — it must
            // never fire for a rolled-back create.
            let kinds =
                auditLog.Recorded |> List.map (fun (_, ev) -> AuditEvent.eventTypeName ev)

            Expect.isFalse (List.contains "TeamCreated" kinds) "no TeamCreated audit on a rolled-back create"
        }

        testCaseAsync "CreateTeam rejects a blank name (server-side)"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let ctx = ctxFor "bob" teamStore adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi (teamConfig AnyAuthenticatedUser) ctx

            let! result = api.CreateTeam "   "

            match result with
            | Error msg -> Expect.equal msg "Team name can't be empty" "blank name is rejected"
            | Ok _ -> failtest "expected Error for a blank team name"

            let! teams = teamStore.ListTeams()
            Expect.isEmpty teams "no team created for a blank name"
        }

        testCaseAsync "CreateTeam rejects an over-length name (server-side)"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let ctx = ctxFor "bob" teamStore adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi (teamConfig AnyAuthenticatedUser) ctx

            let! result = api.CreateTeam(String('x', 101))

            match result with
            | Error msg -> Expect.stringContains msg "too long" "over-length name is rejected"
            | Ok _ -> failtest "expected Error for an over-length team name"
        }
    ]

// ─── Phase 228 — team-creation quota ─────────────────────────────────
//
// Opt-in per-user cap under AnyAuthenticatedUser. Default (None) is
// unlimited; Platform Admins are exempt; inert under PlatformAdminOnly.

[<Tests>]
let quotaTests =
    testList "Phase 228 — team-creation quota" [

        testCaseAsync "Quota unset → unlimited team creation (unchanged)"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let ctx = ctxFor "bob" teamStore adminStore (auditLog :> IAuditLog)
            let api = PlatformApiHandler.teamApi (teamConfig AnyAuthenticatedUser) ctx

            let! r1 = api.CreateTeam "T1"
            let! r2 = api.CreateTeam "T2"
            let! r3 = api.CreateTeam "T3"
            Expect.isOk r1 "first create ok"
            Expect.isOk r2 "second create ok"
            Expect.isOk r3 "third create ok — no quota configured"
        }

        testCaseAsync "Quota set → rejected once the limit is reached"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let ctx = ctxFor "bob" teamStore adminStore (auditLog :> IAuditLog)

            let config = {
                teamConfig AnyAuthenticatedUser with
                    TeamCreationQuota = Some 2
            }

            let api = PlatformApiHandler.teamApi config ctx

            let! r1 = api.CreateTeam "T1"
            let! r2 = api.CreateTeam "T2"
            let! r3 = api.CreateTeam "T3"
            Expect.isOk r1 "first under the limit"
            Expect.isOk r2 "second reaches the limit exactly"

            match r3 with
            | Error msg -> Expect.stringContains msg "limit reached" "third is rejected at quota"
            | Ok _ -> failtest "expected a quota rejection on the third create"

            let! teams = teamStore.GetTeamsForUser "bob"
            Expect.hasLength teams 2 "no third team persisted"
        }

        testCaseAsync "Platform Admin is exempt from the quota"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let! _ = adminStore.AssignPlatformAdmin("_bootstrap", "alice")
            let ctx = ctxFor "alice" teamStore adminStore (auditLog :> IAuditLog)

            let config = {
                teamConfig AnyAuthenticatedUser with
                    TeamCreationQuota = Some 1
            }

            let api = PlatformApiHandler.teamApi config ctx

            let! r1 = api.CreateTeam "A1"
            let! r2 = api.CreateTeam "A2"
            Expect.isOk r1 "admin first create ok"
            Expect.isOk r2 "admin second create ok despite a quota of 1"
        }

        testCaseAsync "Quota is inert under PlatformAdminOnly"
        <| async {
            let auditLog = CapturingAuditLog()
            let teamStore = freshTeamStore () :> ITeamStore
            let adminStore = freshAdminStore (auditLog :> IAuditLog)
            let! _ = adminStore.AssignPlatformAdmin("_bootstrap", "alice")
            let ctx = ctxFor "alice" teamStore adminStore (auditLog :> IAuditLog)

            let config = {
                teamConfig PlatformAdminOnly with
                    TeamCreationQuota = Some 1
            }

            let api = PlatformApiHandler.teamApi config ctx

            let! r1 = api.CreateTeam "P1"
            let! r2 = api.CreateTeam "P2"
            Expect.isOk r1 "admin first create ok"
            Expect.isOk r2 "quota ignored under PlatformAdminOnly"
        }
    ]
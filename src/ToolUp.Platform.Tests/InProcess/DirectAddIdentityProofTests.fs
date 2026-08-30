module ToolUp.Platform.Tests.InProcess.DirectAddIdentityProofTests

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// Deliberately NOT opening `ToolUp.Platform.ConfigValidation` at module
// scope — its `ValidationResult` cases (`Ok | Warning | Error`) shadow the
// standard `Result` constructors the handler tests match on. The validator
// arm references them through `ConfigValidation.*`, matching the posture of
// `TeamCreationPolicyTests` next door.

// ─── Phase 549 — directory existence proof for direct member adds ────
//
// Phase 131 documented that membership rows are admin-asserted: any
// syntactically-valid id becomes a permanent row. This pack pins the
// opt-in gate that closes it, on both halves:
//
//   * the handler paths (`AddTeamMember`, `CreateTeamWithOwner`) —
//     a known id passes, an unknown id is refused with the id named and
//     NO row written, the default mode is byte-for-byte unchanged, and a
//     substrate failure fails closed rather than admitting the add;
//   * the preflight validator — `RequireDirectoryProof` with no
//     `IUserDirectory` composed refuses startup.

// ─── Fakes ───────────────────────────────────────────────────────────

type private SilentAuditLog() =
    interface IAuditLog with
        member _.Record(_, _) = async { return () }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Directory over a fixed roster. `ResolveUsers` honours the substrate
/// contract: ids it does not recognise are omitted, never raised.
type private RosterDirectory(known: string list) =
    interface IUserDirectory with
        member _.SearchUsers(_, _) = async { return Result.Ok [] }

        member _.ResolveUsers ids = async {
            return
                ids
                |> List.filter (fun id -> known |> List.contains id)
                |> List.map (fun id -> {
                    UserId = id
                    DisplayName = Some id
                    Email = Some(id + "@example.test")
                })
                |> Result.Ok
        }

        member _.NotifyInvitation _ = async { return Result.Ok() }

/// Directory whose provider is down — the fail-closed arm. A proof gate
/// that admits an id because the lookup errored has proved nothing.
type private UnavailableDirectory() =
    interface IUserDirectory with
        member _.SearchUsers(_, _) = async { return Result.Error "directory unavailable" }
        member _.ResolveUsers _ = async { return Result.Error "directory unavailable" }
        member _.NotifyInvitation _ = async { return Result.Ok() }

// ─── Fixtures ────────────────────────────────────────────────────────

let private freshTeamStore () =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    TeamStore(storage, notifications) :> ITeamStore

let private freshAdminStore (auditLog: IAuditLog) =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    PlatformAdminStore.BlobBackedPlatformAdminStore(storage, auditLog) :> IPlatformAdminStore

let private ctxFor
    (userId: string)
    (teamStore: ITeamStore)
    (platformAdminStore: IPlatformAdminStore)
    (auditLog: IAuditLog)
    (directory: IUserDirectory option)
    : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<ITeamStore>(teamStore) |> ignore
    services.AddSingleton<IPlatformAdminStore>(platformAdminStore) |> ignore
    services.AddSingleton<IAuditLog>(auditLog) |> ignore

    match directory with
    | Some d -> services.AddSingleton<IUserDirectory>(d) |> ignore
    | None -> ()

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx

let private configWith (proof: DirectAddIdentityProof) = {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
        TeamCreationPolicy = AnyAuthenticatedUser
        DirectAddIdentityProof = proof
}

/// A team owned by `owner`, plus the API record the caller sees.
let private seededTeam (proof: DirectAddIdentityProof) (owner: string) (directory: IUserDirectory option) = async {
    let auditLog = SilentAuditLog() :> IAuditLog
    let teamStore = freshTeamStore ()
    let adminStore = freshAdminStore auditLog
    let! created = teamStore.CreateTeam("t1", "Engineering")
    Expect.isOk created "fixture team is created"
    let! added = teamStore.AddMember("t1", owner, Owner)
    Expect.isOk added "fixture owner is seeded"

    let ctx = ctxFor owner teamStore adminStore auditLog directory
    return teamStore, PlatformApiHandler.teamApi (configWith proof) ctx
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 549 — direct-add directory existence proof" [

        // ── Default mode: byte-for-byte unchanged (GP 11) ────────────

        testCaseAsync "Default NoIdentityProof: an id no directory knows is still added (pre-549 behaviour)"
        <| async {
            // The ghost-member behaviour Phase 131 documented. Asserted
            // deliberately: it is the default, and a change to it would be
            // a silent break for every deployment that has not opted in.
            let! store, api = seededTeam NoIdentityProof "alice" (Some(RosterDirectory [ "alice" ]))

            let! result = api.AddTeamMember("t1", "typo-bob", Member)

            Expect.isOk result "the default mode writes the row without consulting the directory"

            let! role = store.GetMemberRole("t1", "typo-bob")
            Expect.equal role (Some Member) "the unverified membership row exists"
        }

        testCaseAsync "Default NoIdentityProof: no directory composed at all is unaffected"
        <| async {
            let! store, api = seededTeam NoIdentityProof "alice" None

            let! result = api.AddTeamMember("t1", "bob", Member)

            Expect.isOk result "a deployment with no IUserDirectory pays nothing (GP 13)"

            let! role = store.GetMemberRole("t1", "bob")
            Expect.equal role (Some Member) "row written"
        }

        // ── AddTeamMember under RequireDirectoryProof ────────────────

        testCaseAsync "RequireDirectoryProof: a known id is added"
        <| async {
            let! store, api = seededTeam RequireDirectoryProof "alice" (Some(RosterDirectory [ "alice"; "bob" ]))

            let! result = api.AddTeamMember("t1", "bob", Member)

            Expect.isOk result "an id the directory resolves passes the proof"

            let! role = store.GetMemberRole("t1", "bob")
            Expect.equal role (Some Member) "the membership row is written"
        }

        testCaseAsync "RequireDirectoryProof: an unknown id is refused, naming the id, with no row written"
        <| async {
            let! store, api = seededTeam RequireDirectoryProof "alice" (Some(RosterDirectory [ "alice"; "bob" ]))

            let! result = api.AddTeamMember("t1", "typo-bob", Member)

            match result with
            | Error msg ->
                Expect.stringContains msg "typo-bob" "the refusal names the offending id"
                Expect.stringContains msg "RequireDirectoryProof" "the refusal names the proof requirement"
            | Ok() -> failtest "expected the unknown id to be refused"

            let! role = store.GetMemberRole("t1", "typo-bob")
            Expect.isNone role "no ghost row was minted"
        }

        testCaseAsync "RequireDirectoryProof: a directory failure fails CLOSED"
        <| async {
            let! store, api = seededTeam RequireDirectoryProof "alice" (Some(UnavailableDirectory()))

            let! result = api.AddTeamMember("t1", "bob", Member)

            match result with
            | Error msg ->
                Expect.stringContains msg "bob" "the refusal names the id it could not prove"
                Expect.stringContains msg "directory unavailable" "the substrate failure is surfaced verbatim"
            | Ok() -> failtest "a substrate failure must not admit an unproven id"

            let! role = store.GetMemberRole("t1", "bob")
            Expect.isNone role "no row written when the proof could not be obtained"
        }

        testCaseAsync "RequireDirectoryProof with NO directory composed: refused at request time too"
        <| async {
            // Preflight makes this unreachable in a composed deployment;
            // a hand-built pipeline (or SkipPreflight) must not silently
            // downgrade the proof requirement to nothing.
            let! store, api = seededTeam RequireDirectoryProof "alice" None

            let! result = api.AddTeamMember("t1", "bob", Member)

            match result with
            | Error msg -> Expect.stringContains msg "IUserDirectory" "the refusal names the missing companion"
            | Ok() -> failtest "expected a fail-closed refusal with no directory composed"

            let! role = store.GetMemberRole("t1", "bob")
            Expect.isNone role "no row written"
        }

        // ── CreateTeamWithOwner + the caller-self exemption ──────────

        testCaseAsync "RequireDirectoryProof: CreateTeamWithOwner refuses an unknown owner id"
        <| async {
            let auditLog = SilentAuditLog() :> IAuditLog
            let teamStore = freshTeamStore ()
            let adminStore = freshAdminStore auditLog
            let directory = RosterDirectory [ "alice" ] :> IUserDirectory
            let ctx = ctxFor "alice" teamStore adminStore auditLog (Some directory)
            let api = PlatformApiHandler.teamApi (configWith RequireDirectoryProof) ctx

            let! result =
                api.CreateTeamWithOwner {
                    Name = "Ghost Town"
                    InitialOwnerUserId = "nobody"
                }

            match result with
            | Error msg -> Expect.stringContains msg "nobody" "the refusal names the unresolvable owner"
            | Ok _ -> failtest "expected an unknown owner id to be refused"

            let! teams = teamStore.ListTeams()
            Expect.isEmpty teams "the refusal lands before any team blob is minted"
        }

        testCaseAsync "RequireDirectoryProof: CreateTeamWithOwner accepts a known owner id"
        <| async {
            let auditLog = SilentAuditLog() :> IAuditLog
            let teamStore = freshTeamStore ()
            let adminStore = freshAdminStore auditLog
            let directory = RosterDirectory [ "alice"; "bob" ] :> IUserDirectory
            let ctx = ctxFor "alice" teamStore adminStore auditLog (Some directory)
            let api = PlatformApiHandler.teamApi (configWith RequireDirectoryProof) ctx

            let! result =
                api.CreateTeamWithOwner {
                    Name = "Engineering"
                    InitialOwnerUserId = "bob"
                }

            Expect.isOk result "a resolvable owner passes the proof"
        }

        testCaseAsync "RequireDirectoryProof: the caller's own id needs no directory entry (CreateTeam)"
        <| async {
            // The caller's id arrives from the validated access token, so
            // it is already proven; a stale directory entry must not lock
            // an authenticated user out of creating their own team.
            let auditLog = SilentAuditLog() :> IAuditLog
            let teamStore = freshTeamStore ()
            let adminStore = freshAdminStore auditLog
            let directory = RosterDirectory [] :> IUserDirectory
            let ctx = ctxFor "alice" teamStore adminStore auditLog (Some directory)
            let api = PlatformApiHandler.teamApi (configWith RequireDirectoryProof) ctx

            let! result = api.CreateTeam "Alice's team"

            Expect.isOk result "the caller's own id is exempt from the proof"
        }

        // ── Preflight validator ──────────────────────────────────────

        testCaseAsync "Validator: RequireDirectoryProof with no IUserDirectory → Error"
        <| async {
            let services = ServiceCollection() :> IServiceCollection

            let v =
                DirectAddIdentityProofValidator.DirectAddIdentityProofValidator(
                    configWith RequireDirectoryProof,
                    services
                )
                :> ConfigValidation.IConfigValidator

            let! result = v.Validate()

            match result with
            | ConfigValidation.Error msg ->
                Expect.stringContains msg "IUserDirectory" "names the missing companion"
                Expect.stringContains msg "RequireDirectoryProof" "names the mode that demands it"
                Expect.stringContains msg "NoIdentityProof" "names the way back to the default"
            | other -> failtestf "expected Error, got %A" other
        }

        testCaseAsync "Validator: RequireDirectoryProof with an IUserDirectory registered → Ok"
        <| async {
            let services = ServiceCollection() :> IServiceCollection
            services.AddSingleton<IUserDirectory>(RosterDirectory [ "alice" ]) |> ignore

            let v =
                DirectAddIdentityProofValidator.DirectAddIdentityProofValidator(
                    configWith RequireDirectoryProof,
                    services
                )
                :> ConfigValidation.IConfigValidator

            let! result = v.Validate()
            Expect.equal result ConfigValidation.Ok "a composed directory satisfies the requirement"
        }

        testCaseAsync "Validator: default NoIdentityProof → Ok even with no directory"
        <| async {
            let services = ServiceCollection() :> IServiceCollection

            let v =
                DirectAddIdentityProofValidator.DirectAddIdentityProofValidator(configWith NoIdentityProof, services)
                :> ConfigValidation.IConfigValidator

            let! result = v.Validate()
            Expect.equal result ConfigValidation.Ok "the default self-gates — no existing deployment is refused"
        }

        test "Validator metadata is well-formed" {
            let v =
                DirectAddIdentityProofValidator.DirectAddIdentityProofValidator(
                    configWith RequireDirectoryProof,
                    ServiceCollection() :> IServiceCollection
                )
                :> ConfigValidation.IConfigValidator

            Expect.equal v.Name "direct-add-identity-proof" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }

        // ── Config surface ───────────────────────────────────────────

        test "ServerConfig.defaults keeps the pre-549 posture (GP 11)" {
            Expect.equal
                ServerConfig.defaults.DirectAddIdentityProof
                NoIdentityProof
                "the knob defaults to today's behaviour byte-for-byte"
        }
    ]
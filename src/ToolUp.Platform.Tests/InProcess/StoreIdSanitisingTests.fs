module ToolUp.Platform.Tests.InProcess.StoreIdSanitisingTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.StoreIdSanitising
open ToolUp.Platform.Tests.Contracts

// ─── Phase 131 — store-seam id sanitisation ─────────────────────────
//
// The decorators must reject a team/user id that would traverse the
// blob-key path (or collide with the reserved `_platform` scope) BEFORE
// the inner store is touched, and pass valid ids straight through.

type private RecordingTeamStore() =
    let calls = ResizeArray<string>()
    member _.Calls = calls |> List.ofSeq

    interface ITeamStore with
        member _.CreateTeam(teamId, name) = async {
            calls.Add($"CreateTeam:{teamId}")

            return
                Ok {
                    TeamId = teamId
                    Name = name
                    CreatedAt = DateTime.UtcNow
                    Archived = false
                }
        }

        member _.DeleteTeam(teamId) = async {
            calls.Add($"DeleteTeam:{teamId}")
            return Ok()
        }

        member _.AddMember(teamId, userId, _role) = async {
            calls.Add($"AddMember:{teamId}:{userId}")
            return Ok()
        }

        member _.RemoveMember(_, _) = async { return Ok() }
        member _.ChangeMemberRole(_, _, _) = async { return Ok() }
        member _.SetActiveTeam(_, _) = async { return Ok() }

        member _.SetArchived(teamId, _archived) = async {
            calls.Add($"SetArchived:{teamId}")
            return Ok()
        }

        member _.PurgeTeam(teamId) = async {
            calls.Add($"PurgeTeam:{teamId}")
            return Ok()
        }

        member _.PurgeUser(userId) = async {
            calls.Add($"PurgeUser:{userId}")
            return Ok()
        }

        member _.GetTeam _ = failwith "read not exercised"
        member _.ListTeams() = failwith "read not exercised"
        member _.GetTeamsForUser _ = failwith "read not exercised"
        member _.GetTeamMembers _ = failwith "read not exercised"
        member _.GetMemberRole(_, _) = failwith "read not exercised"
        member _.GetActiveTeam _ = failwith "read not exercised"

type private RecordingPermissionStore() =
    let calls = ResizeArray<string>()
    member _.Calls = calls |> List.ofSeq

    interface IPermissionStore with
        member _.SetMemberPermissions(teamId, userId, _moduleName, _permissions) = async {
            calls.Add($"SetMemberPermissions:{teamId}:{userId}")
            return Ok()
        }

        member _.SetTeamPermissions(_, _) = async { return Ok() }
        member _.SetTeamDefaults(_, _) = async { return Ok() }

        member _.SetModuleExposure(teamId, _moduleName, _state) = async {
            calls.Add($"SetModuleExposure:{teamId}")
            return Ok()
        }

        member _.GetTeamPermissions _ = failwith "read not exercised"
        member _.GetEffectivePermissions(_, _) = failwith "read not exercised"
        member _.GetModuleExposure _ = failwith "read not exercised"

// ─── Cross-store contract bindings (real backends) ──────────────────
//
// Promote the decorator-level assertions above to the full
// `StoreIdSanitisingContract` pack bound over a real concrete store +
// blob backend — once in-memory, once local-file — so the rejection is
// proven to fire before any blob write/read reaches the store + its
// blob backend (the gap the decorator-double tests above cannot close).

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private buildStores (storage: IBlobStorage) : StoreIdSanitisingContract.SanitisedStores =
    let nc =
        NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel

    let secrets = ShareTokenStoreTests.InMemorySecretStore() :> ISecretStore

    {
        Team = SanitisingTeamStore(TeamStore(storage, nc) :> ITeamStore) :> ITeamStore
        Permission = SanitisingPermissionStore(PermissionStore(storage, silentLogger)) :> IPermissionStore
        ShareToken =
            SanitisingShareTokenStore(ShareTokenStore.create storage secrets None silentLogger) :> IShareTokenStore
    }

let private inMemoryBackedContract =
    StoreIdSanitisingContract.tests "in-memory blob backend" (fun () ->
        buildStores (InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage))

let private localFileBackedContract =
    StoreIdSanitisingContract.tests "local-file blob backend" (fun () ->
        let root =
            Path.Combine(Path.GetTempPath(), "toolup-storeseam-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore
        buildStores (LocalFileStorage.LocalFileStorage(root) :> IBlobStorage))

[<Tests>]
let tests =
    testList "Phase 131 — store-seam id sanitisation" [
        inMemoryBackedContract
        localFileBackedContract
        testCaseAsync "AddMember rejects a path-traversal teamId before touching the inner store"
        <| async {
            let inner = RecordingTeamStore()
            let store = SanitisingTeamStore(inner) :> ITeamStore

            match! store.AddMember("../../_platform/permissions/t", "user-1", TeamRole.Owner) with
            | Error _ -> Expect.isEmpty inner.Calls "inner store must not be written to"
            | Ok() -> failtest "traversal teamId must be rejected"
        }

        testCaseAsync "AddMember rejects the reserved _platform scope"
        <| async {
            let inner = RecordingTeamStore()
            let store = SanitisingTeamStore(inner) :> ITeamStore

            match! store.AddMember("_platform", "user-1", TeamRole.Owner) with
            | Error _ -> Expect.isEmpty inner.Calls "reserved scope must not reach the store"
            | Ok() -> failtest "_platform teamId must be rejected"
        }

        testCaseAsync "AddMember passes a well-formed Guid-shaped id straight through"
        <| async {
            let inner = RecordingTeamStore()
            let store = SanitisingTeamStore(inner) :> ITeamStore
            let teamId = Guid.NewGuid().ToString("N")

            match! store.AddMember(teamId, "user-42", TeamRole.Member) with
            | Ok() -> Expect.equal inner.Calls [ $"AddMember:{teamId}:user-42" ] "delegates a valid id"
            | Error e -> failtestf "valid id should pass: %s" e
        }

        testCaseAsync "CreateTeam rejects a traversal teamId"
        <| async {
            let inner = RecordingTeamStore()
            let store = SanitisingTeamStore(inner) :> ITeamStore

            match! store.CreateTeam("../evil", "Evil") with
            | Error _ -> Expect.isEmpty inner.Calls "inner store must not be written to"
            | Ok _ -> failtest "traversal teamId must be rejected"
        }

        testCaseAsync "SetMemberPermissions rejects a traversal userId"
        <| async {
            let inner = RecordingPermissionStore()
            let store = SanitisingPermissionStore(inner) :> IPermissionStore

            match! store.SetMemberPermissions("team-ok", "../../_platform/x", "ModuleA", []) with
            | Error _ -> Expect.isEmpty inner.Calls "inner store must not be written to"
            | Ok() -> failtest "traversal userId must be rejected"
        }
    ]
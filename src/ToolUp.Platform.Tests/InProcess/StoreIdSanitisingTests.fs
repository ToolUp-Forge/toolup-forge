module ToolUp.Platform.Tests.InProcess.StoreIdSanitisingTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Scope.StoreIdSanitising
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Testing

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
                    TeamId = teamId.Value
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

        member _.UpdateTeamPermissions(teamId, _change) = async {
            calls.Add($"UpdateTeamPermissions:{teamId}")
            return Ok()
        }

        member _.SetModuleExposure(teamId, _moduleName, _state) = async {
            calls.Add($"SetModuleExposure:{teamId}")
            return Ok()
        }

        member _.GetTeamPermissions _ = async { return failtest "read not exercised" }
        member _.GetEffectivePermissions(_, _) = async { return failtest "read not exercised" }
        member _.GetModuleExposure _ = async { return failtest "read not exercised" }

// ─── Cross-store contract bindings (real backends) ──────────────────
//
// Promote the decorator-level assertions above to the full
// `StoreIdSanitisingContract` pack bound over a real concrete store +
// blob backend — once in-memory, once local-file — so the rejection is
// proven to fire before any blob write/read reaches the store + its
// blob backend (the gap the decorator-double tests above cannot close).

let private buildStores (storage: IBlobStorage) : StoreIdSanitisingContract.SanitisedStores =
    let nc =
        NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel

    let secrets = Fakes.TestSecretStore() :> ISecretStore

    {
        Team = SanitisingTeamStore(TeamStore(storage, nc) :> ITeamStore) :> ITeamStore
        Permission = SanitisingPermissionStore(PermissionStore(storage, Logger.silent)) :> IPermissionStore
        ShareToken =
            SanitisingShareTokenStore(ShareTokenStore.create storage secrets (NoOpAuditLog()) Logger.silent)
            :> IShareTokenStore
    }

let private inMemoryBackedContract =
    StoreIdSanitisingContract.tests "in-memory blob backend" (fun () ->
        buildStores (InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage))

let private localFileBackedContract =
    // The pack's factory has no teardown hook, so each directory outlives
    // its case; the family name attributes it in the census.
    StoreIdSanitisingContract.tests "local-file blob backend" (fun () ->
        let root = TempDir.create "storeseam"
        buildStores (LocalFileStorage.LocalFileStorage(root.Path) :> IBlobStorage))

[<Tests>]
let tests =
    testList "Phase 131 — store-seam id sanitisation" [
        inMemoryBackedContract
        localFileBackedContract
        testCaseAsync "AddMember rejects a path-traversal teamId before touching the inner store"
        <| async {
            let inner = RecordingTeamStore()
            let store = SanitisingTeamStore(inner) :> ITeamStore

            match! store.AddMember(TeamId "../../_platform/permissions/t", UserId "user-1", TeamRole.Owner) with
            | Error _ -> Expect.isEmpty inner.Calls "inner store must not be written to"
            | Ok() -> failtest "traversal teamId must be rejected"
        }

        testCaseAsync "AddMember rejects the reserved _platform scope"
        <| async {
            let inner = RecordingTeamStore()
            let store = SanitisingTeamStore(inner) :> ITeamStore

            match! store.AddMember(TeamId "_platform", UserId "user-1", TeamRole.Owner) with
            | Error _ -> Expect.isEmpty inner.Calls "reserved scope must not reach the store"
            | Ok() -> failtest "_platform teamId must be rejected"
        }

        testCaseAsync "AddMember passes a well-formed Guid-shaped id straight through"
        <| async {
            let inner = RecordingTeamStore()
            let store = SanitisingTeamStore(inner) :> ITeamStore
            let teamId = Guid.NewGuid().ToString("N")

            match! store.AddMember(TeamId teamId, UserId "user-42", TeamRole.Member) with
            | Ok() -> Expect.equal inner.Calls [ $"AddMember:{teamId}:user-42" ] "delegates a valid id"
            | Error e -> failtestf "valid id should pass: %s" e
        }

        testCaseAsync "CreateTeam rejects a traversal teamId"
        <| async {
            let inner = RecordingTeamStore()
            let store = SanitisingTeamStore(inner) :> ITeamStore

            match! store.CreateTeam(TeamId "../evil", "Evil") with
            | Error _ -> Expect.isEmpty inner.Calls "inner store must not be written to"
            | Ok _ -> failtest "traversal teamId must be rejected"
        }

        testCaseAsync "SetMemberPermissions rejects a traversal userId"
        <| async {
            let inner = RecordingPermissionStore()
            let store = SanitisingPermissionStore(inner) :> IPermissionStore

            match! store.SetMemberPermissions(TeamId "team-ok", UserId "../../_platform/x", "ModuleA", []) with
            | Error _ -> Expect.isEmpty inner.Calls "inner store must not be written to"
            | Ok() -> failtest "traversal userId must be rejected"
        }
    ]
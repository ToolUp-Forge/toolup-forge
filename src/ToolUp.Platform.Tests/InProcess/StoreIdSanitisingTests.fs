module ToolUp.Platform.Tests.InProcess.StoreIdSanitisingTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.StoreIdSanitising

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
                }
        }

        member _.AddMember(teamId, userId, _role) = async {
            calls.Add($"AddMember:{teamId}:{userId}")
            return Ok()
        }

        member _.RemoveMember(_, _) = async { return Ok() }
        member _.ChangeMemberRole(_, _, _) = async { return Ok() }
        member _.SetActiveTeam(_, _) = async { return Ok() }
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
        member _.GetTeamPermissions _ = failwith "read not exercised"
        member _.GetEffectivePermissions(_, _) = failwith "read not exercised"

[<Tests>]
let tests =
    testList "Phase 131 — store-seam id sanitisation" [
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
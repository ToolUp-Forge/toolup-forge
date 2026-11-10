module ToolUp.Platform.Tests.InProcess.PermissionStoreTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Scope.AdminMutationApproval
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Scope

/// Bind the `IPermissionStore` contract test pack to the blob-backed
/// `PermissionStore` and to every decorator the scope part composes over
/// it, so the absent / unreadable / corrupt answers hold through each.
/// A log that records nothing and never refuses.
let private noAudit =
    { new IAuditLog with
        member _.Record(AuditScope.StreamKey _, _actor, _) = async { return Ok() }
        member _.GetAuditTrail(AuditScope.StreamKey _, _, _) = async { return [] }
    }

let tests =
    let store (backing: IBlobStorage) =
        PermissionStore(backing) :> IPermissionStore

    testList "IPermissionStore bindings" [
        IPermissionStoreContract.tests "PermissionStore" store

        IPermissionStoreContract.tests "SanitisingPermissionStore" (fun backing ->
            StoreIdSanitising.SanitisingPermissionStore(store backing) :> IPermissionStore)

        IPermissionStoreContract.tests "GrantPolicyPermissionStore" (fun backing ->
            GrantPolicyGuard.GrantPolicyPermissionStore(
                store backing,
                GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [
                    "Governed", GrantPolicy.RequiresAcknowledgement
                ],
                noAudit,
                (AccessContext.system "test")
            )
            :> IPermissionStore)

        IPermissionStoreContract.tests "DualControlPermissionStore" (fun backing ->
            DualControlPermissionStore(
                store backing,
                {
                    Scope = AdminMutationScope.PolicyBearingModulesOnly
                    PendingTtlMinutes = 60
                },
                BlobAdminMutationApprovalStore(backing),
                (fun _ -> false),
                (AccessContext.ofSubject ModulePermissions.Unrestricted (Subject.AuthenticatedUser "admin")),
                (fun () -> DateTimeOffset.UtcNow),
                noAudit
            )
            :> IPermissionStore)
    ]

let private team = TeamId "t-perms"
let private blob = "permissions/t-perms.json"

/// The store-specific half of the document behaviour the contract pack does
/// not reach: an undecodable document is quarantined beside itself and the
/// canonical blob is never overwritten.
let documentTests =
    testList "Phase 1061 — PermissionStore over the document store" [
        test "an undecodable document refuses and is quarantined, never overwritten" {
            let backing = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let corrupt = Text.Encoding.UTF8.GetBytes "{ not json"

            backing.Upload(ScopeKey.PlatformContainer, blob, corrupt)
            |> Async.RunSynchronously
            |> ignore

            let store = PermissionStore(backing) :> IPermissionStore

            Expect.isError (store.SetTeamDefaults(team, Map.empty) |> Async.RunSynchronously) "the write refuses"

            let names =
                backing.List(ScopeKey.PlatformContainer, "permissions/")
                |> Async.RunSynchronously

            Expect.exists names (fun n -> n.StartsWith "permissions/t-perms.json.corrupt-") "a quarantine copy exists"

            Expect.equal
                (backing.Download(ScopeKey.PlatformContainer, blob) |> Async.RunSynchronously)
                (Ok corrupt)
                "the canonical document is untouched"
        }
    ]
module ToolUp.Platform.Tests.InProcess.PermissionStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Scope

/// Bind the `IPermissionStore` contract test pack to the blob-backed
/// `PermissionStore`. Each factory call creates a fresh temp-dir
/// `LocalFileStorage` so tests don't share persisted state.
let tests =
    let factory () =
        let dir =
            Path.Combine(Path.GetTempPath(), "toolup-perms-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory dir |> ignore
        let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
        PermissionStore(storage) :> IPermissionStore

    IPermissionStoreContract.tests "PermissionStore" factory

/// Fails every `Download`; everything else passes through, so the document
/// is present but cannot be read.
type private DownloadFailingStorage(inner: IBlobStorage) =
    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Download(_, _) = async { return Error "simulated outage" }

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

let private team = TeamId "t-perms"
let private blob = "permissions/t-perms.json"

let private refuses (work: Async<'T>) =
    try
        work |> Async.RunSynchronously |> ignore
        false
    with :? PermissionStoreUnavailableException ->
        true

/// The store's document behaviour: absent is the bootstrap default, an
/// unreadable document and an undecodable one refuse (fail closed), and an
/// undecodable one is quarantined beside itself and never overwritten.
let documentTests =
    testList "Phase 1061 — PermissionStore over the document store" [
        test "an absent document is the bootstrap default" {
            let store =
                PermissionStore(InMemoryBlobStorage.InMemoryBlobStorage()) :> IPermissionStore

            let perms = store.GetTeamPermissions team |> Async.RunSynchronously
            Expect.equal perms TeamPermissions.empty "no document reads as no restrictions"
        }

        test "a present document that cannot be read refuses reads and writes" {
            let backing = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let healthy = PermissionStore(backing) :> IPermissionStore

            Expect.isOk
                (healthy.SetModuleExposure(team, "Reports", ModuleExposure.Hidden)
                 |> Async.RunSynchronously)
                "seed a document"

            let before =
                backing.Download(ScopeKey.PlatformContainer, blob) |> Async.RunSynchronously

            let faulted = PermissionStore(DownloadFailingStorage backing) :> IPermissionStore
            Expect.isTrue (refuses (faulted.GetTeamPermissions team)) "a read refuses"
            Expect.isTrue (refuses (faulted.SetTeamDefaults(team, Map.empty))) "a write refuses"

            Expect.equal
                (backing.Download(ScopeKey.PlatformContainer, blob) |> Async.RunSynchronously)
                before
                "the document was not overwritten"
        }

        test "an undecodable document refuses and is quarantined, never overwritten" {
            let backing = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let corrupt = Text.Encoding.UTF8.GetBytes "{ not json"

            backing.Upload(ScopeKey.PlatformContainer, blob, corrupt)
            |> Async.RunSynchronously
            |> ignore

            let store = PermissionStore(backing) :> IPermissionStore
            Expect.isTrue (refuses (store.SetTeamDefaults(team, Map.empty))) "the write refuses"

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
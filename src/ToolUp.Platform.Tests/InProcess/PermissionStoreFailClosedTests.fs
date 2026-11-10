module ToolUp.Platform.Tests.InProcess.PermissionStoreFailClosedTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Infra
open ToolUp.Platform.Scope

// Controllable fake: scripts Download + Exists so we can exercise the
// three load() outcomes (absent → fail-open empty; present-unreadable
// and existence-unknown → fail-closed raise).
type private FakeBlob(download: Result<byte[], string>, exists: Result<bool, exn>) =
    interface IBlobStorage with
        // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member this.Erase(container, prefix, policy, dryRun) =
            ToolUp.Platform.BlobStorage.eraseByPrefix
                (this :> ToolUp.Platform.BlobStorage.IBlobStorage)
                container
                prefix
                policy
                dryRun

        member _.Upload(_, _, _) = async { return Ok "" }
        member _.Download(_, _) = async { return download }

        member this.DownloadRange(container, blobName, offset, length) =
            ToolUp.Platform.BlobStorage.downloadRangeViaDownload (this :> IBlobStorage) container blobName offset length

        member _.Delete(_, _) = async { return Ok() }
        member _.List(_, _) = async { return [] }

        member _.Exists(_, _) = async {
            match exists with
            | Ok b -> return b
            | Result.Error ex -> return raise ex
        }

        member _.GetMetadata(_, _) = async { return Error "n/a" }

let private store (b: IBlobStorage) = PermissionStore(b) :> IPermissionStore

[<Tests>]
let tests =
    testList "PermissionStore fail-open / fail-closed" [

        test "Absent document (Download error + Exists false) → Ok empty (bootstrap fail-open)" {
            let s = store (FakeBlob(Result.Error "Blob not found", Ok false))
            let perms = s.GetTeamPermissions(TeamId "team-x") |> Async.RunSynchronously
            Expect.equal perms (Ok TeamPermissions.empty) "no document = pre-Phase-4 unrestricted bootstrap"
        }

        test "Present but unreadable (Download error + Exists true) → Unreadable (fail closed)" {
            let s = store (FakeBlob(Result.Error "transient storage error", Ok true))

            match s.GetTeamPermissions(TeamId "team-x") |> Async.RunSynchronously with
            | Error(PermissionStoreError.Unreadable _) -> ()
            | other -> failtestf "a readable-but-failed document must not degrade to unrestricted; got %A" other
        }

        test "Existence unconfirmable (Download error + Exists throws) → Error (fail closed)" {
            let s =
                store (FakeBlob(Result.Error "storage down", Result.Error(exn "exists probe failed")))

            Expect.isError
                (s.GetTeamPermissions(TeamId "team-x") |> Async.RunSynchronously)
                "cannot prove absence → must not assume unrestricted"
        }

        test "Present but corrupt JSON (Download ok, unparseable) → Quarantined (fail closed)" {
            let garbage = System.Text.Encoding.UTF8.GetBytes "not json {{{"
            let s = store (FakeBlob(Ok garbage, Ok true))

            match s.GetTeamPermissions(TeamId "team-x") |> Async.RunSynchronously with
            | Error(PermissionStoreError.Quarantined _) -> ()
            | other -> failtestf "a corrupt RBAC document must not be treated as unrestricted; got %A" other
        }

        // Phase 1059 — the fault reaches the caller of the read the request
        // path actually makes (`GetEffectivePermissions`), so the middleware
        // sees it and can refuse.
        test "GetEffectivePermissions over an unreadable document is Error (the caller observes the fault)" {
            let s = store (FakeBlob(Result.Error "transient storage error", Ok true))

            Expect.isError
                (s.GetEffectivePermissions(UserId "u-1", TeamId "team-x")
                 |> Async.RunSynchronously)
                "the effective-permission read must surface the fault, not an empty (unrestricted) map"
        }

        test "Present and valid (Download ok, parseable) → returns parsed permissions" {
            let valid = System.Text.Encoding.UTF8.GetBytes """{"defaults":{},"members":{}}"""

            let s = store (FakeBlob(Ok valid, Ok true))
            let perms = s.GetTeamPermissions(TeamId "team-x") |> Async.RunSynchronously
            Expect.equal perms (Ok TeamPermissions.empty) "well-formed empty document parses cleanly"
        }
    ]
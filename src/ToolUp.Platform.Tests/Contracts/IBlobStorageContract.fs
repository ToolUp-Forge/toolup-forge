module ToolUp.Platform.Tests.Contracts.IBlobStorageContract

open System
open System.Text
open Expecto
open ToolUp.Platform.BlobStorage

/// Contract test list for any `IBlobStorage` implementation. Callers
/// pass a display name (shown as the test-list title) and a factory
/// producing a fresh storage instance — the factory is invoked once
/// per test so stateful implementations get a clean slate. Tests
/// GUID-suffix their container names so implementations that share
/// state between factory invocations (e.g. single-bucket S3 stores
/// reused across tests) don't cross-pollinate.
///
/// Every IBlobStorage implementation — local, Azure, S3, GCS — runs
/// these same assertions. Divergence is a portability bug, not a
/// feature gap.
let tests (name: string) (factory: unit -> IBlobStorage) =
    let uniqueContainer () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        "user-test-" + suffix

    testList $"{name} — IBlobStorage contract" [
        testCaseAsync "Upload then Download round-trips"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let expected = Encoding.UTF8.GetBytes "hello world"

            match! store.Upload(container, "a.txt", expected) with
            | Error e -> failtestf "Upload failed: %s" e
            | Ok _ -> ()

            match! store.Download(container, "a.txt") with
            | Error e -> failtestf "Download failed: %s" e
            | Ok actual -> Expect.sequenceEqual actual expected "round-trip bytes match"
        }

        testCaseAsync "Download of missing blob returns Error"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()

            match! store.Download(container, "missing.txt") with
            | Ok _ -> failtest "Expected Error for missing blob"
            | Error _ -> ()
        }

        testCaseAsync "Exists is false before upload, true after, false after delete"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()

            let! before = store.Exists(container, "thing.txt")
            Expect.isFalse before "exists? before upload"

            let! _ = store.Upload(container, "thing.txt", [| 1uy; 2uy; 3uy |])
            let! after = store.Exists(container, "thing.txt")
            Expect.isTrue after "exists? after upload"

            let! _ = store.Delete(container, "thing.txt")
            let! afterDelete = store.Exists(container, "thing.txt")
            Expect.isFalse afterDelete "exists? after delete"
        }

        testCaseAsync "Delete is idempotent on missing blobs"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()

            match! store.Delete(container, "not-there.txt") with
            | Ok() -> ()
            | Error e -> failtestf "Delete of missing blob should be Ok; got Error: %s" e
        }

        testCaseAsync "Upload overwrites existing blob"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let first = Encoding.UTF8.GetBytes "v1"
            let second = Encoding.UTF8.GetBytes "v2"

            let! _ = store.Upload(container, "x.txt", first)
            let! _ = store.Upload(container, "x.txt", second)

            match! store.Download(container, "x.txt") with
            | Error e -> failtestf "Download failed: %s" e
            | Ok bytes -> Expect.sequenceEqual bytes second "latest write wins"
        }

        testCaseAsync "List includes all uploaded blobs"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()

            let! _ = store.Upload(container, "a.txt", [| 1uy |])
            let! _ = store.Upload(container, "b.txt", [| 2uy |])

            let! entries = store.List(container, "")
            Expect.contains entries "a.txt" "list contains a.txt"
            Expect.contains entries "b.txt" "list contains b.txt"
        }

        testCaseAsync "List of an empty container returns []"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let! entries = store.List(container, "")
            Expect.isEmpty entries "empty container yields empty list"
        }

        testCaseAsync "GetMetadata returns size and recent LastModified for existing blob"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let content = Encoding.UTF8.GetBytes "12345"

            let! _ = store.Upload(container, "m.txt", content)

            match! store.GetMetadata(container, "m.txt") with
            | Error e -> failtestf "GetMetadata failed: %s" e
            | Ok meta ->
                Expect.equal meta.Size 5L "size matches uploaded byte count"

                let age = DateTime.UtcNow - meta.LastModified
                Expect.isLessThan age.TotalMinutes 1.0 "LastModified is within the last minute"
        }

        testCaseAsync "GetMetadata on missing blob returns Error"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()

            match! store.GetMetadata(container, "missing.txt") with
            | Ok _ -> failtest "Expected Error for missing blob"
            | Error _ -> ()
        }

        testCaseAsync "Container isolation: writes to A don't appear in B's listing"
        <| async {
            let store = factory ()
            let containerA = uniqueContainer ()
            let containerB = uniqueContainer ()

            let! _ = store.Upload(containerA, "in-a.txt", [| 1uy |])
            let! entries = store.List(containerB, "")
            Expect.isEmpty entries "container B is isolated from container A"
        }
    ]
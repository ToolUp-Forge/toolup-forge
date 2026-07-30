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

        // Blob names are `/`-delimited on this interface — every caller
        // builds them that way (`$"memberships/{userId}.json"`), so what
        // `List` hands back must be the same shape it was given.
        // Callers rely on that twice: they feed the name straight back
        // into `Download`, and several strip a known prefix off it to
        // recover the id (`name.Replace("memberships/", "")`). A backend
        // that returns the OS separator breaks the second use SILENTLY —
        // the replace no-ops, the caller gets a mangled id, and nothing
        // throws. That is exactly how a `LocalFileStorage` returning
        // `memberships\alice.json` on Windows let `TeamStore.IsLastOwner`
        // compare `"memberships\alice" = "alice"`, always miss, and allow
        // the last Owner of a team to be removed (Phase 617).
        testCaseAsync "List returns `/`-delimited names for nested blobs, never the OS separator"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()

            let! _ = store.Upload(container, "nested/deep/c.txt", [| 3uy |])

            let! entries = store.List(container, "nested/")
            Expect.contains entries "nested/deep/c.txt" "nested name keeps its `/` separators"

            Expect.all
                entries
                (fun n -> not (n.Contains '\\'))
                "no entry carries a backslash — blob names are not filesystem paths"

            // The round-trip half: whatever `List` returned must be a
            // name `Download` accepts, on every platform.
            for entry in entries do
                match! store.Download(container, entry) with
                | Ok _ -> ()
                | Error e -> failtestf "List returned '%s' but Download rejected it: %s" entry e
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

        // ── Phase 455 — DownloadRange ────────────────────────────────
        // 100 distinct byte values so any off-by-one slice mismatches.

        testCaseAsync "DownloadRange reads a range at the start"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let content = Array.init 100 byte

            let! _ = store.Upload(container, "r.bin", content)

            match! store.DownloadRange(container, "r.bin", 0L, 10) with
            | Error e -> failtestf "DownloadRange failed: %s" e
            | Ok bytes -> Expect.sequenceEqual bytes (Array.sub content 0 10) "first 10 bytes"
        }

        testCaseAsync "DownloadRange reads a mid-blob range"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let content = Array.init 100 byte

            let! _ = store.Upload(container, "r.bin", content)

            match! store.DownloadRange(container, "r.bin", 37L, 13) with
            | Error e -> failtestf "DownloadRange failed: %s" e
            | Ok bytes -> Expect.sequenceEqual bytes (Array.sub content 37 13) "bytes [37, 50)"
        }

        testCaseAsync "DownloadRange clamps a range that overshoots EOF"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let content = Array.init 100 byte

            let! _ = store.Upload(container, "r.bin", content)

            match! store.DownloadRange(container, "r.bin", 90L, 50) with
            | Error e -> failtestf "DownloadRange failed: %s" e
            | Ok bytes -> Expect.sequenceEqual bytes (Array.sub content 90 10) "last 10 bytes, clamped"
        }

        testCaseAsync "DownloadRange past EOF returns Ok [||]"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let content = Array.init 100 byte

            let! _ = store.Upload(container, "r.bin", content)

            match! store.DownloadRange(container, "r.bin", 100L, 10) with
            | Error e -> failtestf "DownloadRange at size failed: %s" e
            | Ok bytes -> Expect.isEmpty bytes "offset = size yields empty"

            match! store.DownloadRange(container, "r.bin", 150L, 10) with
            | Error e -> failtestf "DownloadRange beyond size failed: %s" e
            | Ok bytes -> Expect.isEmpty bytes "offset > size yields empty"
        }

        testCaseAsync "DownloadRange concatenated ranges byte-equal the full download"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            // Deliberately not a multiple of the chunk size.
            let content = Array.init 100 byte

            let! _ = store.Upload(container, "r.bin", content)

            let chunk = 7
            let mutable offset = 0L
            let mutable finished = false
            let assembled = ResizeArray<byte>()

            while not finished do
                match! store.DownloadRange(container, "r.bin", offset, chunk) with
                | Error e -> failtestf "DownloadRange at %d failed: %s" offset e
                | Ok bytes ->
                    assembled.AddRange bytes
                    offset <- offset + int64 bytes.Length

                    if bytes.Length < chunk then
                        finished <- true

            match! store.Download(container, "r.bin") with
            | Error e -> failtestf "Download failed: %s" e
            | Ok full -> Expect.sequenceEqual (assembled.ToArray()) full "concatenated ranges = full download"
        }

        testCaseAsync "DownloadRange of missing blob returns Error (parity with Download)"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()

            match! store.DownloadRange(container, "missing.bin", 0L, 10) with
            | Ok _ -> failtest "Expected Error for missing blob"
            | Error _ -> ()
        }

        testCaseAsync "DownloadRange rejects negative offset and non-positive length"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()

            let! _ = store.Upload(container, "r.bin", [| 1uy; 2uy; 3uy |])

            match! store.DownloadRange(container, "r.bin", -1L, 10) with
            | Ok _ -> failtest "Expected Error for negative offset"
            | Error _ -> ()

            match! store.DownloadRange(container, "r.bin", 0L, 0) with
            | Ok _ -> failtest "Expected Error for zero length"
            | Error _ -> ()

            match! store.DownloadRange(container, "r.bin", 0L, -5) with
            | Ok _ -> failtest "Expected Error for negative length"
            | Error _ -> ()
        }
    ]
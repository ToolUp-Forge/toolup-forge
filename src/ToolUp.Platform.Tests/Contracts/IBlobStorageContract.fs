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

        // Phase 733 — the partial-overlap clause and the boundary it
        // meets the fully-past clause at. The clouds clamp partial
        // overlap natively (206 with the overlapping bytes) and refuse
        // only the fully-past case, so the two clauses are answered by
        // different backend paths; `offset = size` is where they meet
        // and is exactly where an off-by-one would land.
        testCaseAsync "DownloadRange partial overlap returns only the overlapping bytes"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let content = Array.init 100 byte

            let! _ = store.Upload(container, "r.bin", content)

            // Maximal partial overlap: one byte inside the object, the
            // rest of the requested window past it.
            match! store.DownloadRange(container, "r.bin", 99L, 64) with
            | Error e -> failtestf "DownloadRange at the last byte failed: %s" e
            | Ok bytes -> Expect.sequenceEqual bytes (Array.sub content 99 1) "the last byte only, not zero-padded"

            // An exact fit is NOT a partial overlap — the window ends
            // precisely at EOF, so the full `length` comes back.
            match! store.DownloadRange(container, "r.bin", 60L, 40) with
            | Error e -> failtestf "DownloadRange exact-fit to EOF failed: %s" e
            | Ok bytes ->
                Expect.sequenceEqual bytes (Array.sub content 60 40) "an exact fit ending at EOF reads full length"

            // One byte on from that fit is the FIRST fully-past offset.
            match! store.DownloadRange(container, "r.bin", 100L, 40) with
            | Error e -> failtestf "DownloadRange at the clause boundary failed: %s" e
            | Ok bytes -> Expect.isEmpty bytes "offset = size is the first fully-past offset"
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

        // ── Concurrent same-blob access ──────────────────────────────
        // Every cloud object store tolerates a Download racing an
        // Upload of the same blob: the reader observes the previous
        // version or the new one — never an error, never a torn
        // buffer. A backend that takes an exclusive file handle for
        // either side turns that race into "the process cannot access
        // the file", a works-in-production-fails-locally divergence:
        // the job store's external-compute callback ingress reading a
        // run blob while the reconciliation poll rewrote the same blob
        // reproduced it reliably against `LocalFileStorage` (found
        // building Phase 320). Each version is a single repeated byte,
        // so a torn read — bytes from two versions in one buffer — is
        // detectable, not just an errored one.
        testCaseAsync "Concurrent Upload and Download of the same blob: no errors, no torn reads"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let payloadFor (version: int) = Array.create 4096 (byte version)

            // Seed first: this case pins overwrite-during-read; the
            // missing-blob path is covered elsewhere and would make
            // reader errors ambiguous here.
            match! store.Upload(container, "contended.bin", payloadFor 0) with
            | Error e -> failtestf "seed Upload failed: %s" e
            | Ok _ -> ()

            let iterations = 100

            let writer = async {
                for version in 1..iterations do
                    match! store.Upload(container, "contended.bin", payloadFor version) with
                    | Error e ->
                        failtestf "Upload racing a Download of the same blob failed (iteration %d): %s" version e
                    | Ok _ -> ()
            }

            let reader = async {
                for iteration in 1..iterations do
                    match! store.Download(container, "contended.bin") with
                    | Error e ->
                        failtestf "Download racing an Upload of the same blob failed (iteration %d): %s" iteration e
                    | Ok bytes ->
                        Expect.equal bytes.Length 4096 "a concurrent read never observes a partial blob"

                        Expect.all
                            bytes
                            (fun b -> b = bytes[0])
                            "a concurrent read never observes a torn blob mixing two versions"
            }

            let! _ = Async.Parallel [ writer; reader ]
            return ()
        }

        // Two WRITERS, which the case above does not cover. Phase 319's
        // ship report recorded `LocalFileStorage` throwing "used by another
        // process" on concurrent writes to the same blob and filed it as a
        // store-robustness note, reachable only under a caller bug. The
        // Phase 320 rework (temp file + atomic `File.Move` under a per-path
        // lock) closed it — but nothing in the contract pack SAID so, so
        // the fix and the note were independent facts and either could
        // change without the other noticing.
        //
        // The last write wins is NOT asserted: cloud object stores make no
        // ordering promise between overlapping PUTs of the same key, and a
        // contract test that demanded one would be pinning a guarantee the
        // interface does not offer. What every backend does promise is that
        // an overlapping pair completes without error and leaves ONE
        // coherent version behind — a value some writer actually wrote,
        // never a mix.
        testCaseAsync "Concurrent Uploads of the same blob: no errors, one coherent version survives"
        <| async {
            let store = factory ()
            let container = uniqueContainer ()
            let payloadFor (version: int) = Array.create 4096 (byte version)

            let iterations = 50

            let writerFor (version: int) = async {
                for _ in 1..iterations do
                    match! store.Upload(container, "contested.bin", payloadFor version) with
                    | Error e -> failtestf "Upload racing another Upload of the same blob failed: %s" e
                    | Ok _ -> ()
            }

            let! _ = Async.Parallel [ writerFor 1; writerFor 2 ]

            match! store.Download(container, "contested.bin") with
            | Error e -> failtestf "Download after concurrent Uploads failed: %s" e
            | Ok bytes ->
                Expect.equal bytes.Length 4096 "the surviving blob is whole, not a partial write"

                Expect.all bytes (fun b -> b = bytes[0]) "the surviving blob is one writer's version, not a mix of two"

                Expect.isTrue
                    (bytes[0] = 1uy || bytes[0] = 2uy)
                    "the surviving blob is a version some writer actually wrote"
        }
    ]
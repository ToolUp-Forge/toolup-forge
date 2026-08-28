// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IMediaLibraryContract

open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.MediaLibrary

// ─── Phase 88 — IMediaLibrary contract pack ───────────────────────────
//
// Behavioural conformance for any `IMediaLibrary` implementation:
// upload / get / list / delete round-trips, byte-range correctness
// (`OpenRange` slicing + the `Unsatisfiable` path that maps to HTTP
// `416`), content-length, and signed-URL minting for present / absent
// items. Pure `ByteRange.parse` (the `206` / `416` decision) and the
// `SignedUrl` crypto are exercised separately in `MediaLibraryTests`.
//
// ─── Phase 468 — the O(range) proof ──────────────────────────────────
//
// `tests` above is behavioural: it says what bytes come back, never
// what they cost. Phase 468's whole claim is a COST claim — a seek into
// a large original must read the requested window, not the object — and
// a behavioural pack cannot fail when that regresses. `rangedReadTests`
// closes that: it interposes a counting `IBlobStorage` between the
// library and its store and asserts on the byte counts.
//
// Two doubles, and the second matters as much as the first. Serving
// correctly over a store that REFUSES ranged reads is not a degraded
// mode to be tolerated — it is the shipped behaviour for every
// encrypted deployment (the Phase 22 whole-blob AES-GCM decorator), so
// the fallback is bound to the full behavioural pack in its own right
// rather than getting one token case here.

let private container = "team-contract"

let private scope: StorageScope = {
    ScopeId = "team-contract"
    Container = container
    Persist = true
}

let private request (mime: string) (bytes: byte[]) : MediaUploadRequest =
    match MediaUploadRequest.create MediaLibraryOptions.defaults bytes "clip.mp4" mime "user-1" (Some "A clip") with
    | Ok r -> r
    | Error e -> failwithf "contract setup: invalid upload request %A" e

let private readAll (s: Stream) : byte[] =
    use ms = new MemoryStream()
    s.CopyTo ms
    ms.ToArray()

let private sample = [| for i in 0..99 -> byte i |]

// ─── Phase 468 test doubles ───────────────────────────────────────────

/// Counts what each read path pulled through the `IBlobStorage` seam.
/// Bytes are attributed to the path that RETURNED them, so a store
/// whose own `DownloadRange` is implemented by download-then-slice is
/// still measured on what it handed back — which is the question this
/// pack asks: how much did the media library ask the store for?
type CountingBlobStorage(inner: IBlobStorage) =
    let mutable downloadCalls = 0
    let mutable downloadBytes = 0L
    let mutable rangeCalls = 0
    let mutable rangeBytes = 0L

    /// Whole-object `Download` calls — the pre-468 range path, and the
    /// Phase 468 fallback. Zero on a working ranged fast path.
    member _.DownloadCalls = downloadCalls
    member _.DownloadBytes = downloadBytes
    /// Bounded `DownloadRange` calls.
    member _.RangeCalls = rangeCalls
    member _.RangeBytes = rangeBytes
    /// Bytes returned through any read path.
    member _.TotalBytesRead = downloadBytes + rangeBytes

    /// Zero the counters — called after upload so the measurement
    /// covers serving only.
    member _.Reset() =
        downloadCalls <- 0
        downloadBytes <- 0L
        rangeCalls <- 0
        rangeBytes <- 0L

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

        member _.Download(container, blobName) = async {
            let! result = inner.Download(container, blobName)
            downloadCalls <- downloadCalls + 1

            match result with
            | Ok bytes -> downloadBytes <- downloadBytes + int64 bytes.Length
            | Error _ -> ()

            return result
        }

        member _.DownloadRange(container, blobName, offset, length) = async {
            let! result = inner.DownloadRange(container, blobName, offset, length)
            rangeCalls <- rangeCalls + 1

            match result with
            | Ok bytes -> rangeBytes <- rangeBytes + int64 bytes.Length
            | Error _ -> ()

            return result
        }

/// A store that refuses ranged reads — the shape of the Phase 22
/// whole-blob AES-GCM `EncryptedBlobStorage` decorator, whose ciphertext
/// cannot be decrypted from a mid-blob window. Everything else
/// delegates, so a library bound over this double is exercising exactly
/// the encrypted deployment's serving path.
type RangeRefusingBlobStorage(inner: IBlobStorage) =
    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

        member _.DownloadRange(_, _, _, _) = async {
            return
                Error
                    "test double: ranged reads refused (mirrors EncryptedBlobStorage — whole-blob AES-GCM is undecryptable from a mid-blob window)"
        }

/// Conformance suite. `makeLibrary` returns a fresh empty library.
let tests (name: string) (makeLibrary: unit -> IMediaLibrary) : Test =
    testList (sprintf "IMediaLibrary contract (%s)" name) [
        testCaseAsync "upload then get round-trips the record"
        <| async {
            let lib = makeLibrary ()
            let! result = lib.Upload(container, request "video/mp4" sample)

            match result with
            | Error e -> failtestf "upload failed: %A" e
            | Ok record ->
                Expect.equal record.SizeBytes 100L "size bytes"
                Expect.equal record.MimeType "video/mp4" "mime type"
                Expect.equal record.Status MediaIngestionStatus.Ready "ready immediately (no transcoder)"

                let! fetched = lib.Get(container, record.Id)
                Expect.isSome fetched "get returns the record"
                Expect.equal fetched.Value.ContentHash record.ContentHash "content hash stable"
        }

        testCaseAsync "content length matches the original size"
        <| async {
            let lib = makeLibrary ()
            let! upload = lib.Upload(container, request "video/mp4" sample)
            let record = Expect.wantOk upload "upload"
            let! len = lib.ContentLength(container, record.Id)
            Expect.equal (Expect.wantOk len "content length") 100L "length"
        }

        testCaseAsync "open full range returns every byte"
        <| async {
            let lib = makeLibrary ()
            let! upload = lib.Upload(container, request "video/mp4" sample)
            let record = Expect.wantOk upload "upload"
            let! ranged = lib.OpenRange(container, record.Id, { Start = 0L; End = 99L })
            let stream = Expect.wantOk ranged "open range"
            Expect.equal (readAll stream) sample "full body"
        }

        testCaseAsync "open partial range returns the slice"
        <| async {
            let lib = makeLibrary ()
            let! upload = lib.Upload(container, request "video/mp4" sample)
            let record = Expect.wantOk upload "upload"
            let! ranged = lib.OpenRange(container, record.Id, { Start = 10L; End = 19L })
            let stream = Expect.wantOk ranged "open range"
            Expect.equal (readAll stream) sample[10..19] "10..19 slice"
        }

        testCaseAsync "open out-of-bounds range is Unsatisfiable (HTTP 416)"
        <| async {
            let lib = makeLibrary ()
            let! upload = lib.Upload(container, request "video/mp4" sample)
            let record = Expect.wantOk upload "upload"
            let! ranged = lib.OpenRange(container, record.Id, { Start = 500L; End = 600L })

            match ranged with
            | Error MediaRangeError.Unsatisfiable -> ()
            | other -> failtestf "expected Unsatisfiable, got %A" other
        }

        testCaseAsync "delete removes the item"
        <| async {
            let lib = makeLibrary ()
            let! upload = lib.Upload(container, request "video/mp4" sample)
            let record = Expect.wantOk upload "upload"
            let! del = lib.Delete(container, record.Id)
            Expect.isOk del "delete ok"
            let! fetched = lib.Get(container, record.Id)
            Expect.isNone fetched "gone after delete"
        }

        testCaseAsync "delete of a missing item is NotFound"
        <| async {
            let lib = makeLibrary ()
            let! del = lib.Delete(container, MediaId "does-not-exist")

            match del with
            | Error MediaDeleteError.NotFound -> ()
            | other -> failtestf "expected NotFound, got %A" other
        }

        testCaseAsync "list returns uploaded items"
        <| async {
            let lib = makeLibrary ()
            let! _ = lib.Upload(container, request "video/mp4" sample)
            let! _ = lib.Upload(container, request "audio/mpeg" sample)
            let! items = lib.List(container, "", 0)
            Expect.equal (List.length items) 2 "two items listed"
        }

        testCaseAsync "signed url for a missing item is NotFound"
        <| async {
            let lib = makeLibrary ()
            let! url = lib.SignedUrl(MediaId "missing", scope, System.TimeSpan.FromMinutes 5.0)

            match url with
            | Error SignedUrlError.NotFound -> ()
            | other -> failtestf "expected NotFound, got %A" other
        }

        testCaseAsync "signed url for a present item yields a token URL"
        <| async {
            let lib = makeLibrary ()
            let! upload = lib.Upload(container, request "video/mp4" sample)
            let record = Expect.wantOk upload "upload"
            let! url = lib.SignedUrl(record.Id, scope, System.TimeSpan.FromMinutes 5.0)
            let signed = Expect.wantOk url "signed url"
            Expect.stringContains signed (MediaId.value record.Id) "url carries media id"
            Expect.stringContains signed "token=" "url carries a token"
        }
    ]

// ─── Phase 468 — O(range) conformance ─────────────────────────────────

/// Object used for the cost measurements — big enough that "read the
/// whole thing" and "read the window" are unmistakably different
/// numbers, small enough to stay a fast in-memory test.
let private largeObjectBytes = 64 * 1024

/// Deliberately far below the 1 MiB production default so the chunk
/// loop actually iterates within a 64 KiB object.
let private testChunkBytes = 4096

let private largePayload = Array.init largeObjectBytes (fun i -> byte (i % 251))

/// Cost conformance for an `IMediaLibrary` backed by an `IBlobStorage`.
/// `makeStore` mints a fresh empty store; `makeOver options store`
/// builds the library over it, so this pack can interpose the counting
/// double and choose the chunk size.
///
/// A CDN-direct or cloud-native implementation that owns no
/// `IBlobStorage` binds `tests` only — the cost claim is about the
/// blob-backed path, and asserting it of an implementation with no such
/// seam would be asserting nothing.
let rangedReadTests
    (name: string)
    (makeStore: unit -> IBlobStorage)
    (makeOver: MediaLibraryOptions -> IBlobStorage -> IMediaLibrary)
    : Test =
    let options = {
        MediaLibraryOptions.defaults with
            RangeChunkBytes = testChunkBytes
    }

    /// Upload the large payload and hand back the record with the
    /// counters zeroed, so every assertion measures serving alone.
    let uploaded (counting: CountingBlobStorage) (lib: IMediaLibrary) = async {
        let! upload = lib.Upload(container, request "video/mp4" largePayload)
        let record = Expect.wantOk upload "upload"
        counting.Reset()
        return record
    }

    testList (sprintf "IMediaLibrary ranged reads (%s)" name) [
        testCaseAsync "a mid-file range reads O(range) bytes, not O(object)"
        <| async {
            let counting = CountingBlobStorage(makeStore ())
            let lib = makeOver options (counting :> IBlobStorage)
            let! record = uploaded counting lib

            let window = { Start = 30000L; End = 30999L }
            let! ranged = lib.OpenRange(container, record.Id, window)
            let stream = Expect.wantOk ranged "open range"
            let served = readAll stream

            Expect.equal served largePayload[30000..30999] "the exact window, byte for byte"

            if counting.DownloadCalls <> 0 then
                failtestf "the ranged fast path must not issue a whole-object Download — saw %d" counting.DownloadCalls

            // The claim, stated as a number: the window plus at most one
            // chunk of look-ahead. Anything approaching the object size
            // means the fast path silently regressed to slicing.
            let ceiling = window.Length + int64 testChunkBytes

            if counting.TotalBytesRead > ceiling then
                failtestf
                    "serving a %d-byte window of a %d-byte object read %d bytes (ceiling %d = window + one chunk)"
                    window.Length
                    largeObjectBytes
                    counting.TotalBytesRead
                    ceiling
        }

        testCaseAsync "serving the whole object still walks it in bounded chunks"
        <| async {
            let counting = CountingBlobStorage(makeStore ())
            let lib = makeOver options (counting :> IBlobStorage)
            let! record = uploaded counting lib

            let! ranged =
                lib.OpenRange(
                    container,
                    record.Id,
                    {
                        Start = 0L
                        End = int64 largeObjectBytes - 1L
                    }
                )

            let stream = Expect.wantOk ranged "open range"
            Expect.equal (readAll stream) largePayload "every byte of the object"

            if counting.DownloadCalls <> 0 then
                failtestf "a full-object range must still stream, not Download — saw %d" counting.DownloadCalls

            // No single read exceeded the chunk: the peak buffer a
            // response holds is bounded by configuration, not by the
            // object's size.
            let expectedCalls = largeObjectBytes / testChunkBytes

            if counting.RangeCalls < expectedCalls then
                failtestf
                    "expected at least %d bounded reads of %d bytes, saw %d"
                    expectedCalls
                    testChunkBytes
                    counting.RangeCalls
        }

        testCaseAsync "a store refusing ranged reads still serves the exact slice"
        <| async {
            let refusing = RangeRefusingBlobStorage(makeStore ()) :> IBlobStorage
            let counting = CountingBlobStorage(refusing)
            let lib = makeOver options (counting :> IBlobStorage)
            let! record = uploaded counting lib

            let! ranged = lib.OpenRange(container, record.Id, { Start = 30000L; End = 30999L })
            let stream = Expect.wantOk ranged "open range"
            Expect.equal (readAll stream) largePayload[30000..30999] "the exact window, byte for byte"

            if counting.DownloadCalls = 0 then
                failtest "the fallback must reach the whole-object Download path"
        }

        testCaseAsync "content length is read without downloading the object"
        <| async {
            let counting = CountingBlobStorage(makeStore ())
            let lib = makeOver options (counting :> IBlobStorage)
            let! record = uploaded counting lib

            let! len = lib.ContentLength(container, record.Id)
            Expect.equal (Expect.wantOk len "content length") (int64 largeObjectBytes) "length"
            Expect.equal counting.TotalBytesRead 0L "no bytes read to answer a length question"
        }
    ]
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

    // Phase 741 — the PEAK counters. The 468 totals answer "how much
    // did it read?"; a memory claim needs "how much at once?", and the
    // two come apart exactly where this phase lives: a streaming commit
    // reads every byte of the object and holds one chunk of it.
    let mutable maxDownloadBytes = 0
    let mutable maxUploadBytes = 0
    let mutable composeCalls = 0

    /// Whole-object `Download` calls — the pre-468 range path, and the
    /// Phase 468 fallback. Zero on a working ranged fast path.
    member _.DownloadCalls = downloadCalls
    member _.DownloadBytes = downloadBytes
    /// Bounded `DownloadRange` calls.
    member _.RangeCalls = rangeCalls
    member _.RangeBytes = rangeBytes
    /// Bytes returned through any read path.
    member _.TotalBytesRead = downloadBytes + rangeBytes

    /// Phase 741 — the largest single `Download` result. The memory
    /// claim in one number: a commit that never materialises the object
    /// never pulls more than one chunk in one call.
    member _.MaxDownloadBytes = maxDownloadBytes
    /// Phase 741 — the largest single `Upload` payload: the same claim
    /// from the write side, since a streaming commit hands the store
    /// part NAMES and never the assembled object.
    member _.MaxUploadBytes = maxUploadBytes
    /// Phase 741 — how many times assembly went through the compose
    /// seam. Zero on the materialised path.
    member _.ComposeCalls = composeCalls

    /// Zero the counters — called after upload so the measurement
    /// covers serving only.
    member _.Reset() =
        downloadCalls <- 0
        downloadBytes <- 0L
        rangeCalls <- 0
        rangeBytes <- 0L
        maxDownloadBytes <- 0
        maxUploadBytes <- 0
        composeCalls <- 0

    interface IBlobStorage with
        // Phase 741 — the capability passes through, so what this
        // double measures is the library's behaviour over a real
        // store's answer rather than one this double invented.
        member _.CanComposeFrom = inner.CanComposeFrom

        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) = async {
            composeCalls <- composeCalls + 1
            return! inner.ComposeFrom(container, targetBlobName, sourceBlobNames)
        }

        member _.Upload(container, blobName, content) = async {
            maxUploadBytes <- max maxUploadBytes (if isNull content then 0 else content.Length)
            return! inner.Upload(container, blobName, content)
        }

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
            | Ok bytes ->
                downloadBytes <- downloadBytes + int64 bytes.Length
                maxDownloadBytes <- max maxDownloadBytes bytes.Length
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
        // Phase 741 — this double refuses ranged READS only; compose
        // passes through, so the encryption decorator's two refusals
        // stay independently testable rather than arriving as one
        // undifferentiated "encrypted store" shape.
        member _.CanComposeFrom = inner.CanComposeFrom

        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) =
            inner.ComposeFrom(container, targetBlobName, sourceBlobNames)

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

/// Phase 741 — a store that refuses to compose stored parts: the shape
/// of the `EncryptedBlobStorage` decorator (each part is its own
/// whole-blob AES-GCM envelope, so concatenating them yields nothing
/// decryptable) and of every custom `IBlobStorage` that adopts the
/// member by declining it.
///
/// A SEPARATE double from `RangeRefusingBlobStorage` above, though the
/// same decorator motivates both: the refusals are independent
/// capabilities and a single "encrypted-shaped" double would make it
/// impossible to say which one a failure was about.
type ComposeRefusingBlobStorage(inner: IBlobStorage) =
    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported
                "test double: compose refused (mirrors EncryptedBlobStorage — concatenated AES-GCM envelopes are not the envelope of the concatenated plaintext)"

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

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

// ─── Phase 469 — IUploadSessionStore conformance (the resume matrix) ──
//
// The pack above measures the READ side. This one is the write side's
// failure matrix, and it is a failure matrix rather than a happy path
// on purpose: a resumable protocol's whole value is what it does when
// something goes wrong, so "resume after a drop" is one case among
// duplicate chunk, wrong offset, oversize, TTL expiry and cross-scope
// refusal, not the headline with six footnotes.
//
// The headline case is nonetheless the acceptance criterion, and it is
// stated as an EQUALITY against the single-shot path rather than as a
// property of the resumed record on its own: a resumed upload must
// produce a record content-hash-equal to a single-shot upload of the
// same bytes, with the same ingestion status. Asserting "the hash is
// some 64 hex characters" would pass against a resumed upload that
// assembled the chunks in the wrong order.

/// A second scope, disjoint from `container` above — the cross-scope
/// refusal case needs two containers over ONE store.
let private otherContainer = "team-contract-other"

/// The session payload: three unequal chunks, so an assembly that
/// concatenates in the wrong order, drops one, or double-counts a
/// retry produces a different hash rather than the same bytes.
let private sessionChunks = [|
    Array.init 1000 (fun i -> byte (i % 251))
    Array.init 1500 (fun i -> byte ((i + 7) % 241))
    Array.init 500 (fun i -> byte ((i + 19) % 229))
|]

let private sessionPayload = Array.concat sessionChunks

let private declaration (options: MediaLibraryOptions) (size: int64) : MediaUploadDeclaration =
    match MediaUploadDeclaration.create options "clip.mp4" "video/mp4" size "user-1" (Some "A clip") with
    | Ok d -> d
    | Error e -> failwithf "contract setup: invalid declaration %A" e

/// Conformance for an `IUploadSessionStore` and the `IMediaLibrary` it
/// commits through. `makeOver` takes the options and a clock so the
/// pack can drive the session TTL without sleeping, and returns both
/// halves because the acceptance criterion compares the committed
/// record against a single-shot upload through the same library.
let uploadSessionTests
    (name: string)
    (makeStore: unit -> IBlobStorage)
    (makeOver:
        MediaLibraryOptions -> (unit -> System.DateTimeOffset) -> IBlobStorage -> IUploadSessionStore * IMediaLibrary)
    : Test =

    let defaultOptions = MediaLibraryOptions.defaults

    let fixedNow () =
        System.DateTimeOffset(2026, 8, 28, 12, 0, 0, System.TimeSpan.Zero)

    /// A store and library over a fresh blob store, at the wall-free
    /// fixed clock most cases want.
    let fresh () =
        makeOver defaultOptions fixedNow (makeStore ())

    /// Open a session for the whole payload.
    let begun (sessions: IUploadSessionStore) (options: MediaLibraryOptions) = async {
        let! opened = sessions.BeginUpload(container, declaration options (int64 sessionPayload.Length))
        return Expect.wantOk opened "begin upload"
    }

    testList (sprintf "IUploadSessionStore contract (%s)" name) [

        testCaseAsync "a dropped upload resumes and commits identically to a single-shot upload of the same bytes"
        <| async {
            let sessions, lib = fresh ()
            let! sessionId = begun sessions defaultOptions

            // Chunk 0 lands.
            let! p0 = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])
            let progress0 = Expect.wantOk p0 "first chunk"
            Expect.equal progress0.ReceivedBytes 1000L "cursor after the first chunk"

            // Chunk 1 lands — but the client never sees the response
            // (the connection drops). It retries from the last cursor
            // it DID see, which is the whole resume protocol.
            let! _ = sessions.AppendChunk(container, sessionId, 1000L, sessionChunks[1])
            let! retry = sessions.AppendChunk(container, sessionId, 1000L, sessionChunks[1])
            let resumed = Expect.wantOk retry "retry of the chunk whose response was lost"
            Expect.equal resumed.ReceivedBytes 2500L "the retry reports the cursor, it does not double-count"

            let! p2 = sessions.AppendChunk(container, sessionId, 2500L, sessionChunks[2])
            let progress2 = Expect.wantOk p2 "final chunk"
            Expect.equal progress2.ReceivedBytes 3000L "every byte accepted"

            let! commit = sessions.CommitUpload(container, sessionId)
            let resumedRecord = Expect.wantOk commit "commit"

            // The equality that IS the acceptance criterion.
            let! single = lib.Upload(container, request "video/mp4" sessionPayload)
            let singleRecord = Expect.wantOk single "single-shot upload of the same bytes"

            Expect.equal resumedRecord.ContentHash singleRecord.ContentHash "content hash equal to the single-shot"
            Expect.equal resumedRecord.SizeBytes singleRecord.SizeBytes "size equal to the single-shot"
            Expect.equal resumedRecord.MimeType singleRecord.MimeType "mime equal to the single-shot"
            Expect.equal resumedRecord.Status singleRecord.Status "ingestion reached the same terminal status"

            // And it is a real, servable item — not merely a record.
            let! ranged = lib.OpenRange(container, resumedRecord.Id, { Start = 0L; End = 2999L })
            let stream = Expect.wantOk ranged "open the committed original"
            Expect.equal (readAll stream) sessionPayload "the committed bytes, in order"
        }

        testCaseAsync "the session is gone once committed"
        <| async {
            let sessions, _ = fresh ()
            let! sessionId = begun sessions defaultOptions
            let! _ = sessions.AppendChunk(container, sessionId, 0L, sessionPayload)
            let! commit = sessions.CommitUpload(container, sessionId)
            Expect.isOk commit "commit"

            let! again = sessions.CommitUpload(container, sessionId)

            match again with
            | Error SessionNotFound -> ()
            | other -> failtestf "expected SessionNotFound after commit, got %A" other
        }

        testCaseAsync "a duplicate chunk is a no-op, not a double-append"
        <| async {
            let sessions, lib = fresh ()
            let! sessionId = begun sessions defaultOptions
            let! _ = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])

            let! dup = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])
            let progress = Expect.wantOk dup "duplicate chunk accepted idempotently"
            Expect.equal progress.ReceivedBytes 1000L "the cursor did not move"

            let! _ = sessions.AppendChunk(container, sessionId, 1000L, sessionChunks[1])
            let! _ = sessions.AppendChunk(container, sessionId, 2500L, sessionChunks[2])
            let! commit = sessions.CommitUpload(container, sessionId)
            let record = Expect.wantOk commit "commit"

            let! single = lib.Upload(container, request "video/mp4" sessionPayload)
            let singleRecord = Expect.wantOk single "single-shot"
            Expect.equal record.ContentHash singleRecord.ContentHash "the duplicate did not corrupt the object"
        }

        testCaseAsync "a chunk at the wrong offset is refused, and names the resume cursor"
        <| async {
            let sessions, _ = fresh ()
            let! sessionId = begun sessions defaultOptions
            let! _ = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])

            // A gap: the client skipped ahead.
            let! gap = sessions.AppendChunk(container, sessionId, 2500L, sessionChunks[2])

            match gap with
            | Error(OffsetMismatch(expected, received)) ->
                Expect.equal expected 1000L "the expected offset IS the resume cursor"
                Expect.equal received 2500L "the offset the client sent"
            | other -> failtestf "expected OffsetMismatch, got %A" other

            // Behind the cursor, but not a chunk we ever accepted at
            // that offset — a client rewriting history, not a retry.
            let! rewrite = sessions.AppendChunk(container, sessionId, 500L, sessionChunks[2])

            match rewrite with
            | Error(OffsetMismatch(expected, _)) -> Expect.equal expected 1000L "cursor unmoved"
            | other -> failtestf "expected OffsetMismatch for a mid-chunk rewrite, got %A" other
        }

        testCaseAsync "a chunk over the per-chunk cap is refused before a byte is written"
        <| async {
            let options = {
                MediaLibraryOptions.defaults with
                    MaxChunkBytes = 512
            }

            let sessions, _ = makeOver options fixedNow (makeStore ())
            let! opened = sessions.BeginUpload(container, declaration options (int64 sessionPayload.Length))
            let sessionId = Expect.wantOk opened "begin upload"

            let! oversize = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])

            match oversize with
            | Error(ChunkTooLarge(size, cap)) ->
                Expect.equal size 1000 "the chunk's size"
                Expect.equal cap 512 "the configured cap"
            | other -> failtestf "expected ChunkTooLarge, got %A" other

            // The cursor is untouched — a refused chunk wrote nothing.
            let! ok = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0][0..499])
            Expect.equal (Expect.wantOk ok "a within-cap chunk").ReceivedBytes 500L "cursor starts from zero"
        }

        testCaseAsync "a declaration over the deployment cap fails fast, before any chunk"
        <| async {
            let options = {
                MediaLibraryOptions.defaults with
                    MaxBytes = 2048L
            }

            let sessions, _ = makeOver options fixedNow (makeStore ())

            match MediaUploadDeclaration.create options "clip.mp4" "video/mp4" 4096L "user-1" None with
            | Ok _ -> failtest "a declaration above the cap must not construct"
            | Error(FileTooLarge(size, cap)) ->
                Expect.equal size 4096L "declared size"
                Expect.equal cap 2048L "deployment cap"
            | other -> failtestf "expected FileTooLarge, got %A" other

            // And an unsupported MIME is refused on the same edge.
            match MediaUploadDeclaration.create options "clip.bin" "application/x-evil" 1024L "user-1" None with
            | Error(UnsupportedMimeType m) -> Expect.equal m "application/x-evil" "the rejected mime"
            | other -> failtestf "expected UnsupportedMimeType, got %A" other

            // Nothing above reached the store, so nothing is open.
            let! orphan = sessions.AppendChunk(container, UploadSessionId "never-opened", 0L, sessionChunks[0])

            match orphan with
            | Error SessionNotFound -> ()
            | other -> failtestf "expected SessionNotFound, got %A" other
        }

        testCaseAsync "a chunk that would exceed the declared size is refused"
        <| async {
            let sessions, _ = fresh ()
            // Declare less than we will send.
            let! opened = sessions.BeginUpload(container, declaration defaultOptions 1200L)
            let sessionId = Expect.wantOk opened "begin upload"
            let! _ = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])

            let! over = sessions.AppendChunk(container, sessionId, 1000L, sessionChunks[1])

            match over with
            | Error(DeclaredSizeExceeded(attempted, declared)) ->
                Expect.equal attempted 2500L "what the append would have reached"
                Expect.equal declared 1200L "what was declared"
            | other -> failtestf "expected DeclaredSizeExceeded, got %A" other
        }

        testCaseAsync "committing early is refused and the session survives so the rest can be sent"
        <| async {
            let sessions, _ = fresh ()
            let! sessionId = begun sessions defaultOptions
            let! _ = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])

            let! early = sessions.CommitUpload(container, sessionId)

            match early with
            | Error(IncompleteUpload(received, declared)) ->
                Expect.equal received 1000L "bytes actually received"
                Expect.equal declared 3000L "bytes declared"
            | other -> failtestf "expected IncompleteUpload, got %A" other

            // Under-delivery is the one commit failure that must NOT
            // destroy the session — the client's remaining chunks are
            // still worth sending.
            let! _ = sessions.AppendChunk(container, sessionId, 1000L, sessionChunks[1])
            let! _ = sessions.AppendChunk(container, sessionId, 2500L, sessionChunks[2])
            let! commit = sessions.CommitUpload(container, sessionId)
            Expect.isOk commit "the session was still there"
        }

        testCaseAsync "an abandoned session disappears after its TTL"
        <| async {
            let clock = ref (System.DateTimeOffset(2026, 8, 28, 12, 0, 0, System.TimeSpan.Zero))

            let options = {
                MediaLibraryOptions.defaults with
                    UploadSessionTtl = System.TimeSpan.FromMinutes 30.0
            }

            let sessions, _ = makeOver options (fun () -> clock.Value) (makeStore ())
            let! opened = sessions.BeginUpload(container, declaration options (int64 sessionPayload.Length))
            let abandoned = Expect.wantOk opened "begin the session that will be abandoned"
            let! _ = sessions.AppendChunk(container, abandoned, 0L, sessionChunks[0])

            // Still live just inside the TTL: opening another session
            // sweeps, and must not take this one.
            clock.Value <- clock.Value.AddMinutes 20.0
            let! _ = sessions.BeginUpload(container, declaration options 1000L)
            let! live = sessions.AppendChunk(container, abandoned, 1000L, sessionChunks[1])
            Expect.isOk live "a session inside its TTL survives a sweep"

            // Past it. The sweep runs on the next BeginUpload — no
            // timer, no hosted service (GP 13).
            clock.Value <- clock.Value.AddMinutes 40.0
            let! _ = sessions.BeginUpload(container, declaration options 1000L)
            let! expired = sessions.AppendChunk(container, abandoned, 2500L, sessionChunks[2])

            match expired with
            | Error SessionNotFound -> ()
            | other -> failtestf "expected the expired session to be gone, got %A" other
        }

        testCaseAsync "another scope cannot append to, commit, or abort this scope's session"
        <| async {
            let sessions, _ = fresh ()
            let! sessionId = begun sessions defaultOptions
            let! _ = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])

            // The container is the isolation boundary (GP 4), so the
            // foreign scope cannot even address the session — every
            // verb answers as though it does not exist, which is the
            // honest answer: for that scope, it does not.
            let! foreignAppend = sessions.AppendChunk(otherContainer, sessionId, 1000L, sessionChunks[1])
            Expect.isError foreignAppend "a foreign scope's append is refused"

            let! foreignCommit = sessions.CommitUpload(otherContainer, sessionId)
            Expect.isError foreignCommit "a foreign scope's commit is refused"

            let! foreignAbort = sessions.AbortUpload(otherContainer, sessionId)
            Expect.isError foreignAbort "a foreign scope's abort is refused"

            // And none of that moved the owning scope's cursor.
            let! resume = sessions.AppendChunk(container, sessionId, 1000L, sessionChunks[1])
            Expect.equal (Expect.wantOk resume "owner resumes").ReceivedBytes 2500L "cursor unharmed"
        }

        testCaseAsync "abort discards the session and its chunks"
        <| async {
            let sessions, lib = fresh ()
            let! sessionId = begun sessions defaultOptions
            let! _ = sessions.AppendChunk(container, sessionId, 0L, sessionChunks[0])

            let! aborted = sessions.AbortUpload(container, sessionId)
            Expect.isOk aborted "abort"

            let! afterAppend = sessions.AppendChunk(container, sessionId, 1000L, sessionChunks[1])

            match afterAppend with
            | Error SessionNotFound -> ()
            | other -> failtestf "expected SessionNotFound after abort, got %A" other

            let! afterCommit = sessions.CommitUpload(container, sessionId)
            Expect.isError afterCommit "an aborted session cannot commit"

            // Nothing was ingested.
            let! items = lib.List(container, "", 0)
            Expect.isEmpty items "an aborted session leaves no media record"
        }
    ]

// ─── Phase 741 — the O(chunk) commit proof ───────────────────────────
//
// `uploadSessionTests` above is behavioural: it says what the committed
// record contains, never what committing it COST. Phase 741's whole
// claim is a cost claim — a commit on a store that can compose stored
// parts must peak at one chunk, not at the object — and a behavioural
// pack cannot fail when that regresses. This pack closes it the way
// Phase 468 closed the ranged-read claim: interpose a counting
// `IBlobStorage` and assert on the byte counts.
//
// The discriminator is `MaxUploadBytes`, and it is worth saying why,
// because the obvious one does not work. BOTH paths read the chunks one
// at a time — `walkChunks` is shared — so the largest single DOWNLOAD is
// one chunk either way and separates nothing. What differs is the
// write: the materialised path hands `IMediaLibrary.Upload` the
// assembled object, which reaches the store as one whole-object
// `Upload`; the streaming path hands over part NAMES and the store
// composes. The two paths are therefore separated by the largest single
// payload the store was ever given — which is exactly the quantity a
// deployment's heap cares about.
//
// Both halves are asserted, and the refusing half is not a degraded
// corner being tolerated: it is the shipped behaviour of every
// encrypted deployment and of every custom store that adopts the member
// by declining it.

/// Conformance for the commit COST over a store that composes and a
/// store that refuses. Same `makeOver` shape as `uploadSessionTests`,
/// so a caller binds both from one factory.
let streamingCommitTests
    (name: string)
    (makeStore: unit -> IBlobStorage)
    (makeOver:
        MediaLibraryOptions -> (unit -> System.DateTimeOffset) -> IBlobStorage -> IUploadSessionStore * IMediaLibrary)
    : Test =

    let defaultOptions = MediaLibraryOptions.defaults

    let fixedNow () =
        System.DateTimeOffset(2026, 8, 28, 12, 0, 0, System.TimeSpan.Zero)

    let largestChunk = sessionChunks |> Array.map Array.length |> Array.max

    /// Open a session and append every chunk. The caller zeroes the
    /// counters afterwards, so what is measured is the COMMIT and not
    /// the appends that preceded it.
    let uploadedSession (sessions: IUploadSessionStore) = async {
        let! opened = sessions.BeginUpload(container, declaration defaultOptions (int64 sessionPayload.Length))
        let sessionId = Expect.wantOk opened "begin upload"

        let mutable offset = 0L

        for chunk in sessionChunks do
            let! appended = sessions.AppendChunk(container, sessionId, offset, chunk)
            Expect.isOk appended "append"
            offset <- offset + int64 chunk.Length

        return sessionId
    }

    testList (sprintf "resumable commit cost (%s)" name) [

        testCaseAsync "a commit over a composing store never handles more than one chunk at a time"
        <| async {
            let counting = CountingBlobStorage(makeStore ())
            let sessions, _ = makeOver defaultOptions fixedNow (counting :> IBlobStorage)
            let! sessionId = uploadedSession sessions
            counting.Reset()

            let! commit = sessions.CommitUpload(container, sessionId)
            let record = Expect.wantOk commit "commit"

            Expect.equal counting.ComposeCalls 1 "assembly went through the compose seam exactly once"

            Expect.isLessThanOrEqual counting.MaxDownloadBytes largestChunk "no single read pulled more than one chunk"

            Expect.isLessThan
                counting.MaxUploadBytes
                sessionPayload.Length
                "the assembled object was never handed to the store as one payload — that is the O(chunk) claim"

            Expect.equal record.SizeBytes (int64 sessionPayload.Length) "the committed record is the whole object"
        }

        testCaseAsync "a commit over a refusing store materialises — the fallback, measured"
        <| async {
            let counting =
                CountingBlobStorage(ComposeRefusingBlobStorage(makeStore ()) :> IBlobStorage)

            let sessions, _ = makeOver defaultOptions fixedNow (counting :> IBlobStorage)
            let! sessionId = uploadedSession sessions
            counting.Reset()

            let! commit = sessions.CommitUpload(container, sessionId)
            let record = Expect.wantOk commit "commit over a refusing store still succeeds"

            Expect.equal
                counting.ComposeCalls
                0
                "a store that declares CanComposeFrom = false is never asked to compose"

            Expect.isGreaterThanOrEqual
                counting.MaxUploadBytes
                sessionPayload.Length
                "the fallback does hand the store the whole object — stated, not hidden"

            Expect.equal record.SizeBytes (int64 sessionPayload.Length) "the committed record is the whole object"
        }

        // The equality that makes the fast path safe to take: whichever
        // path a deployment lands on, the committed item is the same
        // item. Stated against the single-shot upload too, so this is
        // the 469 acceptance criterion extended to the new path rather
        // than a weaker claim about the two new paths agreeing with
        // each other.
        testCaseAsync "streamed and materialised commits are content-hash-equal, and equal to a single-shot upload"
        <| async {
            let sessionsA, libA = makeOver defaultOptions fixedNow (makeStore ())
            let! idA = uploadedSession sessionsA
            let! commitA = sessionsA.CommitUpload(container, idA)
            let streamed = Expect.wantOk commitA "streamed commit"

            let refusing = ComposeRefusingBlobStorage(makeStore ()) :> IBlobStorage
            let sessionsB, _ = makeOver defaultOptions fixedNow refusing
            let! idB = uploadedSession sessionsB
            let! commitB = sessionsB.CommitUpload(container, idB)
            let materialised = Expect.wantOk commitB "materialised commit"

            let! single = libA.Upload(container, request "video/mp4" sessionPayload)
            let singleRecord = Expect.wantOk single "single-shot upload of the same bytes"

            Expect.equal streamed.ContentHash materialised.ContentHash "streamed hash = materialised hash"
            Expect.equal streamed.ContentHash singleRecord.ContentHash "streamed hash = single-shot hash"
            Expect.equal streamed.SizeBytes materialised.SizeBytes "same size"
            Expect.equal streamed.MimeType materialised.MimeType "same MIME"
            Expect.equal streamed.OriginalFilename materialised.OriginalFilename "same filename"
            Expect.equal streamed.Status materialised.Status "same terminal ingestion status"
        }

        // The composed original must be the object, not merely
        // something of the right length: a compose that concatenated in
        // the wrong order passes every size assertion above.
        testCaseAsync "the streamed original round-trips byte-equal through the library"
        <| async {
            let sessions, lib = makeOver defaultOptions fixedNow (makeStore ())
            let! sessionId = uploadedSession sessions

            let! commit = sessions.CommitUpload(container, sessionId)
            let record = Expect.wantOk commit "commit"

            let! ranged =
                lib.OpenRange(
                    container,
                    record.Id,
                    {
                        Start = 0L
                        End = int64 sessionPayload.Length - 1L
                    }
                )

            let stream = Expect.wantOk ranged "open the whole composed original"
            Expect.sequenceEqual (readAll stream) sessionPayload "composed bytes are the payload, in order"
        }
    ]
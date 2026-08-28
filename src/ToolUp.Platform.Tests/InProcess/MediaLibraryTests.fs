// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MediaLibraryTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.MediaLibrary
open ToolUp.Platform.Tests.Contracts

// ─── Phase 88 — media library tests ───────────────────────────────────
//
// Three layers: the pure `ByteRange.parse` 206/416 decision, the pure
// `SignedUrl` mint/verify + expiry crypto, and the full `IMediaLibrary`
// contract pack run against `DefaultMediaLibrary` over an in-memory blob
// store.
//
// ─── Phase 468 — three more bindings ─────────────────────────────────
//
// The behavioural pack is bound TWICE: once over the ordinary store,
// and once over a store that refuses ranged reads, because the
// whole-object fallback is the shipped serving path for every encrypted
// deployment and "unchanged" is a claim that has to be run, not
// asserted. `rangedReadTests` adds the cost claim over the original,
// and `derivedRangeTests` below covers the HLS / poster seam — which
// needs a transcoder to have produced something, so it cannot live in
// the implementation-agnostic pack.

// ─── Test doubles ─────────────────────────────────────────────────────

type private NullLogger() =
    interface ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(_) = ()
        member _.Error(_, _) = ()

/// Trivial in-memory `ISecretStore` — mirrors the `ShareTokenStoreTests`
/// double; the signing key is generated on first use and persisted here.
type private InMemorySecretStore() =
    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(container, name) = async {
            match store.TryGetValue((container, name)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(container, name, value) = async {
            store[(container, name)] <- value
            return Ok()
        }

        member _.DeleteSecret(container, name) = async {
            store.TryRemove((container, name)) |> ignore
            return Ok()
        }

        member _.ListKeys(_) = async { return [] }

let private makeStore () : IBlobStorage =
    InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

let private makeLibraryOver
    (transcoder: IMediaTranscoder)
    (options: MediaLibraryOptions)
    (blob: IBlobStorage)
    : IMediaLibrary =
    let secrets = InMemorySecretStore() :> ISecretStore
    let signer = SignedUrl.MediaUrlSigner(secrets)

    DefaultMediaLibrary(blob, signer, NoopMediaDerivation.create (), transcoder, None, options, NullLogger())
    :> IMediaLibrary

let private makeLibrary () : IMediaLibrary =
    makeLibraryOver (NoopMediaTranscoder.create ()) MediaLibraryOptions.defaults (makeStore ())

/// Phase 468 — the same library over a store that refuses ranged reads
/// (the Phase 22 encryption decorator's shape), so the behavioural pack
/// runs against the whole-object fallback end to end.
let private makeRefusingLibrary () : IMediaLibrary =
    makeLibraryOver
        (NoopMediaTranscoder.create ())
        MediaLibraryOptions.defaults
        (IMediaLibraryContract.RangeRefusingBlobStorage(makeStore ()) :> IBlobStorage)

// ─── Pure ByteRange.parse — the 206 / 416 decision ────────────────────

let private rangeTests =
    testList "ByteRange.parse" [
        test "empty header → NoRange (serve 200)" { Expect.equal (ByteRange.parse "" 100L) NoRange "empty" }

        test "non-bytes unit → NoRange" { Expect.equal (ByteRange.parse "items=0-10" 100L) NoRange "unknown unit" }

        test "bytes=10-19 → Satisfiable 10..19" {
            Expect.equal (ByteRange.parse "bytes=10-19" 100L) (Satisfiable { Start = 10L; End = 19L }) "closed range"
        }

        test "bytes=10- → Satisfiable to end" {
            Expect.equal (ByteRange.parse "bytes=10-" 100L) (Satisfiable { Start = 10L; End = 99L }) "open-ended"
        }

        test "bytes=-20 → final 20 bytes" {
            Expect.equal (ByteRange.parse "bytes=-20" 100L) (Satisfiable { Start = 80L; End = 99L }) "suffix"
        }

        test "bytes=90-200 → end clamped to last byte" {
            Expect.equal (ByteRange.parse "bytes=90-200" 100L) (Satisfiable { Start = 90L; End = 99L }) "clamped"
        }

        test "bytes=100- (start == length) → Unsatisfiable (416)" {
            Expect.equal (ByteRange.parse "bytes=100-" 100L) RangeRequest.Unsatisfiable "start at length"
        }

        test "bytes=200-300 (start past end) → Unsatisfiable (416)" {
            Expect.equal (ByteRange.parse "bytes=200-300" 100L) RangeRequest.Unsatisfiable "fully past"
        }

        test "zero-length resource → Unsatisfiable" {
            Expect.equal (ByteRange.parse "bytes=0-10" 0L) RangeRequest.Unsatisfiable "empty resource"
        }

        test "Length member counts inclusive bytes" {
            Expect.equal ({ Start = 10L; End = 19L }: ByteRange).Length 10L "10 bytes"
        }
    ]

// ─── Pure SignedUrl crypto + expiry ───────────────────────────────────

let private fixedKey = Array.create 32 7uy

let private signScope: StorageScope = {
    ScopeId = "u1"
    Container = "user-u1"
    Persist = true
}

let private now = DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero)

let private signedUrlTests =
    testList "SignedUrl" [
        test "mint then verify round-trips the payload" {
            let token = SignedUrl.mint fixedKey (MediaId "abc") signScope (now.AddHours 1.0)

            match SignedUrl.verify fixedKey token now with
            | Ok payload ->
                Expect.equal payload.MediaId "abc" "media id"
                Expect.equal payload.ScopeId "u1" "scope id"
                Expect.equal payload.Container "user-u1" "container"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "an expired token fails with Expired" {
            let token = SignedUrl.mint fixedKey (MediaId "abc") signScope (now.AddMinutes 5.0)

            match SignedUrl.verify fixedKey token (now.AddHours 1.0) with
            | Error SignedUrlError.Expired -> ()
            | other -> failtestf "expected Expired, got %A" other
        }

        test "a token signed with another key fails verification" {
            let token = SignedUrl.mint fixedKey (MediaId "abc") signScope (now.AddHours 1.0)
            let otherKey = Array.create 32 9uy

            match SignedUrl.verify otherKey token now with
            | Error SignedUrlError.InvalidSignature -> ()
            | other -> failtestf "expected InvalidSignature, got %A" other
        }

        test "a tampered token fails verification" {
            let token = SignedUrl.mint fixedKey (MediaId "abc") signScope (now.AddHours 1.0)
            // Flip the final signature character.
            let tampered =
                token.Substring(0, token.Length - 1) + (if token.EndsWith "A" then "B" else "A")

            match SignedUrl.verify fixedKey tampered now with
            | Error _ -> ()
            | Ok _ -> failtest "tampered token must not verify"
        }

        test "a malformed token fails with Malformed" {
            match SignedUrl.verify fixedKey "not-a-token" now with
            | Error SignedUrlError.Malformed -> ()
            | other -> failtestf "expected Malformed, got %A" other
        }

        testCaseAsync "MediaUrlSigner sign/verify round-trips via the secret store"
        <| async {
            let signer = SignedUrl.MediaUrlSigner(InMemorySecretStore() :> ISecretStore)
            let! signed = signer.SignAsync(MediaId "xyz", signScope, TimeSpan.FromHours 1.0, now)
            let token = Expect.wantOk signed "sign"
            let! verified = signer.VerifyAsync(token, now)
            let payload = Expect.wantOk verified "verify"
            Expect.equal payload.MediaId "xyz" "round-trip media id"
        }
    ]

// ─── Phase 468 — the derived (HLS / poster) ranged path ───────────────

let private derivedContainer = "team-derived"

/// One segment big enough that "read the window" and "read the segment"
/// are different numbers.
let private segmentPayload = Array.init (64 * 1024) (fun i -> byte ((i * 7) % 251))

let private derivedChunkBytes = 4096

/// A transcoder that emits a small master manifest plus one large
/// segment. Deterministic and dependency-free — the point is to put a
/// derived blob in the store, not to transcode anything.
let private fakeHlsTranscoder =
    { new IMediaTranscoder with
        member _.Capabilities = {
            CanExtractPoster = false
            CanTranscodeHls = true
        }

        member _.TranscodeToHls(_, _) = async {
            return
                Ok [
                    {
                        BlobSuffix = "index.m3u8"
                        Bytes = Encoding.UTF8.GetBytes "#EXTM3U\n#EXT-X-VERSION:3\n"
                        MimeType = "application/vnd.apple.mpegurl"
                        RenditionName = "hls"
                        IsMasterManifest = true
                    }
                    {
                        BlobSuffix = "seg0.ts"
                        Bytes = segmentPayload
                        MimeType = "video/mp2t"
                        RenditionName = "hls"
                        IsMasterManifest = false
                    }
                ]
        }
    }

let private derivedUpload () =
    match
        MediaUploadRequest.create
            MediaLibraryOptions.defaults
            (Array.init 128 byte)
            "clip.mp4"
            "video/mp4"
            "user-1"
            None
    with
    | Ok r -> r
    | Error e -> failwithf "derived-path setup: invalid upload request %A" e

let private derivedRangeTests =
    testList "Phase 468 derived-path ranged reads" [
        testCaseAsync "an HLS segment window reads O(range), not O(segment)"
        <| async {
            let counting = IMediaLibraryContract.CountingBlobStorage(makeStore ())

            let options = {
                MediaLibraryOptions.defaults with
                    RangeChunkBytes = derivedChunkBytes
            }

            let lib = makeLibraryOver fakeHlsTranscoder options (counting :> IBlobStorage)
            let! upload = lib.Upload(derivedContainer, derivedUpload ())
            let record = Expect.wantOk upload "upload"
            counting.Reset()

            match box lib with
            | :? IMediaRangeReader as ranged ->
                let! total = ranged.DerivedContentLength(derivedContainer, record.Id, "seg0.ts")
                Expect.equal (Expect.wantOk total "derived length") (int64 segmentPayload.Length) "segment length"
                Expect.equal counting.TotalBytesRead 0L "a length question costs no bytes"

                let window = { Start = 20000L; End = 20999L }
                let! opened = ranged.OpenDerivedRange(derivedContainer, record.Id, "seg0.ts", window)
                let stream, mime = Expect.wantOk opened "open derived range"
                Expect.equal mime "video/mp2t" "content type inferred from the extension"

                use ms = new MemoryStream()
                stream.CopyTo ms
                stream.Dispose()
                Expect.equal (ms.ToArray()) segmentPayload[20000..20999] "the exact window, byte for byte"

                if counting.DownloadCalls <> 0 then
                    failtestf
                        "the derived ranged path must not Download the whole segment — saw %d"
                        counting.DownloadCalls

                let ceiling = window.Length + int64 derivedChunkBytes

                if counting.TotalBytesRead > ceiling then
                    failtestf
                        "serving a %d-byte segment window read %d bytes (ceiling %d)"
                        window.Length
                        counting.TotalBytesRead
                        ceiling
            | _ -> failtest "DefaultMediaLibrary must declare IMediaRangeReader"
        }

        testCaseAsync "the manifest still round-trips whole through OpenDerived"
        <| async {
            let lib =
                makeLibraryOver fakeHlsTranscoder MediaLibraryOptions.defaults (makeStore ())

            let! upload = lib.Upload(derivedContainer, derivedUpload ())
            let record = Expect.wantOk upload "upload"
            let! opened = lib.OpenDerived(derivedContainer, record.Id, "index.m3u8")
            let bytes, mime = Expect.wantOk opened "open derived"
            Expect.equal mime "application/vnd.apple.mpegurl" "manifest content type"
            Expect.stringContains (Encoding.UTF8.GetString bytes) "#EXTM3U" "manifest body"
        }

        testCaseAsync "directory traversal is refused on the ranged derived seam too"
        <| async {
            let lib =
                makeLibraryOver fakeHlsTranscoder MediaLibraryOptions.defaults (makeStore ())

            let! upload = lib.Upload(derivedContainer, derivedUpload ())
            let record = Expect.wantOk upload "upload"

            match box lib with
            | :? IMediaRangeReader as ranged ->
                let! len = ranged.DerivedContentLength(derivedContainer, record.Id, "../../records/x.json")

                match len with
                | Error MediaRangeError.NotFound -> ()
                | other -> failtestf "expected NotFound for a traversal path, got %A" other

                let! opened =
                    ranged.OpenDerivedRange(
                        derivedContainer,
                        record.Id,
                        "../../records/x.json",
                        { Start = 0L; End = 9L }
                    )

                match opened with
                | Error MediaRangeError.NotFound -> ()
                | Ok _ -> failtest "a traversal path must never open"
                | other -> failtestf "expected NotFound for a traversal path, got %A" other
            | _ -> failtest "DefaultMediaLibrary must declare IMediaRangeReader"
        }
    ]

// ─── Phase 469 — upload sessions over BlobUploadSessionStore ─────────
//
// Three claims the implementation-agnostic contract pack cannot make,
// because each is about `BlobUploadSessionStore` specifically: the
// notification stream a commit produces, the on-disk chunk layout, and
// what happens when the backing store does not isolate by container.

/// Records every publish. `Subscribe` / `Unsubscribe` are inert — the
/// question here is what the library PUBLISHED, and a real subscriber
/// would only re-derive it.
type private RecordingNotificationChannel() =
    let published =
        System.Collections.Concurrent.ConcurrentQueue<string * Notification>()

    member _.Published = published |> Seq.toList

    member this.CustomPayloads(key: string) =
        this.Published
        |> List.choose (fun (_, n) ->
            match n with
            | CustomNotification(k, json) when k = key -> Some json
            | _ -> None)

    member _.Clear() =
        while not published.IsEmpty do
            published.TryDequeue() |> ignore

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue((scopeId, notification)) }

        member _.Subscribe(_, _) = async { return Guid.NewGuid() }

        member _.Unsubscribe(_) = async { () }

/// A store that ignores the container argument entirely — every scope
/// shares one namespace. Not a realistic backend; it is the adversary
/// that proves scope isolation does not rest solely on the store doing
/// its job. `BlobUploadSessionStore` must still refuse a cross-scope
/// call here, from the container recorded in the session's own
/// manifest.
type private ContainerCollapsingBlobStorage(inner: IBlobStorage) =
    [<Literal>]
    let one = "collapsed"

    interface IBlobStorage with
        member _.Upload(_, blobName, content) = inner.Upload(one, blobName, content)
        member _.Download(_, blobName) = inner.Download(one, blobName)
        member _.Delete(_, blobName) = inner.Delete(one, blobName)
        member _.List(_, prefix) = inner.List(one, prefix)
        member _.Exists(_, blobName) = inner.Exists(one, blobName)
        member _.GetMetadata(_, blobName) = inner.GetMetadata(one, blobName)

        member _.Erase(_, prefix, policy, dryRun) =
            inner.Erase(one, prefix, policy, dryRun)

        member _.DownloadRange(_, blobName, offset, length) =
            inner.DownloadRange(one, blobName, offset, length)

let private makeSessionsOver
    (notifications: INotificationChannel option)
    (options: MediaLibraryOptions)
    (now: unit -> DateTimeOffset)
    (blob: IBlobStorage)
    : IUploadSessionStore * IMediaLibrary =
    let secrets = InMemorySecretStore() :> ISecretStore
    let signer = SignedUrl.MediaUrlSigner(secrets)

    let lib =
        DefaultMediaLibrary(
            blob,
            signer,
            NoopMediaDerivation.create (),
            NoopMediaTranscoder.create (),
            notifications,
            options,
            NullLogger()
        )
        :> IMediaLibrary

    let sessions =
        BlobUploadSessionStore(blob, lib, notifications, options, NullLogger(), now) :> IUploadSessionStore

    sessions, lib

let private sessionContainer = "team-sessions"

let private sessionPayload = Array.init 3000 (fun i -> byte (i % 251))

let private sessionDeclaration (options: MediaLibraryOptions) (size: int64) =
    match MediaUploadDeclaration.create options "clip.mp4" "video/mp4" size "user-1" (Some "A clip") with
    | Ok d -> d
    | Error e -> failwithf "session setup: invalid declaration %A" e

let private uploadSessionImplTests =
    testList "Phase 469 upload sessions (BlobUploadSessionStore)" [

        testCaseAsync "a committed session produces the same ingestion-status stream as a single-shot upload"
        <| async {
            let channel = RecordingNotificationChannel()

            let sessions, lib =
                makeSessionsOver
                    (Some(channel :> INotificationChannel))
                    MediaLibraryOptions.defaults
                    (fun () -> DateTimeOffset.UtcNow)
                    (makeStore ())

            // Baseline: what a single-shot upload of these bytes says.
            let single =
                match
                    MediaUploadRequest.create
                        MediaLibraryOptions.defaults
                        sessionPayload
                        "clip.mp4"
                        "video/mp4"
                        "user-1"
                        (Some "A clip")
                with
                | Ok r -> r
                | Error e -> failwithf "setup: %A" e

            let! _ = lib.Upload(sessionContainer, single)
            let singleShotStatuses = channel.CustomPayloads "MediaLibrary.IngestionStatus"
            channel.Clear()

            // The resumed path, in three chunks with one retry.
            let! opened = sessions.BeginUpload(sessionContainer, sessionDeclaration MediaLibraryOptions.defaults 3000L)
            let sessionId = Expect.wantOk opened "begin"
            let! _ = sessions.AppendChunk(sessionContainer, sessionId, 0L, sessionPayload[0..999])
            let! _ = sessions.AppendChunk(sessionContainer, sessionId, 1000L, sessionPayload[1000..2499])
            let! _ = sessions.AppendChunk(sessionContainer, sessionId, 1000L, sessionPayload[1000..2499])
            let! _ = sessions.AppendChunk(sessionContainer, sessionId, 2500L, sessionPayload[2500..2999])
            let! commit = sessions.CommitUpload(sessionContainer, sessionId)
            Expect.isOk commit "commit"

            let resumedStatuses = channel.CustomPayloads "MediaLibrary.IngestionStatus"

            // Same number of transitions, same status tokens in the
            // same order. The ids differ, so compare the tokens.
            let tokensOf (payloads: string list) =
                payloads
                |> List.map (fun json ->
                    let parsed = System.Text.Json.JsonDocument.Parse json

                    parsed.RootElement.GetProperty("Status").GetString())

            Expect.equal
                (tokensOf resumedStatuses)
                (tokensOf singleShotStatuses)
                "the committed session walks the same ingestion status sequence"

            // And the resumable path publishes its own progress stream.
            let progress = channel.CustomPayloads "MediaLibrary.UploadProgress"
            Expect.isNonEmpty progress "upload progress is published over INotificationChannel"

            let phases =
                progress
                |> List.map (fun json ->
                    System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("Phase").GetString())

            Expect.contains phases UploadSessionPhase.committed "the commit is announced"
        }

        testCaseAsync "a commit whose assembled bytes exceed the declaration fails closed and destroys the session"
        <| async {
            let blob = makeStore ()

            let sessions, lib =
                makeSessionsOver None MediaLibraryOptions.defaults (fun () -> DateTimeOffset.UtcNow) blob

            let! opened = sessions.BeginUpload(sessionContainer, sessionDeclaration MediaLibraryOptions.defaults 1000L)
            let sessionId = Expect.wantOk opened "begin"
            let! _ = sessions.AppendChunk(sessionContainer, sessionId, 0L, sessionPayload[0..999])

            // Smuggle a chunk past `AppendChunk`'s declared-size guard
            // by writing it straight into the documented layout — the
            // only way to reach the commit-time check, which exists
            // precisely because the append guard is not the only way
            // bytes can arrive.
            let rogueName =
                sprintf "media/uploads/%s/chunks/%s" (UploadSessionId.value sessionId) ((1000L).ToString("D20"))

            let! _ = blob.Upload(sessionContainer, rogueName, sessionPayload[1000..1499])

            let! commit = sessions.CommitUpload(sessionContainer, sessionId)

            match commit with
            | Error(DeclaredSizeExceeded(actual, declared)) ->
                Expect.equal actual 1500L "the bytes actually present"
                Expect.equal declared 1000L "the declaration"
            | other -> failtestf "expected DeclaredSizeExceeded, got %A" other

            // Fails CLOSED: the session is gone, so a client cannot
            // retry its way past the declaration.
            let! retry = sessions.CommitUpload(sessionContainer, sessionId)

            match retry with
            | Error SessionNotFound -> ()
            | other -> failtestf "expected the session to be destroyed, got %A" other

            let! items = lib.List(sessionContainer, "", 0)
            Expect.isEmpty items "nothing was ingested"

            let! leftovers = blob.List(sessionContainer, sprintf "media/uploads/%s/" (UploadSessionId.value sessionId))

            Expect.isEmpty leftovers "the session's chunks are gone"
        }

        testCaseAsync "a store that does not isolate by container still cannot be crossed"
        <| async {
            let collapsing = ContainerCollapsingBlobStorage(makeStore ()) :> IBlobStorage

            let sessions, _ =
                makeSessionsOver None MediaLibraryOptions.defaults (fun () -> DateTimeOffset.UtcNow) collapsing

            let! opened = sessions.BeginUpload(sessionContainer, sessionDeclaration MediaLibraryOptions.defaults 3000L)
            let sessionId = Expect.wantOk opened "begin"
            let! _ = sessions.AppendChunk(sessionContainer, sessionId, 0L, sessionPayload[0..999])

            // The foreign scope CAN now read the manifest blob — the
            // store handed it over. The refusal has to come from the
            // container recorded inside it.
            let! foreign = sessions.AppendChunk("team-intruder", sessionId, 1000L, sessionPayload[1000..2499])

            match foreign with
            | Error SessionScopeMismatch -> ()
            | other -> failtestf "expected SessionScopeMismatch, got %A" other

            let! foreignCommit = sessions.CommitUpload("team-intruder", sessionId)

            match foreignCommit with
            | Error SessionScopeMismatch -> ()
            | other -> failtestf "expected SessionScopeMismatch on commit, got %A" other
        }
    ]

[<Tests>]
let tests =
    testList "MediaLibrary (Phase 88)" [
        rangeTests
        signedUrlTests
        IMediaLibraryContract.tests "DefaultMediaLibrary" makeLibrary
        // Phase 468 — the same behavioural bar over the whole-object
        // fallback: the 206 / 416 matrix must be unchanged for a store
        // that refuses ranged reads.
        IMediaLibraryContract.tests "DefaultMediaLibrary (range-refusing store)" makeRefusingLibrary
        IMediaLibraryContract.rangedReadTests
            "DefaultMediaLibrary"
            makeStore
            (makeLibraryOver (NoopMediaTranscoder.create ()))
        derivedRangeTests
        // Phase 469 — the resume matrix, bound implementation-agnostically…
        IMediaLibraryContract.uploadSessionTests "BlobUploadSessionStore" makeStore (makeSessionsOver None)
        // …and the three claims that are about this implementation.
        uploadSessionImplTests
    ]
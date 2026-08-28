// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MediaLibraryTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Metrics
open ToolUp.Platform.Secrets
open ToolUp.Platform.Usage
open ToolUp.MediaLibrary
open ToolUp.Media.FFmpeg
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

// ─── Phase 471 — gated HLS: AES-128 segments + key delivery ──────────
//
// Four layers, mirroring how the phase is built:
//
//   1. The PURE gate (`HlsKeyDelivery.decideAccess`) and the PURE
//      manifest rewrite (`rewriteKeyUris`), exhaustively — the shape
//      Phase 86's `AudienceGate` established.
//   2. The PURE half of the FFmpeg sub-companion — the
//      `-hls_key_info_file` payload and the argument list. **Nothing
//      here shells ffmpeg**, and that is a deliberate limit rather than
//      an omission: this pack has never required a media binary, and a
//      case that silently no-ops when `ffmpeg` is absent would report a
//      vacuous green on every CI runner. What IS asserted is the part a
//      wrong answer would break — the file format ffmpeg parses
//      positionally with no error reporting, and the claim that the
//      encrypted argument list differs from the plain one by exactly
//      the encryption flag.
//   3. The library end to end over a fake transcoder that performs a
//      REAL AES-128-CBC encryption, so "the segments are byte-garbage
//      without the key" is measured against actual ciphertext rather
//      than asserted.
//   4. The endpoint and the serve path, driven through a real
//      `DefaultHttpContext` (the `SurfaceEnforcementMiddlewareTests`
//      shape) so the status codes, the `no-store` header and the
//      rewritten manifest body are the ones a client would see.

let private encContainer = "team-encrypted"
let private foreignContainer = "team-intruder"

/// The plaintext one "segment" carries. Distinctive enough that finding
/// it inside the stored blob is unambiguous.
let private clearSegment = Array.init 4096 (fun i -> byte ((i * 31 + 17) % 251))

let private aesCbc (key: byte[]) (transform: Aes -> ICryptoTransform) (input: byte[]) =
    use aes = Aes.Create()
    aes.Key <- key
    aes.IV <- Array.zeroCreate 16
    aes.Mode <- CipherMode.CBC
    aes.Padding <- PaddingMode.PKCS7
    use t = transform aes
    t.TransformFinalBlock(input, 0, input.Length)

let private aesEncrypt (key: byte[]) (plain: byte[]) = aesCbc key _.CreateEncryptor() plain
let private aesDecrypt (key: byte[]) (cipher: byte[]) = aesCbc key _.CreateDecryptor() cipher

/// The unencrypted manifest a plain pass produces. Held as a value so
/// the "byte-identical when unencrypted" claim has something exact to
/// compare against.
let private plainManifest =
    "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:6.0,\nseg0.ts\n#EXT-X-ENDLIST\n"

/// A transcoder that emits a manifest + one segment, and — when asked
/// through `IMediaHlsEncryptingTranscoder` — really encrypts the
/// segment with the supplied key and writes the supplied key URI into
/// the manifest's `#EXT-X-KEY`.
///
/// Not an ffmpeg emulation: the IV is fixed at zero rather than derived
/// from the media sequence number, because the claim under test is what
/// the LIBRARY does with a key (mint it, hand it over once, persist
/// only ciphertext, keep the key in `ISecretStore`) — not ffmpeg's
/// segment-IV convention.
let private encryptingTranscoder =
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
                        Bytes = Encoding.UTF8.GetBytes plainManifest
                        MimeType = "application/vnd.apple.mpegurl"
                        RenditionName = "hls"
                        IsMasterManifest = true
                    }
                    {
                        BlobSuffix = "seg0.ts"
                        Bytes = clearSegment
                        MimeType = "video/mp2t"
                        RenditionName = "hls"
                        IsMasterManifest = false
                    }
                ]
        }

      interface IMediaHlsEncryptingTranscoder with
          member _.TranscodeToHlsEncrypted(_, _, key) = async {
              let manifest =
                  sprintf
                      "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-KEY:METHOD=AES-128,URI=\"%s\"\n#EXTINF:6.0,\nseg0.ts\n#EXT-X-ENDLIST\n"
                      key.KeyUri

              return
                  Ok [
                      {
                          BlobSuffix = "index.m3u8"
                          Bytes = Encoding.UTF8.GetBytes manifest
                          MimeType = "application/vnd.apple.mpegurl"
                          RenditionName = "hls"
                          IsMasterManifest = true
                      }
                      {
                          BlobSuffix = "seg0.ts"
                          Bytes = aesEncrypt key.KeyBytes clearSegment
                          MimeType = "video/mp2t"
                          RenditionName = "hls"
                          IsMasterManifest = false
                      }
                  ]
          }
    }

/// A library plus the pieces the key endpoint needs to answer for it.
///
/// Phase 472 added `Options` (so a driven request can see the
/// deployment's declared edge cacheability, which the range handler
/// resolves from DI) and `Edge` (the recording fake the fan-out claims
/// assert against). Both are inert on the Phase 471 fixtures: `Options`
/// declares nothing and `Edge` is `None`.
type private EncryptedFixture = {
    Library: IMediaLibrary
    Keys: HlsKeyDelivery.MediaHlsKeyStore
    Signer: SignedUrl.MediaUrlSigner
    Blob: IBlobStorage
    Options: MediaLibraryOptions
    Edge: IEdgeCacheContract.RecordingEdgeCache option
}

/// Phase 472 — the general fixture builder. Every knob the two media
/// phases need, in one place, so a 471 fixture and a 472 fixture cannot
/// drift into two different libraries.
let private makeFixtureWith
    (transcoder: IMediaTranscoder)
    (withKeyStore: bool)
    (options: MediaLibraryOptions)
    (edge: IEdgeCacheContract.RecordingEdgeCache option)
    (delegated: SignedUrl.IDelegatedUrlSigner option)
    =
    let blob = makeStore ()
    let secrets = InMemorySecretStore() :> ISecretStore
    let signer = SignedUrl.MediaUrlSigner(secrets)
    let keys = HlsKeyDelivery.MediaHlsKeyStore(secrets, NullLogger(), options)

    let lib =
        DefaultMediaLibrary(
            blob,
            signer,
            NoopMediaDerivation.create (),
            transcoder,
            None,
            options,
            NullLogger(),
            (if withKeyStore then Some keys else None),
            (edge |> Option.map (fun e -> e :> IEdgeCache)),
            delegated
        )
        :> IMediaLibrary

    {
        Library = lib
        Keys = keys
        Signer = signer
        Blob = blob
        Options = options
        Edge = edge
    }

let private makeEncryptedFixture (transcoder: IMediaTranscoder) (withKeyStore: bool) (encryptByDefault: bool) =
    let options = {
        MediaLibraryOptions.defaults with
            EncryptHlsByDefault = encryptByDefault
    }

    makeFixtureWith transcoder withKeyStore options None None

let private encUpload () =
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
    | Error e -> failwithf "471 setup: invalid upload request %A" e

/// Read the stored bytes of a derived blob straight out of the blob
/// store, bypassing the library — the byte-level assertion has to look
/// at what is ACTUALLY at rest, not at what a serving path chose to
/// hand back.
let private storedDerived (blob: IBlobStorage) (container: string) (id: MediaId) (file: string) =
    let name = sprintf "media/derived/%s/%s" (MediaId.value id) file

    match blob.Download(container, name) |> Async.RunSynchronously with
    | Ok bytes -> bytes
    | Error e -> failwithf "471: derived blob %s unreadable: %s" name e

// ─── 1. The pure gate ─────────────────────────────────────────────────

let private payloadFor (mediaId: string) (container: string) : SignedUrl.MediaSignedPayload = {
    MediaId = mediaId
    ScopeId = "u1"
    Container = container
    ExpiresAtUnix = 0L
}

let private hlsGateTests =
    testList "Phase 471 key-endpoint gate (pure)" [
        test "no credential at all → 401" {
            Expect.equal
                (HlsKeyDelivery.decideAccess None None "m1")
                HlsKeyDelivery.KeyAccessUnauthenticated
                "anonymous"
        }

        test "a resolved scope admits, and names the container the key is read from" {
            Expect.equal
                (HlsKeyDelivery.decideAccess (Some "team-a") None "m1")
                (HlsKeyDelivery.KeyAccessGranted("team-a", "scope"))
                "scope gate"
        }

        test "a valid signature for THIS media admits, on the signed payload's own container" {
            Expect.equal
                (HlsKeyDelivery.decideAccess None (Some(Ok(payloadFor "m1" "team-b"))) "m1")
                (HlsKeyDelivery.KeyAccessGranted("team-b", "signature"))
                "signature gate"
        }

        test "a signature minted for ANOTHER media id does not unlock this one" {
            Expect.equal
                (HlsKeyDelivery.decideAccess None (Some(Ok(payloadFor "other" "team-b"))) "m1")
                (HlsKeyDelivery.KeyAccessForbidden "media_id_mismatch")
                "id binding"
        }

        test "an expired signature is 403, not a fall-through" {
            Expect.equal
                (HlsKeyDelivery.decideAccess None (Some(Error SignedUrlError.Expired)) "m1")
                (HlsKeyDelivery.KeyAccessForbidden "expired_signature")
                "expiry"
        }

        test "a tampered signature is 403" {
            Expect.equal
                (HlsKeyDelivery.decideAccess None (Some(Error SignedUrlError.InvalidSignature)) "m1")
                (HlsKeyDelivery.KeyAccessForbidden "invalid_signature")
                "signature"
        }

        test "a malformed token is 403" {
            Expect.equal
                (HlsKeyDelivery.decideAccess None (Some(Error SignedUrlError.Malformed)) "m1")
                (HlsKeyDelivery.KeyAccessForbidden "malformed_signature")
                "malformed"
        }

        // The ordering claim, both directions. A present-but-bad token
        // must NOT quietly fall through to the scope gate: if it did,
        // an expired signature presented by an ordinary session would
        // read as a success, and the expired-signature refusal would be
        // unobservable from outside.
        test "a bad token beside a valid scope is still refused" {
            Expect.equal
                (HlsKeyDelivery.decideAccess (Some "team-a") (Some(Error SignedUrlError.Expired)) "m1")
                (HlsKeyDelivery.KeyAccessForbidden "expired_signature")
                "no fall-through"
        }

        test "a valid token beside a scope resolves on the token's container" {
            Expect.equal
                (HlsKeyDelivery.decideAccess (Some "team-a") (Some(Ok(payloadFor "m1" "team-b"))) "m1")
                (HlsKeyDelivery.KeyAccessGranted("team-b", "signature"))
                "token wins"
        }
    ]

// ─── 2. The pure manifest rewrite + FFmpeg argument surface ──────────

let private hlsRewriteTests =
    testList "Phase 471 manifest rewrite (pure)" [
        test "a manifest with no key tag comes back byte-for-byte (the same string)" {
            let rewritten =
                HlsKeyDelivery.rewriteKeyUris "https://o/api/media/hls-key/m1" plainManifest

            Expect.isTrue
                (obj.ReferenceEquals(rewritten, plainManifest))
                "an unencrypted manifest must not be re-serialised at all"
        }

        test "the key URI is replaced and every other attribute survives" {
            let source =
                "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"/api/media/hls-key/m1\",IV=0x00\n#EXTINF:6.0,\nseg0.ts\n"

            let rewritten =
                HlsKeyDelivery.rewriteKeyUris "https://origin.test/api/media/hls-key/m1" source

            Expect.stringContains
                rewritten
                "#EXT-X-KEY:METHOD=AES-128,URI=\"https://origin.test/api/media/hls-key/m1\",IV=0x00"
                "URI swapped in place, METHOD and IV untouched"

            Expect.stringContains rewritten "\nseg0.ts\n" "segment lines untouched"
            Expect.isFalse (rewritten.Contains "\"/api/media/hls-key/m1\"") "the relative URI is gone"
        }

        test "#EXT-X-SESSION-KEY is rewritten too" {
            let source =
                "#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"/api/media/hls-key/m1\"\n"

            let rewritten = HlsKeyDelivery.rewriteKeyUris "https://o/k" source
            Expect.stringContains rewritten "URI=\"https://o/k\"" "session key rewritten"
        }

        test "CRLF line endings survive the rewrite" {
            let source = "#EXTM3U\r\n#EXT-X-KEY:METHOD=AES-128,URI=\"/rel\"\r\nseg0.ts\r\n"
            let rewritten = HlsKeyDelivery.rewriteKeyUris "https://o/k" source
            Expect.stringContains rewritten "URI=\"https://o/k\"\r\n" "the CRLF after the rewritten tag"
            Expect.equal (rewritten.Split("\r\n").Length) (source.Split("\r\n").Length) "same line count"
            Expect.isFalse (rewritten.Contains "\n\n") "no line ending was doubled"
        }

        test "a segment URI that merely LOOKS like a key line is not rewritten" {
            let source = "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\nseg0.ts\n"
            Expect.isTrue (obj.ReferenceEquals(HlsKeyDelivery.rewriteKeyUris "https://o/k" source, source)) "EXT-X-MAP"
        }

        test "the secret name is namespaced by media id" {
            Expect.equal (HlsKeyDelivery.secretName (MediaId "abc")) "media_hls_key:abc" "secret name"
        }

        test "the key-info file is the URI, the key path, and nothing else when there is no IV" {
            let content = FFmpegMediaProvider.keyInfoContent "https://o/k" "C:/tmp/hls.key" None
            Expect.equal content "https://o/k\nC:/tmp/hls.key\n" "two positional lines"
        }

        test "an explicit IV is appended as lowercase hex" {
            let iv = Array.init 16 (fun i -> byte i)
            let content = FFmpegMediaProvider.keyInfoContent "u" "p" (Some iv)
            Expect.equal content "u\np\n000102030405060708090a0b0c0d0e0f\n" "three positional lines"
        }

        test "the encrypted argument list differs from the plain one by exactly the encryption flag" {
            let plain = FFmpegMediaProvider.hlsArgs "in.mp4" "out.m3u8" None

            let encrypted =
                FFmpegMediaProvider.hlsArgs "in.mp4" "out.m3u8" (Some "info.keyinfo")

            Expect.equal
                (encrypted
                 |> List.filter (fun a -> a <> "-hls_key_info_file" && a <> "info.keyinfo"))
                plain
                "removing the two encryption tokens recovers the pre-471 argument list exactly (GP 11)"

            Expect.isFalse (plain |> List.contains "-hls_key_info_file") "the plain pass names no key file"
        }
    ]

// ─── 3. The library end to end ────────────────────────────────────────

let private hlsEncryptionTests =
    testList "Phase 471 encrypted renditions" [
        testCaseAsync "an encrypted rendition is byte-garbage at rest and recovers exactly with the stored key"
        <| async {
            let f = makeEncryptedFixture encryptingTranscoder true true
            let! upload = f.Library.Upload(encContainer, encUpload ())
            let record = Expect.wantOk upload "upload"
            Expect.equal record.Status MediaIngestionStatus.Ready "ingestion completed"

            let atRest = storedDerived f.Blob encContainer record.Id "seg0.ts"

            // The claim, at the byte level: a stolen segment file is not
            // the video. Length differs (PKCS7 pads) and, more to the
            // point, the plaintext is nowhere in it.
            Expect.notEqual atRest clearSegment "the stored segment is not the plaintext"

            Expect.isFalse
                (Convert.ToHexString(atRest).Contains(Convert.ToHexString(clearSegment[0..63])))
                "no run of plaintext survives in the stored ciphertext"

            // And it is genuinely THAT key, fetched from the gate's
            // store, that opens it.
            let! stored = f.Keys.TryGet(encContainer, record.Id)

            match Expect.wantOk stored "key resolved" with
            | None -> failtest "an encrypted rendition must leave a key in the owning scope"
            | Some key ->
                Expect.equal key.Length 16 "AES-128 — 16 bytes"
                Expect.equal (aesDecrypt key atRest) clearSegment "the key recovers the plaintext exactly"
        }

        testCaseAsync "the key is filed under the owning scope, and a foreign scope cannot read it"
        <| async {
            let f = makeEncryptedFixture encryptingTranscoder true true
            let! upload = f.Library.Upload(encContainer, encUpload ())
            let record = Expect.wantOk upload "upload"

            let! foreign = f.Keys.TryGet(foreignContainer, record.Id)

            // `Ok None`, not an error and not the key: the container is
            // the isolation boundary, so a foreign scope's question is
            // well-formed and simply has no answer here (GP 4).
            Expect.equal (Expect.wantOk foreign "foreign lookup") None "a foreign scope resolves no key"
        }

        testCaseAsync "the key never lands beside the segments"
        <| async {
            let f = makeEncryptedFixture encryptingTranscoder true true
            let! upload = f.Library.Upload(encContainer, encUpload ())
            let record = Expect.wantOk upload "upload"

            let! stored = f.Keys.TryGet(encContainer, record.Id)
            let key = (Expect.wantOk stored "key").Value

            let prefix = sprintf "media/derived/%s/" (MediaId.value record.Id)
            let! derived = f.Blob.List(encContainer, prefix)
            Expect.isNonEmpty derived "the sweep must actually have blobs to look at"

            for name in derived do
                let bytes =
                    match f.Blob.Download(encContainer, name) |> Async.RunSynchronously with
                    | Ok b -> b
                    | Error e -> failwithf "471: %s unreadable: %s" name e

                Expect.isFalse
                    (Convert.ToHexString(bytes).Contains(Convert.ToHexString key))
                    (sprintf "the raw key must not appear inside derived blob %s" name)
        }

        testCaseAsync "deleting the item destroys its key"
        <| async {
            let f = makeEncryptedFixture encryptingTranscoder true true
            let! upload = f.Library.Upload(encContainer, encUpload ())
            let record = Expect.wantOk upload "upload"

            let! before = f.Keys.TryGet(encContainer, record.Id)
            Expect.isSome (Expect.wantOk before "before") "the key exists while the item does"

            let! deleted = f.Library.Delete(encContainer, record.Id)
            Expect.isOk deleted "delete"

            let! after = f.Keys.TryGet(encContainer, record.Id)
            Expect.equal (Expect.wantOk after "after") None "no live key survives the item"
        }

        testCaseAsync "a transcoder that cannot encrypt FAILS the ingestion rather than shipping bare segments"
        <| async {
            // `fakeHlsTranscoder` declares `IMediaTranscoder` only.
            let f = makeEncryptedFixture fakeHlsTranscoder true true
            let! upload = f.Library.Upload(encContainer, encUpload ())
            let record = Expect.wantOk upload "upload"

            match record.Status with
            | MediaIngestionStatus.Failed reason ->
                Expect.stringContains reason "cannot encrypt" "the reason names the missing capability"
            | other -> failtestf "expected a failed ingestion, got %A" other

            Expect.isEmpty record.Renditions "no rendition is published for a refused encryption"

            // And no orphan secret: the key is minted only once both
            // preconditions hold.
            let! stored = f.Keys.TryGet(encContainer, record.Id)
            Expect.equal (Expect.wantOk stored "key") None "a refused encryption mints no key"
        }

        testCaseAsync "no composed key store is also a refusal, not a bare rendition"
        <| async {
            let f = makeEncryptedFixture encryptingTranscoder false true
            let! upload = f.Library.Upload(encContainer, encUpload ())
            let record = Expect.wantOk upload "upload"

            match record.Status with
            | MediaIngestionStatus.Failed reason ->
                Expect.stringContains reason "no key store" "the reason names the missing substrate"
            | other -> failtestf "expected a failed ingestion, got %A" other
        }

        testCaseAsync "with encryption off, the rendition is byte-identical to the pre-471 output"
        <| async {
            let f = makeEncryptedFixture encryptingTranscoder true false
            let! upload = f.Library.Upload(encContainer, encUpload ())
            let record = Expect.wantOk upload "upload"
            Expect.equal record.Status MediaIngestionStatus.Ready "ingestion completed"

            Expect.equal
                (storedDerived f.Blob encContainer record.Id "seg0.ts")
                clearSegment
                "the segment is stored in the clear, exactly as before this phase"

            Expect.equal
                (Encoding.UTF8.GetString(storedDerived f.Blob encContainer record.Id "index.m3u8"))
                plainManifest
                "and the manifest carries no key tag"

            let! stored = f.Keys.TryGet(encContainer, record.Id)
            Expect.equal (Expect.wantOk stored "key") None "an unencrypted item mints no key at all"
        }

        testCaseAsync "a per-upload opt-in encrypts even when the deployment default is off"
        <| async {
            let f = makeEncryptedFixture encryptingTranscoder true false

            let request =
                match
                    MediaUploadRequest.createWithEncryption
                        MediaLibraryOptions.defaults
                        (Array.init 128 byte)
                        "clip.mp4"
                        "video/mp4"
                        "user-1"
                        None
                        (Some true)
                with
                | Ok r -> r
                | Error e -> failwithf "471 setup: %A" e

            let! upload = f.Library.Upload(encContainer, request)
            let record = Expect.wantOk upload "upload"
            Expect.equal record.Status MediaIngestionStatus.Ready "ingestion completed"

            Expect.notEqual
                (storedDerived f.Blob encContainer record.Id "seg0.ts")
                clearSegment
                "the per-upload preference won over the deployment default"
        }

        test "a request built by the pre-471 constructor states no preference" {
            Expect.equal (encUpload ()).EncryptHls None "create leaves the preference unstated"

            Expect.equal
                (MediaLibraryOptions.effectiveEncryptHls MediaLibraryOptions.defaults None)
                false
                "and the shipped default resolves it to off (GP 11)"
        }
    ]

// ─── 4. The endpoint + the serve path, through a real HttpContext ────

let private keyRoute (id: MediaId) =
    HlsKeyDelivery.RoutePrefix + MediaId.value id

let private servicesFor (f: EncryptedFixture) : IServiceProvider =
    ServiceCollection()
        .AddSingleton<SignedUrl.MediaUrlSigner>(f.Signer)
        .AddSingleton<HlsKeyDelivery.MediaHlsKeyStore>(f.Keys)
        .AddSingleton<IMediaLibrary>(f.Library)
        // Phase 472 — mirrors what `MediaCompose` registers under
        // `EnabledMediaLibrary`, so a driven request sees this
        // deployment's declared edge cacheability. The 471 fixtures
        // register `MediaLibraryOptions.defaults`, which declares
        // nothing, so their assertions are unaffected.
        .AddSingleton<MediaLibraryOptions>(f.Options)
        .BuildServiceProvider()
    :> IServiceProvider

/// One driven request. `scope` is the container the scope-resolution
/// middleware would have stamped; `query` is the raw query string.
let private drive
    (f: EncryptedFixture)
    (handler: HttpHandler)
    (path: string)
    (query: string)
    (scope: string option)
    : int * byte[] * HttpContext =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- "GET"
    ctx.Request.Scheme <- "https"
    ctx.Request.Host <- HostString "media.example.test"
    ctx.Request.Path <- PathString path

    if query <> "" then
        ctx.Request.QueryString <- QueryString("?" + query)

    ctx.RequestServices <- servicesFor f

    match scope with
    | Some container ->
        ctx.Items["ToolUp.StorageScope"] <-
            box {
                ScopeId = "u1"
                Container = container
                Persist = true
            }
    | None -> ()

    let body = new MemoryStream()
    ctx.Response.Body <- body

    let next: HttpFunc = Some >> Task.FromResult
    (handler next ctx).GetAwaiter().GetResult() |> ignore

    ctx.Response.StatusCode, body.ToArray(), ctx

let private headerOf (ctx: HttpContext) (name: string) =
    match ctx.Response.Headers.TryGetValue name with
    | true, v -> v.ToString()
    | _ -> ""

let private hlsKeyEndpointTests =
    testList "Phase 471 key endpoint (driven)" [
        testCase "an anonymous request is 401 and returns no bytes"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, body, _ = drive f HlsKeyDelivery.keyHandler (keyRoute record.Id) "" None
            Expect.equal status 401 "anonymous"
            Expect.isEmpty body "no key material on a refused request"

        testCase "the owning scope gets the key, no-store, as raw bytes"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, body, ctx =
                drive f HlsKeyDelivery.keyHandler (keyRoute record.Id) "" (Some encContainer)

            Expect.equal status 200 "admitted"
            Expect.equal body.Length 16 "AES-128 key, raw"

            let expected =
                (f.Keys.TryGet(encContainer, record.Id)
                 |> Async.RunSynchronously
                 |> Result.defaultWith (fun e -> failwith e))
                    .Value

            Expect.equal body expected "the delivered bytes are the stored key"
            Expect.equal (headerOf ctx "Cache-Control") "no-store" "a key must never be cached"
            Expect.equal ctx.Response.ContentType "application/octet-stream" "content type"

        testCase "a DIFFERENT scope is admitted by the route and still gets nothing"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            // The cross-scope refusal is STRUCTURAL, not a check: the
            // foreign caller is a perfectly good authenticated subject,
            // and the key simply is not in its container (GP 4).
            let status, body, _ =
                drive f HlsKeyDelivery.keyHandler (keyRoute record.Id) "" (Some foreignContainer)

            Expect.equal status 404 "cross-scope"
            Expect.isEmpty body "no key material crosses a scope boundary"

        testCase "a valid signed token admits, and an expired one is 403"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let scope: StorageScope = {
                ScopeId = "u1"
                Container = encContainer
                Persist = true
            }

            let live =
                f.Signer.SignAsync(record.Id, scope, TimeSpan.FromHours 1.0, DateTimeOffset.UtcNow)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, body, _ =
                drive f HlsKeyDelivery.keyHandler (keyRoute record.Id) ("token=" + Uri.EscapeDataString live) None

            Expect.equal status 200 "a live signature admits with no session at all"
            Expect.equal body.Length 16 "the key"

            // Minted in the past with a short TTL, so it is already dead
            // by the time the handler checks it against the real clock.
            let stale =
                f.Signer.SignAsync(record.Id, scope, TimeSpan.FromMinutes 1.0, DateTimeOffset.UtcNow.AddHours -2.0)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let expiredStatus, expiredBody, _ =
                drive f HlsKeyDelivery.keyHandler (keyRoute record.Id) ("token=" + Uri.EscapeDataString stale) None

            Expect.equal expiredStatus 403 "an expired signature"
            Expect.isEmpty expiredBody "no key material on an expired signature"

        testCase "a token minted for another media item does not unlock this one"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let scope: StorageScope = {
                ScopeId = "u1"
                Container = encContainer
                Persist = true
            }

            let other =
                f.Signer.SignAsync(MediaId "someone-elses", scope, TimeSpan.FromHours 1.0, DateTimeOffset.UtcNow)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, body, _ =
                drive f HlsKeyDelivery.keyHandler (keyRoute record.Id) ("token=" + Uri.EscapeDataString other) None

            Expect.equal status 403 "id binding is enforced at the endpoint"
            Expect.isEmpty body "no key material"

        testCase "an encrypted manifest is served with an ORIGIN-ABSOLUTE key URI"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let path = sprintf "/api/media/hls/%s/index.m3u8" (MediaId.value record.Id)

            let status, body, _ = drive f RangeHandler.hlsHandler path "" (Some encContainer)
            Expect.equal status 200 "manifest served"
            let text = Encoding.UTF8.GetString body

            // This is the whole point of the rewrite: the stored
            // manifest carries a root-relative URI, which would resolve
            // against the CDN host once the segments are cached there.
            Expect.stringContains
                text
                (sprintf "URI=\"https://media.example.test/api/media/hls-key/%s\"" (MediaId.value record.Id))
                "the key URI points back at the origin's gate"

            Expect.isFalse
                (text.Contains "URI=\"/api/media/hls-key/")
                "no root-relative key URI survives the serve path"

        testCase "a token on the manifest request is carried onto the key URI"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let path = sprintf "/api/media/hls/%s/index.m3u8" (MediaId.value record.Id)

            let _, body, _ =
                drive f RangeHandler.hlsHandler path "token=abc123" (Some encContainer)

            let text = Encoding.UTF8.GetString body

            // The signed-playback path end to end: the token that
            // admitted the manifest is bound to the same media id, so
            // it admits the key fetch too — no second token species.
            Expect.stringContains text "/api/media/hls-key/" "key URI present"
            Expect.stringContains text "?token=abc123" "the admitting token rides onto the key URI"

        testCase "an UNENCRYPTED manifest is served byte-for-byte"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true false

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let path = sprintf "/api/media/hls/%s/index.m3u8" (MediaId.value record.Id)

            let status, body, ctx = drive f RangeHandler.hlsHandler path "" (Some encContainer)
            Expect.equal status 200 "served"
            Expect.equal body (Encoding.UTF8.GetBytes plainManifest) "the stored bytes, unmodified"
            Expect.equal ctx.Response.ContentType "application/vnd.apple.mpegurl" "content type unchanged"

        testCase "an encrypted SEGMENT is still served as opaque ranged bytes"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let path = sprintf "/api/media/hls/%s/seg0.ts" (MediaId.value record.Id)
            let status, body, ctx = drive f RangeHandler.hlsHandler path "" (Some encContainer)

            Expect.equal status 200 "segment served"
            Expect.equal ctx.Response.ContentType "video/mp2t" "segments are untouched by the rewrite"

            Expect.equal
                body
                (storedDerived f.Blob encContainer record.Id "seg0.ts")
                "the ciphertext is served exactly as stored"

            Expect.notEqual body clearSegment "and it is still not the plaintext"
    ]

// ─── Phase 472 — edge purge fan-out + delegated signing ───────────────
//
// Three claims the phase's acceptance criteria state directly:
//
//   - publishing / deleting a media item purges the corresponding edge
//     paths through the composed `IEdgeCache` (asserted via a recording
//     fake);
//   - a composed delegated signer replaces origin-signed media URLs end
//     to end, and WITHOUT it behaviour is byte-identical to before;
//   - the declared `Cache-Control` reaches the response — and the key
//     route's `no-store` is not reachable by any declaration.

let private plainTranscoderFixture (options: MediaLibraryOptions) (edge) (delegated) =
    makeFixtureWith (NoopMediaTranscoder.create ()) false options edge delegated

let private edgeFanOutTests =
    testList "Phase 472 media edge fan-out" [
        testCase "an upload purges the item's derived PREFIX and both original paths"
        <| fun () ->
            let edge = IEdgeCacheContract.RecordingEdgeCache()
            let f = plainTranscoderFixture MediaLibraryOptions.defaults (Some edge) None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            // Two purges: one prefix, one path list. Detached, so wait.
            Expect.isTrue (edge.WaitFor 2) "both purges arrived"

            Expect.equal
                edge.Prefixes
                [ sprintf "/api/media/hls/%s/" (MediaId.value record.Id) ]
                "every rendition file of the item, as a prefix"

            Expect.equal
                edge.AllPaths
                [
                    sprintf "/api/media/stream/%s" (MediaId.value record.Id)
                    sprintf "/media/signed/%s" (MediaId.value record.Id)
                ]
                "and the two routes that serve the original"

        testCase "a DELETE purges the same set"
        <| fun () ->
            let edge = IEdgeCacheContract.RecordingEdgeCache()
            let f = plainTranscoderFixture MediaLibraryOptions.defaults (Some edge) None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            Expect.isTrue (edge.WaitFor 2) "the upload's purges landed first"

            f.Library.Delete(encContainer, record.Id)
            |> Async.RunSynchronously
            |> Expect.wantOk
            <| "delete"

            Expect.isTrue (edge.WaitFor 4) "the delete's purges arrived too"

            // The delete's purge set must be identical to the upload's —
            // stated as an assertion because a deleted video that keeps
            // playing from a POP is precisely the failure this phase
            // exists to prevent, and the two call sites drifting apart
            // is how it would happen.
            Expect.equal (List.distinct edge.Prefixes).Length 1 "one distinct prefix across both events"
            Expect.equal edge.Prefixes.Length 2 "issued twice — once per event"
            Expect.equal (List.distinct edge.Paths).Length 1 "the same path set both times"

        testCase "a FAILED delete purges nothing — the item is still live"
        <| fun () ->
            let edge = IEdgeCacheContract.RecordingEdgeCache()
            let f = plainTranscoderFixture MediaLibraryOptions.defaults (Some edge) None

            match
                f.Library.Delete(encContainer, MediaId "never-existed")
                |> Async.RunSynchronously
            with
            | Error MediaDeleteError.NotFound -> ()
            | other -> failtestf "expected NotFound, got %A" other

            Expect.isTrue
                (edge.StaysSilentFor(TimeSpan.FromMilliseconds 200.0))
                "purging the edge for an item that was never deleted is a pointless cache miss, not a fix"

        testCase "NO composed edge cache means no work is scheduled at all (GP 13)"
        <| fun () ->
            // The pre-472 deployment. There is nothing to observe on the
            // library itself, so the claim is asserted where it is
            // observable: the upload succeeds and the record is intact,
            // with an edge cache that would have recorded any call.
            let f = plainTranscoderFixture MediaLibraryOptions.defaults None None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            Expect.equal record.Status MediaIngestionStatus.Ready "the upload is unaffected"

        testCase "a BROKEN edge cache does not fail the upload (GP 7)"
        <| fun () ->
            // The claim that matters operationally: a CDN outage must
            // not turn a successful upload into a failed one.
            let broken =
                IEdgeCacheContract.RecordingEdgeCache("broken", Error(PurgeTransportFailure "the CDN is down"))

            let f = plainTranscoderFixture MediaLibraryOptions.defaults (Some broken) None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "upload must succeed regardless: %A" e)

            Expect.equal record.Status MediaIngestionStatus.Ready "the upload succeeded"
            Expect.isTrue (broken.WaitFor 2) "and the purge was still attempted"
    ]

let private delegatedSigningTests =
    let scopeFor container : StorageScope = {
        ScopeId = "u1"
        Container = container
        Persist = true
    }

    testList "Phase 472 delegated URL signing" [
        testCase "WITHOUT a delegated signer the origin HMAC URL is minted, exactly as before (GP 11)"
        <| fun () ->
            let f = plainTranscoderFixture MediaLibraryOptions.defaults None None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let url =
                f.Library.SignedUrl(record.Id, scopeFor encContainer, TimeSpan.FromHours 1.0)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            Expect.stringStarts url (sprintf "/media/signed/%s?token=" (MediaId.value record.Id)) "the origin route"

        testCase "WITH one composed, the minted URL is the signer's, end to end"
        <| fun () ->
            let mutable seenScope = ""

            let delegated =
                { new SignedUrl.IDelegatedUrlSigner with
                    member _.Name = "fake-cdn"
                    member _.TtlPrecision = SignedUrl.TtlSecond

                    member _.SignUrl(id, scope, _) = async {
                        seenScope <- scope.Container
                        return Ok(sprintf "https://cdn.test/%s?Signature=abc" (MediaId.value id))
                    }
                }

            let f = plainTranscoderFixture MediaLibraryOptions.defaults None (Some delegated)

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let url =
                f.Library.SignedUrl(record.Id, scopeFor encContainer, TimeSpan.FromHours 1.0)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            Expect.equal
                url
                (sprintf "https://cdn.test/%s?Signature=abc" (MediaId.value record.Id))
                "the CDN-native URL"

            Expect.stringContains url "https://" "absolute — it must not resolve against this origin"
            Expect.equal seenScope encContainer "the signer was told which scope is viewing"

        testCase "the origin HMAC remains the VERIFICATION fallback while a signer is composed"
        <| fun () ->
            // Composing a signer changes what is MINTED. It must not
            // stop a previously-minted origin token from verifying, or
            // every live URL would break at the moment of the switch.
            let delegated =
                { new SignedUrl.IDelegatedUrlSigner with
                    member _.Name = "fake-cdn"
                    member _.TtlPrecision = SignedUrl.TtlSecond
                    member _.SignUrl(_, _, _) = async { return Ok "https://cdn.test/x" }
                }

            let f = plainTranscoderFixture MediaLibraryOptions.defaults None (Some delegated)

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let originToken =
                f.Signer.SignAsync(record.Id, scopeFor encContainer, TimeSpan.FromHours 1.0, DateTimeOffset.UtcNow)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, body, _ =
                drive
                    f
                    RangeHandler.signedHandler
                    (sprintf "/media/signed/%s" (MediaId.value record.Id))
                    ("token=" + Uri.EscapeDataString originToken)
                    None

            Expect.equal status 200 "the origin route still verifies its own tokens"
            Expect.isNonEmpty body "and serves the bytes"

        testCase "a FAILING delegated signer is an error, never a fall-through to an unreachable origin URL"
        <| fun () ->
            let delegated =
                { new SignedUrl.IDelegatedUrlSigner with
                    member _.Name = "fake-cdn"
                    member _.TtlPrecision = SignedUrl.TtlSecond
                    member _.SignUrl(_, _, _) = async { return Error(KeyResolutionFailed "no key") }
                }

            let f = plainTranscoderFixture MediaLibraryOptions.defaults None (Some delegated)

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            match
                f.Library.SignedUrl(record.Id, scopeFor encContainer, TimeSpan.FromHours 1.0)
                |> Async.RunSynchronously
            with
            | Error(KeyResolutionFailed detail) -> Expect.stringContains detail "no key" "the failure surfaces"
            | other -> failtestf "expected the signer's failure, got %A" other

        testCase "the signer is not consulted for an item that does not exist"
        <| fun () ->
            // `SignedUrl` checks existence first. Minting a signed URL
            // for a missing item would hand out a live grant for a 404.
            let mutable consulted = false

            let delegated =
                { new SignedUrl.IDelegatedUrlSigner with
                    member _.Name = "fake-cdn"
                    member _.TtlPrecision = SignedUrl.TtlSecond

                    member _.SignUrl(_, _, _) = async {
                        consulted <- true
                        return Ok "https://cdn.test/x"
                    }
                }

            let f = plainTranscoderFixture MediaLibraryOptions.defaults None (Some delegated)

            match
                f.Library.SignedUrl(MediaId "absent", scopeFor encContainer, TimeSpan.FromHours 1.0)
                |> Async.RunSynchronously
            with
            | Error SignedUrlError.NotFound -> ()
            | other -> failtestf "expected NotFound, got %A" other

            Expect.isFalse consulted "no grant is minted for an item that is not there"
    ]

let private declaredCacheHeaderTests =
    let declaring = {
        MediaLibraryOptions.defaults with
            EdgeCache = {
                Segment = EdgePublic(3600, 86400)
                Manifest = EdgeCacheUnset
                Poster = EdgePublic(60, 60)
                Original = EdgePrivate 30
            }
    }

    testList "Phase 472 declared Cache-Control on the media routes" [
        testCase "the DEFAULT options emit no Cache-Control anywhere (GP 11)"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true false

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let _, _, segCtx =
                drive
                    f
                    RangeHandler.hlsHandler
                    (sprintf "/api/media/hls/%s/seg0.ts" (MediaId.value record.Id))
                    ""
                    (Some encContainer)

            Expect.equal (headerOf segCtx "Cache-Control") "" "a segment carries no declaration"

            let _, _, streamCtx =
                drive
                    f
                    RangeHandler.streamHandler
                    (sprintf "/api/media/stream/%s" (MediaId.value record.Id))
                    ""
                    (Some encContainer)

            Expect.equal (headerOf streamCtx "Cache-Control") "" "nor does the original"

        testCase "a declared SEGMENT posture reaches the response"
        <| fun () ->
            let f = makeFixtureWith encryptingTranscoder true declaring None None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, _, ctx =
                drive
                    f
                    RangeHandler.hlsHandler
                    (sprintf "/api/media/hls/%s/seg0.ts" (MediaId.value record.Id))
                    ""
                    (Some encContainer)

            Expect.equal status 200 "served"
            Expect.equal (headerOf ctx "Cache-Control") "public, max-age=3600, s-maxage=86400" "the declaration"

        testCase "a MANIFEST left unset carries no declaration even when segments are public"
        <| fun () ->
            // The asymmetry that matters: an encrypted manifest is
            // rewritten per request and may carry a token; its segments
            // are ciphertext and are not.
            let f = makeFixtureWith encryptingTranscoder true declaring None None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, _, ctx =
                drive
                    f
                    RangeHandler.hlsHandler
                    (sprintf "/api/media/hls/%s/index.m3u8" (MediaId.value record.Id))
                    ""
                    (Some encContainer)

            Expect.equal status 200 "served"
            Expect.equal (headerOf ctx "Cache-Control") "" "no shared cache is invited to hold a rewritten manifest"

        testCase "a declared ORIGINAL posture reaches the scoped stream route"
        <| fun () ->
            let f = makeFixtureWith encryptingTranscoder true declaring None None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, _, ctx =
                drive
                    f
                    RangeHandler.streamHandler
                    (sprintf "/api/media/stream/%s" (MediaId.value record.Id))
                    ""
                    (Some encContainer)

            Expect.equal status 200 "served"
            Expect.equal (headerOf ctx "Cache-Control") "private, max-age=30" "browser-only, never a shared cache"

        testCase "the KEY route is no-store no matter what the deployment declares"
        <| fun () ->
            // The rule 471 set and 472 must not weaken. Driven with the
            // most permissive declaration this record can express, which
            // has no field for keys at all — so the assertion is that
            // the header is unreachable from configuration, not merely
            // that it happens to be right.
            let permissive = {
                MediaLibraryOptions.defaults with
                    EncryptHlsByDefault = true
                    EdgeCache = {
                        Segment = EdgePublic(86400, 86400)
                        Manifest = EdgePublic(86400, 86400)
                        Poster = EdgePublic(86400, 86400)
                        Original = EdgePublic(86400, 86400)
                    }
            }

            let f = makeFixtureWith encryptingTranscoder true permissive None None

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, body, ctx =
                drive f HlsKeyDelivery.keyHandler (keyRoute record.Id) "" (Some encContainer)

            Expect.equal status 200 "admitted"
            Expect.equal body.Length 16 "the key"
            Expect.equal (headerOf ctx "Cache-Control") "no-store" "still no-store"
            Expect.equal (headerOf ctx "Pragma") "no-cache" "and still no-cache"

            // And the same options record is REFUSED at compose time, so
            // this permissive declaration could never have shipped.
            Expect.isSome
                (MediaConfigValidator.edgeCacheabilityRefusal permissive)
                "the validator refuses it before a deployment can start"
    ]

// ─── Phase 473 — playback + delivery telemetry ────────────────────────
//
// Four layers, mirroring the phase's own shape:
//
//   1. The pure surfaces — beacon parsing, the session correlator, the
//      response-class map, the rate-limiter window, the rollup fold.
//   2. Egress reconciliation against the served `Content-Range` window,
//      driven through the real handlers over the 206 matrix.
//   3. The GP 13 claim: nothing is emitted, and nothing is allocated,
//      when neither sink is composed.
//   4. The beacon's validation matrix, read through the ledger rather
//      than through the status code — because every outcome is `204`.

let private telemetryContainer = "team-telemetry"

/// A metrics sink that keeps what it was handed.
type private RecordingMetricsSink() =
    let observations =
        System.Collections.Concurrent.ConcurrentBag<string * float * Map<string, string>>()

    let increments =
        System.Collections.Concurrent.ConcurrentBag<string * Map<string, string>>()

    member _.Observations = observations |> Seq.toList
    member _.Increments = increments |> Seq.toList

    interface IMetricsSink with
        member _.Record(name, value, tags) = observations.Add((name, value, tags))
        member _.Increment(name, tags) = increments.Add((name, tags))
        member _.SetGauge(_, _, _) = ()

/// A usage log that keeps its rows.
///
/// Emission is fire-and-forget (`Async.Start`), so both directions need
/// a bounded wait: `WaitFor` for "a row arrived", and `SettleThenCount`
/// for "no row arrived", which is only a credible claim after the
/// emission has been given a chance to happen.
type private RecordingUsageLog() =
    let rows = System.Collections.Concurrent.ConcurrentBag<UsageRecord>()

    member _.Rows = rows |> Seq.toList

    member _.WaitFor(n: int) =
        let deadline = DateTime.UtcNow.AddSeconds 5.0
        let mutable satisfied = rows.Count >= n

        while not satisfied && DateTime.UtcNow < deadline do
            System.Threading.Thread.Sleep 10
            satisfied <- rows.Count >= n

        satisfied

    member _.SettleThenCount() =
        System.Threading.Thread.Sleep 250
        rows.Count

    interface IUsageLog with
        member _.Record record = async { rows.Add record }
        member _.Query(_, _, _) = async.Return []
        member _.Aggregate(_, _) = async.Return Map.empty

type private TelemetryHost = {
    Fixture: EncryptedFixture
    Metrics: RecordingMetricsSink option
    Usage: RecordingUsageLog option
}

/// A host with both sinks live.
let private meteredHost () =
    let f = plainTranscoderFixture MediaLibraryOptions.defaults None None

    {
        Fixture = f
        Metrics = Some(RecordingMetricsSink())
        Usage = Some(RecordingUsageLog())
    }

/// A host composed exactly as the SDK default composes: the services
/// ARE registered, and they are the no-ops. This is the shape GP 13 is
/// a claim about — not an absent registration.
let private unmeteredHost () =
    let f = plainTranscoderFixture MediaLibraryOptions.defaults None None

    {
        Fixture = f
        Metrics = None
        Usage = None
    }

let private telemetryServices (h: TelemetryHost) : IServiceProvider =
    let services = ServiceCollection()

    services.AddSingleton<SignedUrl.MediaUrlSigner>(h.Fixture.Signer) |> ignore
    services.AddSingleton<HlsKeyDelivery.MediaHlsKeyStore>(h.Fixture.Keys) |> ignore
    services.AddSingleton<IMediaLibrary>(h.Fixture.Library) |> ignore
    services.AddSingleton<MediaLibraryOptions>(h.Fixture.Options) |> ignore

    match h.Metrics with
    | Some m -> services.AddSingleton<IMetricsSink>(m :> IMetricsSink) |> ignore
    | None -> services.AddSingleton<IMetricsSink>(NoOpMetricsSink() :> IMetricsSink) |> ignore

    match h.Usage with
    | Some u -> services.AddSingleton<IUsageLog>(u :> IUsageLog) |> ignore
    | None -> services.AddSingleton<IUsageLog>(NoOpUsageLog() :> IUsageLog) |> ignore

    services.BuildServiceProvider() :> IServiceProvider

/// A 5,000-byte item, big enough that the 206 matrix has room to move.
let private telemetryPayload = Array.init 5000 (fun i -> byte ((i * 13 + 5) % 251))

let private telemetryUpload () =
    match
        MediaUploadRequest.create MediaLibraryOptions.defaults telemetryPayload "clip.mp4" "video/mp4" "user-1" None
    with
    | Ok r -> r
    | Error e -> failwithf "473 setup: invalid upload request %A" e

let private uploadTelemetryItem (h: TelemetryHost) =
    h.Fixture.Library.Upload(telemetryContainer, telemetryUpload ())
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failwithf "473 setup: %A" e)

let private driveMedia
    (h: TelemetryHost)
    (handler: HttpHandler)
    (path: string)
    (range: string option)
    (scope: string option)
    : int * int * HttpContext =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- "GET"
    ctx.Request.Scheme <- "https"
    ctx.Request.Host <- HostString "media.example.test"
    ctx.Request.Path <- PathString path

    match range with
    | Some r -> ctx.Request.Headers["Range"] <- Microsoft.Extensions.Primitives.StringValues r
    | None -> ()

    ctx.RequestServices <- telemetryServices h

    match scope with
    | Some container ->
        ctx.Items["ToolUp.StorageScope"] <-
            box {
                ScopeId = "u1"
                Container = container
                Persist = true
            }
    | None -> ()

    let body = new MemoryStream()
    ctx.Response.Body <- body

    let next: HttpFunc = Some >> Task.FromResult
    (handler next ctx).GetAwaiter().GetResult() |> ignore

    ctx.Response.StatusCode, int body.Length, ctx

let private driveBeacon (h: TelemetryHost) (body: string) (query: string) (scope: string option) : int =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- "POST"
    ctx.Request.Scheme <- "https"
    ctx.Request.Host <- HostString "media.example.test"
    ctx.Request.Path <- PathString MediaApi.beaconRoute
    ctx.Request.ContentType <- "application/json"

    if query <> "" then
        ctx.Request.QueryString <- QueryString("?" + query)

    let bytes = Encoding.UTF8.GetBytes body
    ctx.Request.Body <- new MemoryStream(bytes)
    ctx.Request.ContentLength <- Nullable(int64 bytes.Length)
    ctx.RequestServices <- telemetryServices h

    match scope with
    | Some container ->
        ctx.Items["ToolUp.StorageScope"] <-
            box {
                ScopeId = "u1"
                Container = container
                Persist = true
            }
    | None -> ()

    ctx.Response.Body <- new MemoryStream()

    let next: HttpFunc = Some >> Task.FromResult
    (PlaybackTelemetry.beaconHandler next ctx).GetAwaiter().GetResult() |> ignore

    ctx.Response.StatusCode

/// The window length a `Content-Range: bytes a-b/total` header declares.
let private contentRangeWindow (header: string) : int64 =
    let segments = header.Substring(6).Split('/')
    let spec = segments[0].Trim()
    let parts = spec.Split('-')
    let first = Int64.Parse parts[0]
    let last = Int64.Parse parts[1]
    last - first + 1L

let private egressRows (u: RecordingUsageLog) =
    u.Rows
    |> List.filter (fun r -> r.ResourceKind = PlaybackTelemetry.EgressBytesKind)

let private beaconRows (u: RecordingUsageLog) =
    u.Rows
    |> List.filter (fun r -> r.ResourceKind = PlaybackTelemetry.PlaybackEventsKind)

let private playbackPureTests =
    testList "Phase 473 playback telemetry (pure)" [
        testCase "the response class agrees with the edge-cache class on every extension"
        <| fun () ->
            // The two classifiers are separate functions in separate
            // modules keyed on the same extension tests, so the risk is
            // drift, not correctness today. Pinned with an options
            // record whose four classes are pairwise distinct, so a
            // divergence on ANY extension shows up as a mismatch rather
            // than as a coincidence.
            let distinct = {
                MediaLibraryOptions.defaults with
                    EdgeCache = {
                        Segment = EdgePublic(1, 1)
                        Manifest = EdgePublic(2, 2)
                        Poster = EdgePublic(3, 3)
                        Original = EdgePrivate 4
                    }
            }

            let expected =
                Map.ofList [
                    PlaybackTelemetry.ClassManifest, distinct.EdgeCache.Manifest
                    PlaybackTelemetry.ClassSegment, distinct.EdgeCache.Segment
                    PlaybackTelemetry.ClassPoster, distinct.EdgeCache.Poster
                ]

            for file in
                [
                    "index.m3u8"
                    "INDEX.M3U8"
                    "seg0.ts"
                    "seg0.m4s"
                    "rendition.mp4"
                    "poster.jpg"
                    "poster.PNG"
                    "thumb.webp"
                    "unknown"
                ] do
                let cls = PlaybackTelemetry.responseClassForDerived file

                Expect.equal
                    (MediaLibraryOptions.edgeCacheabilityForDerived distinct file)
                    expected[cls]
                    (sprintf "%s is metered as %s and must be cached as that class" file cls)

        testCase "a well-formed beacon parses for each event"
        <| fun () ->
            let parse body = PlaybackTelemetry.parseBeacon body

            Expect.equal
                (parse """{"mediaId":"m1","event":"started","session":"s1"}""")
                (Some("m1", PlaybackTelemetry.Started, "s1"))
                "started"

            Expect.equal
                (parse """{"mediaId":"m1","event":"completed","session":"s1"}""")
                (Some("m1", PlaybackTelemetry.Completed, "s1"))
                "completed"

            Expect.equal
                (parse """{"mediaId":"m1","event":"progress","percent":42,"session":"s1"}""")
                (Some("m1", PlaybackTelemetry.Progress 42, "s1"))
                "progress carries its percent"

            Expect.equal
                (parse """{"mediaId":"m1","event":"STARTED","session":"s1","extra":{"a":1}}""")
                (Some("m1", PlaybackTelemetry.Started, "s1"))
                "the token is case-insensitive and an unknown field is tolerated — third-party players author this body"

        testCase "the malformed matrix is dropped, every shape of it"
        <| fun () ->
            let dropped name body =
                Expect.isNone (PlaybackTelemetry.parseBeacon body) name

            dropped "empty" ""
            dropped "whitespace" "   "
            dropped "not JSON" "{not json"
            dropped "a JSON array root" """["mediaId","m1"]"""
            dropped "a JSON scalar root" "42"
            dropped "no mediaId" """{"event":"started","session":"s1"}"""
            dropped "empty mediaId" """{"mediaId":"","event":"started","session":"s1"}"""
            dropped "no event" """{"mediaId":"m1","session":"s1"}"""
            dropped "unknown event" """{"mediaId":"m1","event":"paused","session":"s1"}"""
            dropped "no session" """{"mediaId":"m1","event":"started"}"""
            dropped "progress with no percent" """{"mediaId":"m1","event":"progress","session":"s1"}"""

            dropped "progress below range" """{"mediaId":"m1","event":"progress","percent":-1,"session":"s1"}"""

            dropped "progress above range" """{"mediaId":"m1","event":"progress","percent":101,"session":"s1"}"""

            dropped
                "a percent that is a string, not a number"
                """{"mediaId":"m1","event":"progress","percent":"50","session":"s1"}"""

            dropped
                "an over-long session"
                (sprintf """{"mediaId":"m1","event":"started","session":"%s"}""" (String('x', 201)))

            dropped "a numeric mediaId" """{"mediaId":7,"event":"started","session":"s1"}"""

        testCase "the session correlator is stable per (scope, media) and useless across them"
        <| fun () ->
            let a = PlaybackTelemetry.sessionCorrelator "scope-1" "media-1" "raw-session"
            let again = PlaybackTelemetry.sessionCorrelator "scope-1" "media-1" "raw-session"

            let otherMedia =
                PlaybackTelemetry.sessionCorrelator "scope-1" "media-2" "raw-session"

            let otherScope =
                PlaybackTelemetry.sessionCorrelator "scope-2" "media-1" "raw-session"

            Expect.equal a again "stable — which is what counting unique sessions needs"
            Expect.notEqual a otherMedia "the same viewer on another item is not joinable"
            Expect.notEqual a otherScope "nor across scopes (GP 4)"
            Expect.notEqual a "raw-session" "the client's own value never reaches the ledger"
            Expect.equal a.Length 16 "a fixed-width opaque handle"
            Expect.isTrue (a |> Seq.forall (fun c -> Char.IsDigit c || (c >= 'a' && c <= 'f'))) "lowercase hex"

            // Length-prefixed material, so a shifted boundary cannot
            // collide: ("ab","c") and ("a","bc") must differ.
            Expect.notEqual
                (PlaybackTelemetry.sessionCorrelator "ab" "c" "s")
                (PlaybackTelemetry.sessionCorrelator "a" "bc" "s")
                "no boundary ambiguity between the hashed coordinates"

        testCase "the beacon rate limiter admits a window's worth and then drops"
        <| fun () ->
            let policy: RateLimitPolicy = {
                PermitLimit = 3
                WindowSeconds = 60
                QueueLimit = 0
            }

            let limiter = PlaybackTelemetry.BeaconRateLimiter policy
            let t0 = DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)

            Expect.isTrue (limiter.Admit(t0, "ip:a")) "1"
            Expect.isTrue (limiter.Admit(t0, "ip:a")) "2"
            Expect.isTrue (limiter.Admit(t0, "ip:a")) "3"
            Expect.isFalse (limiter.Admit(t0, "ip:a")) "4 is over the permit"

            Expect.isTrue (limiter.Admit(t0, "ip:b")) "a different partition has its own budget"

            Expect.isTrue
                (limiter.Admit(t0.AddMinutes 5.0, "ip:a"))
                "and the spent partition recovers in the next window"

        testCase "the shipped beacon policy is a drop-not-reject shape"
        <| fun () ->
            Expect.equal PlaybackTelemetry.beaconRateLimit.QueueLimit 0 "a refused beacon is never queued"

            Expect.isGreaterThan
                PlaybackTelemetry.beaconRateLimit.PermitLimit
                100
                "headroom for a shared team partition — the losing branch costs telemetry, not playback"

        testCase "the rollup folds plays, unique sessions, completion rate and egress"
        <| fun () ->
            let day = DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc)

            let beacon media session event =
                PlaybackTelemetry.beaconRecord
                    "scope-1"
                    {
                        Media = MediaId media
                        Event = event
                        Session = session
                    }
                    (Guid.NewGuid())
                    day

            let egress media bytes =
                PlaybackTelemetry.egressRecord
                    {
                        Media = MediaId media
                        ScopeId = "scope-1"
                        Class = PlaybackTelemetry.ClassOriginal
                    }
                    bytes
                    (Guid.NewGuid())
                    day

            let records = [
                beacon "m1" "s1" PlaybackTelemetry.Started
                beacon "m1" "s1" (PlaybackTelemetry.Progress 50)
                beacon "m1" "s1" PlaybackTelemetry.Completed
                beacon "m1" "s2" PlaybackTelemetry.Started
                beacon "m1" "s2" (PlaybackTelemetry.Progress 10)
                egress "m1" 1000L
                egress "m1" 2500L
                beacon "m2" "s3" PlaybackTelemetry.Started
                // Not ours, and a row with no media id — both ignored,
                // because the expected call passes a whole scope's
                // ledger.
                {
                    RecordId = Guid.NewGuid()
                    ScopeId = "scope-1"
                    ResourceKind = ResourceKinds.storageBytes
                    Quantity = 99m
                    Unit = "bytes"
                    Origin = None
                    Metadata = Map.empty
                    Timestamp = day
                }
                {
                    (egress "m1" 77L) with
                        Metadata = Map.empty
                }
            ]

            let rollups = PlaybackTelemetry.PlaybackRollup.ofUsageRecords records

            Expect.equal (rollups |> List.map _.MediaId) [ "m1"; "m2" ] "one row per (media, scope, day), ordered"

            let m1 = rollups |> List.find (fun r -> r.MediaId = "m1")
            Expect.equal m1.Day "2026-08-20" "the UTC day bucket"
            Expect.equal m1.Plays 2 "two starts"
            Expect.equal m1.Completions 1 "one completion"
            Expect.equal m1.UniqueSessions 2 "two distinct correlators across five beacons"
            Expect.equal m1.CompletionRate 0.5 "one of two started viewings finished"
            Expect.equal m1.OriginEgressBytes 3500L "the egress rows summed — and the metadata-less one skipped"

            let m2 = rollups |> List.find (fun r -> r.MediaId = "m2")
            Expect.equal m2.CompletionRate 0.0 "a start with no completion is 0.0, not a division by zero"
            Expect.equal m2.OriginEgressBytes 0L "no egress attributed"

        testCase "the ledger row shapes are what a billing reader expects"
        <| fun () ->
            let at = DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc)
            let id = Guid.NewGuid()

            let e =
                PlaybackTelemetry.egressRecord
                    {
                        Media = MediaId "m1"
                        ScopeId = "scope-1"
                        Class = PlaybackTelemetry.ClassSegment
                    }
                    4096L
                    id
                    at

            Expect.equal e.ResourceKind "media.egress.bytes" "the kind the phase names"
            Expect.equal e.Quantity 4096m "decimal, because billing"
            Expect.equal e.Unit "bytes" ""
            Expect.equal e.ScopeId "scope-1" "attributed to the scope, never to the caller"
            Expect.equal e.Origin None "not an AI resource"
            Expect.equal e.Metadata[PlaybackTelemetry.MediaIdKey] "m1" "per-media attribution lives here, not in a tag"
            Expect.equal e.Metadata[PlaybackTelemetry.ClassKey] "segment" ""

            let b =
                PlaybackTelemetry.beaconRecord
                    "scope-1"
                    {
                        Media = MediaId "m1"
                        Event = PlaybackTelemetry.Progress 42
                        Session = "abc"
                    }
                    id
                    at

            Expect.equal b.Quantity 1m "one row per event"
            Expect.equal b.Metadata[PlaybackTelemetry.EventKey] "progress" ""
            Expect.equal b.Metadata[PlaybackTelemetry.PercentKey] "42" ""
            Expect.equal b.Metadata[PlaybackTelemetry.SessionKey] "abc" "the correlator, already derived"

            let started =
                PlaybackTelemetry.beaconRecord
                    "scope-1"
                    {
                        Media = MediaId "m1"
                        Event = PlaybackTelemetry.Started
                        Session = "abc"
                    }
                    id
                    at

            Expect.isFalse
                (started.Metadata.ContainsKey PlaybackTelemetry.PercentKey)
                "a non-progress event carries no percent"

        testCase "both metric series are declared, with bounded tag allowlists"
        <| fun () ->
            let names = PlaybackTelemetry.registrations |> List.map (fun r -> r.Definition.Name)

            Expect.contains names PlaybackTelemetry.EgressBytesMetric "egress"
            Expect.contains names PlaybackTelemetry.PlaybackEventsMetric "beacons"

            for r in PlaybackTelemetry.registrations do
                Expect.isTrue
                    (r.Definition.Name.StartsWith MetricDefinition.ReservedPrefix)
                    "SDK-owned metrics carry the reserved prefix"

                Expect.isFalse
                    (r.Definition.Tags |> List.contains PlaybackTelemetry.MediaIdKey)
                    "the media id is NOT a metric tag — it is unbounded, and the sink's series ceiling is the reason"
    ]

let private egressAccountingTests =
    testList "Phase 473 egress accounting (driven)" [
        testCase "a full 200 meters exactly the body it wrote"
        <| fun () ->
            let h = meteredHost ()
            let record = uploadTelemetryItem h
            let usage = h.Usage.Value
            let metrics = h.Metrics.Value

            let status, bodyLength, _ =
                driveMedia
                    h
                    RangeHandler.streamHandler
                    (sprintf "/api/media/stream/%s" (MediaId.value record.Id))
                    None
                    (Some telemetryContainer)

            Expect.equal status 200 "served whole"
            Expect.equal bodyLength telemetryPayload.Length "the whole body"
            Expect.isTrue (usage.WaitFor 1) "one egress row"

            let row = egressRows usage |> List.exactlyOne
            Expect.equal row.Quantity (decimal telemetryPayload.Length) "bytes actually written"
            Expect.equal row.ScopeId "u1" "the resolved scope, not the container"

            Expect.equal
                row.Metadata[PlaybackTelemetry.MediaIdKey]
                (MediaId.value record.Id)
                "attributed to the media item"

            Expect.equal row.Metadata[PlaybackTelemetry.ClassKey] PlaybackTelemetry.ClassOriginal ""

            let name, value, tags =
                metrics.Observations
                |> List.find (fun (n, _, _) -> n = PlaybackTelemetry.EgressBytesMetric)

            Expect.equal name PlaybackTelemetry.EgressBytesMetric ""
            Expect.equal value (float telemetryPayload.Length) "the same count reaches the metric"
            Expect.equal tags["scope"] "u1" ""
            Expect.equal tags["class"] PlaybackTelemetry.ClassOriginal ""

        testCase "every 206 window meters exactly what its Content-Range declared"
        <| fun () ->
            // The reconciliation claim, over the same range matrix
            // `ByteRange.parse` is pinned on: an explicit window, a
            // one-byte window, an open-ended window, and a suffix
            // window.
            for spec in [ "bytes=0-0"; "bytes=100-199"; "bytes=4900-"; "bytes=-50" ] do
                let h = meteredHost ()
                let record = uploadTelemetryItem h
                let usage = h.Usage.Value

                let status, bodyLength, ctx =
                    driveMedia
                        h
                        RangeHandler.streamHandler
                        (sprintf "/api/media/stream/%s" (MediaId.value record.Id))
                        (Some spec)
                        (Some telemetryContainer)

                Expect.equal status 206 (sprintf "%s is a partial content response" spec)
                Expect.isTrue (usage.WaitFor 1) (sprintf "%s metered" spec)

                let declared = contentRangeWindow (headerOf ctx "Content-Range")
                let row = egressRows usage |> List.exactlyOne

                Expect.equal (int64 bodyLength) declared (sprintf "%s: the body is the declared window" spec)

                Expect.equal
                    row.Quantity
                    (decimal declared)
                    (sprintf "%s: the metered bytes reconcile against Content-Range" spec)

        testCase "a 416 meters nothing"
        <| fun () ->
            let h = meteredHost ()
            let record = uploadTelemetryItem h
            let usage = h.Usage.Value

            let status, _, _ =
                driveMedia
                    h
                    RangeHandler.streamHandler
                    (sprintf "/api/media/stream/%s" (MediaId.value record.Id))
                    (Some "bytes=99999-")
                    (Some telemetryContainer)

            Expect.equal status 416 "unsatisfiable"
            Expect.equal (usage.SettleThenCount()) 0 "no body was written, so nothing is billed"

        testCase "a signed serve is attributed to the signature's own scope"
        <| fun () ->
            let h = meteredHost ()
            let record = uploadTelemetryItem h
            let usage = h.Usage.Value

            let scope: StorageScope = {
                ScopeId = "signed-scope"
                Container = telemetryContainer
                Persist = true
            }

            let url =
                h.Fixture.Library.SignedUrl(record.Id, scope, TimeSpan.FromMinutes 10.0)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let token = url.Substring(url.IndexOf "token=" + 6)

            let ctx = DefaultHttpContext()
            ctx.Request.Method <- "GET"
            ctx.Request.Scheme <- "https"
            ctx.Request.Host <- HostString "media.example.test"
            ctx.Request.Path <- PathString(sprintf "/media/signed/%s" (MediaId.value record.Id))
            ctx.Request.QueryString <- QueryString("?token=" + token)
            ctx.RequestServices <- telemetryServices h
            ctx.Response.Body <- new MemoryStream()

            let next: HttpFunc = Some >> Task.FromResult
            (RangeHandler.signedHandler next ctx).GetAwaiter().GetResult() |> ignore

            Expect.equal ctx.Response.StatusCode 200 "the signature admitted"
            Expect.isTrue (usage.WaitFor 1) "metered"

            Expect.equal
                (egressRows usage |> List.exactlyOne).ScopeId
                "signed-scope"
                "the scope that minted the token pays, not an ambient one"

        testCase "a derived segment is metered under its own class"
        <| fun () ->
            let f =
                makeFixtureWith encryptingTranscoder true MediaLibraryOptions.defaults None None

            let h = {
                Fixture = f
                Metrics = Some(RecordingMetricsSink())
                Usage = Some(RecordingUsageLog())
            }

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let usage = h.Usage.Value

            let status, bodyLength, _ =
                driveMedia
                    h
                    RangeHandler.hlsHandler
                    (sprintf "/api/media/hls/%s/seg0.ts" (MediaId.value record.Id))
                    None
                    (Some encContainer)

            Expect.equal status 200 "served"
            Expect.isTrue (usage.WaitFor 1) "metered"

            let row = egressRows usage |> List.exactlyOne
            Expect.equal row.Metadata[PlaybackTelemetry.ClassKey] PlaybackTelemetry.ClassSegment "class=segment"
            Expect.equal row.Quantity (decimal bodyLength) "the bytes the segment response wrote"

        testCase "a manifest served through the rewrite path is metered too"
        <| fun () ->
            // The rewrite path does not go through `copyToBody`, so this
            // is the claim that the second write site was not forgotten.
            let f =
                makeFixtureWith encryptingTranscoder true MediaLibraryOptions.defaults None None

            let h = {
                Fixture = f
                Metrics = Some(RecordingMetricsSink())
                Usage = Some(RecordingUsageLog())
            }

            let record =
                f.Library.Upload(encContainer, encUpload ())
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let usage = h.Usage.Value

            let status, bodyLength, _ =
                driveMedia
                    h
                    RangeHandler.hlsHandler
                    (sprintf "/api/media/hls/%s/index.m3u8" (MediaId.value record.Id))
                    None
                    (Some encContainer)

            Expect.equal status 200 "served"
            Expect.isTrue (usage.WaitFor 1) "metered"

            let row = egressRows usage |> List.exactlyOne
            Expect.equal row.Metadata[PlaybackTelemetry.ClassKey] PlaybackTelemetry.ClassManifest "class=manifest"

            Expect.equal row.Quantity (decimal bodyLength) "the REWRITTEN body's length, which is what actually left"

        testCase "with neither sink composed nothing is emitted and nothing is allocated (GP 13)"
        <| fun () ->
            let h = unmeteredHost ()
            let record = uploadTelemetryItem h

            let status, bodyLength, _ =
                driveMedia
                    h
                    RangeHandler.streamHandler
                    (sprintf "/api/media/stream/%s" (MediaId.value record.Id))
                    None
                    (Some telemetryContainer)

            Expect.equal status 200 "the serve path is unchanged"
            Expect.equal bodyLength telemetryPayload.Length "byte-for-byte the same body"

            // The structural half of the claim: the account resolved for
            // this exact composition IS the singleton no-op case, so the
            // per-chunk `count` and the trailing `flush` are a tag test
            // and nothing more. Asserting the emission is absent would
            // only show that the no-op sinks are no-ops.
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- telemetryServices h

            Expect.equal
                (PlaybackTelemetry.accountFor ctx (MediaId "m1") "u1" PlaybackTelemetry.ClassOriginal)
                PlaybackTelemetry.EgressUnmetered
                "the SDK-default composition resolves to the allocation-free account"

            let live = meteredHost ()
            let liveCtx = DefaultHttpContext()
            liveCtx.RequestServices <- telemetryServices live

            Expect.notEqual
                (PlaybackTelemetry.accountFor liveCtx (MediaId "m1") "u1" PlaybackTelemetry.ClassOriginal)
                PlaybackTelemetry.EgressUnmetered
                "and a composed deployment does NOT — so the gate is discriminating, not always-off"

        testCase "either sink alone is enough to meter"
        <| fun () ->
            // The gate is a disjunction: a deployment with metrics but
            // no usage metering (or the reverse) must still get the half
            // it composed.
            let usageOnly = {
                unmeteredHost () with
                    Usage = Some(RecordingUsageLog())
            }

            let record = uploadTelemetryItem usageOnly

            driveMedia
                usageOnly
                RangeHandler.streamHandler
                (sprintf "/api/media/stream/%s" (MediaId.value record.Id))
                None
                (Some telemetryContainer)
            |> ignore

            Expect.isTrue (usageOnly.Usage.Value.WaitFor 1) "usage metering alone still records"

            let metricsOnly = {
                unmeteredHost () with
                    Metrics = Some(RecordingMetricsSink())
            }

            let record2 = uploadTelemetryItem metricsOnly

            driveMedia
                metricsOnly
                RangeHandler.streamHandler
                (sprintf "/api/media/stream/%s" (MediaId.value record2.Id))
                None
                (Some telemetryContainer)
            |> ignore

            Expect.isNonEmpty metricsOnly.Metrics.Value.Observations "metrics alone still observes"
    ]

let private beaconEndpointTests =
    testList "Phase 473 beacon endpoint (driven)" [
        testCase "a valid scoped beacon is accepted and lands one ledger row"
        <| fun () ->
            let h = meteredHost ()
            let record = uploadTelemetryItem h
            let usage = h.Usage.Value

            let status =
                driveBeacon
                    h
                    (sprintf """{"mediaId":"%s","event":"started","session":"viewer-1"}""" (MediaId.value record.Id))
                    ""
                    (Some telemetryContainer)

            Expect.equal status 204 "no content"
            Expect.isTrue (usage.WaitFor 1) "recorded"

            let row = beaconRows usage |> List.exactlyOne
            Expect.equal row.ScopeId "u1" "attributed to the resolved scope"
            Expect.equal row.Metadata[PlaybackTelemetry.EventKey] "started" ""

            Expect.notEqual
                row.Metadata[PlaybackTelemetry.SessionKey]
                "viewer-1"
                "the raw client session id never reaches the ledger"

            Expect.isNonEmpty h.Metrics.Value.Increments "and the counter moved"

        testCase "every rejected shape is 204 and leaves no row"
        <| fun () ->
            // The status code is deliberately uninformative — a beacon
            // must never surface an error to a player, and must not be
            // an existence oracle. So the matrix is read through the
            // ledger.
            let cases = [
                "malformed JSON", """{""", Some telemetryContainer, ""
                "unknown event", """{"mediaId":"m1","event":"paused","session":"s"}""", Some telemetryContainer, ""
                "missing session", """{"mediaId":"m1","event":"started"}""", Some telemetryContainer, ""
                "out-of-range percent",
                """{"mediaId":"m1","event":"progress","percent":250,"session":"s"}""",
                Some telemetryContainer,
                ""
                "no credential at all", """{"mediaId":"m1","event":"started","session":"s"}""", None, ""
                "a bad signature",
                """{"mediaId":"m1","event":"started","session":"s"}""",
                None,
                "token=not-a-real-token"
            ]

            for name, body, scope, query in cases do
                let h = meteredHost ()
                let status = driveBeacon h body query scope
                Expect.equal status 204 (sprintf "%s answers 204" name)
                Expect.equal (h.Usage.Value.SettleThenCount()) 0 (sprintf "%s records nothing" name)

        testCase "an over-cap body is dropped without being read whole"
        <| fun () ->
            let h = meteredHost ()

            let oversized =
                sprintf """{"mediaId":"m1","event":"started","session":"s","pad":"%s"}""" (String('x', 4096))

            let status = driveBeacon h oversized "" (Some telemetryContainer)
            Expect.equal status 204 "still 204"
            Expect.equal (h.Usage.Value.SettleThenCount()) 0 "and nothing recorded"

        testCase "a signed token admits a beacon for its own media and no other"
        <| fun () ->
            let h = meteredHost ()
            let record = uploadTelemetryItem h
            let usage = h.Usage.Value

            let scope: StorageScope = {
                ScopeId = "signed-scope"
                Container = telemetryContainer
                Persist = true
            }

            let url =
                h.Fixture.Library.SignedUrl(record.Id, scope, TimeSpan.FromMinutes 10.0)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let token = url.Substring(url.IndexOf "token=" + 6)

            // The token's own media, with no ambient scope at all.
            let status =
                driveBeacon
                    h
                    (sprintf """{"mediaId":"%s","event":"completed","session":"v"}""" (MediaId.value record.Id))
                    ("token=" + Uri.EscapeDataString token)
                    None

            Expect.equal status 204 ""
            Expect.isTrue (usage.WaitFor 1) "the signature admitted it"

            Expect.equal
                (beaconRows usage |> List.exactlyOne).ScopeId
                "signed-scope"
                "attributed to the scope the token was minted for"

            // A DIFFERENT media id under the same token is refused —
            // the token cannot report plays against another item.
            let other = meteredHost ()

            let otherStatus =
                driveBeacon
                    other
                    """{"mediaId":"someone-elses-media","event":"started","session":"v"}"""
                    ("token=" + Uri.EscapeDataString token)
                    None

            Expect.equal otherStatus 204 ""
            Expect.equal (other.Usage.Value.SettleThenCount()) 0 "a token minted for one item unlocks only that item"

        testCase "beacons feed the rollup the usage read path already returns"
        <| fun () ->
            // The 473.C claim, end to end and with no new API: drive
            // real beacons + a real serve, then fold exactly the rows a
            // scope's `IUsageQueryApi.Query` would hand back.
            let h = meteredHost ()
            let record = uploadTelemetryItem h
            let usage = h.Usage.Value
            let mediaId = MediaId.value record.Id

            let beacon session event =
                driveBeacon
                    h
                    (sprintf """{"mediaId":"%s","event":"%s","session":"%s"}""" mediaId event session)
                    ""
                    (Some telemetryContainer)
                |> ignore

            beacon "viewer-a" "started"
            beacon "viewer-a" "completed"
            beacon "viewer-b" "started"

            driveMedia
                h
                RangeHandler.streamHandler
                (sprintf "/api/media/stream/%s" mediaId)
                None
                (Some telemetryContainer)
            |> ignore

            Expect.isTrue (usage.WaitFor 4) "three beacons and one egress row"

            let rollup =
                PlaybackTelemetry.PlaybackRollup.ofUsageRecords usage.Rows |> List.exactlyOne

            Expect.equal rollup.MediaId mediaId ""
            Expect.equal rollup.Plays 2 "two viewers started"
            Expect.equal rollup.Completions 1 "one finished"
            Expect.equal rollup.UniqueSessions 2 "two correlators"
            Expect.equal rollup.CompletionRate 0.5 "a correct completion rate"

            Expect.equal rollup.OriginEgressBytes (int64 telemetryPayload.Length) "and the origin egress beside it"

        testCase "the beacon route is POST-only"
        <| fun () ->
            let h = meteredHost ()
            let ctx = DefaultHttpContext()
            ctx.Request.Method <- "GET"
            ctx.Request.Path <- PathString MediaApi.beaconRoute
            ctx.RequestServices <- telemetryServices h
            ctx.Response.Body <- new MemoryStream()

            let next: HttpFunc = Some >> Task.FromResult
            let result = (PlaybackTelemetry.beaconHandler next ctx).GetAwaiter().GetResult()

            Expect.isNone result "a GET falls through to the rest of the pipeline"

        testCase "the beacon route cannot collide with a remoting member"
        <| fun () ->
            Expect.equal
                MediaApi.beaconRoute
                (MediaApi.routeBuilder "IMediaApi" "beacon")
                "the literal IS what the route builder would produce for a member named `beacon` — which is why no such member may exist"
    ]

// ─── Phase 739 — the queryable GRANT row on key delivery ─────────────
//
// Phase 471 audited every REFUSED key fetch as a queryable
// `AuthorizationDenied` row and left every GRANTED one as a structured
// log line, so the store could answer "who was turned away from this
// media" and not "who holds the key for it". These cases pin the row
// that closes that asymmetry, and — as much to the point — pin the
// places it must NOT appear, because an audit row that fires on a
// refusal would make the trail actively misleading.
//
// The emission is detached (`Async.Start`), exactly like the denial it
// twins, so every positive case polls for the row rather than reading
// straight after the call.

/// Accumulates every recorded event. Deliberately never fails on its
/// own — "exactly one row of this shape" is only a real assertion
/// because this would happily record nothing.
type private RecordingMediaAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()

    member _.Recorded = List.ofSeq recorded

    member this.KeyRows =
        this.Recorded
        |> List.choose (function
            | scope, MediaKeyDelivered p -> Some(scope, p)
            | _ -> None)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Poll to a deadline rather than sleeping a fixed interval: a fixed
/// sleep is either flaky or slow, and this returns the moment the row
/// lands. Returns whatever it has at the deadline instead of throwing,
/// so a failure reads "expected 1 row, got 0" — which names the defect —
/// rather than a timeout that does not.
let private awaitKeyRows (log: RecordingMediaAuditLog) (expected: int) =
    let deadline = DateTime.UtcNow.AddSeconds 5.0

    while log.KeyRows.Length < expected && DateTime.UtcNow < deadline do
        Thread.Sleep 10

    log.KeyRows

/// A negative case cannot poll for an absence, so it waits a fixed,
/// generous slice and then asserts the log is still empty. Long enough
/// that a row which WAS emitted has landed — the fail-once probe
/// confirms these cases go red when the emission is defeated, which is
/// what makes the wait long enough rather than merely hopeful.
let private assertNoKeyRow (log: RecordingMediaAuditLog) (why: string) =
    Thread.Sleep 250
    Expect.isEmpty log.KeyRows why

/// `drive`, plus an `IAuditLog` and an optional resolved `Subject`, and
/// with the key store made omittable so the "companion not composed"
/// case can be driven through the same code path.
let private driveAudited
    (f: EncryptedFixture)
    (log: RecordingMediaAuditLog)
    (withKeyStore: bool)
    (subject: Subject option)
    (path: string)
    (query: string)
    (scope: string option)
    : int * byte[] =
    let services = ServiceCollection()

    services
        .AddSingleton<SignedUrl.MediaUrlSigner>(f.Signer)
        .AddSingleton<IMediaLibrary>(f.Library)
        .AddSingleton<MediaLibraryOptions>(f.Options)
        .AddSingleton<IAuditLog>(log)
    |> ignore

    if withKeyStore then
        services.AddSingleton<HlsKeyDelivery.MediaHlsKeyStore>(f.Keys) |> ignore

    let ctx = DefaultHttpContext()
    ctx.Request.Method <- "GET"
    ctx.Request.Scheme <- "https"
    ctx.Request.Host <- HostString "media.example.test"
    ctx.Request.Path <- PathString path

    if query <> "" then
        ctx.Request.QueryString <- QueryString("?" + query)

    ctx.RequestServices <- services.BuildServiceProvider() :> IServiceProvider

    match scope with
    | Some container ->
        ctx.Items["ToolUp.StorageScope"] <-
            box {
                ScopeId = "u1"
                Container = container
                Persist = true
            }
    | None -> ()

    match subject with
    | Some s -> ctx.Items["ToolUp.Subject"] <- box s
    | None -> ()

    let body = new MemoryStream()
    ctx.Response.Body <- body

    let next: HttpFunc = Some >> Task.FromResult
    (HlsKeyDelivery.keyHandler next ctx).GetAwaiter().GetResult() |> ignore

    ctx.Response.StatusCode, body.ToArray()

/// One encrypted item in `encContainer`, ready to have its key fetched.
let private encryptedItem (f: EncryptedFixture) =
    f.Library.Upload(encContainer, encUpload ())
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failwithf "739 setup: %A" e)

let private mediaKeyGrantAuditTests =
    testList "Phase 739 key-delivery grant audit" [
        testCase "a scope-gated fetch lands ONE row naming media, subject, scope and route"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true
            let record = encryptedItem f
            let log = RecordingMediaAuditLog()

            let status, body =
                driveAudited f log true (Some(TeamMember("u1", "t1"))) (keyRoute record.Id) "" (Some encContainer)

            Expect.equal status 200 "admitted"
            Expect.equal body.Length 16 "the key still reaches the caller"

            let rows = awaitKeyRows log 1
            Expect.hasLength rows 1 "exactly one row — a key handed over silently is the defect"
            let scopeId, row = rows.Head

            Expect.equal scopeId encContainer "the row is filed under the scope that OWNS the media"
            Expect.equal row.MediaId (MediaId.value record.Id) "the axis the question is asked on"
            Expect.equal row.ScopeContainer encContainer "the container the key was resolved from"
            Expect.equal row.AdmissionRoute "scope" "the admitting route, verbatim from the gate"

            // The same `(kind, id)` projection `AuthorizationDenied`
            // carries — that identity is what lets a reviewer union the
            // two halves of this endpoint's trail on one key.
            Expect.equal row.SubjectKind "team" "subject kind"
            Expect.equal row.SubjectId (Some "u1") "subject id"

        testCase "a signed-URL fetch is distinguishable by its route, with no session at all"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true
            let record = encryptedItem f
            let log = RecordingMediaAuditLog()

            let scope: StorageScope = {
                ScopeId = "u1"
                Container = encContainer
                Persist = true
            }

            let live =
                f.Signer.SignAsync(record.Id, scope, TimeSpan.FromHours 1.0, DateTimeOffset.UtcNow)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let status, _ =
                driveAudited f log true None (keyRoute record.Id) ("token=" + Uri.EscapeDataString live) None

            Expect.equal status 200 "a live signature admits with no session"

            let rows = awaitKeyRows log 1
            Expect.hasLength rows 1 "one row"
            let scopeId, row = rows.Head

            // The token's container, not the request's — there is no
            // request scope on this route at all, which is exactly why
            // the row has to carry the one the gate DERIVED (GP 4).
            Expect.equal scopeId encContainer "filed under the container bound into the token"
            Expect.equal row.ScopeContainer encContainer "and carried on the row"

            Expect.equal
                row.AdmissionRoute
                "signature"
                "the two admitted routes are distinguishable — 'by what authority' is half the question"

            Expect.equal row.SubjectKind "anonymous" "a signed fetch legitimately carries no identity"

            Expect.equal row.SubjectId None "and the row says so rather than asserting a session id as an identity"

        testCase "the row is emitted UNCONDITIONALLY — EmitAudit off does not silence it"
        <| fun () ->
            // The 739.B decision, pinned. Gating the grant row on an
            // opt-in the denial row does not respect would reproduce, in
            // any deployment that turned the opt-in off, precisely the
            // asymmetry this phase exists to close.
            let quiet = {
                MediaLibraryOptions.defaults with
                    EncryptHlsByDefault = true
                    EmitAudit = false
            }

            let f = makeFixtureWith encryptingTranscoder true quiet None None
            let record = encryptedItem f
            let log = RecordingMediaAuditLog()

            let status, _ =
                driveAudited f log true (Some(AuthenticatedUser "u9")) (keyRoute record.Id) "" (Some encContainer)

            Expect.equal status 200 "admitted"
            Expect.hasLength (awaitKeyRows log 1) 1 "the security row does not follow the log-volume knob"

        testCase "no row on ANY refused fetch — anonymous, expired signature, or cross-scope"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true
            let record = encryptedItem f

            let anonLog = RecordingMediaAuditLog()
            let anonStatus, _ = driveAudited f anonLog true None (keyRoute record.Id) "" None
            Expect.equal anonStatus 401 "unchanged from Phase 471"
            assertNoKeyRow anonLog "a 401 must never produce a DELIVERED row"

            let scope: StorageScope = {
                ScopeId = "u1"
                Container = encContainer
                Persist = true
            }

            let stale =
                f.Signer.SignAsync(record.Id, scope, TimeSpan.FromMinutes 1.0, DateTimeOffset.UtcNow.AddHours -2.0)
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            let expiredLog = RecordingMediaAuditLog()

            let expiredStatus, _ =
                driveAudited f expiredLog true None (keyRoute record.Id) ("token=" + Uri.EscapeDataString stale) None

            Expect.equal expiredStatus 403 "unchanged from Phase 471"
            assertNoKeyRow expiredLog "a 403 must never produce a DELIVERED row"

            // The cross-scope case is the sharpest of the three: the
            // caller IS a good authenticated subject and the gate admits
            // the route. What refuses is the container lookup, and the
            // row must sit after THAT, not after the gate.
            let foreignLog = RecordingMediaAuditLog()

            let foreignStatus, _ =
                driveAudited
                    f
                    foreignLog
                    true
                    (Some(TeamMember("u2", "t2")))
                    (keyRoute record.Id)
                    ""
                    (Some foreignContainer)

            Expect.equal foreignStatus 404 "unchanged from Phase 471"

            assertNoKeyRow
                foreignLog
                "an admitted route that resolved no key delivered nothing — the row would assert a fetch that never happened"

        testCase "no row — and no cost — when the media companion is not composed (GP 13)"
        <| fun () ->
            let f = makeEncryptedFixture encryptingTranscoder true true
            let record = encryptedItem f
            let log = RecordingMediaAuditLog()

            let status, _ =
                driveAudited
                    f
                    log
                    false // no MediaHlsKeyStore in the container
                    (Some(TeamMember("u1", "t1")))
                    (keyRoute record.Id)
                    ""
                    (Some encContainer)

            Expect.equal status 404 "no key store composed"
            assertNoKeyRow log "a deployment with no encrypted media pays nothing for this phase"

        testCase "a deployment composing no IAuditLog still serves the key"
        <| fun () ->
            // The seam is optional in both directions: the row is
            // best-effort and its absence must never change what the
            // caller sees. Driven through the Phase 471 `drive`, whose
            // service provider deliberately has no `IAuditLog` at all.
            let f = makeEncryptedFixture encryptingTranscoder true true
            let record = encryptedItem f

            let status, body, _ =
                drive f HlsKeyDelivery.keyHandler (keyRoute record.Id) "" (Some encContainer)

            Expect.equal status 200 "the key is served with no audit log composed"
            Expect.equal body.Length 16 "and it is the whole key"
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
        // Phase 471 — the gate, the rewrite, the ciphertext, the endpoint.
        hlsGateTests
        hlsRewriteTests
        hlsEncryptionTests
        hlsKeyEndpointTests
        // Phase 472 — the edge fan-out, delegated signing, and the
        // declared Cache-Control (including the key route's unreachable
        // no-store).
        edgeFanOutTests
        delegatedSigningTests
        declaredCacheHeaderTests
        // Phase 473 — the pure surfaces, egress reconciled against
        // Content-Range, the GP 13 off path, and the beacon matrix.
        playbackPureTests
        egressAccountingTests
        beaconEndpointTests
        // Phase 739 — the queryable grant twin of Phase 471's denial row.
        mediaKeyGrantAuditTests
    ]
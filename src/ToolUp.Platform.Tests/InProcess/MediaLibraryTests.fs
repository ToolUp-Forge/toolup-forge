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
    ]
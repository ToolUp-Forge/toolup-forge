// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IMediaLibraryContract

open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.MediaLibrary

// ─── Phase 88 — IMediaLibrary contract pack ───────────────────────────
//
// Behavioural conformance for any `IMediaLibrary` implementation:
// upload / get / list / delete round-trips, byte-range correctness
// (`OpenRange` slicing + the `Unsatisfiable` path that maps to HTTP
// `416`), content-length, and signed-URL minting for present / absent
// items. Pure `ByteRange.parse` (the `206` / `416` decision) and the
// `SignedUrl` crypto are exercised separately in `MediaLibraryTests`.

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
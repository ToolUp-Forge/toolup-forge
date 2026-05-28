module ToolUp.Platform.Tests.Contracts.IAssetStoreContract

open System
open Expecto
open ToolUp.AssetStore

// ─── IAssetStore contract pack ──────────────────────────────────
//
// Parametrised tests for any `IAssetStore` implementation. The
// factory yields:
//   - a fresh, empty store (no records yet)
//   - the scope container the test should write under
//   - a small valid PNG byte buffer ("seed image") suitable for
//     decode + resize through the wired renderer
//   - a second image with identical bytes to seed image (content-
//     hash dedup test)
//   - a third image with distinct bytes (scope-isolation test)
//
// Bindings (`DefaultAssetStoreTests`) construct the factory over
// `InMemoryBlobStorage` + `SkiaSharpDerivativeRenderer` +
// `NoOpAuditLog`.

type AssetStoreFixture = {
    Store: IAssetStore
    Container: string
    SeedImage: byte[]
    DuplicateImage: byte[]
    OtherImage: byte[]
}

let private mkValidRequest (image: byte[]) (alt: string) =
    UploadRequest.create
        AssetStoreOptions.defaults
        image
        "test.png"
        "image/png"
        alt
        None
        "test-user"
        DerivativeProfileId.webDefault

let tests (name: string) (factory: unit -> AssetStoreFixture) =

    testList $"{name} — IAssetStore contract" [

        testCaseAsync "Upload happy path persists record with alt-text and pixel dimensions"
        <| async {
            let fx = factory ()

            let req =
                match mkValidRequest fx.SeedImage "Sample image" with
                | Ok r -> r
                | Error e -> failwithf "expected valid request, got %A" e

            let! result = fx.Store.Upload(fx.Container, req)

            match result with
            | Error err -> failwithf "Upload returned error: %A" err
            | Ok record ->
                Expect.equal record.AltText "Sample image" "alt-text persisted"
                Expect.equal record.OriginalFilename "test.png" "filename persisted"
                Expect.equal record.MimeType "image/png" "mime persisted"
                Expect.equal record.UploadedBy "test-user" "uploader persisted"
                Expect.isGreaterThan record.SizeBytes 0L "size > 0"
                Expect.equal (record.ContentHash.Length) 64 "SHA-256 hex is 64 chars"
                Expect.isSome record.Width "width populated by SkiaSharp probe"
                Expect.isSome record.Height "height populated by SkiaSharp probe"
        }

        testCaseAsync "Upload rejects empty alt-text with AltTextRequired"
        <| async {
            let fx = factory ()

            // The smart constructor blocks empty alt-text before
            // the store is even reached — assert that path.
            let validation =
                UploadRequest.create
                    AssetStoreOptions.defaults
                    fx.SeedImage
                    "test.png"
                    "image/png"
                    ""
                    None
                    "test-user"
                    DerivativeProfileId.webDefault

            match validation with
            | Error AltTextRequired -> ()
            | other -> failwithf "expected AltTextRequired, got %A" other
        }

        testCaseAsync "Upload rejects > 1024-char alt-text with AltTextTooLong"
        <| async {
            let fx = factory ()
            let longAlt = String.replicate 1025 "a"

            let validation =
                UploadRequest.create
                    AssetStoreOptions.defaults
                    fx.SeedImage
                    "test.png"
                    "image/png"
                    longAlt
                    None
                    "test-user"
                    DerivativeProfileId.webDefault

            match validation with
            | Error(AltTextTooLong 1025) -> ()
            | other -> failwithf "expected AltTextTooLong 1025, got %A" other
        }

        testCaseAsync
            "Content-hash dedup — two uploads of identical bytes produce two distinct records sharing a content hash"
        <| async {
            let fx = factory ()

            let req1 =
                mkValidRequest fx.SeedImage "first"
                |> function
                    | Ok r -> r
                    | _ -> failwith "req1"

            let req2 =
                mkValidRequest fx.DuplicateImage "second"
                |> function
                    | Ok r -> r
                    | _ -> failwith "req2"

            let! r1 = fx.Store.Upload(fx.Container, req1)
            let! r2 = fx.Store.Upload(fx.Container, req2)

            match r1, r2 with
            | Ok rec1, Ok rec2 ->
                Expect.notEqual rec1.Id rec2.Id "distinct asset ids"
                Expect.equal rec1.ContentHash rec2.ContentHash "shared content hash"
                Expect.notEqual rec1.AltText rec2.AltText "distinct alt-text per record"
            | _ -> failwithf "uploads failed: %A %A" r1 r2
        }

        testCaseAsync "Derivative cache miss + hit — first request renders, second serves from cache"
        <| async {
            let fx = factory ()

            let req =
                mkValidRequest fx.SeedImage "deriv"
                |> function
                    | Ok r -> r
                    | _ -> failwith "req"

            let! upload = fx.Store.Upload(fx.Container, req)

            let record =
                match upload with
                | Ok r -> r
                | Error e -> failwithf "upload failed: %A" e

            // Miss — renders, caches, serves.
            let! first = fx.Store.GetDerivative(fx.Container, record.Id, "thumbnail")

            match first with
            | Ok(bytes, mime) ->
                Expect.isGreaterThan bytes.Length 0 "derivative bytes returned"
                Expect.equal mime "image/jpeg" "thumbnail mime from web-default profile"
            | Error e -> failwithf "first GetDerivative failed: %A" e

            // Hit — same bytes.
            let! second = fx.Store.GetDerivative(fx.Container, record.Id, "thumbnail")

            match first, second with
            | Ok(b1, _), Ok(b2, _) -> Expect.equal b1.Length b2.Length "cache hit returns identical bytes"
            | _ -> failwith "cache hit did not return Ok"
        }

        testCaseAsync "GetDerivative returns UnknownDerivative for an unregistered name"
        <| async {
            let fx = factory ()

            let req =
                mkValidRequest fx.SeedImage "unknown-deriv"
                |> function
                    | Ok r -> r
                    | _ -> failwith "req"

            let! upload = fx.Store.Upload(fx.Container, req)

            let record =
                match upload with
                | Ok r -> r
                | Error _ -> failwith "upload"

            let! result = fx.Store.GetDerivative(fx.Container, record.Id, "nonexistent")

            match result with
            | Error(UnknownDerivative "nonexistent") -> ()
            | other -> failwithf "expected UnknownDerivative, got %A" other
        }

        testCaseAsync "Scope isolation — asset uploaded under one container is not visible under another"
        <| async {
            let fx = factory ()

            let req =
                mkValidRequest fx.SeedImage "isolation"
                |> function
                    | Ok r -> r
                    | _ -> failwith "req"

            let! upload = fx.Store.Upload(fx.Container, req)

            let record =
                match upload with
                | Ok r -> r
                | Error _ -> failwith "upload"

            // Same store, different container — should not find.
            let! crossScope = fx.Store.Get("user-other", record.Id)
            Expect.isNone crossScope "cross-scope read returns None"

            let! crossList = fx.Store.List("user-other", "", 0)
            Expect.equal crossList List.empty "cross-scope list is empty"
        }

        testCaseAsync "Delete cascades — record and derivative cache for the hash are removed"
        <| async {
            let fx = factory ()

            let req =
                mkValidRequest fx.SeedImage "delete-cascade"
                |> function
                    | Ok r -> r
                    | _ -> failwith "req"

            let! upload = fx.Store.Upload(fx.Container, req)

            let record =
                match upload with
                | Ok r -> r
                | Error _ -> failwith "upload"

            // Warm the derivative cache.
            let! _ = fx.Store.GetDerivative(fx.Container, record.Id, "thumbnail")

            let! delete = fx.Store.Delete(fx.Container, record.Id)

            match delete with
            | Ok() -> ()
            | Error e -> failwithf "delete failed: %A" e

            let! after = fx.Store.Get(fx.Container, record.Id)
            Expect.isNone after "record gone after delete"

            // Subsequent derivative request returns AssetNotFound.
            let! followup = fx.Store.GetDerivative(fx.Container, record.Id, "thumbnail")

            match followup with
            | Error AssetDerivativeError.AssetNotFound -> ()
            | other -> failwithf "expected AssetNotFound after delete, got %A" other
        }

        testCaseAsync "Delete is idempotent for unknown ids"
        <| async {
            let fx = factory ()
            let phantom = AssetId "does-not-exist"
            let! result = fx.Store.Delete(fx.Container, phantom)
            Expect.equal result (Ok()) "unknown id delete returns Ok"
        }

        testCaseAsync "List returns records newest-first and supports pagination"
        <| async {
            let fx = factory ()

            // Upload 3 records.
            for alt in [ "first"; "second"; "third" ] do
                let req =
                    mkValidRequest fx.SeedImage alt
                    |> function
                        | Ok r -> r
                        | _ -> failwith "req"

                let! _ = fx.Store.Upload(fx.Container, req)
                // Force ordering by ensuring distinct UploadedAt;
                // 1 ms is enough at DateTimeOffset precision.
                do! Async.Sleep 5

            let! page0 = fx.Store.List(fx.Container, "", 0)
            Expect.equal page0.Length 3 "page 0 has 3 records"

            // Newest-first by UploadedAt — "third" was uploaded
            // last so it lands at index 0.
            Expect.equal page0[0].AltText "third" "newest record first"

            let! page1 = fx.Store.List(fx.Container, "", 1)
            Expect.equal page1.Length 0 "page 1 is empty (< 50-record page size)"
        }
    ]
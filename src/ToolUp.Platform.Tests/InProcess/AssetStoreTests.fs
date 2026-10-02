module ToolUp.Platform.Tests.InProcess.AssetStoreTests

open System.IO
open Expecto
open SkiaSharp
open ToolUp.Platform
open ToolUp.Platform.AuditLog
open ToolUp.Platform.BlobStorage
open ToolUp.AssetStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.IAssetStoreContract

/// Produce a deterministic small PNG via SkiaSharp so the
/// contract pack has a real image to decode + resize without
/// depending on a checked-in binary fixture.
let private syntheticPng (width: int) (height: int) (seed: byte) : byte[] =
    let info = SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
    use bitmap = new SKBitmap(info)

    // Fill with a deterministic pattern derived from seed so
    // duplicate / distinct fixtures produce identical / distinct
    // SHA-256s respectively.
    for y in 0 .. height - 1 do
        for x in 0 .. width - 1 do
            let r = byte ((int seed + x) % 256)
            let g = byte ((int seed + y) % 256)
            let b = byte ((int seed + x + y) % 256)
            bitmap.SetPixel(x, y, SKColor(r, g, b, 255uy))

    use image = SKImage.FromBitmap bitmap
    use data = image.Encode(SKEncodedImageFormat.Png, 100)
    data.ToArray()

let private mkFixture () : AssetStoreFixture =
    let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let renderer = SkiaSharpDerivativeRenderer() :> IDerivativeRenderer
    let profiles = DerivativeProfileRegistry.withWebDefault
    let audit = NoOpAuditLog() :> IAuditLog
    let logger = ConsoleLogger.ConsoleLogger() :> ILogger

    let store =
        DefaultAssetStore(blob, renderer, profiles, audit, logger, AssetStoreOptions.defaults) :> IAssetStore

    let seed = syntheticPng 100 80 7uy
    let duplicate = syntheticPng 100 80 7uy
    let other = syntheticPng 100 80 9uy

    {
        Store = store
        Container = "user-test"
        SeedImage = seed
        DuplicateImage = duplicate
        OtherImage = other
    }

[<Tests>]
let tests = IAssetStoreContract.tests "DefaultAssetStore (in-memory)" mkFixture

/// Phase 687 — the in-process binding of the `IDerivativeRenderer`
/// contract pack; the isolated renderer binds the same pack from
/// `ToolUp.Companions.Isolation.Tests`, which is what makes the seam
/// a proven replaceable one (GP 12) rather than an asserted one.
[<Tests>]
let rendererContract =
    IDerivativeRendererContract.tests "SkiaSharpDerivativeRenderer (in-process)" (fun () ->
        SkiaSharpDerivativeRenderer() :> IDerivativeRenderer)

// ─── Phase 967 — a refused delete of the shared original is reported ──
//
// `Delete` removed the asset's original blob (its bytes at rest) through
// `let! _ =`, after the record was already gone: a refused delete read as
// success, and the re-run found no record, so the original was orphaned
// for good. The original now goes first, and its refusal fails the delete
// with the record still in place.

let private storeOver (blob: IBlobStorage) =
    DefaultAssetStore(
        blob,
        SkiaSharpDerivativeRenderer() :> IDerivativeRenderer,
        DerivativeProfileRegistry.withWebDefault,
        NoOpAuditLog() :> IAuditLog,
        ConsoleLogger.ConsoleLogger() :> ILogger,
        AssetStoreOptions.defaults
    )
    :> IAssetStore

let private uploadPng (store: IAssetStore) (container: string) (png: byte[]) (alt: string) = async {
    let request =
        UploadRequest.create
            AssetStoreOptions.defaults
            png
            "test.png"
            "image/png"
            alt
            None
            "test-user"
            DerivativeProfileId.webDefault

    match request with
    | Error e -> return failwithf "expected a valid request, got %A" e
    | Ok req ->
        match! store.Upload(container, req) with
        | Ok record -> return record
        | Error e -> return failwithf "upload failed: %A" e
}

let private originalsOf (inner: IBlobStorage) (container: string) = async {
    let! names = inner.List(container, "assets/originals/")
    return names |> List.sort
}

let phase967Tests =
    testList "Phase 967 — a refused delete of the shared original is reported" [

        testCaseAsync "Delete: a refused original blob fails the delete, naming it, and keeps the record for a re-run"
        <| async {
            let inner = InMemoryBlobStorage.InMemoryBlobStorage()
            let container = "user-test"
            let healthy = storeOver inner
            let! record = uploadPng healthy container (syntheticPng 100 80 7uy) "only"
            let original = "assets/originals/" + record.ContentHash

            let refusing =
                storeOver (
                    ToolUp.Platform.Tests.InProcess.DataObjectStoreTests.DeleteRefusingBlobStorage(inner, (=) original)
                    :> IBlobStorage
                )

            match! refusing.Delete(container, record.Id) with
            | Ok() -> failtest "a refused delete of the asset's own bytes must not read as Ok"
            | Error(AssetDeleteError.StorageError msg) ->
                Expect.stringContains msg original "the failure names the original blob"
            | Error other -> failtestf "expected StorageError, got %A" other

            let! stillThere = healthy.Get(container, record.Id)
            Expect.isSome stillThere "the record is kept, so a re-run finds the asset"

            let! originals = originalsOf inner container
            Expect.equal originals [ original ] "the original is still at rest"

            match! healthy.Delete(container, record.Id) with
            | Ok() -> ()
            | Error e -> failtestf "the re-run over a healthy store must complete: %A" e

            let! after = originalsOf inner container
            Expect.isEmpty after "the re-run removed the original"
            let! gone = healthy.Get(container, record.Id)
            Expect.isNone gone "…and the record"
        }

        testCaseAsync "Delete: a refused record delete is still reported (the record is the operation's own effect)"
        <| async {
            let inner = InMemoryBlobStorage.InMemoryBlobStorage()
            let container = "user-test"
            let healthy = storeOver inner
            let! record = uploadPng healthy container (syntheticPng 100 80 7uy) "only"

            let refusing =
                storeOver (
                    ToolUp.Platform.Tests.InProcess.DataObjectStoreTests.DeleteRefusingBlobStorage(
                        inner,
                        fun n -> n.StartsWith "assets/records/"
                    )
                    :> IBlobStorage
                )

            match! refusing.Delete(container, record.Id) with
            | Error(AssetDeleteError.StorageError _) -> ()
            | other -> failtestf "expected StorageError, got %A" other
        }

        testCaseAsync "Delete: an original another record still references is not deleted, so its refusal never fires"
        <| async {
            let inner = InMemoryBlobStorage.InMemoryBlobStorage()
            let container = "user-test"
            let healthy = storeOver inner
            let png = syntheticPng 100 80 7uy
            let! first = uploadPng healthy container png "first"
            let! second = uploadPng healthy container png "second"
            let original = "assets/originals/" + first.ContentHash

            let refusing =
                storeOver (
                    ToolUp.Platform.Tests.InProcess.DataObjectStoreTests.DeleteRefusingBlobStorage(inner, (=) original)
                    :> IBlobStorage
                )

            match! refusing.Delete(container, first.Id) with
            | Ok() -> ()
            | Error e -> failtestf "the shared original is not this delete's to remove: %A" e

            let! survivor = healthy.Get(container, second.Id)
            Expect.isSome survivor "the other record is untouched"
            let! originals = originalsOf inner container
            Expect.equal originals [ original ] "the shared original stays"

            // The last record sharing the hash takes the original with it.
            match! healthy.Delete(container, second.Id) with
            | Ok() -> ()
            | Error e -> failtestf "last-reference delete failed: %A" e

            let! after = originalsOf inner container
            Expect.isEmpty after "the last reference removed the original"
        }
    ]
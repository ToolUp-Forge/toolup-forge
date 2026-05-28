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
# Phase 127 — AssetStore derivative-pipeline generalisation (consumer migration)

**What changes.** `ToolUp.AssetStore`'s derivative pipeline generalises on two axes,
both additive: profiles can now declare **general (arbitrary-MIME) derivative
entries** alongside the existing image specs (document → preview image, video →
poster frame, model → compressed variant), and general entries can opt into an
**async job-backed mode** (`DerivationMode.AsyncJob`) where derivation runs on
`IJobScheduler`, the request path returns a typed `DerivationPending`, completion
surfaces over the notification channel (`AssetStore.DerivativeReady` key), and the
content-hash cache serves instantly thereafter.

**Nothing changes unless you opt in.** Existing image profiles, the synchronous
SkiaSharp default, `withRenderer` / `RendererOverride`, cache paths
(`assets/derivatives/{hash}/{name}.{ext}`) and `IAssetStore`'s surface behave
byte-for-byte unchanged — pinned by regression tests in
`src/ToolUp.AssetStore.Tests/`. The `DerivativeProfileRegistry` API is
source-compatible: `register` / `resolve` / `resolveSpec` keep their signatures
(internally a profile is now a `ProfileEntry list`; image registrations map onto
`ImageDerivative` automatically).

## Diff to apply (only when adopting the new capability)

```fsharp
// Compose time — register a general profile entry + its renderer + the async opt-in:
AssetStoreServerApp.create ()
|> AssetStoreServerApp.withDerivativeProfileEntries myProfileId [ GeneralDerivative posterSpec ]
|> AssetStoreServerApp.withMimeRenderer "video-poster" myPosterRenderer   // IMimeDerivativeRenderer
|> AssetStoreServerApp.withAsyncDerivation AsyncDerivationOptions.defaults
```

```fsharp
// Profile entry shape:
GeneralDerivative {
    Name = "poster"; AcceptedInputMimes = [ "video/*" ]
    OutputMime = "image/jpeg"; FileExtension = "jpg"
    RendererKey = "video-poster"; Mode = AsyncJob; Parameters = Map.empty
}
```

Custom `IDerivativeRenderer` implementors: **no change** — the image seam is
untouched. The generalised seam is a **new sibling interface**
(`IMimeDerivativeRenderer`), not an extension of `IDerivativeRenderer`, so existing
implementations compile and behave identically.

`DefaultAssetStore` direct constructors: the pre-127 6-argument constructor remains
(delegates with an empty MIME-renderer registry and no async coordinator). The
primary constructor gains `MimeRendererRegistry` + `DerivativeJobCoordinator option`.

New `AssetDerivativeError` case: `DerivationPending of correlationId`. Exhaustive
matches on the error DU gain a case to handle (requests for async-mode entries only;
deployments without async profiles never see it).

## Verification steps

1. `dotnet build` your composition root — no source changes required.
2. Startup behaviour: without `withAsyncDerivation`, no job handler is registered
   and no notification traffic is emitted (GP 13).
3. If adopting async mode: request an uncached async derivative → expect
   `DerivationPending`; watch for the `AssetStore.DerivativeReady` custom
   notification on the asset's scope; re-request → cache hit.

## Rollback

Remove the `withMimeRenderer` / `withAsyncDerivation` calls and any
`GeneralDerivative` profile entries — the image-only pipeline is the default and
needs nothing else. Orphaned status blobs under `assets/derivative-status/` are
inert and may be deleted.

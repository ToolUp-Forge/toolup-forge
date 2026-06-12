# ToolUp.AssetStore

Phase 39 `IAssetStore` companion. Image-first asset substrate for ToolUp.Platform: per-asset records with **alt-text required at upload**, content-hash-keyed derivative cache (thumbnail / medium / OG-image / WebP / AVIF), pluggable `IDerivativeRenderer` (default: SkiaSharp), audit emission under `_platform.assets`.

## What it ships

- `IAssetStore` (5-method surface: `Upload` / `Get` / `GetDerivative` / `Delete` / `List`).
- `DefaultAssetStore` — wraps `IBlobStorage` for originals + derivative cache. SHA-256 content-hash as dedup key; per-asset record at `assets/records/{assetId}.json`; alt-text + MIME-accept-list validation in `Upload`.
- `IDerivativeRenderer` — pluggable image renderer. Default `SkiaSharpDerivativeRenderer` handles JPEG / PNG / WebP natively; AVIF returns `UnsupportedFormat` (needs a SkiaSharp AVIF plug-in not bundled).
- `AssetUploadHandler` — ToolUp.Remoting handler + multipart endpoint at `/api/assets/upload`. Mandatory alt-text validation.
- `AssetApi` — ToolUp.Remoting `IAssetApi` contract; mounted at `/api/assets/` when `ServerConfig.AssetStore = EnabledAssetStore`.
- `AssetCompose` — `AssetStoreServerApp` shape; `withAssetStore` registration; `withDerivativeRenderer` / `withDerivativeProfile` / `withAcceptedMimeTypes` / `withMaxUploadBytes` / `withAssetStoreOverride` builders. Strip-imports byte-for-byte equivalent to `ServerApp` when `AssetStore = NoAssetStore`.
- `DerivativeProfiles` — compose-time registry seeded with `web-default` (thumbnail / medium / OG / webp-medium). Deployments add profiles via `withDerivativeProfile`.

## How to enable

1. Reference this companion's `.fsproj`:

   ```xml
   <PackageReference Include="ToolUp.AssetStore" />
   ```

2. Switch on `ServerConfig.AssetStore = EnabledAssetStore` in the composition root:

   ```fsharp
   let config = {
       ServerConfig.defaults with
           AssetStore = EnabledAssetStore
   }
   ```

3. Wire via `withAssetStore` on the composition pipeline:

   ```fsharp
   ServerApp.empty
   |> ServerApp.withConfig config
   |> ServerApp.withStorage blobStorage
   |> AssetStoreServerApp.create
   |> AssetStoreServerApp.withDerivativeProfile DerivativeProfiles.webDefault
   |> AssetStoreServerApp.run
   ```

4. Client-side, browsers POST multipart to `/api/assets/upload` with the file part + alt-text + caption + profile id fields. Server validates alt-text non-empty + ≤ 1024 chars + MIME on the accept-list.

## Linux deployments

The default `SkiaSharpDerivativeRenderer` needs platform-specific native assets. Linux containers must additionally reference:

```xml
<PackageReference Include="SkiaSharp.NativeAssets.Linux" />
```

Windows + macOS dev environments pull the native assets transitively — no extra reference needed.

## Audit emission

`AssetUploaded` and `AssetDeleted` events emit under `SourceModule = "_platform.assets"`. Payloads carry `AssetId` / `ContentHash` / `MimeType` / `SizeBytes` / `UploadedBy`. Alt-text and caption are NOT included in audit payloads (treated as user-controlled content per the audit-payload-hygiene GP).

## Generalised derivative profiles + async derivation (Phase 127)

**Nothing changes unless you opt in** — existing image profiles, the SkiaSharp default, and every cache path behave byte-for-byte as before. Two additive capabilities:

**Arbitrary-MIME profile entries.** A profile is now a list of `ProfileEntry` — the image specs you already register (`ImageDerivative`, what `withDerivativeProfile` produces) and `GeneralDerivative` entries declaring accepted input MIME(s), output MIME, a cache file extension, and a renderer key resolved against the deployment's `MimeRendererRegistry`:

```fsharp
AssetStoreServerApp.create ()
|> AssetStoreServerApp.withDerivativeProfileEntries (DerivativeProfileId "media") [
    GeneralDerivative {
        Name = "poster"; AcceptedInputMimes = [ "video/*" ]
        OutputMime = "image/jpeg"; FileExtension = "jpg"
        RendererKey = "video-poster"; Mode = AsyncJob; Parameters = Map.empty
    }
  ]
|> AssetStoreServerApp.withMimeRenderer "video-poster" posterRenderer  // IMimeDerivativeRenderer
```

Renderer implementations carrying vendor dependencies ship as companions (GP 1); `IMimeDerivativeRenderer` is a new sibling interface — `IDerivativeRenderer` and its implementations are untouched.

**Async job-backed mode** (for seconds-to-minutes-class derivations) — opt in with `withAsyncDerivation AsyncDerivationOptions.defaults`. A request for an uncached `Mode = AsyncJob` entry enqueues exactly one derivation job per (content hash, derivative name) — concurrent requests coalesce via an in-process gate plus the scheduler's idempotency-key dedup — and returns `Error (DerivationPending correlationId)`. The job (a stateless, idempotent `IJobHandler` that re-checks the cache on entry) renders off the request path, writes the content-hash cache, and publishes a `CustomNotification` under the **`AssetStore.DerivativeReady`** key on the asset's scope; every subsequent request is an instant cache hit. Failures honour the registration's `RetryPolicy`; once attempts are exhausted the failure is recorded and requests answer a typed `RenderFailed` rather than re-enqueueing forever. Without the opt-in there is no job registration and no channel traffic (GP 13); async-mode entries fail typed with a pointer to the opt-in.

Requires `ServerConfig.JobScheduler` to be enabled (the job substrate) — multi-node coalescing is best-effort (idempotent handler absorbs a duplicate run); single-process coalescing is exact.

## Sub-companions (deferred)

`src/AssetStore/Imagemagick/` (wider format coverage) and `src/AssetStore/Cloudinary/` (hosted alternative) are sketched but not built — trigger-gated on deployment demand. Phase 127's generalised seam is where FFmpeg poster-frame / document-preview / model-compression renderers plug in per demand.

## See also

- [docs/companions/asset-store.md](../../docs/companions/asset-store.md) — full architecture walkthrough.

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

## Sub-companions (deferred)

`src/AssetStore/Imagemagick/` (wider format coverage) and `src/AssetStore/Cloudinary/` (hosted alternative) are sketched but not built — trigger-gated on deployment demand.

## See also

- [docs/companions/asset-store.md](../../docs/companions/asset-store.md) — full architecture walkthrough.

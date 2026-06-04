# Phase 39 — `IAssetStore` companion (consumer adoption)

**What changes.** SDK adds a new image-asset companion `ToolUp.AssetStore` (server-only, opt-in). Wraps the SDK's configured `IBlobStorage` with per-asset records, mandatory alt-text-at-upload validation, on-demand derivative generation (thumbnail / medium / OG / WebP / AVIF) cached by content-hash, and audit emission via `IAuditLog` under reserved `SourceModule = "_platform.assets"`.

**Default off.** `ServerConfig.AssetStore = NoAssetStore` is the default. Strip-imports byte-for-byte to the prior behaviour: deployments that don't opt in pay zero runtime cost (no DI registration, no handlers, no audit emission). A consumer can adopt the SDK update with zero migration work — the change is additive.

**Scope.** Forge SDK ships the companion + audit DU cases + `ServerConfig.AssetStore` gate + contract pack + this doc. Consumer adoption is purely opt-in: a consumer wanting CMS image uploads adds the companion and flips the gate.

## Consumer-side changes (zero migration; opt-in adoption only)

A consumer NOT shipping image uploads does nothing. Existing `ServerConfig` literals constructed via `{ ServerConfig.defaults with ... }` automatically pick up the new field at its `NoAssetStore` default. A consumer constructing `ServerConfig` from scratch (no `defaults` base) adds:

```fsharp
let cfg: ServerConfig = {
    // ...existing fields...
    AssetStore = NoAssetStore
}
```

## Opt-in adoption (for consumers wanting the substrate)

1. **Add the companion to the consumer's solution.**

   ```xml
   <ProjectReference Include="..\..\toolup-forge\src\ToolUp.AssetStore\ToolUp.AssetStore.fsproj" />
   ```

   Or, via the published NuGet (once Phase 11.C.3 cuts the GitHub Packages feed):

   ```xml
   <PackageReference Include="ToolUp.AssetStore" />
   ```

2. **Flip the `ServerConfig.AssetStore` gate.**

   ```fsharp
   { ServerConfig.defaults with AssetStore = EnabledAssetStore }
   ```

3. **Switch the composition root to `AssetStoreServerApp`** (mirrors `PublicRenderingServerApp` / `FormsServerApp` shape). For an app that previously used `ServerApp.run`:

   ```fsharp
   open ToolUp.AssetStore
   open ToolUp.AssetStore.AssetCompose

   AssetStoreServerApp.create ()
   |> AssetStoreServerApp.withConfig cfg
   |> AssetStoreServerApp.withAuth authProvider
   |> AssetStoreServerApp.withLogger logger
   |> AssetStoreServerApp.withStorage blobStorage
   |> AssetStoreServerApp.withOptions { AssetStoreOptions.defaults with MaxBytes = 50L * 1024L * 1024L }
   |> AssetStoreServerApp.withDerivativeProfile (DerivativeProfileId "podcast-card") podcastCardSpecs
   |> AssetStoreServerApp.addModules myModules
   |> AssetStoreServerApp.run
   ```

   `with*` helpers delegate to the wrapped base `ServerApp` so existing wiring carries through.

4. **Linux-deployment native dependency.** SkiaSharp ships managed bindings + per-RID native libs. Windows / macOS / dev hosts pick up the native assets bundled with the `SkiaSharp` package automatically. Linux containers must additionally reference:

   ```xml
   <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="3.119.2" />
   ```

   on the consumer's server fsproj. The companion fsproj deliberately does NOT declare this — kept host-specific so non-Linux dev environments don't drag in Linux natives.

5. **Browser-side upload.** The ToolUp.Remoting `IAssetApi` covers metadata + derivative reads. For browser file uploads use the multipart endpoint at `POST /api/assets/upload` (form fields: `file`, `altText`, optional `caption`, optional `profile`):

   ```javascript
   const fd = new FormData()
   fd.append("file", fileBlob)
   fd.append("altText", "Sales chart showing Q3 growth")
   fd.append("profile", "web-default")
   const response = await fetch("/api/assets/upload", { method: "POST", body: fd })
   ```

   Raw browser file uploads through ToolUp.Remoting were avoided deliberately — chunked-encoding via `FormData` POST is half the bytes and one less serialization layer.

## Verification

```bash
# In the consumer repo, after adopting:
dotnet build YourApp.sln
# Smoke-test: hit /api/assets/upload with curl + a small image.
curl -X POST http://localhost:5000/api/assets/upload \
  -F "file=@./test.jpg" \
  -F "altText=Smoke test"
# Expect 201 Created + JSON AssetRecord; audit trail at /api/audit shows AssetUploaded.
```

## Rollback

To roll back from `EnabledAssetStore` to `NoAssetStore`:

1. Flip the gate: `AssetStore = NoAssetStore`.
2. Revert to `ServerApp.run` if you swapped composition roots.

The blob layout (`assets/originals/`, `assets/records/`, `assets/derivatives/`) remains intact in the scope's container; re-enabling lights every record back up. To wipe the data, delete those three blob prefixes per scope via `IBlobStorage.Erase`.

## Compatibility

- `ToolUp.Platform.Core 0.x.y` — gains `AssetStoreMode` DU + `ServerConfig.AssetStore` field at minor bump.
- `ToolUp.Platform.Server 0.x.y` — gains two `AuditEvent` cases (`AssetUploaded` / `AssetDeleted`) + matching encode / decode branches at the same minor bump.
- External `IAuditSink` implementations consuming `AuditEvent` via the closed match will receive an `FS0025` warning on rebuild; the case for both is "PII-free recording-scope routing — no payload ScopeId" (see `DatadogLogsAuditSink.fs` for the precedent). Add the two cases to existing dispatch matches; semantics are identical to the conversation lifecycle audit cases.
- Consumer apps shipping image uploads gain a `withAssetStore` step at compose time.

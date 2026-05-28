# Changelog — ToolUp.AssetStore

All notable changes to the `ToolUp.AssetStore` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.2.3]

- Initial public release. Phase 39 `IAssetStore` substrate:
  image-upload pipeline with derivative generation, alt-text-first
  metadata surface, audit emission via `AssetUploaded` / `AssetDeleted`
  events. Default `IDerivativeRenderer` is SkiaSharp-backed (MIT,
  cross-platform). Linux containers must additionally reference
  `SkiaSharp.NativeAssets.Linux` at consumer level — kept host-specific
  so non-Linux dev hosts don't drag in Linux natives.

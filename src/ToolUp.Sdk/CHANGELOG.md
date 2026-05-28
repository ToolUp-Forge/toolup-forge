# Changelog — ToolUp.Sdk

`ToolUp.Sdk` is a source-less coordinated-release meta-manifest: a single
`<ToolUpSdkVersion>` property resolves every `ToolUp.*` package at the
same version. This changelog records the coordinated SDK line as a whole;
per-package detail lives in the `CHANGELOG.md` beside each package's
fsproj. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.2.3]

- Meta-manifest expanded to cover the full shipped package surface.
  Eleven packages added that had been shipping but were not resolvable
  via `<ToolUpSdkVersion>`: `ToolUp.Platform.Testing` (Phase 11a
  module-testing scaffold), `ToolUp.PublicRendering` (Phase 38
  website-class SSR), `ToolUp.AssetStore` (Phase 39 image-uploads +
  derivatives), `ToolUp.Reporting.Core` + `ToolUp.Reporting.Server`
  (Phase 23 MVP — Markdown + HTML zero-dep renderers),
  `ToolUp.AIProviders.Claude.Client` (client-surface split out of
  `ToolUp.AIProviders.Claude`), `ToolUp.AI.SampleClientTool.{Core,
  Server,Client}` (Phase 46.B reference-only companion exercising
  the `IClientToolAuthorizer` seam end-to-end),
  `ToolUp.Rerankers.Local` (Phase 14f ONNX cross-encoder reranker),
  `ToolUp.Secrets.AwsSecretsManager`, `ToolUp.Secrets.HashiCorpVault`
  (additional `ISecretStore` companions). Consumers bumping
  `<ToolUpSdkVersion>` to `0.2.3` now resolve all of these in
  lockstep without per-package version overrides.

## [0.1.2]

- Coordinated SDK release rolling up Phase 6h.B AI-chat polish, Phase 9h
  Data Subject Request orchestrator, Phase 23 `ToolUp.Reporting`
  companion, Phase 37 Peer-Bearer-Auth foundation, and the Phase 2a/3a
  secret-store / auth-provider additions. See per-package CHANGELOGs for
  detail.

## [0.1.1] - 2026-05-11

- `ToolUp.AIProviders.Claude.Client` introduced via the client-surface
  split out of `ToolUp.AIProviders.Claude`.

## [0.1.0] - 2026-05-11

- Initial public release.

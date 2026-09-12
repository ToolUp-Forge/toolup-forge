# Changelog — ToolUp.EmbeddingProviders.OpenAI

All notable changes to the `ToolUp.EmbeddingProviders.OpenAI` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

- Every provider built through `create` / `createWithModel` /
  `createWithBatchSize` / `createWithOptions` now shares one process-wide
  `HttpClient` instead of constructing one per instance. A client per
  instance owns its own connection pool, whose sockets linger in
  TIME_WAIT, so a deployment building a provider per ingest batch could
  exhaust the process's ephemeral ports.
- `EmbedderResilience.RequestTimeout` is consequently enforced per
  request (it now bounds the response-body read too) rather than as
  `HttpClient.Timeout`. Same default, same behaviour on a hung
  connection.
- Added `createWithClient`, which builds a provider over a
  caller-supplied `HttpClient` — a proxy, an alternative base address, an
  `IHttpClientFactory`-managed handler, or a stub `HttpMessageHandler` in
  a test. The caller owns the client's lifetime.
- Documented that circuit-breaker state is scoped to the provider
  INSTANCE — not the process, not the fleet — and that the per-call
  secret read means a long-lived instance already honours key rotation.

## [0.1.2]

Coordinated SDK release. No package-specific source changes since 0.1.0;
the version moved in lockstep with the `ToolUp.Sdk` meta-manifest.

## [0.1.0] - 2026-05-11

- Initial public release.

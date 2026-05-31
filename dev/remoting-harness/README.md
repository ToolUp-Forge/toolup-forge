# ToolUp.Remoting harness — private dev tool

A small, runnable test harness inside `toolup-forge/dev/` that exercises **ToolUp.Remoting** (the workspace `toolup-remoting/` sibling, published as `ToolUp.Remoting.* v0.1.0` to `local-nuget-feed/` post Phase 69 ship) against **forge's integration patterns** — `Api.make` wrapper shape, body normalisation, error envelope, per-request context, telemetry hooks — *before* we flip forge's SDK companions over in [Phase 69a](../../../ToolUp-Diametrical/roadmap/phases/69a-forge-sdk-adoption-toolup-remoting.md).

The harness is **private** — `<IsPackable>false</IsPackable>`, not in `ToolUp.Forge.sln`, has its own `ToolUp.Remoting.Harness.sln`. It does not ship in any forge nupkg. The OSS publication boundary still applies (no Diametrical / Concord / Fern / Xcelsys / cookbook-apps references).

## Why this exists

The 9-phase ToolUp.Remoting follow-on cluster (Phases 69 / 69a / 69b / 69c / 69d / 69e / 69f / 69g / 69h / 69i / 69j / 69k — see [`application-plans/toolup-remoting.md`](../../../ToolUp-Diametrical/application-plans/toolup-remoting.md)) needs an integration testbed that:

1. **Is iterable** — change a converter, change a dispatcher option, change a middleware seam, see the effect in <30s with `dotnet run`.
2. **Mirrors forge's actual integration shape** — same `Api.make` wrapper pattern, same Giraffe middleware pipeline, same error-handler signature. Tests written here become the regression gate for the eventual SDK flip.
3. **Doesn't touch forge SDK code** until each seam is validated. The harness lives orthogonal to forge's existing `Fable.Remoting.Giraffe` consumption; we don't perturb `ToolUp.Platform.Server/Server/Api.fs` until the harness proves the replacement seam works.
4. **Grows phase-by-phase.** Each new Phase 69b/c/d/... seam adds tests here first; once green, the seam migrates into `ToolUp.Remoting.*` and forge SDK adopts it.

## What's in the harness today

**v0 — the foundation (this commit):**
- A single API record `IHarnessApi` exercising three method shapes:
  - `Echo: string -> Async<string>` — basic round-trip; validates the dispatcher + STJ string converter + arg-array body normalisation.
  - `Heartbeat: unit -> Async<DateTimeOffset>` — `unit`-input method; validates body normalisation for empty/`"null"`/`""` request bodies and STJ `DateTimeOffset` round-tripping (the upstream PR notes this is fragile on the legacy Newtonsoft path).
  - `Boom: string -> Async<int>` — handler throws; validates error envelope shape on `ErrorResult.Propagate`.
- Forge-shaped server composition: `Api.make` wrapper pattern (mimics [`toolup-forge/src/ToolUp.Platform.Server/Server/Api.fs`](../../src/ToolUp.Platform.Server/Server/Api.fs)) + body-normalisation middleware (current behaviour from [`Middleware.fs`](../../src/ToolUp.Platform.Server/Server/Middleware.fs)).
- Expecto suite booting a `TestServer` in-process and asserting wire shape via `HttpClient`.

**Phase coverage targets (added as phases land):**
- Phase 69b — categorised error envelope, per-request context, telemetry hook, correlation-id ambient propagation.
- Phase 69c — `AsyncSeq<'T>` streaming via SSE.
- Phase 69d — authorization metadata + startup classifier rejection.
- Phase 69e — typed validation + per-field violation envelope.
- Phase 69f — idempotency-key memoisation + replay-audit.
- Phase 69g — per-method rate limit + RetryAfter envelope.
- Phase 69h — audit emission + PII safeguard redaction.
- Phase 69i — `JobHandle<'T>` long-running operation + polling + cancellation.
- Phase 69j — `__schema_version` envelope + multi-version dispatch.
- Phase 69k — source-generator dispatch parity + AOT verification.

## How to run

```powershell
cd toolup-forge/dev/remoting-harness
dotnet run
```

Expecto console runner — exits non-zero on any failure. Add `--summary` for verbose output, `--filter <test-name>` to run a single test.

## How it consumes ToolUp.Remoting

`PackageReference`s `ToolUp.Remoting.{Server,Json,Giraffe} v0.1.0`, resolved via the forge [`nuget.config`](../../nuget.config) `local` source (`../local-nuget-feed/`). Phase 69 packed the family into the local feed; subsequent Phase 69b–69k seam work iterates by packing fresh into the same local feed (`dotnet pack` in `toolup-remoting/`), bumping a per-package version when needed, and rebuilding the harness.

Pre-Phase 69 the harness consumed via `ProjectReference` to the fork sibling. The flip to `PackageReference` post-Phase-69 means the harness exercises the production-shape consumption path forge SDK companions will use after Phase 69a's adoption sweep — same code path the eventual GH Packages cloud feed will hit.

## What's not here

- **Unit tests of the converters / dispatcher internals** — those live in the fork's own test suites (`Fable.Remoting.Json.Tests` etc., 704 tests). The harness is *integration*-shaped, not internal-unit-shaped.
- **A Fable client.** The harness uses `HttpClient` directly to drive the server, asserting wire shape byte-for-byte. A Fable client would test the upstream `Fable.Remoting.Client` proxy behaviour — that's not the regression we're guarding. (The eventual `ToolUp.Remoting.Client` plugin question stays deferred per [Phase 69](../../../ToolUp-Diametrical/roadmap/phases/69-toolup-remoting-repackage-from-fable-remoting-fork.md)'s Out-of-Scope.)
- **Build pipeline / CI integration.** This is a dev-only harness today; no CI gate. When Phase 69a is ready to ship, the harness joins the forge build's verification chain.

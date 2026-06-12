# Phase 69 family — `ToolUp.Remoting` platform seams: adoption overview

Start here. This page maps the whole Phase 69 family — the in-tree `ToolUp.Remoting` transport plus the declarative per-method platform seams layered onto its dispatcher — and tells a consumer what to adopt, in what order, and where each per-phase recipe lives.

## What the family is

Phase 69 repackaged the transport as a forge-owned in-tree fork (`ToolUp.Remoting.*` namespaces, distributed inside `ToolUp.Platform.{Core,Client,Server}`). Phases 69b–69k then layered **attribute-driven, per-method platform seams** onto the server dispatcher, all default-off or zero-cost-when-unused (GP 11 / GP 13): a method opts in by carrying an attribute, a deployment opts in by composing a store/emitter via `Remoting.with*`. The substrate for every seam lives under `src/ToolUp.Platform.Server/Server/Remoting/` (one file per seam: `Auth.fs`, `Validation.fs`, `Idempotency.fs`, `RateLimit.fs`, `Audit.fs`, `Jobs.fs`, `Streaming.fs`, `SourceGenDispatch.fs`, `CallContext.fs`, `Diagnostics.fs`, `Errors.fs`).

## Family map

| Phase | Seam | Consumer action | Recipe |
|---|---|---|---|
| 69b | Platform seams (body norm, per-request context, telemetry, correlation, categorised errors) | Package bump — `Api.make` auto-composes (0.4.4) | [69b-remoting-platform-seams.md](69b-remoting-platform-seams.md) |
| 69c | Streaming results (`AsyncSeq` over SSE) | Opt-in per method; adopt when a long result wants progressive delivery | doc lands with the 69c adoption tail |
| 69d | Authorization metadata (`[<RequiresRole>]` / `[<AllowAnonymous>]` / classifier) | Annotate API records; classifier default-on at 0.5.0 | `69d-authorization-metadata.md` (lands with the 0.5.0 annotation sweep) |
| 69e | Typed validation on input records | Annotate input-record fields; default-on when attributes exist | [69e-typed-validation.md](69e-typed-validation.md) |
| 69f | Idempotency keys (`[<Idempotent>]` + `IIdempotencyStore`) | Mark mutating methods + compose a store | [69f-idempotency-keys.md](69f-idempotency-keys.md) |
| 69g | Rate-limit attribution per method/subject | Compose a rate-limit store; operator surface is a follow-on tail | doc lands with the 69g tail |
| 69h | Audit emission (`[<Audit>]` + `IAuditEmitter`) | Annotate compliance methods; sweep lands at 0.5.0 | `69h-audit-annotation-sweep.md` (lands with the 0.5.0 annotation sweep) |
| 69i | Long-running typed handles (`JobHandle<'T>`) | Opt-in per long-op method via `IJobDispatcher` | [69i-long-running-handle.md](69i-long-running-handle.md) |
| 69j | Wire schema versioning (`withSchemaVersion`) | Bump only when shipping a wire evolution | covered by the seam helper's doc comment |
| 69k | Source-generator-driven dispatcher | Nothing yet — runtime contract shipped, generator is a follow-up | [69k-source-generator-dispatcher.md](69k-source-generator-dispatcher.md) |
| 69l / 69m / 69n | Dispatcher perf-shrink (zero-cost telemetry gate, body/arg fast-path, build-once table) | None — internal | [69l](69l-telemetry-zero-cost-gate.md) / [69m](69m-dispatcher-body-and-arg-fastpath.md) / [69n](69n-fromcontextasync-build-once.md) |
| 69o | Client proxy convention (module-level values) | Mechanical client-side sweep | [69o-client-proxy-per-call-header-read.md](69o-client-proxy-per-call-header-read.md) |

## Recommended adoption sequence

1. **69b.tail first** — a package bump to 0.4.4+; the dispatcher seams light up with no code change. Everything below assumes it.
2. **69d.tail + 69h.tail together** — the two annotation sweeps (authorization metadata, audit classification) touch the same API records, so one pass over your API surface covers both. Their per-phase docs ship with the 0.5.0 release.
3. **69g.tail** — rate-limit attribution, once the operator-facing surface lands.
4. **69c.tail** — streaming adoption, per method, when a result wants progressive delivery.
5. **69e / 69f / 69i / 69k as triggers arise** — typed validation when a "forgot to validate" defect surfaces; idempotency when a retry path can double-mutate; `JobHandle<'T>` when a hand-rolled poll endpoint repeats itself; the source-gen dispatcher when cold-start or AOT matters. Each recipe explains its trigger.

The pre-flight chain runs in a fixed order on every dispatch: **auth (69d) → validation (69e) → idempotency (69f) → rate-limit (69g) → handler → audit (69h) → telemetry (69b)**. Seams you haven't composed cost nothing — classification happens once at startup and unattributed methods miss fast.

## Verification

Each per-phase doc carries its own steps. The family-wide gate is unchanged: `dotnet build` against your solution, then exercise one method per adopted seam and confirm the expected envelope / header / audit row.

## See also

- Substrate sources under `src/ToolUp.Platform.Server/Server/Remoting/`.
- Workspace [`SDK-ADOPTION.md`](../../../SDK-ADOPTION.md) for cross-consumer adoption status (one row per item above).

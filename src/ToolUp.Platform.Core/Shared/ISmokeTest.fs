// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SmokeTests

open System

// ─── ISmokeTest — extensibility (Phase 9o) ───────────────────────────
//
// Portable post-deploy smoke-test interface for the SDK. Companions
// implement this and self-register via
// `services.AddSingleton<ISmokeTest>(...)`; the SDK aggregator at
// `GET /api/_internal/smoke` (`SmokeTestHandler`) walks the service
// collection, runs every probe in parallel, and reports 200 if all
// `Pass` or 503 with per-test detail otherwise. Token-gated via
// `TOOLUP_SMOKE_TOKEN`; opt-in via
// `ServerConfig.SmokeTest = EnabledSmokeTest` (default `NoSmokeTest`,
// GP 13).
//
// ## Different from `IHealthCheck`
//
// `IHealthCheck` is a per-component liveness/readiness probe polled
// by the orchestrator on every interval — it answers "can the
// dependency be reached?" cheaply, with no side effects on the
// integration path. `ISmokeTest` is an end-to-end exercise of a
// wired companion: write a sentinel and read it back, publish a
// sentinel notification and observe receipt, schedule and dispatch a
// sentinel job. It catches half-wired companions where the
// individual probe passes (credentials resolve, the SDK starts) but
// the integration is broken (bucket policy denies reads,
// notification handler never fires). Run once, post-deploy, before
// the load balancer is pointed at the new instance.
//
// ## Sentinel-scope contract (non-negotiable)
//
// Every smoke test runs against the reserved scope `_smoke`.
// Sentinel writes never land in a real tenant scope, so a smoke run
// cannot corrupt tenant data. Implementations that take a
// `scopeId`-shaped parameter from their underlying interface pass
// the literal `"_smoke"`. The cleanup invariant — "each smoke test
// must clean up after itself" — is what the smoke endpoint asserts
// of every registered impl. A failing impl is still expected to
// clean up via `try`/`finally` (delete the sentinel blob even when
// read-back fails); the dispatcher does not compensate.
//
// ## Six-rule portability audit (the portability contract)
//
//   1. Identity by value      — `Name : string` is the registration
//                                 key. No live framework handle on
//                                 the surface.
//   2. Async                  — `RunOnce : unit -> Async<SmokeResult>`.
//                                 No sync escape hatch.
//   3. Retry as data          — no built-in retry. A smoke endpoint
//                                 is a deploy-time go/no-go gate;
//                                 retry on failure is the deploy
//                                 pipeline's concern, not the SDK's.
//   4. Stateless handlers     — every `RunOnce ()` invocation must
//                                 read fresh dependency state. No
//                                 in-memory continuity between calls.
//   5. No cross-shard ordering — smoke tests are independent; the
//                                 dispatcher runs them in parallel
//                                 and reports per-test outcomes.
//   6. Precision at the lower bound — the dispatcher applies its
//                                 own ceiling (`SmokeTest.
//                                 defaultTimeout`) per test.

/// Two-valued smoke-test outcome. The dispatcher translates these
/// to 200 ("all Pass") or 503 ("at least one Fail, here's the
/// detail"). There is deliberately no `Degraded` half-state — a
/// smoke gate is either "ready for traffic" or "do not route".
type SmokeResult =
    | Pass
    | Fail of message: string

/// A post-deploy smoke test contributed by a companion or the SDK
/// itself. Implementations exercise the wired integration end-to-end
/// against the reserved sentinel scope (`"_smoke"`) and clean up
/// after themselves. The contract is: by the time `RunOnce` returns,
/// any sentinel state it wrote has been removed; a failing impl
/// still cleans up via `try`/`finally`, the dispatcher never
/// compensates.
type ISmokeTest =
    /// Stable identifier surfaced in the smoke-endpoint JSON
    /// response and used as the per-test reporting key. Must be
    /// unique across all registered smoke tests; companions that may
    /// register more than one instance (e.g. multiple cloud-storage
    /// backends) should suffix the instance id (e.g.
    /// `"blob_storage:s3"`).
    abstract member Name: string

    /// Run the smoke test once. Implementations exercise the
    /// integration end-to-end against the sentinel scope
    /// (`"_smoke"`), clean up any state written, and return `Pass`
    /// or `Fail message`. Cleanup happens inside `RunOnce` itself
    /// (try/finally) — the dispatcher does not run a separate
    /// teardown pass on failure.
    abstract member RunOnce: unit -> Async<SmokeResult>

module SmokeResult =
    /// String form of the variant — used in the JSON response and
    /// in the diagnostics audit event payload. Stable wire format:
    /// external deploy-pipeline scripts may key off these strings.
    let status =
        function
        | Pass -> "Pass"
        | Fail _ -> "Fail"

    let message =
        function
        | Pass -> ""
        | Fail m -> m

module SmokeTest =
    /// Reserved sentinel scope every smoke test runs against. Smoke
    /// impls pass this literal to their backing interface's
    /// `scopeId`-shaped parameter; a sentinel write to `"_smoke"`
    /// must not appear in any real tenant scope's listing — that is
    /// the scope-isolation contract every backing store already
    /// honours.
    [<Literal>]
    let SentinelScope = "_smoke"

    /// Reserved `SourceModule` label used by the smoke-endpoint
    /// audit-event emission. Mirrors the `_platform.diagnostics`
    /// label used by the diagnostic-bundle endpoint (Phase 9n) and
    /// the dev-diagnostics surface — keeps deploy-time observability
    /// events together under one source-module key.
    [<Literal>]
    let AuditSourceModule = "_platform.diagnostics"

    /// Reserved `EventType` for the per-invocation smoke-run audit
    /// event. One row per `/api/_internal/smoke` invocation,
    /// recording the aggregate outcome + per-test details.
    [<Literal>]
    let AuditEventType = "SmokeTestRun"

    /// Default per-test ceiling the dispatcher caps each `RunOnce`
    /// at. Picked to match a generous integration-exercise budget
    /// (10s); probes that exceed it report `Fail "probe exceeded
    /// {ms}ms"`.
    let defaultTimeout = TimeSpan.FromSeconds 10.0
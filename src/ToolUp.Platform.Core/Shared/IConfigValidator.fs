// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConfigValidation

open System

// ─── IConfigValidator — startup config preflight ─────────────────────
//
// Companion-self-registered interface that runs ONCE at the end of
// `compose`, before `app.Run()`. Validators that return `Error` abort
// startup with a `ConfigPreflightFailedException` carrying a multi-line
// summary so the orchestrator's deploy-failure detection picks up the
// non-zero exit. Warnings log at `Warn` level and continue. Ok-results
// are the silent default.
//
// `ServerConfig.SkipPreflight = true` skips only the non-security-class
// validators; the auth / secret / cross-instance-auth-state guards
// (`ConfigValidatorAggregator.securityClassValidatorNames`) always run
// and still abort on `Error`. One boolean must not silently disable the
// identity-spoofing / unauthenticated-access protection.
//
// The pattern is a near-clone of `IHealthCheck`, with two
// intentional divergences:
//   1. One-shot, not poll-driven. `Validate ()` runs once at compose
//      end, never again. Heavier per-validator work is acceptable
//      (sentinel write/read/delete vs the cheap `Exists` of the
//      health-check blob probe).
//   2. Aborts startup on `Error`, not on `Unhealthy`. The aggregator
//      throws before `app.Run()` so Kestrel never binds.
//
// Six-rule portability audit (the portability contract):
//   1. Identity by value      — `Name : string` is the registration
//                                 key. No live framework handle on the
//                                 surface.
//   2. Async                  — `Validate : unit -> Async<ValidationResult>`.
//                                 No sync escape hatch.
//   3. Retry as data          — no built-in retry. Deployment pipeline
//                                 retries by re-running compose; a
//                                 future cron-driven re-validate could
//                                 layer on top via `IJobScheduler`.
//   4. Stateless handlers     — validators run once per process;
//                                 `Validate ()` reads fresh state. No
//                                 in-memory continuity contract between
//                                 calls (Orleans / Akka.Persistence
//                                 implementations may run validators on
//                                 different nodes per restart).
//   5. No cross-shard ordering — validators run in parallel; outcomes
//                                 are independent. No validator-to-
//                                 validator ordering claim.
//   6. Precision at the lower bound — `Timeout : TimeSpan` per
//                                 validator (mirrors the `IHealthCheck`
//                                 Rule 6 closure exactly). Aggregator
//                                 caps any declared timeout at the 10s
//                                 overall budget so a misconfigured
//                                 validator cannot block startup
//                                 indefinitely.

/// Three-valued validator outcome. The aggregator translates these to
/// log entries (`Ok` → Info, `Warning` → Warn) and to abort behaviour
/// (`Error` → throw `ConfigPreflightFailedException`). `Warning` is a
/// deliberate halfway state — the dependency is reachable but flagged
/// (slow handshake, missing optional feature, deprecated config). It
/// does NOT abort startup; only `Error` does.
type ValidationResult =
    | Ok
    | Warning of message: string
    | Error of message: string

/// A startup config probe contributed by a companion or the SDK
/// itself. Implementations are typically heavier than `IHealthCheck`
/// probes — preflight runs once at compose end, never on the request
/// path, so a sentinel write/read/delete is fine. Implementations
/// should be safe to invoke concurrently — the aggregator runs every
/// validator in parallel so a misconfigured slow validator doesn't
/// extend startup beyond the global budget.
type IConfigValidator =
    /// Stable identifier used in log lines, abort summaries, and the
    /// `/dev/inspect` validators panel. Must be unique across all
    /// registered validators — companions that may register more than
    /// one instance (e.g. multiple OIDC issuers) should suffix the
    /// instance id, e.g. `"oidc-auth (https://login.example.com)"`.
    abstract member Name: string

    /// Maximum wallclock the aggregator allows for a single `Validate`
    /// invocation. The aggregator caps each validator at this duration
    /// AND at the global 10s aggregator budget — whichever is smaller
    /// wins. A validator declaring 60s gets 10s in practice; a
    /// validator declaring 200ms gets 200ms. Use
    /// `IConfigValidator.defaultTimeout` (5s) unless the probe has a
    /// well-characterised performance bound.
    abstract member Timeout: TimeSpan

    /// Run the validator. Implementations must be safe to invoke
    /// concurrently (the aggregator runs every validator in parallel)
    /// and must not assume in-memory state between calls.
    abstract member Validate: unit -> Async<ValidationResult>

module ValidationResult =
    /// String form of the variant — used in log lines, abort summary,
    /// and the `/dev/inspect` JSON. Stable wire format: external
    /// monitors may key off these strings.
    let status =
        function
        | Ok -> "Ok"
        | Warning _ -> "Warning"
        | Error _ -> "Error"

    let message =
        function
        | Ok -> ""
        | Warning m -> m
        | Error m -> m

module IConfigValidator =
    /// Default per-validator timeout. Picked to match the
    /// `IHealthCheck` default (5s) — most preflight probes (TCP
    /// connect, HTTP GET, blob round-trip) finish well under that
    /// bound on a healthy network.
    let defaultTimeout = TimeSpan.FromSeconds 5.0
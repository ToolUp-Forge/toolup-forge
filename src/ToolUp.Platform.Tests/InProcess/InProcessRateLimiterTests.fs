module ToolUp.Platform.Tests.InProcess.InProcessRateLimiterTests

open System
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Tests.Contracts

// ─── In-process binding — IRateLimiterContract ───────────────────────
//
// Binds the Phase 9v contract pack to `InProcessRateLimiter`. The
// contract tests assert behavioural shape (admit, soft-ceiling wait,
// long-window refuse, fairness, sub-key partitioning, identity-by-value)
// independent of observability — so the binding wires `NoOpMetricsSink`
// and `NoOpAuditLog` to keep the tests focused on the limiter's gating
// semantics rather than its emission side-effects.

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

let private factory (descriptors: RateLimitDescriptor list) : IRateLimiter =
    InProcessRateLimiter(
        descriptors,
        NoOpMetricsSink() :> IMetricsSink,
        AuditLog.NoOpAuditLog() :> IAuditLog,
        TimeSpan.FromSeconds 5.0,
        noopLogger
    )
    :> IRateLimiter

let tests = IRateLimiterContract.tests "InProcessRateLimiter" factory
module ToolUp.Platform.Tests.InProcess.PrometheusMetricsSinkTests

open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Tests.Contracts

// ─── In-process binding — IMetricsSinkContract ──────────────────────
//
// Binds the Phase 9e contract pack to `PrometheusMetricsSink`. The
// `render` thunk in the factory tuple calls `sink.Render()` directly
// (the contract is asserted against OpenMetrics text). Tests run
// without a flush delay since the sink is fully synchronous.

let private factory (cfg: MetricsSinkConfig) (regs: MetricRegistration list) (logger: ILogger) =
    let sink = PrometheusMetricsSink(cfg, regs, logger)
    sink :> IMetricsSink, fun () -> sink.Render()

let tests = IMetricsSinkContract.tests "PrometheusMetricsSink" factory
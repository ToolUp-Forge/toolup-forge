module ToolUp.AI.AILatencyMetrics

open ToolUp.Platform.Metrics

// ─── Phase 9e wire-up for the Phase 6i.A latency telemetry ──────────
//
// The agent loop emits one `AILatencyRecord` per turn through
// `IEventStore` under `_platform.ai.latency` (always-on; see
// `AIAgentEngine.fs.emitLatency`). The `/dev/ai-latency` rollup
// endpoint reads those events back for operator inspection.
//
// Phase 9e (Metrics / OpenTelemetry sink) shipped 2026-05-12 forge
// `12ae4a9`. This module registers the three `toolup.ai.*` SDK-owned
// metrics that the in-process `PrometheusMetricsSink` (and any
// companion sinks like `OtelMetricsSink`) require for emission to
// flow rather than silently drop. `AIServerApp.create` adds the
// list to `ServerApp.MetricRegistrations` so any AI-using deployment
// has the metrics declared the moment it composes — no consumer
// opt-in needed.
//
// All three are histograms in the `ms` / `1` unit; bucketing uses
// `MetricDefinition.defaultLatencyBucketsMs` for the two latency
// metrics (matches `toolup.requests.latency_ms`) and a 0..1 fractional
// scale for the cache-hit ratio (the cached-prompt-token share).
// Tags are limited to `provider` + `model` — matching the per
// (`ProviderName`, `ProviderModel`) breakdown axis on the
// `/dev/ai-latency` rollup; future tags require a deliberate
// extension of the allowlist on the corresponding `MetricDefinition`.

/// Per-turn duration histogram. Records `TurnDurationMs` from every
/// `AILatencyRecord` emitted by `AIAgentEngine.runAgentLoop`.
[<Literal>]
let TurnDurationMs = "toolup.ai.turn.duration.ms"

/// Time-to-first-token histogram. Records `TtftMs` when the provider
/// streams a non-empty first delta; omitted for turns where TtftMs is
/// `None` (e.g. provider-side error before first token).
[<Literal>]
let TtftMs = "toolup.ai.ttft.ms"

/// Cached-prompt-ratio histogram. Records the fraction of prompt
/// tokens that came from the provider's cache (`CachedPromptTokens
/// / PromptTokens`) — a Phase 6i.B observability surface tracking
/// the value of Anthropic prompt caching. Recorded only when both
/// token counts are present and `PromptTokens > 0`.
[<Literal>]
let CachedPromptRatio = "toolup.ai.cached_prompt_ratio"

/// 0..1 bucket boundaries for the cached-prompt-ratio histogram.
/// Sized to make "what fraction of turns are >= 80% cached?" answerable
/// at a glance — caching's value is concentrated in the long-prefix
/// reuse range (typing-then-talking, follow-up turns over the same
/// system prompt + tools).
let cachedPromptRatioBuckets: float list = [ 0.0; 0.1; 0.25; 0.5; 0.75; 0.9; 1.0 ]

/// SDK-owned AI latency registrations. Wired by `AIServerApp.create`
/// into `ServerApp.MetricRegistrations` so the in-process
/// `PrometheusMetricsSink` allocates the series at compose time and
/// emissions from `AIAgentEngine.runAgentLoop.emitLatency` flow.
let registrations: MetricRegistration list = [
    {
        Module = None
        Definition = {
            Name = TurnDurationMs
            Kind = Histogram MetricDefinition.defaultLatencyBucketsMs
            Description = "AI agent-loop turn duration in milliseconds (one record per turn)"
            Unit = "ms"
            Tags = [ "provider"; "model" ]
        }
    }
    {
        Module = None
        Definition = {
            Name = TtftMs
            Kind = Histogram MetricDefinition.defaultLatencyBucketsMs
            Description = "AI time-to-first-token in milliseconds (first non-empty streaming delta)"
            Unit = "ms"
            Tags = [ "provider"; "model" ]
        }
    }
    {
        Module = None
        Definition = {
            Name = CachedPromptRatio
            Kind = Histogram cachedPromptRatioBuckets
            Description = "Fraction of prompt tokens served from the provider's prompt cache (0..1)"
            Unit = "1"
            Tags = [ "provider"; "model" ]
        }
    }
]
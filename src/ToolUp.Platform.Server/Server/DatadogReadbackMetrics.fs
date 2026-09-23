// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.Metrics

// ─── Datadog-readback metric declarations (Phase 9w) ──────────────────
//
// Declared here rather than beside the handlers for the reason Phase 740
// gives for `EdgePurgeMetrics`: a series the registry does not carry is
// SILENTLY DROPPED by the sink, so the declarations have to compile
// ahead of `MetricsMiddleware.fs` to be spliced into
// `StandardMetrics.registrations`. The handler file that emits them
// compiles later and references these literals.

/// Metric names and tag keys for the Datadog readback surface — the
/// observability of the observability readback itself.
[<RequireQualifiedAccess>]
module DatadogReadbackMetrics =

    /// Counter — one increment per readback call that reached the
    /// composed `IDatadogReadbackApi`, whatever its outcome. The
    /// denominator for the error counter below.
    [<Literal>]
    let RequestsTotal = "toolup.datadog_readback.requests_total"

    /// Counter — readback calls that returned a `DatadogReadbackError`.
    /// A non-zero rate against a flat `RequestsTotal` means the admin
    /// surface is rendering its degraded state rather than data.
    [<Literal>]
    let ErrorsTotal = "toolup.datadog_readback.errors_total"

    /// Tag key naming which of the three endpoints was called.
    /// Bounded by construction — three literal values below, never a
    /// caller-supplied string.
    [<Literal>]
    let EndpointTagKey = "endpoint"

    /// Tag key carrying the failure class. Present on `ErrorsTotal`
    /// only; its values are `DatadogReadbackError.statusTag`'s three,
    /// which are likewise bounded by construction.
    [<Literal>]
    let StatusTagKey = "status"

    /// The monitors endpoint's `endpoint` tag value.
    [<Literal>]
    let EndpointMonitors = "monitors"

    /// The log-search endpoint's `endpoint` tag value.
    [<Literal>]
    let EndpointLogs = "logs"

    /// The metric-query endpoint's `endpoint` tag value.
    [<Literal>]
    let EndpointMetric = "metric"

    /// Declarations, spliced into `StandardMetrics.registrations` so a
    /// deployment with a metrics endpoint carries both series without
    /// composing anything.
    let registrations: MetricRegistration list = [
        {
            Module = None
            Definition = {
                Name = RequestsTotal
                Kind = Counter
                Description = "Datadog readback calls dispatched (tags: endpoint)"
                Unit = "1"
                Tags = [ EndpointTagKey ]
            }
        }
        {
            Module = None
            Definition = {
                Name = ErrorsTotal
                Kind = Counter
                Description =
                    "Datadog readback calls that failed "
                    + "(tags: endpoint + status=missing_credential|api_error|transport). "
                    + "A sustained rate means the admin surface is showing its degraded state, not Datadog's data."
                Unit = "1"
                Tags = [ EndpointTagKey; StatusTagKey ]
            }
        }
    ]
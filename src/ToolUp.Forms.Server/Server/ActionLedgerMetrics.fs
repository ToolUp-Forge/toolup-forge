module ToolUp.Forms.ActionLedgerMetrics

open ToolUp.Platform.Metrics

// ─── Phase 21d — Workflow-action outcome metric registrations ──────
//
// The `WorkflowEngine` emits one `Increment` per action outcome
// through `IMetricsSink`. Wired through `FormsCompose.run` into
// `ServerApp.MetricRegistrations` so `PrometheusMetricsSink` /
// `OtelMetricsSink` pre-allocate the series at compose time and
// emissions flow rather than silently drop. Same pattern as
// `AILatencyMetrics.registrations`.

/// Per-action-outcome counter. Tagged with the action's registered
/// name + the outcome status (`succeeded` / `failed` /
/// `skipped_replay` / `skipped_pending`). One emission per
/// `WorkflowEngine.Apply` that names an action — covers replay
/// skips, pending skips, success, and failure paths.
[<Literal>]
let ActionOutcomeTotal = "toolup.forms.workflow.action.outcome"

/// SDK-owned forms-action registrations. Wired by `FormsCompose.run`
/// into `ServerApp.MetricRegistrations` so any Forms-using
/// deployment has the counter declared the moment it composes — no
/// consumer opt-in needed.
let registrations: MetricRegistration list = [
    {
        Module = None
        Definition = {
            Name = ActionOutcomeTotal
            Kind = Counter
            Description =
                "Workflow action invocation outcome counter "
                + "(tags: actionName + status=succeeded|failed|skipped_replay|skipped_pending)"
            Unit = "1"
            Tags = [ "actionName"; "status" ]
        }
    }
]

// ─── Status-tag string constants ────────────────────────────────────
//
// Keep these constants in lockstep with `WorkflowActionExecutedPayload
// .Status` — the audit and metric surfaces share the same vocabulary
// so dashboards stay consistent.

[<Literal>]
let StatusSucceeded = "succeeded"

[<Literal>]
let StatusFailed = "failed"

[<Literal>]
let StatusSkippedReplay = "skipped_replay"

[<Literal>]
let StatusSkippedPending = "skipped_pending"
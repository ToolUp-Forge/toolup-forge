// Ambient context for `docs/forms/api-reference.md`.
//
// Most of the page is signature listings, which the gate skips as such.
// What is left are the handful of blocks that CALL something: the
// default `WorkflowEngine`'s ten constructor dependencies, the six
// client components, and one Elmish command. Each of those reads values
// a consuming deployment supplies — its own store, ledger, registries
// and DI provider; the schema and submissions it is rendering; its own
// `Msg` type. They are declared once here so the calls compile exactly
// as written, and so a rename on any of those SDK surfaces is caught by
// this page rather than discovered by a reader.
//
// SDK module opens stay in the BLOCKS where the page is teaching which
// module a name lives in — `ToolUp.Forms.WorkflowEngine` and
// `ToolUp.Elmish` are both part of what those two blocks say.
open System

[<AutoOpen>]
module PageAmbient =

    // ─── The default WorkflowEngine's ten dependencies ────────────

    let formStore: IFormStore.IFormStore = failwith "ambient"

    let auditLog: IAuditLog = failwith "ambient"

    let ledger: IActionLedger.IActionLedger = failwith "ambient"

    let metricsSink: Metrics.IMetricsSink = failwith "ambient"

    let warn: WorkflowEngine.WorkflowWarn = failwith "ambient"

    let workflows: Map<Workflow.WorkflowId, Workflow.WorkflowDefinition> =
        failwith "ambient"

    let guards: Map<string, IWorkflowEngine.WorkflowGuard> = failwith "ambient"

    let actions: Map<string, IWorkflowEngine.WorkflowAction> = failwith "ambient"

    let actionPolicies: Map<string, Workflow.ActionFailurePolicy> = failwith "ambient"

    /// Resolved fresh per `Apply` call into each `WorkflowContext`, so a
    /// guard or action reaches DI without capturing at compose time.
    let services: IServiceProvider = failwith "ambient"

    // ─── What the client components are handed ────────────────────

    let schema: FormSchema = failwith "ambient"

    let onSubmit: Map<string, FieldValue> -> unit = failwith "ambient"

    let state: SubmissionState = failwith "ambient"

    let submissions: Submission list = failwith "ambient"

    /// `PublicEmbed`'s only parameter. The share token is NOT passed in —
    /// the component reads it off `window.location` itself.
    let appName: string = failwith "ambient"

    let aggregations: AggregationSummary = failwith "ambient"

    let surveys: FormApi.SurveyOverviewRow list = failwith "ambient"

    let onOpen: FormSchemaId -> unit = failwith "ambient"

    // ─── The Elmish command's own inputs ──────────────────────────

    /// The consuming module's messages. Named page-locally so it cannot
    /// shadow anything the SDK exports.
    type FormsPageMsg =
        | SubmitSucceeded of Result<Submission, FormError>
        | SubmitFailed of exn

    let request: FormApi.SubmitRequest = failwith "ambient"
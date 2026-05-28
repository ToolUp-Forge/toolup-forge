module ToolUp.Forms.FormStore

open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Metrics
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.IFormStore

// ─── Phase 21 — Default IFormStore implementation ───────────────────
//
// Pure persistence wrapper over Phase 19's `IEntityStore`. No audit
// emission here — handlers and the workflow engine emit their own
// audit events at the right semantic moment (Submit -> FormSubmitted
// in the API handler; UpdateDraft -> FormSubmissionUpdated in the
// API handler; transition -> WorkflowTransitioned in the workflow
// engine). The store is a stateless data-shape adapter.
//
// Error mapping: every `EntityError` from the underlying store is
// wrapped in `FormError.StorageFailed` (with the message rendered
// for display) or `FormError.NotFound` for missing entities.

let private mapEntityError (resource: string) (id: string) (err: EntityError) : FormError =
    match err with
    | EntityError.NotFound _ -> FormError.NotFound(resource, id)
    | other -> FormError.StorageFailed(EntityError.message other)

/// Lightweight warn callback, decoupled from `Microsoft.Extensions.Logging`
/// so the test project can construct a store without depending on the
/// logging-abstractions package. Compose roots wire this to
/// `logger.LogWarning(msg)`; tests pass `ignore`. Mirrors the
/// `WorkflowEngine.WorkflowWarn` convention.
type FormStoreWarn = string -> unit

/// Per-scope counter emitted when `ListSchemas` drops a schema due to
/// a per-entity load failure. Tagged with `scopeId` so an operator can
/// spot a single-tenant degradation rather than treating it as global
/// noise. Pre-allocated via `FormStoreMetrics.registrations` at compose
/// time so `PrometheusMetricsSink` / `OtelMetricsSink` see the series
/// declared even when emission count is zero.
[<Literal>]
let ListSchemasLoadFailedTotal = "toolup.forms.formstore.list_schemas.load_failed"

/// First-N entity IDs to surface in the structured warn message. Keeps
/// the log line bounded when a degraded tenant's entire schema table is
/// faulting; the metric counter retains the full count.
[<Literal>]
let private ListSchemasFailedIdSampleLimit = 5

/// SDK-owned metric registrations for `FormStore` so deployment-side
/// sinks pre-allocate the series at compose time. Wired through
/// `FormsCompose.run` into `ServerApp.MetricRegistrations`. Same
/// pattern as `ActionLedgerMetrics.registrations`.
let metricRegistrations: MetricRegistration list = [
    {
        Module = None
        Definition = {
            Name = ListSchemasLoadFailedTotal
            Kind = Counter
            Description =
                "FormStore.ListSchemas per-entity load-failure counter "
                + "(tags: scopeId). Incremented once per schema dropped "
                + "from a ListSchemas result; non-zero means at least "
                + "one schema row was unreadable in that scope."
            Unit = "1"
            Tags = [ "scopeId" ]
        }
    }
]

type FormStore(entityStore: IEntityStore, ?warn: FormStoreWarn, ?metricsSink: IMetricsSink) =

    let warn = defaultArg warn ignore
    let metricsSink = defaultArg metricsSink (NoOpMetricsSink() :> IMetricsSink)

    interface IFormStore with

        member _.SaveSchema(scopeId, schema) = async {
            // Force the entity-type discriminator regardless of what
            // the caller put in `Type` — defensive against schemas
            // built outside `FormSchema.create`.
            let normalized = {
                schema with
                    Type = FormSchema.entityType
            }

            let! r = entityStore.Save<FormSchema>(scopeId, normalized)

            return
                match r with
                | Ok ref ->
                    // Echo the saved version back so callers can
                    // surface "saved as v3" in the UI.
                    Ok {
                        normalized with
                            Version = ref.Version
                    }
                | Error err -> Error(mapEntityError "schema" schema.Id err)
        }

        member _.GetSchema(scopeId, schemaId, version) = async {
            match version with
            | None ->
                let! r = entityStore.Get<FormSchema>(scopeId, FormSchema.entityType, schemaId)

                return
                    match r with
                    | Ok schema -> Ok schema
                    | Error err -> Error(mapEntityError "schema" schemaId err)
            | Some v ->
                let! r = entityStore.GetVersion<FormSchema>(scopeId, FormSchema.entityType, schemaId, v)

                return
                    match r with
                    | Ok schema -> Ok schema
                    | Error err -> Error(mapEntityError "schema" schemaId err)
        }

        member _.ListSchemas(scopeId) = async {
            let! refs = entityStore.ListAll<FormSchema>(scopeId, FormSchema.entityType, 0, System.Int32.MaxValue)

            let acc = ResizeArray<FormSchema>()
            let failed = ResizeArray<string * EntityError>()

            for r in refs do
                let! schemaResult = entityStore.Get<FormSchema>(scopeId, FormSchema.entityType, r.Id)

                match schemaResult with
                | Ok schema -> acc.Add schema
                | Error err -> failed.Add(r.Id, err)

            // Surface per-entity load failures: counter increment (one
            // per dropped schema, tagged by scope) plus one bounded
            // warn line summarising the batch. Survivors flow through
            // unchanged — the result-shape contract is preserved.
            if failed.Count > 0 then
                let tags = Map.ofList [ "scopeId", scopeId ]

                for _ in 1 .. failed.Count do
                    metricsSink.Increment(ListSchemasLoadFailedTotal, tags)

                let sampledIds =
                    failed
                    |> Seq.truncate ListSchemasFailedIdSampleLimit
                    |> Seq.map fst
                    |> String.concat ", "

                let suffix =
                    if failed.Count > ListSchemasFailedIdSampleLimit then
                        sprintf " (+%d more)" (failed.Count - ListSchemasFailedIdSampleLimit)
                    else
                        ""

                warn (
                    sprintf
                        "ToolUp.Forms.FormStore.ListSchemas dropped %d schema(s) on per-entity load failure in scope '%s'; first failing ids: [%s]%s"
                        failed.Count
                        scopeId
                        sampledIds
                        suffix
                )

            return acc |> Seq.sortBy _.Id |> List.ofSeq
        }

        member _.DeleteSchema(scopeId, schemaId) = async {
            let! r = entityStore.Delete(scopeId, FormSchema.entityType, schemaId)

            return
                match r with
                | Ok() -> Ok()
                | Error err -> Error(mapEntityError "schema" schemaId err)
        }

        member _.SaveSubmission(scopeId, submission) = async {
            let normalized = {
                submission with
                    Type = Submission.entityType
            }

            let! r = entityStore.Save<Submission>(scopeId, normalized)

            return
                match r with
                | Ok ref ->
                    Ok {
                        normalized with
                            Version = ref.Version
                    }
                | Error err -> Error(mapEntityError "submission" submission.Id err)
        }

        member _.GetSubmission(scopeId, submissionId) = async {
            let! r = entityStore.Get<Submission>(scopeId, Submission.entityType, submissionId)

            return
                match r with
                | Ok submission -> Ok submission
                | Error err -> Error(mapEntityError "submission" submissionId err)
        }

        member _.ListSubmissions(scopeId, query) = async {
            // Force the query's entity type to Submission — defensive
            // against callers passing a query built for a different
            // entity (cross-type queries don't make sense here).
            let normalized = {
                query with
                    EntityType = Submission.entityType
            }

            let! r = entityStore.Query<Submission>(scopeId, normalized)

            return
                match r with
                | Ok subs -> Ok subs
                | Error err -> Error(FormError.StorageFailed(EntityError.message err))
        }

        member _.DeleteSubmission(scopeId, submissionId) = async {
            let! r = entityStore.Delete(scopeId, Submission.entityType, submissionId)

            return
                match r with
                | Ok() -> Ok()
                | Error err -> Error(mapEntityError "submission" submissionId err)
        }
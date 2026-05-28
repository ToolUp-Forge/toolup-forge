module ToolUp.Forms.Tests.InProcess.FormsServerHygieneTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Metrics
open ToolUp.Forms.FormSchema
open ToolUp.Forms.IFormStore
open ToolUp.Forms.FormStore
open ToolUp.Forms.FormsCompose
open ToolUp.Forms.Tests.InProcess.InMemoryEntityStore

// ─── Forms server-side defensive hygiene tidy-up ────────────────────
//
// Covers M1 / M3 / M4 from the ToolUp.Forms defensive-hygiene audit:
//
//   M1 — `IAnalyserCache.MarkStale` renamed from `Invalidate` so the
//        return-without-action contract is honest. (Existing
//        AnalyserCacheTests retitled to match; coverage stays there.)
//
//   M3 — `FormStore.ListSchemas` per-entity load-failure surfacing.
//        Survivors flow through unchanged; dropped IDs reach the warn
//        callback (with a bounded first-N sample) and the metric sink
//        increments `toolup.forms.formstore.list_schemas.load_failed`
//        once per dropped row, tagged by scope.
//
//   M4 — `FormsCompose.withFormSchema` field-key uniqueness gate.
//        Schemas registered with two `FieldSchema` entries sharing a
//        `Key` fail loudly at compose time with a diagnostic naming
//        the offending key(s) and field positions.

// ─── M3 — ListSchemas error-surfacing fixtures ──────────────────────

/// Test-only counting metrics sink. Increment / Record / SetGauge each
/// append to an internal list so assertions can inspect per-tag counts
/// without depending on the production Prometheus / Otel sinks.
type private RecordingMetricsSink() =
    let increments = ResizeArray<string * Map<string, string>>()

    interface IMetricsSink with
        member _.Record(_, _, _) = ()
        member _.Increment(name, tags) = increments.Add(name, tags)
        member _.SetGauge(_, _, _) = ()

    member _.Increments = increments :> seq<_>

/// `IEntityStore` decorator that wraps an inner store and refuses to
/// `Get` entities whose id sits in the `failIds` set. `ListAll` still
/// returns the full ref list so `FormStore.ListSchemas` walks the
/// faulting ids and exercises the warn-event branch. Mirrors the
/// "underlying store can list refs but per-row reads fault" shape that
/// surfaces in practice when an index entry survives a partial-write
/// outage but the version blob is unreadable.
type private FaultingGetEntityStore(inner: IEntityStore, failIds: Set<EntityId>) =
    interface IEntityStore with
        member _.Save<'T>(scopeId, entity) = inner.Save<'T>(scopeId, entity)

        member _.Get<'T>(scopeId, entityType, entityId) = async {
            if failIds.Contains entityId then
                return Error(EntityError.StorageFailure(sprintf "simulated read failure for %s" entityId))
            else
                return! inner.Get<'T>(scopeId, entityType, entityId)
        }

        member _.GetVersion<'T>(scopeId, entityType, entityId, version) =
            inner.GetVersion<'T>(scopeId, entityType, entityId, version)

        member _.ListVersions<'T>(scopeId, entityType, entityId) =
            inner.ListVersions<'T>(scopeId, entityType, entityId)

        member _.Delete(scopeId, entityType, entityId) =
            inner.Delete(scopeId, entityType, entityId)

        member _.FindByIndex<'T>(scopeId, entityType, indexName, value) =
            inner.FindByIndex<'T>(scopeId, entityType, indexName, value)

        member _.Count(scopeId, entityType) = inner.Count(scopeId, entityType)

        member _.ListAll<'T>(scopeId, entityType, skip, take) =
            inner.ListAll<'T>(scopeId, entityType, skip, take)

        member _.Query<'T>(scopeId, query) = inner.Query<'T>(scopeId, query)

let private freshScope () =
    "scope-" + Guid.NewGuid().ToString("N").Substring(0, 8)

let private buildSchema (id: string) : FormSchema =
    FormSchema.create id ("Display " + id) [
        {
            Key = "field-a"
            DisplayName = "Field A"
            Description = None
            Kind = TextField(Some 64)
            Required = false
            Validators = []
        }
    ]

// ─── M3 — ListSchemas error surfacing ───────────────────────────────

let private listSchemasErrorSurfacingTests =
    testList "FormStore.ListSchemas — per-entity error surfacing (M3)" [
        testCaseAsync "survivors are returned unchanged when every load succeeds"
        <| async {
            let inner = InMemoryEntityStore() :> IEntityStore
            let warnLog = ResizeArray<string>()
            let metrics = RecordingMetricsSink()
            let store = FormStore(inner, warnLog.Add, metrics) :> IFormStore
            let scopeId = freshScope ()

            let! _ = store.SaveSchema(scopeId, buildSchema "alpha")
            let! _ = store.SaveSchema(scopeId, buildSchema "bravo")

            let! listed = store.ListSchemas scopeId

            Expect.equal listed.Length 2 "both schemas survive a clean ListSchemas"
            Expect.equal warnLog.Count 0 "no warn line emitted when no rows fault"
            Expect.isEmpty metrics.Increments "no metric increments on clean read"
        }

        testCaseAsync
            "per-entity load failure increments scope-tagged counter once per drop and emits a bounded warn line"
        <| async {
            let inner = InMemoryEntityStore() :> IEntityStore
            let scopeId = freshScope ()

            // Pre-populate three schemas via a "clean" store so the
            // entity-store contents are real; the faulting wrapper sits
            // in front of reads only.
            let cleanStore = FormStore(inner) :> IFormStore

            let! _ = cleanStore.SaveSchema(scopeId, buildSchema "alpha")
            let! _ = cleanStore.SaveSchema(scopeId, buildSchema "bravo")
            let! _ = cleanStore.SaveSchema(scopeId, buildSchema "charlie")

            let faulting =
                FaultingGetEntityStore(inner, Set.ofList [ "alpha"; "charlie" ]) :> IEntityStore

            let warnLog = ResizeArray<string>()
            let metrics = RecordingMetricsSink()
            let faultingFormStore = FormStore(faulting, warnLog.Add, metrics) :> IFormStore

            let! listed = faultingFormStore.ListSchemas scopeId

            Expect.equal listed.Length 1 "only the surviving 'bravo' schema is returned"
            Expect.equal listed.Head.Id "bravo" "the right schema survives"

            let scopedIncrements =
                metrics.Increments
                |> Seq.filter (fun (name, tags) ->
                    name = ListSchemasLoadFailedTotal
                    && tags |> Map.tryFind "scopeId" = Some scopeId)
                |> Seq.length

            Expect.equal
                scopedIncrements
                2
                "counter increments once per dropped schema, tagged with the offending scope"

            Expect.equal warnLog.Count 1 "exactly one bounded warn line per ListSchemas call"

            let line = warnLog[0]
            Expect.stringContains line "dropped 2 schema(s)" "warn states the drop count"
            Expect.stringContains line scopeId "warn includes the scope id"
            Expect.stringContains line "alpha" "warn samples the alpha entity id"
            Expect.stringContains line "charlie" "warn samples the charlie entity id"
        }
    ]

// ─── M4 — withFormSchema field-key uniqueness gate ──────────────────

let private withFormSchemaUniquenessTests =
    testList "FormsCompose.withFormSchema — duplicate field Key gate (M4)" [
        testCase "schema with two fields sharing a Key is rejected at compose time" (fun () ->
            let duplicate: FormSchema =
                FormSchema.create "survey-broken" "Broken Survey" [
                    {
                        Key = "email"
                        DisplayName = "Email"
                        Description = None
                        Kind = TextField(Some 254)
                        Required = true
                        Validators = []
                    }
                    {
                        Key = "name"
                        DisplayName = "Name"
                        Description = None
                        Kind = TextField(Some 64)
                        Required = true
                        Validators = []
                    }
                    {
                        Key = "email"
                        DisplayName = "Email (again)"
                        Description = None
                        Kind = TextField(Some 254)
                        Required = false
                        Validators = []
                    }
                ]

            let captured =
                try
                    FormsServerApp.create () |> FormsServerApp.withFormSchema duplicate |> ignore

                    None
                with ex ->
                    Some ex

            match captured with
            | None -> failtest "expected withFormSchema to throw on duplicate Keys"
            | Some ex ->
                let message = ex.Message
                Expect.stringContains message "survey-broken" "diagnostic names the offending schema id"
                Expect.stringContains message "'email'" "diagnostic names the duplicated key"
                Expect.stringContains message "0" "diagnostic cites the first occurrence's field position"
                Expect.stringContains message "2" "diagnostic cites the second occurrence's field position")

        testCase "schema with all-unique Keys registers cleanly" (fun () ->
            let cleanSchema: FormSchema =
                FormSchema.create "survey-ok" "Survey OK" [
                    {
                        Key = "email"
                        DisplayName = "Email"
                        Description = None
                        Kind = TextField(Some 254)
                        Required = true
                        Validators = []
                    }
                    {
                        Key = "name"
                        DisplayName = "Name"
                        Description = None
                        Kind = TextField(Some 64)
                        Required = true
                        Validators = []
                    }
                ]

            let registered =
                FormsServerApp.create () |> FormsServerApp.withFormSchema cleanSchema

            Expect.isTrue
                (Map.containsKey "survey-ok" registered.Schemas)
                "unique-key schema lands in the compose-time map")
    ]

let tests =
    testList "Forms server-side defensive hygiene" [ listSchemasErrorSurfacingTests; withFormSchemaUniquenessTests ]
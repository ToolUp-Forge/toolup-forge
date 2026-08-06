// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelEvaluationTests

open System
open System.IO
open System.Text
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.ModelProviders.Reference

// ─── Phase 456 — model evaluation & champion-challenger harness ─────────
//
// Bound to the reference provider family over the full blob-backed default
// stack (datasets + data objects + registry + scorer). Asserts:
//   * an evaluation round-trips: holdout scored via Phase 454, metrics
//     provider-computed (plan D10), the run stored + queryable + audited
//     (`ModelEvaluated`), and idempotent per (artifact, vintage);
//   * comparison ordering is a pure float sort in the declared direction;
//     ties and missing/NaN metrics are typed outcomes, never silent
//     orderings;
//   * the promotion gate permits a challenger that beats the champion,
//     refuses (typed + audited) one that does not, fails closed on anything
//     it cannot compare, and NEVER bypasses the human Owner/Admin
//     transition (a Member is refused by Phase 453 even with a winning
//     verdict);
//   * a standing registration accumulates an out-of-time track record
//     across two holdout vintages with no bespoke code, and the sweep is
//     idempotent;
//   * the job handlers map malformed payloads / terminal refusals to
//     `PermanentFailure`;
//   * a provider without `IModelEvaluationMetrics` is a typed
//     `EvaluationUnsupported` refusal — forge never computes a fallback
//     metric (plan D10).

let private t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private silentLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// The full default modelling stack over one fresh temp dir: blob-backed
/// datasets + data objects, the blob model registry, the reference fit +
/// score providers, the Phase 454 scorer, and the Phase 456 runner.
type private Stack = {
    Datasets: IDatasetStore
    DataObjects: IDataObjectStore
    Registry: IModelRegistry
    Runner: IModelEvaluationRunner
    Audit: RecordingAuditLog
}

let private freshStack () : Stack =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-eval-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let datasets = BlobDatasetStore.create dataObjects
    let audit = RecordingAuditLog()
    let registry = BlobModelRegistry.create dataObjects audit

    let fitProviders = ModelFitProviderRegistry [ ReferenceModelFitProvider.create () ]

    let scoreProviders =
        ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

    let scorer =
        ModelScorer.create scoreProviders datasets audit ModelScorePolicy.permissive

    {
        Datasets = datasets
        DataObjects = dataObjects
        Registry = registry
        Runner = ModelEvaluationRunner.create fitProviders registry scorer datasets dataObjects audit
        Audit = audit
    }

/// A small panel schema: unit / period / one plain feature / one target.
let private panelSchema: DatasetSchema = {
    Columns = [
        {
            Name = "region"
            DType = DatasetDType.Categorical
            Nullable = false
            Role = DatasetColumnRole.PanelUnit
        }
        {
            Name = "week"
            DType = DatasetDType.Timestamp
            Nullable = false
            Role = DatasetColumnRole.PanelPeriod
        }
        {
            Name = "spend"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
        {
            Name = "sales"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Target
        }
    ]
}

let private row (region: string) (weekOffset: float) (spend: float) (sales: float) : DatasetRow = {
    Cells = [
        DatasetValue.Categorical region
        DatasetValue.Timestamp(t0.AddDays(7.0 * weekOffset))
        DatasetValue.Float spend
        DatasetValue.Float sales
    ]
}

/// Seed one vintage of `datasetId` under `scope` (a new version per call)
/// and return its ref. `salt` varies the content so each vintage is real.
let private seedVintage (store: IDatasetStore) (scope: string) (datasetId: string) (salt: float) = async {
    let rows = [
        row "north" 0.0 (100.0 + salt) (1000.0 + salt)
        row "north" 1.0 (120.0 + salt) (1100.0 + salt)
        row "south" 0.0 (80.0 + salt) (900.0 + salt)
    ]

    let! created = store.Create(scope, datasetId, panelSchema, rows, "u1", Map.empty, Versioned)

    match created with
    | Ok v ->
        return
            ({
                ScopeId = scope
                DatasetId = datasetId
                Version = v.Version
            }
            : DatasetVersionRef)
    | Error e -> return failwithf "seed failed: %s" (DatasetError.describe e)
}

/// Fit the reference provider with `seed` against `vintage` and register the
/// outcome — a governed artifact born `Fitted` whose composite key varies
/// with the seed (so two seeds are two distinct artifacts with distinct
/// predictions and therefore distinct provider metrics).
let private fitAndRegister (stack: Stack) (scope: string) (seed: int64) (vintage: DatasetVersionRef) = async {
    let provider = ReferenceModelFitProvider.create ()

    let request: FitRequest = {
        ScopeId = scope
        DatasetVersion = vintage
        SpecRef = ModelSpecRef.ofPayload "shared-spec"
        ProviderKind = ReferenceModelFitProvider.Kind
        Seed = seed
        Gates = []
        SubmitterClass = SubmitterClass.Human
    }

    let! outcome = provider.Fit request

    match! stack.Registry.Register(scope, outcome, "u1", Map.empty, "") with
    | Ok artifact -> return artifact
    | Error e -> return failwithf "register failed: %s" (ModelRegistryError.describe e)
}

let private evaluatedRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | ModelEvaluated p -> Some p
        | _ -> None)

let private deniedRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | ModelArtifactTransitionDenied p -> Some p
        | _ -> None)

/// Evaluate an artifact and fail the test on any refusal.
let private evaluateOk (stack: Stack) (scope: string) (keyHash: string) (holdout: DatasetVersionRef) = async {
    let request: EvaluationRequest = {
        ScopeId = scope
        ArtifactKeyHash = keyHash
        Holdout = holdout
        EvaluatedBy = "u1"
    }

    match! stack.Runner.Evaluate request with
    | Ok run -> return run
    | Error e -> return failwithf "evaluation failed: %s" (EvaluationError.describe e)
}

// ─── Phase 641 — a provider that RESOLVES its artifact through the context ──
//
// The reference provider deliberately resolves nothing: its predictions are
// a pure function of the artifact's content hash. That proves the envelope,
// but it cannot prove the thing Phase 641 exists for — that a provider
// composed *outside* forge can find the model it is being asked to score.
// This provider is written the way a real one is: it persists its fitted
// parameters and its specification at `Fit` time, and at `Predict` time it
// resolves both back through the `ScoreContext` alone — the caller's scope,
// the artifact reference, the resolved spec, and the training vintage
// recovered from the composite key. Nothing reaches it by a side channel.

[<Literal>]
let private ResolvingKind = "resolving"

[<Literal>]
let private ResolvingVersion = "1.0.0"

/// The object id a fitted-parameter blob lands under, within the fit's scope.
let private paramsObjectId (specHash: string) (seed: int64) =
    sprintf "resolving-params-%s-%d" specHash seed

/// The object id the provider's specification lands under.
let private specObjectId (specHash: string) = "resolving-spec-" + specHash

/// `IModelSpecStore` backed by the same data-object store the provider wrote
/// its specification to — so the spec the context carries is the spec the fit
/// actually read, recovered by content hash rather than remembered by a test.
type private ResolvingSpecStore(dataObjects: IDataObjectStore) =
    interface IModelSpecStore with
        member _.TryGet(scopeId, specHash) = async {
            match! dataObjects.Get(scopeId, specObjectId specHash) with
            | Ok(_, bytes) -> return Some(ModelSpecRef.ofPayload (Encoding.UTF8.GetString bytes))
            | Error _ -> return None
        }

/// A provider family that fits, scores and evaluates one `Kind` — the shape
/// a consumer application ships. It holds only substrate handed to it at
/// construction (the companion convention); everything per-call arrives as
/// values.
type private ResolvingProvider(dataObjects: IDataObjectStore) =

    /// The coefficient a seed fits to — deterministic, and emphatically not
    /// statistics: the point is that the *stored* value is what `Predict`
    /// later recovers, not how it was chosen.
    static member CoefficientOf(seed: int64) : float = 1.5 + float seed

    /// Index of a named column, or `None`.
    static member private ColumnIndex (schema: DatasetSchema) (name: string) =
        schema.Columns |> List.tryFindIndex (fun c -> c.Name = name)

    static member private FloatAt (row: DatasetRow) (index: int) =
        match List.item index row.Cells with
        | DatasetValue.Float f -> Some f
        | _ -> None

    interface IModelFitProvider with
        member _.Kind = ResolvingKind
        member _.ProviderVersion = ResolvingVersion
        member _.DeclareGates() = []

        member _.Fit(request: FitRequest) = async {
            let coefficient = ResolvingProvider.CoefficientOf request.Seed
            let payload = sprintf "coefficient=%.6f" coefficient
            let bytes = Encoding.UTF8.GetBytes payload

            let contentHash = SHA256.HashData bytes |> Convert.ToHexStringLower

            let artifactId = paramsObjectId request.SpecRef.SpecHash request.Seed

            // A real provider persists its fitted parameters and returns a
            // reference to them. Both land in the fit's own scope.
            let! saved =
                dataObjects.Save(
                    request.ScopeId,
                    artifactId,
                    bytes,
                    "test.resolving.params",
                    "fit",
                    Map.empty,
                    Versioned
                )

            match saved with
            | Error e -> return failwithf "the test provider could not persist its parameters: %A" e
            | Ok _ ->
                let! specSaved =
                    dataObjects.Save(
                        request.ScopeId,
                        specObjectId request.SpecRef.SpecHash,
                        Encoding.UTF8.GetBytes request.SpecRef.Payload,
                        "test.resolving.spec",
                        "fit",
                        Map.empty,
                        Versioned
                    )

                match specSaved with
                | Error e -> return failwithf "the test provider could not persist its specification: %A" e
                | Ok _ ->
                    return {
                        CompositeKey =
                            FitCompositeKey.compute
                                request.SpecRef.SpecHash
                                (DatasetVersionRef.key request.DatasetVersion)
                                request.Seed
                                ResolvingKind
                                ResolvingVersion
                        ArtifactRef = {
                            ArtifactId = artifactId
                            ContentHash = contentHash
                            ByteLength = int64 bytes.Length
                        }
                        Diagnostics = Map [ "coefficient", coefficient ]
                        GateVerdicts = []
                        DurationMs = 0L
                        CostUnits = 0.0
                    }
        }

    interface IModelScoreProvider with
        member _.Kind = ResolvingKind
        member _.ProviderVersion = ResolvingVersion
        member _.RequiredInputColumns() = [ "spend" ]

        member _.Predict(context: ScoreContext, schema: DatasetSchema, rows: DatasetRow list) = async {
            // 1. The specification the fit read — resolvable only because the
            //    context carries it (or the scope to find it under).
            match context.Spec with
            | None ->
                return
                    Error(
                        ScoreError.ProviderFailed(ResolvingKind, "no specification was resolvable through the context")
                    )
            | Some spec ->
                // 2. The training vintage, recovered from the composite key —
                //    no hand-rolled parser.
                match ScoreContext.trainingVintage context with
                | None ->
                    return
                        Error(ScoreError.ProviderFailed(ResolvingKind, "the composite key names no training vintage"))
                | Some _training ->
                    // 3. The fitted parameters, read under the CALLER's scope
                    //    with the artifact reference the context carries. This
                    //    is the read that was impossible before Phase 641.
                    match! dataObjects.Get(context.ScopeId, context.Artifact.ArtifactId) with
                    | Error _ ->
                        return
                            Error(
                                ScoreError.ProviderFailed(
                                    ResolvingKind,
                                    sprintf
                                        "fitted parameters '%s' are not resolvable in scope '%s'"
                                        context.Artifact.ArtifactId
                                        context.ScopeId
                                )
                            )
                    | Ok(_, bytes) ->
                        let stored = Encoding.UTF8.GetString bytes

                        let coefficient =
                            Double.Parse(
                                stored.Substring("coefficient=".Length),
                                Globalization.CultureInfo.InvariantCulture
                            )

                        // The spec is genuinely the one that was fitted.
                        if spec.SpecHash <> context.CompositeKey.SpecHash then
                            return
                                Error(
                                    ScoreError.ProviderFailed(
                                        ResolvingKind,
                                        "the resolved specification does not match the artifact's identity"
                                    )
                                )
                        else
                            match ResolvingProvider.ColumnIndex schema "spend" with
                            | None ->
                                return Error(ScoreError.InputSchemaMismatch "the input frame carries no 'spend' column")
                            | Some spendIndex ->
                                let keyColumns =
                                    schema.Columns
                                    |> List.filter (fun c ->
                                        c.Role = DatasetColumnRole.PanelUnit || c.Role = DatasetColumnRole.PanelPeriod)

                                let keyIndexes =
                                    keyColumns
                                    |> List.map (fun c -> schema.Columns |> List.findIndex (fun x -> x.Name = c.Name))

                                let outputSchema: DatasetSchema = {
                                    Columns =
                                        keyColumns
                                        @ [
                                            {
                                                Name = "prediction"
                                                DType = DatasetDType.Float
                                                Nullable = false
                                                Role = DatasetColumnRole.Target
                                            }
                                        ]
                                }

                                let outputRows =
                                    rows
                                    |> List.map (fun r ->
                                        let keyCells = keyIndexes |> List.map (fun i -> List.item i r.Cells)

                                        let predicted =
                                            match ResolvingProvider.FloatAt r spendIndex with
                                            | Some spend -> coefficient * spend
                                            | None -> 0.0

                                        {
                                            Cells = keyCells @ [ DatasetValue.Float predicted ]
                                        })

                                return
                                    Ok {
                                        Schema = outputSchema
                                        Rows = outputRows
                                    }
        }

    interface IModelEvaluationMetrics with
        member _.EvaluateMetrics(predictionsSchema, predictions, actualsSchema, actuals) = async {
            match
                ResolvingProvider.ColumnIndex predictionsSchema "prediction",
                ResolvingProvider.ColumnIndex actualsSchema "sales"
            with
            | Some predictionIndex, Some salesIndex ->
                let paired =
                    List.zip predictions actuals
                    |> List.choose (fun (p, a) ->
                        match ResolvingProvider.FloatAt p predictionIndex, ResolvingProvider.FloatAt a salesIndex with
                        | Some pv, Some av -> Some(abs (pv - av))
                        | _ -> None)

                return
                    Ok(
                        Map [
                            "n_scored", float (List.length predictions)
                            "n_actuals", float (List.length actuals)
                            "mean_abs_error", (if List.isEmpty paired then 0.0 else List.average paired)
                        ]
                    )
            | _ -> return Error "the frames lack a 'prediction' / 'sales' column pair"
        }

/// The full default modelling stack composed around the resolving provider,
/// with an `IModelSpecStore` so `ScoreContext.Spec` is populated.
type private ResolvingStack = {
    Datasets: IDatasetStore
    DataObjects: IDataObjectStore
    Registry: IModelRegistry
    Runner: IModelEvaluationRunner
    Provider: ResolvingProvider
}

let private freshResolvingStack () : ResolvingStack =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-eval-resolve-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let datasets = BlobDatasetStore.create dataObjects
    let audit = RecordingAuditLog()
    let registry = BlobModelRegistry.create dataObjects audit
    let provider = ResolvingProvider(dataObjects)

    let scorer =
        ModelScorer.createWithSpecStore
            (ModelScoreProviderRegistry [ provider ])
            datasets
            audit
            ModelScorePolicy.permissive
            (ResolvingSpecStore dataObjects)

    {
        Datasets = datasets
        DataObjects = dataObjects
        Registry = registry
        Runner =
            ModelEvaluationRunner.create
                (ModelFitProviderRegistry [ provider ])
                registry
                scorer
                datasets
                dataObjects
                audit
        Provider = provider
    }

/// A job context carrying `payload` (the scorer-contract idiom).
let private jobCtx (payload: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = "team-1"
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
    Attempt = 1
    Trigger = Manual
    TriggerSource = ScheduledManually "system"
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = payload
    DeadLetterDestination = None
}

let tests =
    testList "ModelEvaluation — evaluation & champion-challenger harness" [

        // ── 456.A — evaluation round-trip ─────────────────────────────

        testCaseAsync "an evaluation round-trips: provider metrics stored against the artifact, queryable, audited"
        <| async {
            let stack = freshStack ()
            let! holdout = seedVintage stack.Datasets "team-1" "holdout" 0.0
            let! artifact = fitAndRegister stack "team-1" 1L holdout

            let! run = evaluateOk stack "team-1" artifact.CompositeKey.Hash holdout

            // The run identity is deterministic per (artifact, vintage).
            Expect.equal run.RunId (EvaluationRun.id artifact.CompositeKey.Hash holdout) "deterministic run id"
            Expect.equal run.ArtifactKeyHash artifact.CompositeKey.Hash "the run names the artifact"
            Expect.equal run.ProviderId ReferenceModelFitProvider.Kind "the run names the provider"

            // Provider-computed metrics stored verbatim (plan D10): the
            // reference evaluator always reports its declared metric set.
            for metric in ReferenceModelFitProvider.DeclaredMetrics do
                Expect.isTrue (Map.containsKey metric run.Metrics) (sprintf "metric '%s' is stored" metric)

            Expect.equal (Map.find "n_scored" run.Metrics) 3.0 "one prediction per holdout row"
            Expect.equal (Map.find "n_actuals" run.Metrics) 3.0 "actuals cardinality reported"

            // The predictions vintage is an ordinary, readable dataset.
            let! predictions =
                stack.Datasets.GetVersion(run.Predictions.ScopeId, run.Predictions.DatasetId, run.Predictions.Version)

            Expect.isTrue (Result.isOk predictions) "the predictions vintage is readable"

            // The track record surfaces the stored run.
            let! trackRecord = stack.Runner.GetTrackRecord("team-1", artifact.CompositeKey.Hash)
            Expect.equal (List.length trackRecord) 1 "one evaluation in the track record"
            Expect.equal (List.head trackRecord).Metrics run.Metrics "the stored metrics round-trip"

            // Exactly one ModelEvaluated audit row, carrying identity +
            // cardinality (GP 6).
            let evaluated = evaluatedRows stack.Audit
            Expect.equal evaluated.Length 1 "one ModelEvaluated audit row"
            Expect.equal evaluated.[0].CompositeKeyHash artifact.CompositeKey.Hash "audit names the artifact"
            Expect.equal evaluated.[0].HoldoutVersion (DatasetVersionRef.key holdout) "audit names the holdout"
            Expect.equal evaluated.[0].MetricCount (Map.count run.Metrics) "audit carries cardinality only"
        }

        // ── Phase 641 — the runner path, end to end, for a resolving provider ──

        testCaseAsync
            "Phase 641 — a provider that resolves its artifact through the score context runs the whole evaluation path, predictions landing as a vintage"
        <| async {
            let stack = freshResolvingStack ()
            let scope = "team-1"
            let seed = 3L

            let! training = seedVintage stack.Datasets scope "training" 0.0
            let! holdout = seedVintage stack.Datasets scope "holdout" 5.0

            let specPayload = "an opaque provider specification the fit read"

            let fitRequest: FitRequest = {
                ScopeId = scope
                DatasetVersion = training
                SpecRef = ModelSpecRef.ofPayload specPayload
                ProviderKind = ResolvingKind
                Seed = seed
                Gates = []
                SubmitterClass = SubmitterClass.Human
            }

            let! outcome = (stack.Provider :> IModelFitProvider).Fit fitRequest

            let! registered = stack.Registry.Register(scope, outcome, "u1", Map.empty, "")

            let artifact =
                match registered with
                | Ok a -> a
                | Error e -> failwithf "register failed: %s" (ModelRegistryError.describe e)

            // The whole point: the runner's own path — score the holdout
            // through the Phase 454 seam into a predictions VINTAGE, then
            // hand predictions + actuals to the provider for metrics.
            let request: EvaluationRequest = {
                ScopeId = scope
                ArtifactKeyHash = artifact.CompositeKey.Hash
                Holdout = holdout
                EvaluatedBy = "u1"
            }

            match! stack.Runner.Evaluate request with
            | Error e ->
                failtestf "the runner must now complete for a resolving provider; got %s" (EvaluationError.describe e)
            | Ok run ->
                Expect.equal run.ProviderId ResolvingKind "the run names the resolving provider"

                // 454's own law: predictions land as a dataset version. The
                // consumer that bypassed the runner skipped exactly this.
                let! predictionsVersion =
                    stack.Datasets.GetVersion(
                        run.Predictions.ScopeId,
                        run.Predictions.DatasetId,
                        run.Predictions.Version
                    )

                match predictionsVersion with
                | Error e -> failtestf "the predictions vintage must be readable: %s" (DatasetError.describe e)
                | Ok version ->
                    Expect.equal version.ScopeId scope "the predictions vintage lands in the caller's scope"
                    Expect.equal version.RowCount 3L "one prediction row per holdout row"

                    Expect.equal
                        (version.Schema.Columns |> List.map _.Name)
                        [ "region"; "week"; "prediction" ]
                        "panel keys carried forward + the prediction column"

                    // Provenance names the artifact + the holdout it read.
                    Expect.equal
                        (Map.tryFind ScoreProvenance.ScoredByKey version.Metadata)
                        (Some artifact.CompositeKey.Hash)
                        "the predictions vintage names the artifact that produced it"

                    Expect.equal
                        (Map.tryFind ScoreProvenance.InputVersionKey version.Metadata)
                        (Some(DatasetVersionRef.key holdout))
                        "the predictions vintage names the holdout it scored"

                // The predictions are the RESOLVED coefficient applied to the
                // holdout's spend — proof the provider really read its stored
                // parameters through the context, rather than producing a
                // number from the reference alone.
                let! page =
                    stack.Datasets.ReadPage(
                        run.Predictions.ScopeId,
                        run.Predictions.DatasetId,
                        run.Predictions.Version,
                        DatasetPageQuery.firstPage 100
                    )

                let coefficient = ResolvingProvider.CoefficientOf seed

                match page with
                | Error e -> failtestf "predictions unreadable: %s" (DatasetError.describe e)
                | Ok p ->
                    let predicted =
                        p.Rows
                        |> List.map (fun r ->
                            match List.last r.Cells with
                            | DatasetValue.Float f -> f
                            | other -> failwithf "expected a float prediction; got %A" other)

                    // seedVintage salt 5.0 ⇒ spend of 105 / 125 / 85.
                    Expect.equal
                        predicted
                        [ coefficient * 105.0; coefficient * 125.0; coefficient * 85.0 ]
                        "predictions are the stored coefficient applied to the holdout's spend"

                // Provider-computed metrics, stored verbatim (plan D10).
                Expect.equal (Map.find "n_scored" run.Metrics) 3.0 "the provider reports its own cardinality"
                Expect.isTrue (Map.containsKey "mean_abs_error" run.Metrics) "the provider's own metric is stored"

                // …and the run is queryable back out as an ordinary track record.
                let! trackRecord = stack.Runner.GetTrackRecord(scope, artifact.CompositeKey.Hash)
                Expect.equal (List.length trackRecord) 1 "the run is in the artifact's track record"
        }

        testCaseAsync
            "Phase 641 — without a resolvable artifact the same provider refuses typed, and the runner surfaces it (no silent fallback)"
        <| async {
            let stack = freshResolvingStack ()
            let scope = "team-1"
            let! holdout = seedVintage stack.Datasets scope "holdout" 5.0

            // An artifact registered from an outcome the provider never fit —
            // so nothing was persisted for it to resolve.
            let unresolvable: FitOutcome = {
                CompositeKey =
                    FitCompositeKey.compute
                        (ModelSpecRef.ofPayload "never fitted").SpecHash
                        (DatasetVersionRef.key holdout)
                        99L
                        ResolvingKind
                        ResolvingVersion
                ArtifactRef = {
                    ArtifactId = "resolving-params-missing"
                    ContentHash = "none"
                    ByteLength = 0L
                }
                Diagnostics = Map.empty
                GateVerdicts = []
                DurationMs = 0L
                CostUnits = 0.0
            }

            let! registered = stack.Registry.Register(scope, unresolvable, "u1", Map.empty, "")

            let artifact =
                match registered with
                | Ok a -> a
                | Error e -> failwithf "register failed: %s" (ModelRegistryError.describe e)

            let request: EvaluationRequest = {
                ScopeId = scope
                ArtifactKeyHash = artifact.CompositeKey.Hash
                Holdout = holdout
                EvaluatedBy = "u1"
            }

            match! stack.Runner.Evaluate request with
            | Error(EvaluationError.ScoreFailed(ScoreError.ProviderFailed(kind, _))) ->
                Expect.equal kind ResolvingKind "the refusing provider is named"
            | other -> failtestf "expected a typed provider refusal; got %A" other

            // No predictions vintage was written for a refused score.
            let! any = stack.Runner.GetTrackRecord(scope, artifact.CompositeKey.Hash)
            Expect.isEmpty any "a refused evaluation stores no run"
        }

        testCaseAsync "re-evaluating the same (artifact, vintage) is idempotent — the track record does not grow"
        <| async {
            let stack = freshStack ()
            let! holdout = seedVintage stack.Datasets "team-1" "holdout" 0.0
            let! artifact = fitAndRegister stack "team-1" 1L holdout

            let! first = evaluateOk stack "team-1" artifact.CompositeKey.Hash holdout
            let! second = evaluateOk stack "team-1" artifact.CompositeKey.Hash holdout

            Expect.equal second.RunId first.RunId "same pair, same run id"
            Expect.equal second.Metrics first.Metrics "a deterministic provider reproduces its metrics"

            let! trackRecord = stack.Runner.GetTrackRecord("team-1", artifact.CompositeKey.Hash)
            Expect.equal (List.length trackRecord) 1 "re-evaluation does not duplicate the track record"
        }

        testCaseAsync "a provider without IModelEvaluationMetrics is a typed EvaluationUnsupported refusal (plan D10)"
        <| async {
            // The extension member dispatches to the provider's own metric
            // computation; a provider that has none is refused — forge never
            // computes a fallback metric.
            let bare =
                { new IModelFitProvider with
                    member _.Kind = "bare"
                    member _.ProviderVersion = "1.0.0"
                    member _.DeclareGates() = []
                    member _.Fit(_) = failwith "not exercised"
                }

            let! result = bare.Evaluate(panelSchema, [], panelSchema, [])

            match result with
            | Error(EvaluationError.EvaluationUnsupported k) -> Expect.equal k "bare" "the provider is named"
            | other -> failtestf "expected EvaluationUnsupported; got %A" other
        }

        // ── 456.B — comparison ordering, ties, missing ────────────────

        test "comparison ordering is a pure float sort in the declared direction" {
            let entrants = [ "a", Some 0.1; "b", Some 0.9; "c", Some 0.5 ]

            let standings, missing, result =
                ModelComparison.order MetricDirection.HigherIsBetter entrants

            Expect.equal
                (standings |> List.map _.ArtifactKeyHash)
                [ "b"; "c"; "a" ]
                "higher-is-better orders descending"

            Expect.isEmpty missing "no missing metrics"
            Expect.equal result (ComparisonResult.DecisiveWinner "b") "the best entrant wins"

            let standings, _, result =
                ModelComparison.order MetricDirection.LowerIsBetter entrants

            Expect.equal (standings |> List.map _.ArtifactKeyHash) [ "a"; "c"; "b" ] "lower-is-better orders ascending"
            Expect.equal result (ComparisonResult.DecisiveWinner "a") "the direction flips the winner"
        }

        test "exact ties are a typed TiedAtBest outcome, never silently broken" {
            let entrants = [ "a", Some 0.5; "b", Some 0.5; "c", Some 0.1 ]

            let standings, _, result =
                ModelComparison.order MetricDirection.HigherIsBetter entrants

            Expect.equal result (ComparisonResult.TiedAtBest [ "a"; "b" ]) "the tie is typed, declared order kept"

            Expect.equal
                (standings |> List.map _.ArtifactKeyHash)
                [ "a"; "b"; "c" ]
                "the stable sort keeps declared order within the tie"
        }

        test "missing and NaN metrics are typed outcomes, never silent orderings" {
            let entrants = [ "a", None; "b", Some 0.2; "c", Some nan ]

            let standings, missing, result =
                ModelComparison.order MetricDirection.HigherIsBetter entrants

            Expect.equal missing [ "a"; "c" ] "absent + NaN metrics land in the missing list, declared order"
            Expect.equal (standings |> List.map _.ArtifactKeyHash) [ "b" ] "only rankable entrants are ranked"
            Expect.equal result (ComparisonResult.DecisiveWinner "b") "the sole rankable entrant wins"

            let _, missing, result =
                ModelComparison.order MetricDirection.HigherIsBetter [ "a", None; "b", None ]

            Expect.equal missing [ "a"; "b" ] "every entrant can be missing"
            Expect.equal result ComparisonResult.NoComparableMetrics "nothing to compare is a typed outcome"
        }

        testCaseAsync "an integrated comparison yields a stored, queryable verdict over provider metrics"
        <| async {
            let stack = freshStack ()
            let! holdout = seedVintage stack.Datasets "team-1" "holdout" 0.0
            let! a = fitAndRegister stack "team-1" 1L holdout
            let! b = fitAndRegister stack "team-1" 2L holdout

            let! runA = evaluateOk stack "team-1" a.CompositeKey.Hash holdout
            let! runB = evaluateOk stack "team-1" b.CompositeKey.Hash holdout

            // A third artifact is registered but never evaluated — it must
            // surface as a typed missing-metric outcome.
            let! c = fitAndRegister stack "team-1" 3L holdout

            let request: ComparisonRequest = {
                ScopeId = "team-1"
                Entrants = [ a.CompositeKey.Hash; b.CompositeKey.Hash; c.CompositeKey.Hash ]
                Holdout = holdout
                PrimaryMetric = "mean_prediction"
                Direction = MetricDirection.HigherIsBetter
                ComparedBy = "u1"
            }

            match! stack.Runner.Compare request with
            | Error e -> failtestf "comparison failed: %s" (EvaluationError.describe e)
            | Ok comparison ->
                // The expected winner is derivable from the stored provider
                // metrics — forge only ordered the floats (plan D10).
                let metricA = Map.find "mean_prediction" runA.Metrics
                let metricB = Map.find "mean_prediction" runB.Metrics

                let expectedWinner =
                    if metricA > metricB then
                        a.CompositeKey.Hash
                    else
                        b.CompositeKey.Hash

                Expect.equal comparison.Result (ComparisonResult.DecisiveWinner expectedWinner) "the better metric wins"
                Expect.equal comparison.MissingMetric [ c.CompositeKey.Hash ] "the unevaluated entrant is typed missing"
                Expect.equal (List.length comparison.Standings) 2 "two rankable entrants"

                // The verdict is stored and queryable by its id.
                match! stack.Runner.GetComparison("team-1", comparison.ComparisonId) with
                | Error e -> failtestf "stored comparison not readable: %s" (EvaluationError.describe e)
                | Ok stored ->
                    Expect.equal stored.Result comparison.Result "the stored verdict round-trips"
                    Expect.equal stored.Standings comparison.Standings "the stored standings round-trip"
        }

        // ── 456.C — promotion gate ────────────────────────────────────

        test "the pure gate check fails closed on everything it cannot compare" {
            let holdout: DatasetVersionRef = {
                ScopeId = "team-1"
                DatasetId = "holdout"
                Version = 1
            }

            let comparison: ModelComparison = {
                ComparisonId = "cmp"
                ScopeId = "team-1"
                Entrants = [ "champ"; "chall"; "unranked" ]
                Holdout = holdout
                PrimaryMetric = "m"
                Direction = MetricDirection.HigherIsBetter
                Standings = [
                    {
                        ArtifactKeyHash = "chall"
                        Metric = 0.6
                    }
                    {
                        ArtifactKeyHash = "champ"
                        Metric = 0.5
                    }
                ]
                MissingMetric = [ "unranked" ]
                Result = ComparisonResult.DecisiveWinner "chall"
                ComparedBy = "u1"
                ComparedAt = t0
            }

            let strict = ChampionChallengerPolicy.strictImprovement

            Expect.equal
                (PromotionGate.check strict comparison (Some "champ") "chall")
                (PromotionVerdict.BeatsChampion("champ", 0.6, 0.5))
                "a strict improvement beats the champion"

            Expect.equal
                (PromotionGate.check strict comparison (Some "chall") "champ")
                (PromotionVerdict.DoesNotBeatChampion("chall", 0.5, 0.6))
                "the loser does not beat the winner"

            Expect.equal
                (PromotionGate.check (ChampionChallengerPolicy.withMargin 0.2) comparison (Some "champ") "chall")
                (PromotionVerdict.DoesNotBeatChampion("champ", 0.6, 0.5))
                "an optional margin narrows further"

            Expect.equal
                (PromotionGate.check strict comparison None "chall")
                PromotionVerdict.NoChampion
                "no approved entrant means the policy narrows nothing"

            Expect.equal
                (PromotionGate.check strict comparison (Some "champ") "outsider")
                PromotionVerdict.ChallengerNotCompared
                "an uncompared challenger fails closed"

            Expect.equal
                (PromotionGate.check strict comparison (Some "champ") "unranked")
                PromotionVerdict.ChallengerMetricMissing
                "a metricless challenger fails closed"

            Expect.equal
                (PromotionGate.check strict comparison (Some "unranked") "chall")
                (PromotionVerdict.ChampionMetricMissing "unranked")
                "a metricless champion fails closed"
        }

        testCaseAsync "the promotion gate permits a beating challenger and refuses (typed + audited) a losing one"
        <| async {
            let stack = freshStack ()
            let! holdout = seedVintage stack.Datasets "team-1" "holdout" 0.0
            let! a = fitAndRegister stack "team-1" 1L holdout
            let! b = fitAndRegister stack "team-1" 2L holdout
            let! _ = evaluateOk stack "team-1" a.CompositeKey.Hash holdout
            let! _ = evaluateOk stack "team-1" b.CompositeKey.Hash holdout

            let request: ComparisonRequest = {
                ScopeId = "team-1"
                Entrants = [ a.CompositeKey.Hash; b.CompositeKey.Hash ]
                Holdout = holdout
                PrimaryMetric = "mean_prediction"
                Direction = MetricDirection.HigherIsBetter
                ComparedBy = "u1"
            }

            let! compared = stack.Runner.Compare request

            let comparison =
                match compared with
                | Ok c -> c
                | Error e -> failtestf "comparison failed: %s" (EvaluationError.describe e)

            let winner, loser =
                match comparison.Result with
                | ComparisonResult.DecisiveWinner w -> w, (comparison.Entrants |> List.find (fun e -> e <> w))
                | other -> failtestf "expected a decisive winner between two seeds; got %A" other

            let gate =
                ChampionChallengerGate.create
                    stack.Registry
                    stack.Runner
                    stack.Audit
                    ChampionChallengerPolicy.strictImprovement

            // Approve the LOSER as the incumbent champion (Owner — the human
            // gate), then promote the winner through the gate: permitted.
            match! stack.Registry.TransitionStatus("team-1", loser, ModelArtifactStatus.Approved, Owner, "alice") with
            | Error e -> failtestf "champion approval failed: %s" (ModelRegistryError.describe e)
            | Ok _ -> ()

            match! gate.PromoteChallenger("team-1", comparison.ComparisonId, winner, Owner, "alice") with
            | Error e -> failtestf "a beating challenger must be promotable: %s" (PromotionError.describe e)
            | Ok promoted -> Expect.equal promoted.Status ModelArtifactStatus.Approved "the challenger is Approved"
        }

        testCaseAsync "the promotion gate blocks a challenger that does not beat the champion"
        <| async {
            let stack = freshStack ()
            let! holdout = seedVintage stack.Datasets "team-1" "holdout" 0.0
            let! a = fitAndRegister stack "team-1" 1L holdout
            let! b = fitAndRegister stack "team-1" 2L holdout
            let! _ = evaluateOk stack "team-1" a.CompositeKey.Hash holdout
            let! _ = evaluateOk stack "team-1" b.CompositeKey.Hash holdout

            let request: ComparisonRequest = {
                ScopeId = "team-1"
                Entrants = [ a.CompositeKey.Hash; b.CompositeKey.Hash ]
                Holdout = holdout
                PrimaryMetric = "mean_prediction"
                Direction = MetricDirection.HigherIsBetter
                ComparedBy = "u1"
            }

            let! compared = stack.Runner.Compare request

            let comparison =
                match compared with
                | Ok c -> c
                | Error e -> failtestf "comparison failed: %s" (EvaluationError.describe e)

            let winner, loser =
                match comparison.Result with
                | ComparisonResult.DecisiveWinner w -> w, (comparison.Entrants |> List.find (fun e -> e <> w))
                | other -> failtestf "expected a decisive winner between two seeds; got %A" other

            let gate =
                ChampionChallengerGate.create
                    stack.Registry
                    stack.Runner
                    stack.Audit
                    ChampionChallengerPolicy.strictImprovement

            // Approve the WINNER as champion; the loser cannot pass the gate.
            match! stack.Registry.TransitionStatus("team-1", winner, ModelArtifactStatus.Approved, Owner, "alice") with
            | Error e -> failtestf "champion approval failed: %s" (ModelRegistryError.describe e)
            | Ok _ -> ()

            let deniedBefore = (deniedRows stack.Audit).Length

            match! gate.PromoteChallenger("team-1", comparison.ComparisonId, loser, Owner, "alice") with
            | Error(PromotionError.GateRefused(PromotionVerdict.DoesNotBeatChampion(champion, _, _))) ->
                Expect.equal champion winner "the refusal names the champion"
            | other -> failtestf "expected a GateRefused DoesNotBeatChampion; got %A" other

            // The refusal is audited as a denied transition (GP 6)…
            let denied = deniedRows stack.Audit
            Expect.equal denied.Length (deniedBefore + 1) "the gate refusal is audited"

            Expect.stringContains
                denied.[denied.Length - 1].Reason
                "champion-challenger gate"
                "the reason names the gate"

            // …and the loser's status is untouched.
            match! stack.Registry.Get("team-1", loser) with
            | Ok artifact -> Expect.equal artifact.Status ModelArtifactStatus.Fitted "the challenger stays Fitted"
            | Error e -> failtestf "challenger not readable: %s" (ModelRegistryError.describe e)
        }

        testCaseAsync "the gate never bypasses the human role gate, and narrows nothing when no champion exists"
        <| async {
            let stack = freshStack ()
            let! holdout = seedVintage stack.Datasets "team-1" "holdout" 0.0
            let! a = fitAndRegister stack "team-1" 1L holdout
            let! b = fitAndRegister stack "team-1" 2L holdout
            let! _ = evaluateOk stack "team-1" a.CompositeKey.Hash holdout
            let! _ = evaluateOk stack "team-1" b.CompositeKey.Hash holdout

            let request: ComparisonRequest = {
                ScopeId = "team-1"
                Entrants = [ a.CompositeKey.Hash; b.CompositeKey.Hash ]
                Holdout = holdout
                PrimaryMetric = "mean_prediction"
                Direction = MetricDirection.HigherIsBetter
                ComparedBy = "u1"
            }

            let! compared = stack.Runner.Compare request

            let comparison =
                match compared with
                | Ok c -> c
                | Error e -> failtestf "comparison failed: %s" (EvaluationError.describe e)

            let winner =
                match comparison.Result with
                | ComparisonResult.DecisiveWinner w -> w
                | other -> failtestf "expected a decisive winner; got %A" other

            let gate =
                ChampionChallengerGate.create
                    stack.Registry
                    stack.Runner
                    stack.Audit
                    ChampionChallengerPolicy.strictImprovement

            // No entrant is Approved → NoChampion → the gate permits — but a
            // Member still cannot Approve: the Phase 453 human gate holds.
            // The policy narrows; it never widens and never auto-promotes.
            match! gate.PromoteChallenger("team-1", comparison.ComparisonId, winner, Member, "mallory") with
            | Error(PromotionError.TransitionFailed(ModelRegistryError.Forbidden _)) -> ()
            | other -> failtestf "a Member must be refused by the role gate; got %A" other

            match! stack.Registry.Get("team-1", winner) with
            | Ok artifact -> Expect.equal artifact.Status ModelArtifactStatus.Fitted "nothing was promoted"
            | Error e -> failtestf "artifact not readable: %s" (ModelRegistryError.describe e)

            // The same promotion by an Owner proceeds (no champion to beat).
            match! gate.PromoteChallenger("team-1", comparison.ComparisonId, winner, Owner, "alice") with
            | Ok promoted -> Expect.equal promoted.Status ModelArtifactStatus.Approved "an Owner promotion proceeds"
            | Error e -> failtestf "a no-champion Owner promotion must proceed: %s" (PromotionError.describe e)
        }

        // ── 456.D — standing re-evaluation across vintages ────────────

        testCaseAsync "a standing registration accumulates an out-of-time track record across two vintages"
        <| async {
            let stack = freshStack ()

            // Fit against a training vintage; the holdout dataset does not
            // exist yet — the registration waits.
            let! training = seedVintage stack.Datasets "team-1" "training" 0.0
            let! artifact = fitAndRegister stack "team-1" 1L training

            match! stack.Runner.RegisterReevaluation("team-1", artifact.CompositeKey.Hash, "holdout", "u1") with
            | Error e -> failtestf "registration failed: %s" (EvaluationError.describe e)
            | Ok registration ->
                Expect.equal registration.HoldoutDatasetId "holdout" "the registration names the dataset"

            // Registration is idempotent.
            match! stack.Runner.RegisterReevaluation("team-1", artifact.CompositeKey.Hash, "holdout", "u2") with
            | Ok again -> Expect.equal again.RegisteredBy "u1" "re-registering returns the existing registration"
            | Error e -> failtestf "idempotent re-registration failed: %s" (EvaluationError.describe e)

            let sweep =
                VintageReevaluationJobHandler.create stack.Runner stack.Datasets silentLogger

            let payload =
                ReevaluationSweepEnvelope.serialiseRequest {
                    ScopeId = "team-1"
                    EvaluatedBy = "system"
                }

            // Sweep before any vintage exists: nothing to do, not a failure.
            let! result = sweep.Execute(jobCtx payload)
            Expect.equal result Success "a waiting registration is not a failure"

            let! trackRecord = stack.Runner.GetTrackRecord("team-1", artifact.CompositeKey.Hash)
            Expect.isEmpty trackRecord "no vintage, no evaluation"

            // First holdout vintage lands → the sweep evaluates it.
            let! _ = seedVintage stack.Datasets "team-1" "holdout" 0.0
            let! result = sweep.Execute(jobCtx payload)
            Expect.equal result Success "the first sweep succeeds"

            let! trackRecord = stack.Runner.GetTrackRecord("team-1", artifact.CompositeKey.Hash)
            Expect.equal (trackRecord |> List.map _.Holdout.Version) [ 1 ] "one evaluation, vintage v1"

            // Second vintage lands → the track record accumulates.
            let! _ = seedVintage stack.Datasets "team-1" "holdout" 50.0
            let! result = sweep.Execute(jobCtx payload)
            Expect.equal result Success "the second sweep succeeds"

            let! trackRecord = stack.Runner.GetTrackRecord("team-1", artifact.CompositeKey.Hash)

            Expect.equal
                (trackRecord |> List.map _.Holdout.Version |> List.sort)
                [ 1; 2 ]
                "the out-of-time track record spans both vintages"

            // A third sweep with no new vintage is a no-op (idempotent).
            let evaluatedBefore = (evaluatedRows stack.Audit).Length
            let! result = sweep.Execute(jobCtx payload)
            Expect.equal result Success "an up-to-date sweep succeeds"

            let! trackRecord = stack.Runner.GetTrackRecord("team-1", artifact.CompositeKey.Hash)
            Expect.equal (List.length trackRecord) 2 "no new vintage, no new evaluation"
            Expect.equal (evaluatedRows stack.Audit).Length evaluatedBefore "no new ModelEvaluated audit row"
        }

        // ── job-handler failure mapping ───────────────────────────────

        testCaseAsync "the evaluation job handler maps payloads and refusals to the right JobResult"
        <| async {
            let stack = freshStack ()
            let! holdout = seedVintage stack.Datasets "team-1" "holdout" 0.0
            let! artifact = fitAndRegister stack "team-1" 1L holdout

            let handler = ModelEvaluationJobHandler.create stack.Runner silentLogger

            let goodPayload =
                ModelEvaluationEnvelope.serialiseRequest {
                    ScopeId = "team-1"
                    ArtifactKeyHash = artifact.CompositeKey.Hash
                    Holdout = holdout
                    EvaluatedBy = "system"
                }

            let! ok = handler.Execute(jobCtx goodPayload)
            Expect.equal ok Success "a well-formed EvaluationRequest runs to Success"

            let! malformed = handler.Execute(jobCtx "{ not json")

            match malformed with
            | PermanentFailure _ -> ()
            | other -> failtestf "a malformed payload must be a PermanentFailure; got %A" other

            let unknownArtifact =
                ModelEvaluationEnvelope.serialiseRequest {
                    ScopeId = "team-1"
                    ArtifactKeyHash = "no-such-artifact"
                    Holdout = holdout
                    EvaluatedBy = "system"
                }

            let! missing = handler.Execute(jobCtx unknownArtifact)

            match missing with
            | PermanentFailure _ -> ()
            | other -> failtestf "an unknown artifact is terminal; expected PermanentFailure, got %A" other
        }
    ]
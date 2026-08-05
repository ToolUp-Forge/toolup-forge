// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelEvaluationTests

open System
open System.IO
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
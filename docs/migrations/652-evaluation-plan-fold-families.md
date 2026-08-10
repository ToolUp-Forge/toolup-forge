# Evaluation plans — masked and rolling fold families

**Ships in:** ToolUp.Platform.Server (`ToolUp.Platform.EvaluationPlan`,
`ToolUp.Platform.EvaluationFold`, `ToolUp.Platform.FoldWindow`,
`ToolUp.Platform.EvaluationFoldOutcome`, `ToolUp.Platform.FoldAggregation`,
`ToolUp.Platform.IModelEvaluationPlanMetrics`,
`ToolUp.Platform.EvaluationRequest`, `ToolUp.Platform.ComparisonRequest`,
`ToolUp.Platform.EvaluationRun`, `ToolUp.Platform.ModelComparison`,
`ToolUp.Platform.IModelEvaluationRunner`).

**Who is affected:** anyone who **constructs** an `EvaluationRequest` or a
`ComparisonRequest`, **implements** `IModelEvaluationRunner`, or pattern-matches
exhaustively on `EvaluationError`. Providers implementing
`IModelEvaluationMetrics` are **unaffected** — the Phase 456 seam is unchanged
and still serves the tail-holdout plan.

**No data migration.** Every stored `EvaluationRun` and `ModelComparison`
written before this change reads back unchanged, and no stored record needs
rewriting. See "Reading existing records" below.

## What changes

The evaluation harness evaluated one artifact against one holdout split. That
is now the degenerate case of a closed plan family:

```fsharp
type EvaluationPlan =
    | TailHoldout                                     // the previous behaviour
    | MaskedObservations of MaskedObservationsPlan     // declared / seeded masks
    | RollingOrigin of RollingOriginPlan               // advancing train/test windows
```

A plan is carried on the request and stored on the run. A multi-fold plan
additionally stores one `EvaluationFoldOutcome` per fold — fold id, window/mask
descriptor, and the provider's metric map for that fold — queryable with
`IModelEvaluationRunner.GetFoldOutcomes`. **The aggregate row is unchanged**:
under every plan, `EvaluationRun.Metrics` is still the provider's evaluation of
the whole holdout frame.

### The discipline line is unchanged

Fold **generation parameters** are data on the plan; fold **metric values** are
provider output. Fold derivation reads the observation *count* and the plan's
own declared parameters — never a row's value — so it enumerates folds without
computing anything over the data. Seeded draws are SHA-256-derived rather than
`System.Random`, so a family re-derives identically on every runtime.

### Reading existing records

Both new stored fields are optional, and the absent value is the old meaning:

| Field | Absent value | Reads as |
|---|---|---|
| `EvaluationRun.Plan` | `None` | `EvaluationPlan.TailHoldout` (`EvaluationRun.plan`) |
| `ModelComparison.Aggregation` | `None` | `FoldAggregation.AggregateMetric` (`FoldAggregation.resolveStored`) |

A pre-existing run also keeps its stored footprint exactly: the degenerate
family generates **no** folds, so a tail-holdout run stores the aggregate row
and nothing else, as before. `ModelComparison.id` is likewise preserved —
`ModelComparison.idFor … FoldAggregation.AggregateMetric` returns the identical
id, so an existing stored comparison stays reachable at the id it was written
under.

## Diff to apply

**Constructing an `EvaluationRequest`** — add `Plan`. `None` is the previous
behaviour:

```fsharp
// Before
{ ScopeId = scope; ArtifactKeyHash = key; Holdout = holdout; EvaluatedBy = "u1" }

// After — unchanged behaviour
{ ScopeId = scope; ArtifactKeyHash = key; Holdout = holdout; Plan = None; EvaluatedBy = "u1" }

// After — a three-window rolling-origin family
{ ScopeId = scope
  ArtifactKeyHash = key
  Holdout = holdout
  Plan =
    Some(
        EvaluationPlan.RollingOrigin {
            InitialTrainSize = 4
            TestSize = 2
            Step = 3
            FoldCount = 3
            Expanding = true
        }
    )
  EvaluatedBy = "u1" }
```

**Constructing a `ComparisonRequest`** — add `Aggregation`. It is **declared,
never defaulted**: a fold family only means something once the comparer says
which number it is judged by.

```fsharp
// Before
{ ScopeId = scope; Entrants = entrants; Holdout = holdout
  PrimaryMetric = "rmse"; Direction = MetricDirection.LowerIsBetter; ComparedBy = "u1" }

// After — identical ranking AND identical comparison id
{ ScopeId = scope; Entrants = entrants; Holdout = holdout
  PrimaryMetric = "rmse"; Direction = MetricDirection.LowerIsBetter
  Aggregation = FoldAggregation.AggregateMetric
  ComparedBy = "u1" }

// After — rank each entrant on its worst fold instead
  Aggregation = FoldAggregation.WorstFold
```

`MeanAcrossFolds` and `WorstFold` refuse partial evidence: an entrant whose run
has no fold family, or whose family is missing the primary metric on any fold,
lands in `MissingMetric` rather than being ranked on whichever folds happened to
report.

**Implementing `IModelEvaluationRunner`** — one new member:

```fsharp
member _.GetFoldOutcomes(scopeId, runId) : Async<EvaluationFoldOutcome list> = …
```

**Matching on `EvaluationError`** — two new cases, both terminal:
`PlanUnsupported of providerId * planKind` and `PlanInvalid of reason`.

## Evaluating a fold family in a provider

A provider that only implements `IModelEvaluationMetrics` keeps working for
`TailHoldout` and **refuses every other family by name** — deliberately, because
quietly evaluating the whole frame and storing the answer as if the family had
been honoured is wrong in a way nothing downstream can detect. To evaluate fold
families, implement the plan-aware seam on the same object:

```fsharp
interface IModelEvaluationPlanMetrics with
    // Declarative data: the envelope reads this and refuses an undeclared
    // kind WITHOUT calling the provider.
    member _.SupportedPlanKinds() = EvaluationPlan.allKindNames

    member _.EvaluateFoldMetrics(context) = async {
        // context.Predictions / context.Actuals are the WHOLE frames — the
        // platform slices nothing, so feature construction that needs a
        // contiguous series keeps one. Apply the window yourself:
        let rows =
            match context.Fold.Window with
            | FoldWindow.WholeHoldout -> context.Predictions
            | FoldWindow.MaskedOrdinals ordinals ->
                let masked = Set.ofList ordinals

                context.Predictions
                |> List.indexed
                |> List.filter (fst >> masked.Contains >> not)
                |> List.map snd
            | FoldWindow.TrainTestWindow(_, _, testStart, testEnd) ->
                context.Predictions
                |> List.indexed
                |> List.filter (fun (i, _) -> i >= testStart && i < testEnd)
                |> List.map snd

        return Ok(Map [ "rmse", yourMetric rows ])
    }
```

`context.Fold` is `EvaluationFold.aggregate` (`WholeHoldout`, index `-1`) for
the run's aggregate row and `fold-<n>` for each family member.

## Verification

- `dotnet build ToolUp.Forge.sln` — surfaces every request construction site and
  every `IModelEvaluationRunner` implementation.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
  — the `IModelEvaluationPlanMetrics` contract pack pins the declaration /
  refusal / determinism contract; the evaluation pack drives a three-window
  rolling family and a seeded blocked mask end to end and asserts a
  tail-holdout run is unchanged.

## Rollback

Revert the SDK version pin. Stored fold outcomes are separate data objects
under their own data type, so an older build simply does not read them; runs and
comparisons written by this version carry two extra optional fields that an
older deserialiser ignores. No data migration either way.

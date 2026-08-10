// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IModelEvaluationPlanMetricsContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.ModelProviders.Reference

// ─── Phase 652 — plan-aware evaluation-metrics seam conformance pack ────
//
// The portable contract of `IModelEvaluationPlanMetrics` and the envelope
// that dispatches to it, bound here to the in-tree reference provider. Any
// external plan-aware provider can be held to the same bar by binding
// `contractFor`.
//
// What the pack pins:
//   * a provider's `SupportedPlanKinds()` is DECLARATIVE DATA — non-empty,
//     stable across calls, and drawn only from the closed plan vocabulary,
//     because the envelope reads it to decide whether to call the provider
//     at all;
//   * the whole frames arrive at every fold — the envelope slices nothing,
//     so a provider whose feature construction needs contiguity keeps it;
//   * a declared plan kind evaluates; an UNDECLARED one is refused BY NAME
//     with no provider call and no tail-holdout fallback;
//   * a provider carrying only the Phase 456 whole-frame seam still serves
//     `TailHoldout` unchanged, and refuses every other family;
//   * determinism (plan D4): identical context ⇒ identical metric map;
//   * a provider refusal is a typed `ProviderRefused`, never an exception,
//     and a RAISING provider is a typed `ProviderFailed`.
//
// GP 12: everything crossing the seam here is a value — plan and fold
// records of primitives, frames as lists, a `Map<string,float>` out — and
// every call is `Async`. No handle, no callback, no ordering promise.

let private t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

/// A four-column panel frame: unit / period / feature / float target.
let private schema: DatasetSchema = {
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

let private rows (count: int) : DatasetRow list =
    [ 0 .. count - 1 ]
    |> List.map (fun i -> {
        Cells = [
            DatasetValue.Categorical "north"
            DatasetValue.Timestamp(t0.AddDays(7.0 * float i))
            DatasetValue.Float(100.0 + float i)
            DatasetValue.Float(1000.0 + float i)
        ]
    })

/// A fold context over an `n`-observation frame.
let private contextFor (plan: EvaluationPlan) (fold: EvaluationFold) (n: int) : EvaluationFoldContext = {
    Plan = plan
    Fold = fold
    PredictionsSchema = schema
    Predictions = rows n
    ActualsSchema = schema
    Actuals = rows n
}

let private rollingPlan =
    EvaluationPlan.RollingOrigin {
        InitialTrainSize = 4
        TestSize = 2
        Step = 3
        FoldCount = 3
        Expanding = true
    }

/// A fit provider carrying ONLY the Phase 456 whole-frame metrics seam.
let private planBlindProvider () : IModelFitProvider =
    let inner = ReferenceModelFitProvider.create ()

    { new IModelFitProvider with
        member _.Kind = "plan-blind"
        member _.ProviderVersion = "1.0.0"
        member _.DeclareGates() = []
        member _.Fit(request) = inner.Fit request
      interface IModelEvaluationMetrics with
          member _.EvaluateMetrics(predictionsSchema, predictions, actualsSchema, actuals) =
              match inner with
              | :? IModelEvaluationMetrics as metrics ->
                  metrics.EvaluateMetrics(predictionsSchema, predictions, actualsSchema, actuals)
              | _ -> async { return Error "unreachable — the reference provider implements the seam" }
    }

/// A provider that declares a plan kind and then refuses it — the typed
/// refusal path, distinct from an undeclared kind.
let private refusingProvider () : IModelFitProvider =
    { new IModelFitProvider with
        member _.Kind = "refusing"
        member _.ProviderVersion = "1.0.0"
        member _.DeclareGates() = []
        member _.Fit(_) = failwith "not reachable in this pack"
      interface IModelEvaluationPlanMetrics with
          member _.SupportedPlanKinds() = EvaluationPlan.allKindNames
          member _.EvaluateFoldMetrics(_) = async { return Error "this provider evaluates nothing" }
    }

/// A provider that RAISES rather than returning a refusal.
let private raisingProvider () : IModelFitProvider =
    { new IModelFitProvider with
        member _.Kind = "raising"
        member _.ProviderVersion = "1.0.0"
        member _.DeclareGates() = []
        member _.Fit(_) = failwith "not reachable in this pack"
      interface IModelEvaluationPlanMetrics with
          member _.SupportedPlanKinds() = EvaluationPlan.allKindNames
          member _.EvaluateFoldMetrics(_) = failwith "provider blew up"
    }

/// The conformance pack for one plan-aware provider. An external provider
/// binds this with its own factory and the plan kinds it declares.
let contractFor (name: string) (provider: unit -> IModelFitProvider) =
    testList $"IModelEvaluationPlanMetrics contract — {name}" [
        testCase "SupportedPlanKinds is non-empty, stable, and drawn from the closed plan vocabulary"
        <| fun _ ->
            match provider () with
            | :? IModelEvaluationPlanMetrics as planned ->
                let first = planned.SupportedPlanKinds()
                Expect.isNonEmpty first "a plan-aware provider declares at least one plan kind"

                Expect.all
                    first
                    EvaluationPlan.isKnownKind
                    "every declared kind is a real plan kind — an unknown token can never be dispatched"

                Expect.equal (planned.SupportedPlanKinds()) first "the declaration is stable across calls"
            | other -> failtestf "%s must implement IModelEvaluationPlanMetrics; got %A" name (other.GetType())

        testCaseAsync "every declared plan kind evaluates, and the provider receives the WHOLE frames"
        <| async {
            let p = provider ()

            let declared =
                match p with
                | :? IModelEvaluationPlanMetrics as planned -> planned.SupportedPlanKinds()
                | _ -> []

            if List.contains "RollingOrigin" declared then
                match EvaluationPlan.folds rollingPlan 12 with
                | Error reason -> failtestf "the family must derive: %s" reason
                | Ok folds ->
                    for fold in folds do
                        let context = contextFor rollingPlan fold 12

                        Expect.equal
                            (List.length context.Predictions)
                            12
                            "the fold call carries every observation — the envelope slices nothing"

                        match! p.EvaluateFold context with
                        | Ok metrics -> Expect.isNonEmpty (Map.toList metrics) "a declared fold yields metrics"
                        | Error e -> failtestf "a declared plan kind must evaluate: %s" (EvaluationError.describe e)
        }

        testCaseAsync "identical contexts produce identical metric maps (plan D4)"
        <| async {
            let p = provider ()
            let context = contextFor EvaluationPlan.TailHoldout EvaluationFold.aggregate 12

            let! first = p.EvaluateFold context
            let! second = p.EvaluateFold context
            Expect.equal first second "a deterministic provider is a pure function of its context"
        }
    ]

let tests =
    testList "IModelEvaluationPlanMetrics — plan-aware evaluation seam" [

        yield contractFor "reference provider" (fun () -> ReferenceModelFitProvider.create ())

        yield
            testCaseAsync "an UNDECLARED plan kind is refused by name — the provider is never called"
            <| async {
                let p = planBlindProvider ()

                match!
                    p.EvaluateFold(
                        contextFor rollingPlan (EvaluationFold.at 0 (FoldWindow.TrainTestWindow(0, 4, 4, 6))) 12
                    )
                with
                | Error(EvaluationError.PlanUnsupported(providerId, planKind)) ->
                    Expect.equal providerId "plan-blind" "the refusal names the provider"
                    Expect.equal planKind "RollingOrigin" "and the kind it does not declare"
                | other -> failtestf "an undeclared plan kind must refuse; got %A" other
            }

        yield
            testCaseAsync "a Phase 456 whole-frame provider still serves TailHoldout unchanged"
            <| async {
                let p = planBlindProvider ()

                match! p.EvaluateFold(contextFor EvaluationPlan.TailHoldout EvaluationFold.aggregate 12) with
                | Ok metrics ->
                    Expect.isTrue
                        (Map.containsKey "mean_prediction" metrics)
                        "the degenerate plan routes to the Phase 456 seam exactly as before"
                | Error e -> failtestf "TailHoldout must still evaluate: %s" (EvaluationError.describe e)
            }

        yield
            testCaseAsync "a provider implementing NEITHER seam is EvaluationUnsupported, not PlanUnsupported"
            <| async {
                let bare =
                    { new IModelFitProvider with
                        member _.Kind = "bare"
                        member _.ProviderVersion = "1.0.0"
                        member _.DeclareGates() = []
                        member _.Fit(_) = failwith "not reachable in this pack"
                    }

                match! bare.EvaluateFold(contextFor rollingPlan EvaluationFold.aggregate 12) with
                | Error(EvaluationError.EvaluationUnsupported providerId) ->
                    Expect.equal providerId "bare" "the two refusals stay distinguishable"
                | other -> failtestf "a provider with no evaluation seam is EvaluationUnsupported; got %A" other
            }

        yield
            testCaseAsync "a declared-then-refused fold is a typed ProviderRefused carrying the provider's reason"
            <| async {
                let p = refusingProvider ()

                match! p.EvaluateFold(contextFor rollingPlan EvaluationFold.aggregate 12) with
                | Error(EvaluationError.ProviderRefused(providerId, reason)) ->
                    Expect.equal providerId "refusing" "the refusal names the provider"
                    Expect.stringContains reason "evaluates nothing" "and carries its own words verbatim"
                | other -> failtestf "a provider refusal is typed; got %A" other
            }

        yield
            testCaseAsync "a RAISING provider surfaces as an exception the runner maps, not as a metric map"
            <| async {
                let p = raisingProvider ()

                let! outcome = async {
                    try
                        let! r = p.EvaluateFold(contextFor rollingPlan EvaluationFold.aggregate 12)
                        return Ok r
                    with ex ->
                        return Error ex.Message
                }

                match outcome with
                | Error message -> Expect.stringContains message "blew up" "the raise propagates for the runner to map"
                | Ok r -> failtestf "a raising provider must not yield a result; got %A" r
            }
    ]
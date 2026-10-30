// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── What kind of run a piece of background work is ──────────────────
//
// The grounding and modelling plane runs nine kinds of work: a fact-table
// run, a fit, an evaluation, one fold of an evaluation, a comparison, a
// promotion decision, a dataset assembly, a dataset re-vintage and an
// external work item. `RunKind` names them once, so a run's identity frame
// (`RunKind.label`, the first field of the canonical frame a run id is
// taken over) and the scheduler handler that executes a kind
// (`RunKind.handlerName`) are read from one place rather than spelled in
// each file that needs them.
//
// **Not every kind is a scheduled job, and not every scheduled job is a
// kind.** Four kinds are executed by a scheduler handler of their own
// (`ModelFit`, `Evaluation`, `PromotionDecision`, `Revintage`), and
// `handlerName` returns their `_platform.*` names byte for byte. The other
// five run inline inside another run (a fold inside an evaluation, a
// comparison over stored evaluations, an assembly inside its caller, a
// fact-table run inside the writer) or are handed to an external backend,
// so they have no handler and `handlerName` says so with `None` rather
// than inventing a name no scheduler has registered. The scheduled jobs
// that are not runs of a kind — a fit batch's item, a scoring pass, the
// standing re-evaluation sweep and a fact recompute — keep the names
// their modules declare. Pure data: Fable-compatible.

/// The kind of a run in the grounding and modelling plane.
[<RequireQualifiedAccess>]
type RunKind =
    | FactTableRun
    | ModelFit
    | Evaluation
    | FoldEvaluation
    | Comparison
    | PromotionDecision
    | Assembly
    | Revintage
    | ExternalWork

[<RequireQualifiedAccess>]
module RunKind =

    /// Every kind, in declaration order.
    let all: RunKind list = [
        RunKind.FactTableRun
        RunKind.ModelFit
        RunKind.Evaluation
        RunKind.FoldEvaluation
        RunKind.Comparison
        RunKind.PromotionDecision
        RunKind.Assembly
        RunKind.Revintage
        RunKind.ExternalWork
    ]

    /// The kind's stable label — the first field of the canonical frame a
    /// run's id is taken over, so two kinds can never share an id. Stored
    /// identity: never rename a label.
    let label (kind: RunKind) : string =
        match kind with
        | RunKind.FactTableRun -> "fact-table-run"
        | RunKind.ModelFit -> "model-fit"
        | RunKind.Evaluation -> "evaluation"
        | RunKind.FoldEvaluation -> "fold-evaluation"
        | RunKind.Comparison -> "comparison"
        | RunKind.PromotionDecision -> "promotion-decision"
        | RunKind.Assembly -> "assembly"
        | RunKind.Revintage -> "revintage"
        | RunKind.ExternalWork -> "external-work"

    /// The scheduler handler name that executes a run of this kind, or
    /// `None` when the kind runs inline inside another run or on an
    /// external backend and has no handler of its own. Registered with
    /// both schedulers and persisted on every queued job: never rename.
    let handlerName (kind: RunKind) : string option =
        match kind with
        | RunKind.ModelFit -> Some "_platform.modelfit.run"
        | RunKind.Evaluation -> Some "_platform.modeleval.run"
        | RunKind.PromotionDecision -> Some "_platform.modelpromotion.evaluate"
        | RunKind.Revintage -> Some "_platform.dataset.revintage"
        | RunKind.FactTableRun
        | RunKind.FoldEvaluation
        | RunKind.Comparison
        | RunKind.Assembly
        | RunKind.ExternalWork -> None
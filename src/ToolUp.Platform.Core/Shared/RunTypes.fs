// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

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
// ─── What a run declares when it is opened ──────────────
//
// A run declares, at open, what it was computed from, which part of its
// subject it replaces and which method produced it. Those declarations are
// part of the run's RECORD, so whoever reads the record after the fact (a
// commit observer, the browse status, the delegate that mints the run's
// rows) reads them from the record alone, never from a request-scoped
// capture. `RunOptions` is that record form: plain data in the platform
// core, so a run record can carry it wherever the record is kept. The
// producer-facing builders (`FactTableRunOptions` in the fact tier) project
// onto it; the fact tier's richer period and method types live a layer
// above this assembly, so the record holds their fields, not the types.
// The three records require qualified access: their labels are the fact
// tier's own (`From`/`To`/`Label`, `MethodId`/`MethodVersion`,
// `Inputs`/`Slice`/`Method`), and an unqualified literal of those types
// must keep inferring the fact tier's records.

/// The period a run replaces: half-open `[From, To)` in UTC, with the
/// optional human label the producer gave it.
[<RequireQualifiedAccess>]
type RunSlice = {
    From: DateTime
    To: DateTime
    Label: string option
}

/// The method a run declares it was produced by: the derivation's stable id
/// and the version of it that ran.
[<RequireQualifiedAccess>]
type RunMethod = {
    MethodId: string
    MethodVersion: string
}

/// What a run declared when it was opened.
[<RequireQualifiedAccess>]
type RunOptions = {
    /// The content hashes of the inputs the run was computed from, in the
    /// order the writer keeps them. Empty declares nothing.
    Inputs: string list
    /// The period slice the run replaces; `None` replaces the whole subject.
    Slice: RunSlice option
    /// The method the run declared; `None` declares nothing.
    Method: RunMethod option
}

[<RequireQualifiedAccess>]
module RunOptions =

    /// No declarations — also what a record written before the options were
    /// kept on it reads as.
    let empty: RunOptions = {
        RunOptions.Inputs = []
        Slice = None
        Method = None
    }

    /// The options as read from a stored record: a record written before
    /// the options were kept carries none (a missing member reads as null),
    /// and reads as `empty`; a null inputs list reads as no inputs.
    let ofStored (options: RunOptions) : RunOptions =
        if isNull (box options) then
            empty
        elif isNull (box options.Inputs) then
            { options with RunOptions.Inputs = [] }
        else
            options
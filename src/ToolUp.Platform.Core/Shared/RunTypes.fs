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
// ─── One record for every kind of run ────────────────────────────────
//
// Every run the plane keeps — a fact-table run, a fit, an evaluation and its
// folds, a comparison, a promotion decision — is read as ONE envelope: its
// kind, the scope it ran in, what it declared, where its input came from,
// how it ended, and an id over the canonical frame with the kind's label
// first, so two kinds can never share an id. Each kind keeps its own stored
// record (the fields only it has); the envelope is that record's projection,
// built by the reader in the scope the platform resolved for it.
//
// **The scope is a `ResolvedScope`, never a string.** What a store keeps at
// rest names its shard; a projection takes the scope the reader resolved and
// refuses a record stored under any other shard (`RunRecord.inScope`), so a
// run read across shards cannot be built. Nothing here mints a scope.

/// Where a run's input came from.
[<RequireQualifiedAccess>]
type RunProvenance =
    /// Computed by the deployment.
    | Computed
    /// Imported under the named certificates.
    | Imported of certificateRefs: string list

/// How a run stands.
[<RequireQualifiedAccess>]
type RunOutcome =
    /// Opened, not yet ended.
    | Open
    /// Ended with a result; `result` addresses what the run produced.
    | Succeeded of result: string
    /// Ended without a result; nothing was produced.
    | Failed of reason: string
    /// Ended by its producer without a result.
    | Abandoned of reason: string

/// One run of any kind, in the scope the platform resolved for its reader.
type RunRecord = {
    /// `RunRecord.id Kind <the kind's identity fields>`.
    Id: string
    /// What kind of run it is.
    Kind: RunKind
    /// The scope it ran in.
    Scope: ResolvedScope
    /// What the run is of: a table id, an artifact's key hash.
    Subject: string
    /// When it started, or for a run recorded once, when it was recorded (UTC).
    StartedAt: DateTime
    /// What it declared when it was opened; `RunOptions.empty` for a kind
    /// that declares nothing.
    Options: RunOptions
    /// Where its input came from.
    Provenance: RunProvenance
    /// How it stands.
    Outcome: RunOutcome
}

[<RequireQualifiedAccess>]
module RunRecord =

#if !FABLE_COMPILER
    /// A run's id: the canonical frame's digest of the kind's label followed
    /// by the kind's identity fields. Stored identity: never reorder a kind's
    /// fields.
    let id (kind: RunKind) (fields: string list) : string =
        CanonicalFrame.digest (RunKind.label kind :: fields)
#endif

    /// `scope` when it is the shard a record was stored under: the check
    /// every projection makes before it builds a record in the reader's
    /// resolved scope. A record stored under another shard, or a reader with
    /// the anonymous scope over a real shard, is refused by name.
    let inScope (scope: ResolvedScope) (storedScopeId: string) : Result<ResolvedScope, string> =
        if scope.ScopeId = storedScopeId then
            Ok scope
        else
            Error(
                sprintf
                    "the run is stored under shard '%s' and cannot be read in a scope resolved for '%s'"
                    storedScopeId
                    scope.ScopeId
            )
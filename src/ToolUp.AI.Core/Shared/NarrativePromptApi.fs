// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System
open ToolUp.Platform

// ─── A grounded narrative's wording, edited in the platform ───
//
// A grounded narrative is declared at composition with its instruction
// (`Prompt`) and a guidance line per section. This contract lets an Owner or
// Admin of the publishing team replace THAT wording for their team, keep
// every earlier wording as a numbered version, reset to the declaration, and
// run the narrative now with the wording in force.
//
// What it cannot reach is the point of the design: the grounding rules the
// run appends (every figure a citation token for a fact a tool returned, or
// the report is refused), the section ids, headings and order, the title,
// the metrics and the surface. `Rules` is returned so the admin can read
// what the wording sits inside; nothing writes it.

/// One section's guidance line, by the section id the narrative declares.
type NarrativeSectionGuidance = { SectionId: string; Guidance: string }

/// One declared section, as the declaration states it. Read-only.
type NarrativeDeclaredSection = {
    SectionId: string
    Heading: string
    Guidance: string
}

/// One saved version of a narrative's wording in a team. Version numbers
/// start at 1 and only grow; version 0 is the declaration itself. A reset is
/// a version too (`IsReset`, with no wording), so the history says when the
/// declaration was put back and by whom.
type NarrativePromptVersion = {
    Version: int
    /// `true` when this version put the declared wording back.
    IsReset: bool
    /// The instruction. Empty on a reset.
    Prompt: string
    /// The guidance per section this version replaces; a declared section it
    /// does not name keeps its declared guidance. Empty on a reset.
    Guidance: NarrativeSectionGuidance list
    /// The user id that saved it.
    Author: string
    SavedAt: DateTime
}

/// The most recent run of a narrative in the team, read from the audit
/// trail.
type NarrativeLastRun = {
    /// `published`, `refused`, `failed`, `unfunded` or `over-budget`.
    Outcome: string
    /// What started it (`report-subscription`, `data-arrival`,
    /// `admin-regenerate`, ...).
    Trigger: string
    /// The wording version that produced it; 0 is the declaration.
    PromptVersion: int
    /// Why, for a run that was not published, or why it fell back to the
    /// declaration.
    Detail: string option
    OccurredAt: DateTime
}

/// One row of the team's narrative list.
type NarrativePromptSummary = {
    RunKey: string
    DisplayName: string
    /// The wording version in force; 0 when the declaration is.
    CurrentVersion: int
}

/// One narrative's page: the declaration, the wording in force, the
/// grounding rules it sits inside, and its last run.
type NarrativePromptView = {
    RunKey: string
    DisplayName: string
    Title: string
    DeclaredPrompt: string
    DeclaredSections: NarrativeDeclaredSection list
    /// The saved version in force; `None` when the declaration is (no
    /// version was ever saved, or the latest one is a reset).
    Current: NarrativePromptVersion option
    /// The newest version number saved, resets included (0 when none) —
    /// what a save passes as `BasedOnVersion`.
    LatestVersion: int
    /// The grounding rules the run appends after the wording. Read-only:
    /// no method of this contract changes them.
    Rules: string
    LastRun: NarrativeLastRun option
}

/// Save a new version of a narrative's wording.
type SaveNarrativePromptRequest = {
    [<PiiSafe>]
    [<NotEmpty>]
    RunKey: string
    [<NotEmpty>]
    Prompt: string
    Guidance: NarrativeSectionGuidance list
    /// The version the editor was opened on (0 for the declaration). A save
    /// over a version someone else saved since is refused, naming it.
    BasedOnVersion: int
}

/// Limits on a saved wording.
module NarrativePromptLimits =
    /// The longest instruction a version may carry, in characters.
    [<Literal>]
    let MaxPromptLength = 4000

    /// The longest guidance line a section may carry, in characters.
    [<Literal>]
    let MaxGuidanceLength = 1000

    /// How many versions a team keeps per narrative; the oldest goes first.
    [<Literal>]
    let MaxVersions = 50

// ─── Refusals that recur, and a wording an admin approves ───
//
// After every run that reaches the grounding gate, each claim it refused —
// in the first draft or in any repair round — is kept per scope and
// narrative with its class. A class that recurs under one wording version
// gets ONE proposed sentence to add to the instruction, drafted by the
// model. A proposal is never in force: it sits beside the wording history,
// and only an Owner or Admin approving it saves it as a version.

/// The class of one refused claim, as the learning loop counts it.
[<RequireQualifiedAccess>]
type NarrativeOffenceClass =
    /// A numeral in prose, or a metric, with no reference.
    | UnreferencedNumber
    /// A number written in words in prose.
    | NumberWord
    /// A numeral that is part of a name ("B2B", "Q3") read as a figure.
    | NameReadAsFigure
    /// A reference that resolves to nothing, or names a kind no deployment
    /// registered.
    | MisattributedReference
    /// A reference whose stated value is not the value it refers to.
    | MisstatedValue
    /// A reference to a fact that is no longer current.
    | Superseded
    /// A reference the publishing surface may not disclose.
    | Withheld

module NarrativeOffenceClass =
    /// Every class, in the order the panel lists them.
    let all: NarrativeOffenceClass list = [
        NarrativeOffenceClass.UnreferencedNumber
        NarrativeOffenceClass.NumberWord
        NarrativeOffenceClass.NameReadAsFigure
        NarrativeOffenceClass.MisattributedReference
        NarrativeOffenceClass.MisstatedValue
        NarrativeOffenceClass.Superseded
        NarrativeOffenceClass.Withheld
    ]

    /// What the class means, in words — for the panel and for the model
    /// drafting a proposal.
    let describe (cls: NarrativeOffenceClass) : string =
        match cls with
        | NarrativeOffenceClass.UnreferencedNumber -> "a figure written without a citation"
        | NarrativeOffenceClass.NumberWord -> "a number written in words"
        | NarrativeOffenceClass.NameReadAsFigure -> "a name containing digits, read as a figure"
        | NarrativeOffenceClass.MisattributedReference -> "a citation naming something that does not exist"
        | NarrativeOffenceClass.MisstatedValue -> "a citation stating a value other than the fact's"
        | NarrativeOffenceClass.Superseded -> "a citation of a fact that has been superseded"
        | NarrativeOffenceClass.Withheld -> "a citation of a fact this report may not disclose"

/// One claim the gate refused in a run.
type NarrativeOffence = {
    Class: NarrativeOffenceClass
    /// The section id it sat in; empty when the location names none.
    Section: string
    /// `prose`, `list`, `table`, `entry` or `element`.
    ElementKind: string
    /// The gate's location, verbatim.
    Location: string
    /// The text carrying the claim.
    Claim: string
    /// `true` when a repair round fixed it; `false` when it was withheld
    /// from the published report or the run was refused.
    Repaired: bool
}

/// One run of a narrative that reached the gate, with every claim it
/// refused. A run whose first draft passed carries none — and still counts.
type NarrativeOffenceRun = {
    RanAt: DateTime
    /// The wording version in force; 0 is the declaration.
    WordingVersion: int
    /// `published`, `published-with-withheld` or `refused`.
    Outcome: string
    Offences: NarrativeOffence list
}

/// Where a proposed sentence stands.
[<RequireQualifiedAccess>]
type NarrativeProposalState =
    /// Waiting for an Owner or Admin. Never in force.
    | Open
    /// Approved, and saved as this wording version.
    | Approved of version: int
    /// Dismissed. The class is not proposed again until it recurs under a
    /// later wording version.
    | Dismissed
    /// The wording in force changed before it was decided, so the evidence
    /// it was drafted from no longer describes the wording.
    | Lapsed

/// One proposed sentence to add to a narrative's instruction.
type NarrativeWordingProposal = {
    Id: string
    Class: NarrativeOffenceClass
    /// The sentence approving it appends to the instruction.
    Sentence: string
    /// The wording version in force when the class recurred; 0 is the
    /// declaration.
    WordingVersion: int
    /// The refused claims it was drafted from.
    Evidence: string list
    /// Sentences approved earlier for the same class that did not stop it.
    Earlier: string list
    ProposedAt: DateTime
    State: NarrativeProposalState
    /// Who approved or dismissed it.
    DecidedBy: string option
    DecidedAt: DateTime option
}

/// How often a class was refused under one wording version, over the
/// recurrence window.
type NarrativeOffenceStanding = {
    Class: NarrativeOffenceClass
    WordingVersion: int
    /// Runs under the version, in the window (at most its size).
    Runs: int
    /// Of those, the runs that refused the class at least once.
    RunsWithClass: int
    /// Whether the class recurs: `RunsWithClass` reached the threshold.
    Recurs: bool
}

/// One proposal as the report admin shows it.
type NarrativeProposalView = {
    Proposal: NarrativeWordingProposal
    /// The instruction approving it would put in force.
    ResultingPrompt: string
    /// For an approved proposal, how its class has fared under the version
    /// it made — whether the sentence stopped the class recurring.
    FollowThrough: NarrativeOffenceStanding option
}

/// A narrative's learning page: its proposals and how each refused class
/// stands under the wording in force.
type NarrativeLearningView = {
    RunKey: string
    /// Open proposals first, then the rest, newest first.
    Proposals: NarrativeProposalView list
    /// Each class refused under the wording in force.
    Standing: NarrativeOffenceStanding list
    /// The recurrence rule: `Threshold` of the last `Window` runs.
    Threshold: int
    Window: int
}

/// Approve or dismiss one proposal.
type NarrativeProposalDecision = {
    [<PiiSafe>]
    [<NotEmpty>]
    RunKey: string
    [<NotEmpty>]
    ProposalId: string
}

/// Remoting contract for the narrative wording admin. Every
/// method acts on the caller's ACTIVE team, which is the publishing scope,
/// and every method refuses a caller who is not an Owner or Admin of it.
/// Each save and reset is audited under the team.
type NarrativePromptApi = {
    /// Every registered grounded narrative, with the wording version in
    /// force in the team.
    [<TenantScoped>]
    ListNarratives: unit -> Async<Result<NarrativePromptSummary list, string>>

    /// One narrative's page, by run key.
    [<TenantScoped>]
    GetNarrative: string -> Async<Result<NarrativePromptView, string>>

    /// Every saved version of a narrative's wording in the team, newest
    /// first.
    [<TenantScoped>]
    ListVersions: string -> Async<Result<NarrativePromptVersion list, string>>

    /// Save a new version. Refuses a section the narrative does not declare
    /// (by name), a blank instruction, wording over the limits, and a save
    /// over a version saved since `BasedOnVersion`.
    [<TenantScoped>]
    SavePrompt: SaveNarrativePromptRequest -> Async<Result<NarrativePromptView, string>>

    /// Put the declared wording back, as a new version.
    [<TenantScoped>]
    ResetPrompt: string -> Async<Result<NarrativePromptView, string>>

    /// Run the narrative now, with the wording in force, through the job
    /// scheduler's manual trigger. Returns the job id. The run is published
    /// or refused exactly as a scheduled run is; its outcome lands on the
    /// audit trail and in `GetNarrative`'s `LastRun`.
    [<TenantScoped>]
    Regenerate: string -> Async<Result<Guid, string>>

    /// A narrative's proposed sentences, with their evidence
    /// and the wording each would produce, and how each refused class
    /// stands under the wording in force.
    [<TenantScoped>]
    ListProposals: string -> Async<Result<NarrativeLearningView, string>>

    /// Approve an open proposal: its sentence is appended to
    /// the instruction in force and saved as a new version, which the next
    /// run uses. Refused when the wording has changed since the proposal
    /// was drafted.
    [<TenantScoped>]
    ApproveProposal: NarrativeProposalDecision -> Async<Result<NarrativePromptView, string>>

    /// Dismiss an open proposal. The decision is recorded, and
    /// the class is not proposed again until it recurs under a later
    /// wording version.
    [<TenantScoped>]
    DismissProposal: NarrativeProposalDecision -> Async<Result<NarrativeLearningView, string>>
}
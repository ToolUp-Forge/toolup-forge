// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System
open ToolUp.Platform

// ─── Phase 1111 — a grounded narrative's wording, edited in the platform ───
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

/// Limits on a saved wording (Phase 1111).
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

/// Remoting contract for the narrative wording admin (Phase 1111). Every
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
}
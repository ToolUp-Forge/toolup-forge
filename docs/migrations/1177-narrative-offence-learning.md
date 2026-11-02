# Phase 1177 — a grounded run learns from refusals that recur

**What changes.** A deployment can now compose a learning loop over its grounded narratives. After
every run, each claim the grounding gate refused is recorded with its class. When a class recurs on
3 of the last 5 runs under one wording version, the run's own provider drafts ONE sentence to add to
the instruction, aimed at that class. The sentence waits in the report admin as a proposal until an
Owner or Admin approves it, which saves it as a new wording version, or dismisses it. **A proposal is
never in force.** No run reads one, and only an approved, saved version changes what the model is
sent.

The full description is in [`docs/platform/grounded-narratives.md`](../platform/grounded-narratives.md)
under "The learning loop".

## Nothing changes unless you compose it

A deployment that does not call `NarrativeOffenceLearning.compose` records nothing, drafts nothing
and spends nothing. Its runs and its report admin behave exactly as before, and the admin shows no
proposal panel.

## Opting in

Compose the loop after grounded narratives, beside the report admin (`withPromptAdmin`, the page
proposals are approved on; see [`1111-narrative-prompt-overrides.md`](1111-narrative-prompt-overrides.md)):

```fsharp skip=fragment
app
|> GroundedNarratives.compose options definitions
|> NarrativePromptHandler.withPromptAdmin
|> NarrativeOffenceLearning.compose NarrativeLearningPolicy.defaults
```

`compose` wraps the grounded run already registered. Composed before `GroundedNarratives.compose`,
the start fails, naming the order. The loop reads the audit trail (`IEventStore`) and writes to blob
storage (`IBlobStorage`). Without either, it records nothing.

The client needs no change beyond the report admin module it already lists. Its panel appears when
the server answers `ListProposals`.

## The knobs

`NarrativeLearningPolicy`:

| Field | Default | Meaning |
|---|---|---|
| `Window` | 5 | the runs, under one wording version, a recurrence is counted over |
| `Threshold` | 3 | the runs of the window that must refuse a class for it to recur |
| `MaxEvidence` | 5 | the refused claims a proposal is drafted from |
| `MaxRunsKept` | 50 | the runs kept per narrative |
| `MaxSentenceLength` | 400 | the longest sentence kept |

```fsharp skip=fragment
NarrativeOffenceLearning.compose { NarrativeLearningPolicy.defaults with Threshold = 4; Window = 7 }
```

## What a consumer sees

- **`NarrativePromptApi` gains three methods**, each an Owner/Admin call on the active team, like the
  rest of the contract: `ListProposals`, `ApproveProposal` and `DismissProposal`. A deployment that
  builds the API record itself (a fake in a test) adds the three fields.
- **New shared types** in `ToolUp.AI.Core`: `NarrativeOffenceClass`, `NarrativeOffence`,
  `NarrativeOffenceRun`, `NarrativeProposalState`, `NarrativeWordingProposal`,
  `NarrativeOffenceStanding`, `NarrativeProposalView`, `NarrativeLearningView` and
  `NarrativeProposalDecision`.
- **The report admin** (`NarrativePromptAdminUI`): its `Model` gains `Learning`, and its `Msg` gains
  `LearningLoaded`, `ApproveProposal`, `DismissProposal` and `ProposalDismissed`. Code that builds
  the model or matches its messages exhaustively adds them.
- **Two audit kinds** under the team: `Custom:NarrativeProposalApproved` and
  `Custom:NarrativeProposalDismissed`.
- **Spend.** Drafting is one model call per recurring class, through the run's own provider under
  the team's funding. It happens only on the run where the class first recurs and no proposal is open.

## Not in this phase

The background-jobs view does not yet link a narrative job with an open proposal to the report
admin. The jobs view is a platform client with no hook a module can add a row link through, and that
hook is its own change.

## Verifying

Run three refused narrative runs of one report under one wording. The report admin's "Proposed
wording" panel shows one open proposal, with the refused claims and the wording it would produce. Run
again: the instruction the model is sent is unchanged. Approve: the next run is sent the instruction
with the sentence appended, and the wording history shows the new version, authored by the approver.

## Rollback

Remove the `NarrativeOffenceLearning.compose` call. Recorded history and proposals stay in
`narrative-learning/<run key>` blobs, which nothing else reads. An approved sentence is an ordinary
wording version: reset or edit it in the report admin as any other.

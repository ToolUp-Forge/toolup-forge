# Phase 1175 — a grounded run repairs its draft, then publishes what is grounded

**What changes.** A grounded narrative run whose draft the grounding gate refuses no longer refuses
the whole report. It asks the model, in the same conversation, to rewrite the elements that hold the
refused claims, re-checks the whole document after each round, and keeps going while the rounds make
progress. When they stop, every claim still refused is cut away, a `withheld-claims` section names
each one's place and reason (never its value), and the grounded remainder is published. A day that
used to publish nothing now publishes everything that is grounded. Every number in a published
report is still a Fact: the gate's rules are unchanged, and it still judges the final document.

The full description is in [`docs/platform/grounded-narratives.md`](../platform/grounded-narratives.md)
under "The repair loop and the publication rule".

## What a consumer sees

- **A refused day publishes.** `IGroundedNarrativeRun.Run` returns `GroundedNarrativePublished` for a
  draft it repaired or pruned. `GroundedNarrativeRefused` is now returned only when the policy does
  not publish a remainder, when nothing of the report survives, or when a refused claim cannot be
  placed.
- **The job succeeds with a warning.** `GroundedNarrativeOutcome.toJobResult` maps a publication with
  withheld claims to the new `JobResult.SuccessWithWarning`. The run row is `Succeeded` with
  `JobRun.Warning`; the definition carries `LastRunWarning`; the jobs view shows the warning beside
  the status and lists warned jobs after the failed ones. It used to be a `PermanentFailure`, so the
  job was dead-lettered.
- **The audit trail has every round.** A `GroundedNarrativeRepairRound` row per round (offences in,
  offences out, progress, tokens) beside the run's outcome row, whose outcome is `published` or
  `published-with-withheld`.

## The knobs

`GroundedRepairPolicy`, set per definition with `GroundedNarrativeDefinition.withRepairPolicy`:

| Field | Default |
|---|---|
| `MaxRepairRounds` | 12 |
| `MaxRoundsWithoutProgress` | 3 |
| `MaxRunTokens` | `Some 500_000L` |
| `PublishGroundedRemainder` | `true` |

```fsharp skip=fragment
GroundedNarrativeDefinition.create key displayName moduleId principal title prompt sections
|> GroundedNarrativeDefinition.withRepairPolicy
    { GroundedRepairPolicy.defaults with MaxRoundsWithoutProgress = 2 }
```

## Keeping the old refuse-everything behaviour

```fsharp skip=fragment
|> GroundedNarrativeDefinition.withRepairPolicy GroundedRepairPolicy.refuseOnOffence
```

`refuseOnOffence` is `MaxRepairRounds = 0` with `PublishGroundedRemainder = false`: no repair round
runs, the first offence refuses the run, and the trail holds the single refusal row it always did.

## Source changes a consumer may need

These are record and union changes; the compiler names every site.

| Change | What to do |
|---|---|
| `GroundedNarrativePublished` gains a sixth field, `repair: GroundedRepairSummary` | A positional pattern adds `, _` (or match by name: `GroundedNarrativePublished(document = d)`); a construction adds `GroundedRepairSummary.complete 0`. |
| `GroundedNarrativeDefinition` gains `RepairPolicy` | Only a full record literal is affected; `GroundedNarrativeDefinition.create` sets the default. |
| `JobRun` gains `Warning`, `JobDefinition` gains `LastRunWarning` | A full record literal adds `Warning = None` / `LastRunWarning = None`. Persisted rows written before read both as `None`. |
| `JobResult` gains `SuccessWithWarning` (appended) | An exhaustive `match` over `JobResult` gains an arm; treat it as a success. |
| `JobLifecycle.RunOutcome` gains `CompletedWithWarning` (appended) | Only code that matches the scheduler's internal outcome vocabulary. |

## Verification

1. Run a grounded narrative whose draft states an unreferenced number: it publishes, with a
   `withheld-claims` section naming the claim, and the trail shows the repair rounds.
2. The narrative job's row reads `Succeeded`, and the jobs view shows its warning.
3. With `GroundedRepairPolicy.refuseOnOffence` the same run is refused, as before.

## Rollback

Set `GroundedRepairPolicy.refuseOnOffence` on each definition; the run then behaves as it did before
this change.

## Limits

The scheduled-report trigger (`GroundedNarrativeProducer`) publishes the remainder too: its
producer returns the published document, withheld-claims note included. A report subscription's own
job run does not carry the warning; the narrative's audit row and its note do.

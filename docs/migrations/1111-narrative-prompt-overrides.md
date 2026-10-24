# Phase 1111 — a report's wording is edited inside the platform

**What changes.** A grounded narrative's instruction (`GroundedNarrativeDefinition.Prompt`) and each declared
section's `Guidance` can now be reworded at runtime by an Owner or Admin of the publishing team, without a code
change or a redeploy. The declaration stays the default and the floor: it is what runs until someone saves a
wording, and what a reset puts back.

**What cannot change.** An override carries only the instruction and guidance for sections the narrative
DECLARES; an undeclared or repeated section is refused by name (`GroundedNarrativeDefinition.reword`). The title,
the section ids, headings and order, the metrics, the projections, the principal and the surface stay the
declaration's. The grounding rules (`GroundedNarrativeRun.rules`) are appended after the wording on every run and
are shown to the admin read-only. A wording that tells the model to drop citations still meets the grounding gate,
which refuses the report; the gate is the guarantee, and a test pins it.

**Where the override lives (decision).** One blob per narrative in the publishing scope's own container,
`narrative-prompts/<run key>`, written through `BlobMapStore` (the guarded read-modify-write every one-document blob
store uses). An existing per-scope settings store was considered and not reused: `IConfigStore` holds a flat,
schema-validated field map per module and has no notion of a version history. Keying by the scope's CONTAINER means
a run reads the wording of the scope it runs in, and the API writes the scope its request resolved to, so one
team's wording cannot reach another team's run.

- **Shape:** every saved version, oldest first: `Version` (from 1; 0 is the declaration), `IsReset`, `Prompt`,
  `Guidance` (per declared section id), `Author`, `SavedAt`. The latest non-reset version is in force.
- **Limits** (`NarrativePromptLimits`): instruction ≤ 4,000 characters, each guidance line ≤ 1,000, the newest 50
  versions kept. A blank instruction or guidance line is refused. A save names the version it was opened on and is
  refused when another was saved since.
- **At run time** the run resolves the wording in force for its scope. A missing wording runs the declaration. An
  unreadable one, or one that no longer fits the declaration (a section was removed), also runs the declaration and
  says why on the run's audit row. Every row now records `PromptVersion`, the wording that produced it.

**Opt in (both halves; off by default, GP 13).** Server, after composing the runs:

```fsharp skip=fragment
app
|> GroundedNarratives.compose options definitions
|> NarrativePromptHandler.withPromptAdmin
```

This mounts `NarrativePromptApi` and registers the regenerate job handler (`_narratives.regenerate`). Regenerate
needs a `ServerConfig.JobScheduler` other than `NoJobScheduler`, and is refused with a reason without one. Client:
add `NarrativePromptAdminUI.create ()` to the module list, and contribute its tile with
`AdminTileContributors = [ { new IAdminTileContributor with member _.Tiles() = [ NarrativePromptAdminUI.adminTile ] } ]`
in `ClientConfig.Handlers`. The module is Team Management, `NavRole.TeamOwnerAdmin`.

**Regenerate** schedules one manual job per narrative per team and fires it through `IJobScheduler.TriggerOnce`,
so the run is published or refused exactly as a scheduled run would be. A run that fails for good is listed in the
jobs admin (Phase 1106). Saves and resets are audited under the team as `Custom:NarrativePromptSaved` /
`Custom:NarrativePromptReset`.

**Compile breaks.** None for consumers: no existing public record gained a field.

**Verify.** Sign in as a team Owner, open "Report wording", save a new instruction, regenerate. The last run shows
the new version. Reset and regenerate: it shows version 0 (the declaration). A Member does not see the module, and
the API refuses them.

**Rollback.** Remove `withPromptAdmin` and the client module. Saved wordings stay in force for their teams until
reset; to drop them, delete the scope's `narrative-prompts/` blobs.

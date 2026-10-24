# Phase 1106 — the background-jobs admin slot

**What changes.** A job that fails for good — dead-lettered after its retries, or refused outright by its handler
(`JobResult.PermanentFailure`, also recorded as `DeadLettered`) — is now visible in the admin client. A new built-in
module, `JobsAdminUI` (`_sdk.Jobs`, "Background Jobs", Team Management, `NavRole.TeamOwnerAdmin`), lists every job in
the caller's scope over `JobApi.ListJobs`: permanently failed jobs first, marked "Failed — will not retry", with the
job's `LastRunError` verbatim. A failed, active job offers a re-run behind a confirmation (`JobApi.TriggerOnce`), and
the row shows the new attempt's result when `JobApi.GetJob` reports it. A failed job that is disabled or cancelled is
not offered the action; the view says why. It is reached from the admin home through the "Background jobs" tile.

It fills a new shell slot, `ShellSlotFills.JobsAdmin` (gated on any authenticated surface, carries the tile), which
**defaults to `SlotFill.Empty`**, because `JobApi` is mounted only when `ServerConfig.JobScheduler` is
`InProcessJobScheduler` or `QuartzJobScheduler _` (GP 13). The module's copy is a new catalog section,
`MessageCatalog.JobsAdmin` (`JobsAdminMessages`).

**Opt in** — one line beside your existing slot configuration:

```fsharp skip=fragment
let clientConfig = {
    ClientConfig.defaults with
        Slots = {
            ClientConfig.defaults.Slots with
                JobsAdmin = SlotFill.Default
        }
}
```

`SlotFill.Configured { Name = …; Icon = … }` relabels it; `SlotFill.External yourModule` replaces it and keeps the tile.

**Compile breaks, and the fix.** Both records gained a field, so a FULL-literal construction stops compiling (FS0764):

- a `ShellSlotFills` written field by field — add `JobsAdmin = SlotFill.Empty` (or start from
  `ShellSlotFills.defaults with …`);
- a `MessageCatalog` written field by field (a full translation) — add `JobsAdmin = { … }`, or start from
  `MessageCatalog.english with …` so new sections fall back to English.

A copy-and-update (`{ x with … }`) needs no change.

**Verify.** With a scheduler configured and the slot filled, sign in as a team Owner or Admin, open the administration
area: the "Background jobs" tile is present and opens the list. A plain Member does not see the tile or the rail entry.

**Rollback.** Set `JobsAdmin = SlotFill.Empty` (the default). Nothing server-side changed.

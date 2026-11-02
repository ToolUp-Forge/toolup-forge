# 1161 — The delegate executor completes: attributed rows, period slices, one run envelope

**Breaking, 0.38.0.** A delegated table now takes a period slice, a delegated read that cannot reach
its storage answers `StorageFault`, and every kind of run is read as one `RunRecord` over a
`ResolvedScope`. Two operator tasks repair what a deployment persisted before.

## What changes

| Before | After |
|---|---|
| `DelegateTableWriter.OpenRun` with a slice: `FactTableRunOptionsRefused` | the slice is honoured: rows inside it are replaced, every other row is carried under the run that wrote it |
| a delegated row was minted under the image's run | under the run that wrote it (the same thing for a whole-table run) |
| a delegated point read whose table could not be read: one `Absent` refusal fact | `Error(StorageFault …)` (and the same for a quote or lineage read, and the population door) |
| `IFactTableWriterContract.runOptionsRefusedTests` | removed: `runOptionsTests` binds every writer |
| — | `RunRecord`, `RunProvenance`, `RunOutcome`, `RunRecord.id`, `RunRecord.inScope` (`ToolUp.Platform.Core`); `toRunRecord` on `FactTableRun`, `EvaluationRun`, `EvaluationFoldOutcome`, `ModelComparison`, `ModelArtifact`, `PromotionDecision`; `FactTableRunProvenance.toRun` |
| — | `FactsOperatorTaskCompose.withFactsOperatorTasks`; `toolup facts migrate --attribute-delegate-rows` and `--reissue-jobs`; `IFactStoreContract.readFaultTests` |
| `FactsMigrateCommand.Options` | gains `Task` (the operator task the verb runs) |

## The rewrite

Compose the repairs beside the fact store, once:

```diff
 app
 |> FactsCompose.withFactStore …
+|> FactsOperatorTaskCompose.withFactsOperatorTasks
```

**A deployment with delegated tables** records which run wrote each stored row, so a sliced run can
be taken. The writer of each image is read from the table's head; nothing is guessed:

```text
toolup facts migrate --attribute-delegate-rows --all-scopes --dry-run
toolup facts migrate --attribute-delegate-rows --all-scopes
```

Until it runs, a slice over a pre-0.38.0 image is refused by name; whole-table runs and reads are
unaffected.

**A deployment with fact jobs scheduled before 0.38.0** under a bare shard id (the runner refuses them,
`carries no resolver-minted scope`): say which kind the shard is, and name a user your scope resolver
resolves to exactly that scope. The dry run lists the refused jobs by id and handler:

```text
toolup facts migrate --reissue-jobs --scope team:<id> --principal <user-id> --dry-run
toolup facts migrate --reissue-jobs --scope team:<id> --principal <user-id>
```

A principal that resolves to any other scope re-issues nothing (exit 1). The task mints no scope:
the deployment's own resolver does, through the platform's `DeclaredPrincipalResolver`.

**A caller that matched the delegated refusal fact** for an unreadable table now handles the
`StorageFault` error instead. **A full literal of `FactsMigrateCommand.Options`** adds `Task`.

## Verifying

Bind `IFactTableWriterContract.runOptionsTests` and `IFactStoreContract.readFaultTests` to your writer
and store. The CLI help (`toolup facts migrate --help`) documents both repairs.

## Rollback

Pin the 0.37 packages. An image written by a 0.38 sliced run carries rows of earlier runs; a 0.37
reader mints those rows under the image's run, so roll back only before a sliced run has committed.

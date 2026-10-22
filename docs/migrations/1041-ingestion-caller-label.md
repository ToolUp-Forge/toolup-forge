# An ingestion names its result by a caller label

**Ships in:** ToolUp.Platform.Server (`IngestionLabel`, `IngestionLabelError`, `ICallerLabelledIngestor`, the
`IDataIngestor.RunIngestion(scopeId, sourceId, table, label)` overload, `DataIngestor.MaxResultNameLength`,
`DataIngestionJobHandler.payloadForLabelled`), ToolUp.DataSources.Sql and ToolUp.DataSources.Synapse (`GetSchema` on a
statement). Phase 1041. Additive: no existing type or member changes.

**Affected:** deployments that ingest a SQL statement, rather than a table name, through `IDataIngestor`; and any
that bypassed the ingestor because a long statement could not be saved.

## What changes

`RunIngestion(scopeId, sourceId, table)` names everything it writes by `table`: the payload object
`_dataingestion__{sourceId}__{table}`, the schema object `_dataingestion_schema__{sourceId}__{table}`, the payload's
`table` metadata and the run record's `Table`. For a SQL connector the "table" may be a statement, and a statement
longer than a path component made a name the blob store refused, after the source had already been queried. Such a
source lost the run record, the lifecycle events and schema-drift detection.

A **caller label** now separates what the result is called from what the connector runs:

| | Named by | Reaches the connector |
|---|---|---|
| `RunIngestion(scopeId, sourceId, table)` | `table`, byte-identical to before | `table` |
| `RunIngestion(scopeId, sourceId, table, label)` | the label | `table`, untouched |

`IngestionLabel.tryCreate` accepts 1 to 128 characters of `A-Z a-z 0-9 - _ .` (not `.` or `..`) and refuses
anything else with a typed `IngestionLabelError`: `EmptyLabel`, `LabelTooLong`, `LabelHasInvalidCharacter`
(naming the character and position) or `LabelIsDotSegment`. `IngestionLabel.describeError` renders one.

An unlabelled ingestion whose derived payload name would pass 255 characters is now refused **before** the source
is probed or queried, as `SchemaMismatch` with a message naming the caller-label option. The failed attempt is still
recorded and still emits `IngestionRunFailed`. Before, the same call queried the source and then failed at the save.

The Sql and Synapse connectors' `GetSchema` now accepts a statement-shaped table, the same shape `Query` already
passed through verbatim. Sql reads the statement's result-set columns with `CommandBehavior.SchemaOnly`. Synapse
calls `sp_describe_first_result_set` with the statement bound as a parameter, so it is never executed. A bare
identifier reads the catalogue exactly as before. A labelled statement therefore keeps its schema object and
schema-drift detection.

A scheduled pull may carry the label: `DataIngestionJobHandler.payloadForLabelled sourceId table label` writes
`{"sourceId":…,"table":…,"label":…}`. A payload without `label` runs as before, and `payloadFor` is unchanged. A
label that fails validation is a `PermanentFailure` naming the reason.

## What a deployment does

**Nothing**, unless it ingests statements. To ingest one under a name of your choosing:

```fsharp skip=fragment
match IngestionLabel.tryCreate "orders-last-30-days" with
| Ok label -> ingestor.RunIngestion(scopeId, "warehouse", statement, label)
| Error refusal -> failwith (IngestionLabel.describeError refusal)
```

The overload is an extension on `IDataIngestor` (in `ToolUp.Platform`, auto-opened). It calls the SDK's
`DataIngestor` through `ICallerLabelledIngestor`. A custom `IDataIngestor` that does not implement that interface
refuses a labelled call with `UnexpectedFailure` and runs nothing. Implement `ICallerLabelledIngestor` to support it.

A deployment that bypassed the ingestor, running the statement through `IDataSource.Query` and saving the payload
itself, can move back. Read the result through `GetRecentRuns` and the payload's `schema-ref` and `schema-drift`
keys as for any other source.

## Rollback

Call the three-argument `RunIngestion` again. Results already written under a label stay where they are; nothing
renames them.

## Verification

`ToolUp.Platform.Tests` → "DataIngestor — payload carries its schema (Phase 832)" → "the caller label (Phase 1041)".
A statement longer than 255 characters ingests under a label with its run record, its completion event and a schema
object named by the label, and a second run detects drift. No label gives the pre-1041 payload id, schema id, run
`Table` and metadata. An unlabelled over-long statement is refused before `GetSchema` or `Query` is called. Invalid
labels are refused by type. A scheduled pull carries the label, and the unlabelled payload is byte-identical.

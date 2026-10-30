# A fact table and a subject describe themselves: `describeFactTables` and `describeSubjects`

**Ships in:** ToolUp.Platform.Core, ToolUp.Platform.Server and ToolUp.Facts.Shared 0.38.0.

**Affected:** nobody has to change anything to keep working. A module that declares fact tables adds one call to say
what they are for, and a module that declares subjects adds one to say what they mean. Code that builds a `FactTableDetail` or `FactTableColumnView` as a full record literal (usually a
test fixture) adds `Description = None`. Code that builds a `ServerModule` or `ServerApp` as a full record literal (very
rare: use `create` / `empty`) adds `SubjectDescriptions = []` / `RegisteredSubjectDescriptions = []`.

## What changes

1. **The record.** `FactTableDescription = { TableId; Description; Columns }` in `ToolUp.Platform.Core`
   (`ToolUp.Platform.Grounding`): what a declared table is for and how its rows are derived, plus an optional
   `(metric id, description)` per column. It is a record of its own, keyed by table id, rather than a field on
   `FactTableDefinition`. So every existing declaration, including a generated composition's `FactTables.fs`, compiles
   and behaves as before. A description is prose about the table and never a figure. A number belongs in a fact.
2. **The registration.** `ServerModule.describeFactTables` sits beside `declareFactTables` and adds descriptions.
   Compose refuses one that names a table no module declares, or a column its table does not carry, and the message
   names the module, the table and the column. It also refuses a table or a column described twice.
   `ServerApp.factTableRegistry` is the registry `run` registers, and the place the refusal happens.
3. **The read.** The composed `IFactTableRegistry` also implements the optional capability `IDescribesFactTables`.
   `FactTableRegistry.tryDescription registry tableId` probes for it, and a registry without it has no descriptions.
   This is the registry the browse reads and the model's fact tools resolve (`FactBrowseHandler.factBrowseDeps`).
4. **The browse.** `FactTableDetail.Description` and `FactTableColumnView.Description` (both `string option`) carry
   the description. The table page shows it under the table's title, and each column's description as that column's
   hint. An undescribed table reads `None` and renders as before.

5. **Subjects.** `SubjectDescription = { SubjectId; Description; Levels }` in `ToolUp.Platform.Core`
   (`ToolUp.Platform.Grounding`) is the same shape for a subject hierarchy: what it is and how its levels nest, plus an
   optional `(level, description)` per level. `ServerModule.describeSubjects` registers it beside `declareSubjects`.
   `ServerApp.metricRegistry` is the registry `run` registers, and the place the refusal happens: a description of a
   subject no module declares, or of a level its subject does not carry, stops the composition, naming the module, the
   subject and the level; so does a subject or a level described twice. `IMetricRegistry` is not widened. The composed
   registry also implements the optional capability `IDescribesSubjects`, and `MetricRegistry.trySubjectDescription
   registry subjectId` probes for it: a registry that predates it has no descriptions.
6. **The coverage tool.** `list_metric_coverage` adds `description` and `levelDescriptions` (described levels only,
   in declaration order) to a hierarchy that has a description. A hierarchy without one serialises exactly as before:
   the fields are absent, not null.

## What to do

Add one call beside the declaration. Describe the columns that need it, and leave out the rest:

```fsharp skip=fragment
ServerModule.create "trade"
|> ServerModule.declareFactTables [ countryTable ]
|> ServerModule.describeFactTables [
    {
        TableId = "trade.country"
        Description = "Daily trade by market and country, from the order header extract, excluding cancelled orders."
        Columns = [ "orders", "Orders placed, counted once per order header." ]
    }
]
```

Subjects follow the same shape:

```fsharp skip=fragment
ServerModule.create "trade"
|> ServerModule.declareSubjects [ geography ]
|> ServerModule.describeSubjects [
    {
        SubjectId = "geography"
        Description = "Where a sale is booked: each site sits in one market, each market in one country."
        Levels = [ "site", "A single trading outlet." ]
    }
]
```

A test fixture that builds `FactTableDetail` or `FactTableColumnView` in full adds `Description = None`.

## Verification

In the Platform pack, `a fact table describes itself` (under `Phase 895 — fact browse surface`) covers four things.
A composed description reaches `GetTable` through the registry the request resolves. An undescribed table reads `None`.
A registry without the capability has no descriptions. Each refusal names what it refuses. In the Fable pack,
`fact browse pages` checks that the table page renders the description and the column hint, and renders neither for an
undescribed table.

In the Platform pack, `MetricRegistry (Phase 519)` covers the subject side: a description is read back by subject id, a
registry without the capability has none, and each refusal (an undeclared subject, an undeclared level, a subject or
level described twice) names the module, the subject and the level. `CoverageTool (Phase 705)` checks that
`list_metric_coverage` reports the description and the level descriptions in declaration order, and that an
undescribed hierarchy is byte-identical with and without another hierarchy being described.

## Rollback

Remove the `describeFactTables` and `describeSubjects` calls. The composition is then exactly what it was without it.

# A fact table describes itself: `ServerModule.describeFactTables`

**Ships in:** ToolUp.Platform.Core, ToolUp.Platform.Server and ToolUp.Facts.Shared 0.38.0.

**Affected:** nobody has to change anything to keep working. A module that declares fact tables adds one call to say
what they are for. Code that builds a `FactTableDetail` or `FactTableColumnView` as a full record literal (usually a
test fixture) adds `Description = None`.

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

A test fixture that builds `FactTableDetail` or `FactTableColumnView` in full adds `Description = None`.

## Verification

In the Platform pack, `a fact table describes itself` (under `Phase 895 — fact browse surface`) covers four things.
A composed description reaches `GetTable` through the registry the request resolves. An undescribed table reads `None`.
A registry without the capability has no descriptions. Each refusal names what it refuses. In the Fable pack,
`fact browse pages` checks that the table page renders the description and the column hint, and renders neither for an
undescribed table.

## Rollback

Remove the `describeFactTables` call. The composition is then exactly what it was without it.

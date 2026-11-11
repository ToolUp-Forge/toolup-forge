# A data source binds what it is given: bound statements

**Ships in:** ToolUp.Platform.Core and ToolUp.Platform.Server 0.42.0. The change is breaking-class for one narrow
reason: `ConnectorCapabilities` gains two fields and `ConnectorCapability` two cases. Everything else is new surface.

**Affected:** a consumer that renders a value into a statement's text before calling `IDataSource.Query`; a full
`ConnectorCapabilities` record literal (construct from `ConnectorCapabilities.none` with `with` instead); an exhaustive
`match` over `ConnectorCapability`. A connector that declares nothing new is unchanged, and so is every call to
`IDataSource.Query`.

## What changes

1. **The shared types** (`ToolUp.Platform.Core`, Fable-safe): `BoundValue`, a closed union over the dataset model's
   scalar kinds with `Null of DatasetDType` for an absent value of a declared kind; `BoundParameter` (`Name`, `Value`);
   `BoundStatement` (`Text`, `Parameters`, `Timeout`); and `BindingRefusal` (`InvalidName`, `DuplicateName`,
   `UnusedParameter`, `MissingParameter`, `UnbindableKind`). A statement names a parameter as `@name`; a connector
   rewrites the marker to its backend's own form.
2. **The capability** (`ToolUp.Platform.Server`): `IBindsStatements` (`BindableKinds`, `QueryBound`) and its native
   twin `IEmitsNativeBoundPayload`, declared as `ConnectorCapabilities.Binds` / `NativeBound`. A bound statement's bytes
   are in the connector's declared payload format, and its failures are `BoundStatementError` (`Refused` with a
   `BindingRefusal`, `Failed` with the usual `IngestionError`).
3. **One entry point, `BoundQuery.run` / `runNative`.** A connector that does not declare the capability is refused
   by name (`BoundStatementError.NotDeclared` carrying its `Kind`). Nothing falls back to rendering a value into text.
   A parameter name, a repeated name and a kind outside `BindableKinds` are refused before the connector is called.
4. **`InMemoryDataSource` is the reference binder** (`select @a, @b` returns one row of the bound values; `table @t`
   reads the table the value of `t` names).

## What to do

Move each value out of the text and into a parameter:

```diff
- let! result = source.Query(ctx, $"... where region = '{escape region}' and year = {year}")
+ let statement =
+     BoundStatement.ofText "... where region = @region and year = @year"
+     |> BoundStatement.bind "region" (BoundValue.Text region)
+     |> BoundStatement.bind "year" (BoundValue.Int(int64 year))
+ let! result = BoundQuery.run source ctx statement
```

`result` is `Result<byte[], BoundStatementError>`; `BoundStatementError.describe` renders it. A value that may be
absent binds as `BoundValue.Null DatasetDType.Text`, never as an empty string. Delete the escaping helper the old call
needed. An identifier (a table or column name) is not a value, and no backend binds one: keep it behind your own
identifier check.

A connector that binds declares it:

```fsharp skip=fragment
member this.Capabilities = { ConnectorCapabilities.none with Binds = Some(this :> IBindsStatements) }
```

and refuses `UnusedParameter` / `MissingParameter` from its own reading of the text, because only its dialect knows
where a marker can stand. A decorator forwards the record, so a wrapped binder still binds.

## Verify

Build. A binder runs the binding laws: `IDataSourceContract.tests` includes them in the reference dialect when the
connector declares `Binds`, and `IDataSourceContract.testsWith name harness factory` (or `bindingLaws`) runs them in
the connector's own dialect through a `BindingHarness` (`Select`, `Lookup`, `Row`, `Read`). The laws: every declared
kind round-trips through a select of the parameter; a value holding a quote, a comment marker, a statement separator
or a NUL comes back as data and changes nothing; a repeated, an unused and an unsupplied parameter are refused; an
absent value binds as a typed null; an undeclared kind is refused before the connector runs; an undeclared capability
is refused naming the connector.

## Rollback

Return to the text form above; nothing persisted changes.

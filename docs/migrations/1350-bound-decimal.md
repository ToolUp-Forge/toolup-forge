# Migration — a bound statement carries an exact decimal (`BoundValue.Decimal`)

**What changes.** `BoundValue`, the value a bound statement's parameter carries
([915](915-bound-statements.md)), gains `Decimal of digits: string`: an exact decimal as **canonical
decimal text**, the same form a `DatasetValue.Decimal` cell holds
([921](921-exact-decimal-in-typed-rows.md)). A value read from a `DECIMAL` / `NUMERIC` column can now
be bound back without passing through a `Float`, which rounds past 15 to 17 significant digits, or
through `Text`, which leaves the backend to parse it by its own rule.

- `BoundValue.kind` maps the case to `DatasetDType.Decimal`, and `BoundValue.kinds` ends with
  `DatasetDType.Decimal` (declaration order).
- `BindingRefusal` gains `NonCanonicalDecimal of name: string`. `BoundStatement.check`, which
  `BoundQuery.run` and `BoundQuery.runNative` apply before any connector is called, refuses a
  `BoundValue.Decimal` whose text is not canonical (`DatasetDecimal.isCanonical`), naming the
  parameter. The refusal does not carry the text, so a log line never echoes a bound value.
- `InMemoryDataSource` binds the kind and writes the digits exactly. A connector declares the kind
  in `IBindsStatements.BindableKinds` when it can bind it exactly; one that does not is refused
  with `UnbindableKind(name, DatasetDType.Decimal)` before it runs, as for any other kind.
- `BoundValue.Null DatasetDType.Decimal`, the absent decimal, is unchanged. A connector whose
  `BindableKinds` is `Set.ofList BoundValue.kinds` now admits it, because the kind is now in that list.

**It is breaking.** `BoundValue` and `BindingRefusal` are closed unions, so every exhaustive `match`
over either becomes incomplete. Under the SDK's `FS0025`-as-error setting that is a compile error
naming the site.

## The fix

Over `BoundValue`, add one line beside the other text-shaped cases, and bind the digits with your
backend's exact decimal type, never by way of a float:

```fsharp
| BoundValue.Decimal digits -> (* bind `digits` as the backend's exact decimal *)
```

Over `BindingRefusal`, render the new refusal:

```fsharp
| BindingRefusal.NonCanonicalDecimal name -> $"parameter '{name}' is not canonical decimal text"
```

To bind text you hold that may not be canonical (`12.50`, `+3`), canonicalise it first:

```fsharp
match DatasetDecimal.tryParse text with
| Some digits -> statement |> BoundStatement.bind "amount" (BoundValue.Decimal digits)
| None -> (* not a plain decimal: refuse it at your own boundary *)
```

## The census

The case reported **two exhaustive matches over `BoundValue`** across forge: `BoundValue.kind`
(`ToolUp.Platform.Core`) and `InMemoryBinding.render` (`ToolUp.Platform.Server`), and **one over
`BindingRefusal`**: `BindingRefusal.describe`. All three are updated in this change.

## Verifying

The `IDataSource` contract pack's binding laws gain two: a 38-digit decimal (wider than a float or
`System.Decimal` holds) binds and reads back digit for digit, while the same value bound as a
`Float` does not, or the connector refuses the kind as `UnbindableKind`; and a non-canonical
decimal is refused as `NonCanonicalDecimal` without the connector being called. A connector that
runs `IDataSourceContract.tests` or `testsWith` picks both up with no change; a harness of its own
reads a decimal cell with `DatasetDecimal.tryParse` (`BindingHarness.invariantRead` does).

## Rollback

Revert the change. No stored data changes shape: a bound value is never persisted.

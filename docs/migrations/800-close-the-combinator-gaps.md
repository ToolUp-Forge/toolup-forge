# Phase 800 — Close the two combinator gaps: tuples and several-field union cases

**Applies to:** any deployment that opts wire types onto the closed decoder
algebra (`RemotingDecoders.register`, or the `ToolUp.Remoting.Generator`
emission), and any repository that reads a `GenerationPlan` directly.
**Breaking:** no for the algebra and the wire — every existing combinator is
byte-for-byte unchanged and nothing about what the writer emits moved. One
**planning-model** type on `ToolUp.Remoting.Generator` changed shape (below);
a consumer that only runs the generator is unaffected.
**Action required:** none to keep working. One optional regeneration.

## What changes

Phase 69k's expressibility census over `ToolUp.Platform.Core` found 13 of the
platform's API records blocked by exactly two gaps in `ToolUp.Remoting.Decode`:
there was no tuple combinator, and a union case carrying more than one field
had no form the generator could emit. Both are closed, in the algebra and in the
proved model beside it.

- **`Decode.tuple2` / `tuple3` / `tuple4`.** `Write.writeTuple` emits a tuple
  exactly as it emits a record — an array of the elements, positionally — so a
  tuple decoder is an arity check followed by `index` reads. An array of the
  wrong width is a named refusal (`a tuple of 2 element(s)` / `an array of 3
  element(s)`), never a slice; a refusal beneath an element carries `[n]`.
- **`Decode.fields n`.** The several-field `UnionCase` form over the inner
  array the writer emits for a case with more than one field. It is a
  combinator the case **author** chooses — a single-field case whose field is
  itself an array puts identical bytes on the wire, so only the case's own
  arity can tell them apart. `payload` is unchanged and still admits a pipeline
  over the inner array; `fields` adds the arity check that refuses a case of
  the wrong width, and it is what the generator now emits for every case with
  more than one field.
- **The generator** plans a reference tuple of arity ≤ 4 as one `tupleN` over
  its element decoders (registered under the tuple type itself), refuses a
  wider tuple and a struct tuple by name, and plans a several-field case as
  `fields n` over the record-shaped pipeline in the case's declared field
  order — the same order `Write.writeUnion` writes.
- **The proved model** (`proofs/RemotingDecode.fst`) carries the same
  definitions, three new characterisation lemmas, and a widened reference
  vocabulary whose round trip covers a pair and a several-field case. The
  oracle is re-extracted and byte-diffed; the differential host pairs the
  corpus's tuple fixtures and pairs `Outcome` a second time through `fields`.

## What moved off the reflection path

Twelve platform API records are now wholly expressible and can be registered
on the algebra path: `IDataSubjectRequestApi`, `IConfigApi`, `IFeatureFlagApi`,
`IModuleQueryBusApi`, `IPlatformTenantApi`, `IProvenanceQueryApi`,
`IProviderProfileApi`, `ITeamInviteApi`, `IUserSchemaApi`, `IWebhookApi`,
`JobApi`, `ModelExecutionApi`. Nothing registers them for you — the algebra is
opt-in, as it was — so a deployment that has not opted a record in sees no
change at all.

Two more stay on the reflection path (the census had grown to 38 records and
14 blocked by the time this shipped; 69k's 36 / 13 were its own day's count), for reasons neither gap
covered and which this phase does not own: `FileManagementApi` holds an
`obj`-typed field, and `IConversionApi` returns `ColumnExpr`, a *recursive*
union (its several-field cases plan now; the cycle is what the generator
refuses). The census test pins both by name.

## The one surface change, and the diff

`UnionCasePlan.Payload` on `ToolUp.Remoting.Generator` was `string option`
(`None` = no fields, `Some decoder` = one field) and could not say "several".
It is now a closed union:

```fsharp skip=fragment
// before
match c.Payload with
| None -> …                 // Decode.case0
| Some decoder -> …         // Decode.payload decoder

// after
match c.Payload with
| CasePayload.NoFields -> …               // Decode.case0
| CasePayload.OneField decoder -> …       // Decode.payload decoder
| CasePayload.SeveralFields fields -> …   // Decode.fields (List.length fields) (pipeline over fields)
```

Only code that pattern-matches a plan is affected. The CLI, the MSBuild
targets and every emitted file are consumed unchanged.

## Regenerating

A repository that commits generated decoders (the AOT sample does) will see
them gain arms on the next build: a `Decode.fields` arm per several-field case,
and a `Decode.tupleN` registration per tuple root. If it also records the
generator's refusal set by name, remove the tuple and several-field entries —
that list is exactly where this phase said its arrival would show.

## Verification

- `dotnet run --project Build.fsproj -- VerifyAll` — the `Phase 785`, `Phase
  787` and `Phase 69k` lists in `ToolUp.Platform.Tests` carry the new cases,
  including 69k's inverted gap test.
- `pwsh ./proofs/check.ps1` — the model checks with no admits and the
  extraction is byte-identical to the committed oracle.
- `dotnet run --project samples/HelloWorld-AOT/HelloWorld.AOT` — 64 of 71
  fixtures decode through generated decoders; the 7 refusals are all
  `DateOnly` / `TimeOnly`.

## Rollback

Revert the phase's commits. No wire bytes, no baseline of any consumer and no
registered decoder outside the sample changed, so a revert restores the prior
tree exactly.

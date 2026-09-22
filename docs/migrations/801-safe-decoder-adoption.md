# Phase 801 — Safe decoder adoption and the served-set coverage facet

**Applies to:** every deployment (the platform's own remoting decoders changed); any repository
that registers generated decoders; any composition root that reads `RemotingDecoderFacet`.
**Breaking:** no wire change — no byte on either remoting wire moved. Additive surface on
`ToolUp.Platform.Core` (`RemotingDecoders.verify` / `registerVerified`, the `DecoderShapes` /
`DecoderRefusal` types) and `ToolUp.Platform.Server` (`ServedApiRecords`,
`RemotingDecoderFacet.inspectServed` / `inspectServedPlatform` / `coverage` / `describe`); generated
modules gain `verifyAll` / `registerAllVerified`.
**Action required:** none to keep working. A root under `CompositionProfile.Verified` should read
the second section.

## What changes

1. **A generated decoder is verified before it is registered.** `RemotingDecoders.verify<'T>
   draws seed decoder` draws random values of `'T` by reflection, writes each with the platform's
   serializer, and refuses on the first draw where the candidate's value differs from the
   reflection reader's — the defect class the algebra's totality cannot see (two same-typed fields
   swapped, a case index off by one). `registerVerified<'T>` is the one-call form. Both are
   `.NET`-only (`#if !FABLE_COMPILER`): the Fable client has no reflection reader to compare
   against and registers what the .NET side verified.
2. **Generated modules carry the gate.** `ToolUp.Remoting.Generator` now emits `verifyAll draws
   seed : Result<DecoderVerification, DecoderRefusal> list` and `registerAllVerified ()` beside
   `registerAll`, under the same `#if !FABLE_COMPILER`.
3. **`PlatformDecoders` is the generator's emission over all 38 platform API records**, replacing
   the two hand-written records of Phase 785. A contract test (`Phase 801` in
   `ToolUp.Platform.Tests`) regenerates the file in memory, fails on the first differing byte, and
   runs `verifyAll` over every registration (313 wire types, 64 draws each). The file is excluded
   from Fantomas (`.fantomasignore`) because the emitter's layout is the contract.
4. **The facet enumerates what the composition SERVES.** `Api.make` — the one mount point — records
   its record type in `ServedApiRecords`; `RemotingDecoderFacet.inspectServed` classifies that set
   by each record's own method return types, so an undeclared record is a `Reflection` line rather
   than an absence. `coverage` is the ratio; `ServerApp.run` registers the platform decoders (the
   same set the client registers) and logs one line:
   `remoting decoders: N of M served API record(s) decode through the closed algebra (…)`. The
   deployment verification report derives its decoder section from the served set when the root
   supplies none, and its trust-boundary narrowing says "served" rather than "declared".

## If you run `CompositionProfile.Verified`

The refusal now fires on a **served** record with no algebra decoder. A root that mounts its own
API records and never registered decoders for them was previously admitted (the facet only saw
declared records); it is refused now, naming the record. Either register a decoder — generate one
with `ToolUp.Remoting.Generator` and `registerAllVerified ()` it — or run `Standard`, where the facet
is advisory and the boot line reports the ratio.

## Regenerating `PlatformDecoders.fs`

When a platform wire type or API record changes, the contract test fails and prints the recipe:

```powershell
$env:TOOLUP_REGEN_PLATFORM_DECODERS = "1"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 801"
$env:TOOLUP_REGEN_PLATFORM_DECODERS = $null
```

Rebuild `ToolUp.Platform.Core` and re-run the pack without the variable; commit the file with the
type change.

## Verification

- `Phase 801` in `ToolUp.Platform.Tests`: the committed file equals the emission; every
  registration verifies; `registerAllVerified` registers `covered`; a decoder with two fields
  swapped is refused by name and draw; an undrawable type (`obj`) is `DecoderUndrawable`.
- `Phase 785 — the composition-profile facet`: `Api.make` records the mount; coverage reads `1 of
  2` then `2 of 2` when the record is opted in; the verified profile refuses naming the served
  record.
- `Phase 69k`: every expressible API record is declared to the facet (the coverage gap 69k measured
  is closed — that case is now the equality, and goes red if a record is ever undeclared again).

## Rollback

Revert the phase's commits. No wire bytes changed; the hand-written `PlatformDecoders` returns
with the revert, and a root that registered decoders through `registerVerified` registers them
through `register`.

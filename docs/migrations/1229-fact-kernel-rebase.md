# Phase 1229 — the Fact kernel is the published `Fuaran.Facts` package

**Class: breaking.** Forge's own Fact kernel types are removed. Forge now uses the published, Apache-2.0
[`Fuaran.Facts`](https://www.nuget.org/packages/Fuaran.Facts) package (0.2.1, the .NET reference implementation of
the [Fact wire specification](https://github.com/Fuaran-Data/fuaran-fact-specification)) directly: no aliases, no
forwarders. **Stored data does not change**: every fact id, lineage key and lineage digest is byte for byte what
forge minted, and stored facts are read as they stand.

## Namespace map

| Old (forge) | New |
|---|---|
| `ToolUp.Facts.{Fact, FactValue, MethodRef, Disclosure, SubjectRef, MetricRef, TemporalExtent, Evidence, Confidence}` | `Fuaran.Facts.*` (same names) |
| `ToolUp.Facts.Fact.compute / computeFor / effectiveInputHashes / methodIdentity / valueHash / isForecast` | `Fuaran.Facts.Fact.*` |
| `Fact.lineageKey s m p meth` | `Fact.lineageKey fact`; from parts: `ToolUp.Facts.FactLineage.keyOf s m p meth` |
| `Sha256.hexOfUtf8 (Fact.lineageKey …)` | `Fact.lineageDigest fact` / `FactLineage.digestOf …` (same bytes) |
| `SubjectRef.toString s` | `Fact.subjectRendering s` (same text) |
| `ToolUp.Facts.{InputHash, Freshness, FactFreshness, CurrencySymbols}` | `Fuaran.Facts.*` (`Freshness` and `FactFreshness.Stale` take an `Instant`) |
| `ToolUp.Facts.FactRendering.render fmt v` / `renderFor` | `Fuaran.Facts.FactRendering.renderFor (metric option) v` |
| `ToolUp.Platform.Grounding.{MetricDefinition, SubjectDefinition, DirectionOfBetter, StalenessPolicy, RecomputePolicy, RollUp, CanonicalMethod}` | `Fuaran.Facts.*` |
| `ToolUp.Facts.Disclosure.{toString, tryParse, floor}`, `DeclaredMethod` | unchanged (forge's own) |

Kept in `ToolUp.Platform.Grounding`: the registry (`IMetricRegistry`, `MetricRegistry`, registrations and subject
descriptions), `RecomputePolicy.resolve` and `RollUp.additiveTolerance` (now `DecimalText option`). New in
`ToolUp.Facts`: `FactLineage` and `FactStoreCodec.read`.

## What a call site changes

- A quantity is a `DecimalText` (`DecimalText.ofDecimal 1.50m`; its scale is significant), a time an `Instant`
  (`Instant.ofDateTime`, `Instant.toDateTime`). A series is `Series(HostKey "objects/x@v3", None)` or a
  `ContentAddress`. `Fact`, `FactValue` and `MetricDefinition` are not comparable: sort by a key.
- A metric definition's `Unit` is a `Fuaran.Core.UnitOfMeasure`, its `DisplayFormat` a typed record, its staleness
  window whole seconds (`FreshFor 3600`), and it carries `RecomputePolicy`. Convert a stored string with
  `Legacy.unitOf "GBP"` and `Legacy.displayFormatOf "C0"`. The registry now refuses a definition the kernel's
  `Definition.checkMetric` refuses (a currency format over a unit that is not one currency: write `N0`).
- **Rendering.** The package's rendering is the standard (specification §10, rule 1, `FactRendering.Rule`) and
  forge's own renderer is removed. A negative currency shows `-£1,235` (was `(£1,235)`), a percent `12.3%` (was
  `12.3 %`), and a currency format never shows `¤`. A narrative issued under forge's earlier rendering carries no
  rule number: it is pre-standard. Nothing re-renders it, its certificate still verifies (it signs ids and text, not
  a rendering), and the numeric gate re-verifies its figures by value, not by text. A deployment that wants every
  displayed figure in the standard re-issues the narrative; `FactRendering.renderUnder` refuses any rule but 1.
- Units shown to a person or a model are canonical unit text (`[GBP]`, `1`).

## Stored data

A store writes the specification's encoding (`FactWire.writeFact`) and reads with `FactStoreCodec.read`, which
also reads the legacy saved shape, verifying every id. Existing blobs and `payload` rows are never rewritten; a fact
is converted when the store next writes it. Fact-table documents that embed a period or value keep their stored
shape.

**Tokens the specification refuses (its §3.5).** A new fact may not carry `>` or `|` in a path member, an empty path
member, `/` or `|` in a hierarchy, `|` in a metric id, `:` in a computed method's operation or version, or `=` or `;`
in a distribution key: an assert or batch carrying one is refused as a malformed draft. A fact stored before these
rules reads as stored, with its stored id and lineage key. Such a fact can share an id or a lineage key with a clean
fact (`["a>b"]` and `["a";"b"]` render alike), so before the store treats a stored fact as the one a write names (a
replay, a supersession, the head of a lineage) it compares them: a write that a stored fact matches only by
rendering is refused, naming the stored fact, and nothing is merged, skipped or superseded. A batch whose every
draft is already stored is answered from the store's census without reading a fact, so a batch replay of such a
draft is taken as already stored. To clear an ambiguity, assert the stored facts again under admitted tokens: those
are new facts in new lineages. Moving a blob store that holds such a fact into the Postgres store
(`FactStoreMigration.migrate`) writes each fact, and so refuses that one: re-assert it first.

**Ids written before 0.38.0.** A fact stored under an id minted before the canonical frame faults on every ordinary
read: those ids are migrated, not dual-read, and the only read that does not verify an id is the id migration's own
export. **Before upgrading**, a deployment that has not yet run the id migration runs it on the release it is on
(`BlobFactStore.MigrateIds` / `FactStoreMigration.migrateIds`, [`1089`](1089-redesign-h-sha256-hex-and-canonicalframe.md)),
or runs it first thing after upgrading, before any other read. A fact under such an id that also carries a refused
token cannot be re-keyed (the re-keyed fact would have to be written, and is refused); re-assert it under admitted
tokens.

## The package reference

```xml
<!-- Directory.Packages.props -->
<FuaranFactsVersion>0.2.1</FuaranFactsVersion>
<PackageVersion Include="Fuaran.Facts" Version="$(FuaranFactsVersion)" />
<!-- a project that names the kernel types directly -->
<PackageReference Include="Fuaran.Facts" />
```

It arrives transitively through `ToolUp.Platform.Core` and `ToolUp.Facts.Core`.

## Verify

1. `dotnet build` the consumer; add `open Fuaran.Facts` (before `open ToolUp.Facts`) where it names kernel types.
2. Run the id migration if the deployment has not (above), then read a scope through the store and compare the
   fact ids before and after: they are equal.
3. The forge suite's `Fact kernel — a fact forge stored keeps its id and its lineage` and
   `Fact wire specification — forge's fact store and identity` lists pass.

## Rollback

Pin the previous SDK version. Facts written by this version are in the specification's encoding, which an earlier
version cannot read: roll back only a deployment that has written no fact since upgrading, or restore its fact
store from a backup taken before the upgrade.

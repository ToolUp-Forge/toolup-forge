# Migration — Phase 1411: the Fuaran pins move to Core 0.45.0, Compute 0.46.0, UI 0.106.0 and DB 0.5.0

**Status:** breaking for a consumer, through the dependency floors. Forge's own public surface only grows (three
members on `PipelineProfile`), but its packages now floor four Fuaran families at a new `0.x` minor, and those
minors break.

## The pins

| Property in `Directory.Packages.props` | Packages | From | To |
|---|---|---|---|
| `FuaranCoreVersion` | `Fuaran.Core.*` | 0.42.0 | 0.45.0 |
| `FuaranComputeVersion` | `Fuaran.Compute.*` | 0.44.0 | 0.46.0 |
| `FuaranUIVersion` | `Fuaran.UI`, `Fuaran.UI.Charts` and the rest of that release | 0.104.0 | 0.106.0 |
| `FuaranDbVersion` | `Fuaran.DB` | 0.2.0 | 0.5.0 |

`FuaranFactsVersion` stays at `Fuaran.Facts` 0.2.1, which floors Core 0.40.3 and runs on 0.45.0. The set is the
one `Fuaran.UI` 0.106.0 is built on, and `Fuaran.DB` 0.5.0 is built on the same Core and Compute, so the restore
graph holds one Core and one Compute version. `Fuaran.MVU` 0.1.1 restores beside it.

## What a consumer does

1. **Raise its own Fuaran pins to the same set.** A consumer that pins any of these families below the table's
   "To" column resolves to forge's floors anyway, and a package built on the older line can fail at run time.
   `Fuaran.DB` 0.2.0 to 0.4.0 fail on Core 0.45.0 the first time a dataset model is checked (a
   `MissingMethodException` inside the type initialiser of `Fuaran.DB.ModelRules`).
2. **A match over `Transform` gains two cases**, `KMeans` and `Bootstrap` (Compute 0.45.0). Forge does not offer
   either step yet: a pipeline carrying one is refused by its wire spelling (`kmeans`, `bootstrap`) as
   `PipelineRefusal.OutsideProfile`, with a pipeline profile registered or without one, and a profile naming one
   is refused at registration as `PipelineProfileRefusal.UnknownStep`. `PipelineProfile.notOfferedSteps` lists
   them.
3. **A match over `Fuaran.DB.PipelineRefusal` gains `UnresolvedJoinKey` and `StepNotAdmitted`** (DB 0.4.0 and
   0.5.0). A view declared over a dataset model that carries either step is refused at registration as
   `ViewRefusal.ModelCheck` with the code `StepNotAdmitted`.
4. **`SelectOption.Label` is a `TextSource`** (UI 0.105.0): wrap a string label in `literal`.
5. **Name `Grant` in full** where `Fuaran.Core` and `Fuaran.DB` are both open, since both define one.

Read the producers' `STABILITY.md` entries for the full list: Core 0.43.0 to 0.45.0, Compute 0.45.0 and 0.46.0, UI
0.105.0 and 0.106.0, DB 0.3.0 to 0.5.0. `Fuaran.DB` 0.4.0 also changes behaviour: a floored grouping can return
more groups than 0.3.0 did.

## Verification

- `dotnet build` of the consumer's solution, with warnings as errors, shows no incomplete match.
- `dotnet run --project Build.fsproj -- VerifyFuaranCoResolution` passes: every floor is aligned with the
  published `Fuaran.UI` 0.106.0 (`fuaran-ui-floors.json`).
- The consumer's restore graph lists one version of each `Fuaran.Core.*` package.

## Rollback

Pin the previous ToolUp release, and with it the previous Fuaran set (Core 0.42.0, Compute 0.44.0, UI 0.104.0,
DB 0.2.0). Raising the Fuaran pins alone while staying on a ToolUp release built on the older set is not supported.

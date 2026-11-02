# The build census: what each project costs to build

**Ships in:** `ToolUp.Platform.Build` 0.39.0 (additive: the `Census` module and the `ProjectCost`, `CensusEntry` and
`BuildCensus` records), the root driver's `Census` target, and the committed `build-census.json`. No existing member
changes and no consumer action is needed.

**Affected:** anyone planning a project split, sizing a gate's build budget, or quoting how long the solution takes to
build. The figure now lives in one generated file, never in prose.

## What it is

`dotnet run --project Build.fsproj -- Census` rebuilds `ToolUp.Forge.sln` once from scratch and writes
`build-census.json`. For every project in the solution, the file records the project's own build seconds, its
evaluation seconds, the lines in its `Compile` items, its fan-in (solution projects that reference it) and its fan-out
(solution projects it references). It also records the tree SHA, the machine (OS, architecture and processor count,
never the host name), the UTC time, the wall clock of the build and the command. Rows are sorted by build seconds.

`Census --check` builds nothing. It is red when the file's project set differs from the solution's, and it names each
project added or removed. The `ToolUp.Platform.Build.Tests` pack runs the same check over the committed file, so a
project added to the solution without a regeneration reddens `VerifyAll`. Timings are never compared: they differ from
machine to machine, and a project added or removed is what makes the file stale.

## The figure

The committed census was measured on a 12-processor Windows Arm64 machine at tree `6cdf75f2` on 2026-11-01. Other
sessions were building on the same machine at the time. The clean graph build took **890 s** of wall clock, and the
per-project seconds sum to 9,629 s. Read both from the file rather than from this page: `wallSeconds` and
`totalCompileSeconds` are the names a gate manifest's build row reads. Until that manifest exists, `CLAUDE.md`'s "Fast
iteration" paragraph cites the file instead of restating a number.

**The figures are one machine's, and they are noisy.** An earlier graph build of the same tree on the same machine,
the same evening, took 475 s. Between the two runs, `ToolUp.Platform.Server` went from 93 s to 146 s and
`ToolUp.Cloud.Aws` from 61 s to 181 s, while `ToolUp.Platform.Core` stayed between 31 s and 36 s. Projects build in parallel, so a
project's seconds include contention with the projects beside it in the graph as well as with other load on the
machine. Projects that build early, before the graph widens, move least: Core is one. Compare projects within one file, prefer
a ranking to a ratio, and regenerate before you rely on a small difference. A serial build (`-m:1`) would remove the
contention between projects, but it would take most of an hour, so the census does not use one.

## Why a graph build's performance summary, not a binary log

Two readers were open: MSBuild's structured binary-log reader, or the `-clp:PerformanceSummary` text as a fallback.
The census reads the text. The structured reader is a package dependency, and `ToolUp.Platform.Build` is a published
package whose dependencies every consumer's build driver restores. The text needs no dependency. It is enough, but
only under `-graph`, which is why `Census.buildArgs` is the one command the reader is valid for:

- **In a plain solution build the text cannot give a per-project cost.** A project's row in the `Project Performance
  Summary` covers every second between its start and finish, including the time it spent blocked while a reference
  built inside its `ResolveProjectReferences`. Measured on this tree with `--no-incremental`, the median project row was
  211 s and the largest 545 s, in a build whose wall clock was 398 s. Leaf projects read almost as long as the whole
  build.
- **Under `-graph` the same text is per-project.** MSBuild starts a project only after every project it references has
  finished, so no project waits on a reference. In the same tree's graph build, the median row was 16 s and the
  largest 114 s, against 475 s of wall clock. The `Fsc` task's total, 3,107 s, falls below the sum of the project rows,
  4,815 s, as it must if the rows are each project's own work. The falsifier is the plain build's figures: had the
  graph rows still included waiting, leaves would again read close to the wall clock.

The diagnostic-verbosity log does hold per-target timestamps, but a replay of one build's binary log at that verbosity
wrote 2.7 GB of text. The census does not parse that.

## Reproducing

```powershell
dotnet run --project Build.fsproj -- Census          # rebuild and rewrite build-census.json
dotnet run --project Build.fsproj -- Census --check  # is the committed file's project set current?
```

The summary log is written to the system temp directory (`toolup-census/performance-summary.log`), outside the
repository. Commit the regenerated `build-census.json`.

## Rollback

Delete `build-census.json`, the `Census` target in `Build.fs`, `BuildCensusTests.fs` and its registration. Nothing else
reads them yet.

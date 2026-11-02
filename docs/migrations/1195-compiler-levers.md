# Compiler levers: reference assemblies held on, parallel compilation left off

**Ships in:** the repository's own build (`Directory.Build.props`, the `ToolUp.Platform.Build.Tests` pack). No
package surface moves and no consumer action is needed.

**Affected:** contributors and automated workers who build the solution. A build produces the same output as before:
`ProduceReferenceAssembly` was already on by SDK default, and `ParallelCompilation` stays unset.

## What was decided

| Setting | Decision | Why |
|---|---|---|
| `ProduceReferenceAssembly` | **on**, declared in `Directory.Build.props` | Already the SDK default for an `.fsproj` targeting net7.0 or later. An internal edit to `ToolUp.Platform.Server` recompiles **2** projects with it and **129** without. The declaration holds it against a project, an import or a future SDK turning it off. |
| `ParallelCompilation` | **off**, declared nowhere | Measured on this machine it made both bottleneck projects **slower** (Server 48 s to 58 s, Core 30 s to 41 s, median compile-task time). The F# 10.0 compiler (15.2.400) also warns on every project that `--parallelcompilation` is "for test purposes only" (FS0075, 234 warnings per solution build). The warning is raised for `false` as well as `true`, so the setting is not declared at all. |

`src/ToolUp.Platform.Build.Tests/CompilerLeversTests.fs` holds both decisions. The pack is red when
`ProduceReferenceAssembly` is absent, false or behind a `Condition` in `Directory.Build.props`, when any `.fsproj`
turns it off, and when `ParallelCompilation` is declared anywhere. Each finding names this document.

## The measurement

**Machine and tree.** One Windows machine (12 logical cores), Debug, `MSBUILDDISABLENODEREUSE=1`, tree `394943a9`,
.NET SDK 10.0.400 (rolled forward from the 10.0.203 pin), F# compiler 15.2.400. **The machine was not idle.** CPU load
read 36–100 % before each run. Other sessions were building on it, and another session may have been running a gate.
Treat single figures as noisy. Every configuration was interleaved with the others so that contention fell on all of
them, and the medians are the figures to read.

**Configurations.**

- `base`: the tree as found. `ProduceReferenceAssembly` on by SDK default, `ParallelCompilation` unset.
- `pc`: `base` plus `-p:ParallelCompilation=true`.
- `noref`: `base` plus `-p:ProduceReferenceAssembly=false`. This is the counterfactual for the reference-assembly
  setting, because the tree already had it on.

**Clean builds.** `dotnet build ToolUp.Forge.sln --no-incremental`. All 234 F# projects compiled in every run. Rounds
were ordered base/pc/noref, then pc/noref/base, then noref/base/pc.

| Config | Run 1 | Run 2 | Run 3 | Median |
|---|---|---|---|---|
| `base` | 389.6 s | 329.1 s | 542.5 s | **389.6 s (6m30s)** |
| `pc` | 337.7 s | 1065.3 s | 337.5 s | **337.7 s** |
| `noref` | 492.5 s | 347.9 s | 318.7 s | **347.9 s** |

`base` and `noref` compile the same projects and differ only in the reference assembly write. Their medians are 42 s
apart, which shows the size of the noise. The `pc` median is inside that noise, so the clean builds do not settle
whether `pc` helps.

**Incremental builds.** Each configuration ran one warm build first, then three timed builds. The `noop` build had no
edit. The `srv` build edited a string literal inside a constructor body in `ToolUp.Platform.Server`
(`PrometheusMetricsSink.fs`). The `core` build edited a string literal inside a non-inline function in
`ToolUp.Platform.Core` (`ComponentId.ensureUnique`). Projects recompiled were counted from the build log, one per
compiler invocation.

| Config | `noop` | `srv` (internal edit) | `core` (internal edit) |
|---|---|---|---|
| `base` | 20.6 s, 0 projects | 90.1 s, **2** projects | 47.5 s, **2** projects |
| `pc` | 17.2 s, 0 projects | 78.7 s, 2 projects | 43.7 s, 2 projects |
| `noref` | 19.2 s, 0 projects | 202.9 s, **129** projects | 326.6 s, **193** projects |

With reference assemblies on, the two projects are the edited one and `ToolUp.AI.Evaluation`. That harness sets
`CompileUsingReferenceAssemblies=false` on purpose (see the known gap below), so it recompiles whenever Core or Server
changes.

**Parallel compilation, measured on the bottleneck projects alone.** The solution figures could not separate `pc`
from the noise, so the two projects the lever is meant to speed up were measured directly. Each run was
`dotnet build <project> --no-dependencies --no-incremental`, alternating `base` and `pc`, four rounds each. The
figure is the `Fsc` task time from `-clp:PerformanceSummary`.

| Project | `base` runs | `base` median | `pc` runs | `pc` median |
|---|---|---|---|---|
| `ToolUp.Platform.Server` (601 files) | 45.2, 149.7, 45.6, 50.7 s | **48.2 s** | 60.2, 61.3, 48.9, 56.3 s | **58.3 s** |
| `ToolUp.Platform.Core` (214 files) | 49.8, 29.9, 29.4, 25.5 s | **29.7 s** | 35.7, 27.0, 63.7, 45.4 s | **40.6 s** |

**Threshold and verdict.** A lever lands if the median gain is at least 5 % and it costs nothing. `ParallelCompilation`
fails both tests. On the projects it targets it was 21 % and 37 % slower on this machine. Graph-based checking needs
idle cores, and a busy build machine has none to give. The setting also adds one FS0075 warning per project. It is
left off.

**Falsifier for the re-measure.** On an idle machine, or on a CI runner, run the micro-benchmark above in both
directions. If `pc` beats `base` by 5 % or more on both projects, the decision can be revisited, but only on a compiler
that no longer marks the switch as test-only. Change this document and `CompilerLeversTests.fs` in the same commit.

## Reference assemblies and the gates

**The approval gate still reads implementation DLLs.** The Public-API approval gate renders each assembly from
`bin/<config>/net10.0/<name>.dll` and skips every `ref/` path, so reference assemblies never reach it. Both directions
were checked on this tree with reference assemblies on:

- The internal-only `srv` edit recompiled 2 projects. `Phase 175 — Public-API approval baseline` passed 230 of 230.
  `api-baselines/` was unchanged.
- A public addition to `ToolUp.Platform.Server` (a new public module with one function) recompiled 129 projects. The
  same list failed 1 of 230, `per-assembly surface.ToolUp.Platform.Server`, naming both added members and
  `api-baselines/ToolUp.Platform.Server.approved.txt`.

**Packing.** `ToolUp.Platform.Core` and `ToolUp.Platform.Server` were each packed twice in Release, with a forced
recompile of both projects between the two packs. Every file inside the packages was byte-identical: the DLLs, the
PDBs, the XML documentation, the nuspec and the `fable/` sources. The `.nupkg` and `.snupkg` containers were not byte-identical. NuGet
stamps each zip entry with the time of the pack. That comes from NuGet and is the same whether or not either setting
is on, so neither setting affects determinism.

**Known gap: a `[<Literal>]` does not move the reference assembly.** These probes were run on this tree:

| Edit in `ToolUp.Platform.Core` | Reference assembly changed? | Recompiled |
|---|---|---|
| change a `[<Literal>]` value (`ConfigKeys.PlatformModuleKey`) | **no** | Core and `AI.Evaluation` only |
| add a public `[<Literal>]` | **no** | Core and `AI.Evaluation` only |
| change the body of an `inline` function | yes | 193 projects |
| add a public member to `ToolUp.Platform.Server` | yes | 129 projects |

A literal's value is compiled into each caller. `ToolUp.Platform.Server` uses `PlatformModuleKey`, but it was not
recompiled when the value changed. An incremental build then leaves Server with the old value, and nothing reports it.
An added literal fails in the dependant instead, with FS0039 on a name that exists. Phase 514 met that failure and
fixed it locally with `CompileUsingReferenceAssemblies=false` on `ToolUp.AI.Evaluation`.

What this means for you:

- **After you change or add a `[<Literal>]`, build its dependants clean.** Run `dotnet build ToolUp.Forge.sln
  --no-incremental`, or delete the dependants' `obj/`.
- CI is not affected, because it builds from a fresh checkout. A local or worktree gate builds incrementally over its
  existing `obj/`. Such a gate can miss a changed literal, but only when no other edit has forced the dependants to
  recompile.
- Turning reference assemblies off would close the gap. It would also make every Core or Server edit recompile up to
  193 projects, which is 2.3 to 6.9 times slower on the figures above. That trade is the operator's to make. This phase
  holds the setting the tree already had.

## The build floor the lanes read

The selected-lane arithmetic in `docs/migrations/1121-affected-lane-v0.md` uses **4m18s**. That figure is the
solution build inside a full `verify.ps1` run on a quiet machine on 2026-10-25. Neither setting changed it. The tree
already had reference assemblies on, and parallel compilation stays off. The clean build's current wall clock and each
project's share of it are in `build-census.json`, which names the machine and tree that measured them. The figures to use are:

- **Fresh checkout or clean build.** 4m18s on a quiet machine is still the floor. On a busy machine, plan for 5½–6½
  minutes (medians from 330 to 390 s here).
- **A worker's own incremental build over a warm worktree.** This is the real floor for an `affected` run.
  - Internal edit to Server: about 1.5 minutes, 2 projects.
  - Internal edit to Core: under 1 minute, 2 projects.
  - Public change to Server: 129 projects. Public change to Core: 193 projects. Either costs close to a clean build.
  - No change: about 20 s.

Gate budget rows should use the incremental figures for a worker's pre-merge run and the clean figure for a fresh
landing checkout. No gate manifest is checked in yet. Its budget rows should start from these figures.

## Reproducing

Logs and scripts are kept outside the repository. The commands are:

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet build ToolUp.Forge.sln --nologo -v:n --no-incremental                          # clean
dotnet build ToolUp.Forge.sln --nologo -v:n                                           # incremental, after one edit
dotnet build src/ToolUp.Platform.Server/ToolUp.Platform.Server.fsproj --no-dependencies --no-incremental -clp:PerformanceSummary -p:ParallelCompilation=true
```

Count recompiled projects as the lines containing `fsc.dll" -o:` in a `-v:n` log. To check whether a reference
assembly moved, hash `obj/Debug/net10.0/ref/<name>.dll` before and after the build.

# A prose-only change has its own lane: `TOOLUP_TEST_LANE=prose`

**Ships in:** the repository's own gate (`TestLane.fs`, `GateManifest.fs`, `Build.fs`, `gate-reads.json`, the lane
arms of `TestLaneFilter.fs` and `PackEntry.fs`). No package surface moves and no consumer action is needed.

**Affected:** contributors and automated workers who change forge's documents, and the landing tooling that reads
`gate-reads.json`.

## What changed

`TOOLUP_TEST_LANE` accepts a sixth value, `prose`. Under it `verify.ps1` runs its own steps as in every lane, and
`VerifyAll` then refuses or runs:

1. **It refuses a change it does not cover.** It reads every path changed since the merge base with
   `TOOLUP_PROSE_BASE` (`origin/main` when unset), committed, staged, unstaged or untracked, and exits 2 before any
   row runs when a path is a .NET default input or sits under a `reads` entry the lane does not cover. Each refused
   path is printed with why.
2. **Otherwise it runs the rows that open prose.** `Build` and `FuaranCoResolution` run whole; `Platform` and
   `Platform.Remoting` run only the lists in `TestLane.Lists.proseNames`, through the same keep the `affected` lane
   uses.

The lane covers `GateManifest.proseCovers`: `docs/**`, `README.md`, `CHANGELOG.md`, `CLAUDE.md`, `CONTRIBUTING.md`,
`SECURITY.md` and `DRAFT-NOTES.md`. How to run it: [`docs/platform/testing-conventions.md`](../platform/testing-conventions.md#the-prose-lane-a-change-to-the-documents-in-minutes).

**It is not the citable gate yet.** It becomes the citable gate for a prose-only commit only when the landing tooling
admits a selected lane under a declared policy. Until then a ship cites `full`, and a prose commit landed while a gate
holds the machine still re-gates, because `docs/**` and the root documents stay in `reads`.

## What opens each prose path

Read from `GateManifest.fs` and the sources, not from the record. Costs are row medians from `gate-timings.json`
(19 green full-lane runs, 2026-11-10 to 2026-11-17); a list's own cost is measured below.

| Path in `reads` | Rows (and lists) that open it in the full lane | Row cost alone |
|---|---|---|
| `README.md` | the build step (packed beside every project); `Platform.Remoting` / `OpenCoreVocabularyNeutralityTests` | build; 78 s |
| `CONTRIBUTING.md`, `SECURITY.md` | `Platform.Remoting` / `OpenCoreVocabularyNeutralityTests` | 78 s |
| `CHANGELOG.md` | `Build` / `ChangelogTests` | 22 s |
| `CLAUDE.md` | `Build` / `GateManifestTests` (the generated CI table) | 22 s |
| `DRAFT-NOTES.md` | `Platform` / `BaselineMergeDriverTests` | 1080 s (whole pack) |
| `docs/**` | `Platform.Remoting` / `OpenCoreVocabularyNeutralityTests` (every file) | 78 s |
| `docs/migrations/**` | `Build` / `ChangelogTests`, `V1ReadinessTests`, `CompilerLeversTests` | 22 s |
| `docs/platform/open-core-boundary.md` | the `FuaranCoResolution` leg | 0.0 s |
| `docs/reference/audit-event-reference.md`, `docs/platform/events.md` | `Platform` / `AuditEventRegistryTests` | 1080 s (whole pack) |
| `docs/reference/config-reference.md`, `docs/reference/toolup.config.schema.json`, `docs/operations/env-vars.md` | `Platform` / `ConfigReferenceTests` | 1080 s (whole pack) |
| `docs/platform/trust-boundaries.md`, `docs/platform/remoting-decoder-algebra.md` | `Platform` / `TrustBoundaryTests` | 1080 s (whole pack) |
| `docs/rag/api-reference.md` | `Platform` / `SparseIndexCompositionTests` | 1080 s (whole pack) |
| `docs/interplatform/cross-runtime/**` | `Platform` / `CrossRuntimeFederationConformanceTests` | 1080 s (whole pack) |
| `samples/**` | `Samples` (builds every sample), `Platform` (fitness sweeps), `Platform.Remoting` (the sweep) | 648 s; not covered |

**Findings.** No prose path in `reads` is opened by nothing, so none left `reads`. No prose path is opened only by a
CI-only row. Two declarations were wrong and are corrected in the manifest, without moving the record's `reads`:

- `docs/platform/open-core-boundary.md` was declared on the `Build` pack. No Build test opens it (its tests read a
  fixture copy); the `FuaranCoResolution` leg does. The read moved to that row.
- `docs/platform/deprecation-policy.md` was declared on `Platform.PublicApi`. The pack names it only in a failure
  message and never opens it. The declaration was removed.

`Platform` declared `docs/**`. Its fitness sweeps open only F# sources and project files; the documents it opens are
the nine files above, now declared one by one, so the lane can keep the lists that open them and leave the rest of
the pack out.

## The record: `lanes`

`gate-reads.json` gains a `lanes` member, one object per lane narrower than the full one:

```json
"lanes": [
  { "lane": "prose", "env": "TOOLUP_TEST_LANE=prose", "covers": [ "CHANGELOG.md", "…", "docs/**" ], "units": [ "Platform/…", "Build", "FuaranCoResolution" ] }
]
```

`covers` are entries of `reads`, in the record's own path grammar. A change is the lane's when no path is a .NET
default input or under a `reads` entry outside `covers`. `units` name what the lane runs in the vocabulary of the
selected-lane manifest the landing tooling is to read: `<row>/<list>` for a list of a Platform-family pack, the bare
row for a row run whole. The landing tooling ignores the member today (it reads `reads`, `notRead`, `builds` and
`notBuilt` by name). When it admits a selected lane, `covers` is the complement of that policy's forcing paths for
this repository, and `units` is the declared unit set a selected run must have run.

## Measurements

Measured 2026-11-23 on one 16-logical-processor Windows machine, in a worktree, through
`pwsh ./verify.ps1` with `TOOLUP_TEST_LANE=prose`. Seconds. The full lane's median over the same record is 3405 s
(p90 4790 s).

| Run | Change | Build step | `VerifyAll` | Whole run | Verdict |
|---|---|---|---|---|---|
| 1 | one new document, first run after the commit | 830 | 561 | 1481 | green: Platform 174/174, Platform.Remoting 17/17, Build 765/765, FuaranCoResolution |
| 2 | the same tree again | 38 | 158 | 273 | green, the same tallies |
| 3 | a private name seeded in `docs/platform/events.md`, a new commit | about 830 | about 430 | 1345 | red: the publication-boundary sweep names `docs/platform/events.md:326` and the banned token; Platform 174/174, Build 765/765 |

The `Platform` pack's 174 kept cases took 54 s on run 2 and 297 to 424 s on runs 1 and 3, which followed a solution
rebuild. Its full-lane median is 1080 s.

**The build step dominates the first run after a commit, in every lane.** The SDK stamps the commit's hash into
every assembly's informational version, so a new commit, prose or not, rebuilds the solution: 830 s here. The lane
removes the test packs, not the build. A prose commit therefore costs about 23 to 25 minutes on its first run and
under five once built, against the full lane's 57-minute median, which pays the same build.

**The refusal, run on this phase's own branch** (`TOOLUP_PROSE_BASE` unset, so the change since the merge base with
`origin/main`): `VerifyAll` exits 2 before any row runs and names each of the seven code paths, for example
`Build.fs is a .NET default input (*.fs): the full lane gates it`.

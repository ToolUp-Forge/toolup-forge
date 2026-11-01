# The `affected` lane: run the Platform lists a change can reach

**Ships in:** the repository's own build and test tooling (`Build.fsproj`, `ToolUp.Platform.Tests`,
`ToolUp.Platform.Build.Tests`). No package changes and no consumer action.

**Affected:** contributors and automated workers who run `verify.ps1` before the full gate. The default lane
(`TOOLUP_TEST_LANE` unset, which is `full`) is unchanged byte for byte, and it is still the only lane a ship or a
release may cite.

## Why

The full gate runs every Platform list for every change. On a quiet machine that is about 26 minutes. A change to one
server file reaches a handful of lists, and the worker making it wants to know whether those lists are green before
paying for the whole gate. `fast` drops a fixed slow set whatever the change is. `affected` drops what the change
cannot reach, as far as the change says, and prints what it dropped.

## The measurement

**Quiet machine, 2026-10-25,** on an idle machine, Debug, tree `6b0d7280`. `verify.ps1`, full lane, was green in
**25m46s**:

| Stage | Wall-clock | Share |
|---|---|---|
| tool restore, Fantomas `--check`, SPDX headers | 25 s | 2% |
| `dotnet build ToolUp.Forge.sln` | 4m18s | 17% |
| VerifyAll warm-up | 29 s | 2% |
| Platform pack (14,732 cases, 974 top-level lists) | 9m15s | 36% |
| the 25 other packs together (largest 10.5 s) | 1m57s | 8% |
| Fable leg | 2m26s | 9% |
| Samples leg (21 sample projects, six Fable transpiles) | 6m57s | 27% |
| Analyzers leg | 6 s | 0% |

**CI, the same day:** the `verify-all` job took **33 minutes**.

**Inside the Platform pack.** A standalone run with `--junit-summary` took 7m57s and passed 14,687 cases (45 ignored),
with 470.4 s of case time over 974 lists. No list dominates any more. The thirty slowest lists below are 353.4 s,
75% of the case time. The other 944 lists are each under 5 s, and together they take 117 s.

| # | List | Cases | Case time | Slowest case |
|---|---|---|---|---|
| 1 | Phase 702 metric surface at scale | 2 | 45.3 s | 40.6 s |
| 2 | HnswFidelity | 6 | 38.8 s | 16.3 s |
| 3 | Phase 1025 — a store write started fire-and-forget cannot kill the host | 47 | 21.1 s | 0.6 s |
| 4 | Phase 1047 — the event streams authenticate whichever sign-in the user took | 12 | 19.4 s | 5.0 s |
| 5 | Phase 805 — generated-baseline merge drivers | 19 | 17.8 s | 8.5 s |
| 6 | ConfigValidatorAggregator | 22 | 16.7 s | 10.0 s |
| 7 | Phase 500 — OCR provider companion + OCR-unavailable signal | 32 | 12.9 s | 6.3 s |
| 8 | Phase 888 — PostgresFactStore (no database) | 19 | 12.7 s | 7.0 s |
| 9 | InProcessJobScheduler | 52 | 12.5 s | 5.1 s |
| 10 | ToolUp.ExternalCompute.Http — the generic HTTP/REST dispatcher (Phase 322) | 66 | 12.4 s | 4.0 s |
| 11 | Phase 890 point-read index | 12 | 12.2 s | 3.3 s |
| 12 | Phase 175 — Public-API approval baseline | 228 | 12.0 s | 0.7 s |
| 13 | Phase 1026 — every fire-and-forget audit record is caught at its call site | 24 | 11.4 s | 0.9 s |
| 14 | Phase 801 — the platform's decoders are generated and verified | 8 | 10.5 s | 5.3 s |
| 15 | Phase 730 — grant-governance completeness | 16 | 10.2 s | 5.0 s |
| 16 | IExternalComputeDispatcher contract — self-test (the pack has teeth) | 9 | 10.0 s | 10.0 s |
| 17 | Phase 303 — ingestion-queue backpressure observability | 35 | 8.8 s | 1.6 s |
| 18 | Phase 562 taint-propagating disclosure | 29 | 7.5 s | 5.4 s |
| 19 | Phase 1024 — a fire-and-forget audit write never faults unobserved | 15 | 7.3 s | 0.6 s |
| 20 | Phase 895 — fact browse surface | 30 | 6.5 s | 5.5 s |
| 21 | Phase 788 - the proved Elmish runtime as oracle | 22 | 6.2 s | 4.1 s |
| 22 | Phase 889 — delegate facts | 38 | 6.0 s | 5.7 s |
| 23 | Phase 859 — team conversation visibility | 57 | 6.0 s | 1.9 s |
| 24 | Phase 483 IRoundOrchestrator | 17 | 5.2 s | 2.0 s |
| 25 | Phase 799 — the JSON wire joins the decoder algebra | 70 | 4.6 s | 2.2 s |
| 26 | Phase 18f ICommutativeCipher | 74 | 4.5 s | 1.5 s |
| 27 | EdgeCache (Phase 472) | 99 | 3.9 s | 0.5 s |
| 28 | MediaLibrary (Phase 88) | 153 | 3.8 s | 1.6 s |
| 29 | FileSecretStoreAtomicity | 4 | 3.8 s | 2.0 s |
| 30 | InProcessRateLimiter — IRateLimiter contract | 9 | 3.2 s | 1.1 s |

Re-derive it with the recipe beside the lane declaration in `src/ToolUp.Platform.Tests/Program.fs`. Run the pack's
dll with `--junit-summary`, then group `//testcase/@time` by the second `;`-separated segment of `@name`.

**What the figures say about the lane.** The 48 to 88 minute gates seen on a busy campaign machine were contention
between parallel builds, not suite size. For a change confined to server code, the saving comes from three stages.
First, the Platform lists the change cannot reach: about eight minutes become seconds. Second, the Samples transpiles.
Third, the Fable leg. Together the last two are 9m23s, and neither runs unless client-tier code moved. The solution
build (4m18s) is the floor and is not selected: breaking a dependent's compile is a real regression. On these figures a
server-only change's `affected` run is about 4m18s build, under a minute of selected lists, two minutes of other packs
and the sample builds. That is about a third of the full lane.

## How to use it

```powershell
dotnet run --project Build.fsproj -- Select --base origin/main --head HEAD --declared tests.txt --out .affected/selection.json
$env:TOOLUP_TEST_LANE = 'affected'
$env:TOOLUP_TEST_LISTS = "$PWD/.affected/selection.lists"
pwsh ./verify.ps1
Remove-Item Env:TOOLUP_TEST_LANE, Env:TOOLUP_TEST_LISTS
```

`--paths <file>` (one path per line) replaces `--base`/`--head` to predict the selection from the files a change will
touch, before it exists. `--declared` is optional. Each of its lines is `<pack>/<list>`, or a bare Platform list, given
by its label or by the binding `Program.fs` registers. The rules, the manifest and what the lane does not protect
against are in [`docs/platform/testing-conventions.md`](../platform/testing-conventions.md#the-affected-lane-run-the-lists-a-change-can-reach).

## Rollback

Unset `TOOLUP_TEST_LANE`. Nothing else reads the lists file or the manifest.

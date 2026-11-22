# The gate has a pre-flight lane: `TOOLUP_TEST_LANE=preflight`

**Ships in:** the repository's own gate (`TestLane.fs`, `GateManifest.fs`, `Build.fs`, the lane arms of
`TestLaneFilter.fs` and `PackEntry.fs`). No package surface moves and no consumer action is needed.

**Affected:** contributors and automated workers who run the gate before a push or before a landing claims the full
lane.

## What changed

`TOOLUP_TEST_LANE` accepts a fifth value, `preflight`. Under it `verify.ps1` runs its own steps as in every lane (tool
restore, `fantomas --check`, the SPDX header check, the solution build), and `VerifyAll` then runs only the rows of
`GateManifest.rows` that opt in with `preflight`:

| Row | What it catches |
|---|---|
| `Platform.PublicApi` | a public-surface change with no baseline update, in either direction |
| `GateReconcile` | a test project that is neither a pack row nor an exclusion; a `gate-reads.json` or `gate-timings.json` that disagrees with the manifest; a fixture directory a run left behind |
| `Fable` | a client-tier source that does not transpile, or a `node:test` case that fails |
| `Analyzers` | a new TUA rule site, or an inventory that disagrees with the tree |
| `ScopeLiterals`, `ServiceConfigFold`, `NavRoleFinding`, `FuaranCoResolution` | the tree-only source checks |

No behavioural test pack runs. `Platform.PublicApi` is a pack only in form: it holds one list, the approval gate over
every packable project's built surface. The lane is never citable as the gate; a ship cites `full`, as before. What the
lane is for and how to run it: [`docs/platform/testing-conventions.md`](../platform/testing-conventions.md#the-preflight-lane-the-gates-cheap-reds-in-minutes).

**Left out, and why.** `Samples` is about ten minutes on its own (`gate-timings.json`), the whole budget. It catches a
sample that no longer builds or, in the full lane, transpiles. `FuaranCoBuild` restores from the network and Fable
compiles the fact tier, five minutes or more. `MergeDrivers` only warns, so it cannot make the lane red. The CI-only
`SourceCitations` and `UploadResults` rows stay CI-only here: joining them to the local lanes is Phase 1383's work, and
whichever of the two phases lands second adds them to this lane.

## Measurements

Measured on one 16-logical-processor Windows machine with no other gate running (`waitedSeconds` 0 in both journal
records), on the tree this phase landed from. Seconds.

| Step | Run 1: a fresh worktree, first run after its first build | Run 2: the same tree, built |
|---|---|---|
| tool restore, `fantomas --check`, SPDX check | about 100 | about 80 |
| solution build (`verify.ps1`) | 60 | 40 |
| `VerifyAll`'s own build | 51 | 40 |
| `Platform.PublicApi` | 463 | 14.6 |
| `Fable` | 355.2 | 349.8 |
| `Analyzers` | 11.0 | 10.0 |
| the other five rows together | 3.2 | 3.2 |
| **wall clock, `verify.ps1` end to end** | **1049** | **539** |

The second run is the lane's budget: **8 minutes 59 seconds**, under the ten-minute limit, and that is the case the
lane is for, a tree its author has already built. The first run is over the limit, and the excess is one leg:
`Platform.PublicApi` loads every packable project's freshly written assembly, and on a tree nothing has run against yet
it took 463 seconds against 14.6 a run later. The journal shows the same leg at 15–48 seconds on most full-lane runs and
at 440–2,750 seconds on a handful, runs in which the other legs were slow too. The cold solution build before either run
took 17 minutes 37 seconds, which no lane can save: every lane builds first.

`Fable` is two thirds of the warm lane and it is the floor: it is the only leg in this lane that catches a client-tier source that
compiles on .NET and not under Fable. No leg was cut. If a later change pushes the warm lane past ten minutes, `Fable`
is the leg to look at first, and dropping it would give up the reds in the table below that it alone catches.

### After a change, on a loaded machine

The three acceptance runs below were each made on a seeded change, one at a time, while another session's builds
and Fable compiles ran on the same machine. Each is red at the row it should be and at no other.

| Seed | Red row (its own message) | `verify.ps1` build | `Platform.PublicApi` | `Fable` | Wall clock |
|---|---|---|---|---|---|
| `Async.RunSynchronously` in a server source | `Analyzers`: TUA003, one site the inventory does not account for | 1685 | 485.7 | 796.3 | 3407 |
| a public member with no baseline update | `Platform.PublicApi`: 1 public member ADDED vs the committed baseline | 1051 | 996.7 (failed) | 618.9 | 2935 |
| `System.IO.File.ReadAllText` in a client-tier source | `Fable`: "is not supported by Fable" (the .NET build is green) | 998 | 374.6 | 227.8 (failed) | 1809 |

None of these is under ten minutes, and the lane's own rows are not the main reason. A change to a source that many
projects reference rebuilds them, and that build is `verify.ps1`'s, paid by every lane before any row runs. Of what
the rows add, `Platform.PublicApi` is the variable part: in each slow run its process sat at almost no CPU for
minutes before its cases ran, and each case took a fraction of a second once they did. That stall is not this lane's;
the full lane's journal records it too. Until it is found, budget the lane at the warm figure plus the build a change
causes plus up to a quarter of an hour for that row.

## The lane against the gate's recorded history

The admission journal on the measuring machine (`<common git dir>/fuaran-gate/verify-runs.jsonl`) held 32 records
when this phase was written, 2026-11-03 to 2026-11-22: 24 in the full lane and 8 in `fast`. That is fewer than the
forty the phase asked for, so every record is used. Seven of the 24 full-lane runs were not green. Each is listed by
the rows that failed, with the row's median time on its own over the green full-lane runs (`gate-timings.json`):

| Run (UTC) | Rows that failed (median seconds alone) | Would `preflight` have been red? |
|---|---|---|
| 2026-11-05 12:54 | `Platform` (1080), `Platform.PublicApi` (256; usually 15–30) | yes, at `Platform.PublicApi` |
| 2026-11-17 14:27 | `Platform.Remoting` (78) | no: a behavioural pack |
| 2026-11-17 20:49 | `CompanionsIsolation` (8), `Fable` (256) | yes, at `Fable` |
| 2026-11-20 00:35 | `JobSchedulers` (4.5) | no: a behavioural pack |
| 2026-11-20 19:39 | `Platform` (1080), `GateReconcile` (0.7) | yes, at `GateReconcile` |
| 2026-11-21 05:22 | `Build` (22) | no: a test pack |
| 2026-11-21 08:30 | `Build` (22) | no: a test pack |

**The lane would have caught 3 of the 7 full-lane reds (43%).** Counting the `fast` runs too, 4 of 11 (36%): of the
four `fast` reds, only the one on 2026-11-21 22:36 failed a row in the lane (`GateReconcile`, beside four packs).

Two things bound the figure:

- **It is a lower bound for the lane's own steps.** The journal records `VerifyAll`; a run that `verify.ps1` stopped
  at the format check, the header check or the solution build never reached it, so no record says how often those
  were the red. The lane runs all three.
- **The `Build` pack is the largest share it misses.** Two of the four misses are the `ToolUp.Platform.Build.Tests`
  pack, which holds the repository's own census and render checks (the gate manifest, `gate-reads.json`, the build
  census, the baselines' tooling) and runs in about 22 seconds alone. It is a test pack, and this lane runs none, so it
  is out. Adding it would raise the replayed share to 5 of 7 (71%) for about half a minute; the journal does not say
  which of its cases failed, so whether those two reds were census rows or behaviour is not known.

The red legs are dominated by rows that run in minutes or less alone: of the ten row failures in the seven reds,
eight are rows under five minutes. The two that are not are the `Platform` pack, and both of those runs also failed a
row the lane runs.

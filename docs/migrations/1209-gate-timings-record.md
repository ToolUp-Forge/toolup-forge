# The gate's cost is a committed record: `gate-timings.json`

**Ships in:** the repository's own gate (`VerifyGate.fs`, `GateManifest.fs`, `Build.fs`'s `GateTimings` target and
`GateReconcile` row, the `ToolUp.Platform.Build.Tests` pack). No package surface moves and no consumer action is needed.

**Affected:** contributors and automated workers who run the gate, plan work against its cost, or edit the gate
manifest.

## What changed

`VerifyGate` has appended one admission record per gated run to `<common git dir>/fuaran-gate/verify-runs.jsonl` since
Phase 735, and `dotnet run --project Build.fsproj -- GateTimings` folded those lines into medians. Both lived in a
gitignored journal on one machine. So the gate's cost was a number someone remembered, and each row's `Budget` was
whatever was last typed into `GateManifest.fs`.

Now:

- **`GateTimings --record` writes `gate-timings.json`** at the repository root (kind `gateTimings`): the window
  (`since`, `until`), the number of runs in it, the machines that ran them, per lane `{ median, p90, runs }` and per
  row `{ median, runs }` in seconds, and `foldedAt`, the commit it was folded at. Without `--record` the target prints
  the table as before.
- **A row's `Budget` in `GateManifest.rows` is now its seed.** `GateManifest.withRecordedBudgets` replaces every seed
  the record measures. `GateManifest.budgetSource` says whether a row's budget is `Measured`, a `Seed`, or
  `Unbudgeted`. `VerifyGate`'s stale-holder bound and its set of gated targets read the derived budgets, so the bound
  sums the figures the planning tooling reads.
- **`GateReconcile` and `Gate --check` hold the record against the manifest.** They are red when the record measures a
  row `VerifyAll` does not run in the full lane, or when a row it runs has neither a median in the record nor a seed.
  They warn, without failing, when the record's window ended more than 14 days ago.
- **The record is gate-inert.** It is in `gate-reads.json`'s `notRead`. `GateReconcile` reads it only to hold it
  against the manifest, and `--record` refuses to write a record that check would fail, so committing a fold cannot
  change a verdict. The stale-holder bound decides how long a run waits, never whether it passes.
- **The close-out of every series of landings on this repository runs `GateTimings --record` and commits
  `gate-timings.json` by name.** A commit carrying only the record crosses a held landing without a re-gate.

## The fold's rules

- **The window is the given width ending at the newest recorded run.** Seven days by default, and `--since <days>`
  widens it. It is anchored at the journal's newest run, not at the clock, so the same journal folds to the same record
  whenever it is folded.
- **A run is green when its exit code is 0 and no leg failed.** Lanes fold the green `VerifyAll` runs of each lane.
  Rows fold the passing legs of the green full-lane `VerifyAll` runs, for the rows the manifest runs in the full lane;
  a leg naming a row the manifest no longer has is dropped. A standalone `Verify<Row>` run is counted in `runs` but
  folds into no row, because its wall clock includes a build the leg inside `VerifyAll` does not pay.
- **A re-fold of the same journal is a no-op diff.** `foldedAt` is the commit `HEAD` named when the record was written,
  and it is the one field a re-fold does not compare: a fold that differs only there leaves the file alone. So the
  first record keeps the commit it was folded at, and `GateTimings --record` run again over the same journal prints
  `already holds this fold; not rewritten`.
- **Seconds are written to one decimal, keys in a fixed order, lanes and rows sorted ordinally.**
  `VerifyGateTests` folds a known journal and compares the record byte for byte.
- **A journal with no runs writes nothing and says so.**

## Two defects in the journal, fixed on the way

- **Every record said `exit 0`.** The driver reports a failed target by returning 1, not by throwing, and the gate
  recorded the exit as "did the body throw". Three runs in this machine's journal recorded `exit 0` over a failed leg.
  The gate now records the code the driver returned, and the fold reads a line as green only when no leg failed as
  well, so the older lines are judged correctly.
- **No record named its machine.** The phase was written on the assumption that the journal recorded the machine; it
  did not. Each line now carries `machine`. A line written before this carries none, and the fold reads it as the
  folding machine's, since the journal lives in one clone's git directory.

## Machines are hashed labels

The journal is gitignored and machine-local, and it records the host name. The committed record is published, so it
never carries one: the fold writes each machine as a label, the first eight hex digits of the SHA-256 of the
upper-cased host name (`VerifyGate.Runs.machineLabel`). A label is stable per machine and says nothing about it. The
fold applies it to every line, so an older line carrying a raw host name folds to the label as well.

## The first record

Folded from one machine's journal: the 18 runs between 2026-11-02 01:36 UTC and 2026-11-03 00:51 UTC, which are the
runs between 1193 and 1205 that this machine gated. The other machines that worked in that window keep their journals
in their own clones and were not reachable from here, so this record covers one machine. A `GateTimings --record` on
another machine writes that machine's window in its place.

| Window | Runs | Machines |
|---|---|---|
| 2026-10-27 00:51 to 2026-11-03 00:51 UTC (the runs fall on 2026-11-02 and 03) | 18 | one, labelled `bc424cbe` |

| Lane | Median | p90 | Green runs |
|---|---|---|---|
| `full` | 1717.0 s (28.6 min) | 2099.2 s (35.0 min) | 7 |
| `fast` | 1093.8 s (18.2 min) | 1249.2 s (20.8 min) | 2 |
| `pure` | 38.5 s | 72.4 s | 3 |

The rows that dominate the full lane:

| Row | Median | Runs |
|---|---|---|
| `Platform` | 599.4 s | 7 |
| `Samples` | 441.2 s | 7 |
| `FuaranCoBuild` | 278.3 s | 7 |
| `Fable` | 157.2 s | 7 |
| `Platform.Remoting` | 70.1 s | 2 |
| `Platform.PublicApi` | 58.6 s | 2 |

The row medians sum to 1866.6 s, so the stale-holder bound (twice the sum) is 62.2 minutes. The seeds gave 62.5.

Two readings of the table:

- **The `Platform` median spans the pack's partition.** Phase 1194 moved most of the residue pack's lists into the
  `Platform.*` packs inside this window. The two runs after the partition recorded 496.8 s and 471.6 s, and the five
  before it 594 s to 650 s. The median is the window's figure; the next weekly fold will be all post-partition runs.
  The `Platform.*` rows have two runs each for the same reason.
- **`FuaranCoBuild` now takes the gate when run on its own.** Its measured median (278.3 s) is over a minute and it
  reads the tree, so `VerifyFuaranCoBuild` joins `VerifyAll`, `VerifyFable` and `VerifySamples` in the gated set. That
  is the derivation working as designed: a row's measured budget is what admits it.

The `fast` lane has two green runs. Three other fast runs in the window had a failed leg and are not green.

## Rollback

Delete `gate-timings.json` and its `notRead` entry, and drop the `timingsFindings` call from `GateReconcile` and
`Gate --check`. The budgets then fall back to their seeds, which is the state before this phase.

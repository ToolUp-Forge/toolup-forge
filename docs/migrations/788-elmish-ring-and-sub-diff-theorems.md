# Phase 788 — the Elmish ring buffer and subscription diff, proved and tested

**Consumer action required: none.** Additive. One behaviour change is visible only to a deployment
that called `Program.withRingBufferCapacity` with a value **below 10**: it used to get a 10-slot
ring silently, and now gets the ring it asked for (floored at 2). The ring grows on overflow either
way, so nothing observable changes but the allocation pattern.

## What changes

| | Before | After |
|---|---|---|
| `RingBuffer` (internal, `Client/Elmish/Ring.fs`) | no test on either host; constructor floors at 10 (`Array.zeroCreate (max size 10)`) | proved a FIFO queue through every grow (`proofs/ElmishRing.fst`, `ring_is_queue`); constructor floors at **`RingBuffer.MinimumCapacity` = 2**, the one number the proof's precondition needs |
| `Program.withRingBufferCapacity` | `max 1 capacity` — a floor the constructor then silently overrode, and at which the ring representation is ambiguous | `max RingBuffer.MinimumCapacity capacity`; **+ `Program.ringBufferCapacity`** accessor beside it |
| `Sub.Internal.diff` / `NewSubs.calculate` / `Fx.change` | no test on either host | proved to start / stop / keep exactly what the source comment says, never both for one key, the duplicates exact, the `keys = newKeys` shortcut agreeing with the general path (`proofs/ElmishSub.fst`) |
| `proofs/` | five models | **+ `ElmishRing.fst`, `ElmishSub.fst`**, their extractions under `oracle/`, two `check.ps1` entries, a README ladder, `proofs.json` claims |
| Differential hosts | one per model, .NET only | one shared host-neutral module (`ToolUp.Platform.Tests/Client/ElmishProofDifferential.fs`) driven from **both** the Expecto pack (model live) and the Fable `node:test` pack (the model's verdicts replayed from `tests/elmish-proof-corpus/`, because an extraction cannot compile under Fable — see the note), since the runtime ships to both |
| `ToolUp.Platform.Client` | — | `InternalsVisibleTo` the two test packs (test-only visibility; no shipped surface) |

## Why

The in-tree Elmish runtime is the loop every client in the estate runs on, and its core had nothing
exercising it. Two of its structures are closed algebraic objects whose law is already written in
the source — the ring's "placeholders are never observed as a value" and the diff's "subs whose
`SubId` disappears are stopped, new ones are started" — which makes them the cheapest proofs on the
estate's list and the ones a differential can hold to the code. The shortcut at the top of `diff` is
the specific worry: a `keys = newKeys` fast path that disagreed with the general path is exactly
where a started-twice / never-stopped subscription leak would live, and nothing else would see it.

Two things the proof surfaced that the source did not say:

- **The grow step is `2n + 1`, not `2n`.** `for _ in 0 .. items.Length` is inclusive. Harmless —
  the theorem holds — but the comment says "doubling" and the model reproduces what the code does.
- **The capacity floor was two numbers.** The constructor floored at 10 while `withRingBufferCapacity`
  floored at 1 and documented per-program configurability. At capacity 1 the `ReadWritable` state
  cannot distinguish one unread slot from none and the second push overwrites the first item before
  the grow step runs (`capacity_one_loses_an_item`, computed). The proof's precondition is 2; the
  phase makes that the one floor, stated once.

## Diff to apply

**Every deployment:** nothing.

**A deployment that set `withRingBufferCapacity n` with `n < 10`:** you now get an `n`-slot ring
(2 at minimum) that doubles on overflow, rather than a 10-slot one. If the 10 was load-bearing for
you — it cannot have been for correctness, the theorem says — pass 10.

**A deployment that reads the capacity back:** `Program.ringBufferCapacity program` is new.

## Verification

- `pwsh ./proofs/check.ps1`: both modules check on the pinned prover (`v2026.09.06`, Z3 4.13.3)
  with `--report_assumes error`, no admits; `--quake 3` green; extractions byte-identical to the
  committed oracles; the oracle project builds; the differential host runs its declared case floor.
- `ElmishProofOracleTests` (.NET): 100 generated ring sequences (capacities 2–13, four push biases,
  each also run with a full drain), the model reaching > 111 slots; 400 generated subscription
  inputs with > 30 shortcut hits and > 30 duplicates; both go-red cases caught.
- The same campaign under Fable via `VerifyFable`, replayed from `tests/elmish-proof-corpus/`
  (written by the .NET host and held to the live model on every run; regenerate with
  `dev-scripts/generate-elmish-proof-corpus.ps1`): 200 ring cases and 400 diff inputs, both go-red
  cases caught. **Why a corpus:** the F\* extractor's output needs `--strict-indentation-`, which
  Fable does not read from an fsproj, and pinning the Fable pack's `LangVersion` to 7 is forbidden
  by the workspace baseline.
- `VerifyAll` green; `api-baselines/ToolUp.Platform.Client.approved.txt` regenerated for the
  accessor.

## Rollback

Revert the commit. The floor returns to its two numbers, the accessor disappears, the proof leg's
module list shrinks by two. The gitignored `proofs/.fstar/` toolchain is unaffected either way.

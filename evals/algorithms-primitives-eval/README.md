# Algorithms-primitives eval (Phase 11.E.0)

**Question:** how much LLM friction does a *curated* analytical-primitive catalog actually remove
over a code assistant reaching for the raw numerics library? The answer decides which per-operation
interfaces `ToolUp.Algorithms` ships (Phase 11.E.2) — measurement, not intuition.

This is a **pre-build measurement exercise**, not a regression suite. It is not wired into
`VerifyAll` and does not run in CI. Re-run it by hand when the underlying numerics library moves a
major version, or when a new candidate operation is proposed for the catalog.

## Method

Five representative vibe-coder prompts (plus one control), each run twice:

- **Pass A — raw library.** "Write F# code using MathNet.Numerics 5.0 to do *X*." The code is
  written **from memory first**, exactly as a code assistant would emit it, *then* compiled and run.
  Looking the API up before writing would measure the documentation, not the assistant.
- **Pass B — curated catalog.** The same task, with the `ToolUp.Algorithms` catalog tool signature
  hand-mocked into the prompt. Scored on call shape and typed-return interpretation, not on
  compilation (there is nothing to compile until 11.E.2 exists).

Each Pass-A script is executed for real:

```
dotnet fsi <script>.fsx      # with  #r "nuget: MathNet.Numerics.FSharp, 5.0.0"
```

## Scoring

Two axes, because they fail differently and only one of them is dangerous.

| Axis | What it measures | How it shows up |
|---|---|---|
| **Attempts-to-green** | compile / run failures | loud — the compiler names it, the author fixes it |
| **Silent-divergence risk** | the code compiles, runs, and returns a plausible number that is **wrong for the question asked** | invisible — nothing in the toolchain objects |

Attempts-to-green is the cost that *feels* expensive. Silent divergence is the cost that actually
matters, and it is the axis a wrapper is uniquely able to remove — by putting the ambiguous choice
(quantile definition, window alignment, estimator method) into the *type* rather than into a default
the caller never sees.

**Verdict rule.** An operation is wrapped when its measured delta is high on **either** axis, and
excluded when both are low. An operation with no measured evidence is excluded and recorded as such
— shipping an interface on intuition is precisely what this eval exists to prevent.

## Files

| File | Contents |
|---|---|
| `prompts/*.md` | the six prompts, one file each |
| `results-pass-a.md` | the raw-library scripts as written from memory, verbatim compiler/run output, and per-attempt failure analysis |
| `results-pass-b.md` | the hand-mocked catalog signatures and the call-shape / return-interpretation scoring |
| `findings.md` | the delta table and the curated interface list it selects for Phase 11.E.2 |

## Environment

- .NET 10 (`dotnet fsi`), F# 10, Windows.
- `MathNet.Numerics.FSharp` **5.0.0** (the version the phase names).
- Run date: 2026-07-28.

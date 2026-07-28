# Prompt 6 — nonlinear curve fit (control)

**Operation space:** `ICurveFitter`.

**Why a control.** The other five prompts were chosen because there was reason to expect friction.
This one exists to give the *exclusion* decision the same evidentiary standing as the inclusion
decisions — an interface dropped on the grounds that "raw MathNet does fine" should have been
measured doing fine, not assumed to.

## Pass A (raw library)

> I have spend levels and the response at each level, following a diminishing-returns shape
> `y = a·(1 − exp(−b·x))`. Write F# using MathNet.Numerics 5.0 that fits `a` and `b` and prints
> them.

## Pass B (curated catalog)

> Not run — the catalog signature is only mocked for algorithms that the eval recommends shipping,
> and this prompt exists to test whether it should be shipped at all.

## What "correct" means

Data is generated from `a = 100`, `b ≈ 0.2`; a correct fit recovers both to within a few percent.

The interesting question is not whether it fits — it is whether the caller can tell **that** it fit.
A nonlinear optimiser that stalls at a local optimum, or that exhausts its iteration budget, returns
parameters shaped exactly like a converged answer.

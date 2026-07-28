# Prompt 2 — multivariate regression with one categorical predictor

**Operation space:** `IRegressionFitter` (multivariate arm + categorical encoding).

## Pass A (raw library)

> I have sales, marketing spend, and a region label that is either "North" or "South", one row per
> observation. Write F# using MathNet.Numerics 5.0 that regresses sales on spend **and** region, and
> prints the coefficient for each term.

## Pass B (curated catalog)

> Same question, using the `ToolUp.Algorithms` catalog algorithm `regression.linear` with the
> mocked signature. State the coefficient on spend, the coefficient on region, and which region
> level the model treated as the reference.

## What "correct" means

Data is constructed from `sales = 5 + 2·spend + 5·[region = South]` exactly, so a correct fit
recovers `intercept = 5`, `spend = 2`, `regionSouth = 5`.

Three things must go right and only one of them is arithmetic:

1. the categorical is **dummy-coded**, not treated as a number;
2. exactly one level is **dropped as the reference** (encoding all levels plus an intercept makes
   the design matrix singular — the dummy-variable trap);
3. an **intercept column** is present.

The reported coefficients are uninterpretable unless the caller is told *which* level became the
reference. Raw-library code returns a bare coefficient vector and says nothing about it.

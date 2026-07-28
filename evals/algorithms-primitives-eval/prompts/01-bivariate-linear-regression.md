# Prompt 1 — bivariate linear regression

**Operation space:** `IRegressionFitter` (bivariate arm).

## Pass A (raw library)

> I have advertising spend in £k and the resulting sales in units, as two `float[]` of the same
> length. Write F# using MathNet.Numerics 5.0 that fits a straight line and prints the intercept,
> the slope, and the R².

## Pass B (curated catalog)

> The deployment exposes a `ToolUp.Algorithms` catalog. Call the algorithm
> `regression.linear` with the mocked signature below to answer the same question, then say what
> the intercept, slope and R² are in the returned value.
>
> (signature supplied verbatim in `results-pass-b.md`)

## What "correct" means

Intercept and slope of the ordinary-least-squares line, plus a coefficient of determination in
`[0, 1]` computed against the *same* fitted line. Data is constructed so the true relationship is
`y ≈ 2x` with light noise; a correct fit reports slope ≈ 2.0 and R² > 0.99.

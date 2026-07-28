# Prompt 5 — simple time-series smoothing

**Operation space:** `ITimeSeriesFilter`.

## Pass A (raw library)

> I have 12 weeks of sales as a `float[]`. Write F# using MathNet.Numerics 5.0 that computes a
> **3-period centred rolling mean** to smooth out the week-to-week noise, and an exponentially
> weighted moving average with α = 0.3 for comparison. Print both.

## Pass B (curated catalog)

> Same question, using the `ToolUp.Algorithms` catalog algorithm `timeseries.smooth` with the
> mocked signature. Report the smoothed value at week 2, say whether the window is centred or
> trailing, and say what the value is for the periods before the window is full.

## What "correct" means

A **centred** 3-period mean puts `mean(w1, w2, w3)` at **week 2**. A **trailing** 3-period mean puts
that same value at **week 3**. The two series are identical numbers shifted by one period, which is
exactly why a mis-alignment survives every eyeball check: the smoothed curve looks right, tracks the
raw series, and is a week out.

The second half of the question — what happens before the window is full — has three plausible
answers (partial-window mean, repeat the first value, undefined/NaN) that disagree at exactly the
points a trend-detection routine reads first.

## Adjacent finding

The phase's candidate list also names seasonal decomposition (STL) and the Hodrick–Prescott filter
under `ITimeSeriesFilter`. Neither has *any* surface in MathNet.Numerics 5.0 — see
`results-pass-a.md` §5b for the enumerated `Statistics` surface. There is no raw-library baseline to
measure against, because there is no raw-library capability.

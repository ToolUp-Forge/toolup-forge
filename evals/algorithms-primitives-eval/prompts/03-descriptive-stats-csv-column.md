# Prompt 3 — descriptive statistics on a CSV column

**Operation space:** `IDescriptiveStats`.

## Pass A (raw library)

> I have a CSV with columns `date,region,revenue`. Some `revenue` cells are blank. Write F# using
> MathNet.Numerics 5.0 that reads the file, takes the `revenue` column, and prints count, mean,
> median, standard deviation, min, max, the 25th and 75th percentiles, and skewness.

## Pass B (curated catalog)

> Same question, using the `ToolUp.Algorithms` catalog algorithm `stats.describe` with the mocked
> signature. Report the 25th percentile and say which percentile convention produced it.

## What "correct" means

The blank cell is excluded (n = 9, not 10), and every statistic is the sample (not population) form.

The percentile is where this gets interesting: for `n = 9` the 25th percentile is **111.0** under the
R-7 convention that Excel `PERCENTILE`, `numpy.percentile` and `pandas.quantile` all use by default,
and **106.67** under the R-8 (median-unbiased) convention. Both are defensible statistics. Only one
of them matches the spreadsheet the vibe-coder is checking against, and nothing in the code says
which one was chosen.

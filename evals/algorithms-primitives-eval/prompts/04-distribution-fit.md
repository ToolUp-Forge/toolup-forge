# Prompt 4 — distribution fit

**Operation space:** `IDistributionFitter`.

## Pass A (raw library)

> I have per-household purchase counts — mostly zeros, a long right tail. Write F# using
> MathNet.Numerics 5.0 that fits a Normal and a Negative Binomial to the counts, prints the fitted
> parameters of each, and prints the log-likelihood of each so I can see which fits better.

## Pass B (curated catalog)

> Same question, using the `ToolUp.Algorithms` catalog algorithm `distribution.fit` with the mocked
> signature, once per family. Report the fitted parameters, the estimation method used, and which
> family has the better log-likelihood.

## What "correct" means

A Normal fitted by MLE (`μ` = sample mean, `σ` = sample standard deviation), a Negative Binomial
fitted by *some* declared method, and log-likelihoods computed against the *same* data under each
family — the discrete family evaluated as a PMF, the continuous one as a density.

The over-dispersed count data makes the Negative Binomial the better fit; a correct answer reports
its log-likelihood as the higher (less negative) of the two.

The trap is that the two families are not fitted by the same machinery, and the assistant has to
notice that before it can write anything.

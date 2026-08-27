# ToolUp.AlgorithmProviders.MathNet

[Math.NET Numerics](https://numerics.mathdotnet.com/) implementation of the `ToolUp.Algorithms` provider seam. Server-tier only; the vendor dependency stops at this package and never reaches `ToolUp.Algorithms.Core` or `ToolUp.Algorithms.Server`.

Implements all four curated fitters — `IRegressionFitter`, `IDescriptiveStats`, `IDistributionFitter`, `ITimeSeriesFilter` — and declares the four canonical algorithm ids: `regression.linear`, `stats.describe`, `distribution.fit`, `timeseries.smooth`.

## Composing it

```fsharp skip=fragment
open ToolUp.Platform
open ToolUp.Algorithms
open ToolUp.AlgorithmProviders

ServerApp.empty
|> ServerApp.withConfig config
|> AlgorithmsCompose.withAlgorithms MathNetAlgorithms.withMathNetAlgorithmProvider
|> ServerApp.run
```

`MathNetAlgorithms.withMathNetAlgorithms` is the one-call form for a deployment whose algorithms come entirely from this provider. Alongside other providers, chain the registrations in a single `withAlgorithms` call — registration is append-only and a second claim on any id fails at compose time rather than being resolved by registration order.

## What the wrapper is actually for

The interfaces this package implements were selected by a measurement pass, not by intuition. Math.NET computes correct numbers nearly everywhere it has a surface at all; what it does not do is *state which convention it used*. This provider's job is to make those choices explicit, honour them, and echo them back:

| Convention | What this provider does |
|---|---|
| Quantile definition | `QuantileCustom` with an explicit `QuantileDefinition` — R-7 for `excelCompatible`, R-8 for `medianUnbiased`. Never the bare `Quantile` / `Percentile`, both of which default to R-8 under names that read as spreadsheet-compatible. |
| Smoothing alignment | `trailingMean` is the library's `MovingAverage`; `centredMean` is that series re-indexed with explicit `null` padding at **both** ends. The alignment is derived from the requested kind and echoed. |
| Estimation method | The estimators are this package's own closed forms. `maximumLikelihood` and `methodOfMoments` genuinely differ for the normal and log-normal families; gamma and negative-binomial are moment-only and **refuse** a maximum-likelihood request rather than substituting. |
| Categorical reference levels | Raw labels arrive, dummy coding happens here, and the level that became each factor's contrast base comes back on the result. With no intercept there is no base, so every level gets its own indicator and the reference list is empty. |

Each algorithm's `PrecisionContract` states the estimator variants, the parameterisations, the degenerate-case behaviour, and the Math.NET release the bindings were written against. Read it before comparing a number against another tool.

## Parameterisations

Distribution parameters are reported by name; the family conventions are:

| Family | Parameters | Estimators |
|---|---|---|
| `normal` | `mu`, `sigma` | MLE (n denominator), method of moments (n − 1) |
| `logNormal` | `mu`, `sigma` — **log scale** | MLE on log x; method of moments on the raw-scale moments |
| `poisson` | `lambda` | MLE and method of moments coincide (the mean is the only moment) |
| `gamma` | `shape`, `rate` — **rate, not scale** | method of moments only |
| `negativeBinomial` | `r`, `p` — `p` is the **success** probability, mean `r(1 − p)/p` | method of moments only; requires overdispersion |

Licensed under Apache-2.0. Math.NET Numerics is MIT-licensed and is credited in the repository `NOTICE.md`.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

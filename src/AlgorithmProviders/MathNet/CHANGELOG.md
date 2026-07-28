# Changelog — ToolUp.AlgorithmProviders.MathNet

All notable changes to the `ToolUp.AlgorithmProviders.MathNet` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.11.0]

Initial release (Phase 11.E.3) — the first implementation of the
`ToolUp.Algorithms` provider seam, over Math.NET Numerics 5.0.

- All four curated fitters: `IRegressionFitter`, `IDescriptiveStats`,
  `IDistributionFitter`, `ITimeSeriesFilter`, declaring the four
  canonical algorithm ids.
- The four echoed-convention obligations are honoured rather than
  defaulted: R-7/R-8 selected through `QuantileCustom`, centred
  smoothing re-indexed with padding at both ends, the estimator that
  ran reported on every distribution fit, and the categorical contrast
  base reported on every regression.
- Gamma and negative-binomial fits are method-of-moments only and refuse
  a maximum-likelihood request as `AlgorithmError.Unsupported` naming
  the provider — never a silent substitution.
- Every estimator, parameterisation and alignment is pinned by a
  known-answer case in `src/ToolUp.AlgorithmProviders.Tests`, which also
  binds the SDK's shared `IAlgorithmProviderContract` packs.

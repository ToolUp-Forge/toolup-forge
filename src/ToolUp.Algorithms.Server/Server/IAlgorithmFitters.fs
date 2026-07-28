// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Algorithms

open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.2 — the four curated fitter interfaces ──────────────
//
// One interface per operation the pre-build eval selected. They live in
// one file because they are one curated SET, chosen together by one
// measurement pass (`evals/algorithms-primitives-eval/findings.md`) —
// splitting them across four files would suggest they were arrived at
// independently, and would obscure that a fifth (`ICurveFitter`) was
// measured and deliberately left out.
//
// A provider implements whichever subset it can serve and declares only
// the algorithms it backs; `AlgorithmProvider.create` assembles the
// subset into an `IAlgorithmProvider`.
//
// ═══ Six portability rules (GP 12) — audit ══════════════════════════
//
// 1. **Identity by value.** Every parameter and return is a value
//    record over primitives and arrays (`AlgorithmOperations.fs`). No
//    live handle, matrix object, stream, or store reference crosses the
//    boundary. Algorithms are identified by `AlgorithmId = string`.
//
// 2. **Async at every boundary.** Every execution method returns
//    `Async<Result<_, AlgorithmError>>`, so a fitter backed by an
//    out-of-process worker, a grain, or a queued job is a drop-in.
//    *Declaration* metadata is deliberately synchronous — see
//    `IAlgorithmProvider.DeclareAlgorithms`, which follows the existing
//    `IModelFitProvider.DeclareGates` / `IAuditSink.Name` /
//    `INotificationSink.Kind` precedent: compose-time metadata is
//    read once while building the container, and making it awaitable
//    would force an `Async.RunSynchronously` at compose.
//
// 3. **Behaviour as data.** Failure is a typed `AlgorithmError` in the
//    `Result`, never an exception and never a caller-supplied callback.
//    A provider that cannot serve a request says so as data. The
//    dispatcher wraps any escaped exception into `ExecutionFailed`, so
//    the data contract holds even against a misbehaving provider.
//
// 4. **Stateless between invocations.** Every method receives the
//    ENTIRE input by value on each call. No fitter carries per-caller
//    state between calls, so a provider instance may be recycled,
//    deactivated, or replicated freely. A provider that memoises MUST
//    key purely on the request value.
//
// 5. **No cross-shard ordering.** Each invocation is independent; no
//    ordering is promised or required between two calls, to the same
//    fitter or across fitters.
//
// 6. **Precision at the lower bound.** Numerical precision is
//    implementation-defined and therefore DECLARED, not assumed:
//    `AlgorithmInfo.PrecisionContract` carries the provider's statement
//    per algorithm, and the four echoed-convention fields
//    (`QuantileConvention`, `SmoothingAlignment`, `EstimationMethod`,
//    `ReferenceLevels`) make the choices that would otherwise be
//    silent defaults into assertable data. Floating-point results are
//    reproducible only to the tolerance a provider's contract states.

/// Linear least-squares fitting. One interface covers both arms the
/// eval measured — a bivariate fit is one numeric predictor and no
/// categoricals.
///
/// **Categoricals arrive as raw labels.** Dummy coding, reference-level
/// selection and the intercept column are the implementation's, which
/// is what removes the dummy-variable trap and the missing-intercept
/// slip from the call site. The implementation MUST report the level it
/// chose as the contrast base in `RegressionFitResult.ReferenceLevels`;
/// a contrast coefficient without it is uninterpretable.
type IRegressionFitter =
    /// Fit `Response` on the supplied predictors by ordinary least
    /// squares.
    abstract FitLinear: request: RegressionRequest -> Async<Result<RegressionFitResult, AlgorithmError>>

/// Univariate descriptive statistics.
///
/// **The quantile convention is the contract.** An implementation MUST
/// honour `DescriptiveRequest.Convention` and echo it back on the
/// summary. Reporting `ExcelCompatible` while computing the
/// median-unbiased estimate is the precise defect this interface
/// exists to prevent — and it is one a numerics library invites, since
/// several default to R-8 under method names that read as
/// spreadsheet-compatible.
///
/// Dispersion statistics are the **sample** (n − 1) forms.
type IDescriptiveStats =
    /// Summarise `Values`, estimating each requested quantile under the
    /// requested convention.
    abstract Summarise: request: DescriptiveRequest -> Async<Result<DescriptiveSummary, AlgorithmError>>

/// Parametric distribution fitting.
///
/// **The estimator is reported, not assumed.** When the request leaves
/// `Method = None` the implementation picks its best available
/// estimator and MUST report which one ran. When the request names a
/// method the implementation cannot provide, it returns
/// `AlgorithmError.Unsupported` — never the other estimator silently.
///
/// `LogLikelihood` is evaluated as a probability MASS for discrete
/// families and a DENSITY for continuous ones
/// (`DistributionFamily.isDiscrete`), so two fits of the same sample
/// are comparable within their own class. `Aic` / `Bic` follow from it
/// via `DistributionFitResult.aicOf` / `bicOf`.
type IDistributionFitter =
    /// Fit `Family` to `Values`.
    abstract Fit: request: DistributionFitRequest -> Async<Result<DistributionFitResult, AlgorithmError>>

/// Time-series smoothing.
///
/// **Alignment is the contract.** `TrailingMean` places the window's
/// mean at the window's LAST period; `CentredMean` places it at the
/// window's MIDDLE period. The two produce identical numbers offset by
/// `(window − 1) / 2`, and the offset survives every visual check —
/// which is why `SmoothingResult.Alignment` is echoed and why the two
/// are separate `SmoothingKind` cases rather than a flag.
///
/// Periods before the window is full are `None` under
/// `UndefinedWarmUp` (the default) — never `nan`, which a downstream
/// mean would propagate silently.
///
/// The result MUST have the same length as the input series.
type ITimeSeriesFilter =
    /// Smooth `Values` under the requested kind.
    abstract Smooth: request: SmoothingRequest -> Async<Result<SmoothingResult, AlgorithmError>>
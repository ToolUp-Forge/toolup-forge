// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AlgorithmProviders

open MathNet.Numerics.Distributions
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders.MathNetAlgorithmSupport

// ─── Phase 11.E.3 — IDistributionFitter over Math.NET ───────────────
//
// The eval's only row scoring 3 on BOTH axes. Friction: Math.NET ships
// an `Estimate` helper for four distributions (normal, log-normal,
// Weibull, inverse-Gaussian) and none for the count families, so
// `NegativeBinomial.Estimate` simply does not exist. Divergence: the
// repair is a handful of lines of hand-derived moment matching that the
// compiler cannot check, and a wrong parameterisation compiles and
// prints plausibly.
//
// **The estimators here are this companion's own, including for the two
// families Math.NET *does* fit.** `Normal.Estimate` returns the mean and
// the UNBIASED (n − 1) sample standard deviation — which is neither the
// maximum-likelihood estimate (n) nor a moment estimate the caller can
// name. Reporting either `method` over it would be exactly the false
// claim the echoed-method field exists to prevent, so the closed forms
// are written out and Math.NET supplies the log-densities instead —
// which is the part a hand-rolled implementation gets wrong.
//
// **The parameterisation is the least checkable code in the family**
// (the eval's words), so every family below is pinned by a known-answer
// case in `src/ToolUp.AlgorithmProviders.Tests`.

/// Closed-form estimators, one per (family, method) pair this provider
/// serves. Separated from the interface implementation so the
/// arithmetic — the part with no type-checker backstop — reads on its
/// own.
module private MathNetDistributionEstimators =

    /// A fitted family: its named parameters and the log-likelihood of
    /// the sample under them.
    type MathNetFittedDistribution = {
        Parameters: DistributionParameter list
        LogLikelihood: float
    }

    let private id' = MathNetAlgorithmIds.DistributionFit

    let private parameter name value = { Name = name; Value = value }

    /// Sum a log-density / log-mass over the sample.
    let private logLikelihoodOf (values: float[]) (logDensity: float -> float) = values |> Array.sumBy logDensity

    let private requirePositive (values: float[]) (family: DistributionFamily) =
        match values |> Array.tryFind (fun v -> v <= 0.0) with
        | Some v ->
            invalidArguments
                id'
                (sprintf
                    "family '%s' is supported on strictly positive values but the sample contains %g"
                    (DistributionFamily.name family)
                    v)
        | None -> Ok()

    let private requireSpread (scale: float) (family: DistributionFamily) =
        if scale > 0.0 && not (System.Double.IsNaN scale) then
            Ok()
        else
            invalidArguments
                id'
                (sprintf
                    "family '%s' cannot be fitted to a sample with no dispersion — the estimated scale is %g"
                    (DistributionFamily.name family)
                    scale)

    // ── Normal ──────────────────────────────────────────────────────
    //
    // MLE and method-of-moments differ ONLY in the variance denominator
    // (n vs n − 1), which is exactly the kind of difference that is
    // invisible in an output and material in a comparison against
    // another tool. Both are served; which one ran is echoed.

    let normal (values: float[]) (method': EstimationMethod) =
        let mu = mean values

        let sigma =
            match method' with
            | MaximumLikelihood -> sqrt (populationVariance values)
            | MethodOfMoments -> sqrt (sampleVariance values)

        requireSpread sigma NormalFamily
        |> Result.map (fun () -> {
            Parameters = [ parameter "mu" mu; parameter "sigma" sigma ]
            LogLikelihood = logLikelihoodOf values (fun x -> Normal.PDFLn(mu, sigma, x))
        })

    // ── Log-normal ──────────────────────────────────────────────────
    //
    // Here the two estimators genuinely disagree: the MLE fits on the
    // LOG scale, while moment matching solves the raw-scale mean and
    // variance for the log-scale parameters. Same family, same sample,
    // different numbers — and nothing but the echoed `method`
    // distinguishes the two outputs.

    let logNormal (values: float[]) (method': EstimationMethod) =
        requirePositive values LogNormalFamily
        |> Result.bind (fun () ->
            let mu, sigma =
                match method' with
                | MaximumLikelihood ->
                    let logs = values |> Array.map log
                    mean logs, sqrt (populationVariance logs)
                | MethodOfMoments ->
                    // E[X] = exp(mu + sigma^2 / 2);
                    // Var[X] = (exp(sigma^2) - 1) * exp(2mu + sigma^2).
                    let m = mean values
                    let v = sampleVariance values
                    let sigmaSquared = log (1.0 + v / (m * m))
                    log m - sigmaSquared / 2.0, sqrt sigmaSquared

            requireSpread sigma LogNormalFamily
            |> Result.map (fun () -> {
                Parameters = [ parameter "mu" mu; parameter "sigma" sigma ]
                LogLikelihood = logLikelihoodOf values (fun x -> LogNormal.PDFLn(mu, sigma, x))
            }))

    // ── Poisson ─────────────────────────────────────────────────────
    //
    // The sample mean is both the MLE and the first-moment match, so the
    // two estimators COINCIDE. Both are served and reported as asked —
    // that is not a substitution, because there is no second estimator
    // to substitute; the precision contract says so explicitly.

    let poisson (values: float[]) =
        let lambda = mean values

        requireSpread lambda PoissonFamily
        |> Result.map (fun () -> {
            Parameters = [ parameter "lambda" lambda ]
            LogLikelihood = logLikelihoodOf values (fun x -> Poisson.PMFLn(lambda, int x))
        })

    // ── Gamma ───────────────────────────────────────────────────────
    //
    // Method of moments only. The MLE needs an iterative digamma
    // root-find, which this companion does not ship — so a request
    // naming `maximumLikelihood` is REFUSED rather than served by the
    // moment estimator wearing the wrong label.
    //
    // Parameterisation is (shape, RATE), matching Math.NET's `Gamma` —
    // not (shape, scale). The two are reciprocals, and a fit reported
    // under the wrong one is the plausible-but-wrong number the eval
    // measured.

    let gamma (values: float[]) =
        requirePositive values GammaFamily
        |> Result.bind (fun () ->
            let m = mean values
            let v = sampleVariance values

            requireSpread v GammaFamily
            |> Result.map (fun () ->
                let shape = m * m / v
                let rate = m / v

                {
                    Parameters = [ parameter "shape" shape; parameter "rate" rate ]
                    LogLikelihood = logLikelihoodOf values (fun x -> Gamma.PDFLn(shape, rate, x))
                }))

    // ── Negative binomial ───────────────────────────────────────────
    //
    // Method of moments only, for the same reason as gamma. The
    // parameterisation is Math.NET's: `r` failures-before-successes
    // shape and `p` the SUCCESS probability, with mean r(1 − p)/p and
    // variance r(1 − p)/p². Solving those two for (r, p) gives
    // p = m/v and r = m²/(v − m) — which requires v > m, i.e. genuine
    // overdispersion. An equidispersed or underdispersed sample is a
    // Poisson (or worse) wearing a negative-binomial request, and is
    // refused with the two moments quoted rather than fitted to a
    // negative shape.

    let negativeBinomial (values: float[]) =
        let m = mean values
        let v = sampleVariance values

        if System.Double.IsNaN v || v <= m then
            invalidArguments
                id'
                (sprintf
                    "the negative-binomial moment estimator requires overdispersion (sample variance > mean) but the sample has mean %g and variance %g — an equidispersed count sample is a Poisson fit, not a negative-binomial one"
                    m
                    v)
        else
            let p = m / v
            let r = m * m / (v - m)

            Ok {
                Parameters = [ parameter "r" r; parameter "p" p ]
                LogLikelihood = logLikelihoodOf values (fun x -> NegativeBinomial.PMFLn(r, p, int x))
            }

/// `IDistributionFitter` backed by Math.NET Numerics' distribution
/// log-densities plus this companion's own closed-form estimators.
type MathNetDistributionFitter() =

    /// The estimator this provider runs when the request leaves
    /// `Method = None`. Reported on the result either way — a caller
    /// that did not choose still learns what was chosen.
    static member DefaultMethodFor(family: DistributionFamily) : EstimationMethod =
        match family with
        | NormalFamily
        | LogNormalFamily
        | PoissonFamily -> MaximumLikelihood
        | GammaFamily
        | NegativeBinomialFamily -> MethodOfMoments

    /// `true` when this provider can serve `family` under `method`.
    /// Gamma and negative-binomial are moment-only; everything else
    /// serves both.
    static member Serves(family: DistributionFamily, method': EstimationMethod) : bool =
        match family, method' with
        | GammaFamily, MaximumLikelihood
        | NegativeBinomialFamily, MaximumLikelihood -> false
        | _ -> true

    interface IDistributionFitter with

        member _.Fit request = async {
            match AlgorithmValidation.distribution MathNetAlgorithmIds.DistributionFit request with
            | Error e -> return Error e
            | Ok() ->
                let method' =
                    request.Method
                    |> Option.defaultValue (MathNetDistributionFitter.DefaultMethodFor request.Family)

                if not (MathNetDistributionFitter.Serves(request.Family, method')) then
                    // The obligation: refuse, naming both the family and
                    // the method, rather than quietly running the
                    // estimator we do have.
                    return
                        unsupported
                            MathNetAlgorithmIds.DistributionFit
                            (sprintf
                                "family '%s' is fitted by method of moments only — its maximum-likelihood estimator needs an iterative digamma root-find this provider does not ship. Request '%s', or choose a family whose MLE is closed-form."
                                (DistributionFamily.name request.Family)
                                (EstimationMethod.name MethodOfMoments))
                else
                    let fitted =
                        match request.Family with
                        | NormalFamily -> MathNetDistributionEstimators.normal request.Values method'
                        | LogNormalFamily -> MathNetDistributionEstimators.logNormal request.Values method'
                        | PoissonFamily -> MathNetDistributionEstimators.poisson request.Values
                        | GammaFamily -> MathNetDistributionEstimators.gamma request.Values
                        | NegativeBinomialFamily -> MathNetDistributionEstimators.negativeBinomial request.Values

                    return
                        fitted
                        |> Result.map (fun f ->
                            let parameterCount = List.length f.Parameters
                            let observations = request.Values.Length

                            {
                                Family = request.Family
                                // The contract (`IDistributionFitter`):
                                // report the estimator that actually ran.
                                Method = method'
                                Parameters = f.Parameters
                                LogLikelihood = f.LogLikelihood
                                Aic = DistributionFitResult.aicOf parameterCount f.LogLikelihood
                                Bic = DistributionFitResult.bicOf parameterCount observations f.LogLikelihood
                                Observations = observations
                            })
        }
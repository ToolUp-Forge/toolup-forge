// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AlgorithmProviders

open MathNet.Numerics.Statistics
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.3 — Math.NET provider: identity + shared helpers ─────
//
// The vendor boundary (GP 1). Everything below `ToolUp.Algorithms.*` in
// the dependency graph is Math.NET Numerics; nothing above it names the
// library. This file carries the provider's stable identity, the
// algorithm ids it declares, and the handful of numeric helpers the four
// fitters share.
//
// **Server-tier only.** Nothing here is packed under `fable/` and
// nothing in this companion is Fable-compiled — Math.NET is a .NET
// numerics library, and the catalog's Fable-safe tier is
// `ToolUp.Algorithms.Core`, which this companion consumes but never
// contributes to.
//
// **Why the namespace is `ToolUp.AlgorithmProviders` rather than
// `…AlgorithmProviders.MathNet`.** A namespace whose leaf is `MathNet`
// puts a sibling of that name in scope at every call site, so a
// fully-qualified `MathNet.Numerics.…` reference inside the companion
// would resolve ambiguously. The package id keeps the `.MathNet` suffix;
// the F# namespace deliberately does not.

/// The `AlgorithmId`s this provider declares. They are the canonical ids
/// of the four curated operations — a deployment composes exactly one
/// provider per id, and the registry refuses a second claim on any of
/// them (see `AlgorithmProviderRegistry`).
module MathNetAlgorithmIds =

    [<Literal>]
    let Regression = "regression.linear"

    [<Literal>]
    let Describe = "stats.describe"

    [<Literal>]
    let DistributionFit = "distribution.fit"

    [<Literal>]
    let Smooth = "timeseries.smooth"

/// Provider identity and the numeric helpers shared across the four
/// fitters. Every helper here is pure and total for the inputs the
/// shared `AlgorithmValidation` admits.
module MathNetAlgorithmSupport =

    /// Stable provider discriminator. Stamped onto every declaration by
    /// `AlgorithmProviderRegistry` and named in every refusal, so an
    /// operator reading an `Unsupported` knows which companion to look
    /// at.
    [<Literal>]
    let ProviderId = "mathnet"

    /// This companion's own version, stamped onto every declaration.
    /// Tracks the coordinated `ToolUp.Sdk` meta-release.
    [<Literal>]
    let ProviderVersion = "0.11.0"

    /// The Math.NET Numerics release the bindings are written against.
    /// Quoted in every `PrecisionContract`, because a precision claim
    /// that does not name the implementation it was measured on is not
    /// a contract.
    [<Literal>]
    let VendorVersion = "5.0"

    /// A typed refusal naming this provider. The obligation the eval
    /// pinned: a family, method or kind this companion cannot serve
    /// comes back as data — never an exception, never a silent
    /// substitution of the estimator it *does* have.
    let unsupported (algorithmId: AlgorithmId) (reason: string) : Result<'T, AlgorithmError> =
        Error(AlgorithmError.Unsupported(algorithmId, ProviderId, reason))

    /// A typed malformed-request refusal. Used for the conditions the
    /// shared validation cannot see — a non-positive value under a
    /// log-scale family, an under-dispersed sample under a
    /// negative-binomial fit — which are properties of the DATA, not of
    /// the request shape.
    let invalidArguments (algorithmId: AlgorithmId) (detail: string) : Result<'T, AlgorithmError> =
        Error(AlgorithmError.InvalidArguments(algorithmId, detail))

    /// Arithmetic mean, via Math.NET.
    let mean (xs: float[]) : float = Statistics.Mean xs

    /// Sample variance — the **n − 1** denominator. The estimator every
    /// spreadsheet's `VAR` and every `describe()` reports, and the one
    /// this companion's method-of-moments estimators match.
    let sampleVariance (xs: float[]) : float = Statistics.Variance xs

    /// Population variance — the **n** denominator. The maximum-
    /// likelihood estimate of a normal's variance, which is why it is a
    /// separate helper rather than a flag: the two differ, and which one
    /// ran is the `method` field the catalog echoes.
    let populationVariance (xs: float[]) : float = Statistics.PopulationVariance xs

    /// The Math.NET quantile definition for a catalog convention.
    ///
    /// **This mapping is the whole point of the companion.** Math.NET's
    /// bare `Quantile` *and* its `Percentile` both compute R-8, under
    /// names that read as spreadsheet-compatible; the R-7 a caller
    /// comparing against Excel actually wants is only reachable through
    /// `QuantileCustom` with an explicit definition. Routing every
    /// quantile through here makes the wrong one unreachable.
    let quantileDefinitionOf (convention: QuantileConvention) : QuantileDefinition =
        match convention with
        | ExcelCompatible -> QuantileDefinition.R7
        | MedianUnbiased -> QuantileDefinition.R8

    /// Estimate one quantile of an already-sorted sample under a
    /// catalog convention.
    let quantileOfSorted (convention: QuantileConvention) (sorted: float[]) (probability: float) : float =
        SortedArrayStatistics.QuantileCustom(sorted, probability, quantileDefinitionOf convention)
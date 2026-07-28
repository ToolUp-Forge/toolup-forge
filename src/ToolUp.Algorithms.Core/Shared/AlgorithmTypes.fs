// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmTypes

// ─── Phase 11.E.2 — algorithm catalog core types ────────────────────
//
// The declaration vocabulary of the analytical-primitive catalog: what
// an algorithm IS, what it takes, and how a request for one can fail.
// Zero vendor dependency — this tier names no numerics library, and no
// provider type appears in it (GP 1).
//
// **Fable-safe.** These types are packed under `fable/` for client
// consumers, so nothing here touches reflection, `System.Text.Json`, or
// any BCL surface Fable cannot compile.
//
// **Why a catalog at all.** The pre-build eval
// (`evals/algorithms-primitives-eval/`) measured a code assistant
// writing raw-numerics F# against the same five tasks a curated catalog
// would serve. The measured value was almost never the arithmetic — the
// raw library computed correct numbers wherever it had a surface. What
// it does not do is state WHICH convention it used, and four fields
// carry essentially the whole delta: the quantile convention, the
// smoothing alignment, the estimation method, and the categorical
// reference level. Every one of them is an explicit, echoed field in
// `AlgorithmOperations.fs` for exactly that reason.

/// Stable identity of one catalogued algorithm, e.g.
/// `"regression.linear"`. Identity by value (GP 12 rule 1) — never a
/// live handle to a fitter. Convention: `{kind}.{operation}`, lower
/// case, dot-separated; the catalog does not enforce the shape, but
/// every algorithm the SDK ships follows it.
type AlgorithmId = string

/// Coarse classification a caller can filter the catalog by. One kind
/// per curated interface (see `IAlgorithmFitters.fs`) — the eval that
/// selected the interface set therefore also selected this DU, and a
/// new kind arrives only with a new measured interface.
type AlgorithmKind =
    /// Linear least-squares fitting — `IRegressionFitter`.
    | Regression
    /// Univariate summary statistics — `IDescriptiveStats`.
    | DescriptiveStatistics
    /// Parametric distribution fitting — `IDistributionFitter`.
    | DistributionFit
    /// Series smoothing / filtering — `ITimeSeriesFilter`.
    | TimeSeriesSmoothing

module AlgorithmKind =

    /// Stable wire string. Round-trips through `parse`; used in AI tool
    /// payloads, catalog JSON, and `/dev/inspect` output, so it is part
    /// of the package's public contract — do not re-spell an existing
    /// case.
    let name =
        function
        | Regression -> "regression"
        | DescriptiveStatistics -> "descriptiveStatistics"
        | DistributionFit -> "distributionFit"
        | TimeSeriesSmoothing -> "timeSeriesSmoothing"

    /// Inverse of `name`. `None` for an unknown tag — an unrecognised
    /// kind is data, never an exception.
    let parse =
        function
        | "regression" -> Some Regression
        | "descriptiveStatistics" -> Some DescriptiveStatistics
        | "distributionFit" -> Some DistributionFit
        | "timeSeriesSmoothing" -> Some TimeSeriesSmoothing
        | _ -> None

    /// Every kind, in declaration order. The catalog's `ListByKind`
    /// caller uses this to enumerate the filterable set without
    /// reflection (Fable-safe).
    let all = [ Regression; DescriptiveStatistics; DistributionFit; TimeSeriesSmoothing ]

/// Value type of one declared algorithm parameter. Deliberately a small
/// closed set rather than a JSON-Schema fragment: the catalog's
/// parameters are the arguments of four typed request records, not an
/// open schema language.
type AlgorithmParameterType =
    | NumberParameter
    | NumberArrayParameter
    | StringParameter
    | StringArrayParameter
    | BooleanParameter
    /// A nested object or an array of objects — e.g. the
    /// `{ name, values }` predictor rows of a regression request. The
    /// shape is described in prose on the parameter's `Description`;
    /// the catalog does not carry a nested schema.
    | ObjectParameter

module AlgorithmParameterType =

    /// Stable wire string.
    let name =
        function
        | NumberParameter -> "number"
        | NumberArrayParameter -> "numberArray"
        | StringParameter -> "string"
        | StringArrayParameter -> "stringArray"
        | BooleanParameter -> "boolean"
        | ObjectParameter -> "object"

    /// The JSON-Schema primitive an AI tool definition advertises for
    /// this parameter. Several catalog types collapse onto `"array"` /
    /// `"object"` — the finer distinction stays in the parameter's
    /// `Description`, which is what the model actually reads.
    let jsonTypeName =
        function
        | NumberParameter -> "number"
        | NumberArrayParameter -> "array"
        | StringParameter -> "string"
        | StringArrayParameter -> "array"
        | BooleanParameter -> "boolean"
        | ObjectParameter -> "object"

/// One declared parameter of a catalogued algorithm. Mirrors the shape
/// of `ToolUp.Platform.ToolParameterSchema` deliberately — the AI tool
/// surface projects one onto the other — but stays a distinct type so
/// the algorithms catalog does not take a dependency on the AI tool
/// vocabulary, and so the catalog can carry parameter types the AI
/// schema has no word for.
type AlgorithmParameterSpec = {
    Name: string
    Type: AlgorithmParameterType
    Description: string
    Required: bool
    /// JSON-encoded default, as a string, for optional parameters.
    /// `None` for required parameters. A string default is itself
    /// JSON-quoted (`Some "\"excelCompatible\""`).
    Default: string option
}

module AlgorithmParameterSpec =

    /// A required parameter.
    let required (name: string) (ty: AlgorithmParameterType) (description: string) : AlgorithmParameterSpec = {
        Name = name
        Type = ty
        Description = description
        Required = true
        Default = None
    }

    /// An optional parameter with a JSON-encoded default.
    let optional
        (name: string)
        (ty: AlgorithmParameterType)
        (description: string)
        (defaultJson: string)
        : AlgorithmParameterSpec =
        {
            Name = name
            Type = ty
            Description = description
            Required = false
            Default = Some defaultJson
        }

/// A catalogued algorithm's public declaration. Everything a caller —
/// human or model — needs to decide whether to invoke it, and nothing
/// that ties it to a particular implementation beyond the provider
/// stamp.
///
/// **`ProviderId` / `ProviderVersion` are stamped, not declared.** A
/// provider authors this record without them (or with anything at all
/// in them); `AlgorithmProviderRegistry` overwrites both from the
/// registering provider at compose time, so the catalog can never
/// report a provenance that disagrees with the provider that will
/// actually execute the call.
type AlgorithmInfo = {
    Id: AlgorithmId
    /// Short human label, e.g. `"Linear regression"`.
    DisplayName: string
    Kind: AlgorithmKind
    /// What the algorithm computes, in prose. This is the text an AI
    /// tool definition surfaces to the model, so it should state any
    /// convention the result echoes (which quantile definition is the
    /// default, what the reference level means, and so on).
    Description: string
    Parameters: AlgorithmParameterSpec list
    /// Prose description of the returned shape. The typed result record
    /// is the contract; this is the model-readable gloss on it.
    ReturnsDescription: string
    /// GP 12 rule 6 — the precision contract. States what the caller
    /// may rely on numerically: determinism, tolerance, and any
    /// convention that is implementation-defined. Stamped by the
    /// provider's declaration and surfaced verbatim in the catalog, so
    /// a deployment swapping providers can diff the contracts.
    PrecisionContract: string
    /// Stamped by the registry from the registering provider.
    ProviderId: string
    /// Stamped by the registry from the registering provider.
    ProviderVersion: string
}

module AlgorithmInfo =

    /// Author a declaration without provider provenance — the registry
    /// stamps `ProviderId` / `ProviderVersion` at registration. This is
    /// the constructor a provider companion uses.
    let declare
        (id: AlgorithmId)
        (displayName: string)
        (kind: AlgorithmKind)
        (description: string)
        (parameters: AlgorithmParameterSpec list)
        (returnsDescription: string)
        (precisionContract: string)
        : AlgorithmInfo =
        {
            Id = id
            DisplayName = displayName
            Kind = kind
            Description = description
            Parameters = parameters
            ReturnsDescription = returnsDescription
            PrecisionContract = precisionContract
            ProviderId = ""
            ProviderVersion = ""
        }

    /// Stamp provider provenance onto a declaration. Applied by
    /// `AlgorithmProviderRegistry` at compose time.
    let withProvider (providerId: string) (providerVersion: string) (info: AlgorithmInfo) : AlgorithmInfo = {
        info with
            ProviderId = providerId
            ProviderVersion = providerVersion
    }

/// Typed failure of an algorithm request. Every failure mode is data —
/// a provider never signals refusal by raising, and the dispatcher
/// converts an escaped exception into `ExecutionFailed` rather than
/// letting it reach the caller (GP 12 rule 3: behaviour as data).
[<RequireQualifiedAccess>]
type AlgorithmError =
    /// No provider declared this id. Terminal — retrying will not
    /// register one.
    | NotFound of algorithmId: AlgorithmId
    /// A provider declared the id but cannot serve this particular
    /// request — e.g. a distribution family it has no estimator for, or
    /// a smoothing kind it does not implement. Names the provider so
    /// the operator knows which companion to look at.
    | Unsupported of algorithmId: AlgorithmId * providerId: string * reason: string
    /// The request is malformed — mismatched vector lengths, an empty
    /// sample, a non-positive window. Checked before dispatch where
    /// possible (see `AlgorithmOperations`), so the caller gets a
    /// specific diagnostic rather than a numerics-library exception.
    | InvalidArguments of algorithmId: AlgorithmId * detail: string
    /// The provider's execution raised or otherwise failed. Message is
    /// the provider's; treat as potentially transient.
    | ExecutionFailed of algorithmId: AlgorithmId * message: string
    /// The invocation's payload does not match the algorithm's declared
    /// kind — e.g. a descriptive-statistics request routed to
    /// `regression.linear`. A wiring defect, surfaced as data.
    | KindMismatch of algorithmId: AlgorithmId * expected: string * supplied: string

module AlgorithmError =

    /// Human-readable one-line description for logs, job results, and
    /// AI tool error payloads.
    let describe =
        function
        | AlgorithmError.NotFound id -> sprintf "no algorithm registered with id '%s'" id
        | AlgorithmError.Unsupported(id, provider, reason) ->
            sprintf "provider '%s' cannot serve '%s': %s" provider id reason
        | AlgorithmError.InvalidArguments(id, detail) -> sprintf "invalid arguments for '%s': %s" id detail
        | AlgorithmError.ExecutionFailed(id, message) -> sprintf "'%s' failed: %s" id message
        | AlgorithmError.KindMismatch(id, expected, supplied) ->
            sprintf "'%s' expects a %s invocation but a %s invocation was supplied" id expected supplied

    /// Stable machine tag, for AI tool payloads and telemetry. The
    /// human text rides `describe`; this is what a caller branches on.
    let tag =
        function
        | AlgorithmError.NotFound _ -> "notFound"
        | AlgorithmError.Unsupported _ -> "unsupported"
        | AlgorithmError.InvalidArguments _ -> "invalidArguments"
        | AlgorithmError.ExecutionFailed _ -> "executionFailed"
        | AlgorithmError.KindMismatch _ -> "kindMismatch"
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open ToolUp.Platform
open ToolUp.InterPlatform.PeerAuditEvents
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── Phase 638 — the federated model-execution profile ───────────────
//
// The model-execution submitter surface answers one question: "fit this
// spec against that vintage, and give me back what the fit produced".
// In a single deployment the answer is uninteresting, because the specs
// and the data are already on the same machine. The interesting topology
// is the one where they are not: one deployment holds the datasets and
// runs the fits, another authors the specs and submits them, and the raw
// series never crosses between them.
//
// This file is the forge binding for that topology — the peer-contract
// registrations that put the submitter wire objects over InterPlatform
// RPC, in the shape `docs/interplatform/FEDERATION_WIRE.md` §5.7
// specifies. There are two contracts, and the split is the whole design:
//
//   * **The submitter contract** (`ModelExecutionProfile.ContractId`)
//     carries submission, outcome retrieval, registry query and vintage
//     resolution. Every one of them is **metadata-shaped**: a hash, a
//     status, a diagnostic scalar, a row COUNT. None of them can return
//     a row, because none of them has a shape a row could be put in.
//   * **The governed-diagnostics contract**
//     (`ModelExecutionProfile.DiagnosticsContractId`) carries the
//     aggregate projections a remote modeller needs in order to model
//     responsibly without eyeballing the raw series — collinearity,
//     coverage, transform previews, and the binned fit diagnostics a
//     modeller judges a fit by. Those answers ARE derived from rows,
//     so they do not travel on a bare registration at all: the only
//     construction route is `governedDiagnostics`, whose signature
//     demands an `ICleanRoomBroker` and a `CleanRoomTemplate` and whose
//     return type is `GatedContractRegistration`. A value of that type
//     is evidence the clean-room gate is installed (see
//     `CleanRoomGate.fs`); there is no ungated variant to reach for.
//
// ── Why "closed against row egress" is a fact and not a promise ──
//
// Three independent things have to be true at once, and each is checked
// by something other than a reviewer's attention:
//
//   1. **No row-shaped method exists.** The contracts' methods are
//      enumerated below; `ReadPage`-class access is not among them, and
//      `admit` refuses a request naming one with a distinct refusal
//      class (`RowAccessRefused`) rather than a generic "unknown
//      method", so an operator reading a refusal log can tell a probe
//      from a typo.
//   2. **A diagnostic answer must clear the floor.** Every gated answer
//      goes through the gate's checkability invariant, which withholds
//      anything that is not a `CohortResult`. A projection that tried to
//      smuggle rows back — as a list, a string, a record of some other
//      shape — does not bypass the gate, it FAILS it.
//   3. **Only declared diagnostics are answerable.** The template's
//      `AllowedMethods` is the declaration, and the gate's surface
//      invariant refuses anything off it *before the handler runs*. So
//      the family is extensible by declaration and closed by default:
//      adding a projection means declaring it, and a deployment that
//      declares none answers none.
//
// ── Admission is a wrapper, not a call the handler makes ──
//
// `registration` builds the dispatch closure itself rather than
// reflecting a record of functions, for the same reason `CleanRoomGate`
// wraps a dispatch rather than asking handlers to call a broker: the
// dispatch closure is the only route from a handler's answer to the
// wire, and it is the only place the VALIDATED call context is
// available. Resolving the peer binding, refusing an unbound peer,
// refusing a scope-widening assertion and refusing an off-surface
// operation therefore all happen where they cannot be forgotten, and a
// handler that never thinks about any of it is still governed by all of
// it.
//
// ── SpecHash opacity, carried through unchanged ──
//
// The submission carries the submitter's own `SpecHash` beside the
// opaque payload, and this binding stores and keys exactly the hash it
// was handed (`ModelExecutionWire.fs`'s posture). It never re-derives,
// re-normalises or validates it against the payload — across a peer
// boundary that would be worse than pointless, because the two sides do
// not share a canonicalisation rule and the whole point of the hash is
// that the submitter's rule is the one that counts.
//
// ── Cost when unused (GP 13) ──
//
// Nothing here is composed by `PeerCompose.withPeer`. A deployment that
// never calls `host` or `governedDiagnostics` constructs none of these
// types, registers no contract, and schedules no job.

// ─── Wire shapes (FEDERATION_WIRE.md §5.7) ───────────────────────────

/// A vintage of a dataset, named **scope-relatively**. There is no scope
/// member: the data host resolves the scope from the peer binding, so a
/// reference cannot address another tenant's data by construction
/// (GP 4). A modeller that wants to state which scope it BELIEVES it is
/// addressing does so on the request envelope, where it is checked and
/// refused rather than routed on.
type ModelExecutionPeerVintage = { DatasetId: string; Version: int }

/// One requested gate, with the direction as its stable case-name string
/// (`"AtLeast"` / `"AtMost"`) exactly as the submitter surface carries
/// it.
type ModelExecutionPeerGate = {
    Name: string
    Threshold: float
    Direction: string
}

/// A fit submission as it crosses the seam: which vintage, the opaque
/// provider spec and the submitter-minted hash of it, the provider kind,
/// the reproducibility seed, and the gates to evaluate.
type ModelExecutionPeerSubmission = {
    Vintage: ModelExecutionPeerVintage
    /// Opaque provider payload — neither side inspects it.
    SpecPayload: string
    /// Submitter-minted. Stored and keyed verbatim; never re-derived.
    SpecHash: string
    ProviderKind: string
    Seed: int64
    /// Sorted ordinally by `Name`.
    Gates: ModelExecutionPeerGate list
    /// Phase 451 (interface-plan decision D5) — `SubmitterClass.label` of
    /// whoever asked for this fit: `"human"` / `"scheduled"` / `"agent"`.
    ///
    /// A **string** rather than the DU, like every other field on this
    /// contract: the peer wire is language-neutral JSON-RPC and the caller
    /// is very often not an F# deployment, so a closed vocabulary of
    /// stable lowercase labels is the shape a shell script can emit. An
    /// unrecognised or absent value reads as `"human"`
    /// (`SubmitterClass.ofLabelOrHuman`), so an older peer that predates
    /// this field submits exactly as it always did.
    ///
    /// **It is the submitter's own claim, and the receiving deployment
    /// treats it as one.** Forge never infers a class and never
    /// cross-checks this against the peer's identity — a peer that wants
    /// its agent traffic budgeted as agent traffic says so. A deployment
    /// unwilling to take a given peer's word pins the class at its own
    /// binding rather than trusting the field, which is the same posture
    /// `ModelExecutionAdmission` already takes for declared operations.
    SubmitterClass: string
}

/// One gate's verdict, as data.
type ModelExecutionPeerGateVerdict = {
    Name: string
    Threshold: float
    Direction: string
    Observed: float
    Passed: bool
}

/// A registered fit outcome. Every member is metadata or an aggregate
/// scalar — there is deliberately no member a row could be carried in.
type ModelExecutionPeerOutcome = {
    CompositeKeyHash: string
    SpecHash: string
    DatasetVersion: string
    Seed: int64
    ProviderId: string
    ProviderVersion: string
    ArtifactId: string
    ArtifactContentHash: string
    /// Keys sorted ordinally (§3.1 rule 14).
    Diagnostics: Map<string, float>
    /// Sorted ordinally by `Name`.
    GateVerdicts: ModelExecutionPeerGateVerdict list
    Status: string
    /// Keys sorted ordinally.
    Annotations: Map<string, string>
    RegisteredAt: DateTimeOffset
}

/// A conjunctive multi-key outcome filter plus its page window. An empty
/// list matches anything; the lists are sorted ordinally so the same
/// query canonicalises to the same bytes whoever built it.
type ModelExecutionPeerQuery = {
    SpecHashes: string list
    DatasetVersions: string list
    Statuses: string list
    BatchId: string option
    Cursor: string option
    Limit: int
}

/// One page of a cursor-paginated outcome read.
type ModelExecutionPeerPage = {
    Outcomes: ModelExecutionPeerOutcome list
    NextCursor: string option
}

/// A resolved vintage: enough for a modeller to pin what it is fitting
/// against, and nothing more. `RowCount` is a cohort size, not a row.
type ModelExecutionPeerVintageInfo = {
    DatasetId: string
    Version: int
    RowCount: int64
    Format: string
    ContentHash: string
    CreatedAt: DateTimeOffset
}

/// A governed-diagnostic request: which vintage, and which terms of it
/// the projection is over. The diagnostic itself is the OPERATION, not a
/// member — which is what lets the clean-room template's declared method
/// surface be the declaration of what this deployment answers.
type ModelExecutionPeerDiagnosticRequest = {
    Vintage: ModelExecutionPeerVintage
    /// Sorted ordinally.
    Terms: string list
}

/// A fit-diagnostic request: which stored outcome the diagnostic is over,
/// and nothing else. The outcome is one this peer's `SubmitFit` already
/// produced, so the row set is the fit's, never one the call selects; and
/// the binning is the approved template's, never the call's. A body
/// carrying any other member is refused, by name, before anything is
/// computed.
type ModelExecutionPeerFitDiagnosticRequest = {
    /// The outcome's `CompositeKeyHash`.
    OutcomeId: string
}

// ─── Fit diagnostics: the template's binning, and the projections ────
//
// A remote modeller judging a fit needs to see how it fits: residuals,
// calibration, threshold curves. Each of those, drawn raw, is the rows
// again, so each is released here as a binned aggregate the clean-room
// gate checks like any other. Two things make the binning safe to rely
// on. The edges are the TEMPLATE's (a call carries none), so a caller
// cannot place two edges either side of one record. And every edge and
// every released value is rounded to the template's declared precision,
// because an unrounded quantile or domain end is an observed value, which
// is a record.

/// How a fit diagnostic's bin edges are placed.
[<RequireQualifiedAccess>]
type FitDiagnosticBins =
    /// `count` equal-width bins over the observed range, its ends rounded
    /// outward to the declaration's precision and each edge rounded to it.
    | FixedWidth of count: int
    /// `count` bins whose edges are the observed quantiles, each rounded
    /// to the declaration's precision.
    | Quantiles of count: int
    /// Edges the template fixes, strictly increasing and on the
    /// precision's lattice, with an open-ended bin below the first and
    /// another above the last, so no extreme is located.
    | Edges of edges: decimal list

/// What a template declares for one fit diagnostic. Which members a
/// diagnostic reads, and which it refuses, is `FitDiagnostics.check`.
type FitDiagnosticDeclaration = {
    /// The bins the diagnostic's axis is cut into.
    Bins: FitDiagnosticBins option
    /// `QuantileComparison`'s probabilities, strictly inside (0, 1), or
    /// `ThresholdCurve`'s threshold grid. Strictly increasing.
    Points: decimal list
    /// Every released value, and every computed edge, is rounded to this.
    Precision: decimal
    /// `InfluenceSummary`: the absolute standardised residual above which
    /// a point is counted.
    ResidualThreshold: decimal option
    /// `InfluenceSummary`: the leverage above which a point is counted.
    LeverageThreshold: decimal option
}

/// One observation of a stored fit, as the data host reads it back. It
/// never leaves the data host: the projections bin it, and the gate
/// checks what they produce.
type ModelExecutionFitObservation = {
    /// The fitted value (for a classifier, the predicted probability).
    Fitted: float
    /// Observed minus fitted.
    Residual: float
    /// The residual over the fit's estimated scale, as the fitter computes
    /// it (studentised where it can).
    Standardised: float
    /// The point's leverage, where the fitter reports one.
    Leverage: float option
}

/// The canonical text of a `FitDiagnosticDeclaration`: the bytes a
/// template's release terms carry for one fit diagnostic.
[<RequireQualifiedAccess>]
module FitDiagnosticDeclaration =

    let private invariant = Globalization.CultureInfo.InvariantCulture

    /// A decimal in its shortest invariant form, so one value has one
    /// spelling in a template and in a label.
    let number (d: decimal) : string =
        d.ToString("0.############################", invariant)

    let private tryNumber (s: string) : decimal option =
        match
            Decimal.TryParse(
                s,
                Globalization.NumberStyles.AllowLeadingSign
                ||| Globalization.NumberStyles.AllowDecimalPoint,
                invariant
            )
        with
        | true, d -> Some d
        | _ -> None

    let private numbers (ds: decimal list) =
        ds |> List.map number |> String.concat ","

    /// The declaration's canonical text: the bytes the template's release
    /// terms carry, and so the bytes its content hash covers.
    let render (declaration: FitDiagnosticDeclaration) : string =
        [
            match declaration.Bins with
            | Some(FitDiagnosticBins.FixedWidth count) -> $"bins=fixed:{count}"
            | Some(FitDiagnosticBins.Quantiles count) -> $"bins=quantiles:{count}"
            | Some(FitDiagnosticBins.Edges edges) -> $"bins=edges:{numbers edges}"
            | None -> ()
            if not declaration.Points.IsEmpty then
                $"points={numbers declaration.Points}"
            $"precision={number declaration.Precision}"
            match declaration.ResidualThreshold with
            | Some r -> $"residual={number r}"
            | None -> ()
            match declaration.LeverageThreshold with
            | Some l -> $"leverage={number l}"
            | None -> ()
        ]
        |> String.concat ";"

    /// Read a declaration from its canonical text. Text that is not
    /// exactly the canonical rendering of what it parses to is refused:
    /// two spellings of one declaration would hash as two templates.
    let parse (text: string) : Result<FitDiagnosticDeclaration, string> =
        let list (s: string) =
            let parts = s.Split(',') |> Array.map tryNumber

            if parts |> Array.forall Option.isSome then
                Some(parts |> Array.choose id |> Array.toList)
            else
                None

        let segments =
            (if isNull text then "" else text).Split(';')
            |> Array.map (fun segment ->
                match segment.IndexOf '=' with
                | i when i > 0 -> Some(segment.Substring(0, i), segment.Substring(i + 1))
                | _ -> None)

        if segments |> Array.exists Option.isNone then
            Error $"'{text}' is not a fit-diagnostic declaration: every segment is key=value"
        else
            let pairs = segments |> Array.choose id |> Map.ofArray

            let bins =
                match Map.tryFind "bins" pairs with
                | None -> Ok None
                | Some v when v.StartsWith "fixed:" ->
                    match Int32.TryParse(v.Substring 6, Globalization.NumberStyles.None, invariant) with
                    | true, n -> Ok(Some(FitDiagnosticBins.FixedWidth n))
                    | _ -> Error $"bins '{v}' does not name a bin count"
                | Some v when v.StartsWith "quantiles:" ->
                    match Int32.TryParse(v.Substring 10, Globalization.NumberStyles.None, invariant) with
                    | true, n -> Ok(Some(FitDiagnosticBins.Quantiles n))
                    | _ -> Error $"bins '{v}' does not name a bin count"
                | Some v when v.StartsWith "edges:" ->
                    match list (v.Substring 6) with
                    | Some edges -> Ok(Some(FitDiagnosticBins.Edges edges))
                    | None -> Error $"bins '{v}' does not list numeric edges"
                | Some v -> Error $"bins '{v}' is not fixed:, quantiles: or edges:"

            let optional key =
                match Map.tryFind key pairs with
                | None -> Ok None
                | Some v ->
                    match tryNumber v with
                    | Some d -> Ok(Some d)
                    | None -> Error $"{key} '{v}' is not a number"

            let points =
                match Map.tryFind "points" pairs with
                | None -> Ok []
                | Some v ->
                    match list v with
                    | Some ds -> Ok ds
                    | None -> Error $"points '{v}' is not a list of numbers"

            match bins, points, optional "precision", optional "residual", optional "leverage" with
            | Ok bins, Ok points, Ok(Some precision), Ok residual, Ok leverage ->
                let declaration = {
                    Bins = bins
                    Points = points
                    Precision = precision
                    ResidualThreshold = residual
                    LeverageThreshold = leverage
                }

                if render declaration = text then
                    Ok declaration
                else
                    Error $"'{text}' is not in canonical form; its canonical spelling is '{render declaration}'"
            | Ok _, Ok _, Ok None, _, _ -> Error $"'{text}' declares no precision"
            | Error e, _, _, _, _
            | _, Error e, _, _, _
            | _, _, Error e, _, _
            | _, _, _, Error e, _
            | _, _, _, _, Error e -> Error e

/// The six fit diagnostics, computed on the data host over a stored
/// outcome's observations and answered as a `CohortResult` for the gate.
///
/// **Three states a cell can be in, and the projection keeps them apart.**
/// A bin with no rows is ABSENT and is not emitted. A bin with rows is
/// emitted with its count, and the gate either releases it or, below the
/// suppression threshold, WITHHOLDS it and names its label to the caller.
/// Emitting an empty bin would make the gate name it as withheld, which
/// says "between 1 and k−1" where the truth is "none".
///
/// **The whole-result floor binds on the distinct cohort.** Several of
/// these answer more than one statistic over the same rows (a bin's mean
/// prediction and its observed rate), and the gate's floor sums cell
/// counts, which would count those rows twice. So the projection checks
/// the floor itself on the distinct rows behind the cells that would
/// clear suppression, and answers no cells when they fall short.
[<RequireQualifiedAccess>]
module FitDiagnostics =

    /// Counts per standardised-residual bin, over the template's declared
    /// edges, with open-ended outer bins.
    [<Literal>]
    let ResidualHistogram = "ResidualHistogram"

    /// The mean residual per fitted-value bin.
    [<Literal>]
    let ResidualSummaryByBin = "ResidualSummaryByBin"

    /// Interior quantiles of the standardised residuals, at the template's
    /// declared probabilities.
    [<Literal>]
    let QuantileComparison = "QuantileComparison"

    /// The mean prediction and the observed rate per predicted-value bin.
    [<Literal>]
    let Calibration = "Calibration"

    /// True- and false-positive rates on the template's threshold grid.
    [<Literal>]
    let ThresholdCurve = "ThresholdCurve"

    /// Per fitted-value bin, how many points lie beyond the declared
    /// standardised-residual and leverage thresholds.
    [<Literal>]
    let InfluenceSummary = "InfluenceSummary"

    /// The fit diagnostics the profile defines.
    let all: Set<string> =
        Set.ofList [
            ResidualHistogram
            ResidualSummaryByBin
            QuantileComparison
            Calibration
            ThresholdCurve
            InfluenceSummary
        ]

    let private increasing (ds: decimal list) =
        ds |> List.pairwise |> List.forall (fun (a, b) -> a < b)

    let private onLattice (precision: decimal) (d: decimal) = d % precision = 0m

    /// Is `declaration` a complete, well-formed declaration for
    /// `diagnostic`? A member the diagnostic does not read is refused
    /// rather than ignored: a template that appears to declare something
    /// it does not apply is a lie an approver would read.
    let check (diagnostic: string) (declaration: FitDiagnosticDeclaration) : Result<unit, string> =
        let binsWellFormed () =
            match declaration.Bins with
            | Some(FitDiagnosticBins.FixedWidth n)
            | Some(FitDiagnosticBins.Quantiles n) when n < 1 || n > 1000 ->
                Error $"a bin count must be between 1 and 1000, not {n}"
            | Some(FitDiagnosticBins.Edges edges) when edges.IsEmpty -> Error "an edge list must name at least one edge"
            | Some(FitDiagnosticBins.Edges edges) when not (increasing edges) ->
                Error "edges must be strictly increasing"
            | Some(FitDiagnosticBins.Edges edges) when not (edges |> List.forall (onLattice declaration.Precision)) ->
                Error "every edge must lie on the declared precision"
            | _ -> Ok()

        let needsBins () =
            match declaration.Bins with
            | None -> Error "it needs bins"
            | Some _ -> Ok()

        let noBins () =
            match declaration.Bins with
            | None -> Ok()
            | Some _ -> Error "it takes no bins"

        let noPoints () =
            if declaration.Points.IsEmpty then
                Ok()
            else
                Error "it takes no points"

        let needsPoints (inside: decimal -> bool) (what: string) () =
            if declaration.Points.IsEmpty then
                Error "it needs points"
            elif not (increasing declaration.Points) then
                Error "points must be strictly increasing"
            elif not (declaration.Points |> List.forall inside) then
                Error $"every point must be {what}"
            else
                Ok()

        let noThresholds () =
            match declaration.ResidualThreshold, declaration.LeverageThreshold with
            | None, None -> Ok()
            | _ -> Error "it takes no influence thresholds"

        let needsThresholds () =
            match declaration.ResidualThreshold, declaration.LeverageThreshold with
            | Some r, Some l when r > 0m && l > 0m -> Ok()
            | _ -> Error "it needs a positive residual threshold and a positive leverage threshold"

        let edgesOnly () =
            match declaration.Bins with
            | Some(FitDiagnosticBins.Edges _) -> Ok()
            | _ -> Error "its bins must be declared edges, in standardised-residual units"

        let rules =
            match diagnostic with
            | ResidualHistogram -> Some [ edgesOnly; noPoints; noThresholds ]
            | ResidualSummaryByBin
            | Calibration -> Some [ needsBins; noPoints; noThresholds ]
            | QuantileComparison ->
                Some [
                    noBins
                    needsPoints (fun p -> p > 0m && p < 1m) "strictly inside (0, 1)"
                    noThresholds
                ]
            | ThresholdCurve -> Some [ noBins; needsPoints (fun _ -> true) ""; noThresholds ]
            | InfluenceSummary -> Some [ needsBins; noPoints; needsThresholds ]
            | _ -> None

        match rules with
        | None -> Error $"'{diagnostic}' is not a fit diagnostic"
        | Some _ when declaration.Precision <= 0m -> Error "the precision must be positive"
        | Some rules ->
            (binsWellFormed () :: (rules |> List.map (fun rule -> rule ())))
            |> List.tryPick (function
                | Error e -> Some e
                | Ok() -> None)
            |> function
                | None -> Ok()
                | Some e -> Error $"'{diagnostic}': {e}"

    /// A candidate cell: its label, the rows behind it and its value.
    type private Candidate = {
        Label: string
        Rows: int list
        Value: float option
    }

    let private number = FitDiagnosticDeclaration.number

    let private roundTo (precision: decimal) (value: float) : float =
        float (Math.Round(decimal value / precision, MidpointRounding.ToEven) * precision)

    let private floorTo (precision: decimal) (value: float) : decimal =
        Math.Floor(decimal value / precision) * precision

    let private ceilTo (precision: decimal) (value: float) : decimal =
        Math.Ceiling(decimal value / precision) * precision

    let private nearest (precision: decimal) (value: float) : decimal =
        Math.Round(decimal value / precision, MidpointRounding.ToEven) * precision

    /// The type-7 sample quantile of an ascending array.
    let private quantile (sorted: float[]) (p: float) : float =
        let h = float (sorted.Length - 1) * p
        let lo = int (Math.Floor h)
        let hi = min (lo + 1) (sorted.Length - 1)
        sorted[lo] + (h - float lo) * (sorted[hi] - sorted[lo])

    /// Cut `values` into the declared bins: each bin's label and the
    /// indices of the rows in it, lowest first, empty bins included.
    let private bin (precision: decimal) (bins: FitDiagnosticBins) (values: float[]) : (string * int list) list =
        let label (lo: string) (hi: string) (closed: bool) =
            $"""[{lo},{hi}{if closed then "]" else ")"}"""

        let rowsWhere (keep: float -> bool) =
            values
            |> Array.indexed
            |> Array.choose (fun (i, v) -> if keep v then Some i else None)
            |> Array.toList

        // The observed range, its ends rounded outward to the precision.
        let range () =
            let lo = floorTo precision (Array.min values)
            let hi = ceilTo precision (Array.max values)
            lo, (if hi <= lo then lo + precision else hi)

        // Bins over computed edges: each closed below and open above, the
        // last closed at both ends so the range's top value is in it.
        let closedBins (raw: decimal[]) =
            let edges = raw |> Array.distinct |> Array.sort
            let es = edges |> Array.map float
            let names = edges |> Array.map number
            let last = es.Length - 1

            [
                for i in 0 .. last - 1 ->
                    let closed = i = last - 1

                    label names[i] names[i + 1] closed,
                    rowsWhere (fun v -> v >= es[i] && (v < es[i + 1] || closed && v <= es[i + 1]))
            ]

        match bins with
        | FitDiagnosticBins.Edges edges ->
            let es = edges |> List.map float |> Array.ofList
            let names = edges |> List.map number |> Array.ofList
            let last = es.Length - 1

            [
                $"(-inf,{names[0]})", rowsWhere (fun v -> v < es[0])
                for i in 0 .. last - 1 do
                    label names[i] names[i + 1] false, rowsWhere (fun v -> v >= es[i] && v < es[i + 1])
                $"[{names[last]},+inf)", rowsWhere (fun v -> v >= es[last])
            ]
        | FitDiagnosticBins.FixedWidth count ->
            let lo, hi = range ()
            let width = (hi - lo) / decimal count

            closedBins [|
                for i in 0..count ->
                    if i = count then
                        hi
                    else
                        nearest precision (float (lo + width * decimal i))
            |]
        | FitDiagnosticBins.Quantiles count ->
            let lo, hi = range ()
            let sorted = Array.sort values

            closedBins [|
                for i in 0..count ->
                    if i = 0 then
                        lo
                    elif i = count then
                        hi
                    else
                        nearest precision (quantile sorted (float i / float count))
            |]

    let private mean (select: int -> float) (rows: int list) = rows |> List.averageBy select

    let private finite (o: ModelExecutionFitObservation) =
        let ok (v: float) =
            not (Double.IsNaN v || Double.IsInfinity v) && abs v < 1e15

        ok o.Fitted
        && ok o.Residual
        && ok o.Standardised
        && (o.Leverage |> Option.forall ok)

    /// Compute `diagnostic` over `observations` as `declaration` bins it,
    /// checking the whole-result floor of `floor` on the distinct cohort.
    /// `Error` names why it could not be computed; an answer that cannot
    /// clear the floor is `Ok` with no cells, which the gate withholds.
    let project
        (diagnostic: string)
        (declaration: FitDiagnosticDeclaration)
        (floor: PrivacyGate)
        (observations: ModelExecutionFitObservation list)
        : Result<CohortResult, string> =
        let rows = Array.ofList observations
        let p = declaration.Precision

        let shape =
            match diagnostic with
            | ResidualHistogram
            | InfluenceSummary -> Histogram
            | _ -> Aggregate

        let candidates () : Result<Candidate list, string> =
            let fitted = rows |> Array.map _.Fitted
            let observed i = rows[i].Fitted + rows[i].Residual

            let axisBins (values: float[]) =
                match declaration.Bins with
                | Some bins -> bin p bins values
                | None -> []

            match diagnostic with
            | ResidualHistogram ->
                axisBins (rows |> Array.map _.Standardised)
                |> List.map (fun (label, members) -> {
                    Label = label
                    Rows = members
                    Value = None
                })
                |> Ok
            | ResidualSummaryByBin ->
                axisBins fitted
                |> List.filter (fun (_, members) -> not members.IsEmpty)
                |> List.map (fun (label, members) -> {
                    Label = label
                    Rows = members
                    Value = Some(roundTo p (mean (fun i -> rows[i].Residual) members))
                })
                |> Ok
            | Calibration ->
                axisBins fitted
                |> List.filter (fun (_, members) -> not members.IsEmpty)
                |> List.collect (fun (label, members) -> [
                    {
                        Label = $"predicted:{label}"
                        Rows = members
                        Value = Some(roundTo p (mean (fun i -> rows[i].Fitted) members))
                    }
                    {
                        Label = $"observed:{label}"
                        Rows = members
                        Value = Some(roundTo p (mean observed members))
                    }
                ])
                |> Ok
            | QuantileComparison ->
                let standardised = rows |> Array.map _.Standardised
                let sorted = Array.sort standardised

                declaration.Points
                |> List.map (fun point ->
                    let q = quantile sorted (float point)

                    let below = [
                        for i in 0 .. rows.Length - 1 do
                            if standardised[i] <= q then
                                i
                    ]

                    let above = [
                        for i in 0 .. rows.Length - 1 do
                            if standardised[i] >= q then
                                i
                    ]

                    {
                        Label = number point
                        Rows = if below.Length <= above.Length then below else above
                        Value = Some(roundTo p q)
                    })
                |> Ok
            | ThresholdCurve ->
                let positives = [
                    for i in 0 .. rows.Length - 1 do
                        if observed i >= 0.5 then
                            i
                ]

                let negatives = [
                    for i in 0 .. rows.Length - 1 do
                        if observed i < 0.5 then
                            i
                ]

                let rate (members: int list) (threshold: decimal) =
                    let hits = members |> List.filter (fun i -> rows[i].Fitted >= float threshold)
                    roundTo p (float hits.Length / float members.Length)

                declaration.Points
                |> List.collect (fun threshold -> [
                    if not positives.IsEmpty then
                        {
                            Label = $"tpr:{number threshold}"
                            Rows = positives
                            Value = Some(rate positives threshold)
                        }
                    if not negatives.IsEmpty then
                        {
                            Label = $"fpr:{number threshold}"
                            Rows = negatives
                            Value = Some(rate negatives threshold)
                        }
                ])
                |> Ok
            | InfluenceSummary ->
                if rows |> Array.exists (fun o -> o.Leverage.IsNone) then
                    Error "InfluenceSummary needs every observation's leverage, and this fit did not report one"
                else
                    let residual = float (defaultArg declaration.ResidualThreshold 0m)
                    let leverage = float (defaultArg declaration.LeverageThreshold 0m)

                    axisBins fitted
                    |> List.collect (fun (label, members) -> [
                        {
                            Label = $"residual:{label}"
                            Rows = members |> List.filter (fun i -> abs rows[i].Standardised > residual)
                            Value = None
                        }
                        {
                            Label = $"leverage:{label}"
                            Rows = members |> List.filter (fun i -> rows[i].Leverage.Value > leverage)
                            Value = None
                        }
                    ])
                    |> Ok
            | other -> Error $"'{other}' is not a fit diagnostic"

        if not (rows |> Array.forall finite) then
            Error "the fit's observations include a non-finite or out-of-range value"
        elif rows.Length = 0 then
            Ok { Shape = shape; Cells = [] }
        else
            match candidates () with
            | Error e -> Error e
            | Ok candidates ->
                let present = candidates |> List.filter (fun c -> not c.Rows.IsEmpty)

                let cohort =
                    present
                    |> List.filter (fun c -> c.Rows.Length >= floor.SuppressionThreshold)
                    |> List.collect _.Rows
                    |> List.distinct
                    |> List.length

                let cells =
                    if cohort < floor.MinCohortSize then
                        []
                    else
                        present
                        |> List.map (fun c -> {
                            Label = c.Label
                            Count = c.Rows.Length
                            Value = c.Value
                        })

                Ok { Shape = shape; Cells = cells }

/// Why a model-execution request was refused at the seam.
///
/// Three of these classes exist because the profile is closed rather
/// than merely narrow, and each names a different way a caller can try
/// to widen it: asking for rows, asking for an undeclared projection,
/// and asking under someone else's scope. A fourth carries the
/// submitter surface's own typed refusal through unchanged, so a
/// modeller enumerates what happened data-side without string-matching a
/// message.
///
/// Phase 642 adds three more, and they answer a question the original
/// set could not: the ones above say what this deployment SERVES, and
/// these say what it has GRANTED. A closed profile needs only the first;
/// a profile with declared authority levels needs both, because "we do
/// not do that" and "we do that, and not for you" have different
/// remedies and a caller that cannot tell them apart will pursue the
/// wrong one.
[<RequireQualifiedAccess>]
type ModelExecutionPeerRefusal =
    /// The envelope declares a profile version this deployment cannot
    /// read. Refused whole rather than read partially — a member the
    /// reader has no field for would satisfy a check by omission.
    | ProfileVersionUnsupported of requested: int * supported: int
    /// The request named a row-level read. **The profile has no such
    /// surface**, and saying so with its own class rather than a generic
    /// "unknown operation" is deliberate: it is the one refusal an
    /// operator wants to see counted.
    | RowAccessRefused of operation: string
    /// The request named an operation this deployment has not declared.
    /// Declaration is the only route onto the diagnostics surface, so an
    /// undeclared name is refused before any computation.
    | UndeclaredDiagnostic of operation: string
    /// The envelope asserted a scope other than the one the peer binding
    /// addresses. The host never ROUTES on an asserted scope — it
    /// refuses one that disagrees, which is the difference between a
    /// diagnostic aid and an impersonation vector.
    | ScopeWideningRefused of asserted: string
    /// The validated caller has no binding here, so it addresses no
    /// scope. Fail-closed: an unbound peer is refused, never defaulted.
    | PeerUnbound of peerId: string
    /// The envelope did not parse as a model-execution request.
    | RequestUnreadable of reason: string
    /// The submitter surface refused, and its typed reason is carried
    /// through verbatim.
    | SubmitterRefused of ModelExecutionRefusal
    /// Phase 642 — the operation requires a data-visibility authority
    /// level above the one this peer's binding DECLARES.
    ///
    /// A distinct class from `UndeclaredDiagnostic`, and the distinction
    /// is what a counterparty acts on: an undeclared operation is one
    /// this deployment does not implement, and the remedy is to stop
    /// asking; an authority refusal is one it implements and has not
    /// GRANTED to this peer, and the remedy is to negotiate a wider
    /// grant. Collapsing them would tell a modeller to abandon a call
    /// that a phone call could enable.
    | AuthorityLevelExceeded of operation: string * required: string * declared: string
    /// Phase 642 — the peer's ceiling would have admitted the operation;
    /// a narrowing declared beneath it does not.
    ///
    /// Separate from `AuthorityLevelExceeded` for the reason the profile
    /// keeps `RowAccessRefused` and `UndeclaredDiagnostic` apart: the
    /// remedies differ and an operator reading a refusal log wants to see
    /// which one happened. A ceiling refusal is a question for the two
    /// organisations; a narrowing refusal is a question for one
    /// deployment's own configuration, and `narrowedBy` names the layer
    /// to look at.
    | AuthorityNarrowingRefused of operation: string * required: string * effective: string * narrowedBy: string
    /// Phase 642 — the answer cleared the authority level, and the
    /// disclosure plane withheld a reference it carries.
    ///
    /// **Names the operation and nothing else, deliberately.** At a door
    /// inside the trust boundary a withhold names the policy, because
    /// telling an operator which control fired is the control
    /// demonstrably working. Across a federation edge the same wording
    /// would tell a counterparty that a fact it may not see EXISTS, and
    /// that existence is itself the disclosure. The deployment that owns
    /// the data has the full record: every withhold is audited by the
    /// gate in the same `_facts` trail every other egress door writes to.
    | EgressWithheld of operation: string
    /// Phase 643 — the authority admitted the view request, and the
    /// request left the bounds the view DECLARES.
    ///
    /// Carried through with the view vocabulary's own class rather than
    /// flattened to one seam class — the posture `SubmitterRefused` takes,
    /// for a sharper reason. Every one of these says "narrow this and ask
    /// again", and which bound to narrow is the only useful part; a caller
    /// told merely that a view request was refused would have to guess
    /// between the window, the series set and its rate budget.
    | ViewRefused of refusal: PeerViewRefusal
    /// Phase 644 — the author-agnostic transition seam judged, and
    /// refused.
    ///
    /// Carried with the seam's own vocabulary nested, the posture
    /// `ViewRefused` takes, and for the same reason: the three classes
    /// name three different things the caller would have to change — the
    /// artifact it named, the edge it asked for, or the grant it holds —
    /// and a caller told merely that a transition was refused would have
    /// to guess which.
    ///
    /// The nested type belongs to `ToolUp.Platform`, not to this profile,
    /// because the judgment does: a local admin screen and a promotion
    /// policy are refused by the identical function with the identical
    /// vocabulary. What this profile owns is the wire CLASS of each case
    /// (`PeerTransition.className`) — a wire word is a federation
    /// artefact, and the platform seam has no business minting one.
    | TransitionRefused of refusal: ModelTransitionRefusal
    /// Phase 646 — the promotion-transfer seam judged, and refused.
    ///
    /// Nested with the platform seam's own vocabulary, the posture
    /// `TransitionRefused` takes, and the nesting goes two deep for the
    /// lifecycle arm: a transfer refused on the edge or the grant carries
    /// §5.7.11's refusal INSIDE this one, because it is §5.7.11's
    /// judgment. Only the three things a transfer can get wrong that a
    /// transition cannot — corrupt bytes, an oversized set, a key already
    /// held with different content — earn a class of their own.
    | PromotionRefused of refusal: ModelPromotionRefusal
    /// A fit-diagnostic request carried a member other than the outcome
    /// it is over — a bin edge, a bin count, a row filter. The binning is
    /// the approved template's and the rows are the fit's, so a call has
    /// nothing to set; `parameter` names the member, because "you may not
    /// set the bins" is the remedy and a generic unreadable would hide it.
    | DiagnosticParameterRefused of operation: string * parameter: string

[<RequireQualifiedAccess>]
module ModelExecutionPeerRefusal =

    /// The stable refusal CLASS (FEDERATION_WIRE.md §7.2). The class is
    /// normative; the wording below is not, and an implementation in
    /// another language will word it differently.
    let className =
        function
        | ModelExecutionPeerRefusal.ProfileVersionUnsupported _ -> "model-execution-profile-version-unsupported"
        | ModelExecutionPeerRefusal.RowAccessRefused _ -> "model-execution-row-read-refused"
        | ModelExecutionPeerRefusal.UndeclaredDiagnostic _ -> "model-execution-undeclared-diagnostic"
        | ModelExecutionPeerRefusal.ScopeWideningRefused _ -> "model-execution-scope-widening"
        | ModelExecutionPeerRefusal.PeerUnbound _ -> "model-execution-peer-unbound"
        | ModelExecutionPeerRefusal.RequestUnreadable _ -> "model-execution-request-unreadable"
        | ModelExecutionPeerRefusal.SubmitterRefused _ -> "model-execution-submitter-refused"
        | ModelExecutionPeerRefusal.AuthorityLevelExceeded _ -> "model-execution-authority-level-exceeded"
        | ModelExecutionPeerRefusal.AuthorityNarrowingRefused _ -> "model-execution-authority-narrowed"
        | ModelExecutionPeerRefusal.EgressWithheld _ -> "model-execution-egress-withheld"
        // Phase 643 — the INNER class, unlike `SubmitterRefused` above,
        // which collapses to one. The difference is whose vocabulary it
        // is: a submitter refusal belongs to another surface, so this
        // profile names the passthrough and stops; a view refusal belongs
        // to this profile, so its own classes are the ones a corpus
        // vector and a refusal log should carry.
        | ModelExecutionPeerRefusal.ViewRefused refusal -> PeerViewRefusal.className refusal
        // Phase 644 — the INNER class again. The seam's refusal type is a
        // platform type, so the class strings live in
        // `PeerTransitionContract.fs` beside the wire shapes rather than
        // on the platform seam, which has no wire.
        | ModelExecutionPeerRefusal.TransitionRefused refusal -> PeerTransition.className refusal
        // Phase 646 — the INNER class again, and `PeerPromotion.className`
        // is where the lifecycle arm folds back onto §5.7.11's classes
        // rather than minting parallel ones for the same decision.
        | ModelExecutionPeerRefusal.PromotionRefused refusal -> PeerPromotion.className refusal
        | ModelExecutionPeerRefusal.DiagnosticParameterRefused _ -> "model-execution-diagnostic-parameter-refused"

    /// Human-readable one-line description (logs + operator display; the
    /// case, not this string, is the contract).
    let describe =
        function
        | ModelExecutionPeerRefusal.ProfileVersionUnsupported(requested, supported) ->
            $"the request declares model-execution profile version {requested}; this deployment reads at most {supported}"
        | ModelExecutionPeerRefusal.RowAccessRefused operation ->
            $"'{operation}' is a row-level read, and the model-execution profile serves no row-level surface"
        | ModelExecutionPeerRefusal.UndeclaredDiagnostic operation ->
            $"'{operation}' is not a declared operation of this deployment's model-execution profile"
        | ModelExecutionPeerRefusal.ScopeWideningRefused asserted ->
            $"the request asserted scope '{asserted}', which is not the scope this peer binding addresses"
        | ModelExecutionPeerRefusal.PeerUnbound peerId ->
            $"peer '{peerId}' has no model-execution binding on this deployment"
        | ModelExecutionPeerRefusal.RequestUnreadable reason ->
            $"the model-execution request could not be read: {reason}"
        | ModelExecutionPeerRefusal.SubmitterRefused refusal -> ModelExecutionRefusal.describe refusal
        | ModelExecutionPeerRefusal.AuthorityLevelExceeded(operation, required, declared) ->
            $"'{operation}' requires data-visibility authority '{required}'; this peer's binding declares '{declared}'"
        | ModelExecutionPeerRefusal.AuthorityNarrowingRefused(operation, required, effective, narrowedBy) ->
            $"'{operation}' requires data-visibility authority '{required}'; the peer ceiling admits it but '{narrowedBy}' narrows this caller to '{effective}'"
        | ModelExecutionPeerRefusal.EgressWithheld operation ->
            $"the answer to '{operation}' references data this deployment's disclosure plane withheld at the federated-egress door"
        | ModelExecutionPeerRefusal.ViewRefused refusal -> PeerViewRefusal.describe refusal
        | ModelExecutionPeerRefusal.TransitionRefused refusal -> ModelTransitionRefusal.describe refusal
        | ModelExecutionPeerRefusal.PromotionRefused refusal -> ModelPromotionRefusal.describe refusal
        | ModelExecutionPeerRefusal.DiagnosticParameterRefused(operation, parameter) ->
            $"'{operation}' takes only the outcome it is over; '{parameter}' is not a member a caller may set, since the binning is the approved template's"

    /// Phase 640 — the seam's refusal read in the **submitter** face's own
    /// closed vocabulary, given the operation names this deployment serves.
    ///
    /// A modeller talking to a data host over this seam is a submitter, and
    /// it should not have to hold two refusal vocabularies to find out what
    /// happened: one for denials that originated in the submitter surface
    /// and a second for denials the seam raised in front of it. This is the
    /// projection that collapses them — and it is where two of the
    /// submitter face's refusal classes are actually produced, because the
    /// seam is where a *version* and a *document kind* exist at all. The
    /// in-deployment remoting face is typed end to end, so neither
    /// condition can arise there; across a peer boundary both are ordinary.
    ///
    /// The mapping is total by construction and each arm is a claim:
    ///   * an unread profile version is exactly `EnvelopeVersionMismatch`,
    ///     and `accepted` is enumerated (`1 .. supported`) rather than
    ///     reported as a ceiling, because the caller's question is "which
    ///     one may I send", not "how high can you count";
    ///   * a row-level probe and an undeclared operation are both
    ///     `UnknownDocumentKind` — the caller named something this
    ///     participant does not implement. That the seam keeps them apart
    ///     internally is for the refusal LOG, so an operator can tell a
    ///     probe from a typo; a submitter's remedy is the same either way,
    ///     and inventing a distinction on the wire it cannot act on would
    ///     be a class it must handle for nothing;
    ///   * a scope-widening assertion is `PolicyRefused` under a stable
    ///     rule id, not `Forbidden`. The distinction is real: `Forbidden`
    ///     says *you* may not, and invites the caller to seek permission,
    ///     whereas nothing about this request is ever permitted from any
    ///     caller — the binding decides the scope, so asserting a different
    ///     one is refused as a rule, not as a role;
    ///   * an unbound peer IS `Forbidden`, for the mirror-image reason.
    /// Recursive only for Phase 646's one nested arm: a promotion refused
    /// on the lifecycle is a transition refusal, and it maps by delegating
    /// to this function's own transition arms rather than by restating
    /// them. One judgment, one reading.
    let rec toSubmitterRefusal (known: string list) (r: ModelExecutionPeerRefusal) : ModelExecutionRefusal =
        match r with
        | ModelExecutionPeerRefusal.ProfileVersionUnsupported(requested, supported) ->
            ModelExecutionRefusal.EnvelopeVersionMismatch(requested, [ 1..supported ])
        | ModelExecutionPeerRefusal.RowAccessRefused operation
        | ModelExecutionPeerRefusal.UndeclaredDiagnostic operation ->
            ModelExecutionRefusal.UnknownDocumentKind(operation, List.sort known)
        | ModelExecutionPeerRefusal.ScopeWideningRefused _ ->
            ModelExecutionRefusal.PolicyRefused "model-execution.scope-widening"
        | ModelExecutionPeerRefusal.PeerUnbound peerId ->
            ModelExecutionRefusal.Forbidden $"peer '{peerId}' has no model-execution binding here"
        | ModelExecutionPeerRefusal.RequestUnreadable reason -> ModelExecutionRefusal.InvalidSubmission reason
        // Already in the submitter vocabulary — carried through rather than
        // re-wrapped, which is what makes this projection idempotent over
        // the class a submitter actually cares about.
        | ModelExecutionPeerRefusal.SubmitterRefused refusal -> refusal
        // Phase 642 — a ceiling refusal IS `Forbidden`: it says *you* may
        // not, and it invites the caller to seek permission, which is
        // precisely the right invitation because a wider grant is a
        // decision the two organisations can take.
        | ModelExecutionPeerRefusal.AuthorityLevelExceeded(operation, required, declared) ->
            ModelExecutionRefusal.Forbidden
                $"'{operation}' requires data-visibility authority '{required}'; this binding grants '{declared}'"
        // A narrowing refusal is `PolicyRefused` under a stable rule id,
        // for the mirror-image reason `ScopeWideningRefused` is: no
        // caller under that narrowing is ever permitted this operation,
        // so it is refused as a rule and not as a role.
        | ModelExecutionPeerRefusal.AuthorityNarrowingRefused _ ->
            ModelExecutionRefusal.PolicyRefused "model-execution.authority-narrowing"
        // A disclosure withhold is likewise a rule, and the rule id is
        // all a counterparty learns — see the case's own comment for why
        // naming the policy across this seam would be the disclosure.
        | ModelExecutionPeerRefusal.EgressWithheld _ ->
            ModelExecutionRefusal.PolicyRefused "model-execution.egress-withheld"
        // Phase 643 — a bounds refusal is `InvalidSubmission`, not
        // `Forbidden` and not `PolicyRefused`. Nothing about the caller
        // or the grant is wrong: the request itself is outside the
        // published bounds, and the description names which one, so the
        // remedy is to narrow and re-send. Mapping it to a policy rule
        // would hand a submitter an identifier it can only log, in place
        // of the one sentence that tells it what to change.
        | ModelExecutionPeerRefusal.ViewRefused refusal ->
            ModelExecutionRefusal.InvalidSubmission(PeerViewRefusal.describe refusal)
        // Phase 644 — the transition family maps case by case rather than
        // collapsing, because the three do not share a remedy. An unknown
        // artifact is `NotFound`, which is the fact and not a judgment. An
        // invalid edge is `InvalidSubmission`: nothing about the caller or
        // its grant is wrong, the request is, and the description names
        // the edge so it can send a different one. An insufficient grant
        // is `Forbidden` — the one case that says *you* may not, and the
        // one whose remedy is a conversation between the two
        // organisations, which is exactly the invitation `Forbidden`
        // extends.
        | ModelExecutionPeerRefusal.TransitionRefused(ModelTransitionRefusal.UnknownArtifact artifactKey) ->
            ModelExecutionRefusal.NotFound artifactKey
        | ModelExecutionPeerRefusal.TransitionRefused(ModelTransitionRefusal.InvalidTransition _ as refusal) ->
            ModelExecutionRefusal.InvalidSubmission(ModelTransitionRefusal.describe refusal)
        | ModelExecutionPeerRefusal.TransitionRefused(ModelTransitionRefusal.InsufficientAuthority _ as refusal) ->
            ModelExecutionRefusal.Forbidden(ModelTransitionRefusal.describe refusal)
        // Phase 646 — the promotion family maps case by case for the reason
        // the transition family does: the five do not share a remedy. A
        // corrupt or oversized attachment set is `InvalidSubmission` —
        // nothing about the caller is wrong, the document is, and the
        // description names which bound so it can be re-sent. A conflict is
        // `PolicyRefused` under a stable rule id, the `ScopeWideningRefused`
        // posture: no caller is ever permitted to put a second artifact
        // under one composite key, so it is refused as a rule and not as a
        // role. A signing failure is `SubstrateDisabled` — the honest shape,
        // since what the caller learns is that this data host cannot
        // currently accept a transfer at all, and retrying the same bytes
        // later is exactly the right response. The lifecycle arm delegates
        // to the transition mapping so one judgment reads one way.
        | ModelExecutionPeerRefusal.PromotionRefused(ModelPromotionRefusal.AttachmentRefused _ as refusal) ->
            ModelExecutionRefusal.InvalidSubmission(ModelPromotionRefusal.describe refusal)
        | ModelExecutionPeerRefusal.PromotionRefused(ModelPromotionRefusal.PayloadConflict _) ->
            ModelExecutionRefusal.PolicyRefused "model-execution.promotion-conflict"
        | ModelExecutionPeerRefusal.PromotionRefused(ModelPromotionRefusal.SigningFailed _) ->
            ModelExecutionRefusal.SubstrateDisabled "promoted-artifact signer"
        | ModelExecutionPeerRefusal.PromotionRefused(ModelPromotionRefusal.TransitionRefused inner) ->
            toSubmitterRefusal known (ModelExecutionPeerRefusal.TransitionRefused inner)
        // The request, not the caller, is wrong, and the description names
        // the member to drop.
        | ModelExecutionPeerRefusal.DiagnosticParameterRefused _ as refusal ->
            ModelExecutionRefusal.InvalidSubmission(describe refusal)

/// The versioned envelope every model-execution request rides in.
///
/// `Body` is an **embedded** document (§3.1 rule 12) — a string whose
/// content is itself canonical JSON — so a relay can carry a request it
/// does not understand, and so the operation's own shape can move
/// without the envelope's shape moving with it.
type ModelExecutionPeerRequest = {
    /// The model-execution profile's own version. Distinct from the
    /// seam's `FormatVersion` and from the contract's `ContractVersion`;
    /// the three move independently.
    ProfileVersion: int
    /// The operation this envelope invokes — a declared submitter
    /// operation or a declared diagnostic.
    Operation: string
    /// The scope the caller believes it is addressing, for its own
    /// diagnostics. **Never routed on.** A value naming any scope other
    /// than the peer binding's is refused; `null` asserts nothing and is
    /// the ordinary case.
    AssertedScope: string option
    /// The operation's own document, embedded.
    Body: string
}

/// What a model-execution call answers: a body, or a typed refusal.
///
/// A union rather than two nullable members, so "answered with nothing"
/// is not expressible — the shape that lets a refusal be mistaken for an
/// empty result.
[<RequireQualifiedAccess>]
type ModelExecutionPeerAnswer =
    /// The operation's own result document, embedded.
    | Answered of body: string
    | Refused of refusal: ModelExecutionPeerRefusal

// ─── Profile constants ───────────────────────────────────────────────

/// The names, ids and versions FEDERATION_WIRE.md §5.7 fixes. Held in
/// one place so the spec, the corpus and this binding cannot drift into
/// three slightly different vocabularies.
[<RequireQualifiedAccess>]
module ModelExecutionProfile =

    /// The model-execution profile's own version: the highest this
    /// deployment reads. Version 2 added the fit diagnostics.
    [<Literal>]
    let Version = 2

    /// The submitter contract's id.
    // OSS-BOUNDARY-EXEMPT: a wire value defined by the public, Apache-2.0
    // model-execution specification; renaming it would break the seam.
    [<Literal>]
    let ContractId = "fuaran.model-execution"

    /// The governed-diagnostics contract's id. A separate contract, not
    /// a separate method group: the two have different release rules,
    /// and one gate over a contract whose methods are governed
    /// differently is a gate whose surface argument does not hold.
    // OSS-BOUNDARY-EXEMPT: a wire value defined by the public, Apache-2.0
    // model-execution specification; renaming it would break the seam.
    [<Literal>]
    let DiagnosticsContractId = "fuaran.model-execution.diagnostics"

    /// The wire version both contracts are served at.
    let contractVersion: ContractVersion = { Major = 1; Minor = 0 }

    /// The submitter operations the profile defines.
    let operations: Set<string> =
        Set.ofList [ "SubmitFit"; "GetOutcome"; "QueryOutcomes"; "ResolveVintage" ]

    /// The governed-diagnostic projections the profile defines. A
    /// DEPLOYMENT declares the subset it answers (see `template`); this
    /// is the vocabulary, not the offer.
    let diagnostics: Set<string> =
        Set.union (Set.ofList [ "Collinearity"; "Coverage"; "TransformPreview" ]) FitDiagnostics.all

    /// The profile version an operation was defined at, which is the
    /// version a request for it declares. A request names the version its
    /// operation needs rather than the newest the sender reads, so a data
    /// host that predates the fit diagnostics still reads every request
    /// it can answer, and refuses one for a fit diagnostic as a version it
    /// does not read: the remedy, an upgrade, is then the one it names.
    let operationVersion (operation: string) : int =
        if Set.contains operation FitDiagnostics.all then 2 else 1

    /// Operation names that denote row-level access. The profile serves
    /// none of them; they are enumerated so that asking for one is
    /// refused as what it is rather than as an unrecognised string.
    ///
    /// Deliberately generous — it names shapes the in-process surfaces
    /// use AND the obvious synonyms a caller would try — because the
    /// list costs nothing and its only job is to make the refusal log
    /// legible.
    let rowAccessOperations: Set<string> =
        Set.ofList [
            "ReadPage"
            "ReadRows"
            "GetRows"
            "GetPage"
            "DownloadContent"
            "FetchContent"
            "ExportRows"
            "StreamRows"
        ]

    /// Phase 642 — operations that require `ViewOnly`: the
    /// server-rendered bounded views, where the RENDERING crosses the
    /// seam and the series does not.
    ///
    /// **Classified before the machinery exists, on purpose.** The views
    /// themselves are a later phase; naming them here means a deployment
    /// at `AggregatesOnly` refuses a view request as an AUTHORITY
    /// question from the first day — the answer a modeller can act on —
    /// rather than as an unrecognised string it would read as a typo. It
    /// also fixes the classification before any implementation can be
    /// tempted to serve one below the level it belongs to.
    /// Phase 643 — the three view operation names as literals, so the
    /// dispatch, the classification set and the egress carve-out below
    /// cannot drift into three spellings of the same word.
    [<Literal>]
    let ListViewsOperation = "ListViews"

    [<Literal>]
    let DescribeViewOperation = "DescribeView"

    [<Literal>]
    let RenderViewOperation = "RenderView"

    let viewOperations: Set<string> =
        Set.ofList [ DescribeViewOperation; ListViewsOperation; RenderViewOperation ]

    /// Phase 644 — the registry lifecycle-transition invocation.
    ///
    /// One operation carrying a requested TARGET, not one operation per
    /// target. The set of statuses is the lifecycle's business and moves
    /// with it; an operation vocabulary that mirrored it would have to be
    /// re-declared on both sides every time the graph gained a state, and
    /// the grant is expressed over targets anyway.
    [<Literal>]
    let InvokeTransitionOperation = "InvokeTransition"

    /// Phase 646 — the promotion transfer: a final artifact, its opaque
    /// spec payload and its provenance attachments landing here as one
    /// recorded act.
    ///
    /// A distinct operation from `InvokeTransition` rather than a widened
    /// body on it, because the two are different acts with different
    /// failure modes and one deployment may reasonably admit either
    /// without the other: a data host willing to let a partner approve
    /// what it already holds is not thereby willing to accept artifacts it
    /// has never seen. They share a GRANT, which is a different thing
    /// from sharing an operation.
    [<Literal>]
    let TransferPromotionOperation = "TransferPromotion"

    /// Phase 642 — operations that require `Full`: raw data, for
    /// co-located or otherwise fully-trusted deployments.
    ///
    /// **The profile implements none of them, and this level does not add
    /// one.** They are named so that asking is refused as an authority
    /// question, and so that a deployment which one day serves one cannot
    /// serve it below `Full`.
    ///
    /// Disjoint from `rowAccessOperations` by construction (a
    /// conformance test pins it), because the two lists answer different
    /// questions and a name on both would be ambiguous. That list
    /// enumerates what a caller REACHES FOR when it wants rows through a
    /// surface that does not exist — refused identically at every level,
    /// because the absence is structural rather than granted. This one
    /// enumerates operations the profile RESERVES to its highest level.
    let fullOperations: Set<string> =
        Set.ofList [ "ExportVintageSeries"; "ReadVintageSeries" ]

    /// The authority level an operation requires.
    ///
    /// Everything the profile serves today is metadata or a governed
    /// aggregate, so it requires `AggregatesOnly` — which is why a
    /// deployment upgrading into Phase 642 enforces exactly what it
    /// already enforced (GP 11).
    ///
    /// **Phase 644's transition invocation requires `AggregatesOnly`
    /// too, and that is a claim rather than an oversight.** It carries no
    /// data in either direction — a composite-key hash and a status label
    /// out, an attributed record back — so there is nothing for a
    /// visibility level to govern. What governs it is the DECLARED
    /// TRANSITION GRANT on the peer's binding, a separate axis checked at
    /// the seam. Folding the two together would make the ordinary
    /// arrangement — a partner that may approve models and must never see
    /// a row — inexpressible.
    ///
    /// **An unrecognised name requires `AggregatesOnly`, not more**, and
    /// that is deliberate rather than lax: the authority check must never
    /// be the thing that refuses a typo. A name nobody declared is
    /// refused by the declaration check, with the class that tells a
    /// caller it named something this deployment does not implement.
    let requiredAuthority (operation: string) : PeerDataVisibilityLevel =
        if Set.contains operation fullOperations then
            PeerDataVisibilityLevel.Full
        elif Set.contains operation viewOperations then
            PeerDataVisibilityLevel.ViewOnly
        else
            PeerDataVisibilityLevel.AggregatesOnly

    /// The clean-room template that DECLARES a deployment's governed
    /// diagnostics. `AllowedMethods` is the declaration — the gate's
    /// surface invariant refuses anything off it before the projection
    /// runs — so narrowing the offer is a matter of declaring less, and
    /// declaring nothing offers nothing.
    ///
    /// An entry that is not in `diagnostics` is dropped rather than
    /// honoured: a template cannot mint a projection the profile does
    /// not define, because the corresponding method would never be
    /// dispatched anyway and a template that appears to offer one is a
    /// lie an operator would read.
    let template (templateId: string) (floor: PrivacyGate) (declared: Set<string>) : CleanRoomTemplate = {
        TemplateId = templateId
        AllowedMethods = Set.intersect declared diagnostics
        Floor = floor
        Release = ReleaseTerms.none
    }

    /// Declare a fit diagnostic on `template`: put it on the surface and
    /// carry its binning in the template's release terms, where the
    /// template's content hash covers it, so changing a bin edge is a new
    /// template that has to be approved again. It also makes the template
    /// name its withheld cells to the caller. A name that is not a fit
    /// diagnostic leaves the template unchanged, as `template` drops one.
    let withFitDiagnostic
        (diagnostic: string)
        (declaration: FitDiagnosticDeclaration)
        (template: CleanRoomTemplate)
        : CleanRoomTemplate =
        if Set.contains diagnostic FitDiagnostics.all then
            {
                template with
                    AllowedMethods = Set.add diagnostic template.AllowedMethods
                    Release.Declarations =
                        Map.add diagnostic (FitDiagnosticDeclaration.render declaration) template.Release.Declarations
                    // A fit diagnostic is drawn on the modeller's side, where
                    // a withheld bin must be named, never drawn as empty.
                    Release.NamesWithheld = true
            }
        else
            template

    /// Switch calibrated noise on for `template`'s released cells. Off by
    /// default: k-suppression alone, for which no differential-privacy
    /// claim is made. With it on, every release spends `policy`'s ε.
    let withNoise (policy: NoisedReleasePolicy) (template: CleanRoomTemplate) : CleanRoomTemplate = {
        template with
            Release.Noise = Some policy
    }

// ─── Admission ───────────────────────────────────────────────────────

/// What a deployment admits on the model-execution seam: the profile
/// version it reads, the submitter operations it serves, and the
/// diagnostics it has declared. Policy as data (GP 12 rule 3) — no
/// callback decides admission, so the same value can be logged, shown on
/// an admin surface, and compared between deployments.
type ModelExecutionAdmission = {
    ProfileVersion: int
    Operations: Set<string>
    Diagnostics: Set<string>
    /// Phase 642 — the resolved data-visibility authority for the CALLER
    /// this admission is being evaluated for: the peer's declared
    /// ceiling, the level after the team/user narrowing walk, and where
    /// each came from.
    ///
    /// **The one per-caller member of an otherwise deployment-wide
    /// record, and it sits here rather than as another parameter of
    /// `admit` for a reason worth stating.** `admit` and `read` are the
    /// functions an implementation certifies the profile's reject vectors
    /// against; every argument added to them is a re-encoding every
    /// conformance harness in every language has to mirror. Folding the
    /// per-call resolution into the admission value keeps the reader's
    /// shape fixed and makes the whole admission decision one value that
    /// can be logged, shown on an admin surface, and compared — which is
    /// what "policy as data" (GP 12 rule 3) was already buying for the
    /// other three members.
    ///
    /// `PeerVisibility.unresolved` (the default ceiling, no narrowing) on
    /// a deployment that declares nothing, so the admission of a pre-642
    /// composition is byte-for-byte its old one (GP 11).
    Authority: PeerVisibilityResolution
}

[<RequireQualifiedAccess>]
module ModelExecutionAdmission =

    /// The whole profile at the current version, with a declared
    /// diagnostic set. The submitter operations are not narrowable —
    /// they are what makes a deployment a data host at all — while the
    /// diagnostics are exactly what a deployment chooses.
    let create (declared: Set<string>) : ModelExecutionAdmission = {
        ProfileVersion = ModelExecutionProfile.Version
        Operations = ModelExecutionProfile.operations
        Diagnostics = Set.intersect declared ModelExecutionProfile.diagnostics
        // Phase 642 — no caller yet, so the narrowest grant. `governed`
        // replaces this per call, from the resolved binding.
        Authority = PeerVisibility.unresolved ""
    }

    /// Phase 642 — the same admission, evaluated for one caller's
    /// resolved authority. What the dispatch prologue folds in once the
    /// binding is known, so the reader's signature never has to carry a
    /// second per-call argument.
    let withAuthority (authority: PeerVisibilityResolution) (admission: ModelExecutionAdmission) = {
        admission with
            Authority = authority
    }

    /// Phase 643 — admit the bounded-view operations.
    ///
    /// **Declaring the views is a separate act from granting the level,
    /// and both are required.** The level says what a peer may see; this
    /// says what the deployment implements. A deployment that grants
    /// `ViewOnly` and declares no views refuses a view request as
    /// undeclared — "we do not do that" — while one that declares views
    /// and grants `AggregatesOnly` refuses it as an authority question.
    /// Two different remedies, kept distinguishable by keeping the two
    /// declarations apart (§5.7.9's admission order is what makes the
    /// authority answer win when both are true).
    ///
    /// Not called ⇒ the view names stay off the admitted set and a
    /// pre-643 composition admits byte-for-byte what it always did
    /// (GP 11).
    let withViews (admission: ModelExecutionAdmission) = {
        admission with
            Operations = Set.union admission.Operations ModelExecutionProfile.viewOperations
    }

    /// Phase 644 — admit the registry lifecycle-transition invocation.
    ///
    /// **Declaring the operation is a separate act from granting a
    /// transition, and both are required**, exactly as `withViews` is
    /// separate from granting `ViewOnly`. This says the deployment
    /// implements cross-peer transitions at all; the grant on each peer's
    /// binding says which of them that peer may invoke. A deployment that
    /// implements them and grants none refuses with the authority class —
    /// "we do that, and not for you" — while one that implements none
    /// refuses as undeclared. Two remedies, kept distinguishable by
    /// keeping the two declarations apart.
    ///
    /// Not called ⇒ the operation stays off the admitted set and a pre-644
    /// composition admits byte-for-byte what it always did (GP 11).
    let withTransitions (admission: ModelExecutionAdmission) = {
        admission with
            Operations = Set.add ModelExecutionProfile.InvokeTransitionOperation admission.Operations
    }

    /// Phase 646 — admit the promotion transfer.
    ///
    /// **A third separate declaration, and separate from `withTransitions`
    /// on purpose.** Accepting an artifact a peer built is a strictly
    /// larger act than judging one this deployment already holds: it puts
    /// bytes in this registry that no local process produced. A data host
    /// that admits transitions and not transfers refuses a transfer as
    /// undeclared — "we do not do that" — which is a different answer from
    /// the authority one, and the two remedies differ.
    ///
    /// Not called ⇒ the operation stays off the admitted set and a pre-646
    /// composition admits byte-for-byte what it always did (GP 11).
    let withPromotions (admission: ModelExecutionAdmission) = {
        admission with
            Operations = Set.add ModelExecutionProfile.TransferPromotionOperation admission.Operations
    }

    /// A data host that answers no governed diagnostics: the submitter
    /// operations and nothing else. This is the default a deployment
    /// gets by not declaring, and it is the honest one — a diagnostic
    /// nobody declared is one nobody agreed to.
    let submitterOnly: ModelExecutionAdmission = create Set.empty

// ─── Peer binding ────────────────────────────────────────────────────

/// What one modeller peer is bound to on this data host: the scope its
/// calls resolve under, and the already-scoped submitter surface they
/// resolve through.
///
/// The binding is the ONLY thing that decides scope. Nothing on the wire
/// contributes to it, which is what makes the profile's scope discipline
/// structural rather than a validation rule somebody has to remember
/// (GP 4).
type ModelExecutionPeerBinding = {
    PeerId: string
    ScopeId: string
    Api: ModelExecutionApi
    /// Phase 642 — what THIS peer may see: the declared ceiling plus any
    /// team / user narrowing beneath it.
    ///
    /// On the binding for the same reason the scope is: the binding is
    /// the receiver's own declaration about one counterparty, nothing on
    /// the wire contributes to it, and the execution side of a
    /// long-running fit re-resolves it from the same place the request
    /// side did (GP 12 rules 1 and 4).
    ///
    /// `PeerVisibilityBinding.default'` for a peer nobody granted
    /// anything: the narrowest level, no narrowing — the pre-642
    /// behaviour exactly (GP 11).
    Visibility: PeerVisibilityBinding
    /// Phase 642 — the disclosure-plane route this binding's level-gated
    /// answers take before they cross the seam.
    ///
    /// `None` on a deployment with no fact substrate (or one that
    /// composes no route): nothing is routed, nothing is consulted,
    /// nothing is allocated (GP 13). A composed route is what makes the
    /// federated egress door a real door rather than a claim.
    Egress: PeerEgressRoute option
    /// Phase 644 — which registry lifecycle transitions THIS peer may
    /// invoke. `ModelTransitionAuthority.none` for a peer nobody granted
    /// anything, which admits nothing (GP 11).
    ///
    /// On the binding for the reasons the scope and the visibility are:
    /// it is the receiver's own declaration about one counterparty,
    /// nothing on the wire contributes to it, and the execution side of a
    /// queued transition re-resolves it from the same place the request
    /// side did (GP 12 rules 1 and 4).
    ///
    /// **Per-peer, not deployment-wide, and the difference is the whole
    /// point.** `PeerOptions.withPeerTransitionAuthority` publishes what
    /// the deployment is willing to admit in general — the offer a
    /// counterparty pins — while this is what one counterparty was
    /// actually granted. A deployment publishing `Approved` has said it
    /// does cross-peer approval; it has not said every peer may.
    TransitionAuthority: ModelTransitionAuthority
}

/// How long the fit job waits for its outcome to appear in the registry.
/// Expressed as data rather than a timeout callback so a distributed job
/// substrate honours the same contract (GP 12 rule 3).
type ModelExecutionFitPollPolicy = {
    /// Registry reads attempted before the job reports the fit as still
    /// unregistered. At least one read always happens.
    Attempts: int
    /// Wait between reads. `TimeSpan.Zero` polls back-to-back, which is
    /// what an in-process reference provider wants.
    Interval: TimeSpan
}

[<RequireQualifiedAccess>]
module ModelExecutionFitPollPolicy =

    /// A minute of patience at second granularity — generous for a fit
    /// whose provider registers synchronously, and short enough that a
    /// wedged provider surfaces as a refusal rather than a job that
    /// never terminates.
    let default': ModelExecutionFitPollPolicy = {
        Attempts = 60
        Interval = TimeSpan.FromSeconds 1.0
    }

    /// No waiting: one read, immediately. The shape an in-process
    /// reference provider (and a test) wants.
    let immediate: ModelExecutionFitPollPolicy = {
        Attempts = 1
        Interval = TimeSpan.Zero
    }

/// The substrate the submitter contract runs over.
type ModelExecutionPeerDeps = {
    /// Resolve a validated peer id to its binding. `None` refuses the
    /// call with `PeerUnbound` — an unbound peer addresses no scope, and
    /// defaulting one would be the whole isolation argument undone.
    ///
    /// Keyed by peer id rather than by call context because the
    /// execution side of a long-running fit has neither a request nor a
    /// principal: the owner rides the job payload as a value, and this
    /// signature is what lets both halves resolve the same way (GP 12
    /// rules 1 and 4).
    ResolveBinding: string -> Async<ModelExecutionPeerBinding option>
    Admission: ModelExecutionAdmission
    FitPoll: ModelExecutionFitPollPolicy
    /// Phase 643 — the bounded-view substrate. `None` on a deployment
    /// that declares no views, which is every pre-643 deployment: the
    /// view operations are then not on the admitted set either (see
    /// `ModelExecutionAdmission.withViews`), so nothing reaches the
    /// dispatch arms below and nothing is constructed (GP 13).
    ///
    /// Beside `Admission` rather than on the binding because the views a
    /// deployment renders are a property of the DEPLOYMENT; which of them
    /// a given peer may reach is the level on its binding, and holding
    /// the two apart is what lets one offer be published to many
    /// counterparties without restating it per binding.
    Views: PeerViewDeps option
    /// Phase 644 — the author-agnostic transition seam's substrate.
    /// `None` on a deployment that admits no cross-peer transition, which
    /// is every pre-644 deployment: the operation is then off the admitted
    /// set too (see `ModelExecutionAdmission.withTransitions`), so nothing
    /// reaches the dispatch arm and nothing is constructed (GP 13).
    ///
    /// Beside `Admission` rather than on the binding for the reason
    /// `Views` is: the registry a deployment transitions IN is a property
    /// of the deployment, while which transitions a given peer may invoke
    /// is the grant on its binding. Holding the two apart is what lets one
    /// registry serve many counterparties at different grants.
    Transitions: ModelTransitionDeps option
    /// Phase 646 — the promotion-transfer seam's substrate, including the
    /// optional acceptance signer. `None` on a deployment that admits no
    /// transfer, which is every pre-646 deployment: the operation is then
    /// off the admitted set too (see
    /// `ModelExecutionAdmission.withPromotions`), so nothing reaches the
    /// dispatch arm and nothing is constructed (GP 13).
    ///
    /// Beside `Admission` for the reason `Views` and `Transitions` are: the
    /// registry a transfer lands in is a property of the deployment, while
    /// which lifecycle statuses a given peer may drive an artifact into is
    /// the grant on its binding — the SAME grant a bare transition is
    /// judged by, deliberately, since it is the same question.
    Promotions: ModelPromotionDeps option
}

/// The substrate the governed-diagnostics contract runs over.
type ModelExecutionDiagnosticsDeps = {
    ResolveBinding: string -> Async<ModelExecutionPeerBinding option>
    /// Compute one declared aggregate projection over a vintage the
    /// binding's scope owns. **This only computes.** Whether the answer
    /// may be released is the gate's decision, and a projection that
    /// answers in a shape the gate cannot check is withheld — so there
    /// is no way for an implementation of this function to release rows
    /// by returning them.
    Project: ModelExecutionPeerBinding -> string -> ModelExecutionPeerDiagnosticRequest -> Async<CohortResult>
    /// Read back a stored fit outcome's observations, for the fit
    /// diagnostics: the outcome named by its composite key, in the
    /// binding's scope, or `None` when the scope holds no such outcome.
    /// **This only reads.** The binning is the template's and the
    /// projection is the profile's (`FitDiagnostics.project`), so an
    /// implementation of this function decides neither what is computed
    /// nor what is released.
    FitObservations: ModelExecutionPeerBinding -> string -> Async<ModelExecutionFitObservation list option>
}

/// What the governed-diagnostics contract is gated by: the clean-room
/// broker and template, the bilateral approval check, the privacy-budget
/// meter every release is charged to, and the noise mechanism a template
/// that switches noise on draws from.
type ModelExecutionDiagnosticsGate = {
    /// The clean-room broker the gate enforces the floor through.
    Broker: ICleanRoomBroker
    /// The template whose `AllowedMethods` is the declaration of what this
    /// deployment answers, and whose release terms carry each fit
    /// diagnostic's binning and the optional noise.
    Template: CleanRoomTemplate
    /// The bilateral approval check, or `None` for a composition that
    /// declares no approval registry.
    Approval: CleanRoomApprovalCheck option
    /// The shared privacy-budget ledger every diagnostic release is charged
    /// to, keyed by template, counterparty peer and epoch. A release under
    /// an un-noised template is charged zero ε: it is counted, and it spends
    /// nothing, because without noise there is no privacy loss the ledger
    /// could honestly bound. A noised template's release spends its policy's
    /// ε, and an exhausted budget is a withhold the audit records.
    Budget: PrivacyBudgetMeter
    /// The mechanism a noised template draws from. Required exactly when
    /// the template switches noise on.
    Noise: INoiseMechanism option
    /// The decision sink (`CleanRoomGate.noAudit` to record nothing).
    Audit: PeerCleanRoomDecisionPayload -> Async<unit>
}

/// Why a governed-diagnostics composition was refused.
[<RequireQualifiedAccess>]
type ModelExecutionTemplateRefusal =
    /// The template offers a fit diagnostic and declares no binning for
    /// it. Refused rather than defaulted: a default bin is a binning no
    /// counterparty approved.
    | BinningUndeclared of diagnostic: string
    /// The template's declaration for a fit diagnostic is unreadable or
    /// incomplete; `reason` says which.
    | DeclarationInvalid of diagnostic: string * reason: string
    /// The template switches noise on and the composition holds no
    /// mechanism to draw it from.
    | NoiseMechanismAbsent of templateId: string

/// Reading and checking a governed-diagnostics template's refusals.
[<RequireQualifiedAccess>]
module ModelExecutionTemplateRefusal =

    /// Human-readable one-line description.
    let describe =
        function
        | ModelExecutionTemplateRefusal.BinningUndeclared diagnostic ->
            $"the template offers '{diagnostic}' and declares no binning for it"
        | ModelExecutionTemplateRefusal.DeclarationInvalid(diagnostic, reason) ->
            $"the template's declaration for '{diagnostic}' cannot be used: {reason}"
        | ModelExecutionTemplateRefusal.NoiseMechanismAbsent templateId ->
            $"template '{templateId}' switches calibrated noise on and no INoiseMechanism is composed"

    /// Every reason `template` cannot govern the diagnostics contract, in
    /// method order; empty when it can.
    let check (template: CleanRoomTemplate) : ModelExecutionTemplateRefusal list =
        Set.intersect template.AllowedMethods FitDiagnostics.all
        |> Set.toList
        |> List.choose (fun diagnostic ->
            match Map.tryFind diagnostic template.Release.Declarations with
            | None -> Some(ModelExecutionTemplateRefusal.BinningUndeclared diagnostic)
            | Some text ->
                match
                    FitDiagnosticDeclaration.parse text
                    |> Result.bind (FitDiagnostics.check diagnostic)
                with
                | Ok() -> None
                | Error reason -> Some(ModelExecutionTemplateRefusal.DeclarationInvalid(diagnostic, reason)))

// ─── The binding ─────────────────────────────────────────────────────

/// The forge binding for FEDERATION_WIRE.md §5.7: the peer contracts, the
/// admission check every dispatch runs, and the projections between the
/// submitter wire objects and the profile's own shapes.
[<RequireQualifiedAccess>]
module ModelExecutionPeerContract =

    // ── Projections ──────────────────────────────────────────────────

    let private toWireGateVerdict (v: ModelExecutionGateVerdict) : ModelExecutionPeerGateVerdict = {
        Name = v.Name
        Threshold = v.Threshold
        Direction = v.Direction
        Observed = v.Observed
        Passed = v.Passed
    }

    /// A registered outcome, projected onto the profile's shape. The
    /// verdict list is sorted ordinally so two data hosts that computed
    /// the same fit produce the same bytes for it.
    let toWireOutcome (o: ModelExecutionOutcome) : ModelExecutionPeerOutcome = {
        CompositeKeyHash = o.CompositeKeyHash
        SpecHash = o.SpecHash
        DatasetVersion = o.DatasetVersion
        Seed = o.Seed
        ProviderId = o.ProviderId
        ProviderVersion = o.ProviderVersion
        // The federation profile's outcome shape carries the artifact
        // reference flat and non-optional at this version, so an outcome
        // with no retained artifact projects as empty identifiers. The
        // submitter face distinguishes the two cases since Phase 640; this
        // wire does not, and widening it is a profile-version change, not a
        // projection detail — so the loss is recorded here rather than
        // papered over.
        ArtifactId = o.Artifact |> Option.map _.ArtifactId |> Option.defaultValue ""
        ArtifactContentHash = o.Artifact |> Option.map _.ContentHash |> Option.defaultValue ""
        // Phase 1011 — the outcome's `DiagnosticUnits` and its verdicts'
        // units do not cross at this profile version, for the reason the
        // artifact's optionality does not: widening the profile's records is
        // a profile-version change, not a projection detail.
        Diagnostics = o.Diagnostics
        GateVerdicts =
            o.GateVerdicts
            |> List.map toWireGateVerdict
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))
        Status = o.Status
        Annotations = o.Annotations
        RegisteredAt = o.RegisteredAt
    }

    let toWireVintage (v: ModelExecutionDatasetVersion) : ModelExecutionPeerVintageInfo = {
        DatasetId = v.DatasetId
        Version = v.Version
        RowCount = v.RowCount
        Format = v.Format
        ContentHash = v.ContentHash
        CreatedAt = v.CreatedAt
    }

    /// A profile submission projected onto the submitter surface's own
    /// shape. **The `SpecHash` is copied, never recomputed** — the
    /// opacity posture, which matters more across a peer boundary than
    /// within one deployment because the two sides do not share a
    /// canonicalisation rule.
    let ofWireSubmission (s: ModelExecutionPeerSubmission) : ModelExecutionFitSubmission = {
        DatasetId = s.Vintage.DatasetId
        DatasetVersion = s.Vintage.Version
        SpecPayload = s.SpecPayload
        SpecHash = s.SpecHash
        // Phase 640 — the submitter face carries the minting rule; the
        // federation wire at this profile version does not, so a peer
        // states none. Deliberately NOT defaulted to the registered rule:
        // asserting on a peer's behalf that its hash was minted a
        // particular way is the one claim this seam has no standing to
        // make, and it would be indistinguishable downstream from the peer
        // having said so itself.
        SpecHashAlgorithm = ""
        ProviderKind = s.ProviderKind
        Seed = s.Seed
        Gates =
            s.Gates
            |> List.map (fun g -> {
                Name = g.Name
                Threshold = g.Threshold
                // Phase 1011 — the federation profile's gate carries no
                // unit at this version, so a peer's gate states none and is
                // evaluated only against a diagnostic that states none.
                Unit = None
                Direction = g.Direction
            })
        // Phase 451 — the peer's own declaration, tolerant of an older
        // peer that does not send the field.
        SubmitterClass = SubmitterClass.ofLabelOrHuman s.SubmitterClass
    }

    // ── Admission ────────────────────────────────────────────────────

    /// Decide whether a request may be dispatched at all, given what
    /// this deployment admits and the scope the caller's binding
    /// addresses.
    ///
    /// The order is not incidental. The version is checked first because
    /// a document the reader cannot fully read is one whose later
    /// members it would be guessing at; row access is checked next so
    /// that a probe is named as a probe even at an unadmitted version;
    /// and the scope assertion is checked last, on a request already
    /// known to be one this deployment serves.
    ///
    /// **Phase 642 puts the authority checks between row access and
    /// declaration, and both halves of that placement are load-bearing.**
    /// After row access, because the row vocabulary is a structural
    /// absence rather than a grant — the profile serves no row surface at
    /// ANY level, so re-reporting a `ReadPage` probe as an authority
    /// question would tell a caller that a wider grant might get it one,
    /// which is false. Before declaration, because an operation this
    /// deployment implements but has not GRANTED should say so: refusing
    /// `RenderView` as "undeclared" on a deployment at `AggregatesOnly`
    /// would send a modeller to argue about the wrong thing.
    ///
    /// The ceiling is checked before the narrowing so that the two
    /// refusals stay distinguishable in a log: a ceiling that does not
    /// reach the operation is a question for the two organisations, and a
    /// narrowing that does not is a question for one deployment's own
    /// configuration.
    let admit
        (admission: ModelExecutionAdmission)
        (boundScope: string)
        (request: ModelExecutionPeerRequest)
        : Result<unit, ModelExecutionPeerRefusal> =
        let required = ModelExecutionProfile.requiredAuthority request.Operation

        if request.ProfileVersion > admission.ProfileVersion then
            Error(ModelExecutionPeerRefusal.ProfileVersionUnsupported(request.ProfileVersion, admission.ProfileVersion))
        elif Set.contains request.Operation ModelExecutionProfile.rowAccessOperations then
            Error(ModelExecutionPeerRefusal.RowAccessRefused request.Operation)
        elif not (PeerDataVisibilityLevel.admits admission.Authority.Ceiling required) then
            Error(
                ModelExecutionPeerRefusal.AuthorityLevelExceeded(
                    request.Operation,
                    PeerDataVisibilityLevel.label required,
                    PeerDataVisibilityLevel.label admission.Authority.Ceiling
                )
            )
        elif not (PeerDataVisibilityLevel.admits admission.Authority.Effective required) then
            Error(
                ModelExecutionPeerRefusal.AuthorityNarrowingRefused(
                    request.Operation,
                    PeerDataVisibilityLevel.label required,
                    PeerDataVisibilityLevel.label admission.Authority.Effective,
                    admission.Authority.NarrowedBy
                    |> Option.map PeerVisibilityScope.toString
                    |> Option.defaultValue ""
                )
            )
        elif
            not (
                Set.contains request.Operation admission.Operations
                || Set.contains request.Operation admission.Diagnostics
            )
        then
            Error(ModelExecutionPeerRefusal.UndeclaredDiagnostic request.Operation)
        else
            match request.AssertedScope with
            | Some asserted when asserted <> boundScope ->
                Error(ModelExecutionPeerRefusal.ScopeWideningRefused asserted)
            | _ -> Ok()

    /// Read a fit-diagnostic body strictly: an object whose one member is
    /// the `OutcomeId` string. Any other member is refused by name, which
    /// is what makes "a call carries no binning" a property of the reader
    /// rather than of a well-behaved caller — a lenient decode would drop
    /// a `Bins` member silently, and the caller would believe it had been
    /// honoured.
    let readFitDiagnostic
        (operation: string)
        (body: string)
        : Result<ModelExecutionPeerFitDiagnosticRequest, ModelExecutionPeerRefusal> =
        try
            use document = Text.Json.JsonDocument.Parse(if isNull body then "" else body)
            let root = document.RootElement

            if root.ValueKind <> Text.Json.JsonValueKind.Object then
                Error(ModelExecutionPeerRefusal.RequestUnreadable "a fit-diagnostic body is a JSON object")
            else
                let members = root.EnumerateObject() |> Seq.toList

                match members |> List.tryFind (fun m -> m.Name <> "OutcomeId") with
                | Some extra -> Error(ModelExecutionPeerRefusal.DiagnosticParameterRefused(operation, extra.Name))
                | None ->
                    match members with
                    | [ outcome ] when outcome.Value.ValueKind = Text.Json.JsonValueKind.String ->
                        Ok {
                            OutcomeId = outcome.Value.GetString()
                        }
                    | _ ->
                        Error(
                            ModelExecutionPeerRefusal.RequestUnreadable
                                "a fit-diagnostic body names the outcome it is over, as the string OutcomeId"
                        )
        with ex ->
            Error(ModelExecutionPeerRefusal.RequestUnreadable ex.Message)

    /// Read a request envelope and admit it in one step — the reader an
    /// implementation certifies the profile's `reject` vectors against.
    ///
    /// A document that does not parse is refused rather than partially
    /// read, for the reason §5.3 gives about labels: a member the reader
    /// has no value for would satisfy a check by omission.
    let read
        (admission: ModelExecutionAdmission)
        (boundScope: string)
        (json: string)
        : Result<ModelExecutionPeerRequest, ModelExecutionPeerRefusal> =
        let parsed =
            try
                let request = JsonRpc.deserialize<ModelExecutionPeerRequest> json

                if
                    isNull (box request)
                    || isNull (box request.Operation)
                    || isNull (box request.Body)
                then
                    Error "the document is not a model-execution request envelope"
                else
                    Ok request
            with ex ->
                Error ex.Message

        match parsed with
        | Error reason -> Error(ModelExecutionPeerRefusal.RequestUnreadable reason)
        | Ok request ->
            match admit admission boundScope request with
            | Error refusal -> Error refusal
            | Ok() when Set.contains request.Operation FitDiagnostics.all ->
                readFitDiagnostic request.Operation request.Body
                |> Result.map (fun _ -> request)
            | Ok() -> Ok request

    // ── Envelope helpers ─────────────────────────────────────────────

    /// Build a request envelope for `operation` carrying `body` as its
    /// embedded document. The modeller side's one construction route, so
    /// a caller cannot forget the profile version.
    let request (operation: string) (body: 'T) : ModelExecutionPeerRequest = {
        ProfileVersion = ModelExecutionProfile.operationVersion operation
        Operation = operation
        AssertedScope = None
        Body = JsonRpc.serialize body
    }

    /// The same envelope, asserting the scope the caller believes it is
    /// addressing. The host refuses a disagreement rather than routing
    /// on it, so this is a self-check, never an addressing mechanism.
    let requestAsserting (scope: string) (operation: string) (body: 'T) : ModelExecutionPeerRequest = {
        request operation body with
            AssertedScope = Some scope
    }

    /// The ordinal comparison every list sort in this profile uses.
    /// Named rather than inlined so no call site can reach for a
    /// culture-sensitive comparison, which would make a document's bytes
    /// depend on the machine that produced them (§3.1 rule 10).
    let private byOrdinal (key: 'a -> string) (a: 'a) (b: 'a) = String.CompareOrdinal(key a, key b)

    /// A `SubmitFit` request envelope, with the requested gates put into
    /// the profile's ordinal order. **The emitter owns the sort, not the
    /// caller** — two modellers asking for the same gates in different
    /// orders must produce the same document, or the envelope is not
    /// canonical in the sense §3 means.
    let submissionRequest (submission: ModelExecutionPeerSubmission) : ModelExecutionPeerRequest =
        request "SubmitFit" {
            submission with
                Gates = submission.Gates |> List.sortWith (byOrdinal _.Name)
        }

    /// Phase 643 — a `RenderView` request envelope, with the requested
    /// series in ordinal order. **The emitter owns the sort**, for the
    /// same reason it owns the gate and term sorts: two modellers asking
    /// for the same series in different orders must produce the same
    /// document, or the envelope is not canonical in the sense §3 means.
    let viewRequest (view: PeerViewRequest) : ModelExecutionPeerRequest =
        request ModelExecutionProfile.RenderViewOperation {
            view with
                Series = view.Series |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        }

    /// Phase 644 — an `InvokeTransition` request envelope.
    ///
    /// No sort to own here: the invocation carries no list. It still goes
    /// through a named constructor rather than `request` directly, so a
    /// caller cannot spell the operation name differently from the one
    /// the profile dispatches on.
    let transitionRequest (invocation: PeerTransitionInvocation) : ModelExecutionPeerRequest =
        request ModelExecutionProfile.InvokeTransitionOperation invocation

    /// Phase 646 — a `TransferPromotion` request envelope, canonicalised.
    ///
    /// **The emitter owns the sorts**, as it does for gates, terms and
    /// series: two builders promoting the same artifact with the same
    /// evidence must produce the same document, or the envelope is not
    /// canonical in the sense §3 means — and a transfer's whole
    /// idempotency claim is that the receiver can recognise a re-send.
    let promotionRequest (transfer: PeerPromotionTransfer) : ModelExecutionPeerRequest =
        request ModelExecutionProfile.TransferPromotionOperation (PeerPromotion.canonicalise transfer)

    /// A governed-diagnostic request envelope for `diagnostic`, with the
    /// terms in ordinal order for the same reason.
    let diagnosticRequest
        (diagnostic: string)
        (projection: ModelExecutionPeerDiagnosticRequest)
        : ModelExecutionPeerRequest =
        request diagnostic {
            projection with
                Terms = projection.Terms |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        }

    /// A fit-diagnostic request envelope for `diagnostic` over the stored
    /// outcome `outcomeId`. It carries no binning: there is no member for
    /// one.
    let fitDiagnosticRequest (diagnostic: string) (outcomeId: string) : ModelExecutionPeerRequest =
        request diagnostic { OutcomeId = outcomeId }

    /// Decode an `Answered` body into the operation's own shape.
    /// `Refused` is returned as the typed refusal it carries — a caller
    /// enumerates the class rather than matching on a message.
    let answerBody<'T> (answer: ModelExecutionPeerAnswer) : Result<'T, ModelExecutionPeerRefusal> =
        match answer with
        | ModelExecutionPeerAnswer.Answered body -> Ok(JsonRpc.deserialize<'T> body)
        | ModelExecutionPeerAnswer.Refused refusal -> Error refusal

    /// Phase 640 — `answerBody`, with any refusal read in the **submitter**
    /// face's closed vocabulary rather than the seam's.
    ///
    /// This is the reader a modelling client wants, and the reason both
    /// exist: `answerBody` is right for an operator surface, which cares
    /// that a probe was a probe; this one is right for a submitter, which
    /// cares what it may now do. `admission` supplies the operation names
    /// the deployment serves, so an `UnknownDocumentKind` can tell the
    /// caller what it *could* have asked for instead of merely that it
    /// asked wrongly.
    let submitterAnswerBody<'T>
        (admission: ModelExecutionAdmission)
        (answer: ModelExecutionPeerAnswer)
        : Result<'T, ModelExecutionRefusal> =
        match answerBody<'T> answer with
        | Ok body -> Ok body
        | Error refusal ->
            let known = Set.union admission.Operations admission.Diagnostics |> Set.toList

            Error(ModelExecutionPeerRefusal.toSubmitterRefusal known refusal)

    let private answered (body: 'T) =
        ModelExecutionPeerAnswer.Answered(JsonRpc.serialize body)

    let private submitterRefused (refusal: ModelExecutionRefusal) =
        ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.SubmitterRefused refusal)

    // ── Argument marshalling ─────────────────────────────────────────
    //
    // Every operation takes exactly one positional argument: the request
    // envelope, embedded as a string (§3.1 rule 12). Hand-marshalled
    // rather than reflected so the bytes that cross the seam are the
    // bytes the corpus pins — a reflection shim that re-encoded the
    // envelope would produce a document the specification does not
    // describe.

    /// The positional-argument array a modeller sends.
    let arguments (request: ModelExecutionPeerRequest) : string =
        JsonRpc.serialize [ JsonRpc.serialize request ]

    let private soleArgument (argsJson: string) : Result<string, ModelExecutionPeerRefusal> =
        try
            match JsonRpc.deserialize<string list> argsJson with
            | [ single ] -> Ok single
            | other ->
                Error(
                    ModelExecutionPeerRefusal.RequestUnreadable
                        $"a model-execution call carries exactly one embedded request argument; this one carried {List.length other}"
                )
        with ex ->
            Error(ModelExecutionPeerRefusal.RequestUnreadable ex.Message)

    // ── The submitter contract ───────────────────────────────────────

    /// The registry read a fit job waits on: the outcome keyed by the
    /// submission's own spec hash. Pure over its arguments and holding
    /// nothing between attempts (GP 12 rule 4).
    let private awaitOutcome
        (policy: ModelExecutionFitPollPolicy)
        (api: ModelExecutionApi)
        (specHash: string)
        : Async<Result<ModelExecutionOutcome option, ModelExecutionRefusal>> =
        let query: ModelExecutionOutcomeQuery = {
            SpecHashes = [ specHash ]
            DatasetVersions = []
            Statuses = []
            BatchId = None
        }

        let rec attempt (remaining: int) = async {
            match! api.QueryOutcomes(query, None, 1) with
            | Error e -> return Error e
            | Ok page ->
                match page.Outcomes with
                | outcome :: _ -> return Ok(Some outcome)
                | [] ->
                    if remaining <= 1 then
                        return Ok None
                    else
                        if policy.Interval > TimeSpan.Zero then
                            do! Async.Sleep(int policy.Interval.TotalMilliseconds)

                        return! attempt (remaining - 1)
        }

        attempt (max 1 policy.Attempts)

    /// Run one admitted submitter operation against a binding.
    let private runOperation
        (deps: ModelExecutionPeerDeps)
        (binding: ModelExecutionPeerBinding)
        (request: ModelExecutionPeerRequest)
        : Async<ModelExecutionPeerAnswer> =
        async {
            match request.Operation with
            | "SubmitFit" ->
                let submission = JsonRpc.deserialize<ModelExecutionPeerSubmission> request.Body

                match! binding.Api.SubmitFit(ofWireSubmission submission) with
                | Error e -> return submitterRefused e
                | Ok _ ->
                    match! awaitOutcome deps.FitPoll binding.Api submission.SpecHash with
                    | Error e -> return submitterRefused e
                    | Ok None ->
                        return
                            submitterRefused (ModelExecutionRefusal.NotFound $"outcome for spec {submission.SpecHash}")
                    | Ok(Some outcome) -> return answered (toWireOutcome outcome)

            | "GetOutcome" ->
                let keyHash = JsonRpc.deserialize<string> request.Body

                match! binding.Api.GetOutcome keyHash with
                | Error e -> return submitterRefused e
                | Ok outcome -> return answered (toWireOutcome outcome)

            | "QueryOutcomes" ->
                let query = JsonRpc.deserialize<ModelExecutionPeerQuery> request.Body

                let inner: ModelExecutionOutcomeQuery = {
                    SpecHashes = query.SpecHashes
                    DatasetVersions = query.DatasetVersions
                    Statuses = query.Statuses
                    BatchId = query.BatchId
                }

                match! binding.Api.QueryOutcomes(inner, query.Cursor, query.Limit) with
                | Error e -> return submitterRefused e
                | Ok page ->
                    return
                        answered {
                            Outcomes = page.Outcomes |> List.map toWireOutcome
                            NextCursor = page.NextCursor
                        }

            | "ResolveVintage" ->
                let vintage = JsonRpc.deserialize<ModelExecutionPeerVintage> request.Body

                match! binding.Api.ResolveDatasetVersion(vintage.DatasetId, vintage.Version) with
                | Error e -> return submitterRefused e
                | Ok resolved -> return answered (toWireVintage resolved)

            // ── Phase 643 — the bounded views ────────────────────────
            //
            // Reached only when this deployment DECLARED views (else the
            // names are off the admitted set) and the peer's binding
            // grants `ViewOnly` or above (else `admit` refused the
            // request as an authority question). Both gates are above
            // this arm and neither is re-checked here: a check written
            // twice is a check that can come to disagree with itself.
            //
            // The `None` arms are unreachable for the same reason, and
            // are written rather than assumed — `withViews` and `Views`
            // are two declarations a composition could get half right,
            // and the honest answer to "you admitted an operation you
            // cannot serve" is the undeclared class, not an exception in
            // a request path.
            | ModelExecutionProfile.ListViewsOperation ->
                match deps.Views with
                | None ->
                    return
                        ModelExecutionPeerAnswer.Refused(
                            ModelExecutionPeerRefusal.UndeclaredDiagnostic request.Operation
                        )
                | Some views ->
                    let! declared = PeerView.list views binding.ScopeId
                    return answered declared

            | ModelExecutionProfile.DescribeViewOperation ->
                match deps.Views with
                | None ->
                    return
                        ModelExecutionPeerAnswer.Refused(
                            ModelExecutionPeerRefusal.UndeclaredDiagnostic request.Operation
                        )
                | Some views ->
                    let viewId = JsonRpc.deserialize<string> request.Body

                    match! PeerView.describe views binding.ScopeId viewId with
                    | Error refusal ->
                        return ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.ViewRefused refusal)
                    | Ok declaration -> return answered declaration

            | ModelExecutionProfile.RenderViewOperation ->
                match deps.Views with
                | None ->
                    return
                        ModelExecutionPeerAnswer.Refused(
                            ModelExecutionPeerRefusal.UndeclaredDiagnostic request.Operation
                        )
                | Some views ->
                    let view = JsonRpc.deserialize<PeerViewRequest> request.Body

                    match! PeerView.render views binding.ScopeId binding.PeerId view with
                    | Error refusal ->
                        return ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.ViewRefused refusal)
                    | Ok artifact ->
                        // The render takes the federated-egress door HERE
                        // rather than in `runGoverned`, and takes it
                        // exactly once (that function's carve-out is the
                        // other half of this). The reason is the audit
                        // the level is sold on: keyed by operation alone
                        // every render produces the same record, and
                        // "which peer viewed which series when" is
                        // answerable only if the reference set names the
                        // series the artifact actually covers.
                        let! decision =
                            PeerVisibilityEgress.routeView
                                binding.Egress
                                binding.ScopeId
                                binding.PeerId
                                request.Operation
                                artifact.ViewId
                                artifact.Series

                        if PeerEgressDecision.cleared decision then
                            return answered artifact
                        else
                            return
                                ModelExecutionPeerAnswer.Refused(
                                    ModelExecutionPeerRefusal.EgressWithheld request.Operation
                                )

            // ── Phase 644 — the lifecycle transition ─────────────────
            //
            // Reached only when this deployment DECLARED transitions
            // (else the name is off the admitted set). The peer's own
            // GRANT is not checked here and is not checked by `admit`
            // either: it is checked inside `ModelTransition.invoke`,
            // which is the author-agnostic seam, and putting it there
            // rather than in the seam's prologue is deliberate. A local
            // admin screen and a promotion policy must be judged by the
            // same function against the same graph; a grant check
            // hoisted into this file would be one that only peers are
            // subject to, and the phase's whole claim is that there is
            // one state machine with three authors.
            | ModelExecutionProfile.InvokeTransitionOperation ->
                match deps.Transitions with
                | None ->
                    return
                        ModelExecutionPeerAnswer.Refused(
                            ModelExecutionPeerRefusal.UndeclaredDiagnostic request.Operation
                        )
                | Some transitions ->
                    let invocation = JsonRpc.deserialize<PeerTransitionInvocation> request.Body

                    match PeerTransition.target invocation with
                    // A target label naming no status this profile
                    // defines is refused as an UNREADABLE REQUEST, not as
                    // an illegal transition. The lifecycle graph has
                    // nothing to say about a word that names no state,
                    // and reporting one as a forbidden edge would send a
                    // caller looking for a different edge when what it
                    // needs is a different word. This is also what keeps
                    // the transition vocabulary at exactly three classes,
                    // each of them a judgment.
                    | None ->
                        return
                            ModelExecutionPeerAnswer.Refused(
                                ModelExecutionPeerRefusal.RequestUnreadable
                                    $"'{invocation.Target}' names no model-artifact lifecycle status"
                            )
                    | Some target ->
                        let seamRequest = PeerTransition.toRequest binding.PeerId target invocation

                        match!
                            ModelTransition.invoke transitions binding.ScopeId binding.TransitionAuthority seamRequest
                        with
                        | Error refusal ->
                            return ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.TransitionRefused refusal)
                        | Ok recorded -> return answered (PeerTransition.toWireRecord recorded)

            // ── Phase 646 — the promotion transfer ───────────────────
            //
            // Reached only when this deployment DECLARED transfers (else
            // the name is off the admitted set). The peer's GRANT is not
            // checked here, for the reason the transition arm above does
            // not check it: it is checked inside `ModelPromotion.judge`,
            // which reaches `ModelTransition.judge` for the lifecycle
            // half, so a local promotion and a peer's are decided by one
            // function against one graph. A grant check hoisted into this
            // file would be one only peers were subject to.
            | ModelExecutionProfile.TransferPromotionOperation ->
                match deps.Promotions with
                | None ->
                    return
                        ModelExecutionPeerAnswer.Refused(
                            ModelExecutionPeerRefusal.UndeclaredDiagnostic request.Operation
                        )
                | Some promotions ->
                    let transfer = JsonRpc.deserialize<PeerPromotionTransfer> request.Body

                    match PeerPromotion.target transfer with
                    // Same distinction §5.7.11 draws: a label naming no
                    // status is an unreadable REQUEST, not an illegal
                    // edge. "That is not a state" and "that is not an
                    // edge" have different remedies and only one of them
                    // is a different edge.
                    | None ->
                        return
                            ModelExecutionPeerAnswer.Refused(
                                ModelExecutionPeerRefusal.RequestUnreadable
                                    $"'{transfer.Target}' names no model-artifact lifecycle status"
                            )
                    | Some target ->
                        match PeerPromotion.toPromoted binding.PeerId target transfer with
                        // Base64 that does not decode is likewise an
                        // unreadable request rather than a hash mismatch:
                        // a sender whose encoding is malformed has a
                        // different bug from one whose bytes were altered
                        // in flight, and telling it the digest disagreed
                        // would send it to check the wrong thing.
                        | Error reason ->
                            return ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.RequestUnreadable reason)
                        | Ok promoted ->
                            match!
                                ModelPromotion.accept promotions binding.ScopeId binding.TransitionAuthority promoted
                            with
                            | Error refusal ->
                                return
                                    ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.PromotionRefused refusal)
                            | Ok recorded -> return answered (PeerPromotion.toWireRecord recorded)

            // Unreachable: `admit` has already refused anything outside
            // the admitted operation set. Kept total rather than assumed
            // — a surface whose default branch is an exception is one
            // whose closure claim rests on a match nobody re-checks.
            | other -> return ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.UndeclaredDiagnostic other)
        }

    /// Resolve the binding, admit the request, and hand both to `run`.
    /// The shared prologue of every dispatch on the seam — written once
    /// so no path can be given a slightly weaker one.
    let private governed
        (resolveBinding: string -> Async<ModelExecutionPeerBinding option>)
        (admission: ModelExecutionAdmission)
        (peerId: string)
        (argsJson: string)
        (run: ModelExecutionPeerBinding -> ModelExecutionPeerRequest -> Async<ModelExecutionPeerAnswer>)
        : Async<ModelExecutionPeerAnswer> =
        async {
            match! resolveBinding peerId with
            | None -> return ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.PeerUnbound peerId)
            | Some binding ->
                match soleArgument argsJson with
                | Error refusal -> return ModelExecutionPeerAnswer.Refused refusal
                | Ok document ->
                    // Phase 642 — the scope walk runs HERE, once, on the
                    // one path every dispatch takes. Resolving it inside
                    // `run` would put it after the handler has been
                    // chosen; resolving it per operation would let a path
                    // be given a slightly weaker one, which is the whole
                    // reason this prologue is written once.
                    let admission =
                        admission
                        |> ModelExecutionAdmission.withAuthority (
                            PeerVisibility.resolve binding.Visibility binding.PeerId
                        )

                    match read admission binding.ScopeId document with
                    | Error refusal -> return ModelExecutionPeerAnswer.Refused refusal
                    | Ok request -> return! run binding request
        }

    /// Phase 642 — run an admitted operation and take its answer through
    /// the federated-egress door before it crosses the seam.
    ///
    /// The level admitted the REQUEST; this decides whether what the
    /// answer CARRIES may leave (see `PeerVisibilityEgress` for why those
    /// are different questions). It wraps `runOperation` rather than
    /// sitting inside `governed` for one reason: `governed` is also the
    /// pre-schedule admission check on the long-running leg, whose "run"
    /// answers nothing at all, and routing that empty answer would spend
    /// a gate check — and write an audit row — for a crossing that is not
    /// happening yet. The real answer still routes, on the execution
    /// side, where it exists.
    ///
    /// A refusal never rides the door either: there is nothing in it to
    /// disclose.
    let private runGoverned
        (deps: ModelExecutionPeerDeps)
        (binding: ModelExecutionPeerBinding)
        (request: ModelExecutionPeerRequest)
        : Async<ModelExecutionPeerAnswer> =
        async {
            let! answer = runOperation deps binding request

            match answer with
            | ModelExecutionPeerAnswer.Refused _ -> return answer
            // Phase 643 — a render has ALREADY taken the door, with a
            // reference set naming the series it covered. Routing it
            // again here would spend a second gate check and write a
            // second, less informative audit row for one crossing.
            | ModelExecutionPeerAnswer.Answered _ when request.Operation = ModelExecutionProfile.RenderViewOperation ->
                return answer
            // Phase 644 — a transition answer does not take the
            // disclosure door, and this is the one carve-out on this
            // function that is about the ANSWER rather than about
            // avoiding a second crossing.
            //
            // The door asks "may this data leave". The answer to a
            // transition is not data leaving; it is the RECEIPT of a
            // write this deployment has already committed. Withholding it
            // would leave the caller unable to learn the outcome of a
            // change that happened — the worst of both, since the state
            // moved and the mover was told nothing — and it would arrive
            // as `EgressWithheld`, a class that says the answer
            // references data the disclosure plane held back, which would
            // be false. Every member of the record is metadata about the
            // caller's own request.
            //
            // The transition is not therefore unaudited: it is recorded
            // on the attributed trail by the seam itself, admitted or
            // refused, which is a stronger record than an egress row.
            | ModelExecutionPeerAnswer.Answered _ when
                request.Operation = ModelExecutionProfile.InvokeTransitionOperation
                ->
                return answer
            // Phase 646 — a promotion receipt takes the same carve-out,
            // and by an argument the transfer makes more sharply than the
            // transition does. The door asks "may this data leave"; every
            // member of a promotion receipt is either something the CALLER
            // ITSELF SENT (the artifact key, the attachment digests) or a
            // statement about a write this deployment has just committed
            // on the caller's instruction. There is nothing in it the
            // caller did not already have or is not entitled to learn —
            // and a withheld receipt would leave a builder unable to
            // discover whether the artifact it is about to be retired in
            // favour of actually landed, which is the exact failure this
            // phase exists to prevent.
            | ModelExecutionPeerAnswer.Answered _ when
                request.Operation = ModelExecutionProfile.TransferPromotionOperation
                ->
                return answer
            | ModelExecutionPeerAnswer.Answered _ ->
                let! decision =
                    PeerVisibilityEgress.route binding.Egress binding.ScopeId binding.PeerId request.Operation

                if PeerEgressDecision.cleared decision then
                    return answer
                else
                    return ModelExecutionPeerAnswer.Refused(ModelExecutionPeerRefusal.EgressWithheld request.Operation)
        }

    /// The queued leg's job handler. It re-resolves the binding from the
    /// owner peer id carried on the payload, because the execution side
    /// has no request and no principal — the same reason
    /// `PeerJobHandler` takes its owner from the payload rather than
    /// from an ambient context (GP 12 rules 1 and 4).
    ///
    /// **Phase 644 — one handler, parameterised by method, and it
    /// records the terminal outcome.** Two things changed here and both
    /// are the same argument. The handler is no longer fit-specific
    /// because the lifecycle transition rides the identical shape
    /// (schedule, park, poll) and a second copy would be a second place
    /// for the admission prologue to drift. And it now emits the Phase
    /// 310 `PeerJobCompleted` row, which this file's hand-built handler
    /// never did: Phase 310 gave every REFLECTED long-running method a
    /// terminal row, and a contract that builds its own dispatch closure
    /// — which is exactly what this one does, for the reasons in the
    /// header — silently opted out of it. So every long-running call on
    /// this profile was logged at DISPATCH and never at completion, and
    /// the transparency contract reported the schedule as the outcome.
    ///
    /// The `Outcome` string is the profile's own refusal CLASS rather
    /// than a generic error name, because a queued transition that was
    /// refused for want of authority and one refused for an illegal edge
    /// are the two rows an operator is actually trying to tell apart.
    type private PeerOperationJobHandler(deps: ModelExecutionPeerDeps, fusion: PeerJobFusion, methodName: string) =

        /// Record the terminal outcome, best-effort. A flaky audit store
        /// must never change the outcome of a job whose result is already
        /// durably parked — so this runs AFTER `SaveResult` and swallows
        /// its own failures, exactly as `PeerJobHandler` does.
        let recordTerminalOutcome (envelope: PeerJobPayload) (jobId: PeerJobId) (answer: ModelExecutionPeerAnswer) = async {
            try
                let payload: PeerJobCompletedPayload = {
                    ContractId = ModelExecutionProfile.ContractId
                    MethodName = methodName
                    CallerPeerId = envelope.OwnerPeerId
                    RootRequestId = envelope.RootRequestId
                    JobId = jobId
                    Succeeded =
                        match answer with
                        | ModelExecutionPeerAnswer.Answered _ -> true
                        | ModelExecutionPeerAnswer.Refused _ -> false
                    Outcome =
                        match answer with
                        | ModelExecutionPeerAnswer.Answered _ -> "ok"
                        | ModelExecutionPeerAnswer.Refused refusal -> ModelExecutionPeerRefusal.className refusal
                    OccurredAt = DateTimeOffset.UtcNow
                }

                do!
                    fusion.AuditLog.Record(
                        AuditScope.platform,
                        AuditSubject.system "PeerHost",
                        PeerAuditEvent.event (PeerJobCompleted payload)
                    )
                    |> AuditRow.orRefuse
            with _ ->
                ()
        }

        interface IJobHandler with
            member _.Execute(ctx: JobContext) = async {
                let envelope =
                    try
                        JsonRpc.deserialize<PeerJobPayload> ctx.Payload
                    with _ -> {
                        OwnerPeerId = ""
                        ArgsJson = ctx.Payload
                        RootRequestId = ""
                    }

                // A job scheduled before the correlation id rode the
                // payload parses cleanly and leaves it null — a missing
                // field is absence, not a parse failure — so normalise
                // rather than trusting the shape.
                let envelope = {
                    envelope with
                        RootRequestId =
                            if isNull (box envelope.RootRequestId) then
                                ""
                            else
                                envelope.RootRequestId
                }

                let! answer =
                    governed
                        deps.ResolveBinding
                        deps.Admission
                        envelope.OwnerPeerId
                        envelope.ArgsJson
                        (runGoverned deps)

                do!
                    fusion.ResultStore.SaveResult(
                        ctx.ScopeId,
                        ctx.JobId,
                        envelope.OwnerPeerId,
                        PeerJobStatus.Completed(JsonRpc.serialize answer)
                    )

                do! recordTerminalOutcome envelope ctx.JobId answer
                return Success
            }

    /// Schedule a queued operation and answer with its job id, which the
    /// caller polls at `GET /peer/v1/{contractId}/jobs/{jobId}`
    /// (§5.5.6). The validated caller's peer id and the receiver-derived
    /// correlation id ride the payload, so the parked result is owned by
    /// the peer that scheduled it and the terminal row joins the
    /// schedule-time one.
    let private scheduleOperation
        (methodName: string)
        (fusion: PeerJobFusion)
        (context: PeerCallContext)
        (argsJson: string)
        : Async<Result<string, PeerError>> =
        async {
            let payload: PeerJobPayload = {
                OwnerPeerId = context.Peer.PeerId
                ArgsJson = argsJson
                RootRequestId = context.RootRequestId
            }

            let registration: JobRegistration = {
                ScopeId = PeerJob.Scope
                Handler = PeerJob.handlerName ModelExecutionProfile.ContractId methodName
                Payload = JsonRpc.serialize payload
                Trigger = Manual
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = JobPrecision.Minute
                CreatedBy = PeerJob.SourceModule
                Tags = Map.empty
            }

            match! fusion.Scheduler.Schedule registration with
            | Error _ -> return Error(PeerHandler $"failed to schedule the federated '{methodName}' call")
            | Ok(Scheduled jobId | Coalesced jobId) ->
                match! fusion.Scheduler.TriggerOnce(PeerJob.Scope, jobId, UserId PeerJob.SourceModule) with
                | Error refusal ->
                    return
                        Error(
                            PeerHandler
                                $"failed to trigger the federated '{methodName}' call: {TriggerError.message refusal}"
                        )
                | Ok() -> return Ok(JsonRpc.serialize jobId)
        }

    /// The operations this profile serves on the QUEUED leg.
    ///
    /// `SubmitFit` because a fit takes as long as it takes. Phase 644's
    /// `InvokeTransition` because a lifecycle judgment across a trust
    /// boundary is exactly the kind of act a data host may not want to
    /// answer synchronously: an approval that a promotion policy
    /// (Phase 645), a second signature or a human reviewer has to weigh
    /// is not a request-scoped computation, and a seam that answered it
    /// immediately today would have to grow a second shape to defer it
    /// tomorrow. One shape, from the first day — and the poll leg the
    /// specification already describes (§5.5.6) is where the caller
    /// collects it.
    ///
    /// Phase 646's `TransferPromotion` for the same reason and one more:
    /// a transfer carries bytes, and a data host that must hash, store and
    /// sign them has a unit of work whose duration is a function of what
    /// the caller sent rather than of what the receiver decided. A
    /// synchronous seam would make a large but entirely legitimate
    /// transfer indistinguishable from a wedged one.
    let private queuedOperations: Set<string> =
        Set.ofList [
            "SubmitFit"
            ModelExecutionProfile.InvokeTransitionOperation
            ModelExecutionProfile.TransferPromotionOperation
        ]

    /// The submitter contract's registration.
    ///
    /// `fusion` is `None` on a deployment with no job substrate. The
    /// immediate operations are unaffected; a queued operation refuses
    /// with a typed `SubstrateDisabled` carried through the profile's own
    /// passthrough class, so a modeller learns that this data host
    /// cannot run fits (or record transitions) rather than that something
    /// broke.
    let registration (deps: ModelExecutionPeerDeps) (fusion: PeerJobFusion option) : PeerContractRegistration =
        let dispatch: PeerDispatch =
            fun context methodName argsJson -> async {
                match Set.contains methodName queuedOperations, fusion with
                | true, Some f ->
                    // Admission runs BEFORE the job is scheduled: an
                    // unbound peer, a row-read probe or a scope-widening
                    // assertion must not become a queued job that a
                    // background worker then refuses out of sight.
                    let! precheck =
                        governed deps.ResolveBinding deps.Admission context.Peer.PeerId argsJson (fun _ _ -> async {
                            return ModelExecutionPeerAnswer.Answered ""
                        })

                    match precheck with
                    | ModelExecutionPeerAnswer.Refused _ -> return Ok(JsonRpc.serialize precheck)
                    | ModelExecutionPeerAnswer.Answered _ -> return! scheduleOperation methodName f context argsJson

                | true, None ->
                    return
                        Ok(
                            JsonRpc.serialize (
                                submitterRefused (ModelExecutionRefusal.SubstrateDisabled "job scheduler")
                            )
                        )

                | false, _ ->
                    let! answer =
                        governed deps.ResolveBinding deps.Admission context.Peer.PeerId argsJson (fun binding request ->
                            if request.Operation <> methodName then
                                async {
                                    return
                                        ModelExecutionPeerAnswer.Refused(
                                            ModelExecutionPeerRefusal.UndeclaredDiagnostic request.Operation
                                        )
                                }
                            else
                                runGoverned deps binding request)

                    return Ok(JsonRpc.serialize answer)
            }

        {
            ContractId = ModelExecutionProfile.ContractId
            Versions = [ ModelExecutionProfile.contractVersion ]
            Dispatch = dispatch
        }

    /// The submitter contract plus the job handlers its queued leg needs
    /// — the shape `PeerOptions.withContract` consumes.
    ///
    /// One handler per queued operation, registered under the name the
    /// dispatch side schedules against, so the two halves cannot come to
    /// disagree about which handler runs a call.
    let host (deps: ModelExecutionPeerDeps) (fusion: PeerJobFusion option) : PeerContractHost = {
        Registration = registration deps fusion
        AnswerBounds = None
        JobHandlers =
            match fusion with
            | None -> []
            | Some f ->
                queuedOperations
                |> Set.toList
                |> List.map (fun methodName ->
                    PeerJob.handlerName ModelExecutionProfile.ContractId methodName,
                    PeerOperationJobHandler(deps, f, methodName) :> IJobHandler)
    }

    // ── The governed-diagnostics contract ────────────────────────────

    /// The ungated diagnostics registration. **Private on purpose.**
    ///
    /// Making it public would be the whole argument undone: the only
    /// exported route is `governedDiagnostics`, whose signature demands
    /// a broker and a template and whose return type is
    /// `GatedContractRegistration` — so a composition cannot register
    /// the projections without the gate, because there is no value of
    /// the right shape to hand it.
    let private diagnosticsRegistration
        (template: CleanRoomTemplate)
        (deps: ModelExecutionDiagnosticsDeps)
        : PeerContractRegistration =
        let dispatch: PeerDispatch =
            fun context methodName argsJson -> async {
                match! deps.ResolveBinding context.Peer.PeerId with
                | None -> return Error(PeerHandler "no model-execution binding")
                | Some binding ->
                    match soleArgument argsJson with
                    | Error refusal -> return Error(PeerHandler(ModelExecutionPeerRefusal.describe refusal))
                    | Ok document ->
                        // The diagnostics seam admits at the ENVELOPE
                        // level only: which projections are answerable is
                        // the gate's surface invariant, already decided
                        // before this dispatch ran.
                        let admission = {
                            ProfileVersion = ModelExecutionProfile.Version
                            Operations = Set.empty
                            Diagnostics = ModelExecutionProfile.diagnostics
                            // Phase 642 — resolved from the same binding
                            // the submitter seam resolves from. Every
                            // governed diagnostic requires
                            // `AggregatesOnly`, so this admits whatever
                            // the gate is about to judge; carrying it
                            // anyway is what stops the two seams drifting
                            // when a later projection is classified
                            // higher.
                            Authority = PeerVisibility.resolve binding.Visibility binding.PeerId
                        }

                        match read admission binding.ScopeId document with
                        | Error refusal -> return Error(PeerHandler(ModelExecutionPeerRefusal.describe refusal))
                        // The method the gate checked against the surface
                        // and the operation the envelope names must be the
                        // same projection, or one would be judged and the
                        // other answered.
                        | Ok request when request.Operation <> methodName ->
                            return
                                Error(
                                    PeerHandler
                                        $"the envelope names '{request.Operation}' and the call is to '{methodName}'"
                                )
                        | Ok request ->
                            let! result =
                                if Set.contains methodName FitDiagnostics.all then
                                    async {
                                        let fit =
                                            JsonRpc.deserialize<ModelExecutionPeerFitDiagnosticRequest> request.Body

                                        // Composition refused a template whose
                                        // declaration does not read, so this is
                                        // the declaration the approval covers.
                                        match
                                            Map.tryFind methodName template.Release.Declarations
                                            |> Option.map FitDiagnosticDeclaration.parse
                                        with
                                        | Some(Ok declaration) ->
                                            match! deps.FitObservations binding fit.OutcomeId with
                                            | None ->
                                                return Error $"no fit outcome '{fit.OutcomeId}' in this peer's scope"
                                            | Some observations ->
                                                return
                                                    FitDiagnostics.project
                                                        methodName
                                                        declaration
                                                        template.Floor
                                                        observations
                                        | _ -> return Error $"the template declares no binning for '{methodName}'"
                                    }
                                else
                                    async {
                                        let projection =
                                            JsonRpc.deserialize<ModelExecutionPeerDiagnosticRequest> request.Body

                                        let! result = deps.Project binding methodName projection
                                        return Ok result
                                    }

                            match result with
                            | Error reason -> return Error(PeerHandler reason)
                            // Every answer here is one-dimensional, so a label
                            // carrying the grid separator would read back as a
                            // grid cell it is not: refused, never released.
                            | Ok cohort when not (cohort.Cells |> List.forall (fun c -> CellLabel.isPlain c.Label)) ->
                                return
                                    Error(
                                        PeerHandler
                                            $"'{methodName}' answered a cell label containing the grid separator U+241F"
                                    )
                            | Ok cohort -> return Ok(JsonRpc.serialize cohort)
            }

        {
            ContractId = ModelExecutionProfile.DiagnosticsContractId
            Versions = [ ModelExecutionProfile.contractVersion ]
            Dispatch = dispatch
        }

    /// The governed-diagnostics contract, gated.
    ///
    /// **The only construction route**, and the reason it takes the gate
    /// as a value rather than reading it from a composition: a deployment
    /// cannot end up believing it gated a surface it did not, because
    /// there is no un-gated value of this contract to register.
    ///
    /// `gate.Template.AllowedMethods` is the declaration of what this
    /// deployment answers. The gate refuses anything off it before the
    /// projection runs, and withholds any answer that is not a
    /// `CohortResult`, which is what closes the surface against row egress
    /// rather than merely discouraging it. Every release is charged to
    /// `gate.Budget`, and a template that switches noise on has its
    /// released cells noised from `gate.Noise`.
    ///
    /// Refused at composition, by name, when the template offers a fit
    /// diagnostic without a usable binning, or switches noise on with no
    /// mechanism to draw it.
    let governedDiagnostics
        (gate: ModelExecutionDiagnosticsGate)
        (deps: ModelExecutionDiagnosticsDeps)
        : Result<GatedContractRegistration, ModelExecutionTemplateRefusal list> =
        let refusals =
            ModelExecutionTemplateRefusal.check gate.Template
            @ (match gate.Template.Release.Noise, gate.Noise with
               | Some _, None -> [ ModelExecutionTemplateRefusal.NoiseMechanismAbsent gate.Template.TemplateId ]
               | _ -> [])

        if not refusals.IsEmpty then
            Error refusals
        else
            // An un-noised release is counted at zero ε; a noised one is
            // charged its policy's ε by the gate itself, which reads the
            // noise policy in place of this schedule.
            let meter = {
                gate.Budget with
                    Policy = {
                        gate.Budget.Policy with
                            EpsilonPerQuery = 0m
                            MethodEpsilon = Map.empty
                    }
            }

            let noise =
                gate.Template.Release.Noise
                |> Option.bind (fun policy -> gate.Noise |> Option.map (fun mechanism -> mechanism, policy))

            Ok(
                CleanRoomGate.wrapNoised
                    gate.Broker
                    gate.Template
                    gate.Approval
                    (Some meter)
                    noise
                    gate.Audit
                    (diagnosticsRegistration gate.Template deps)
            )
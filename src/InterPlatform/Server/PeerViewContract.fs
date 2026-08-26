// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Collections.Concurrent
open System.Globalization
open System.Security.Cryptography

// ─── Phase 643 — `ViewOnly`: server-rendered bounded data views ──────
//
// Phase 642 named the level; this is the machinery that makes it do
// something. At `ViewOnly` a remote modeller may EYEBALL raw series
// without any export route: the data is rendered where it lives, and the
// RENDERING crosses the seam.
//
// Four properties carry the whole design, and each is structural rather
// than a rule somebody has to remember:
//
//   1. **The response is an artifact, never a payload.** The only shape
//      an answer can take is `PeerViewArtifact`, whose content member is
//      base64 of rendered bytes under a declared media type. There is no
//      member a row, a series or a point could ride in — the same
//      argument §5.7.4's outcome table makes, applied one level up. The
//      series themselves are read data-side (`PeerViewDeps.ReadSeries`),
//      handed to the renderer, and never serialised.
//   2. **Every request is bounded by a DECLARATION.** A view is a
//      `PeerViewDeclaration`: which dataset, which series, which
//      resolutions, how wide a window, how many series per request, how
//      many renders per window. A request is validated against it and an
//      over-bound one is refused with a typed class naming which bound it
//      exceeded. Declaring nothing offers nothing — the posture the
//      governed-diagnostics template already takes.
//   3. **The request cannot name a dataset.** It names a VIEW and a
//      version; the declaration binds the dataset. So a peer cannot
//      address a dataset through a view that does not cover it by
//      construction, exactly as it cannot address another tenant's scope
//      because the vintage reference has no scope member (GP 4).
//   4. **Rendering goes through the deployment's own chart grammar.**
//      This substrate never draws anything. It builds the prop bag that
//      grammar already reads and hands it to a composition-supplied
//      renderer — so there is one grammar with three surfaces (published
//      pages, decks, federated views) rather than a second renderer that
//      slowly disagrees with the first.
//
// ── Why the renderer is a supplied function and not a reference (GP 1)
//
// The deterministic server-side chart renderer lives in a different
// companion package. A companion never reaches into another — the
// argument `PeerVisibilityEgress` makes about the fact companion — so
// the peer substrate takes the renderer as data:
//
// ```fsharp
// let renderer: PeerViewRenderer = {
//     MediaType = "image/svg+xml"
//     Render =
//         fun bags ->
//             bags
//             |> List.map (NarrativeCharts.renderChart >> RenderView.AsString.htmlNode)
//             |> String.concat ""
//             |> Encoding.UTF8.GetBytes
// }
// ```
//
// What keeps that wiring from drifting into a parallel grammar is
// `PeerView.chartProps` / `chartPropsAt`: they emit the SAME prop keys
// and the SAME point encoding the chart component's own projector emits
// — including [Phase 649]'s `chart.datasetVintage` binding, which a
// bounded view always knows because the declaration binds the dataset
// and the request pins the version — and a conformance test asserts each
// against that projector rather than trusting prose. The layout of
// several series into one artifact is the composition's business,
// deliberately — how a deployment arranges its own charts is not a wire
// concern.
//
// ── The honesty boundary, restated where the machinery is ────────────
//
// A rendered artifact contains the values it renders, and a counterparty
// can read them off the screen or out of the SVG. `ViewOnly` prevents
// BULK PROGRAMMATIC EGRESS and provides AUDIT: there is no shape here a
// peer can loop over to pull a series, every render is bounded, rate-
// limited, and recorded through the deployment's own disclosure plane.
// It does not prevent human capture of a rendered screen, and nothing
// here should be described as if it did.
//
// ── Cost when unused (GP 11 / GP 13) ─────────────────────────────────
//
// Nothing composes any of this. A deployment that declares no views has
// no `PeerViewDeps`, admits no view operation, constructs no rate guard
// and allocates nothing — and a peer asking for a view is refused by the
// authority check at `AggregatesOnly` or by the declaration check above
// it, exactly as it was before this file existed.

// ─── Wire shapes (FEDERATION_WIRE.md §5.7.10) ────────────────────────

/// The window a view is asked to render, as two instants.
///
/// A bound rather than a hint: the declaration caps how wide it may be,
/// and a wider one is refused rather than silently truncated. Truncating
/// would answer a question the caller did not ask and would leave the
/// caller unable to tell a narrow dataset from a narrowed request.
type PeerViewWindow = {
    From: DateTimeOffset
    To: DateTimeOffset
}

/// What a data host DECLARES it will render for its peers.
///
/// The declaration is the offer, and it is the whole offer: a series not
/// named here cannot be rendered, a resolution not named here cannot be
/// asked for, and the three numeric members are the volume bounds the
/// receiver enforces data-side. Declaring nothing offers nothing.
///
/// This document CROSSES THE SEAM (it is what `DescribeView` and
/// `ListViews` answer), so it carries nothing a counterparty should not
/// have: series names it may already ask for, bounds it needs in order
/// to stay inside them, and no internal identifier of any kind.
type PeerViewDeclaration = {
    /// The view's stable id — what a request names.
    ViewId: string
    /// The dataset this view reads, scope-relatively. **Named here and
    /// not on the request**: a peer selects a view, never a dataset, so
    /// a view cannot be pointed at data it does not cover.
    DatasetId: string
    /// A human-readable caption for the rendered artifact.
    Title: string
    /// The chart kind the view renders at — the grammar's own token
    /// (`"line"` / `"bar"` / `"area"`). The HOST decides how its data is
    /// drawn; a bounded view is not a plotting API.
    Kind: string
    /// The series a peer may name. Sorted ordinally.
    Series: string list
    /// The resolutions this view renders at (e.g. `"day"` / `"week"` /
    /// `"month"`). Sorted ordinally. A closed vocabulary per view rather
    /// than a profile-wide one, because what resolutions are meaningful
    /// is a property of the data and not of the protocol.
    Resolutions: string list
    /// The widest window, in whole days, a single request may cover.
    MaxWindowDays: int
    /// How many series one request may name.
    MaxSeriesPerRequest: int
    /// How many points of one series may be rendered. A ceiling on the
    /// ARTIFACT rather than on the request: the window and the resolution
    /// already bound what a caller asked for, and this bounds what the
    /// deployment's own reader hands back.
    MaxPointsPerSeries: int
    /// Renders admitted per peer per window.
    MaxRendersPerWindow: int
    /// The rate window, in seconds.
    RenderWindowSeconds: int
}

/// A request for one rendered view.
///
/// **No dataset member and no scope member**, and both absences are the
/// same argument: the view declaration binds the dataset and the peer
/// binding binds the scope, so neither can be widened by anything a
/// caller sends (GP 4).
type PeerViewRequest = {
    ViewId: string
    /// The pinned vintage of the view's declared dataset. A view names a
    /// version, never "latest", for the reason a fit does: an answer that
    /// resolved differently on re-run is not one a modeller can cite.
    DatasetVersion: int
    /// The series to render. Sorted ordinally — **the emitter owns the
    /// sort**, so two modellers asking for the same series in different
    /// orders produce the same document.
    Series: string list
    Window: PeerViewWindow
    Resolution: string
}

/// The answer: a rendered artifact and the description of what it shows.
///
/// **Every member is the artifact or metadata about it.** There is no
/// member a row, a series or a point could ride in, which is what makes
/// "views are not an export route" a fact about the shape rather than a
/// promise about an implementation.
type PeerViewArtifact = {
    ViewId: string
    /// What the bytes ARE — `"image/svg+xml"` for the shipped chart
    /// grammar. Carried so the artifact is self-describing and a later
    /// raster format needs no new shape.
    MediaType: string
    /// Base64 of the rendered bytes (§3.1: a string member, so the
    /// canonical encoding needs no new rule).
    ///
    /// Base64 rather than the markup inline, deliberately: it is
    /// media-type-agnostic, and it makes the member obviously an OPAQUE
    /// ARTIFACT rather than a document a caller is invited to walk.
    Content: string
    /// `sha256:<hex>` over the rendered bytes, in the profile's own hash
    /// form. What a modeller pins when it cites what it was shown, and
    /// what joins an artifact to the disclosure record of its release.
    ContentHash: string
    /// The series the artifact actually covers, ordinally sorted. Echoed
    /// so the artifact and the disclosure record name the same thing.
    Series: string list
    Window: PeerViewWindow
    Resolution: string
    /// Points rendered, summed over the series. A COUNT, which is not a
    /// point — the same distinction `ModelExecutionVintageInfo.RowCount`
    /// already makes.
    RenderedPoints: int
}

/// Why a view request was refused.
///
/// A vocabulary of its own rather than more cases on the seam's refusal
/// type, because these are all one thing said seven ways — "the request
/// left the declared bounds" — and a caller that can enumerate WHICH
/// bound can narrow and retry. The seam carries them through unchanged
/// (`ModelExecutionPeerRefusal.ViewRefused`), the way it already carries
/// the submitter surface's own vocabulary.
///
/// **Every payload here is something the caller already knows** — the
/// view it named, the series it named, the bound the deployment
/// published in its declaration. Nothing is disclosed by refusing.
[<RequireQualifiedAccess>]
type PeerViewRefusal =
    /// No view with that id is declared in this peer's scope.
    | UndeclaredView of viewId: string
    /// The request named a series this view does not carry. Checked
    /// BEFORE the count, because "that series is not in this view" and
    /// "you asked for too many" have different fixes.
    | UndeclaredSeries of viewId: string * series: string
    /// The request named no series at all. A render of nothing is not a
    /// narrower request; it is a request that cannot be answered.
    | NoSeriesRequested of viewId: string
    /// More series than the declaration admits in one request.
    | SeriesBudgetExceeded of viewId: string * requested: int * limit: int
    /// The window's end is not after its start.
    | WindowUnordered of viewId: string
    /// A wider window than the declaration admits.
    | WindowBudgetExceeded of viewId: string * requestedDays: int * limitDays: int
    /// A resolution this view does not render at.
    | UndeclaredResolution of viewId: string * resolution: string
    /// This peer has spent its render budget for the current window.
    ///
    /// **A refusal and not a wait**, which is the whole point of stating
    /// it: a caller told to try again in n seconds can schedule, whereas
    /// a caller left hanging on a timeout learns nothing and retries into
    /// the same wall. Exhaustion is a decision the receiver took, so it
    /// is reported as one.
    | RenderBudgetExhausted of viewId: string * limit: int * windowSeconds: int

[<RequireQualifiedAccess>]
module PeerViewRefusal =

    /// The stable refusal class (§7.2). The CASE is the contract; the
    /// wording below is not.
    let className (refusal: PeerViewRefusal) : string =
        match refusal with
        | PeerViewRefusal.UndeclaredView _ -> "model-execution-view-undeclared"
        | PeerViewRefusal.UndeclaredSeries _ -> "model-execution-view-series-undeclared"
        | PeerViewRefusal.NoSeriesRequested _ -> "model-execution-view-no-series"
        | PeerViewRefusal.SeriesBudgetExceeded _ -> "model-execution-view-series-budget"
        | PeerViewRefusal.WindowUnordered _ -> "model-execution-view-window-unordered"
        | PeerViewRefusal.WindowBudgetExceeded _ -> "model-execution-view-window-budget"
        | PeerViewRefusal.UndeclaredResolution _ -> "model-execution-view-resolution-undeclared"
        | PeerViewRefusal.RenderBudgetExhausted _ -> "model-execution-view-render-budget"

    /// Human-readable one-line description (logs + operator display).
    let describe (refusal: PeerViewRefusal) : string =
        match refusal with
        | PeerViewRefusal.UndeclaredView viewId -> $"no view '{viewId}' is declared for this peer"
        | PeerViewRefusal.UndeclaredSeries(viewId, series) -> $"view '{viewId}' does not carry the series '{series}'"
        | PeerViewRefusal.NoSeriesRequested viewId -> $"a render of view '{viewId}' must name at least one series"
        | PeerViewRefusal.SeriesBudgetExceeded(viewId, requested, limit) ->
            $"view '{viewId}' renders at most {limit} series per request; this one named {requested}"
        | PeerViewRefusal.WindowUnordered viewId -> $"the window for view '{viewId}' does not end after it starts"
        | PeerViewRefusal.WindowBudgetExceeded(viewId, requestedDays, limitDays) ->
            $"view '{viewId}' renders at most {limitDays} days per request; this one covered {requestedDays}"
        | PeerViewRefusal.UndeclaredResolution(viewId, resolution) ->
            $"view '{viewId}' does not render at resolution '{resolution}'"
        | PeerViewRefusal.RenderBudgetExhausted(viewId, limit, windowSeconds) ->
            $"view '{viewId}' admits {limit} renders per {windowSeconds}s for this peer, and that budget is spent"

// ─── Data-side shapes (these never cross the seam) ───────────────────

/// One point of a series, read data-side.
///
/// **Deliberately not a wire type.** It is the input to the renderer and
/// nothing else; no answer shape in this file has a member it could be
/// carried in, and that is the property that makes a view a view rather
/// than an export with extra steps.
type PeerViewPoint = { Label: string; Value: float }

/// One named series, read data-side for a render.
type PeerViewSeries = {
    Name: string
    /// In the order the axis should read; the renderer spaces them
    /// evenly and does not sort.
    Points: PeerViewPoint list
}

/// The deployment's own chart grammar, supplied as data.
///
/// Supplied rather than referenced because the grammar lives in another
/// companion and a companion never reaches into another (GP 1). The
/// substrate builds the prop bags — one per series, in the request's
/// ordinal order — and this turns them into the artifact's bytes.
type PeerViewRenderer = {
    /// What `Render` produces, e.g. `"image/svg+xml"`.
    MediaType: string
    /// One prop bag per requested series, in request order, to bytes.
    /// **How several series are arranged into one artifact is the
    /// deployment's business**, not the wire's — the wire fixes the
    /// document shape, and §5.6 already takes that posture for a host's
    /// own derivations.
    Render: Map<string, string> list -> byte[]
}

/// What one rate reservation decided, as data (GP 12 rule 3) — so a
/// refusal, an operator surface and a test read one value rather than
/// three reconstructions of it.
type PeerViewReservation = {
    /// Did this render get a slot?
    Admitted: bool
    /// Renders taken in the current window, saturating one above the
    /// limit. Diagnostic; the contract is `Admitted`.
    Used: int
    Limit: int
    WindowSeconds: int
}

/// The per-peer render budget, enforced data-side.
///
/// A seam rather than a fixed implementation for the ordinary reason
/// (GP 12): a deployment on more than one instance needs a shared
/// counter, and one baked into this file could not be replaced. Keyed by
/// peer id and view id by VALUE (rule 1), asynchronous at the boundary
/// (rule 2), and holding nothing between calls that a caller supplies
/// (rule 4).
type PeerViewRateGuard = {
    /// Reserve one render for `peerId` against the view's declared
    /// budget. Called only after a request has been validated: a
    /// malformed request must not spend a slot.
    Reserve: string -> PeerViewDeclaration -> Async<PeerViewReservation>
}

[<RequireQualifiedAccess>]
module PeerViewRateGuard =

    /// A fixed-window counter held in this process.
    ///
    /// **Single-instance, and it says so.** N instances of a deployment
    /// each admit the declared budget, so a horizontally-scaled data host
    /// supplies a shared-store guard instead. It is the shipped default
    /// because a bound enforced approximately is a bound, and no bound at
    /// all is not — but a deployment that needs the exact number across
    /// instances must not assume this one gives it.
    ///
    /// `now` is supplied rather than read from the clock so a test can
    /// assert a window boundary without sleeping through one.
    let inProcess (now: unit -> DateTimeOffset) : PeerViewRateGuard =
        let counters = ConcurrentDictionary<string * string, DateTimeOffset * int>()

        {
            Reserve =
                fun peerId declaration -> async {
                    let key = peerId, declaration.ViewId
                    let at = now ()
                    let limit = max 0 declaration.MaxRendersPerWindow
                    let seconds = max 1 declaration.RenderWindowSeconds
                    let window = TimeSpan.FromSeconds(float seconds)

                    let _, used =
                        counters.AddOrUpdate(
                            key,
                            (fun _ -> at, 1),
                            (fun _ (startedAt, taken) ->
                                if at - startedAt >= window then at, 1
                                elif taken > limit then startedAt, taken
                                else startedAt, taken + 1)
                        )

                    return {
                        Admitted = used <= limit
                        Used = used
                        Limit = limit
                        WindowSeconds = seconds
                    }
                }
        }

/// The substrate a deployment's declared views run over.
///
/// Keyed by scope id rather than by a peer binding, so the whole record
/// is expressible without naming any type the model-execution seam
/// defines (GP 12 rule 1 — identity by value). The binding supplies the
/// scope; nothing here can widen it.
type PeerViewDeps = {
    /// The views declared in a scope. `[]` offers none, which is the
    /// honest default: a view nobody declared is one nobody agreed to.
    Declarations: string -> Async<PeerViewDeclaration list>
    /// Read the series a VALIDATED request named, data-side.
    ///
    /// Its result never crosses the seam — it is the renderer's input.
    /// A reader that returns more points than the view declares is
    /// clamped rather than trusted, so a bound is enforced here even
    /// against the deployment's own reader.
    ReadSeries: string -> PeerViewRequest -> Async<PeerViewSeries list>
    Renderer: PeerViewRenderer
    Rate: PeerViewRateGuard
}

// ─── Validation + rendering ──────────────────────────────────────────

[<RequireQualifiedAccess>]
module PeerView =

    let private inv = CultureInfo.InvariantCulture

    /// The chart grammar's own prop keys. Named here so the one place
    /// this substrate speaks the grammar is greppable, and so a
    /// conformance test can assert they are the keys the grammar's own
    /// projector emits rather than three strings that happen to match
    /// today.
    [<Literal>]
    let KindProp = "chart.kind"

    [<Literal>]
    let TitleProp = "chart.title"

    [<Literal>]
    let PointsProp = "chart.points"

    /// The grammar's [Phase 649] dataset-vintage binding prop — the same
    /// key `ChartArtifact.DatasetVintageProp` and
    /// `NarrativeFromData.DatasetVintageProp` emit, so a reader that
    /// holds a rendered federated view recovers the binding exactly as it
    /// would from any other chart the grammar drew.
    ///
    /// A bounded peer view already knows its vintage: the DECLARATION
    /// binds the dataset and the REQUEST pins the version, and refusing
    /// either is `validate`'s business well before anything renders. So
    /// the artifact can say what it is a picture of, which is the whole
    /// point of a pinned vintage — an answer that resolved differently on
    /// re-run is not one a modeller can cite.
    ///
    /// The grammar's sibling binding prop, `chart.artifactKey`, is NOT
    /// emitted: a view renders a dataset series, not a governed result,
    /// so there is no artifact key to name and an empty one would say
    /// "bound to nothing" rather than "not bound".
    [<Literal>]
    let DatasetVintageProp = "chart.datasetVintage"

    /// The vintage token a rendered view carries: `"<datasetId>@<version>"`.
    ///
    /// Both halves, because neither alone re-derives the picture — a
    /// version number means nothing without the dataset it versions, and
    /// the dataset id alone is the thing a pinned vintage exists to
    /// refuse. Ordinary text to the grammar, which treats the prop as an
    /// opaque identifier.
    let vintageToken (datasetId: string) (datasetVersion: int) : string = $"{datasetId}@{datasetVersion}"

    /// The grammar's point encoding: `"label=value;…"`, values in the
    /// invariant culture, separators sanitised out of labels.
    ///
    /// Reproduced rather than referenced for the companion-isolation
    /// reason above, and pinned against the grammar's own projector by a
    /// conformance test — which is what makes this a shared surface
    /// rather than a second grammar.
    let private encodePoints (points: PeerViewPoint list) : string =
        points
        |> List.map (fun p ->
            let safe = p.Label.Replace(';', ' ').Replace('=', ' ')
            $"{safe}={p.Value.ToString(inv)}")
        |> String.concat ";"

    /// One series as the prop bag the chart component reads, UNBOUND. The
    /// series NAME is the caption, so an artifact carrying several series
    /// is legible without a legend the wire would have to describe.
    ///
    /// `render` uses `chartPropsAt`; this stays because it is the exact
    /// bag `NarrativeFromData.chart` emits, which is what the conformance
    /// test pins the encoding against.
    let chartProps (kind: string) (series: PeerViewSeries) : Map<string, string> =
        Map.ofList [
            KindProp, kind
            PointsProp, encodePoints series.Points
            TitleProp, series.Name
        ]

    /// `chartProps` plus the [Phase 649] dataset-vintage binding — what a
    /// rendered view actually carries, since a bounded view always knows
    /// its vintage. Byte-identical to `chartProps` except for the one
    /// added key (GP 11 for any consumer reading the other three).
    let chartPropsAt
        (kind: string)
        (datasetId: string)
        (datasetVersion: int)
        (series: PeerViewSeries)
        : Map<string, string> =
        chartProps kind series
        |> Map.add DatasetVintageProp (vintageToken datasetId datasetVersion)

    /// Whole days a window covers, rounded UP — a request covering any
    /// part of an extra day has covered it.
    let windowDays (window: PeerViewWindow) : int =
        int (ceil (window.To - window.From).TotalDays)

    /// Judge a request against the deployment's declarations.
    ///
    /// Pure and total over its inputs — no store read, no clock, nothing
    /// ambient — so the corpus can certify a reject vector against the
    /// shipped function rather than against a harness's idea of it, and
    /// so both halves of a dispatch judge identically.
    ///
    /// **The order is not incidental.** The view is resolved first
    /// because nothing else can be judged without a declaration. Series
    /// membership precedes the count, because naming a series this view
    /// does not carry and naming too many have different fixes. The
    /// window's shape precedes its width, because a window that ends
    /// before it starts has no meaningful width to report. The
    /// resolution is last because it is the only member whose refusal
    /// tells the caller nothing about the others.
    let validate
        (declarations: PeerViewDeclaration list)
        (request: PeerViewRequest)
        : Result<PeerViewDeclaration, PeerViewRefusal> =
        match declarations |> List.tryFind (fun d -> d.ViewId = request.ViewId) with
        | None -> Error(PeerViewRefusal.UndeclaredView request.ViewId)
        | Some declaration ->
            let undeclared =
                request.Series
                |> List.tryFind (fun s -> not (List.contains s declaration.Series))

            match undeclared with
            | Some series -> Error(PeerViewRefusal.UndeclaredSeries(declaration.ViewId, series))
            | None ->
                let requested = List.length request.Series

                if requested = 0 then
                    Error(PeerViewRefusal.NoSeriesRequested declaration.ViewId)
                elif requested > declaration.MaxSeriesPerRequest then
                    Error(
                        PeerViewRefusal.SeriesBudgetExceeded(
                            declaration.ViewId,
                            requested,
                            declaration.MaxSeriesPerRequest
                        )
                    )
                elif request.Window.To <= request.Window.From then
                    Error(PeerViewRefusal.WindowUnordered declaration.ViewId)
                elif windowDays request.Window > declaration.MaxWindowDays then
                    Error(
                        PeerViewRefusal.WindowBudgetExceeded(
                            declaration.ViewId,
                            windowDays request.Window,
                            declaration.MaxWindowDays
                        )
                    )
                elif not (List.contains request.Resolution declaration.Resolutions) then
                    Error(PeerViewRefusal.UndeclaredResolution(declaration.ViewId, request.Resolution))
                else
                    Ok declaration

    /// The declared views in a scope, ordinally ordered by id so two
    /// reads of an unchanged offer produce the same document.
    let list (deps: PeerViewDeps) (scopeId: string) : Async<PeerViewDeclaration list> = async {
        let! declared = deps.Declarations scopeId
        return declared |> List.sortWith (fun a b -> String.CompareOrdinal(a.ViewId, b.ViewId))
    }

    /// One view's declaration. `UndeclaredView` rather than an empty
    /// answer: "there is no such view" and "the view has nothing in it"
    /// are different facts and only one of them is true.
    let describe
        (deps: PeerViewDeps)
        (scopeId: string)
        (viewId: string)
        : Async<Result<PeerViewDeclaration, PeerViewRefusal>> =
        async {
            let! declared = deps.Declarations scopeId

            match declared |> List.tryFind (fun d -> d.ViewId = viewId) with
            | Some declaration -> return Ok declaration
            | None -> return Error(PeerViewRefusal.UndeclaredView viewId)
        }

    let private hashOf (bytes: byte[]) : string =
        "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    /// Validate, reserve, read, render.
    ///
    /// The order is the point of the function. Validation is first
    /// because a malformed request must not spend a peer's budget;
    /// reservation is second because a request that will not be answered
    /// must not read data; the read is third and the render fourth, so
    /// nothing leaves the deployment until every bound has held.
    let render
        (deps: PeerViewDeps)
        (scopeId: string)
        (peerId: string)
        (request: PeerViewRequest)
        : Async<Result<PeerViewArtifact, PeerViewRefusal>> =
        async {
            let! declared = deps.Declarations scopeId

            match validate declared request with
            | Error refusal -> return Error refusal
            | Ok declaration ->
                let! reservation = deps.Rate.Reserve peerId declaration

                if not reservation.Admitted then
                    return
                        Error(
                            PeerViewRefusal.RenderBudgetExhausted(
                                declaration.ViewId,
                                reservation.Limit,
                                reservation.WindowSeconds
                            )
                        )
                else
                    let! read = deps.ReadSeries scopeId request

                    // Clamped against the DECLARATION, not against the
                    // reader's good behaviour: a reader that returned a
                    // series nobody asked for, or more points than the
                    // view admits, must not be able to widen what
                    // crosses. The ordinal sort is the canonicalisation
                    // every list in this profile takes.
                    let ordered =
                        request.Series
                        |> List.choose (fun name -> read |> List.tryFind (fun s -> s.Name = name))
                        |> List.map (fun s -> {
                            s with
                                Points = s.Points |> List.truncate (max 0 declaration.MaxPointsPerSeries)
                        })

                    let bytes =
                        ordered
                        |> List.map (chartPropsAt declaration.Kind declaration.DatasetId request.DatasetVersion)
                        |> deps.Renderer.Render

                    return
                        Ok {
                            ViewId = declaration.ViewId
                            MediaType = deps.Renderer.MediaType
                            Content = Convert.ToBase64String bytes
                            ContentHash = hashOf bytes
                            Series =
                                ordered
                                |> List.map _.Name
                                |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                            Window = request.Window
                            Resolution = request.Resolution
                            RenderedPoints = ordered |> List.sumBy (fun s -> List.length s.Points)
                        }
        }
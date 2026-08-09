// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 647 — the chart-artifact handoff: this SDK's half of the deck
/// export seam.
///
/// Deck emission belongs to a downstream document-emission tier
/// ([`DeckExport`](DeckExport.fs)). What that tier cannot reasonably
/// re-derive is the deployment's OWN chart grammar — the declared,
/// deterministic server-side renderer that published pages and bounded
/// federated views already draw through. A second renderer over there
/// would be a second grammar, and two grammars disagree slowly and
/// invisibly: the deck's chart and the page's chart of the same series
/// drift a pixel, then a scale, then a number.
///
/// So the grammar stays here and the ARTIFACT crosses. This module is
/// the contract for that crossing: a spec in, deterministic bytes plus
/// the metadata needed to trust them out.
///
/// ── Three properties, each structural ────────────────────────────────
///
///   1. **No new rendering machinery.** Nothing here draws. `render`
///      builds the prop bag the grammar already reads and hands it to a
///      composition-supplied renderer function. There is one renderer in
///      the deployment and this is a third surface onto it, not a rival.
///   2. **The renderer arrives as data (GP 1).** The deterministic
///      server-side chart renderer lives in a different companion
///      package, and a companion never reaches into another. So
///      `ChartArtifactRenderer` is a record of a function, wired at
///      composition:
///
///      ```fsharp
///      let renderer: ChartArtifactRenderer = {
///          MediaType = "image/svg+xml"
///          Render =
///              fun props ->
///                  NarrativeCharts.renderChart props
///                  |> RenderView.AsString.htmlNode
///                  |> Encoding.UTF8.GetBytes
///      }
///      ```
///
///      What stops that wiring drifting into a parallel grammar is
///      `ChartArtifact.props`: it emits the SAME prop keys and the SAME
///      point encoding the grammar's own projector emits, and a
///      conformance test asserts the two agree rather than trusting this
///      paragraph.
///   3. **Determinism is checkable by the consumer, not promised to it.**
///      Every artifact carries a `sha256:` content hash of its own bytes.
///      An export tier that renders the same spec twice — or that
///      regenerates a deck six months later — can tell "the chart is
///      unchanged" from "the chart is unchanged so far as I can see".
///
/// ── What the provenance fields are for ───────────────────────────────
///
/// A chart in a governed deck is not decoration; it is a claim about
/// data, and a reader is entitled to ask which data. `ChartBinding`
/// carries the two answers that make that question decidable — which
/// stored result the series came from, and which vintage of the dataset
/// underneath it. Both are OPTIONAL and neither is invented here: a chart
/// over an ad-hoc series honestly has no binding, and a metadata field
/// filled with a plausible-looking value would be worse than an absent
/// one. `ChartArtifact.isBound` is the predicate an export tier gates on
/// when its own policy requires a binding.
///
/// Since Phase 649 the binding is also part of the PROP grammar, not only
/// of the metadata: `props` emits the two binding keys when they are
/// present, the narrative chart projector emits the same two, and
/// `bindingOf` reads them back. So a tier holding a chart block from a
/// document and a tier holding a spec of its own recover the identical
/// pair, and a chart declares what it is a claim about exactly once.
namespace ToolUp.Reporting

open System
open System.Globalization
open System.Security.Cryptography

/// One labelled point of a chartable series.
type ChartPoint = { Label: string; Value: float }

/// Where a chart binds governed results.
///
/// Both members are opaque identifiers this SDK never interprets — the
/// export tier resolves them against the stores that issued them. Absent
/// means "not bound", never "unknown"; see the module header.
type ChartBinding = {
    /// The stored artifact the rendered series came from — a result id, a
    /// data-object version, whatever the deployment's own result surface
    /// issued.
    ArtifactKey: string option
    /// The vintage of the dataset the artifact was computed over. The
    /// answer to "would this chart redraw the same today?".
    DatasetVintage: string option
}

/// A chart to hand off, in the grammar's own vocabulary.
type ChartArtifactSpec = {
    /// The grammar's kind token (`"line"` / `"bar"` / `"area"`). A string
    /// rather than a DU because the grammar's vocabulary is the
    /// deployment's to extend, and a closed DU here would cap it.
    Kind: string
    /// Optional visible caption.
    Title: string option
    /// The series, in the order it renders.
    Points: ChartPoint list
    /// Where this chart binds governed results.
    Binding: ChartBinding
}

/// What travels with the rendered bytes.
///
/// Flat rather than nesting `ChartBinding`, because this record is what
/// an export tier serialises alongside the artifact and a flat shape
/// survives more wire formats unchanged.
type ChartArtifactMetadata = {
    /// The chart grammar that produced the bytes.
    Grammar: string
    /// The grammar's prop-format version — what `props` speaks.
    GrammarVersion: string
    Kind: string
    Title: string option
    /// How many points were rendered. The cheap check an export tier can
    /// run against its own expectation without parsing the artifact.
    PointCount: int
    ArtifactKey: string option
    DatasetVintage: string option
}

/// A rendered, embeddable chart artifact.
type ChartArtifact = {
    /// The renderer's declared media type (e.g. `"image/svg+xml"`).
    MediaType: string
    Content: byte[]
    /// `sha256:<lowercase hex>` over `Content`.
    ContentHash: string
    Metadata: ChartArtifactMetadata
}

/// The composition-supplied bridge to the deployment's own chart
/// renderer. See the module header for why this is a function and not a
/// package reference.
type ChartArtifactRenderer = {
    MediaType: string
    Render: Map<string, string> -> byte[]
}

[<RequireQualifiedAccess>]
module ChartArtifact =

    let private inv = CultureInfo.InvariantCulture

    /// The chart grammar's stable identity, stamped on every artifact so
    /// a consumer can tell which grammar drew what it holds.
    [<Literal>]
    let Grammar = "toolup.narrative.chart"

    /// The prop-format version `props` emits. Bumped only when the prop
    /// KEYS or the point ENCODING change — not when the drawing does,
    /// which is the renderer's business and not part of this contract.
    [<Literal>]
    let GrammarVersion = "1"

    /// The grammar's own prop keys. Named here so the one place this
    /// module speaks the grammar is greppable, and so a conformance test
    /// can assert they are the keys the grammar's projector emits rather
    /// than three strings that happen to match today.
    [<Literal>]
    let KindProp = "chart.kind"

    [<Literal>]
    let TitleProp = "chart.title"

    [<Literal>]
    let PointsProp = "chart.points"

    /// The binding props (Phase 649). A chart block in a narrative
    /// declares its governed binding through these same two keys, so an
    /// export tier that reads a document and one that renders a spec
    /// recover the identical pair — see `bindingOf`.
    [<Literal>]
    let ArtifactKeyProp = "chart.artifactKey"

    [<Literal>]
    let DatasetVintageProp = "chart.datasetVintage"

    /// The grammar's point encoding: `"label=value;…"`, values in the
    /// invariant culture, separators sanitised out of labels.
    ///
    /// Reproduced rather than referenced, for the companion-isolation
    /// reason in the module header, and pinned against the grammar's own
    /// projector by a conformance test — which is what makes this a
    /// shared surface rather than a second grammar.
    let private encodePoints (points: ChartPoint list) : string =
        points
        |> List.map (fun p ->
            let safe = p.Label.Replace(';', ' ').Replace('=', ' ')
            sprintf "%s=%s" safe (p.Value.ToString(inv)))
        |> String.concat ";"

    /// The spec as the prop bag the chart grammar reads.
    ///
    /// `chart.title` is OMITTED rather than emitted empty when there is
    /// no caption, because that is what the grammar's projector does and
    /// an absent key and an empty one render differently. The two binding
    /// props follow the same rule for the stronger reason: an absent prop
    /// says "not bound", and an empty one would say "bound to nothing".
    let props (spec: ChartArtifactSpec) : Map<string, string> =
        let optional key value =
            match value with
            | Some v -> [ key, v ]
            | None -> []

        [ KindProp, spec.Kind; PointsProp, encodePoints spec.Points ]
        @ optional TitleProp spec.Title
        @ optional ArtifactKeyProp spec.Binding.ArtifactKey
        @ optional DatasetVintageProp spec.Binding.DatasetVintage
        |> Map.ofList

    /// Recover the binding a chart prop bag declares — the read half of
    /// `props`, for a tier holding a chart block from a narrative document
    /// rather than a spec of its own.
    ///
    /// Total: neither prop present yields an empty binding, which is the
    /// honest reading — "this chart declares no binding", never "unknown".
    let bindingOf (props: Map<string, string>) : ChartBinding = {
        ArtifactKey = props.TryFind ArtifactKeyProp
        DatasetVintage = props.TryFind DatasetVintageProp
    }

    /// The metadata a spec yields, independent of rendering — so a
    /// consumer can inspect what an artifact WOULD carry before paying
    /// for the render.
    let metadata (spec: ChartArtifactSpec) : ChartArtifactMetadata = {
        Grammar = Grammar
        GrammarVersion = GrammarVersion
        Kind = spec.Kind
        Title = spec.Title
        PointCount = List.length spec.Points
        ArtifactKey = spec.Binding.ArtifactKey
        DatasetVintage = spec.Binding.DatasetVintage
    }

    /// `sha256:<lowercase hex>` — the same content-address shape the rest
    /// of the platform's artifact surfaces use.
    let contentHash (bytes: byte[]) : string =
        "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    /// Whether an artifact's metadata carries a full binding to governed
    /// results — both the artifact key and the dataset vintage.
    ///
    /// BOTH, not either: an artifact key without a vintage says which
    /// computation drew the chart but not whether it would draw the same
    /// today, and a vintage without a key says the opposite. Half a
    /// binding answers neither question, so it does not count as one.
    let isBound (metadata: ChartArtifactMetadata) : bool =
        metadata.ArtifactKey.IsSome && metadata.DatasetVintage.IsSome

    /// Render a spec into an embeddable artifact through the supplied
    /// renderer.
    ///
    /// Total and pure over its inputs given a pure renderer — no store
    /// read, no clock, nothing ambient — so the same spec through the
    /// same renderer produces byte-identical content and an identical
    /// hash, which is the property an export tier regenerating a deck
    /// depends on.
    let render (renderer: ChartArtifactRenderer) (spec: ChartArtifactSpec) : ChartArtifact =
        let bytes = props spec |> renderer.Render

        {
            MediaType = renderer.MediaType
            Content = bytes
            ContentHash = contentHash bytes
            Metadata = metadata spec
        }
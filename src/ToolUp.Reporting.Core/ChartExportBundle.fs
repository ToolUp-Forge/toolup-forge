// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 650 — the chart export bundle: a document paired with its own
/// rendered chart artifacts.
///
/// [`ChartArtifact`](ChartArtifact.fs) gave a downstream
/// document-emission tier a way to render ONE chart it already had a spec
/// for, and Phase 649 put the binding on the block so a reader recovers
/// what the chart is a claim about. Between the two sat the gap this
/// module closes: a tier holding a `NarrativeDocument` holds chart
/// *props*, and the grammar that turns props into pixels is server-side.
/// So the tier could read every chart block in the document and render
/// none of them, and nothing paired a block with its artifact even when
/// it could.
///
/// The bundle is the pairing, in one call: the document, plus a
/// block-keyed collection of rendered artifacts, plus the blocks that
/// yielded none and why. That is everything a faithful re-emission needs
/// — no grammar access over there, and no second render path.
///
/// ── The keying rule (the load-bearing decision) ──────────────────────
///
/// A key is **positional**: `"chart:N"`, where `N` is the zero-based
/// index of the block in `blocks`' document-order walk — sections in
/// declared order, elements within a section in declared order, and a
/// container's nested body (`Card` / `Accordion` / `Tabs`) walked
/// depth-first at the point the container appears.
///
/// Positional rather than declared, because the grammar has no chart id
/// and inventing one here would be a second grammar — the thing
/// `ChartArtifact` exists not to be. Two identical chart blocks in one
/// document are therefore distinguishable, which a content-derived key
/// could not manage.
///
/// What makes a positional key usable rather than merely deterministic is
/// that the walk is PUBLIC. A consumer holding the bundle's document
/// calls `blocks` and gets the same keys, in the same order, without
/// reimplementing the traversal — the failure mode of positional keying
/// is two walks that disagree, and there is only one walk.
///
/// The honest caveat, stated once: a key identifies a POSITION in this
/// document, not a chart across revisions. Insert a chart into section 1
/// and every later key shifts by one. A tier that needs identity across
/// revisions should key on the block's declared binding
/// (`ChartArtifact.bindingOf`), which is what a binding is for. Keys
/// index the bundle's OWN `Document` field, which is the disclosed one
/// where a gate ran — see [`NarrativeExportBundle`] on the server side.
///
/// ── Partial by design ────────────────────────────────────────────────
///
/// One unrenderable block does not fail the bundle. A document is a
/// governed artifact and a tier that asked for eleven charts is better
/// served by ten plus a typed statement about the eleventh than by an
/// exception carrying none — so refusals are collected per block, named,
/// and returned alongside what did render. `isComplete` is the predicate
/// for a tier whose own policy is all-or-nothing.
namespace ToolUp.Reporting

open System
open System.Globalization
open ToolUp.Platform.Narrative

/// A chart block's key within one document — `"chart:N"` for the
/// zero-based ordinal of the block in document order. See the module
/// header for the keying rule and its one caveat.
type ChartBlockKey = string

/// A chart block located in a document: its key, where it sits, and the
/// prop bag it declares. The unit `blocks` yields and `ChartExportBundle`
/// keys on.
type ChartBlockRef = {
    Key: ChartBlockKey
    /// Zero-based index in the document-order walk. `Key` is derived from
    /// it; both are carried so a consumer can sort without re-parsing.
    Ordinal: int
    /// `Id` of the section the block sits in — the anchor renderers use,
    /// so a tier can point a reader at the block. Author-supplied and NOT
    /// assumed unique, which is why it is not the key.
    SectionId: string
    /// The block's declared props, verbatim.
    Props: Map<string, string>
}

/// Why a chart block yielded no artifact.
///
/// Both cases are refusals to produce an artifact, never a claim that the
/// document is malformed: the block stays in `Document` exactly as
/// authored, and a page rendering that document draws whatever the
/// grammar draws for it.
type ChartBlockRefusal =
    /// The block declares no series — `chart.points` absent, empty, or
    /// decoding to no usable points.
    ///
    /// Deliberately stricter than the page grammar, which degrades an
    /// empty series to a "no data" placeholder. An export tier embedding
    /// a placeholder it did not ask for is worse off than one told the
    /// block carries no series, because only the second can decide.
    | ChartBlockHasNoSeries
    /// The composition-supplied renderer raised. Carries its message —
    /// the renderer belongs to the deployment, so this is the only thing
    /// this module can honestly say about the failure.
    | ChartRendererFailed of reason: string

/// A block that yielded no artifact, with its refusal. Carries the same
/// locating fields as `ChartBlockRef` so a gap is actionable without
/// re-walking the document.
type ChartBundleGap = {
    Key: ChartBlockKey
    Ordinal: int
    SectionId: string
    Refusal: ChartBlockRefusal
}

/// A document and everything a document-emission tier needs to re-emit
/// its charts faithfully.
type ChartExportBundle = {
    /// The document the artifacts were rendered from — the disclosed one
    /// where an egress gate ran. Keys index THIS document, not the one
    /// handed in.
    Document: NarrativeDocument
    /// Rendered artifacts, keyed by block key. A block absent from this
    /// map appears in `Gaps`; the two partition the document's chart
    /// blocks exactly.
    Charts: Map<ChartBlockKey, ChartArtifact>
    /// The blocks that yielded no artifact, in document order.
    Gaps: ChartBundleGap list
}

[<RequireQualifiedAccess>]
module ChartExportBundle =

    let private inv = CultureInfo.InvariantCulture

    /// The `Component` block name the chart grammar claims.
    [<Literal>]
    let ChartComponentName = "chart"

    /// The key prefix. Named so a consumer matching on keys does not
    /// hardcode the string this module formats.
    [<Literal>]
    let KeyPrefix = "chart:"

    /// The key for a given document-order ordinal.
    let keyFor (ordinal: int) : ChartBlockKey = KeyPrefix + ordinal.ToString(inv)

    /// Decode the grammar's point encoding — `"label=value;…"`, values
    /// under `InvariantCulture`, malformed segments SKIPPED.
    ///
    /// Reproduced rather than referenced, for the companion-isolation
    /// reason in [`ChartArtifact`](ChartArtifact.fs)'s header, and
    /// skipping (not failing) on a malformed segment because that is what
    /// the grammar does: a decoder that refused what the grammar draws
    /// would be a second grammar with a different opinion, which is the
    /// whole failure mode this seam is arranged to avoid. Pinned against
    /// the grammar's own projector by a conformance test — a canonically
    /// projected block round-trips to its own prop bag unchanged.
    let private decodePoints (raw: string) : ChartPoint list =
        if String.IsNullOrWhiteSpace raw then
            []
        else
            raw.Split(';')
            |> Array.toList
            |> List.choose (fun segment ->
                let eq = segment.IndexOf '='

                if eq <= 0 then
                    None
                else
                    let label = segment.Substring(0, eq).Trim()
                    let valueText = segment.Substring(eq + 1).Trim()

                    match Double.TryParse(valueText, NumberStyles.Float, inv) with
                    | true, value -> Some { Label = label; Value = value }
                    | _ -> None)

    /// Reconstruct the artifact spec a chart block declares.
    ///
    /// The read half of `ChartArtifact.props`: kind and title come off the
    /// grammar's own prop keys, the series decodes through the encoding
    /// above, and the binding is read by `ChartArtifact.bindingOf` rather
    /// than by a second reader — Phase 649 shipped that one, and two
    /// readers of one pair is exactly the drift the 649 conformance pin
    /// exists to catch.
    ///
    /// An absent `chart.kind` reads as `"line"`, matching the grammar's
    /// default: the spec states what WILL be drawn, and saying anything
    /// else would put a fiction in the artifact's metadata.
    let specOf (props: Map<string, string>) : ChartArtifactSpec = {
        Kind = props.TryFind ChartArtifact.KindProp |> Option.defaultValue "line"
        Title = props.TryFind ChartArtifact.TitleProp
        Points = props.TryFind ChartArtifact.PointsProp |> Option.defaultValue "" |> decodePoints
        Binding = ChartArtifact.bindingOf props
    }

    /// Every chart block in the document, in the walk order the keying
    /// rule is defined by. Public because a consumer must be able to
    /// derive the same keys from the same document without reimplementing
    /// the traversal — see the module header.
    let blocks (document: NarrativeDocument) : ChartBlockRef list =
        // Nested containers are walked depth-first at the point the
        // container appears, so a chart inside a card sits between the
        // elements either side of that card rather than after them.
        let rec chartsIn (elements: NarrativeElement list) : Map<string, string> list =
            elements
            |> List.collect (fun element ->
                match element with
                | Component(name, props) when name = ChartComponentName -> [ props ]
                | Card spec -> chartsIn spec.Body
                | Accordion panels -> panels |> List.collect (snd >> chartsIn)
                | Tabs panels -> panels |> List.collect (snd >> chartsIn)
                | _ -> [])

        document.Sections
        |> List.collect (fun section -> chartsIn section.Elements |> List.map (fun props -> section.Id, props))
        |> List.mapi (fun ordinal (sectionId, props) -> {
            Key = keyFor ordinal
            Ordinal = ordinal
            SectionId = sectionId
            Props = props
        })

    /// Render one located block through the handoff seam.
    let private renderBlock
        (renderer: ChartArtifactRenderer)
        (block: ChartBlockRef)
        : Result<ChartBlockKey * ChartArtifact, ChartBundleGap> =
        let gap refusal : Result<ChartBlockKey * ChartArtifact, ChartBundleGap> =
            Error {
                Key = block.Key
                Ordinal = block.Ordinal
                SectionId = block.SectionId
                Refusal = refusal
            }

        let spec = specOf block.Props

        if List.isEmpty spec.Points then
            gap ChartBlockHasNoSeries
        else
            try
                Ok(block.Key, ChartArtifact.render renderer spec)
            with ex ->
                // The renderer is the deployment's; one block's failure is
                // contained so the other ten still reach the export tier.
                gap (ChartRendererFailed ex.Message)

    /// Pair a document with its rendered chart artifacts.
    ///
    /// Total and deterministic given a deterministic renderer: nothing
    /// here reads a store or a clock, and the walk, the keys and each
    /// artifact's bytes are functions of the document alone — so the same
    /// document yields a byte-identical bundle, which is the property a
    /// tier regenerating a deck six months later depends on.
    ///
    /// **This is the ungated entry point.** A deployment composing the
    /// fact tier calls the server-side `NarrativeExportBundle` variant
    /// instead, which runs the document through the one `FactExport`
    /// egress door before pairing. A deployment with no classified facts
    /// pays nothing for a door it does not have (GP 13).
    let ofDocument (renderer: ChartArtifactRenderer) (document: NarrativeDocument) : ChartExportBundle =
        let outcomes = blocks document |> List.map (renderBlock renderer)

        {
            Document = document
            Charts =
                outcomes
                |> List.choose (function
                    | Ok entry -> Some entry
                    | Error _ -> None)
                |> Map.ofList
            Gaps =
                outcomes
                |> List.choose (function
                    | Error gap -> Some gap
                    | Ok _ -> None)
        }

    /// Whether every chart block in the bundle's document rendered — the
    /// predicate for a tier whose own policy is all-or-nothing.
    let isComplete (bundle: ChartExportBundle) : bool = List.isEmpty bundle.Gaps
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.BlockRender

open System
open Feliz
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Facts.Presentation

// ─── Phase 1130 — a block part, drawn ────────────────────────────────
//
// A block part (Phase 1128) persists the presentation request a model
// wrote and the provenance of the read that answered it, never its rows.
// So the panel never decodes the stored node and draws it: it re-runs the
// request through the registered series resolver — the reader's own gated
// fact read, at the moment of reading — rebuilds the node with the fact
// presentation builders (Phase 1129, the same code a server builds with),
// and draws that through the published Fuaran-UI renderer. Nothing is ever
// drawn from the stored copy.
//
// The resolver is a registration (`AIClientConfig.registerBlockSeriesResolver`),
// filled at client boot from the fact client's fact-browse proxy, so this
// tier takes no reference to the fact client and a deployment without the
// fact store pays nothing: with no resolver a block renders a placeholder
// saying no fact reader is composed.
//
// The block wears the page's theme. The renderer themes through inherited
// CSS (the `--fuaran-*` variables and `currentColor`), so the container
// this module draws carries no class, style, colour, font or background of
// its own, and the render runs under whatever chart style the host has
// installed (`Fuaran.UI.Charts.installChartStyle*`) — the same style every
// other Fuaran-UI chart on the page lowers under. This module installs none.

/// The series read a block re-runs its request through: one gated series
/// per query, or the read's refusal verbatim. A fact client supplies it
/// from its fact-browse proxy (`IFactBrowseApi.QuerySeries`), so every
/// figure drawn was judged by the disclosure gate for the reader, now.
type BlockSeriesResolver = FactSeriesQuery -> Async<Result<FactSeries, string>>

// Sanctioned mutable global — the `FastPathBridge.resolver` precedent. Sole
// writer is the client boot, through `AIClientConfig.registerBlockSeriesResolver`.
let mutable private resolver: BlockSeriesResolver option = None

/// Install (or clear) the resolver. `AIClientConfig.registerBlockSeriesResolver`
/// is the public route; tests clear it between cases.
let internal setResolver (r: BlockSeriesResolver option) : unit = resolver <- r

/// The registered resolver, if a fact reader is composed.
let current () : BlockSeriesResolver option = resolver

/// The fact ids a re-read answered with that differ from the ones the block
/// was written with: a fact restated or superseded since.
type BlockDrift = {
    /// Fact ids the block was written with that the read no longer returns.
    Superseded: string list
    /// Fact ids the read returns now that the block was not written with —
    /// the newer figures, which are the ones drawn.
    Introduced: string list
}

/// What re-running a block's request gave.
[<RequireQualifiedAccess>]
type BlockOutcome =
    /// No resolver is registered: this deployment composes no fact reader.
    | NoReader
    /// The block cannot be drawn; the reason names the refusal (a malformed
    /// request, every read refused, or a node the builder or the pre-emit
    /// validator refused).
    | Refused of reason: string
    /// The node rebuilt from the re-read, and the drift from the stored
    /// provenance, if any.
    | Drawn of block: PresentationBlock<unit> * drift: BlockDrift option

/// The drift between the fact ids a block was written with and the ones a
/// re-read answered with, in each list's own order; `None` when they match.
let drift (stored: string list) (now: string list) : BlockDrift option =
    let storedSet = Set.ofList stored
    let nowSet = Set.ofList now

    if storedSet = nowSet then
        None
    else
        Some {
            Superseded = stored |> List.filter (nowSet.Contains >> not) |> List.distinct
            Introduced = now |> List.filter (storedSet.Contains >> not) |> List.distinct
        }

/// One read, its failure caught: a resolver that throws is a refusal of
/// that metric, never a broken panel.
let private answer (read: BlockSeriesResolver) (query: FactSeriesQuery) : Async<SeriesAnswer> = async {
    try
        match! read query with
        | Ok series -> return SeriesAnswer.Answered series
        | Error why -> return SeriesAnswer.Refused(query.Metric, why)
    with ex ->
        return SeriesAnswer.Refused(query.Metric, ex.Message)
}

let rec private answerAll (read: BlockSeriesResolver) (queries: FactSeriesQuery list) : Async<SeriesAnswer list> = async {
    match queries with
    | [] -> return []
    | q :: rest ->
        let! first = answer read q
        let! others = answerAll read rest
        return first :: others
}

/// Re-run a block's request through `read` and rebuild its node. The stored
/// node is never consulted: only the request and the stored fact ids (for
/// the drift) are read from `provenance`.
let resolve (read: BlockSeriesResolver) (provenance: BlockProvenance) : Async<BlockOutcome> =
    match PresentationRequest.ofJson provenance.Request with
    | Error e ->
        async.Return(
            BlockOutcome.Refused(
                sprintf "The block's request could not be read: %s" (PresentationRequestError.describe e)
            )
        )
    | Ok request -> async {
        let! answers = answerAll read (PresentationRequest.toQueries FactBrowseApi.MaxSeriesPoints request)

        let refusals =
            answers
            |> List.choose (function
                | SeriesAnswer.Refused(metric, why) -> Some(sprintf "%s: %s" metric why)
                | SeriesAnswer.Answered _ -> None)

        // Every read refused: nothing to draw. A partial refusal is the
        // builder's to show, as a callout beside what was served.
        if not answers.IsEmpty && refusals.Length = answers.Length then
            return
                BlockOutcome.Refused(
                    sprintf "The fact read refused this block for you: %s" (String.concat "; " refusals)
                )
        else
            match Presentation.build<unit> request answers with
            | Error defect ->
                return
                    BlockOutcome.Refused(
                        sprintf "The block could not be built: %s" (PresentationDefect.describe defect)
                    )
            | Ok block -> return BlockOutcome.Drawn(block, drift provenance.FactIds block.Provenance.FactIds)
      }

// ─── The furniture ───────────────────────────────────────────────────

/// Whether the fact browse module is in the shell's published module list —
/// the gate on every link into it (`FactBrowseLinks.isComposed`).
let isFactBrowseComposed () : bool =
    FactBrowseLinks.isComposed (RegisteredModules.snapshot () |> List.map _.ModuleId)

/// Open one fact's row and provenance in the fact browse module: the
/// citation opener the panel's fact sources use (Phase 895). The publish is
/// a Cmd; it runs here, outside any update loop, with a message sink that
/// is never called.
let openFact (factId: string) : unit =
    for effect in ModuleEvents.publish FactBrowseLinks.OpenFactTopic factId do
        effect ignore

    NavigationRequest.request (FactBrowseLinks.sidebarId FactBrowseLinks.RowsRoute)

/// Open one fact table's detail page in the fact browse module.
let openTable (tableId: string) : unit =
    for effect in ModuleEvents.publish FactBrowseLinks.OpenTableTopic tableId do
        effect ignore

    NavigationRequest.request (FactBrowseLinks.sidebarId FactBrowseLinks.TableRoute)

/// The most citation markers the strip shows; the rest are counted, and the
/// table link opens them all.
[<Literal>]
let MaxCitations = 8

let private plural (n: int) (one: string) (many: string) = if n = 1 then one else many

/// The drift note's sentence, naming the supersession.
let driftText (d: BlockDrift) : string =
    let superseded =
        match d.Superseded with
        | [] -> ""
        | ids ->
            sprintf
                " %d %s this answer was written with %s since been restated or superseded (%s)."
                ids.Length
                (plural ids.Length "fact" "facts")
                (plural ids.Length "has" "have")
                (String.concat ", " ids)

    let introduced =
        match d.Introduced with
        | [] -> ""
        | ids -> sprintf " The newer figures are drawn (%s)." (String.concat ", " ids)

    "The figures were re-read for you." + superseded + introduced

/// The withheld note's sentence for one disclosure policy.
let withheldText (w: BlockWithheld) : string =
    sprintf
        "%d %s withheld under the %s disclosure policy %s not drawn."
        w.Count
        (plural w.Count "point" "points")
        w.PolicyRef
        (plural w.Count "is" "are")

let private placeholder (text: string) =
    Html.p [ prop.custom ("data-block-placeholder", ""); prop.role "note"; prop.text text ]

let private citations (factIds: string list) =
    let composed = isFactBrowseComposed ()
    let shown = factIds |> List.truncate MaxCitations

    Html.div [
        prop.custom ("data-block-citations", "")
        prop.children [
            Html.span [ prop.text "Facts: " ]
            for i, factId in List.indexed shown do
                let label = sprintf "[%d]" (i + 1)

                if composed then
                    Html.button [
                        prop.type' "button"
                        prop.className "underline mr-1"
                        prop.title factId
                        prop.custom ("data-fact", factId)
                        prop.onClick (fun _ -> openFact factId)
                        prop.text label
                    ]
                else
                    Html.span [
                        prop.className "mr-1"
                        prop.title factId
                        prop.custom ("data-fact", factId)
                        prop.text label
                    ]
            if factIds.Length > shown.Length then
                Html.span [ prop.text (sprintf "and %d more" (factIds.Length - shown.Length)) ]
        ]
    ]

let private furniture (block: PresentationBlock<unit>) (blockDrift: BlockDrift option) =
    let provenance = block.Provenance

    Html.div [
        prop.custom ("data-block-furniture", "")
        prop.className "mt-1 space-y-1"
        prop.children [
            match blockDrift with
            | Some d ->
                Html.p [
                    prop.custom ("data-block-drift", "")
                    prop.role "note"
                    prop.text (driftText d)
                ]
            | None -> ()
            for w in provenance.Withheld do
                Html.p [ prop.custom ("data-block-withheld", w.PolicyRef); prop.text (withheldText w) ]
            if not provenance.FactIds.IsEmpty then
                citations provenance.FactIds
            if isFactBrowseComposed () then
                Html.button [
                    prop.type' "button"
                    prop.className "underline"
                    prop.custom ("data-block-table", provenance.TableId)
                    prop.title provenance.TableId
                    prop.onClick (fun _ -> openTable provenance.TableId)
                    prop.text "Open the table"
                ]
        ]
    ]

/// The block for `outcome`: the rebuilt node through the Fuaran-UI renderer
/// with its furniture, or a placeholder naming why it is not drawn. The
/// container carries no class or style: the block takes the page's theme.
let view (provenance: BlockProvenance) (outcome: BlockOutcome) : ReactElement =
    Html.div [
        prop.custom ("data-block-part", provenance.Kind)
        prop.children [
            match outcome with
            | BlockOutcome.NoReader ->
                placeholder "This block cannot be drawn: no fact reader is composed in this deployment."
            | BlockOutcome.Refused reason -> placeholder reason
            | BlockOutcome.Drawn(block, blockDrift) ->
                Html.div [
                    prop.custom ("data-block-node", "")
                    prop.children [
                        Fuaran.UI.Renderer.Render.renderWithSources
                            Fuaran.UI.Renderer.BindingResolver.empty
                            ignore
                            block.Node
                    ]
                ]

                furniture block blockDrift
        ]
    ]

/// A block part in the conversation: re-read through the registered
/// resolver when it mounts (and whenever its request changes), drawn once
/// the read answers. With no resolver it renders the no-reader placeholder.
[<ReactComponent>]
let Block (provenance: BlockProvenance) =
    let outcome, setOutcome = React.useState (None: BlockOutcome option)

    React.useEffect (
        (fun () ->
            let mutable live = true

            match current () with
            | None -> setOutcome (Some BlockOutcome.NoReader)
            | Some read ->
                async {
                    let! result = resolve read provenance

                    if live then
                        setOutcome (Some result)
                }
                |> Async.StartImmediate

            { new IDisposable with
                member _.Dispose() = live <- false
            }),
        [| box provenance.Request; box (String.concat "," provenance.FactIds) |]
    )

    match outcome with
    | Some o -> view provenance o
    | None ->
        Html.div [
            prop.custom ("data-block-part", provenance.Kind)
            prop.children [ placeholder "Reading the facts for this block…" ]
        ]
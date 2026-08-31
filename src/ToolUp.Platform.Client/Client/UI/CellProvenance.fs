// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

[<AutoOpen>]
module ToolUp.Platform.CellProvenance

// Phase 12d — AG Grid value-provenance overlay.
//
// A display-only substrate that answers "where did this cell value come
// from?" on hover / click. A module attaches a `CellProvenance` getter to
// a column via `ColumnDef.provenance`; on hover the grid renders the
// `ProvenanceOverlay` tooltip (source-kind badge, label, optional detail,
// optional click-through). This is the *display* substrate — modules emit
// `CellProvenance` from their own data; provenance CAPTURE is out of scope.
//
// Placement (Phase 12d decision): AG Grid tooltips — including custom
// `tooltipComponent` — are a **Community** feature, and this file follows
// the exact precedent of `AgGrid.Enterprise.fs`: Enterprise-flavoured
// colDef / grid members are defined Community-side as erased prop setters
// (so they compile through the MinimalClient Fable gate) and only "light
// up" at runtime when the Enterprise companion registers. Here the overlay
// only *renders* when the `AgGridEnterprise` companion has flipped
// `setProvenanceOverlayEnabled ()` (mirrors `AgGrid.setGridModulesRegistered`).
// When the companion is absent the getter still attaches (metadata is
// collectable) but the tooltip never fires — the Community-edition
// graceful no-op the phase acceptance criteria require.

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform
open Feliz.AgGrid

#nowarn "1182"

// ─── Provenance types ────────────────────────────────────────────

/// Where a cell's value originated. Display-only; each case names the
/// upstream location a module can point at.
[<RequireQualifiedAccess>]
type ProvenanceLocation =
    /// A producing module (its stable module id).
    | Module of moduleId: string
    /// A versioned data object in the entity / data-object store.
    | DataObject of objectId: string * version: int
    /// A user-supplied input field (its key).
    | InputField of fieldKey: string
    /// A computed value — the expression / formula that produced it.
    | Computed of expression: string

/// A single cell's value provenance. `LinkedEntity` is
/// `(entityType, entityId)` — both `string`: the estate has no dedicated
/// `EntityType` type (the entity store keys on a `Type: string`
/// discriminator) and `EntityId` is a `string` alias in
/// `ToolUp.Platform.EntityTypes`. The pair deep-links to a catalog /
/// lineage record the click handler can open.
type CellProvenance = {
    SourceLabel: string
    SourceLocation: ProvenanceLocation
    Detail: string option
    LinkedEntity: (string * string) option
}

// ─── Enterprise-gated overlay activation ─────────────────────────
// Sanctioned module-level mutable — same precedent as
// `AgGrid.gridModulesRegistered`. Default `false`: the overlay renders
// nothing until the `AgGridEnterprise` companion registers.

let mutable private overlayEnabled = false

/// Flip the provenance overlay on. Called by the `AgGridEnterprise`
/// companion at module-evaluation time (see
/// `AgGridEnterprise/ProvenanceOverlay.fs`). Idempotent.
let setProvenanceOverlayEnabled () = overlayEnabled <- true

/// Whether the provenance overlay may render. `false` until the
/// Enterprise companion registers — the Community-edition no-op.
let isProvenanceOverlayEnabled () = overlayEnabled

// ─── Click seam ──────────────────────────────────────────────────
// Two seams fire on a provenance click:
//  1. a typed local registry (`subscribeProvenanceClick`) so a subscriber
//     (AI "explain this cell", a debug page) gets the `CellProvenance`
//     value directly, and
//  2. a `NotificationEnvelope` published via `NotificationClient.publishLocal`
//     under the reserved `CustomNotification` key below, so existing
//     notification subscribers observe it through the established client
//     seam. Payload JSON is hand-built (no serializer dependency) so the
//     wire shape is predictable and the path stays .NET-test-safe.

/// Reserved `CustomNotification` key the click envelope is published under.
[<Literal>]
let provenanceClickKey = "Provenance.CellClick"

let mutable private clickHandlers: (int * (CellProvenance -> unit)) list = []
let mutable private nextClickHandlerId = 0

/// Register a handler invoked with the `CellProvenance` of every clicked
/// provenance cell. Returns a dispose thunk (idempotent).
let subscribeProvenanceClick (handler: CellProvenance -> unit) : unit -> unit =
    let id = nextClickHandlerId
    nextClickHandlerId <- id + 1
    clickHandlers <- (id, handler) :: clickHandlers

    fun () -> clickHandlers <- clickHandlers |> List.filter (fun (hid, _) -> hid <> id)

let private jsonEscape (s: string) : string =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

let private provenancePayloadJson (prov: CellProvenance) : string =
    let opt =
        function
        | Some s -> "\"" + jsonEscape s + "\""
        | None -> "null"

    let entityType, entityId =
        match prov.LinkedEntity with
        | Some(t, i) -> Some t, Some i
        | None -> None, None

    sprintf
        "{\"sourceLabel\":\"%s\",\"detail\":%s,\"linkedEntityType\":%s,\"linkedEntityId\":%s}"
        (jsonEscape prov.SourceLabel)
        (opt prov.Detail)
        (opt entityType)
        (opt entityId)

/// Emit a provenance-click event: fan out to typed subscribers and publish
/// a `CustomNotification` envelope on the shared client notification seam.
let publishProvenanceClick (prov: CellProvenance) : unit =
    for _, handler in clickHandlers do
        try
            handler prov
        with _ ->
            ()

    let envelope =
        NotificationEnvelope.create
            "provenance"
            (Notification.CustomNotification(provenanceClickKey, provenancePayloadJson prov))

    NotificationClient.publishLocal envelope

// ─── Overlay rendering ───────────────────────────────────────────

let private badgeText (loc: ProvenanceLocation) : string =
    match loc with
    | ProvenanceLocation.Module _ -> "Module"
    | ProvenanceLocation.DataObject _ -> "Data object"
    | ProvenanceLocation.InputField _ -> "Input"
    | ProvenanceLocation.Computed _ -> "Computed"

let private locationSummary (loc: ProvenanceLocation) : string =
    match loc with
    | ProvenanceLocation.Module m -> m
    | ProvenanceLocation.DataObject(id, v) -> sprintf "%s (v%d)" id v
    | ProvenanceLocation.InputField k -> k
    | ProvenanceLocation.Computed e -> e

let private renderProvenanceCard (prov: CellProvenance) : ReactElement =
    Html.div [
        prop.className "toolup-provenance-overlay"
        prop.style [ style.padding 8; style.maxWidth 320; style.fontSize 12 ]
        prop.children [
            Html.div [
                prop.style [
                    style.display.flex
                    style.alignItems.center
                    style.gap 6
                    style.marginBottom 4
                ]
                prop.children [
                    Html.span [
                        prop.className "toolup-provenance-badge"
                        prop.style [
                            style.fontSize 10
                            style.fontWeight.bold
                            style.textTransform.uppercase
                            style.padding (2, 6)
                            style.borderRadius 3
                            style.backgroundColor "#334155"
                            style.color "#e2e8f0"
                        ]
                        prop.text (badgeText prov.SourceLocation)
                    ]
                    Html.span [ prop.style [ style.fontWeight.bold ]; prop.text prov.SourceLabel ]
                ]
            ]
            Html.div [
                prop.className "toolup-provenance-location"
                prop.style [ style.color "#64748b" ]
                prop.text (locationSummary prov.SourceLocation)
            ]
            match prov.Detail with
            | Some detail ->
                Html.div [
                    prop.className "toolup-provenance-detail"
                    prop.style [ style.marginTop 4 ]
                    prop.text detail
                ]
            | None -> Html.none
            match prov.LinkedEntity with
            | Some(entityType, _) ->
                Html.button [
                    prop.className "toolup-provenance-link"
                    prop.style [
                        style.marginTop 6
                        style.cursor.pointer
                        style.color "#3b82f6"
                        style.backgroundColor "transparent"
                        style.border (1, borderStyle.solid, "#3b82f6")
                        style.borderRadius 3
                        style.padding (2, 8)
                        style.fontSize 11
                    ]
                    prop.text (sprintf "Open %s →" entityType)
                    prop.onClick (fun _ -> publishProvenanceClick prov)
                ]
            | None -> Html.none
        ]
    ]

/// The reusable provenance tooltip component. Wired as a column's
/// `tooltipComponent` by `ColumnDef.provenance`. Renders nothing unless the
/// Enterprise companion has enabled the overlay (Community no-op) or the
/// clicked/hovered cell carries no provenance value.
///
/// Not `private` — `ColumnDef.provenance` is `inline` on the `[<Erase>]`
/// `ColumnDef<'row>` type, so Fable inlines the call site and imports this
/// component directly (same rule that gates `MemoizedGrid` / `MemoizedChart`).
[<ReactComponent>]
let ProvenanceOverlay (props: obj) : ReactElement =
    if not (isProvenanceOverlayEnabled ()) then
        Html.none
    else
        let value = props?value

        if isNull (box value) then
            Html.none
        else
            renderProvenanceCard (unbox<CellProvenance> value)

// ─── colDef + grid extensions ────────────────────────────────────

/// Resolve the column key from tooltip params — `colDef.field` when set,
/// else the column's col id. Public because it is referenced from the
/// `inline` `ColumnDef.provenance` body (Fable inline-export rule).
let provenanceColKey (p: obj) : string =
    let cd = p?colDef

    let fromField = if isNull (box cd) then null else cd?field

    if not (isNull (box fromField)) then
        unbox<string> fromField
    else
        let col = p?column

        if isNull (box col) then
            ""
        else
            unbox<string> (col?getColId ())

/// Whether this grid's per-grid provenance toggle is on. Reads the
/// `showProvenanceOverlay` flag off the grid `context`. Public for the
/// same inline-export reason as `provenanceColKey`.
let provenanceToggleOn (p: obj) : bool =
    let api = p?api

    if isNull (box api) then
        false
    else
        let ctx = api?getGridOption ("context")
        not (isNull (box ctx)) && ctx?showProvenanceOverlay = true

type ColumnDef<'row> with

    /// Attach a value-provenance getter to this column. The getter maps
    /// `(rowData, colKey)` to an optional `CellProvenance`; returning
    /// `None` leaves the cell with no overlay. Splice into a column's prop
    /// list with `yield!` (F# has no optional colDef arg): e.g.
    /// `[ ColumnDef.field _.Revenue; yield! ColumnDef.provenance getRevenueProvenance ]`.
    ///
    /// Wires a `tooltipValueGetter` (gated on the Enterprise companion
    /// being registered AND the grid's `showProvenanceOverlay` toggle — so
    /// a grid that has not opted in fires no tooltip and pays nothing) plus
    /// the `ProvenanceOverlay` `tooltipComponent`.
    static member inline provenance<'value>(getter: 'row -> string -> CellProvenance option) = [
        columnDefProp<'row, 'value> (
            "tooltipValueGetter"
            ==> (fun (p: obj) ->
                if not (isProvenanceOverlayEnabled ()) || not (provenanceToggleOn p) then
                    None
                else
                    let data = p?data

                    if isNull (box data) then
                        None
                    else
                        getter (unbox<'row> data) (provenanceColKey p))
        )
        columnDefProp<'row, 'value> ("tooltipComponent" ==> ProvenanceOverlay)
    ]

type AgGrid<'row> with

    /// Per-grid provenance-overlay toggle. Default off — a grid that never
    /// calls this fires no provenance tooltips (no perf regression). Sets
    /// the grid `context` `showProvenanceOverlay` flag the provenance
    /// `tooltipValueGetter` reads. Consumers already using `context` should
    /// merge this flag into their own context object instead.
    static member inline showProvenanceOverlay(v: bool) =
        agGridProp<'row> ("context" ==> {| showProvenanceOverlay = v |})
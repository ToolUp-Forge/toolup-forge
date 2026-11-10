// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module AuditLogUI

open System
open ToolUp.Elmish
open Feliz
open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 529 — audit-trail viewer admin module (client) ────────────
//
// Read-only Owner/Admin surface over `IAuditViewApi`: a filter bar
// (time window / event type / actor), a paged event table, a per-row
// detail expansion rendering the recorded payload, and a CSV export of
// the whole filtered window. Mirrors `UsageDashboard`'s shape — the
// reserved `_sdk.AuditLog` Id, the `Team Management` sidebar group, the
// `TeamOwnerAdmin` nav role, and the same Blob-and-anchor download —
// because that module is the same thing over a different substrate and
// a second idiom here would be a second thing to keep in step.
//
// **Paging is APPEND, not replace.** "Load older events" concatenates
// the next page onto what is already on screen rather than swapping to
// it. An audit trail is read by scrolling backwards through a sequence,
// and a viewer that discards the rows above the fold makes an operator
// re-derive where they were on every click. Changing any filter starts
// a fresh sequence, which is when replacing IS the right behaviour.

/// Filter-bar state, held as the STRINGS the inputs carry rather than
/// as the parsed query. A half-typed date is not a `DateTime`, and
/// parsing per keystroke would either reject the intermediate states or
/// silently apply a filter the operator has not finished expressing.
/// `toQuery` parses once, when the filters are applied.
type Filters = {
    From: string
    To: string
    EventType: string
    Actor: string
}

type Model = {
    /// Filters as edited — may differ from `Applied` until Apply.
    Draft: Filters
    /// Filters the rows on screen were fetched under. The export uses
    /// THESE, so an operator cannot export a window they can see edits
    /// to but have not applied.
    Applied: Filters
    /// Rows accumulated across pages, newest first.
    Events: AuditEventView list
    /// Continuation token for the next (older) page; `None` at the end.
    NextCursor: string option
    /// How many rows the applied filter matches in total.
    MatchedCount: int
    /// The first page's read; the rows themselves live in `Events`.
    Status: Remote<unit, string>
    /// True while an older page is in flight — distinguished from the
    /// first load so the table stays on screen under it.
    LoadingMore: bool
    /// Event types present in this scope's trail, for the filter bar.
    EventTypes: string list
    /// Ids of the rows whose detail expansion is open.
    Expanded: Set<Guid>
    Exporting: bool
}

type Msg =
    | SetFrom of string
    | SetTo of string
    | SetEventType of string
    | SetActor of string
    | ApplyFilters
    | ClearFilters
    | Refresh
    | PageLoaded of Result<AuditEventPage, string>
    | LoadMore
    | MorePageLoaded of Result<AuditEventPage, string>
    | EventTypesLoaded of Result<string list, string>
    | ToggleDetail of Guid
    | ExportCsv
    | ExportComplete of Result<byte[], string>

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private auditApi: IAuditViewApi =
    Api.makeProxy<IAuditViewApi> (
        routeBuilder = AuditViewApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// ─── Filters → query ─────────────────────────────────────────────────

let private emptyFilters = {
    From = ""
    To = ""
    EventType = ""
    Actor = ""
}

let private blankToNone (s: string) =
    if String.IsNullOrWhiteSpace s then None else Some(s.Trim())

/// Parse a `<input type=date>` value (`yyyy-MM-dd`) as a UTC instant.
/// `endOfDay` pushes the upper bound to the last tick of the named day
/// so a window of "1st to 1st" contains the 1st, which is what an
/// operator picking one day means. An unparseable value contributes no
/// bound rather than an error: the input's own type restricts what can
/// be typed, and refusing to search on a malformed date the browser
/// would not produce is noise.
let private parseDate (endOfDay: bool) (raw: string) : DateTime option =
    match blankToNone raw with
    | None -> None
    | Some value ->
        match DateTime.TryParse value with
        | true, parsed ->
            let utc = DateTime(parsed.Year, parsed.Month, parsed.Day, 0, 0, 0, DateTimeKind.Utc)
            Some(if endOfDay then utc.AddDays(1.0).AddTicks(-1L) else utc)
        | _ -> None

/// The query for a filter set. `cursor` is the only thing that differs
/// between the first page and a continuation, so both go through here
/// and cannot drift in what they filter on.
let private toQuery (cursor: string option) (filters: Filters) : AuditTrailQuery = {
    AuditViewApi.defaultQuery with
        From = parseDate false filters.From
        To = parseDate true filters.To
        EventType = blankToNone filters.EventType
        Actor = blankToNone filters.Actor
        Cursor = cursor
}

// ─── Commands ────────────────────────────────────────────────────────

let private loadPageCmd (filters: Filters) =
    Cmd.OfRemoting.call auditApi.Query (toQuery None filters) PageLoaded (fun e -> PageLoaded(Error e.Message))

let private loadMoreCmd (filters: Filters) (cursor: string) =
    Cmd.OfRemoting.call auditApi.Query (toQuery (Some cursor) filters) MorePageLoaded (fun e ->
        MorePageLoaded(Error e.Message))

let private loadEventTypesCmd () =
    Cmd.OfRemoting.call auditApi.ListEventTypes () EventTypesLoaded (fun e -> EventTypesLoaded(Error e.Message))

let init () : Model * Cmd<Msg> =
    let model = {
        Draft = emptyFilters
        Applied = emptyFilters
        Events = []
        NextCursor = None
        MatchedCount = 0
        Status = Remote.Loading
        LoadingMore = false
        EventTypes = []
        Expanded = Set.empty
        Exporting = false
    }

    model, Cmd.batch [ loadPageCmd emptyFilters; loadEventTypesCmd () ]

// ─── CSV download ────────────────────────────────────────────────────

/// Build a Blob, attach an `<a download>`, click it, revoke the URL.
/// Lifted verbatim from `UsageDashboard.downloadCsv` — same mechanism,
/// same browser constraints, different filename.
let private downloadCsv (bytes: byte[]) =
    let blobObj: obj =
        emitJsExpr (bytes) "new Blob([new Uint8Array($0)], { type: 'text/csv;charset=utf-8' })"

    let url: string = emitJsExpr blobObj "URL.createObjectURL($0)"
    let anchor = document.createElement "a" :?> HTMLAnchorElement
    anchor.href <- url

    anchor?download <- sprintf "audit-trail-%s.csv" (DateTime.UtcNow.ToString "yyyy-MM-dd")

    document.body.appendChild anchor |> ignore
    anchor.click ()
    document.body.removeChild anchor |> ignore
    emitJsExpr<unit> url "URL.revokeObjectURL($0)"

// ─── Update ──────────────────────────────────────────────────────────

/// Start a fresh sequence under `filters`: the accumulated rows, the
/// cursor and the open expansions all belong to the previous filter and
/// none of them survive it.
let private restart (filters: Filters) (model: Model) =
    {
        model with
            Draft = filters
            Applied = filters
            Events = []
            NextCursor = None
            MatchedCount = 0
            Expanded = Set.empty
            Status = Remote.Loading
    },
    loadPageCmd filters

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | SetFrom v ->
        {
            model with
                Draft = { model.Draft with From = v }
        },
        Cmd.none

    | SetTo v ->
        {
            model with
                Draft = { model.Draft with To = v }
        },
        Cmd.none

    | SetEventType v ->
        {
            model with
                Draft = { model.Draft with EventType = v }
        },
        Cmd.none

    | SetActor v ->
        {
            model with
                Draft = { model.Draft with Actor = v }
        },
        Cmd.none

    | ApplyFilters -> restart model.Draft model
    | ClearFilters -> restart emptyFilters model
    | Refresh -> restart model.Applied model

    | PageLoaded(Ok page) ->
        {
            model with
                Events = page.Events
                NextCursor = page.NextCursor
                MatchedCount = page.MatchedCount
                Status = Remote.Loaded()
        },
        Cmd.none

    | PageLoaded(Error err) ->
        {
            model with
                Status = Remote.Failed err
        },
        Cmd.none

    | LoadMore ->
        match model.NextCursor with
        | None -> model, Cmd.none
        | Some cursor -> { model with LoadingMore = true }, loadMoreCmd model.Applied cursor

    | MorePageLoaded(Ok page) ->
        {
            model with
                Events = model.Events @ page.Events
                NextCursor = page.NextCursor
                MatchedCount = page.MatchedCount
                LoadingMore = false
        },
        Cmd.none

    // A failed continuation leaves the rows already on screen alone —
    // they are still valid — and reports the failure in the same banner.
    | MorePageLoaded(Error err) ->
        {
            model with
                LoadingMore = false
                Status = Remote.Failed err
        },
        Cmd.none

    | EventTypesLoaded(Ok types) -> { model with EventTypes = types }, Cmd.none

    // The filter bar degrades to a free-of-options select rather than
    // taking the whole page down: the event-type list is an affordance,
    // not the data.
    | EventTypesLoaded(Error _) -> model, Cmd.none

    | ToggleDetail id ->
        let expanded =
            if Set.contains id model.Expanded then
                Set.remove id model.Expanded
            else
                Set.add id model.Expanded

        { model with Expanded = expanded }, Cmd.none

    | ExportCsv ->
        let cmd =
            Cmd.OfRemoting.call auditApi.ExportCsv (toQuery None model.Applied) ExportComplete (fun e ->
                ExportComplete(Error e.Message))

        { model with Exporting = true }, cmd

    | ExportComplete(Ok bytes) -> { model with Exporting = false }, Cmd.ofEffect (fun _ -> downloadCsv bytes)

    | ExportComplete(Error err) ->
        {
            model with
                Exporting = false
                Status = Remote.Failed err
        },
        Cmd.none

// ─── View ────────────────────────────────────────────────────────────

let private formatTimestamp (ts: DateTime) : string =
    ts.ToUniversalTime().ToString "yyyy-MM-dd HH:mm:ss"

/// Pretty-print the recorded payload for the detail expansion. A
/// payload that will not parse is shown as stored rather than hidden —
/// a tombstoned row (the GDPR erasure marker) is exactly that case, and
/// the fact that a row was redacted is itself audit-relevant.
let private prettyPayload (payload: string) : string =
    try
        emitJsExpr payload "JSON.stringify(JSON.parse($0), null, 2)"
    with _ ->
        payload

let private filterBar (msgs: AuditLogMessages) (model: Model) (dispatch: Msg -> unit) =
    // Each visible <label> sits BESIDE its control rather than wrapping
    // it and this tier has no `for`/`id` pairing convention, so every
    // control carries its own accessible name read from the SAME
    // binding as the label — see `UsageDashboard.renderControls` for the
    // full rationale.
    let field (label: string) (control: ReactElement) =
        Html.div [
            prop.className "flex flex-col"
            prop.children [
                Html.label [ prop.className ("text-xs mb-1 " + Tokens.Text.secondary); prop.text label ]
                control
            ]
        ]

    Html.div [
        prop.className "flex flex-wrap items-end gap-4 mb-4"
        prop.children [
            field
                msgs.FromLabel
                (Html.input [
                    prop.type' "date"
                    prop.ariaLabel msgs.FromLabel
                    prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                    prop.value model.Draft.From
                    prop.onChange (SetFrom >> dispatch)
                ])
            field
                msgs.ToLabel
                (Html.input [
                    prop.type' "date"
                    prop.ariaLabel msgs.ToLabel
                    prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                    prop.value model.Draft.To
                    prop.onChange (SetTo >> dispatch)
                ])
            field
                msgs.EventTypeLabel
                (Html.select [
                    prop.ariaLabel msgs.EventTypeLabel
                    prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                    prop.value model.Draft.EventType
                    prop.onChange (SetEventType >> dispatch)
                    prop.children (
                        Html.option [ prop.value ""; prop.text msgs.AllEventTypes ]
                        :: (model.EventTypes
                            |> List.map (fun t -> Html.option [ prop.value t; prop.text t ]))
                    )
                ])
            // The text input commits on Enter / blur, so it takes its
            // accessible name from a wrapping <label>.
            Html.label [
                prop.className "flex flex-col"
                prop.children [
                    Html.span [
                        prop.className [ "text-xs mb-1"; Tokens.Text.secondary ]
                        prop.text msgs.ActorLabel
                    ]
                    Forms.Input.text model.Draft.Actor (SetActor >> dispatch) msgs.ActorPlaceholder
                ]
            ]
            Forms.Button.primaryWith Forms.Button.Enabled msgs.ApplyFilters (fun () -> dispatch ApplyFilters)
            Forms.Button.secondaryWith Forms.Button.Enabled msgs.ClearFilters (fun () -> dispatch ClearFilters)
            Forms.Button.secondaryWith Forms.Button.Enabled msgs.Refresh (fun () -> dispatch Refresh)
            Forms.Button.primaryWith
                (Forms.Button.stateOf model.Exporting msgs.Exporting (List.isEmpty model.Events))
                msgs.ExportCsv
                (fun () -> dispatch ExportCsv)
        ]
    ]

/// The recorded payload, shown under the row's summary while its detail is open.
let private detail (msgs: AuditLogMessages) (view: AuditEventView) =
    Html.div [
        prop.className [ "mt-2 p-3 rounded"; Tokens.Bg.subtle ]
        prop.children [
            Html.div [
                prop.className [ "text-xs font-semibold uppercase tracking-wider mb-1"; Tokens.Text.secondary ]
                prop.text msgs.PayloadHeading
            ]
            Html.pre [
                prop.className [ "text-xs font-mono whitespace-pre-wrap break-all"; Tokens.Text.secondary ]
                prop.text (prettyPayload view.Payload)
            ]
        ]
    ]

let private columns (msgs: AuditLogMessages) (model: Model) (dispatch: Msg -> unit) : Data.Column<AuditEventView> list =
    let mono (text: string) =
        Html.span [ prop.className "font-mono whitespace-nowrap"; prop.text text ]

    [
        {
            Header = msgs.ColumnTime
            Align = Data.Left
            Cell = fun v -> mono (formatTimestamp v.OccurredAt)
        }
        {
            Header = msgs.ColumnEventType
            Align = Data.Left
            Cell = fun v -> mono v.EventType
        }
        {
            Header = msgs.ColumnActor
            Align = Data.Left
            Cell = fun v -> mono (v.Actor |> Option.defaultValue msgs.UnattributedActor)
        }
        {
            Header = msgs.ColumnSummary
            Align = Data.Left
            Cell =
                fun v ->
                    Html.div [
                        prop.className "break-all"
                        prop.children [
                            Html.text v.Summary
                            if Set.contains v.Id model.Expanded then
                                detail msgs v
                        ]
                    ]
        }
        {
            Header = ""
            Align = Data.Right
            Cell =
                fun v ->
                    let isExpanded = Set.contains v.Id model.Expanded

                    Html.button [
                        prop.ariaExpanded isExpanded
                        prop.ariaLabel (if isExpanded then msgs.HideDetail else msgs.ShowDetail)
                        prop.className "px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 border border-gray-300 rounded"
                        prop.onClick (fun _ -> dispatch (ToggleDetail v.Id))
                        prop.text (if isExpanded then "−" else "+")
                    ]
        }
    ]

/// The two empty states are DIFFERENT states, not one message with two
/// wordings: nothing recorded means the trail is off (or nothing
/// audited has happened yet) and the remedy is server-side; nothing
/// matching means the filters are too narrow and the remedy is right
/// here. A single "no results" would send the operator to the wrong one
/// half the time.
let private emptyState (msgs: AuditLogMessages) (model: Model) =
    let filtered = model.Applied <> emptyFilters || not (List.isEmpty model.EventTypes)

    Html.div [
        prop.className ("p-8 text-center text-sm " + Tokens.Text.muted)
        prop.text (if filtered then msgs.NoMatches else msgs.NoEvents)
    ]

let private eventTable (msgs: AuditLogMessages) (model: Model) (dispatch: Msg -> unit) =
    if List.isEmpty model.Events then
        emptyState msgs model
    else
        Html.div [
            prop.children [
                Data.table (columns msgs model dispatch) model.Events
                Html.div [
                    prop.className "flex items-center justify-between mt-3"
                    prop.children [
                        Html.span [
                            prop.className ("text-xs " + Tokens.Text.muted)
                            prop.text (msgs.ShowingCount (List.length model.Events) model.MatchedCount)
                        ]
                        match model.NextCursor with
                        | None -> Html.none
                        | Some _ ->
                            Forms.Button.secondaryWith
                                (Forms.Button.stateOf model.LoadingMore msgs.Loading false)
                                msgs.LoadMore
                                (fun () -> dispatch LoadMore)
                    ]
                ]
            ]
        ]

/// The module body as a React COMPONENT rather than a plain render
/// function, so it has a hook site of its own from which to read the
/// resolved catalog. See `HealthMonitorUI.HealthMonitorBody`.
[<ReactComponent>]
let private AuditLogBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).AuditLog

    Html.div [
        prop.className "p-4"
        prop.children [
            Html.h2 [
                prop.className ("text-lg font-semibold mb-2 " + Tokens.Text.primary)
                prop.text msgs.Heading
            ]
            Html.p [
                prop.className ("text-sm mb-4 " + Tokens.Text.secondary)
                prop.text msgs.Subheading
            ]
            filterBar msgs model dispatch

            match model.Status with
            | Remote.Failed err -> StateViews.noticeStatic (Notice.Problem err)
            | Remote.NotAsked
            | Remote.Loading -> Html.div [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
            | Remote.Reloading()
            | Remote.Loaded() -> eventTable msgs model dispatch
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = AuditLogBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.AuditLog"

/// Create the built-in audit-trail viewer as an `ErasedModule`. The
/// module fills the `AuditViewer` shell slot (Default / Configured),
/// admitted on any authenticated surface. The server-side handler
/// enforces the Owner/Admin gate and the caller-scope isolation
/// independently — the `NavRole` below hides the sidebar entry, which
/// is an affordance, never the boundary.
let create (config: ModuleLabel option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Audit Trail"

    // No dedicated audit glyph ships in `Icons`; `lock` is the closest
    // in the existing set and adding an SVG asset for one module is not
    // this phase's business.
    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.lock

    // SDK-built-in — reserved under the `_sdk.` Id namespace so it can
    // never collide with an app's RBAC-managed `ServerConfig.ModuleNames`.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withView (fun model ctx -> PageContent.FullWidth(view model ctx.Dispatch))
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
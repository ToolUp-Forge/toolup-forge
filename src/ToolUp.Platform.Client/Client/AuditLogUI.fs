// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module AuditLogUI

open Fuaran.MVU
open System
open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Audit-trail viewer admin module (client) ────────────────────────
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

let internal emptyFilters = {
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

let internal loadPage (filters: Filters) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IAuditViewApi.Query"
            auditApi.Query
            (toQuery None filters)
            (RemoteEffect.flatten >> PageLoaded)
    )
]

let internal loadMore (filters: Filters) (cursor: string) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IAuditViewApi.Query"
            auditApi.Query
            (toQuery (Some cursor) filters)
            (RemoteEffect.flatten >> MorePageLoaded)
    )
]

let internal loadEventTypes () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IAuditViewApi.ListEventTypes"
            auditApi.ListEventTypes
            ()
            (RemoteEffect.flatten >> EventTypesLoaded)
    )
]

let internal exportCsv (filters: Filters) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IAuditViewApi.ExportCsv"
            auditApi.ExportCsv
            (toQuery None filters)
            (RemoteEffect.flatten >> ExportComplete)
    )
]

let init () : Model * Step<Msg, RemoteEffect<Msg>> list =
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

    (model, loadPage emptyFilters @ loadEventTypes ())

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
    loadPage filters

/// The browser download of an exported CSV. It dispatches nothing.
let internal download (bytes: byte[]) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(RemoteEffect.signal "AuditLogUI.downloadCsv" (fun () -> downloadCsv bytes))
]

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | SetFrom v ->
        {
            model with
                Draft = { model.Draft with From = v }
        },
        []

    | SetTo v ->
        {
            model with
                Draft = { model.Draft with To = v }
        },
        []

    | SetEventType v ->
        {
            model with
                Draft = { model.Draft with EventType = v }
        },
        []

    | SetActor v ->
        {
            model with
                Draft = { model.Draft with Actor = v }
        },
        []

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
        []

    | PageLoaded(Error err) ->
        {
            model with
                Status = Remote.Failed err
        },
        []

    | LoadMore ->
        match model.NextCursor with
        | None -> model, []
        | Some cursor -> { model with LoadingMore = true }, loadMore model.Applied cursor

    | MorePageLoaded(Ok page) ->
        {
            model with
                Events = model.Events @ page.Events
                NextCursor = page.NextCursor
                MatchedCount = page.MatchedCount
                LoadingMore = false
        },
        []

    // A failed continuation leaves the rows already on screen alone —
    // they are still valid — and reports the failure in the same banner.
    | MorePageLoaded(Error err) ->
        {
            model with
                LoadingMore = false
                Status = Remote.Failed err
        },
        []

    | EventTypesLoaded(Ok types) -> { model with EventTypes = types }, []

    // The filter bar degrades to a free-of-options select rather than
    // taking the whole page down: the event-type list is an affordance,
    // not the data.
    | EventTypesLoaded(Error _) -> model, []

    | ToggleDetail id ->
        let expanded =
            if Set.contains id model.Expanded then
                Set.remove id model.Expanded
            else
                Set.add id model.Expanded

        { model with Expanded = expanded }, []

    | ExportCsv -> { model with Exporting = true }, exportCsv model.Applied

    | ExportComplete(Ok bytes) -> { model with Exporting = false }, download bytes

    | ExportComplete(Error err) ->
        {
            model with
                Exporting = false
                Status = Remote.Failed err
        },
        []

// ─── View ────────────────────────────────────────────────────────────

let internal formatTimestamp (ts: DateTime) : string =
    ts.ToUniversalTime().ToString "yyyy-MM-dd HH:mm:ss"

/// Pretty-print the recorded payload for the detail expansion. A
/// payload that will not parse is shown as stored rather than hidden —
/// a tombstoned row (the GDPR erasure marker) is exactly that case, and
/// the fact that a row was redacted is itself audit-relevant.
let internal prettyPayload (payload: string) : string =
    try
        emitJsExpr payload "JSON.stringify(JSON.parse($0), null, 2)"
    with _ ->
        payload

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: AuditLogMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./AuditLogTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "AuditLogTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.AuditLog
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.AuditLog"

/// The built-in audit-trail viewer (`ShellSlots.builtIn` builds its module). The
/// module fills the `AuditViewer` shell slot (Default / Configured),
/// admitted on any authenticated surface. The server-side handler
/// enforces the Owner/Admin gate and the caller-scope isolation
/// independently — the `NavRole` below hides the sidebar entry, which
/// is an affordance, never the boundary.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Audit Trail"
        Icon = ToolUp.Platform.Icons.lock
    }
    Group = Some "Team Management"
    NavRole = Some ToolUp.Platform.NavRole.TeamOwnerAdmin
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = fun _ -> init ()
    Update = update
    View = BuiltInView.Page view
    Extend = id
}
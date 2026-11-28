// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module UsageDashboard

open Fuaran.MVU
open System
open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform
open ToolUp.Platform.Usage

// ─── Usage admin module (client) ─────────────────────────────────
//
// Read-only Owner / Admin dashboard. Calls `IUsageQueryApi`
// (`Aggregate` / `ExportCsv`) and renders a grouping selector, an
// aggregated table and a CSV export of the month to date, drawn by
// `UsageDashboardTree`. Mirrors `HealthMonitorUI`'s shape —
// `_sdk.UsageDashboard` reserved Id, `Admin` sidebar group.

type Model = {
    Grouping: UsageGrouping
    From: DateTime
    To: DateTime
    Aggregate: Remote<UsageAggregateRow list, string>
    Exporting: bool
}

type Msg =
    | SetGrouping of UsageGrouping
    | SetFrom of DateTime
    | SetTo of DateTime
    | Refresh
    | AggregateLoaded of Result<UsageAggregateRow list, string>
    | ExportCsv
    | ExportComplete of Result<byte[], string>

// ─── API proxy ───────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private usageApi: IUsageQueryApi =
    Api.makeProxy<IUsageQueryApi> (
        routeBuilder = UsageQueryApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// ─── Init / update ───────────────────────────────────────────────

let internal loadAggregate (grouping: UsageGrouping) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IUsageQueryApi.Aggregate"
            usageApi.Aggregate
            grouping
            (Result.mapError _.Message >> AggregateLoaded)
    )
]

let internal exportCsv (range: UsageDateRange option) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IUsageQueryApi.ExportCsv"
            usageApi.ExportCsv
            range
            (Result.mapError _.Message >> ExportComplete)
    )
]

let init () : Model * Step<Msg, RemoteEffect<Msg>> list =
    let now = DateTime.UtcNow
    let monthStart = DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)

    let model = {
        Grouping = ByResourceKind
        From = monthStart
        To = now
        Aggregate = Remote.Loading
        Exporting = false
    }

    (model, loadAggregate model.Grouping)

let private downloadCsv (bytes: byte[]) =
    // Build a Blob, attach an <a download>, click it, revoke the URL.
    let blobObj: obj =
        emitJsExpr (bytes) "new Blob([new Uint8Array($0)], { type: 'text/csv;charset=utf-8' })"

    let url: string = emitJsExpr blobObj "URL.createObjectURL($0)"
    let anchor = document.createElement "a" :?> HTMLAnchorElement
    anchor.href <- url

    anchor?download <- sprintf "usage-%s.csv" (DateTime.UtcNow.ToString "yyyy-MM-dd")

    document.body.appendChild anchor |> ignore
    anchor.click ()
    document.body.removeChild anchor |> ignore
    emitJsExpr<unit> url "URL.revokeObjectURL($0)"

/// The browser download of an exported CSV. It dispatches nothing.
let internal download (bytes: byte[]) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(RemoteEffect.signal "UsageDashboard.downloadCsv" (fun () -> downloadCsv bytes))
]

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | SetGrouping g ->
        let m = {
            model with
                Grouping = g
                Aggregate = Remote.Loading
        }

        m, loadAggregate g

    | SetFrom from -> { model with From = from }, []

    | SetTo toDt -> { model with To = toDt }, []

    | Refresh ->
        {
            model with
                Aggregate = Remote.Loading
        },
        loadAggregate model.Grouping

    | AggregateLoaded result ->
        {
            model with
                Aggregate = Remote.ofResult result
        },
        []

    | ExportCsv ->
        let range = Some { From = model.From; To = model.To }

        { model with Exporting = true }, exportCsv range

    | ExportComplete(Ok bytes) -> { model with Exporting = false }, download bytes

    | ExportComplete(Error _) -> { model with Exporting = false }, []

// ─── View ────────────────────────────────────────────────────────

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: UsageDashboardMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./UsageDashboardTree.fs")

let view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "UsageDashboardTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.UsageDashboard
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ─────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.UsageDashboard"

/// The built-in usage dashboard admin (`ShellSlots.builtIn` builds its module).
/// It fills the `UsageDashboard` shell slot (Default / Configured),
/// which the shell admits on any authenticated surface. The server-side
/// handler enforces the Owner/Admin gate independently.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Usage"
        Icon = ToolUp.Platform.Icons.usage
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

/// The administration-landing tile of this built-in's
/// slot (see `HealthMonitorUI.adminTile` for the full rationale): built
/// for whichever module fills the slot (`owner`), so a replacement keeps
/// the tile, with its own id, name and icon.
///
/// Supply `"_sdk.admin.usage"` from an `IHomeWidgetDataProvider` to
/// lead the tile with the period's headline figure.
let adminTile (owner: ModuleDefinition) : AdminTile = {
    OwnerModuleId = owner.Id
    Widget = {
        Id = "_sdk.tile.usage"
        Title = owner.Name
        Icon = owner.Icon
        Weight = 20
        Body =
            AdminTileBody.summary
                "_sdk.admin.usage"
                "Per-scope consumption and cost telemetry for the current billing period."
    }
}
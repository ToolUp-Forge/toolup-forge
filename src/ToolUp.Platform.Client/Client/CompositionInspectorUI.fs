// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module CompositionInspectorUI

open Fuaran.MVU
open System
open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Composition inspector admin module (client) ─────────────────────
//
// Five read-only pages over `ICompositionInspectorApi`, each rendering
// ONLY what the deployment declared and each offering that declaration's
// canonical JSON for download — the reviewer leaves with the artifact,
// not a screenshot.
//
// **Multi-page (`withPages`), not tabs.** Each panel is a distinct
// question with a distinct answer, and a reviewer working through a
// governance review wants to link to one — "the Rules page of this
// deployment" — rather than to a module with a tab state that a reload
// discards. `withPages` gives each panel its own route for free; a
// hand-rolled tab bar would give none of it.
//
// **One `Model`, five loads, one `Refresh`.** `withPages` fires `Init`
// and `Update` once for the whole module and shares the `Model` across
// pages, so all five panels load when the module is first opened. That
// is the right trade for this surface: the payloads are compose-time
// declarations (a manifest, a rule list, an envelope), the module is
// opened deliberately rather than passed through, and per-page lazy
// loading would make the export button's "the JSON of what you are
// looking at" promise depend on load order.
//
// **Every empty state distinguishes "declared none" from "could not
// say".** A composition that registers no grounding metrics genuinely
// declares none, and the panel says so as a complete answer. A missing
// snapshot is a different thing entirely, and the handler's message
// says which. Rendering both as a bare "no data" would be the one
// failure this module exists to prevent — a reviewer cannot audit a
// blank.

// ─── State ────────────────────────────────────────────────────────────

/// One panel's load. `Result` rather than a `LoadState` DU because
/// every panel here has exactly two outcomes and the error text is the
/// handler's own sentence — there is no third "empty" state to model:
/// emptiness is a property of the loaded value, not of the load.
type PanelData<'T> =
    | PanelLoading
    | PanelLoaded of 'T
    | PanelFailed of string

type Model = {
    Composition: PanelData<CompositionView>
    Surfaces: PanelData<SurfacesView>
    Rules: PanelData<RulesView>
    Disclosure: PanelData<DisclosureView>
    Provenance: PanelData<ProvenanceView>
    /// The panel whose export is in flight, if any. One at a time — the
    /// button is per-panel and each click replaces the last.
    Exporting: InspectorPanel option
    /// An export failure, shown on the panel that asked for it.
    ExportError: (InspectorPanel * string) option
}

type Msg =
    | Refresh
    | CompositionLoaded of Result<CompositionView, string>
    | SurfacesLoaded of Result<SurfacesView, string>
    | RulesLoaded of Result<RulesView, string>
    | DisclosureLoaded of Result<DisclosureView, string>
    | ProvenanceLoaded of Result<ProvenanceView, string>
    | ExportRequested of InspectorPanel
    | ExportComplete of InspectorPanel * Result<string, string>

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private inspectorApi: ICompositionInspectorApi =
    Api.makeProxy<ICompositionInspectorApi> (
        routeBuilder = CompositionInspectorApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// ─── Init / update ───────────────────────────────────────────────────

let internal loadAll () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "ICompositionInspectorApi.GetComposition"
            inspectorApi.GetComposition
            ()
            (RemoteEffect.flatten >> CompositionLoaded)
    )
    Do(
        RemoteEffect.call
            "ICompositionInspectorApi.GetSurfaces"
            inspectorApi.GetSurfaces
            ()
            (RemoteEffect.flatten >> SurfacesLoaded)
    )
    Do(
        RemoteEffect.call
            "ICompositionInspectorApi.GetRules"
            inspectorApi.GetRules
            ()
            (RemoteEffect.flatten >> RulesLoaded)
    )
    Do(
        RemoteEffect.call
            "ICompositionInspectorApi.GetDisclosure"
            inspectorApi.GetDisclosure
            ()
            (RemoteEffect.flatten >> DisclosureLoaded)
    )
    Do(
        RemoteEffect.call
            "ICompositionInspectorApi.GetProvenance"
            inspectorApi.GetProvenance
            ()
            (RemoteEffect.flatten >> ProvenanceLoaded)
    )
]

let internal exportPanel (panel: InspectorPanel) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call "ICompositionInspectorApi.ExportPanel" inspectorApi.ExportPanel panel (fun reply ->
            ExportComplete(panel, RemoteEffect.flatten reply))
    )
]

let private loading: Model = {
    Composition = PanelLoading
    Surfaces = PanelLoading
    Rules = PanelLoading
    Disclosure = PanelLoading
    Provenance = PanelLoading
    Exporting = None
    ExportError = None
}

let init () : Model * Step<Msg, RemoteEffect<Msg>> list = (loading, loadAll ())

/// Fold one panel's answer into its slot.
let private settle (result: Result<'T, string>) : PanelData<'T> =
    match result with
    | Ok value -> PanelLoaded value
    | Error msg -> PanelFailed msg

// ─── JSON download ───────────────────────────────────────────────────

/// Build a Blob, attach an `<a download>`, click it, revoke the URL.
/// The mechanism `UsageDashboard.downloadCsv` and `AuditLogUI` use —
/// same browser constraints, a different media type and filename.
let private downloadJson (panel: InspectorPanel) (json: string) =
    let blobObj: obj =
        emitJsExpr (json) "new Blob([$0], { type: 'application/json;charset=utf-8' })"

    let url: string = emitJsExpr blobObj "URL.createObjectURL($0)"
    let anchor = document.createElement "a" :?> HTMLAnchorElement
    anchor.href <- url

    // The stem comes from the contract (`exportFileName`), the date from
    // here: two panels exported on different days must not overwrite
    // each other in a reviewer's downloads folder.
    anchor?download <-
        sprintf
            "composition-%s-%s"
            (DateTime.UtcNow.ToString "yyyy-MM-dd")
            (CompositionInspectorApi.exportFileName panel)

    document.body.appendChild anchor |> ignore
    anchor.click ()
    document.body.removeChild anchor |> ignore
    emitJsExpr<unit> url "URL.revokeObjectURL($0)"

/// The browser download of an exported panel. It dispatches nothing.
let internal download (panel: InspectorPanel) (json: string) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(RemoteEffect.signal "CompositionInspectorUI.downloadJson" (fun () -> downloadJson panel json))
]

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | Refresh -> { loading with Exporting = None }, loadAll ()
    | CompositionLoaded r -> { model with Composition = settle r }, []
    | SurfacesLoaded r -> { model with Surfaces = settle r }, []
    | RulesLoaded r -> { model with Rules = settle r }, []
    | DisclosureLoaded r -> { model with Disclosure = settle r }, []
    | ProvenanceLoaded r -> { model with Provenance = settle r }, []

    | ExportRequested panel ->
        {
            model with
                Exporting = Some panel
                ExportError = None
        },
        exportPanel panel

    | ExportComplete(panel, Ok json) -> { model with Exporting = None }, download panel json

    | ExportComplete(panel, Error err) ->
        {
            model with
                Exporting = None
                ExportError = Some(panel, err)
        },
        []

// ─── View ─────────────────────────────────────────────────────────────

/// What the deferred tree component takes: the panel its page draws.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: CompositionInspectorMessages
    Panel: InspectorPanel
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./CompositionInspectorTree.fs")

// ─── Module creation ─────────────────────────────────────────────────

let private page (panel: InspectorPanel) =
    let config: PageConfig = {
        // The shell splits a sidebar id at its first `/`, so the route carries it.
        Route = "/" + InspectorPanel.slug panel
        // A `PageConfig` is built before any render, so the sidebar title
        // is the panel's slug-derived English; the page BODY renders the
        // localised title from its `RenderContext`.
        Title =
            (InspectorPanel.slug panel).Substring(0, 1).ToUpperInvariant()
            + (InspectorPanel.slug panel).Substring 1
        Icon = ToolUp.Platform.Icons.interconnected
    }

    config,
    (fun (model: Model) (ctx: RenderContext<Msg>) ->
        PageContent.FullWidth(
            TreeSurface.deferred "CompositionInspectorTree.Surface" loadSurface {|
                Model = model
                Dispatch = ctx.Dispatch
                Messages = ctx.Messages.CompositionInspector
                Panel = panel
                Locale = ctx.Locale
            |}
        ))

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.CompositionInspector"

/// The built-in composition inspector (`ShellSlots.builtIn` builds its module). The
/// module fills the `CompositionInspector` shell slot (Default /
/// Configured), admitted on any authenticated surface. The
/// server-side handler enforces the Owner/Admin gate independently — the
/// `NavRole` below hides the sidebar entry, which is an affordance,
/// never the boundary.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    // `interconnected` is the closest glyph in the shipped set to "what
    // this deployment is wired out of".
    Defaults = {
        Name = "Composition"
        Icon = ToolUp.Platform.Icons.interconnected
    }
    Group = Some "Platform Management"
    NavRole = Some ToolUp.Platform.NavRole.TeamOwnerAdmin
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = fun _ -> init ()
    Update = update
    View =
        BuiltInView.Pages [
            page CompositionPanel
            page SurfacesPanel
            page RulesPanel
            page DisclosurePanel
            page ProvenancePanel
        ]
    Extend = id
}
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module CompositionInspectorUI

open System
open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Phase 593 — composition inspector admin module (client) ─────────
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

let private loadAll () =
    Cmd.batch [
        Cmd.OfRemoting.call inspectorApi.GetComposition () CompositionLoaded (fun e ->
            CompositionLoaded(Error e.Message))
        Cmd.OfRemoting.call inspectorApi.GetSurfaces () SurfacesLoaded (fun e -> SurfacesLoaded(Error e.Message))
        Cmd.OfRemoting.call inspectorApi.GetRules () RulesLoaded (fun e -> RulesLoaded(Error e.Message))
        Cmd.OfRemoting.call inspectorApi.GetDisclosure () DisclosureLoaded (fun e -> DisclosureLoaded(Error e.Message))
        Cmd.OfRemoting.call inspectorApi.GetProvenance () ProvenanceLoaded (fun e -> ProvenanceLoaded(Error e.Message))
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

let init () : Model * Cmd<Msg> = loading, loadAll ()

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

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Refresh -> { loading with Exporting = None }, loadAll ()
    | CompositionLoaded r -> { model with Composition = settle r }, Cmd.none
    | SurfacesLoaded r -> { model with Surfaces = settle r }, Cmd.none
    | RulesLoaded r -> { model with Rules = settle r }, Cmd.none
    | DisclosureLoaded r -> { model with Disclosure = settle r }, Cmd.none
    | ProvenanceLoaded r -> { model with Provenance = settle r }, Cmd.none

    | ExportRequested panel ->
        let cmd =
            Cmd.OfRemoting.call inspectorApi.ExportPanel panel (fun r -> ExportComplete(panel, r)) (fun e ->
                ExportComplete(panel, Error e.Message))

        {
            model with
                Exporting = Some panel
                ExportError = None
        },
        cmd

    | ExportComplete(panel, Ok json) -> { model with Exporting = None }, Cmd.ofEffect (fun _ -> downloadJson panel json)

    | ExportComplete(panel, Error err) ->
        {
            model with
                Exporting = None
                ExportError = Some(panel, err)
        },
        Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

/// A table column: identifiers and values in monospace, prose plain.
let private column (header: string) (cell: 'row -> string) : Data.Column<'row> = {
    Header = header
    Align = Data.Left
    Cell = fun row -> Html.span [ prop.className "font-mono break-all"; prop.text (cell row) ]
}

let private proseColumn (header: string) (cell: 'row -> string) : Data.Column<'row> = {
    Header = header
    Align = Data.Left
    Cell = fun row -> Html.span [ prop.text (cell row) ]
}

let private emptyNote (text: string) =
    Html.p [ prop.className ("text-sm italic " + Tokens.Text.muted); prop.text text ]

let private sectionHeading (text: string) =
    Html.h3 [
        prop.className ("text-sm font-semibold mt-5 mb-2 " + Tokens.Text.primary)
        prop.text text
    ]

/// A component group: heading with its count, then the table or the
/// group's own "declares none" line. The count is in the heading rather
/// than only in the table because a reviewer scanning the page needs the
/// zero to be as visible as the seven.
let private componentSection (msgs: CompositionInspectorMessages) (name: string) (entries: InspectedComponent list) =
    Html.div [
        prop.children [
            sectionHeading (msgs.SectionCount name (List.length entries))
            if List.isEmpty entries then
                emptyNote (msgs.NoneDeclared name)
            else
                Data.table
                    [
                        column msgs.ColumnId (fun (e: InspectedComponent) -> e.Id)
                        column msgs.ColumnKind _.Kind
                        proseColumn msgs.ColumnLabel _.Label
                        column msgs.ColumnImpl (fun e -> e.Impl |> Option.defaultValue "—")
                    ]
                    entries
        ]
    ]

/// The per-panel export control, plus any failure it produced. Rendered
/// at the head of every panel so the affordance sits in the same place
/// on all five.
let private exportBar (msgs: CompositionInspectorMessages) (panel: InspectorPanel) (model: Model) dispatch =
    let inFlight = model.Exporting = Some panel

    Html.div [
        prop.className "flex items-center gap-3 mb-3"
        prop.children [
            Forms.Button.secondaryWith (Forms.Button.stateOf inFlight msgs.Exporting false) msgs.ExportJson (fun () ->
                dispatch (ExportRequested panel))
            Forms.Button.secondaryWith Forms.Button.Enabled msgs.Refresh (fun () -> dispatch Refresh)
            match model.ExportError with
            | Some(failed, err) when failed = panel ->
                Html.span [ prop.className "text-sm text-red-700"; prop.text err ]
            | _ -> Html.none
        ]
    ]

/// Wrap one panel's body in its load state. The failure arm renders the
/// handler's own sentence verbatim — those messages distinguish "you may
/// not read this", "no snapshot was recorded" and a transport failure,
/// and paraphrasing them client-side would collapse the distinction.
let private panelBody (msgs: CompositionInspectorMessages) (data: PanelData<'T>) (render: 'T -> ReactElement) =
    match data with
    | PanelLoading -> Html.div [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
    | PanelFailed err ->
        Html.div [
            prop.className "p-3 bg-red-50 border border-red-200 rounded text-sm text-red-700"
            prop.text err
        ]
    | PanelLoaded value -> render value

/// Shared page chrome: the module heading, the panel title, the export
/// bar, then the body.
/// The panel body as a React COMPONENT rather than a plain
/// render function, so it has a hook site of its own from which to read
/// the resolved catalog. See `HealthMonitorUI.HealthMonitorBody`. All
/// five pages share this one, taking their own body as a parameter: the
/// catalog is only readable from a hook site, so the per-panel renderer
/// has to be invoked from INSIDE the component rather than composed
/// outside it.
[<ReactComponent>]
let private InspectorPage
    (panel: InspectorPanel)
    (model: Model)
    (dispatch: Msg -> unit)
    (renderBody: CompositionInspectorMessages -> Model -> ReactElement)
    =
    let msgs = (MessageCatalogProvider.useMessages ()).CompositionInspector

    let title =
        match panel with
        | CompositionPanel -> msgs.CompositionTitle
        | SurfacesPanel -> msgs.SurfacesTitle
        | RulesPanel -> msgs.RulesTitle
        | DisclosurePanel -> msgs.DisclosureTitle
        | ProvenancePanel -> msgs.ProvenanceTitle

    Html.div [
        prop.className "p-4"
        prop.children [
            Html.h2 [
                prop.className ("text-lg font-semibold mb-1 " + Tokens.Text.primary)
                prop.text msgs.Heading
            ]
            Html.p [
                prop.className ("text-sm mb-3 " + Tokens.Text.secondary)
                prop.text msgs.Subheading
            ]
            Html.h3 [
                prop.className ("text-base font-semibold mb-2 " + Tokens.Text.primary)
                prop.text title
            ]
            exportBar msgs panel model dispatch
            renderBody msgs model
        ]
    ]

// ─── Panels ──────────────────────────────────────────────────────────

let private compositionBody (msgs: CompositionInspectorMessages) (model: Model) =
    panelBody msgs model.Composition (fun view ->
        Html.div [
            prop.children [
                componentSection msgs msgs.Modules view.Modules
                componentSection msgs msgs.CompanionSlots view.CompanionSlots
                componentSection msgs msgs.DataTypes view.DataTypes
                componentSection msgs msgs.Tools view.Tools
                componentSection msgs msgs.Metrics view.Metrics
                componentSection msgs msgs.Subjects view.Subjects
                componentSection msgs msgs.Purposes view.Purposes

                sectionHeading (msgs.SectionCount msgs.ConfigKnobs (List.length view.ConfigKnobs))

                if List.isEmpty view.ConfigKnobs then
                    emptyNote (msgs.NoneDeclared msgs.ConfigKnobs)
                else
                    Data.table
                        [
                            column msgs.ColumnName (fun (k: InspectedKnob) -> k.Name)
                            column msgs.ColumnValue _.Value
                        ]
                        view.ConfigKnobs

                sectionHeading (msgs.SectionCount msgs.CanonicalMethods (List.length view.CanonicalMethods))

                if List.isEmpty view.CanonicalMethods then
                    emptyNote (msgs.NoneDeclared msgs.CanonicalMethods)
                else
                    Data.table
                        [
                            column msgs.ColumnName (fun (m: InspectedCanonicalMethod) -> m.MetricId)
                            column msgs.ColumnValue _.Selector
                        ]
                        view.CanonicalMethods
            ]
        ])

let private surfacesBody (msgs: CompositionInspectorMessages) (model: Model) =
    panelBody msgs model.Surfaces (fun view ->
        Html.div [
            prop.children [
                if List.isEmpty view.Descriptors then
                    match view.Note with
                    | Some note -> emptyNote note
                    | None -> emptyNote (msgs.NoneDeclared msgs.SurfacesTitle)
                else
                    Html.div [
                        prop.children [
                            for descriptor in view.Descriptors ->
                                Html.div [
                                    prop.key (descriptor.Family + ":" + descriptor.Subject)
                                    prop.children [
                                        sectionHeading (descriptor.Family + " — " + descriptor.Subject)
                                        Html.pre [
                                            prop.className
                                                "p-3 bg-gray-50 border border-gray-200 rounded text-xs font-mono overflow-x-auto"
                                            prop.text descriptor.Json
                                        ]
                                    ]
                                ]
                        ]
                    ]
            ]
        ])

let private rulesBody (msgs: CompositionInspectorMessages) (model: Model) =
    panelBody msgs model.Rules (fun view ->
        Html.div [
            prop.children [
                Html.p [
                    prop.className ("text-xs mb-2 " + Tokens.Text.muted)
                    prop.text (msgs.CapturedAt(view.CapturedAtUtc.ToString "u"))
                ]

                sectionHeading (msgs.SectionCount msgs.RuleManifest (List.length view.Rules))

                if List.isEmpty view.Rules then
                    emptyNote (msgs.NoneDeclared msgs.RuleManifest)
                else
                    Data.table
                        [
                            column msgs.ColumnCode (fun (r: InspectedRule) -> r.Code)
                            column msgs.ColumnSeverity _.Severity
                            column msgs.ColumnClass _.RuleClass
                            proseColumn msgs.ColumnDescription _.Description
                        ]
                        view.Rules

                sectionHeading (msgs.SectionCount msgs.PreflightFindings (List.length view.Defects))

                // The pass state is its own sentence, not an empty
                // table: "nothing failed" and "nothing ran" look
                // identical as a blank, and only one of them is a
                // guarantee.
                if List.isEmpty view.Defects then
                    emptyNote msgs.AllRulesPassed
                else
                    Data.table
                        [
                            column msgs.ColumnCode (fun (d: InspectedDefect) -> d.Code)
                            column msgs.ColumnSeverity _.Severity
                            proseColumn msgs.ColumnMessage _.Message
                        ]
                        view.Defects
            ]
        ])

let private disclosureBody (msgs: CompositionInspectorMessages) (model: Model) =
    panelBody msgs model.Disclosure (fun view ->
        Html.div [
            prop.children [
                Html.p [
                    prop.className ("text-xs font-mono break-all mb-3 " + Tokens.Text.muted)
                    prop.text (msgs.EnvelopeDigest + ": " + view.Digest)
                ]

                if List.isEmpty view.Declarations then
                    emptyNote msgs.NoDeclarations
                else
                    Data.table
                        [
                            column msgs.ColumnFacet (fun (d: InspectedDeclaration) -> d.Facet)
                            column msgs.ColumnSubject _.Subject
                            proseColumn msgs.ColumnValue _.Value
                        ]
                        view.Declarations
            ]
        ])

let private provenanceBody (msgs: CompositionInspectorMessages) (model: Model) =
    panelBody msgs model.Provenance (fun view ->
        let stateOf composed =
            if composed then msgs.Composed else msgs.NotComposed

        Html.div [
            prop.children [
                Data.table [ proseColumn msgs.ColumnName fst; column msgs.ColumnValue (snd >> stateOf) ] [
                    msgs.ProvenanceGraph, view.GraphComposed
                    msgs.ProvenanceFactEvidence, view.FactEvidenceComposed
                    msgs.ProvenanceArtifacts, view.ArtifactProvenanceComposed
                ]
                Html.p [
                    prop.className ("text-sm mt-3 " + Tokens.Text.secondary)
                    prop.text msgs.ProvenanceWalkNote
                ]
            ]
        ])

// ─── Module creation ─────────────────────────────────────────────────

let private page (panel: InspectorPanel) (render: CompositionInspectorMessages -> Model -> ReactElement) =
    let config: PageConfig = {
        // The shell splits a sidebar id at its first `/`, so the route carries it.
        Route = "/" + InspectorPanel.slug panel
        // The catalog is only readable from a hook site, so the sidebar
        // title falls back to the panel's slug-derived English. The page
        // BODY renders the localised title; a `PageConfig` is built
        // outside React and cannot consult the provider.
        Title =
            (InspectorPanel.slug panel).Substring(0, 1).ToUpperInvariant()
            + (InspectorPanel.slug panel).Substring 1
        Icon = ToolUp.Platform.Icons.interconnected
    }

    config,
    (fun (model: Model) (dispatch: Msg -> unit) -> PageContent.FullWidth(InspectorPage panel model dispatch render))

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.CompositionInspector"

/// Create the built-in composition inspector as an `ErasedModule`. The
/// module fills the `CompositionInspector` shell slot (Default /
/// Configured), admitted on any authenticated surface. The
/// server-side handler enforces the Owner/Admin gate independently — the
/// `NavRole` below hides the sidebar entry, which is an affordance,
/// never the boundary.
let create (config: ModuleLabel option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Composition"

    // `interconnected` is the closest glyph in the shipped set to "what
    // this deployment is wired out of"; adding an SVG asset for one
    // module is not this phase's business.
    let icon =
        config
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.Platform.Icons.interconnected

    // SDK-built-in — reserved under the `_sdk.` Id namespace so it can
    // never collide with an app's RBAC-managed `ServerConfig.ModuleNames`.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withPages [
        page CompositionPanel compositionBody
        page SurfacesPanel surfacesBody
        page RulesPanel rulesBody
        page DisclosurePanel disclosureBody
        page ProvenancePanel provenanceBody
    ]
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
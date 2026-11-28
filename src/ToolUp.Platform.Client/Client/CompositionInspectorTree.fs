// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The composition inspector's five pages as Fuaran-UI trees, built and drawn
/// in a file of their own, which `CompositionInspectorUI`'s pages mount
/// through `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// Every list arrives whole with its panel's read and never changes in place,
/// so each table's rows are kept per record under the catalog section
/// (`KeepScoped.Rows`), one keep per table, their ids from the record's own
/// identity: an export, the one dispatch that leaves the panels alone, builds
/// no row. A refresh clears every panel first, so there is nothing to keep
/// across one. The provenance panel's three rows are the view's three flags,
/// not records, so that table is kept whole on the view (`KeepScoped.One`).
module internal CompositionInspectorTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open CompositionInspectorUI
open KeepScoped

let private sameMsgs (a: CompositionInspectorMessages) b = sameObject a b

/// A data row whose cells each hold one line of text, in column order.
let private textRow (id: string) (columns: Nodes.Table.Column list) (cells: (string * string) list) : Node<Msg> =
    Nodes.Table.row id columns [
        for part, value in cells ->
            Nodes.Table.cell (Nodes.Ids.child id part) [ Nodes.text (Nodes.Ids.child id (part + "-text")) value ]
    ]

/// A line saying a list is empty, as a complete answer.
let private note (id: string) (text: string) : Node<Msg> = Nodes.text id text

let private sectionHeading (id: string) (text: string) : Node<Msg> = Nodes.heading id 3 text

// ─── The component groups ────────────────────────────────────────────

let private componentColumns (msgs: CompositionInspectorMessages) = [
    Nodes.Table.column "id" msgs.ColumnId 3
    Nodes.Table.column "kind" msgs.ColumnKind 2
    Nodes.Table.column "label" msgs.ColumnLabel 3
    Nodes.Table.column "impl" msgs.ColumnImpl 3
]

/// The seven component groups, each the section key its ids derive from, its
/// name and its entries.
let private groups (msgs: CompositionInspectorMessages) (view: CompositionView) = [
    "modules", msgs.Modules, view.Modules
    "companion-slots", msgs.CompanionSlots, view.CompanionSlots
    "data-types", msgs.DataTypes, view.DataTypes
    "tools", msgs.Tools, view.Tools
    "metrics", msgs.Metrics, view.Metrics
    "subjects", msgs.Subjects, view.Subjects
    "purposes", msgs.Purposes, view.Purposes
]

/// One keep per group, so drawing one group never replaces another's table.
let private componentRows =
    [
        "modules"
        "companion-slots"
        "data-types"
        "tools"
        "metrics"
        "subjects"
        "purposes"
    ]
    |> List.map (fun key ->
        key,
        Rows(
            sameMsgs,
            fun msgs (entry: InspectedComponent) ->
                textRow (Nodes.Ids.keyed ("inspector-" + key) entry.Id) (componentColumns msgs) [
                    "id", entry.Id
                    "kind", entry.Kind
                    "label", entry.Label
                    "impl", entry.Impl |> Option.defaultValue "—"
                ]
        ))
    |> Map.ofList

let private nameValueColumns (msgs: CompositionInspectorMessages) = [
    Nodes.Table.column "name" msgs.ColumnName 2
    Nodes.Table.column "value" msgs.ColumnValue 3
]

let private knobRows =
    Rows(
        sameMsgs,
        fun msgs (knob: InspectedKnob) ->
            textRow (Nodes.Ids.keyed "inspector-knob" knob.Name) (nameValueColumns msgs) [
                "name", knob.Name
                "value", knob.Value
            ]
    )

let private methodRows =
    Rows(
        sameMsgs,
        fun msgs (method: InspectedCanonicalMethod) ->
            textRow (Nodes.Ids.keyed "inspector-method" method.MetricId) (nameValueColumns msgs) [
                "name", method.MetricId
                "value", method.Selector
            ]
    )

/// A section: its heading with its count (the zero as visible as the seven),
/// then its table or its "declares none" line.
let private section
    (id: string)
    (msgs: CompositionInspectorMessages)
    (name: string)
    (entries: 'Row list)
    (table: unit -> Node<Msg>)
    : Node<Msg> =
    Nodes.Layout.column id [
        sectionHeading (Nodes.Ids.child id "heading") (msgs.SectionCount name (List.length entries))
        if List.isEmpty entries then
            note (Nodes.Ids.child id "none") (msgs.NoneDeclared name)
        else
            table ()
    ]

/// A panel's sections, set further apart than a section's own lines so each
/// heading reads with the table under it.
let private sections (id: string) (children: Node<Msg> list) : Node<Msg> =
    Fuaran.box id {
        Defaults.box with
            Layout = BoxLayout.Flex(Orientation.Vertical, false, Some 28)
            Children = children
    }

let private compositionBody (msgs: CompositionInspectorMessages) (view: CompositionView) : Node<Msg> =
    sections "inspector-composition" [
        for key, name, entries in groups msgs view do
            let id = "inspector-" + key

            section id msgs name entries (fun () ->
                Nodes.Table.table
                    (Nodes.Ids.child id "table")
                    name
                    (componentColumns msgs)
                    (componentRows[key].List msgs entries))

        section "inspector-knobs" msgs msgs.ConfigKnobs view.ConfigKnobs (fun () ->
            Nodes.Table.table
                "inspector-knobs-table"
                msgs.ConfigKnobs
                (nameValueColumns msgs)
                (knobRows.List msgs view.ConfigKnobs))

        section "inspector-methods" msgs msgs.CanonicalMethods view.CanonicalMethods (fun () ->
            Nodes.Table.table
                "inspector-methods-table"
                msgs.CanonicalMethods
                (nameValueColumns msgs)
                (methodRows.List msgs view.CanonicalMethods))
    ]

// ─── Surfaces ────────────────────────────────────────────────────────

/// A declared surface: its family and subject, then its descriptor's JSON. The
/// catalog names no columns for them, so they are a list of blocks.
let private descriptorBlocks =
    Rows(
        sameMsgs,
        fun _ (descriptor: InspectedSurface) ->
            let id =
                Nodes.Ids.keyed "inspector-surface" (descriptor.Family + "-" + descriptor.Subject)

            Nodes.Layout.column id [
                sectionHeading (Nodes.Ids.child id "heading") (descriptor.Family + " — " + descriptor.Subject)
                Fuaran.codeBlockSpec (Nodes.Ids.child id "json") {
                    Defaults.codeBlock with
                        Language = "json"
                        Code = descriptor.Json
                        Copyable = false
                }
            ]
    )

let private surfacesBody (msgs: CompositionInspectorMessages) (view: SurfacesView) : Node<Msg> =
    Nodes.Layout.column "inspector-surfaces" [
        if List.isEmpty view.Descriptors then
            note
                "inspector-surfaces-none"
                (view.Note |> Option.defaultWith (fun () -> msgs.NoneDeclared msgs.SurfacesTitle))
        else
            yield! descriptorBlocks.List msgs view.Descriptors
    ]

// ─── Rules ───────────────────────────────────────────────────────────

let private ruleColumns (msgs: CompositionInspectorMessages) = [
    Nodes.Table.column "code" msgs.ColumnCode 2
    Nodes.Table.column "severity" msgs.ColumnSeverity 1
    Nodes.Table.column "class" msgs.ColumnClass 2
    Nodes.Table.column "description" msgs.ColumnDescription 5
]

let private defectColumns (msgs: CompositionInspectorMessages) = [
    Nodes.Table.column "code" msgs.ColumnCode 2
    Nodes.Table.column "severity" msgs.ColumnSeverity 1
    Nodes.Table.column "message" msgs.ColumnMessage 7
]

let private ruleRows =
    Rows(
        sameMsgs,
        fun msgs (rule: InspectedRule) ->
            textRow (Nodes.Ids.keyed "inspector-rule" rule.Code) (ruleColumns msgs) [
                "code", rule.Code
                "severity", rule.Severity
                "class", rule.RuleClass
                "description", rule.Description
            ]
    )

/// A finding's id: its rule's code and its message, since one rule may fail
/// more than once.
let private defectRows =
    Rows(
        sameMsgs,
        fun msgs (defect: InspectedDefect) ->
            let key = sprintf "%s-%d" defect.Code (abs (hash defect.Message))

            textRow (Nodes.Ids.keyed "inspector-defect" key) (defectColumns msgs) [
                "code", defect.Code
                "severity", defect.Severity
                "message", defect.Message
            ]
    )

let private rulesBody (msgs: CompositionInspectorMessages) (view: RulesView) : Node<Msg> =
    sections "inspector-rules" [
        Nodes.text "inspector-captured" (msgs.CapturedAt(universal view.CapturedAtUtc))
        section "inspector-rule-manifest" msgs msgs.RuleManifest view.Rules (fun () ->
            Nodes.Table.table
                "inspector-rules-table"
                msgs.RuleManifest
                (ruleColumns msgs)
                (ruleRows.List msgs view.Rules))

        // The pass state is its own sentence, not an empty table: "nothing
        // failed" and "nothing ran" look identical as a blank, and only one
        // of them is a guarantee.
        Nodes.Layout.column "inspector-findings" [
            sectionHeading
                "inspector-findings-heading"
                (msgs.SectionCount msgs.PreflightFindings (List.length view.Defects))
            if List.isEmpty view.Defects then
                note "inspector-findings-none" msgs.AllRulesPassed
            else
                Nodes.Table.table
                    "inspector-findings-table"
                    msgs.PreflightFindings
                    (defectColumns msgs)
                    (defectRows.List msgs view.Defects)
        ]
    ]

// ─── Disclosure ──────────────────────────────────────────────────────

let private declarationColumns (msgs: CompositionInspectorMessages) = [
    Nodes.Table.column "facet" msgs.ColumnFacet 2
    Nodes.Table.column "subject" msgs.ColumnSubject 2
    Nodes.Table.column "value" msgs.ColumnValue 4
]

let private declarationRows =
    Rows(
        sameMsgs,
        fun msgs (declaration: InspectedDeclaration) ->
            textRow
                (Nodes.Ids.keyed "inspector-declaration" (declaration.Facet + "-" + declaration.Subject))
                (declarationColumns msgs)
                [
                    "facet", declaration.Facet
                    "subject", declaration.Subject
                    "value", declaration.Value
                ]
    )

let private disclosureBody (msgs: CompositionInspectorMessages) (view: DisclosureView) : Node<Msg> =
    Nodes.Layout.column "inspector-disclosure" [
        Nodes.text "inspector-digest" (msgs.EnvelopeDigest + ": " + view.Digest)
        if List.isEmpty view.Declarations then
            note "inspector-declarations-none" msgs.NoDeclarations
        else
            Nodes.Table.table
                "inspector-declarations-table"
                msgs.DisclosureTitle
                (declarationColumns msgs)
                (declarationRows.List msgs view.Declarations)
    ]

// ─── Provenance ──────────────────────────────────────────────────────

let private provenanceTable =
    One(
        (fun (a: CompositionInspectorMessages * ProvenanceView) b ->
            sameObject (fst a) (fst b) && sameObject (snd a) (snd b)),
        fun (msgs, view) ->
            let columns = nameValueColumns msgs

            let stateOf composed =
                if composed then msgs.Composed else msgs.NotComposed

            Nodes.Table.table "inspector-provenance-table" msgs.ProvenanceTitle columns [
                for key, name, composed in
                    [
                        "graph", msgs.ProvenanceGraph, view.GraphComposed
                        "fact-evidence", msgs.ProvenanceFactEvidence, view.FactEvidenceComposed
                        "artifacts", msgs.ProvenanceArtifacts, view.ArtifactProvenanceComposed
                    ] ->
                    textRow (Nodes.Ids.keyed "inspector-provenance" key) columns [
                        "name", name
                        "value", stateOf composed
                    ]
            ]
    )

let private provenanceBody (msgs: CompositionInspectorMessages) (view: ProvenanceView) : Node<Msg> =
    Nodes.Layout.column "inspector-provenance" [
        provenanceTable.Node(msgs, view)
        Nodes.text "inspector-provenance-note" msgs.ProvenanceWalkNote
    ]

// ─── The page ────────────────────────────────────────────────────────

/// One panel's load state. A failure is the handler's own sentence verbatim:
/// it distinguishes "you may not read this", "no snapshot was recorded" and a
/// transport failure, which a paraphrase would collapse.
let private panelBody (msgs: CompositionInspectorMessages) (data: PanelData<'T>) (render: 'T -> Node<Msg>) =
    match data with
    | PanelLoading -> Nodes.text "inspector-loading" msgs.Loading
    | PanelFailed message ->
        Fuaran.callout "inspector-failed" {
            Defaults.callout with
                Body = Nodes.literal message
                Tone = ToneVariant.Critical
        }
    | PanelLoaded value -> render value

/// The export bar, at the head of every panel so it sits in the same place on
/// all five: the panel's JSON, inert while it is being built, the refresh,
/// and the export's failure on the panel that asked for it.
let private exportBar (msgs: CompositionInspectorMessages) (panel: InspectorPanel) (model: Model) : Node<Msg> list = [
    let inFlight = model.Exporting = Some panel

    Nodes.Layout.row "inspector-export-bar" [
        Nodes.Actions.button
            "inspector-export"
            ButtonVariant.Secondary
            (if inFlight then msgs.Exporting else msgs.ExportJson)
            inFlight
            (ExportRequested panel)
        Nodes.Actions.secondary "inspector-refresh" msgs.Refresh Refresh
    ]

    match model.ExportError with
    | Some(failed, message) when failed = panel ->
        Fuaran.callout "inspector-export-failed" {
            Defaults.callout with
                Body = Nodes.literal message
                Tone = ToneVariant.Critical
        }
    | _ -> ()
]

/// One panel's page: the inspector's heading, the panel's title and export
/// bar, then the panel's body. Every node sends its message through the tree.
let tree (msgs: CompositionInspectorMessages) (panel: InspectorPanel) (model: Model) : Node<Msg> =
    let title =
        match panel with
        | CompositionPanel -> msgs.CompositionTitle
        | SurfacesPanel -> msgs.SurfacesTitle
        | RulesPanel -> msgs.RulesTitle
        | DisclosurePanel -> msgs.DisclosureTitle
        | ProvenancePanel -> msgs.ProvenanceTitle

    Nodes.Layout.column "inspector" [
        Nodes.Layout.column "inspector-titles" [
            Nodes.heading "inspector-heading" 2 msgs.Heading
            Nodes.text "inspector-subheading" msgs.Subheading
        ]
        Nodes.heading "inspector-panel-title" 3 title
        yield! exportBar msgs panel model
        match panel with
        | CompositionPanel -> panelBody msgs model.Composition (compositionBody msgs)
        | SurfacesPanel -> panelBody msgs model.Surfaces (surfacesBody msgs)
        | RulesPanel -> panelBody msgs model.Rules (rulesBody msgs)
        | DisclosurePanel -> panelBody msgs model.Disclosure (disclosureBody msgs)
        | ProvenancePanel -> panelBody msgs model.Provenance (provenanceBody msgs)
    ]

/// The deferred component each of `CompositionInspectorUI`'s pages mounts.
let Surface (props: CompositionInspectorUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Panel props.Model))
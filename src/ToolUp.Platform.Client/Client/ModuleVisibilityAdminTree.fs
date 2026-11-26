// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The module-visibility profile editor as a Fuaran-UI tree, built and drawn
/// in a file of its own, which `ModuleVisibilityAdminUI`'s view mounts through
/// `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// The editor's form stays an island: its note is display state a hook owns
/// (committed on save, so free typing does not round-trip through `update`),
/// and its candidate checkboxes sit inside the same form. The renderer's local
/// buffer (`Binding.local` flushed on submit) is not yet a replacement: its
/// submit listener outlives each render of the buffer, so a submit commits
/// every value the field held, the first of them empty. The rule's kind and
/// the resolution are kept on the model sub-values they show.
module internal ModuleVisibilityAdminTree

open Feliz
open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open ModuleVisibilityAdminUI
open KeepScoped

/// One candidate: the module id, its membership of the edited rule, and,
/// once the resolution has landed, whether the server admits it now.
let private candidate
    (msgs: ModuleVisibilityAdminMessages)
    (resolution: ModuleVisibilityResolution option)
    (selected: bool)
    (moduleId: string)
    (onToggle: unit -> unit)
    =
    let admitted =
        match resolution with
        | None -> Html.none
        | Some r ->
            if ModuleVisibility.admitsModule r moduleId then
                Html.span [
                    prop.className "text-xs px-2 py-0.5 rounded bg-green-100 text-green-700"
                    prop.text msgs.VisibleNow
                ]
            else
                Html.span [
                    prop.className ("text-xs px-2 py-0.5 rounded bg-gray-200 " + Tokens.Text.secondary)
                    prop.text msgs.HiddenNow
                ]

    Html.label [
        prop.key moduleId
        prop.className "flex items-center gap-3 px-3 py-2 border-t border-border cursor-pointer hover:bg-gray-50"
        prop.children [
            Html.input [
                prop.type' "checkbox"
                prop.isChecked selected
                prop.onChange (fun (_: bool) -> onToggle ())
            ]
            Html.span [ prop.className "font-mono text-xs flex-1 break-all"; prop.text moduleId ]
            admitted
        ]
    ]

/// The candidates, the note and the two mutating actions. The note is local
/// React state seeded once from `model.Note` on mount, so a reload after a
/// save does not overwrite what the operator is typing; `onSave` carries it.
[<ReactComponent>]
let private EditorForm (msgs: ModuleVisibilityAdminMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let note, setNote = React.useState model.Note

    Html.div [
        prop.children [
            Html.div [
                prop.className "border border-border rounded-lg overflow-hidden mb-4"
                prop.children [
                    Html.div [
                        prop.className ("px-3 py-2 bg-gray-50 text-xs font-medium " + Tokens.Text.secondary)
                        prop.text (msgs.RegisteredModulesHeader model.RegisteredModuleIds.Length model.Selection.Length)
                    ]
                    if List.isEmpty model.RegisteredModuleIds then
                        Html.p [
                            prop.className ("px-3 py-3 text-xs " + Tokens.Text.muted)
                            prop.text msgs.NoCuratableModules
                        ]
                    else
                        for moduleId in model.RegisteredModuleIds do
                            candidate msgs model.Resolved (List.contains moduleId model.Selection) moduleId (fun () ->
                                dispatch (ToggleModule moduleId))
                ]
            ]
            // The label wraps the field, so the field is named by it.
            Html.label [
                prop.className "block mb-4"
                prop.children [
                    Html.span [
                        prop.className ("block text-xs font-medium mb-1 " + Tokens.Text.secondary)
                        prop.text msgs.NoteLabel
                    ]
                    Forms.Input.text note setNote msgs.NotePlaceholder
                ]
            ]
            Html.div [
                prop.className "flex gap-2"
                prop.children [
                    Forms.Button.primaryWith
                        (Forms.Button.stateOf model.Busy msgs.Working false)
                        msgs.SaveProfile
                        (fun () -> dispatch (Save note))
                    Forms.Button.secondaryWith
                        (Forms.Button.stateOf false msgs.Working (model.Busy || model.StoredProfile.IsNone))
                        msgs.ClearProfile
                        (fun () -> dispatch Clear)
                ]
            ]
        ]
    ]

let private notice (id: string) (msgs: ModuleVisibilityAdminMessages) (notice: Notice option) (dismiss: Msg) =
    match notice with
    | Some(Notice.Problem text) -> [ Nodes.Actions.banner id ToneVariant.Critical text msgs.Dismiss dismiss ]
    | Some(Notice.Info text) -> [ Nodes.Actions.banner id ToneVariant.Info text msgs.Dismiss dismiss ]
    | None -> []

// ─── The rule's kind ─────────────────────────────────────────────────

type private KindContext = {
    Msgs: ModuleVisibilityAdminMessages
    Kind: RuleKind
}

/// The rule's two shapes, each a button with what it means beside it.
let private kind =
    One(
        (fun (a: KindContext) b -> sameObject a.Msgs b.Msgs && a.Kind = b.Kind),
        fun context ->
            let msgs = context.Msgs

            let choice (id: string) (label: string) (description: string) (value: RuleKind) =
                Nodes.Layout.column id [
                    Nodes.Actions.button
                        (Nodes.Ids.child id "pick")
                        (if context.Kind = value then
                             ButtonVariant.Primary
                         else
                             ButtonVariant.Secondary)
                        label
                        false
                        (SetKind value)
                    Nodes.text (Nodes.Ids.child id "means") description
                ]

            Nodes.Layout.row "visibility-kind" [
                choice "visibility-allow" msgs.AllowLabel msgs.AllowDescription RuleKind.Allow
                choice "visibility-deny" msgs.DenyLabel msgs.DenyDescription RuleKind.Deny
            ]
    )

// ─── The resolution ──────────────────────────────────────────────────

type private ResolvedContext = {
    Msgs: ModuleVisibilityAdminMessages
    Resolved: ModuleVisibilityResolution option
    Loaded: bool
}

/// The resolved answer beside the editable profile: what makes "I allowed it
/// and it still does not appear" answerable without leaving the page.
let private resolved =
    One(
        (fun (a: ResolvedContext) b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Resolved b.Resolved
            && a.Loaded = b.Loaded),
        fun context ->
            let msgs = context.Msgs

            let items (id: string) (label: int -> string) (values: string list) =
                Nodes.Layout.column id [
                    Nodes.text (Nodes.Ids.child id "label") (label (List.length values))
                    if List.isEmpty values then
                        Nodes.text (Nodes.Ids.child id "none") msgs.NoItems
                    else
                        Nodes.Layout.row (Nodes.Ids.child id "items") [
                            for value in values do
                                Nodes.text (Nodes.Ids.keyed id value) value
                        ]
                ]

            Nodes.Panel.section "visibility-resolved" msgs.ResolvedForYouHeading [
                Nodes.text "visibility-resolved-help" msgs.ResolvedForYouHelp
                match context.Resolved with
                | None when not context.Loaded -> Nodes.text "visibility-resolved-loading" msgs.Loading
                | None -> Nodes.text "visibility-resolved-none" msgs.NoResolutionYet
                | Some r ->
                    items "visibility-governed" msgs.GovernedModules r.GovernedModuleIds
                    items "visibility-selected" msgs.SelectedAfterEveryLayer r.SelectedModuleIds
                    items "visibility-excluded" msgs.ExcludedEntries r.ExcludedEntryIds
                    items "visibility-scopes" msgs.ContributingScopes (r.ContributingScopes |> List.map FlagScope.slug)
            ]
    )

/// The page. `dispatch` reaches the editor's island only; every node sends its
/// message through the tree.
let tree (msgs: ModuleVisibilityAdminMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    Nodes.Layout.column "visibility" [
        Nodes.heading "visibility-heading" 2 msgs.Heading
        Nodes.text "visibility-subheading" msgs.Subheading
        yield! notice "visibility-error" msgs model.Error DismissError
        yield! notice "visibility-status" msgs model.Status DismissStatus
        if not model.Loaded then
            Nodes.text "visibility-loading" msgs.Loading
        else
            Nodes.Panel.section "visibility-profile" msgs.YourProfileHeading [
                Nodes.text "visibility-profile-help" msgs.YourProfileHelp
                kind.Node { Msgs = msgs; Kind = model.Kind }
                TreeLowering.island "visibility-editor" (fun () -> EditorForm msgs model dispatch)
            ]
        resolved.Node {
            Msgs = msgs
            Resolved = model.Resolved
            Loaded = model.ResolvedLoaded
        }
    ]

/// The deferred component `ModuleVisibilityAdminUI`'s view mounts.
[<ReactComponent>]
let Surface (props: ModuleVisibilityAdminUI.SurfaceProps) : ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model props.Dispatch)
    )
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Fuaran.UI
open Fuaran.UI.Types

/// The toolkit's shapes as Fuaran-UI `Node<'Msg>` builders: the same concepts
/// as the Feliz `Layout`, `Forms`, `Panel` and `pageHeader`, for a view authored
/// as a tree. A builder returns a value, never an element, so a view that builds
/// a tree imports no React and the tree is data the estate's tools can read.
///
/// **Ids.** Every node carries an id. A builder takes the id of the node it
/// returns, and a builder that returns several nodes derives the inner ids from
/// that one (`pageHeader "members"` gives `members-title`), so a view names one
/// id per shape and never keeps a second. A node in a list takes its id from the
/// row's identity through `Ids.keyed`, never from its position.
///
/// **Text.** Every user-visible string is a parameter, as in the Feliz toolkit:
/// a view passes what it read from its message catalog, and nothing here holds a
/// literal a reader sees.
///
/// **Events.** A control's message is `Action.Dispatch`: the renderer hands it to
/// the dispatch the tree is lowered with, so a node never closes over a
/// dispatch function and a kept node stays valid across renders (see `Keep`).
[<RequireQualifiedAccess>]
module Nodes =

    /// Ids derived from a caller-supplied prefix.
    [<RequireQualifiedAccess>]
    module Ids =

        /// The id of the part `part` of the shape whose id is `prefix`.
        let child (prefix: string) (part: string) : string = prefix + "-" + part

        /// The id of a list item under `prefix`, from the item's own identity
        /// (its record id, its key), never from its index in the list.
        let keyed (prefix: string) (key: string) : string = prefix + "-" + key

    /// Literal text for a `TextSource` slot.
    let literal (value: string) : TextSource = TextSource.Literal value

    /// A run of literal text, the vocabulary's `Text` node: never parsed, so a
    /// module id such as `_sdk.Team_Manager` reads as itself.
    let text (id: string) (value: string) : Node<'Msg> = Fuaran.text id value

    /// A heading at `level` (1 to 6).
    let heading (id: string) (level: int) (value: string) : Node<'Msg> =
        Fuaran.heading id {
            Defaults.heading with
                Level = level
                Text = literal value
        }

    /// The layout shapes.
    [<RequireQualifiedAccess>]
    module Layout =

        /// A row of children, wrapping: the layout of an action bar. Its
        /// children sit 8 px apart (the renderer writes a gap in pixels).
        let row (id: string) (children: Node<'Msg> list) : Node<'Msg> =
            Fuaran.box id {
                Defaults.box with
                    Layout = BoxLayout.Flex(Orientation.Horizontal, true, Some 8)
                    Children = children
            }

        /// A column of children, 12 px apart.
        let column (id: string) (children: Node<'Msg> list) : Node<'Msg> =
            Fuaran.box id {
                Defaults.box with
                    Layout = BoxLayout.Flex(Orientation.Vertical, false, Some 12)
                    Children = children
            }

        /// A grid of `columns` equal columns, its cells 12 px apart.
        let grid (id: string) (columns: int) (children: Node<'Msg> list) : Node<'Msg> =
            Fuaran.box id {
                Defaults.box with
                    Layout = BoxLayout.Grid(columns, None, Some 12)
                    Children = children
            }

    /// A table whose data rows are built, and kept, one record at a time: a
    /// header row naming each column once, then a row per record with its own
    /// cells and actions. The shape for a list whose actions vary by record (a
    /// button only some rows offer, a row disabled while its call is pending, a
    /// notice under one row), which a `DataGrid` button column cannot carry.
    ///
    /// Every row is a grid of its own on the table's one column template, so
    /// a kept row lines up with the header and with every other row without
    /// knowing about them. The template is built of content-independent tracks
    /// (`minmax(0, <share>fr)`): a column's width depends on the table's width
    /// alone, never on what one row holds, which is what keeps the columns
    /// straight across rows laid out separately.
    ///
    /// Roles: the table carries `table` and its name, each row `row`, each
    /// header cell `columnheader` and each data cell `cell`.
    [<RequireQualifiedAccess>]
    module Table =

        /// One column, built with `column`: the key its header cell's id derives from, the header a
        /// reader meets once, and its share of the table's width.
        type Column = private {
            /// The key the column's header cell id derives from (`<table>-header-<key>`).
            Key: string
            /// The column's name, shown once, in the header row.
            Header: string
            /// The column's share of the table's width, in `fr` parts.
            Share: int
        }

        /// A column headed `header` taking `share` parts of the width (at
        /// least one).
        let column (key: string) (header: string) (share: int) : Column = {
            Key = key
            Header = header
            Share = max 1 share
        }

        /// The track list every row of a table on `columns` lays out on.
        let template (columns: Column list) : string =
            columns
            |> List.map (fun c -> sprintf "minmax(0, %dfr)" (max 1 c.Share))
            |> String.concat " "

        let private withRole (role: string) (label: string option) (node: Node<'Msg>) : Node<'Msg> =
            Node.withAccessibility
                (Some {
                    Defaults.Accessibility.empty with
                        Role = Some(AriaRole.Custom role)
                        Label = label |> Option.map (fun l -> Binding.Static(Some l))
                })
                node

        let private onTemplate (id: string) (columns: Column list) (cells: Node<'Msg> list) : Node<'Msg> =
            Fuaran.box id {
                Defaults.box with
                    Layout = BoxLayout.Grid(List.length columns, Some(template columns), Some 12)
                    Children = cells
            }
            |> withRole "row" None

        /// A data cell: its children stacked 8 px apart.
        let cell (id: string) (children: Node<'Msg> list) : Node<'Msg> =
            Fuaran.box id {
                Defaults.box with
                    Layout = BoxLayout.Flex(Orientation.Vertical, false, Some 8)
                    Children = children
            }
            |> withRole "cell" None

        /// A data row on `columns`: one `cell` per column, in column order.
        /// Build it in a `Keep` so a dispatch that leaves its record alone
        /// serves it again.
        let row (id: string) (columns: Column list) (cells: Node<'Msg> list) : Node<'Msg> = onTemplate id columns cells

        /// The table named `label`: its header row (`<id>-header`, a header
        /// cell `<id>-header-<key>` per column), then `rows`, each built with
        /// `row` on the same `columns`, 8 px apart.
        let table (id: string) (label: string) (columns: Column list) (rows: Node<'Msg> list) : Node<'Msg> =
            let headerId = Ids.child id "header"

            let header =
                onTemplate headerId columns [
                    for c in columns do
                        text (Ids.keyed headerId c.Key) c.Header |> withRole "columnheader" None
                ]

            Fuaran.box id {
                Defaults.box with
                    Layout = BoxLayout.Flex(Orientation.Vertical, false, Some 8)
                    Children = header :: rows
            }
            |> withRole "table" (Some label)

    /// The panel shapes: a headed container and a titled section inside one.
    [<RequireQualifiedAccess>]
    module Panel =

        /// A headed card, the panel shape of a settings surface: its heading at
        /// level 2 (`<id>-title`), then the children. The heading is a heading
        /// node, not the box's `Heading` slot, which renders a `<header>` and
        /// so leaves the panel out of the accessibility outline.
        let card (id: string) (title: string) (children: Node<'Msg> list) : Node<'Msg> =
            Fuaran.box id {
                Defaults.box with
                    Role = BoxRole.Card
                    Children = heading (Ids.child id "title") 2 title :: children
            }

        /// A titled group inside a panel: its title at heading level 3, then
        /// the children in a column.
        let section (id: string) (title: string) (children: Node<'Msg> list) : Node<'Msg> =
            Fuaran.box id {
                Defaults.box with
                    Children = heading (Ids.child id "title") 3 title :: children
            }

    /// The buttons and the dismissable banner.
    [<RequireQualifiedAccess>]
    module Actions =

        /// A button sending `msg`, inert while `disabled`.
        let button (id: string) (variant: ButtonVariant) (label: string) (disabled: bool) (msg: 'Msg) : Node<'Msg> =
            Fuaran.button id {
                Defaults.button with
                    Label = literal label
                    Variant = variant
                    OnClick = Action.dispatch msg
                    Disabled = if disabled then Some(Binding.Static(Some true)) else None
            }

        /// The page's main action.
        let primary (id: string) (label: string) (msg: 'Msg) : Node<'Msg> =
            button id ButtonVariant.Primary label false msg

        /// The quiet companion of `primary` (cancel, back, refresh).
        let secondary (id: string) (label: string) (msg: 'Msg) : Node<'Msg> =
            button id ButtonVariant.Secondary label false msg

        /// An action that removes or revokes something.
        let danger (id: string) (label: string) (msg: 'Msg) : Node<'Msg> =
            button id ButtonVariant.Destructive label false msg

        /// A banner with a dismiss button. `Callout` carries no dismiss action
        /// of its own, so dismissal routes through the module's `update`.
        let banner
            (id: string)
            (tone: ToneVariant)
            (body: string)
            (dismissLabel: string)
            (onDismiss: 'Msg)
            : Node<'Msg> =
            Layout.row id [
                Fuaran.callout (Ids.child id "body") {
                    Defaults.callout with
                        Body = literal body
                        Tone = tone
                }
                button (Ids.child id "dismiss") ButtonVariant.Tertiary dismissLabel false onDismiss
            ]

    /// The page's title row: the title at heading level 1 and the page's
    /// actions beside it. The ids are `<id>-title` and `<id>-actions`.
    let pageHeader (id: string) (title: string) (actions: Node<'Msg> list) : Node<'Msg> =
        Layout.row id [
            heading (Ids.child id "title") 1 title
            if not (List.isEmpty actions) then
                Layout.row (Ids.child id "actions") actions
        ]

    /// Form fields and the form that submits them. A field's id is its name;
    /// the form's id scopes it, so two forms may each have a `name` field.
    [<RequireQualifiedAccess>]
    module Forms =

        let private field (name: string) (label: string) (kind: FormFieldKind<'Msg>) : FormField<'Msg> = {
            Defaults.formField with
                Id = name
                Label = literal label
                Kind = kind
        }

        /// A text field showing `value`, sending `onChange` with each edit.
        let text (name: string) (label: string) (value: string) (onChange: string -> 'Msg) : FormField<'Msg> =
            field
                name
                label
                (FormFieldKind.textControl (Binding.Static(Some value), onChange = (onChange >> Action.dispatch)))

        /// A number field showing `value`, sending `onChange` with each edit.
        let number (name: string) (label: string) (value: float) (onChange: float -> 'Msg) : FormField<'Msg> =
            field
                name
                label
                (FormFieldKind.numberControl (Binding.Static(Some value), onChange = (onChange >> Action.dispatch)))

        /// A checkbox showing `value`, sending `onChange` when it is toggled.
        let checkbox (name: string) (label: string) (value: bool) (onChange: bool -> 'Msg) : FormField<'Msg> =
            field
                name
                label
                (FormFieldKind.checkboxControl (Binding.Static(Some value), onChange = (onChange >> Action.dispatch)))

        /// `field` that must be filled before the form submits.
        let required (field: FormField<'Msg>) : FormField<'Msg> = { field with Required = true }

        /// A form of `fields` sending `onSubmit`, its submit button labelled
        /// `submitLabel` and inert while `disabled`.
        let form
            (id: string)
            (submitLabel: string)
            (disabled: bool)
            (onSubmit: 'Msg)
            (fields: FormField<'Msg> list)
            : Node<'Msg> =
            Fuaran.form id {
                Defaults.form with
                    Fields = fields
                    OnSubmit = Action.dispatch onSubmit
                    SubmitLabel = literal submitLabel
                    Disabled = if disabled then Some(Binding.Static(Some true)) else None
            }

        /// A drop-down of `options` (value, label) showing `value`, sending
        /// `onChange` with the chosen value; the Feliz toolkit's `dropdown`.
        let dropdown
            (id: string)
            (label: string)
            (options: (string * string) list)
            (value: string)
            (onChange: string -> 'Msg)
            : Node<'Msg> =
            Fuaran.select id {
                Defaults.select with
                    Label = literal label
                    Source =
                        Binding.Static(
                            Some(
                                options
                                |> List.map (fun (value, label) -> { Value = value; Label = literal label })
                            )
                        )
                    Value = Some(Binding.Static(Some value))
                    OnChange = Some(fun chosen -> chosen |> Option.defaultValue value |> onChange |> Action.dispatch)
            }
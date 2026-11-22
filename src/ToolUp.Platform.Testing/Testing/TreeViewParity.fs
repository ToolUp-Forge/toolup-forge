// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

/// Accessible parity between two renders of one model: a view as it is
/// written today and the same view authored as a tree. Markup is not
/// compared (a lowered tree's markup is nothing like a hand-written view's);
/// what a user of assistive technology meets is:
///
/// - the **outline**: every control, heading and dialog, by role and
///   accessible name, compared as a multiset so wrapper markup and layout
///   order do not matter;
/// - the **tab order**: the sequence of focus stops, by role and name,
///   compared exactly, because a reordered stop is a different page for a
///   keyboard user even when every name is present.
///
/// Names are computed as a browser does, in its precedence order:
/// `aria-labelledby` resolved through the referenced elements' text, then
/// `aria-label`, then a form control's `<label>` (by `for` or by wrapping),
/// then content, then `title`, then `placeholder`. Ids are never compared:
/// two renders mint different ids, and a name that reaches the user through
/// a reference is the referenced text.
///
/// BCL-only and Fable-safe, over `Accessibility.A11yNode` and
/// `Accessibility.ofHtml`, so it runs wherever the a11y rules run.
[<RequireQualifiedAccess>]
module TreeViewParity =


    /// One entry of the accessible outline: what a screen reader announces.
    /// `Path` locates the element for a diagnostic and takes no part in a
    /// comparison.
    type Stop = {
        Role: string
        Name: string option
        Path: string
    }

    /// What a render exposes: its outline in document order and its focus
    /// stops in tab order.
    type Outline = {
        Stops: Stop list
        TabOrder: Stop list
    }

    /// One way the tree's render differs from the view it replaces.
    type Difference =
        /// The view exposes this, the tree does not.
        | MissingFromTree of Stop
        /// The tree exposes this, the view does not.
        | ExtraInTree of Stop
        /// Both expose the same focus stops in a different order; the two
        /// sequences, by role and name.
        | TabOrderDiffers of view: string list * tree: string list

    let private attr (name: string) (attrs: Map<string, string>) =
        Map.tryFind name attrs |> Option.map _.Trim()

    let private nonEmpty (name: string) (attrs: Map<string, string>) =
        match attr name attrs with
        | Some v when v <> "" -> Some v
        | _ -> None

    let private headingLevel (tag: string) =
        match tag with
        | "h1" -> Some 1
        | "h2" -> Some 2
        | "h3" -> Some 3
        | "h4" -> Some 4
        | "h5" -> Some 5
        | "h6" -> Some 6
        | _ -> None

    /// The roles an outline carries: what a user navigates by.
    let private outlineRoles =
        set [
            "button"
            "checkbox"
            "combobox"
            "dialog"
            "alertdialog"
            "link"
            "listbox"
            "menuitem"
            "menuitemcheckbox"
            "menuitemradio"
            "radio"
            "searchbox"
            "slider"
            "spinbutton"
            "switch"
            "tab"
            "textbox"
            "treeitem"
        ]

    /// The element's role: its explicit `role`, else the role its tag
    /// implies. A heading's role carries its level, `heading(2)`.
    let private roleOf (tag: string) (attrs: Map<string, string>) : string option =
        match nonEmpty "role" attrs with
        | Some r ->
            let first = r.Split(' ').[0].ToLowerInvariant()

            match first with
            | "heading" ->
                let level = nonEmpty "aria-level" attrs |> Option.defaultValue "2"
                Some("heading(" + level + ")")
            | "presentation"
            | "none" -> None
            | other -> Some other
        | None ->
            match headingLevel tag with
            | Some level -> Some(sprintf "heading(%d)" level)
            | None ->
                match tag, attr "type" attrs |> Option.map _.ToLowerInvariant() with
                | "button", _
                | "summary", _ -> Some "button"
                | "a", _ when (nonEmpty "href" attrs).IsSome -> Some "link"
                | "input", Some "hidden" -> None
                | "input", Some("checkbox") -> Some "checkbox"
                | "input", Some("radio") -> Some "radio"
                | "input", Some("range") -> Some "slider"
                | "input", Some("number") -> Some "spinbutton"
                | "input", Some("search") -> Some "searchbox"
                | "input", Some("button" | "submit" | "reset" | "image") -> Some "button"
                | "input", _ -> Some "textbox"
                | "textarea", _ -> Some "textbox"
                | "select", _ ->
                    if Map.containsKey "multiple" attrs then
                        Some "listbox"
                    else
                        Some "combobox"
                | "dialog", _ -> Some "dialog"
                | _ -> None

    let private isHidden (attrs: Map<string, string>) =
        attr "aria-hidden" attrs = Some "true" || Map.containsKey "hidden" attrs

    let private isFormControl (tag: string) (attrs: Map<string, string>) =
        match tag, attr "type" attrs |> Option.map _.ToLowerInvariant() with
        | "input", Some("button" | "submit" | "reset" | "image" | "hidden") -> false
        | "input", _
        | "select", _
        | "textarea", _ -> true
        | _ -> false

    /// Whether the element is a focus stop, and its tab index (`0` for the
    /// natural order).
    let private tabIndexOf (tag: string) (attrs: Map<string, string>) : int option =
        let explicitIndex =
            match attr "tabindex" attrs with
            | Some raw ->
                match System.Int32.TryParse raw with
                | true, v -> Some v
                | _ -> None
            | None -> None

        let disabled = Map.containsKey "disabled" attrs

        let natural =
            match tag with
            | "button"
            | "select"
            | "textarea"
            | "summary" -> not disabled
            | "input" -> not disabled && attr "type" attrs <> Some "hidden"
            | "a" -> (nonEmpty "href" attrs).IsSome
            | _ -> false

        match explicitIndex with
        | Some v when v < 0 -> None
        | Some v when natural || not disabled -> Some v
        | Some _ -> None
        | None -> if natural then Some 0 else None

    // ─── the walk ─────────────────────────────────────────────────────

    type private Visited = {
        Tag: string
        Attrs: Map<string, string>
        Children: Accessibility.A11yNode list
        Path: string
        /// The text of the `<label>` wrapping this element, if any.
        WrappingLabel: string option
    }

    let private visit (root: Accessibility.A11yNode) : Visited list =
        let acc = System.Collections.Generic.List<Visited>()

        let rec go (path: string) (label: string option) (index: int) (node: Accessibility.A11yNode) =
            match node with
            | Accessibility.Text _ -> ()
            | Accessibility.Element(tag, attrs, children) ->
                if not (isHidden attrs) then
                    let here =
                        if tag = "#fragment" then
                            path
                        else
                            let seg =
                                match nonEmpty "id" attrs with
                                | Some id -> tag + "#" + id
                                | None -> sprintf "%s[%d]" tag index

                            if path = "" then seg else path + " > " + seg

                    let label' =
                        if tag = "label" then
                            Some(
                                children
                                |> List.map Accessibility.visibleText
                                |> String.concat " "
                                |> Accessibility.normaliseName
                            )
                        else
                            label

                    if tag <> "#fragment" then
                        acc.Add {
                            Tag = tag
                            Attrs = attrs
                            Children = children
                            Path = here
                            WrappingLabel = label
                        }

                    children |> List.iteri (fun i child -> go here label' i child)

        go "" None 0 root
        List.ofSeq acc

    let private textOf (children: Accessibility.A11yNode list) =
        children
        |> List.map Accessibility.visibleText
        |> String.concat " "
        |> Accessibility.normaliseName

    /// The accessible name of `v`, by the precedence in this module's summary.
    let private nameOf (byId: Map<string, Visited>) (labelsFor: Map<string, string>) (v: Visited) : string option =
        let orElse (next: unit -> string option) (current: string option) =
            match current with
            | Some _ -> current
            | None -> next ()

        let labelledBy () =
            nonEmpty "aria-labelledby" v.Attrs
            |> Option.bind (fun ids ->
                let text =
                    ids.Split(' ')
                    |> Array.filter (fun s -> s <> "")
                    |> Array.choose (fun id -> Map.tryFind id byId |> Option.map (fun r -> textOf r.Children))
                    |> String.concat " "
                    |> Accessibility.normaliseName

                if text = "" then None else Some text)

        let control = isFormControl v.Tag v.Attrs

        let label () =
            if not control then
                None
            else
                let byFor =
                    nonEmpty "id" v.Attrs |> Option.bind (fun id -> Map.tryFind id labelsFor)

                match byFor with
                | Some t when t <> "" -> Some t
                | _ ->
                    match v.WrappingLabel with
                    | Some t when t <> "" -> Some t
                    | _ -> None

        let content () =
            match v.Tag, attr "type" v.Attrs |> Option.map _.ToLowerInvariant() with
            | "input", Some("submit" | "button" | "reset") -> nonEmpty "value" v.Attrs
            | "input", Some "image" -> nonEmpty "alt" v.Attrs
            | _ when control -> None
            | _ ->
                let t = textOf v.Children
                if t = "" then None else Some t

        labelledBy ()
        |> orElse (fun () -> nonEmpty "aria-label" v.Attrs |> Option.map Accessibility.normaliseName)
        |> orElse label
        |> orElse content
        |> orElse (fun () -> nonEmpty "title" v.Attrs)
        |> orElse (fun () -> if control then nonEmpty "placeholder" v.Attrs else None)

    /// The outline and the tab order of a render.
    let outline (root: Accessibility.A11yNode) : Outline =
        let visited = visit root

        let byId =
            visited
            |> List.choose (fun v -> nonEmpty "id" v.Attrs |> Option.map (fun id -> id, v))
            |> List.distinctBy fst
            |> Map.ofList

        let labelsFor =
            visited
            |> List.filter (fun v -> v.Tag = "label")
            |> List.choose (fun v -> nonEmpty "for" v.Attrs |> Option.map (fun id -> id, textOf v.Children))
            |> List.distinctBy fst
            |> Map.ofList

        let stopOf (v: Visited) (role: string) = {
            Role = role
            Name = nameOf byId labelsFor v
            Path = v.Path
        }

        let stops = [
            for v in visited do
                match roleOf v.Tag v.Attrs with
                | Some role when outlineRoles.Contains role || role.StartsWith "heading(" -> yield stopOf v role
                | _ -> ()
        ]

        let focusable = [
            for i, v in List.indexed visited do
                match tabIndexOf v.Tag v.Attrs with
                | Some t ->
                    let role = roleOf v.Tag v.Attrs |> Option.defaultValue v.Tag
                    yield t, i, stopOf v role
                | None -> ()
        ]

        // Positive tab indices first, ascending; then the natural order.
        let tabOrder =
            (focusable
             |> List.filter (fun (t, _, _) -> t > 0)
             |> List.sortBy (fun (t, i, _) -> t, i))
            @ (focusable |> List.filter (fun (t, _, _) -> t = 0))
            |> List.map (fun (_, _, s) -> s)

        { Stops = stops; TabOrder = tabOrder }

    /// The outline of an HTML fragment.
    let ofHtml (html: string) : Outline = outline (Accessibility.ofHtml html)

    let private label (s: Stop) =
        match s.Name with
        | Some n -> sprintf "%s \"%s\"" s.Role n
        | None -> s.Role + " (unnamed)"

    let private key (s: Stop) = s.Role, s.Name

    /// Every difference between the view's outline and the tree's. Empty
    /// means parity.
    let compare (view: Outline) (tree: Outline) : Difference list =
        let counts (stops: Stop list) = stops |> List.countBy key |> Map.ofList

        let surplus (from: Stop list) (other: Map<string * string option, int>) =
            from
            |> List.groupBy key
            |> List.collect (fun (k, group) ->
                let theirs = Map.tryFind k other |> Option.defaultValue 0
                group |> List.skip (min theirs group.Length))

        let missing = surplus view.Stops (counts tree.Stops) |> List.map MissingFromTree
        let extra = surplus tree.Stops (counts view.Stops) |> List.map ExtraInTree

        let viewTabs = view.TabOrder |> List.map label
        let treeTabs = tree.TabOrder |> List.map label

        let tabs =
            if viewTabs <> treeTabs then
                [ TabOrderDiffers(viewTabs, treeTabs) ]
            else
                []

        missing @ extra @ tabs

    /// One line per difference, naming the element on the side it is on.
    let describe (d: Difference) : string =
        match d with
        | MissingFromTree s -> sprintf "missing from the tree: %s (the view's %s)" (label s) s.Path
        | ExtraInTree s -> sprintf "only in the tree: %s (at %s)" (label s) s.Path
        | TabOrderDiffers(v, t) ->
            let at =
                Seq.zip (Seq.append v (Seq.replicate t.Length "<end>")) (Seq.append t (Seq.replicate v.Length "<end>"))
                |> Seq.tryFindIndex (fun (a, b) -> a <> b)
                |> Option.defaultValue 0

            sprintf
                "tab order differs from stop %d:\n    view: %s\n    tree: %s"
                (at + 1)
                (String.concat " → " v)
                (String.concat " → " t)

    /// The whole diff as the failure message of a parity assertion.
    let report (differences: Difference list) : string =
        sprintf "the tree is not at accessible parity with the view (%d difference(s)):\n" differences.Length
        + (differences |> List.map (describe >> sprintf "  - %s") |> String.concat "\n")

    /// Compare two rendered HTML fragments, the view's first; throw the
    /// report when they differ.
    let assertHtml (viewHtml: string) (treeHtml: string) : unit =
        match compare (ofHtml viewHtml) (ofHtml treeHtml) with
        | [] -> ()
        | differences -> failwith (report differences)
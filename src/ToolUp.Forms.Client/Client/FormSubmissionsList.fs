// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.FormSubmissionsList

open Feliz
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.WorkflowBadge

// ─── Phase 21 — Generic submissions list ────────────────────────────
//
// Tiny presentational component that renders a list of Submissions
// as a HTML table. The caller supplies the already-loaded list (and
// presumably built it from `FormsClient.proxy.ListSubmissions
// query`); this component does not fetch or filter.
//
// Out of scope for v1 (deferred follow-ups):
//   * AG-Grid integration with server-side paging — for now an
//     unstyled `Html.table` keeps the companion's client surface
//     dependency-light.
//   * Per-row actions (view / edit / transition) — caller renders
//     their own action column by passing a `renderActions` callback
//     in a future revision.
//   * Sorting / filtering UI on top of the list.

// ─── Phase 21a — matrix-value summary ───────────────────────────────
//
// A `MatrixField` flattens into the submission map under
// `{base}[{row},{col}]` sub-keys. This component has only the
// `Submission` (no schema), so it recovers the matrix shape directly
// from the sub-key pattern: group cells by base key, infer the
// dimensions from the max row/col, and show a collapsed
// "{rows}×{cols} grid" chip that expands to the raw cell values.

/// Parse a `base[row,col]` sub-key. Returns `Some (base, row, col)` for
/// a well-formed matrix cell key, `None` for an ordinary flat key.
let private tryParseCellKey (k: string) : (string * int * int) option =
    if k.EndsWith "]" then
        match k.LastIndexOf '[' with
        | -1 -> None
        | openIdx ->
            let baseKey = k.Substring(0, openIdx)
            let inner = k.Substring(openIdx + 1, k.Length - openIdx - 2)

            match inner.Split(',') with
            | [| rowStr; colStr |] ->
                match System.Int32.TryParse rowStr, System.Int32.TryParse colStr with
                | (true, r), (true, c) when r >= 0 && c >= 0 && baseKey <> "" -> Some(baseKey, r, c)
                | _ -> None
            | _ -> None
    else
        None

/// Render a single `FieldValue` compactly for the expanded grid view.
let private displayValue (v: FieldValue) : string =
    match v with
    | TextValue s -> s
    | NumberValue n -> string n
    // DateOnly.ToString is not Fable-supported — format from the parts.
    | DateValue d -> sprintf "%04i-%02i-%02i" d.Year d.Month d.Day
    | DateTimeValue dt -> dt.ToString("yyyy-MM-dd HH:mm")
    | BoolValue b -> if b then "✓" else "·"
    | ChoiceValue c -> c
    | MultiChoiceValue cs -> String.concat ", " cs
    | FileValue id -> id
    | EntityRefValue id -> id
    | NestedSubmissionValue id -> id

/// A one-line summary hint for a matrix's cells: `Σ=<sum>` when every
/// cell is numeric, `<n> selected` when every cell is boolean, else
/// nothing (dimensions alone are shown).
let private summariseCells (cells: FieldValue list) : string =
    let allNumbers =
        cells
        |> List.forall (fun v ->
            match v with
            | NumberValue _ -> true
            | _ -> false)

    let allBools =
        cells
        |> List.forall (fun v ->
            match v with
            | BoolValue _ -> true
            | _ -> false)

    if not (List.isEmpty cells) && allNumbers then
        let sum =
            cells
            |> List.sumBy (fun v ->
                match v with
                | NumberValue n -> n
                | _ -> 0.0)

        sprintf ": Σ=%g" sum
    elif not (List.isEmpty cells) && allBools then
        let trues =
            cells
            |> List.filter (fun v ->
                match v with
                | BoolValue true -> true
                | _ -> false)
            |> List.length

        sprintf ": %d selected" trues
    else
        ""

/// One matrix group recovered from a submission's values: its base key,
/// inferred dimensions, and the (row, col, value) cells.
type private MatrixGroup = {
    BaseKey: string
    Rows: int
    Cols: int
    Cells: (int * int * FieldValue) list
}

/// Recover every matrix group present in a submission's value map.
let private matrixGroups (values: Map<string, FieldValue>) : MatrixGroup list =
    values
    |> Map.toList
    |> List.choose (fun (k, v) ->
        match tryParseCellKey k with
        | Some(baseKey, r, c) -> Some(baseKey, r, c, v)
        | None -> None)
    |> List.groupBy (fun (baseKey, _, _, _) -> baseKey)
    |> List.map (fun (baseKey, entries) ->
        let cells = entries |> List.map (fun (_, r, c, v) -> (r, c, v))
        let rows = (cells |> List.map (fun (r, _, _) -> r) |> List.max) + 1
        let cols = (cells |> List.map (fun (_, c, _) -> c) |> List.max) + 1

        {
            BaseKey = baseKey
            Rows = rows
            Cols = cols
            Cells = cells
        })
    |> List.sortBy _.BaseKey

/// Collapsed-by-default summary of every matrix in a submission, with a
/// per-submission expand toggle revealing the raw cell grids.
[<ReactComponent>]
let MatrixSummary (values: Map<string, FieldValue>) =
    let expanded, setExpanded = React.useState false
    let groups = matrixGroups values

    if List.isEmpty groups then
        Html.none
    else
        Html.div [
            prop.children [
                Html.button [
                    prop.type'.button
                    prop.onClick (fun _ -> setExpanded (not expanded))
                    prop.style [
                        style.cursor.pointer
                        style.backgroundColor "transparent"
                        style.border (0, borderStyle.none, "")
                        style.color "#2563eb"
                        style.padding 0
                        style.fontSize (length.em 0.9)
                    ]
                    prop.text (
                        let chips =
                            groups
                            |> List.map (fun g ->
                                sprintf
                                    "%d×%d grid%s"
                                    g.Rows
                                    g.Cols
                                    (summariseCells (g.Cells |> List.map (fun (_, _, v) -> v))))
                            |> String.concat ", "

                        (if expanded then "▾ " else "▸ ") + chips
                    )
                ]
                if expanded then
                    for g in groups do
                        Html.div [
                            prop.key g.BaseKey
                            prop.style [ style.marginTop (length.em 0.4) ]
                            prop.children [
                                Html.div [
                                    prop.style [ style.fontSize (length.em 0.8); style.color "#6b7280" ]
                                    prop.text g.BaseKey
                                ]
                                Html.table [
                                    prop.style [ style.borderCollapse.collapse; style.fontSize (length.em 0.85) ]
                                    prop.children [
                                        Html.tbody [
                                            for r in 0 .. g.Rows - 1 do
                                                Html.tr [
                                                    prop.key (string r)
                                                    prop.children [
                                                        for c in 0 .. g.Cols - 1 do
                                                            let cell =
                                                                g.Cells
                                                                |> List.tryPick (fun (cr, cc, v) ->
                                                                    if cr = r && cc = c then Some v else None)

                                                            Html.td [
                                                                prop.key (string c)
                                                                prop.style [
                                                                    style.padding (length.em 0.25)
                                                                    style.border (1, borderStyle.solid, "#e5e7eb")
                                                                    style.textAlign.center
                                                                ]
                                                                prop.text (
                                                                    match cell with
                                                                    | Some v -> displayValue v
                                                                    | None -> ""
                                                                )
                                                            ]
                                                    ]
                                                ]
                                        ]
                                    ]
                                ]
                            ]
                        ]
            ]
        ]

[<ReactComponent>]
let FormSubmissionsList (submissions: Submission list) =
    Html.table [
        prop.style [ style.width (length.percent 100); style.borderCollapse.collapse ]
        prop.children [
            Html.thead [
                Html.tr [
                    for h in [ "Form"; "Submitted by"; "Submitted at"; "State"; "Matrix" ] do
                        Html.th [
                            prop.style [
                                style.textAlign.left
                                style.padding (length.em 0.5)
                                style.borderBottom (1, borderStyle.solid, "#e5e7eb")
                            ]
                            prop.text h
                        ]
                ]
            ]
            Html.tbody [
                if List.isEmpty submissions then
                    Html.tr [
                        Html.td [
                            prop.colSpan 5
                            prop.style [ style.padding (length.em 1); style.color "#6b7280"; style.textAlign.center ]
                            prop.text "No submissions."
                        ]
                    ]
                else
                    for s in submissions do
                        Html.tr [
                            prop.key s.Id
                            prop.children [
                                Html.td [ prop.style [ style.padding (length.em 0.5) ]; prop.text s.FormId ]
                                Html.td [
                                    prop.style [ style.padding (length.em 0.5) ]
                                    prop.text (
                                        match s.Author with
                                        | AuthenticatedUser uid -> uid
                                        | InvitedRespondent(tokenId, Some handle) -> handle
                                        | InvitedRespondent(tokenId, None) -> "(token " + tokenId.Substring(0, 8) + "…)"
                                    )
                                ]
                                Html.td [
                                    prop.style [ style.padding (length.em 0.5) ]
                                    prop.text (s.SubmittedAt.ToString("yyyy-MM-dd HH:mm"))
                                ]
                                Html.td [
                                    prop.style [ style.padding (length.em 0.5) ]
                                    prop.children [ WorkflowBadge s.State ]
                                ]
                                Html.td [
                                    prop.style [ style.padding (length.em 0.5) ]
                                    prop.children [ MatrixSummary s.Values ]
                                ]
                            ]
                        ]
            ]
        ]
    ]
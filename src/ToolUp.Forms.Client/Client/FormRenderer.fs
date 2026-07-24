// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.FormRenderer

open System
open Feliz
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission

// ─── Phase 21 — Schema-driven form renderer ─────────────────────────
//
// React component that renders a FormSchema as typed inputs with
// React.useState for in-flight values (per CLAUDE.md "text inputs /
// transient UI state" rule — only dispatch upward on submit, never
// per-keystroke).
//
// `onSubmit` receives the assembled `Map<string, FieldValue>`; the
// caller wraps the values in a `SubmitRequest` and calls
// `FormsClient.proxy.Submit`. The caller is also responsible for
// surfacing the server's `Result<Submission, FormError>` (with
// per-field `FieldError` highlights) — the renderer doesn't try to
// reach back into its own internal state from outside.
//
// Out of scope for v1 (deferred follow-ups):
//   * Conditional field visibility ("show field B only if field A == X")
//   * Computed fields ("field C = field A + field B")
//   * Drag-drop designer UI
//   * File upload (FileField renders a placeholder)
//   * Nested forms (NestedFormField renders a placeholder)
//   * Entity reference picker (EntityRefField renders a text input)
//   * Per-field client-side validation mirror — server is the source
//     of truth; client just collects inputs

// Map a FieldSchema + transient string state to a FieldValue. Returns
// `None` for empty input on a non-required field; `Some` for any
// present value (even if invalid — server validates).
let private stringToFieldValue (kind: FieldKind) (raw: string) : FieldValue option =
    if String.IsNullOrEmpty raw && not (kind = BoolField) then
        None
    else
        match kind with
        | TextField _ -> Some(TextValue raw)
        | NumberField _ ->
            match Double.TryParse raw with
            | true, n -> Some(NumberValue n)
            | _ -> Some(TextValue raw) // Defer to server "wrong-type" error.
        | DateField ->
            match DateOnly.TryParse raw with
            | true, d -> Some(DateValue d)
            | _ -> Some(TextValue raw)
        | DateTimeField ->
            match DateTimeOffset.TryParse raw with
            | true, dt -> Some(DateTimeValue dt)
            | _ -> Some(TextValue raw)
        | BoolField -> Some(BoolValue(raw = "true"))
        | ChoiceField _ -> Some(ChoiceValue raw)
        | MultiChoiceField _ -> Some(MultiChoiceValue(raw.Split(',') |> Array.toList))
        | FileField _ -> Some(FileValue raw)
        | EntityRefField _ -> Some(EntityRefValue raw)
        | NestedFormField _ -> Some(NestedSubmissionValue raw)
        // Phase 21a — a matrix has no direct value of its own; its cells
        // flatten into `{key}[r,c]` sub-keys (handled in `buildSubmission`
        // with the per-cell `FieldKind`). This arm is only reached if a
        // matrix key is looked up directly, which never happens.
        | MatrixField _ -> None

let private renderInput (field: FieldSchema) (value: string) (onChange: string -> unit) : ReactElement =
    match field.Kind with
    | TextField maxLen ->
        Html.input [
            prop.type'.text
            prop.value value
            prop.onChange (fun (v: string) -> onChange v)
            prop.required field.Required
            match maxLen with
            | Some n -> prop.maxLength n
            | None -> ()
        ]
    | NumberField _ ->
        Html.input [
            prop.type'.number
            prop.value value
            prop.onChange (fun (v: string) -> onChange v)
            prop.required field.Required
        ]
    | DateField ->
        Html.input [
            prop.type'.date
            prop.value value
            prop.onChange (fun (v: string) -> onChange v)
            prop.required field.Required
        ]
    | DateTimeField ->
        Html.input [
            prop.type'.dateTimeLocal
            prop.value value
            prop.onChange (fun (v: string) -> onChange v)
            prop.required field.Required
        ]
    | BoolField ->
        let isChecked = value = "true"

        Html.input [
            prop.type'.checkbox
            prop.isChecked isChecked
            prop.onChange (fun (b: bool) -> onChange (if b then "true" else "false"))
        ]
    | ChoiceField options ->
        Html.select [
            prop.value value
            prop.onChange (fun (v: string) -> onChange v)
            prop.required field.Required
            prop.children [
                Html.option [ prop.value ""; prop.text "—" ]
                for o in options do
                    Html.option [ prop.value o; prop.text o ]
            ]
        ]
    | MultiChoiceField options ->
        let selected =
            if value = "" then
                Set.empty
            else
                value.Split(',') |> Set.ofArray

        Html.div [
            prop.children [
                for o in options do
                    Html.label [
                        prop.style [ style.display.block ]
                        prop.children [
                            Html.input [
                                prop.type'.checkbox
                                prop.isChecked (Set.contains o selected)
                                prop.onChange (fun (b: bool) ->
                                    let next = if b then Set.add o selected else Set.remove o selected

                                    onChange (next |> Set.toList |> String.concat ","))
                            ]
                            Html.text o
                        ]
                    ]
            ]
        ]
    | FileField _ ->
        Html.input [
            prop.type'.text
            prop.placeholder "(file id — upload UI deferred)"
            prop.value value
            prop.onChange (fun (v: string) -> onChange v)
        ]
    | EntityRefField _ ->
        Html.input [
            prop.type'.text
            prop.placeholder "(entity id)"
            prop.value value
            prop.onChange (fun (v: string) -> onChange v)
        ]
    | NestedFormField _ ->
        Html.input [
            prop.type'.text
            prop.placeholder "(nested submission id — picker deferred)"
            prop.value value
            prop.onChange (fun (v: string) -> onChange v)
        ]
    // Phase 21a — a nested matrix cell is out of scope (`Matrix.create`
    // rejects it); render a safe placeholder rather than recursing.
    | MatrixField _ ->
        Html.span [
            prop.style [ style.color "#6b7280" ]
            prop.text "(nested matrix unsupported)"
        ]

// Phase 21a — render a `MatrixField` as an HTML table: a header row of
// column labels, then one row per matrix row (labelled) whose cells reuse
// `renderInput` with the cell `FieldKind`. Each cell reads/writes the
// flattened `{key}[r,c]` sub-key in the shared transient state, so the
// assembled submission (see `buildSubmission`) picks the values up with
// no matrix-specific plumbing.
let private renderMatrix
    (field: FieldSchema)
    (rows: int)
    (cols: int)
    (cell: FieldKind)
    (labels: MatrixFieldLabels option)
    (values: Map<string, string>)
    (updateField: string -> string -> unit)
    : ReactElement =
    // A synthetic per-cell field: the cell's `FieldKind` + the outer
    // field's `Required` flag, so `renderInput`'s required marking and
    // max-length behaviour carry through to every cell.
    let cellField = { field with Kind = cell }

    let headerCellStyle = [
        style.padding (length.em 0.35)
        style.borderBottom (1, borderStyle.solid, "#e5e7eb")
        style.fontWeight 500
        style.fontSize (length.em 0.85)
    ]

    Html.div [
        prop.style [ style.overflowX.auto ]
        prop.children [
            Html.table [
                prop.style [ style.borderCollapse.collapse ]
                prop.children [
                    Html.thead [
                        Html.tr [
                            prop.children [
                                // Empty corner cell above the row labels.
                                Html.th [ prop.style headerCellStyle; prop.text "" ]
                                for c in 0 .. cols - 1 do
                                    Html.th [ prop.style headerCellStyle; prop.text (Matrix.colLabel labels c) ]
                            ]
                        ]
                    ]
                    Html.tbody [
                        for r in 0 .. rows - 1 do
                            Html.tr [
                                prop.key (string r)
                                prop.children [
                                    Html.th [
                                        prop.style [
                                            style.padding (length.em 0.35)
                                            style.textAlign.left
                                            style.fontWeight 500
                                            style.fontSize (length.em 0.85)
                                        ]
                                        prop.text (Matrix.rowLabel labels r)
                                    ]
                                    for c in 0 .. cols - 1 do
                                        let ck = Matrix.cellKey field.Key r c
                                        let cellValue = Map.tryFind ck values |> Option.defaultValue ""

                                        Html.td [
                                            prop.key (string c)
                                            prop.style [ style.padding (length.em 0.35); style.textAlign.center ]
                                            prop.children [ renderInput cellField cellValue (updateField ck) ]
                                        ]
                                ]
                            ]
                    ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let FormRenderer (schema: FormSchema) (onSubmit: Map<string, FieldValue> -> unit) =
    // One transient string per field — local React state, not Elmish.
    let values, setValues = React.useState (Map.empty: Map<string, string>)

    let updateField (key: string) (raw: string) = setValues (Map.add key raw values)

    let buildSubmission () : Map<string, FieldValue> =
        schema.Fields
        |> List.collect (fun field ->
            match field.Kind with
            // Phase 21a — expand a matrix into one entry per cell under
            // the `{key}[r,c]` sub-key, each carrying the cell's typed
            // value. Empty/absent cells are dropped exactly as flat
            // fields are (server enforces `Required`).
            | MatrixField(rows, cols, cell, _) ->
                Matrix.coords rows cols
                |> List.choose (fun (r, c) ->
                    let ck = Matrix.cellKey field.Key r c
                    let raw = Map.tryFind ck values |> Option.defaultValue ""

                    match stringToFieldValue cell raw with
                    | Some v -> Some(ck, v)
                    | None -> None)
            | _ ->
                let raw = Map.tryFind field.Key values |> Option.defaultValue ""

                match stringToFieldValue field.Kind raw with
                | Some v -> [ (field.Key, v) ]
                | None -> [])
        |> Map.ofList

    Html.form [
        prop.onSubmit (fun ev ->
            ev.preventDefault ()
            onSubmit (buildSubmission ()))
        prop.children [
            for field in schema.Fields do
                let value = Map.tryFind field.Key values |> Option.defaultValue ""

                Html.div [
                    prop.style [ style.marginBottom (length.em 0.75) ]
                    prop.children [
                        Html.label [
                            prop.style [ style.display.block; style.fontWeight 500 ]
                            prop.children [
                                Html.text field.DisplayName
                                if field.Required then
                                    Html.span [ prop.style [ style.color "red" ]; prop.text " *" ]
                            ]
                        ]
                        match field.Description with
                        | Some d ->
                            Html.div [
                                prop.style [ style.fontSize (length.em 0.85); style.color "#6b7280" ]
                                prop.text d
                            ]
                        | None -> ()

                        match field.Kind with
                        | MatrixField(rows, cols, cell, labels) ->
                            renderMatrix field rows cols cell labels values updateField
                        | _ -> renderInput field value (updateField field.Key)
                    ]
                ]

            Html.button [
                prop.type'.submit
                prop.text "Submit"
                prop.style [ style.marginTop (length.em 0.5) ]
            ]
        ]
    ]
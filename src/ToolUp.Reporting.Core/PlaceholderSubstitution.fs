module ToolUp.Reporting.PlaceholderSubstitution

open System
open System.Globalization

// ─── Shared placeholder-substitution machinery ───────────────────────
//
// Renderers that work with text-shaped templates (`Markdown`, `Html`,
// and the textual content inside `Docx` / `Xlsx` / `Pptx` cells)
// share the same placeholder syntax: `{{key}}` for scalars,
// `{{#each items}} … {{/each}}` for tables. This module captures the
// substitution + validation logic so each renderer implements only
// its format-specific encoding (HTML escaping, etc.).
//
// Format-specific renderers in sub-companions (Pdf, Docx, Xlsx, Pptx)
// reuse the validation helpers but typically apply substitution
// against the vendor's DOM rather than raw text.

/// Validate that every required placeholder declared in the schema
/// has a value supplied, and that every supplied value's case
/// matches its schema kind. Returns the first error encountered or
/// `Ok ()` when validation passes.
let validate (schema: PlaceholderSchema list) (values: Map<string, PlaceholderValue>) : Result<unit, RenderError> =
    let kindString =
        function
        | Text -> "Text"
        | Number _ -> "Number"
        | Date _ -> "Date"
        | Image _ -> "Image"
        | Table _ -> "Table"

    let valueKindString =
        function
        | TextValue _ -> "Text"
        | NumberValue _ -> "Number"
        | DateValue _ -> "Date"
        | ImageValue _ -> "Image"
        | TableValue _ -> "Table"
        // Narrative values are resolved (disclosure-filtered + projected
        // to text) by the report API handler before any renderer runs;
        // one reaching validation directly is a kind mismatch by design.
        | NarrativeValue _ -> "Narrative"

    let kindsAgree (kind: PlaceholderKind) (value: PlaceholderValue) =
        match kind, value with
        | Text, TextValue _ -> true
        | Number _, NumberValue _ -> true
        | Date _, DateValue _ -> true
        | Image _, ImageValue _ -> true
        | Table _, TableValue _ -> true
        | _ -> false

    let rec walk (schema: PlaceholderSchema list) =
        match schema with
        | [] -> Ok()
        | entry :: rest ->
            match values.TryFind entry.Key with
            | None when entry.Required -> Error(MissingRequiredPlaceholder entry.Key)
            | None -> walk rest
            | Some value when not (kindsAgree entry.Kind value) ->
                Error(PlaceholderTypeMismatch(entry.Key, kindString entry.Kind, valueKindString value))
            | Some _ -> walk rest

    walk schema

/// Render a `PlaceholderValue` to a plain string using the schema's
/// formatting hint. Ignores `Image` and `Table` — those need
/// renderer-specific handling.
let renderScalar (kind: PlaceholderKind) (value: PlaceholderValue) : string =
    match kind, value with
    | Text, TextValue s -> s
    | Number formatHint, NumberValue n ->
        match formatHint with
        | Some fmt -> n.ToString(fmt, CultureInfo.InvariantCulture)
        | None -> n.ToString(CultureInfo.InvariantCulture)
    | Date formatHint, DateValue d ->
        match formatHint with
        | Some fmt -> d.ToString(fmt, CultureInfo.InvariantCulture)
        | None -> d.ToString("o", CultureInfo.InvariantCulture)
    | _ -> ""

/// Apply `{{key}}` substitution against a UTF-8 text body, with each
/// placeholder rendered via the supplied per-key render function.
/// Unknown `{{key}}` tokens (not in schema) pass through unchanged —
/// caller decided whether validation has run upstream.
let substituteText (renderValue: string -> string) (body: string) : string =
    // Regex-free scan to keep the DLL Fable-clean and avoid regex
    // engine variance. Recognises `{{key}}` where key is
    // alphanumeric + `_` + `.`. Unmatched `{{` passes through.
    let sb = System.Text.StringBuilder()
    let mutable i = 0
    let len = body.Length

    while i < len do
        if i + 1 < len && body[i] = '{' && body[i + 1] = '{' then
            // Find closing `}}`
            let close = body.IndexOf("}}", i + 2)

            if close = -1 then
                sb.Append body[i] |> ignore
                i <- i + 1
            else
                let key = body.Substring(i + 2, close - (i + 2)).Trim()

                let isKeyChar (c: char) =
                    Char.IsLetterOrDigit c || c = '_' || c = '.' || c = ' '

                if key |> Seq.forall isKeyChar then
                    sb.Append(renderValue (key.Trim())) |> ignore
                    i <- close + 2
                else
                    sb.Append body[i] |> ignore
                    i <- i + 1
        else
            sb.Append body[i] |> ignore
            i <- i + 1

    sb.ToString()
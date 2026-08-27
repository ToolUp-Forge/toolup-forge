// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Docx.DocxReportRenderer

open ToolUp.OpenXml
open ToolUp.Platform.Narrative
open ToolUp.Reporting
open ToolUp.Reporting.PlaceholderSubstitution

// ─── DOCX renderer (Phase 23 sub-companion) ──────────────────────────
//
// Fills `.docx` templates through ToolUp.OpenXml's structural model:
// `Import` → `{{key}}` substitution over runs → `Emit`. Working at the
// model altitude (rather than string-replacing inside XML) is what
// preserves styles, numbering, tables, comments and opaque parts, and
// keeps the door open to emitting fills as tracked changes later.
//
// Substitution semantics:
//   * Scalars substitute inside run text via the shared
//     `PlaceholderSubstitution` machinery — same syntax and format-hint
//     behaviour as every other renderer.
//   * Adjacent runs with identical formatting are coalesced before
//     substitution, so a token Word has split across runs (its editing
//     history does this routinely) still matches. A token split across
//     runs with DIFFERENT formatting is left as authored — the template
//     author's formatting boundary is honoured over the token.
//   * A `Table`-kind placeholder whose token is the entire paragraph
//     renders as a native Word table (bold header row + data rows).
//     Inline table tokens render as tab-separated text lines.
//   * `Image`-kind values render as a bracketed marker — the structural
//     model does not carry image parts (they round-trip as residue);
//     mirrors MarkdownRenderer's posture.
//   * A `NarrativeValue` placeholder whose token is the entire paragraph
//     expands through `NarrativeOoxml` at that anchor — the narrative's
//     headings, lists, tables and callouts become native Word structures
//     rather than flattened text. In an inline position (a token inside a
//     sentence) there is no anchor to expand into, so the value takes the
//     plaintext projection.
//   * Unknown `{{key}}` tokens pass through unchanged so authors can
//     spot template/schema drift.

let private name = "DocxReportRenderer"

/// Coalesce adjacent runs whose formatting and revision state are
/// identical, so tokens split mid-key by editing history re-join
/// before substitution.
let private coalesceRuns (runs: Run list) : Run list =
    let folder (acc: Run list) (run: Run) =
        match acc with
        | prev :: rest when prev.Formatting = run.Formatting && prev.Revision = run.Revision ->
            {
                prev with
                    Text = prev.Text + run.Text
            }
            :: rest
        | _ -> run :: acc

    runs |> List.fold folder [] |> List.rev

/// The paragraph's text when it consists of exactly one `{{key}}`
/// token (surrounding whitespace tolerated) — the shape that promotes
/// a Table-kind placeholder to a native table block.
let private wholeParagraphToken (paragraph: ParagraphModel) : string option =
    let text = (ParagraphModel.text paragraph).Trim()

    if
        text.StartsWith "{{"
        && text.EndsWith "}}"
        && text.IndexOf("}}", 2) = text.Length - 2
    then
        Some(text.Substring(2, text.Length - 4).Trim())
    else
        None

let private nativeTable (columns: ColumnSchema list) (rows: Map<string, PlaceholderValue> list) : TableModel =
    let cell (runs: Run list) : TableCell = {
        Blocks = [ Block.Paragraph(ParagraphModel.create runs) ]
        RawProperties = None
    }

    let headerRow = {
        Cells =
            columns
            |> List.map (fun c ->
                cell [
                    {
                        Run.plain c.DisplayName with
                            Formatting = { RunFormatting.none with Bold = true }
                    }
                ])
        RawProperties = None
    }

    let dataRow (row: Map<string, PlaceholderValue>) = {
        Cells =
            columns
            |> List.map (fun c ->
                let text =
                    row.TryFind c.Key |> Option.map (renderScalar c.Kind) |> Option.defaultValue ""

                cell [ Run.plain text ])
        RawProperties = None
    }

    {
        Rows = headerRow :: (rows |> List.map dataRow)
        RawProperties = None
        RawGrid = None
    }

/// Compact text fallback for a Table value in an inline position:
/// cells tab-joined, rows newline-joined (the model normalises `'\t'`
/// / `'\n'` back to `w:tab` / `w:br` at emission).
let private tableAsText (columns: ColumnSchema list) (rows: Map<string, PlaceholderValue> list) : string =
    let header = columns |> List.map _.DisplayName |> String.concat "\t"

    let dataLines =
        rows
        |> List.map (fun row ->
            columns
            |> List.map (fun c -> row.TryFind c.Key |> Option.map (renderScalar c.Kind) |> Option.defaultValue "")
            |> String.concat "\t")

    header :: dataLines |> String.concat "\n"

/// Narrative placeholders are validated as their anchor's declared kind
/// (`Text`) rather than as a case `PlaceholderSubstitution.validate`
/// knows, so a genuinely mismatched slot — a narrative supplied for a
/// `Table`-kind placeholder — still fails, while the narrative channel
/// this renderer expands structurally does not.
let private validationView (values: Map<string, PlaceholderValue>) : Map<string, PlaceholderValue> =
    values
    |> Map.map (fun _ value ->
        match value with
        | NarrativeValue _ -> TextValue ""
        | other -> other)

let private narrativeKeys (values: Map<string, PlaceholderValue>) =
    values
    |> Map.filter (fun _ value ->
        match value with
        | NarrativeValue _ -> true
        | _ -> false)

/// Build a renderer that resolves narrative `Component` blocks through
/// the supplied registry. The registry is a composition-root concern —
/// Reporting never names a rendering companion (GP 1) — so a deployment
/// that has one registers it here and one that has none composes
/// `create`, whose components take their data-table degradation.
let createWith (componentRenderers: NarrativeOoxml.ComponentRenderers) : IReportRenderer =
    { new IReportRenderer with
        member _.SupportedFormats = [ Docx ]
        member _.Name = name

        member _.Render(template, values) = async {
            let narratives = narrativeKeys values

            match validate template.Placeholders (validationView values) with
            | Error e -> return Error e
            | Ok() ->
                try
                    let imported = Import.fromBytes template.Body
                    let bulletId, orderedId = NarrativeOoxml.freeNumberingIds imported.Model.Numbering

                    let projection = {
                        NarrativeOoxml.ProjectionOptions.Default with
                            ComponentRenderers = componentRenderers
                            BulletNumberingId = bulletId
                            OrderedNumberingId = orderedId
                    }

                    let renderKey (key: string) =
                        match template.Placeholders |> List.tryFind (fun p -> p.Key = key), values.TryFind key with
                        | Some schema, Some value ->
                            match schema.Kind, value with
                            | Image _, _ -> $"[image: {key} (not supported by {name})]"
                            | Table cols, TableValue rows -> tableAsText cols rows
                            // A narrative token sitting inside a sentence has
                            // no anchor paragraph to expand into, so it takes
                            // the plaintext projection — the same degradation
                            // the format-free renderers apply.
                            | _, NarrativeValue document -> NarrativePlaintext.render document
                            | _ -> renderScalar schema.Kind value
                        | _ -> $"{{{{{key}}}}}"

                    // A whole-paragraph token bound to a Table-kind value
                    // promotes to a native table block.
                    let tryTableFor (key: string) : TableModel option =
                        match template.Placeholders |> List.tryFind (fun p -> p.Key = key), values.TryFind key with
                        | Some { Kind = Table cols }, Some(TableValue rows) -> Some(nativeTable cols rows)
                        | _ -> None

                    // A whole-paragraph token bound to a narrative expands
                    // into that narrative's own blocks at the anchor.
                    let tryNarrativeFor (key: string) : NarrativeDocument option =
                        match template.Placeholders |> List.tryFind (fun p -> p.Key = key), narratives.TryFind key with
                        | Some _, Some(NarrativeValue document) -> Some document
                        | _ -> None

                    let substituteParagraph (paragraph: ParagraphModel) = {
                        paragraph with
                            Runs =
                                paragraph.Runs
                                |> coalesceRuns
                                |> List.map (fun run -> {
                                    run with
                                        Text = substituteText renderKey run.Text
                                })
                    }

                    let rec substituteBlock (block: Block) : Block list =
                        match block with
                        | Block.Paragraph p ->
                            match wholeParagraphToken p with
                            | Some key ->
                                match tryTableFor key with
                                | Some table -> [ Block.Table table ]
                                | None ->
                                    match tryNarrativeFor key with
                                    | Some document -> NarrativeOoxml.projectWith projection document
                                    | None -> [ Block.Paragraph(substituteParagraph p) ]
                            | None -> [ Block.Paragraph(substituteParagraph p) ]
                        | Block.Heading(level, p) -> [ Block.Heading(level, substituteParagraph p) ]
                        | Block.ListItem(numbering, p) -> [ Block.ListItem(numbering, substituteParagraph p) ]
                        | Block.Table t -> [
                            Block.Table {
                                t with
                                    Rows =
                                        t.Rows
                                        |> List.map (fun row -> {
                                            row with
                                                Cells =
                                                    row.Cells
                                                    |> List.map (fun c -> {
                                                        c with
                                                            Blocks = c.Blocks |> List.collect substituteBlock
                                                    })
                                        })
                            }
                          ]
                        // A figure carries a byte payload, not runs, so
                        // there is no placeholder token in it to
                        // substitute — it passes through untouched, as
                        // an opaque block does.
                        | Block.Figure _
                        | OpaqueBlock _ -> [ block ]

                    let substituted = {
                        imported.Model with
                            Sections =
                                imported.Model.Sections
                                |> List.map (fun s -> {
                                    s with
                                        Blocks = s.Blocks |> List.collect substituteBlock
                                })
                    }

                    // Only a render that carried a narrative can have minted
                    // list numbering the template does not declare, so a
                    // narrative-free render is left exactly as it was (GP 11).
                    let model =
                        if Map.isEmpty narratives then
                            substituted
                        else
                            NarrativeOoxml.ensureListNumbering projection substituted

                    return Ok(Emit.toBytesWith imported.CustomParts model)
                with ex ->
                    return Error(RendererFailure(name, $"template could not be processed as .docx: {ex.Message}"))
        }
    }

/// The renderer with no component registry: every `Component` block in a
/// projected narrative takes its data-table degradation.
let create () : IReportRenderer =
    createWith (fun _ _ -> NarrativeOoxml.Fallback)
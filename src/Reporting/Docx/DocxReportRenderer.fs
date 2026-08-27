// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Docx.DocxReportRenderer

open ToolUp.OpenXml
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
        Blocks = [ Paragraph(ParagraphModel.create runs) ]
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

let create () : IReportRenderer =
    { new IReportRenderer with
        member _.SupportedFormats = [ Docx ]
        member _.Name = name

        member _.Render(template, values) = async {
            match validate template.Placeholders values with
            | Error e -> return Error e
            | Ok() ->
                try
                    let imported = Import.fromBytes template.Body

                    let renderKey (key: string) =
                        match template.Placeholders |> List.tryFind (fun p -> p.Key = key), values.TryFind key with
                        | Some schema, Some value ->
                            match schema.Kind, value with
                            | Image _, _ -> $"[image: {key} (not supported by {name})]"
                            | Table cols, TableValue rows -> tableAsText cols rows
                            | _ -> renderScalar schema.Kind value
                        | _ -> $"{{{{{key}}}}}"

                    // A whole-paragraph token bound to a Table-kind value
                    // promotes to a native table block.
                    let tryTableFor (key: string) : TableModel option =
                        match template.Placeholders |> List.tryFind (fun p -> p.Key = key), values.TryFind key with
                        | Some { Kind = Table cols }, Some(TableValue rows) -> Some(nativeTable cols rows)
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

                    let rec substituteBlock (block: Block) : Block =
                        match block with
                        | Paragraph p ->
                            match wholeParagraphToken p |> Option.bind tryTableFor with
                            | Some table -> Block.Table table
                            | None -> Paragraph(substituteParagraph p)
                        | Heading(level, p) -> Heading(level, substituteParagraph p)
                        | ListItem(numbering, p) -> ListItem(numbering, substituteParagraph p)
                        | Block.Table t ->
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
                                                            Blocks = c.Blocks |> List.map substituteBlock
                                                    })
                                        })
                            }
                        | OpaqueBlock _ -> block

                    let model = {
                        imported.Model with
                            Sections =
                                imported.Model.Sections
                                |> List.map (fun s -> {
                                    s with
                                        Blocks = s.Blocks |> List.map substituteBlock
                                })
                    }

                    return Ok(Emit.toBytesWith imported.CustomParts model)
                with ex ->
                    return Error(RendererFailure(name, $"template could not be processed as .docx: {ex.Message}"))
        }
    }
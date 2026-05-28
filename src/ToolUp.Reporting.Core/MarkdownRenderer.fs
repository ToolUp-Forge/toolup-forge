module ToolUp.Reporting.MarkdownRenderer

open System.Text
open ToolUp.Reporting.PlaceholderSubstitution

// ─── Markdown renderer ───────────────────────────────────────────────
//
// Pure F#, zero deps. Treats the template body as UTF-8 markdown with
// `{{key}}` placeholders for scalars. Tables are rendered as GitHub-
// flavoured markdown tables — header row | separator | data rows.
//
// Image placeholders not supported (the markdown layer can't embed
// arbitrary bytes; consumers needing inline images should use
// HtmlRenderer with base64 data URLs, or the DOCX/PDF sub-companions).

let private name = "MarkdownRenderer"

let private renderTable (columns: ColumnSchema list) (rows: Map<string, PlaceholderValue> list) =
    let sb = StringBuilder()
    // Header
    sb.Append "| " |> ignore
    sb.AppendJoin(" | ", columns |> List.map _.DisplayName) |> ignore
    sb.AppendLine " |" |> ignore
    // Separator
    sb.Append "| " |> ignore
    sb.AppendJoin(" | ", columns |> List.map (fun _ -> "---")) |> ignore
    sb.AppendLine " |" |> ignore
    // Data rows
    for row in rows do
        sb.Append "| " |> ignore

        let cells =
            columns
            |> List.map (fun col ->
                match row.TryFind col.Key with
                | Some value -> renderScalar col.Kind value
                | None -> "")

        sb.AppendJoin(" | ", cells) |> ignore
        sb.AppendLine " |" |> ignore

    sb.ToString()

let create () : IReportRenderer =
    { new IReportRenderer with
        member _.SupportedFormats = [ Markdown ]
        member _.Name = name

        member _.Render(template, values) = async {
            match validate template.Placeholders values with
            | Error e -> return Error e
            | Ok() ->
                let body = Encoding.UTF8.GetString template.Body

                let renderKey (key: string) =
                    match template.Placeholders |> List.tryFind (fun p -> p.Key = key), values.TryFind key with
                    | Some schema, Some value ->
                        match schema.Kind, value with
                        | Image _, _ ->
                            // Image kind unsupported by Markdown — emit a
                            // placeholder rather than failing the whole
                            // render. The validation pass already accepted
                            // the value as kind-compatible; this is a per-
                            // renderer support gap, not a contract violation.
                            $"![image: {key} (not supported by MarkdownRenderer)]"
                        | Table cols, TableValue rows -> "\n" + renderTable cols rows
                        | _ -> renderScalar schema.Kind value
                    | _ ->
                        // Unknown key — pass through the original token
                        // so authors can spot template/schema drift.
                        $"{{{{{key}}}}}"

                let rendered = substituteText renderKey body
                return Ok(Encoding.UTF8.GetBytes rendered)
        }
    }
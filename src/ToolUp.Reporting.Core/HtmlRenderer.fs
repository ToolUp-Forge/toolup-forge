module ToolUp.Reporting.HtmlRenderer

open System.Net
open System.Text
open ToolUp.Reporting.PlaceholderSubstitution

// ─── HTML renderer ───────────────────────────────────────────────────
//
// Pure F# + `System.Web.HttpUtility.HtmlEncode` (BCL since .NET Core).
// Treats the template body as HTML with `{{key}}` placeholders. Every
// substituted value is HTML-encoded by default — to inject raw HTML
// (e.g. a pre-rendered chart SVG), pass the value through a `Text`
// placeholder whose key is suffixed `_raw` (encoded values use the
// bare key; raw values use the `_raw`-suffixed key).
//
// Image placeholders emit a base64 data URL inside an `<img>` tag.
// Table placeholders render a styled `<table>` with header / body
// rows, no class attributes — consumers style via the surrounding
// page's CSS.

let private name = "HtmlRenderer"

let private escape (s: string) = WebUtility.HtmlEncode s

let private renderTable (columns: ColumnSchema list) (rows: Map<string, PlaceholderValue> list) =
    let sb = StringBuilder()
    sb.Append "<table><thead><tr>" |> ignore

    for col in columns do
        sb.Append $"<th>{escape col.DisplayName}</th>" |> ignore

    sb.Append "</tr></thead><tbody>" |> ignore

    for row in rows do
        sb.Append "<tr>" |> ignore

        for col in columns do
            let cell =
                match row.TryFind col.Key with
                | Some value -> escape (renderScalar col.Kind value)
                | None -> ""

            sb.Append $"<td>{cell}</td>" |> ignore

        sb.Append "</tr>" |> ignore

    sb.Append "</tbody></table>" |> ignore
    sb.ToString()

let private renderImage (mimeType: string) (bytes: byte[]) =
    let b64 = System.Convert.ToBase64String bytes
    $"<img src=\"data:{escape mimeType};base64,{b64}\" />"

let create () : IReportRenderer =
    { new IReportRenderer with
        member _.SupportedFormats = [ Html ]
        member _.Name = name

        member _.Render(template, values) = async {
            match validate template.Placeholders values with
            | Error e -> return Error e
            | Ok() ->
                let body = System.Text.Encoding.UTF8.GetString template.Body

                let renderKey (key: string) =
                    // Allow `{{key_raw}}` to inject already-trusted HTML
                    // (chart SVG, pre-rendered fragment). Schema entry's
                    // Kind = Text in both cases; the suffix is a per-
                    // value escape-bypass marker.
                    let isRaw = key.EndsWith "_raw"
                    let lookupKey = if isRaw then key.Substring(0, key.Length - 4) else key

                    match
                        template.Placeholders |> List.tryFind (fun p -> p.Key = lookupKey), values.TryFind lookupKey
                    with
                    | Some schema, Some value ->
                        match schema.Kind, value with
                        | Image mt, ImageValue(bytes, _) -> renderImage mt bytes
                        | Table cols, TableValue rows -> renderTable cols rows
                        | _ ->
                            let text = renderScalar schema.Kind value
                            if isRaw then text else escape text
                    | _ -> $"{{{{{key}}}}}"

                let rendered = substituteText renderKey body
                return Ok(Encoding.UTF8.GetBytes rendered)
        }
    }
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Atom 1.0 renderer. `renderEntry` produces one `<entry>` element for
/// a `NarrativeDocument`; `renderFeed` wraps a sequence of documents
/// in a complete `<feed>` document.
///
/// Use for content sites that publish RSS-style aggregated views over
/// Narrative-bodied pages, or for any deployment that wants to expose
/// `application/atom+xml` from the renderer registry. The output is
/// XML, not HTML — escaping rules differ from the HTML renderer
/// (single quotes and ampersands matter; `<` / `>` still matter).
module ToolUp.Platform.Narrative.NarrativeAtom

open System
open System.Text

let private xmlEscape (s: string) : string =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;")

let private summaryText (doc: NarrativeDocument) : string =
    // Atom's `<summary>` is plaintext; reuse the plaintext renderer so
    // the summary degrades through the same path RSS readers would
    // anyway. Cap at 512 chars — Atom doesn't constrain length, but
    // readers vary wildly on display length.
    let plain = NarrativePlaintext.render doc

    if plain.Length <= 512 then
        plain
    else
        plain.Substring(0, 509) + "…"

let private entryId (doc: NarrativeDocument) : string =
    match doc.CanonicalUrl with
    | Some url -> url
    | None ->
        match doc.Provenance with
        | Some prov -> sprintf "urn:toolup-narrative:%s:%s" (xmlEscape prov.ModuleId) (xmlEscape prov.SettingsKey)
        | None ->
            // Fallback — non-deterministic, but Atom's `<id>` is
            // required. Title + a hash of the section count keeps it
            // stable across re-renders of the same document.
            sprintf "urn:toolup-narrative:%s:s%d" (xmlEscape doc.Title) (List.length doc.Sections)

let private updatedTimestamp (doc: NarrativeDocument) : string =
    match doc.Provenance with
    | Some prov -> prov.GeneratedAt.ToString("o")
    | None ->
        // No provenance — emit the unix epoch as a stable, non-
        // misleading "we don't know when this was updated" marker.
        DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("o")

/// Render a single Atom `<entry>` for the document. Embed multiple
/// entries inside a `<feed>` either via `renderFeed` (for a complete
/// document) or by hand (when wrapping inside an existing feed shell).
let renderEntry (doc: NarrativeDocument) : string =
    let sb = StringBuilder()
    sb.AppendLine("<entry>") |> ignore

    sb.AppendFormat("  <title>{0}</title>", xmlEscape doc.Title).AppendLine()
    |> ignore

    sb.AppendFormat("  <id>{0}</id>", xmlEscape (entryId doc)).AppendLine()
    |> ignore

    sb.AppendFormat("  <updated>{0}</updated>", updatedTimestamp doc).AppendLine()
    |> ignore

    match doc.CanonicalUrl with
    | Some url ->
        sb.AppendFormat("  <link rel=\"alternate\" type=\"text/html\" href=\"{0}\" />", xmlEscape url).AppendLine()
        |> ignore
    | None -> ()

    match doc.Subtitle with
    | Some sub ->
        sb.AppendFormat("  <summary>{0}</summary>", xmlEscape sub).AppendLine()
        |> ignore
    | None ->
        sb.AppendFormat("  <summary>{0}</summary>", xmlEscape (summaryText doc)).AppendLine()
        |> ignore

    match doc.Provenance with
    | Some prov ->
        sb.AppendLine("  <author>") |> ignore

        sb.AppendFormat("    <name>{0}</name>", xmlEscape prov.ModuleId).AppendLine()
        |> ignore

        sb.AppendLine("  </author>") |> ignore
    | None -> ()

    sb.AppendLine("  <content type=\"html\">") |> ignore
    sb.Append("    ").AppendLine(xmlEscape (NarrativeHtml.render doc)) |> ignore
    sb.AppendLine("  </content>") |> ignore
    sb.AppendLine("</entry>") |> ignore
    sb.ToString().TrimEnd()

/// Render a complete Atom 1.0 `<feed>` containing every supplied
/// document as an `<entry>`. `feedTitle` is the feed's `<title>`,
/// `feedSelfUrl` is the feed's own URL (`<link rel="self">`), and
/// `feedAlternateUrl` is the human-readable site URL
/// (`<link rel="alternate">`). The feed's `<updated>` element is set
/// to the most-recent document's `Provenance.GeneratedAt` (or the
/// current time if every document lacks provenance).
let renderFeed
    (feedTitle: string)
    (feedSelfUrl: string)
    (feedAlternateUrl: string)
    (docs: NarrativeDocument list)
    : string =
    let mostRecent =
        docs
        |> List.choose _.Provenance
        |> List.map _.GeneratedAt
        |> function
            | [] -> DateTimeOffset.UtcNow
            | dates -> List.max dates

    let sb = StringBuilder()
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>") |> ignore
    sb.AppendLine("<feed xmlns=\"http://www.w3.org/2005/Atom\">") |> ignore

    sb.AppendFormat("  <title>{0}</title>", xmlEscape feedTitle).AppendLine()
    |> ignore

    sb.AppendFormat("  <id>{0}</id>", xmlEscape feedSelfUrl).AppendLine() |> ignore

    sb.AppendFormat("  <updated>{0}</updated>", mostRecent.ToString("o")).AppendLine()
    |> ignore

    sb
        .AppendFormat("  <link rel=\"self\" type=\"application/atom+xml\" href=\"{0}\" />", xmlEscape feedSelfUrl)
        .AppendLine()
    |> ignore

    sb
        .AppendFormat("  <link rel=\"alternate\" type=\"text/html\" href=\"{0}\" />", xmlEscape feedAlternateUrl)
        .AppendLine()
    |> ignore

    for doc in docs do
        sb.AppendLine(renderEntry doc) |> ignore

    sb.AppendLine("</feed>") |> ignore
    sb.ToString().TrimEnd()

/// `INarrativeRenderer`-shape entry-only renderer. Use this for a
/// single-document export endpoint that the layout will wrap into a
/// surrounding `<feed>`. For a complete feed, call `renderFeed`
/// directly with the document list — the registry interface is per-
/// document by design.
let render (doc: NarrativeDocument) : string = renderEntry doc
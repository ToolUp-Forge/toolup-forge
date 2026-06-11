// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform.Narrative

// ─── Phase 80a — Content-negotiated export handler ──────────────
//
// Surfaces non-HTML representations of a `PublicPage` at the same URL
// as the HTML page. Triggered by a `?format=` query parameter; absent
// the parameter the handler falls through to the standard page handler
// (which renders the layout's `<html>` tree).
//
// Supported formats (Narrative bodies):
//   ?format=html  → NarrativeHtml.render (article fragment, not the full page)
//   ?format=md    → NarrativeMarkdown.render
//   ?format=txt   → NarrativePlaintext.render
//   ?format=atom  → NarrativeAtom.renderEntry (single Atom entry)
//
// Supported formats (Markdown bodies):
//   ?format=md    → source pass-through
//
// Supported formats (Html bodies):
//   ?format=html  → fragment pass-through
//
// Unsupported combinations return 415 Unsupported Media Type with a
// short JSON body listing the formats available for the page's body
// kind. The handler short-circuits — when `?format=` is present and
// the slug doesn't resolve, returns 404 instead of falling through
// (otherwise the standard page handler would attempt to serve HTML
// for a known-missing route).

module NarrativeExportHandler =
    let private contentType (format: string) =
        match format.ToLowerInvariant() with
        | "html" -> Some "text/html; charset=utf-8"
        | "md"
        | "markdown" -> Some "text/markdown; charset=utf-8"
        | "txt"
        | "plain" -> Some "text/plain; charset=utf-8"
        | "atom" -> Some "application/atom+xml; charset=utf-8"
        | "csv" -> Some "text/csv; charset=utf-8"
        | "json" -> Some "application/json; charset=utf-8"
        | _ -> None

    let private renderNarrative (format: string) (doc: NarrativeDocument) : string option =
        match format.ToLowerInvariant() with
        | "html" -> Some(NarrativeHtml.render doc)
        | "md"
        | "markdown" -> Some(NarrativeMarkdown.render doc)
        | "txt"
        | "plain" -> Some(NarrativePlaintext.render doc)
        | "atom" -> Some(NarrativeAtom.renderEntry doc)
        | _ -> None

    let private supportedFor (body: ContentBody) : string list =
        match body with
        // Phase 101 — csv / json export of the Narrative's Table elements.
        | Narrative _ -> [ "html"; "md"; "txt"; "atom"; "csv"; "json" ]
        | Markdown _ -> [ "md" ]
        | Html _ -> [ "html" ]

    /// Phase 101 — a filesystem-safe download filename stem from a slug
    /// (`services/q4` → `services-q4`; empty → `export`).
    let private filenameStem (slug: string) : string =
        let cleaned = slug.Replace('/', '-').Trim('-')
        if cleaned = "" then "export" else cleaned

    let handler (api: IPublicContentApi) : HttpHandler =
        fun _next (ctx: HttpContext) -> task {
            let format =
                match ctx.Request.Query.TryGetValue "format" with
                | true, values when values.Count > 0 -> Some(values.[0])
                | _ -> None

            match format with
            | None ->
                // No format parameter — fall through to the standard
                // page handler. Returning `None` from a Giraffe handler
                // signals "I don't handle this", and the surrounding
                // `choose` advances to the next branch. Calling
                // `next ctx` here would halt the choose with an empty
                // 200 (the no-op finisher path), shadowing the page
                // handler for every format-less GET.
                return None
            | Some fmt ->
                let rawPath = ctx.Request.Path.Value
                let slug = rawPath.TrimStart('/')
                let slugOrIndex = if slug = "" then "index" else slug
                let! pageOpt = api.GetPage slugOrIndex

                match pageOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    return! ctx.WriteStringAsync(sprintf "No page at '%s'" slugOrIndex)
                | Some page ->
                    let supported = supportedFor page.Body
                    let normalised = fmt.ToLowerInvariant()

                    let matches =
                        supported
                        |> List.exists (fun s ->
                            s = normalised
                            || (s = "md" && normalised = "markdown")
                            || (s = "txt" && normalised = "plain"))

                    if not matches then
                        ctx.Response.StatusCode <- 415
                        ctx.Response.ContentType <- "application/json; charset=utf-8"

                        let supportedList = supported |> List.map (sprintf "\"%s\"") |> String.concat ","

                        return!
                            ctx.WriteStringAsync(
                                sprintf
                                    "{\"error\":\"unsupported format for this body type\",\"requested\":\"%s\",\"supported\":[%s]}"
                                    fmt
                                    supportedList
                            )
                    else
                        match page.Body, fmt.ToLowerInvariant() with
                        | Narrative doc, (("csv" | "json") as tabular) ->
                            // Phase 101 — export the document's Table
                            // element(s) as data. `?table=N` selects which
                            // (default 0); a page with no table degrades to
                            // an empty export (200), not an error.
                            let tables = NarrativeData.tables doc

                            let idx =
                                match ctx.Request.Query.TryGetValue "table" with
                                | true, values when values.Count > 0 ->
                                    match System.Int32.TryParse values.[0] with
                                    | true, n -> n
                                    | _ -> 0
                                | _ -> 0

                            let ext = tabular

                            let payload =
                                match List.tryItem idx tables with
                                | Some t when tabular = "csv" -> NarrativeData.toCsv t
                                | Some t -> NarrativeData.toJson t
                                | None when tabular = "csv" -> ""
                                | None -> "[]"

                            ctx.Response.ContentType <- (contentType tabular |> Option.defaultValue "text/plain")

                            ctx.Response.Headers["Content-Disposition"] <-
                                Microsoft.Extensions.Primitives.StringValues(
                                    sprintf "attachment; filename=\"%s.%s\"" (filenameStem slugOrIndex) ext
                                )

                            return! ctx.WriteStringAsync payload
                        | Narrative doc, _ ->
                            match renderNarrative fmt doc, contentType fmt with
                            | Some payload, Some ct ->
                                ctx.Response.ContentType <- ct
                                return! ctx.WriteStringAsync payload
                            | _ ->
                                ctx.Response.StatusCode <- 500
                                return! ctx.WriteStringAsync "internal renderer mismatch"
                        | Markdown source, ("md" | "markdown") ->
                            ctx.Response.ContentType <- "text/markdown; charset=utf-8"
                            return! ctx.WriteStringAsync source
                        | Html fragment, "html" ->
                            ctx.Response.ContentType <- "text/html; charset=utf-8"
                            return! ctx.WriteStringAsync fragment
                        | _ ->
                            // The `supported` check above should have caught
                            // every mismatch; this branch is the belt over
                            // the braces in case of future ContentBody
                            // variants that the per-body handler hasn't
                            // been updated for.
                            ctx.Response.StatusCode <- 415
                            return! ctx.WriteStringAsync "unsupported"
        }
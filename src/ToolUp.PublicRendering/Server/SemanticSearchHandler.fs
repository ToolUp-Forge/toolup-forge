// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open Giraffe
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline

// ─── Phase 91 — semantic-search SSR endpoint ─────────────────────────
//
// A server-rendered `/search?q=` page hitting `IRetrievalPipeline.Retrieve`
// and rendering a ranked, indexable result list — distinct from the
// in-app AI side panel. It shares the retrieval→Narrative core with
// `RagAnswerSource`: results are extractive (the retrieved chunk content,
// grounded by construction) and each can deep-link into a KB-as-docs page
// (Phase 91 `KnowledgeDocsSource`) via an opt-in per-result link builder.
//
// **Rate limiting (Phase 14y).** The `/search` route is mounted on the
// SDK's route chain, so it rides the deployment's *general* rate-limit
// partition — exactly the posture Phase 14y documents for retrieval
// (retrieval rides the general partition rather than a bespoke limiter).
// The search-specific guard this handler adds is the **query-size cap**
// (`MaxQueryChars`, default 16384, mirroring Phase 14y's `withMaxQueryChars`):
// an over-long query is refused with a `400` and a graceful page rather
// than truncated silently at the provider boundary.
//
// **Scope isolation (GP 4).** Retrieval runs under the request's resolved
// `AccessContext`, so the pipeline's own scope validation applies — a
// caller only ever searches scopes it is authorised for.
//
// **Zero cost when unused (GP 13).** The route is only mounted when the
// deployment calls `withSemanticSearch`; even then, if no
// `IRetrievalPipeline` is composed (no RAG), the handler declines and
// falls through, so `/search` 404s like any unknown slug.

/// Compose-time configuration for the semantic-search SSR page.
type SemanticSearchConfig = {
    /// Vector scopes to query. Validated against the caller's
    /// `AccessContext` by the pipeline (GP 4).
    Scopes: VectorScope list
    /// Number of results to retrieve + render.
    TopK: int
    /// Minimum fused score a match must clear to be shown. Matches below
    /// this are dropped before rendering.
    MinScore: float
    /// Application-layer query-size cap (Phase 14y). A query longer than
    /// this is refused (`400`) rather than truncated at the provider
    /// boundary. Default 16384.
    MaxQueryChars: int
    /// Optional per-result deep-link builder. The deployment maps a match
    /// to a URL (typically a KB-as-docs landing, e.g. via its chunk-id /
    /// `_source` convention — see `SemanticSearch.docsLinkByChunkId`).
    /// `None` → results render cited but without links. Kept as an
    /// injected function so PublicRendering stays free of a KB dependency
    /// (GP 1).
    ResultLink: (VectorMatch -> string option) option
    /// Layout to render the search page under. `None` → first-registered
    /// layout (same fallback as `PublicPageHandler`).
    Layout: LayoutName option
}

module SemanticSearchConfig =
    /// A config querying `scopes` with sensible defaults (TopK 10,
    /// MinScore 0.0, MaxQueryChars 16384, no result links, default
    /// layout). Tune with the `with*` helpers.
    let create (scopes: VectorScope list) : SemanticSearchConfig = {
        Scopes = scopes
        TopK = 10
        MinScore = 0.0
        MaxQueryChars = 16384
        ResultLink = None
        Layout = None
    }

    let withTopK (k: int) (c: SemanticSearchConfig) : SemanticSearchConfig = { c with TopK = k }

    let withMinScore (s: float) (c: SemanticSearchConfig) : SemanticSearchConfig = { c with MinScore = s }

    let withMaxQueryChars (n: int) (c: SemanticSearchConfig) : SemanticSearchConfig = { c with MaxQueryChars = n }

    let withResultLink (f: VectorMatch -> string option) (c: SemanticSearchConfig) : SemanticSearchConfig = {
        c with
            ResultLink = Some f
    }

    let withLayout (name: LayoutName) (c: SemanticSearchConfig) : SemanticSearchConfig = { c with Layout = Some name }

/// Pure helpers for the semantic-search page — the retrieval→Narrative
/// core, testable without an `HttpContext`.
module SemanticSearch =

    /// The decision taken for an incoming `?q=` value, before any
    /// retrieval runs.
    type SearchRequest =
        /// No query supplied — render the search landing / prompt.
        | EmptyQuery
        /// Query exceeds `MaxQueryChars` — refuse (`400`).
        | QueryTooLong of maxChars: int
        /// A well-formed query to run.
        | Search of query: string

    /// Classify a raw `?q=` value against the config's size cap.
    let classify (config: SemanticSearchConfig) (rawQuery: string) : SearchRequest =
        let q = rawQuery.Trim()

        if q = "" then
            EmptyQuery
        elif q.Length > config.MaxQueryChars then
            QueryTooLong config.MaxQueryChars
        else
            Search q

    /// Build the `RetrievalRequest` for a query (interleaved merge across
    /// the configured scopes).
    let retrievalRequest (config: SemanticSearchConfig) (query: string) : RetrievalRequest =
        RetrievalRequest.create query config.Scopes config.TopK Interleaved

    let private snippet (content: string) : string =
        if content.Length <= 240 then
            content
        else
            content.Substring(0, 240).TrimEnd() + "…"

    let private scoreBadge (score: float) : string =
        sprintf "%d%% match" (int (Math.Round(score * 100.0)))

    /// Citation label for a result — its location hint, else its origin
    /// classification, else a generic fallback.
    let private resultLabel (m: VectorMatch) : string =
        match m.Metadata.TryFind ChunkMetadata.LocationHintKey with
        | Some h when h <> "" -> h
        | _ ->
            match m.Metadata.TryFind ChunkMetadata.OriginKey with
            | Some o when o <> "" -> o
            | _ -> "Knowledge base"

    /// Build the results document — a ranked list, each item a cited
    /// snippet (optionally a deep link into a KB-as-docs page). An empty
    /// result set renders a graceful no-results callout, never a bare
    /// page.
    let buildResultsDoc (config: SemanticSearchConfig) (query: string) (matches: VectorMatch list) : NarrativeDocument =
        let title = sprintf "Search: %s" query

        if List.isEmpty matches then
            Narrative.create title
            |> Narrative.subtitle "No results"
            |> Narrative.section "Results" "results" [
                Narrative.callout Severity.Warning [
                    Narrative.text (sprintf "No content in the knowledge base matched “%s”." query)
                ]
            ]
            |> Narrative.withProvenance "semantic-search" None "search-results" []
        else
            let items =
                matches
                |> List.map (fun m ->
                    let label = resultLabel m + " · " + scoreBadge m.Score

                    let head =
                        match config.ResultLink |> Option.bind (fun f -> f m) with
                        | Some href when href <> "" -> Narrative.link href [ Narrative.text label ]
                        | _ -> Narrative.strong label

                    [ head; Narrative.text (" — " + snippet m.Content) ])

            Narrative.create title
            |> Narrative.subtitle (sprintf "%d result(s)" (List.length matches))
            |> Narrative.section "Results" "results" [ Narrative.bullets items ]
            |> Narrative.withProvenance "semantic-search" None "search-results" []

    /// The search landing shown when no query is supplied.
    let emptyQueryDoc: NarrativeDocument =
        Narrative.create "Search"
        |> Narrative.subtitle "Search the knowledge base"
        |> Narrative.section "Search" "search" [
            Narrative.paragraph [ Narrative.text "Enter a query (the ?q= parameter) to search." ]
        ]
        |> Narrative.withProvenance "semantic-search" None "search-empty" []

    /// The refusal page rendered (with a `400`) for an over-long query.
    let queryTooLongDoc (maxChars: int) : NarrativeDocument =
        Narrative.create "Search"
        |> Narrative.subtitle "Query too long"
        |> Narrative.section "Query too long" "too-long" [
            Narrative.callout Severity.Warning [
                Narrative.text (sprintf "The search query exceeds the %d-character limit. Please shorten it." maxChars)
            ]
        ]
        |> Narrative.withProvenance "semantic-search" None "search-too-long" []

    /// A ready-made `ResultLink` builder for the common case: deep-link a
    /// match into a `KnowledgeDocsSource` page using the KB chunk-id
    /// convention `"{docId}:chunk:{i}"` (the shape `KnowledgeApi` uploads
    /// write). Returns `/<docsPrefix>/<docId>` when the chunk id carries a
    /// `":chunk:"` marker; `None` otherwise. Opt-in — a deployment whose
    /// chunk ids differ supplies its own `withResultLink`.
    let docsLinkByChunkId (docsPrefix: string) : VectorMatch -> string option =
        let prefix = docsPrefix.Trim('/')

        fun (m: VectorMatch) ->
            let marker = ":chunk:"
            let idx = m.ChunkId.IndexOf(marker, StringComparison.Ordinal)

            if idx > 0 then
                Some("/" + prefix + "/" + m.ChunkId.Substring(0, idx))
            else
                None

module SemanticSearchHandler =

    /// Layout resolution mirrors `PublicPageHandler.resolveLayout`: try the
    /// configured layout name; fall back to the first-registered layout.
    let private resolveLayout
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (name: LayoutName option)
        : (PublicPage -> XmlNode) option =
        let chosen = name |> Option.bind (fun n -> Map.tryFind n layouts)

        match chosen with
        | Some f -> Some f
        | None -> layouts |> Map.toSeq |> Seq.tryHead |> Option.map snd

    /// Wrap a Narrative body in a `PublicPage` and render it through the
    /// resolved layout. Returns `None` (→ 500 written by caller) when no
    /// layout is registered at all.
    let private renderDoc
        (config: SemanticSearchConfig)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (doc: NarrativeDocument)
        : string option =
        let page: PublicPage = {
            Slug = Slug "search"
            Title = doc.Title
            Description = doc.Subtitle |> Option.defaultValue ""
            Body = ContentBody.Narrative doc
            Layout = config.Layout |> Option.defaultValue (LayoutName "")
            Frontmatter = Map.empty
            PublishedAt = None
            Collection = None
            Status = Published
            // The search page is a public surface; results are already
            // scope-filtered by retrieval (GP 4).
            Audience = PageAudience.Public
        }

        resolveLayout layouts config.Layout
        |> Option.map (fun layout -> layout page |> RenderView.AsString.htmlDocument)

    let private writeHtml
        (ctx: HttpContext)
        (statusCode: int)
        (html: string)
        : System.Threading.Tasks.Task<HttpContext option> =
        ctx.Response.StatusCode <- statusCode
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        // A search results page is request-specific (per query / per
        // principal) — never shared-cached.
        ctx.Response.Headers["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-store"
        ctx.WriteStringAsync html

    let private writeOrNoLayout
        (ctx: HttpContext)
        (statusCode: int)
        (rendered: string option)
        : System.Threading.Tasks.Task<HttpContext option> =
        match rendered with
        | Some html -> writeHtml ctx statusCode html
        | None ->
            ctx.Response.StatusCode <- 500
            ctx.WriteStringAsync "PublicRendering: no layout registered"

    /// The `/search` handler core (no route binding — the compose root
    /// wraps it with `route "/search"`). Resolves the per-request
    /// `AccessContext` from DI, classifies the query against the size cap,
    /// runs retrieval under the caller's scope, and renders the ranked
    /// result list.
    let handle
        (config: SemanticSearchConfig)
        (pipeline: IRetrievalPipeline)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        : HttpHandler =
        fun _next (ctx: HttpContext) -> task {
            let rawQuery = ctx.TryGetQueryStringValue "q" |> Option.defaultValue ""

            let accessContext =
                match ctx.RequestServices.GetService(typeof<AccessContext>) with
                | :? AccessContext as ac -> ac
                | _ -> AccessContext.unrestricted (AnonymousSession "anonymous")

            match SemanticSearch.classify config rawQuery with
            | SemanticSearch.EmptyQuery ->
                return! writeOrNoLayout ctx 200 (renderDoc config layouts SemanticSearch.emptyQueryDoc)
            | SemanticSearch.QueryTooLong maxChars ->
                return! writeOrNoLayout ctx 400 (renderDoc config layouts (SemanticSearch.queryTooLongDoc maxChars))
            | SemanticSearch.Search query ->
                let! matches = pipeline.Retrieve (SemanticSearch.retrievalRequest config query) accessContext
                let ranked = matches |> List.filter (fun m -> m.Score >= config.MinScore)
                let doc = SemanticSearch.buildResultsDoc config query ranked
                return! writeOrNoLayout ctx 200 (renderDoc config layouts doc)
        }
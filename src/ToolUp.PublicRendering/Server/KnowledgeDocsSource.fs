// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open ToolUp.Platform
open ToolUp.Platform.Narrative

// ─── Phase 91 — KB-as-docs SSR surface ───────────────────────────────
//
// `KnowledgeDocsSource` renders a knowledge-document collection as
// browsable, SEO-indexable SSR pages: a collection *index* listing every
// document, and a per-document *landing* whose body is the document's
// extraction-derived structure (per page / slide / section). Like
// `RagAnswerSource` it is an ordinary `IContentSource` (Phase 83), so it
// composes via `withContentSource` and caches through the Phase 84 render
// cache. It additionally implements `IEnumerableContentSource` (Phase 95)
// so the document pages surface in `sitemap.xml` and the static export.
//
// **Dependency isolation (GP 1).** PublicRendering depends only on
// `ToolUp.Platform.{Core,Server}` — never on the `ToolUp.KnowledgeBase`
// companion. So the document data arrives through a small read-port the
// deployment fills from its own KB (the same shape `RagSynthesisHook`
// uses to reach the deployment's `IAIProvider` without PublicRendering
// taking a provider dependency). The deployment maps its KB
// `KnowledgeDocument` + extracted chunks into the PublicRendering-local
// projections below; the KB's `SourceLocation` / `SourceReference`
// location hint (`"Page 4"`, `"Slide 12"`) is surfaced as a plain string
// and turned into a section deep-link anchor here.
//
// **Scope isolation (GP 4).** The read-port receives the request's
// resolved `AccessContext`, so the deployment returns only documents the
// caller is authorised to read. Route enumeration (sitemap / export) runs
// the port under an anonymous context, so a deployment that gates some
// documents simply doesn't surface them to crawlers.

/// A document's headline facts, for the collection index.
type KnowledgeDocSummary = {
    /// Stable document id — the slug tail under the source's prefix.
    Id: string
    Title: string
    Description: string option
    /// Optional collection / grouping label (rendered on the index).
    Collection: string option
}

/// One extraction-derived block of a document — a page, a slide, a
/// section. `LocationHint` (the KB's `SourceLocation` rendered as text,
/// e.g. `"Page 4"` / `"Slide 12: Methodology"`) becomes the section's
/// deep-link anchor; `None` falls back to a positional anchor.
type KnowledgeDocSection = {
    Heading: string
    LocationHint: string option
    Body: string
}

/// A document's full renderable content, for its landing page.
type KnowledgeDocContent = {
    Id: string
    Title: string
    Description: string option
    Collection: string option
    Sections: KnowledgeDocSection list
}

/// Read-port the deployment fills from its KnowledgeBase. Keeps
/// PublicRendering free of a KB dependency (GP 1) — the deployment adapts
/// its `KnowledgeApi.GetDocuments` + extraction into these projections.
///
/// **Six portability rules** (CLAUDE.md — every substrate interface that
/// could plausibly be implemented by a distributed framework satisfies
/// all six):
///   1. Identity by value — inputs are `AccessContext` / `string` (doc
///      id); outputs are records of primitives. No live handles. ✓
///   2. Async at every boundary — both members return `Async<_>`. ✓
///   3. Retry as data — N/A; a read-only query surface. ✓
///   4. Stateless between invocations — each call carries the
///      `AccessContext` + doc id in full; no inter-call state. ✓
///   5. No cross-shard ordering — each call is a point / list query;
///      no ordering contract beyond the returned list. ✓
///   6. Precision at the lower bound — N/A; no timing primitives. ✓
type IKnowledgeDocsProvider =
    /// List the documents the caller may read (scoped by `ctx`, GP 4).
    abstract ListDocuments: ctx: AccessContext -> Async<KnowledgeDocSummary list>
    /// Fetch one document's renderable content, or `None` when it is
    /// unknown or the caller may not read it.
    abstract GetDocument: docId: string -> ctx: AccessContext -> Async<KnowledgeDocContent option>

/// Compose-time configuration for a `KnowledgeDocsSource`.
type KnowledgeDocsConfig = {
    /// Slug prefix the source claims. `"docs"` makes the source resolve
    /// the index at `docs` / `docs/index` and per-document landings at
    /// `docs/<id>`. A request outside the prefix falls through (`None`).
    SlugPrefix: string
    /// Title rendered on the collection index page.
    IndexTitle: string
}

module KnowledgeDocsConfig =
    /// A config claiming `slugPrefix`, with a generic index title. Tune
    /// the title with `withIndexTitle`.
    let create (slugPrefix: string) : KnowledgeDocsConfig = {
        SlugPrefix = slugPrefix
        IndexTitle = "Knowledge base"
    }

    let withIndexTitle (title: string) (c: KnowledgeDocsConfig) : KnowledgeDocsConfig = { c with IndexTitle = title }

module KnowledgeDocsSource =

    /// The three slug shapes the source recognises under its prefix.
    type private Route =
        | Index
        | Doc of id: string
        | Outside

    /// Classify a slug against the configured prefix.
    let private classify (prefix: string) (slug: string) : Route =
        let p = prefix.Trim('/')
        let s = slug.Trim('/')

        if p = "" then
            Outside
        elif s = p || s = p + "/index" then
            Index
        elif s.StartsWith(p + "/") then
            let tail = s.Substring(p.Length + 1)
            if tail = "" then Index else Doc tail
        else
            Outside

    /// Stable, URL-safe section anchor from a location hint (`"Page 4"` →
    /// `"page-4"`); falls back to a positional `"section-N"` when no hint
    /// is present.
    let private anchorOf (index: int) (hint: string option) : string =
        match hint with
        | Some h when h.Trim() <> "" ->
            let lowered = h.Trim().ToLowerInvariant()

            let cleaned =
                lowered
                |> String.map (fun c -> if System.Char.IsLetterOrDigit c then c else '-')

            // Collapse runs of '-' and trim leading / trailing ones.
            let collapsed =
                cleaned.Split([| '-' |], System.StringSplitOptions.RemoveEmptyEntries)
                |> String.concat "-"

            if collapsed = "" then
                sprintf "section-%d" (index + 1)
            else
                collapsed
        | _ -> sprintf "section-%d" (index + 1)

    /// The collection index document — every document as a cited link
    /// into its landing page. An empty collection renders a graceful
    /// callout rather than a bare heading.
    let private indexDoc (config: KnowledgeDocsConfig) (docs: KnowledgeDocSummary list) : NarrativeDocument =
        let prefix = config.SlugPrefix.Trim('/')

        let baseDoc =
            Narrative.create config.IndexTitle
            |> Narrative.subtitle (sprintf "%d document(s)" (List.length docs))

        if List.isEmpty docs then
            baseDoc
            |> Narrative.section "Documents" "documents" [
                Narrative.callout Severity.Info [ Narrative.text "No documents are published in this collection yet." ]
            ]
            |> Narrative.withProvenance "knowledge-docs" None "kb-docs-index" []
        else
            let items =
                docs
                |> List.map (fun d ->
                    let href = "/" + prefix + "/" + d.Id
                    let titleSpan = Narrative.link href [ Narrative.text d.Title ]

                    match d.Description with
                    | Some desc when desc.Trim() <> "" -> [ titleSpan; Narrative.text (" — " + desc) ]
                    | _ -> [ titleSpan ])

            baseDoc
            |> Narrative.section "Documents" "documents" [ Narrative.bullets items ]
            |> Narrative.withProvenance "knowledge-docs" None "kb-docs-index" []

    /// A document's landing page — one Narrative section per extraction
    /// block, the location hint folded into the heading and the anchor so
    /// a citation elsewhere can deep-link to the exact page / slide.
    let private docPage (doc: KnowledgeDocContent) : NarrativeDocument =
        let withSubtitle =
            match doc.Description with
            | Some d when d.Trim() <> "" -> Narrative.create doc.Title |> Narrative.subtitle d
            | _ -> Narrative.create doc.Title

        let withSections =
            doc.Sections
            |> List.mapi (fun i s ->
                let anchor = anchorOf i s.LocationHint

                let heading =
                    match s.LocationHint with
                    | Some h when h.Trim() <> "" -> sprintf "%s · %s" s.Heading h
                    | _ -> s.Heading

                heading, anchor, [ Narrative.paragraph [ Narrative.text s.Body ] ])
            |> List.fold (fun acc (heading, anchor, els) -> Narrative.section heading anchor els acc) withSubtitle

        withSections |> Narrative.withProvenance "knowledge-docs" None "kb-doc" []

    /// Construct a KB-as-docs content source over a read-port. Register it
    /// with `PublicRenderingServerApp.withContentSource`. The produced
    /// pages are `Narrative`-bodied and cache through the Phase 84 render
    /// cache like any other content-source page; the source also
    /// enumerates its routes (Phase 95) for sitemap + static export.
    let create (provider: IKnowledgeDocsProvider) (config: KnowledgeDocsConfig) : IContentSource =
        { new IContentSource with
            member _.Resolve (Slug s) ctx = async {
                match classify config.SlugPrefix s with
                | Outside -> return None
                | Index ->
                    let! docs = provider.ListDocuments ctx
                    return Some(ContentBody.Narrative(indexDoc config docs))
                | Doc id ->
                    let! docOpt = provider.GetDocument id ctx

                    match docOpt with
                    | Some doc -> return Some(ContentBody.Narrative(docPage doc))
                    // Unknown / unauthorised doc → fall through to the
                    // 404 path rather than render an empty page.
                    | None -> return None
            }

          interface IEnumerableContentSource with
              member _.EnumerateRoutes() = async {
                  // Sitemap / static-export discovery runs under an
                  // anonymous context: the provider returns only what an
                  // anonymous caller may see (GP 4), so nothing gated
                  // leaks to a crawler. (`unrestricted` here means empty
                  // module-permissions, not data-scope bypass — the
                  // subject is still `AnonymousSession`, so the
                  // provider's own team/scope checks hold.)
                  let anon = AccessContext.unrestricted (AnonymousSession "kb-docs-enumerate")
                  let! docs = provider.ListDocuments anon
                  let prefix = config.SlugPrefix.Trim('/')
                  return Slug prefix :: (docs |> List.map (fun d -> Slug(prefix + "/" + d.Id)))
              }
        }
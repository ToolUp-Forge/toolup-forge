module ToolUp.Platform.Tests.InProcess.KnowledgeSurfaceTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.PublicRendering

// ─── Phase 91 — RAG-backed answer pages (RagAnswerSource) ───────────
//
// The security-critical property under test is grounding: an extractive
// answer shows only retrieved content (no hallucination surface), and
// `StrictlyGrounded` renders a no-answer page on a retrieval miss rather
// than any speculative prose.

let private ctx = AccessContext.unrestricted (AnonymousSession "s")

/// A retrieval pipeline stub returning a fixed match list. Index /
/// DeleteByScope are no-ops (the answer source never writes).
let private fakePipeline (matches: VectorMatch list) : IRetrievalPipeline =
    { new IRetrievalPipeline with
        member _.Retrieve _request _context = async { return matches }
        member _.Index _chunkId _chunk _scope = async { return () }
        member _.DeleteByScope _scope = async { return () }
    }

let private mkMatch (content: string) (score: float) : VectorMatch = {
    ChunkId = System.Guid.NewGuid().ToString("N")
    Content = content
    Score = score
    Scope = VectorScope.Team "kb"
    Metadata = Map[ChunkMetadata.LocationHintKey, "Page 1"]
}

let private resolve (source: IContentSource) (slug: string) =
    source.Resolve (Slug slug) ctx |> Async.RunSynchronously

/// All visible text inside an inline span, recursing into `Link` content
/// (a link wraps inner spans). Other inline kinds contribute nothing.
let rec private spanText (sp: InlineSpan) : string list =
    match sp with
    | Text t -> [ t ]
    | Link(_, inner) -> inner |> List.collect spanText
    | _ -> []

/// The text of a narrative section by id (concatenated visible text from
/// paragraphs / callouts / blockquotes AND bullet / ordered list items,
/// following links into their visible content).
let private sectionText (doc: NarrativeDocument) (sectionId: string) : string =
    doc.Sections
    |> List.tryFind (fun s -> s.Id = sectionId)
    |> Option.map (fun s ->
        s.Elements
        |> List.collect (fun e ->
            match e with
            | Paragraph spans -> spans
            | Callout(_, spans) -> spans
            | Blockquote(_, spans) -> spans
            | BulletList items
            | OrderedList items -> items |> List.collect id
            | _ -> [])
        |> List.collect spanText
        |> String.concat " ")
    |> Option.defaultValue ""

let private hasSection (doc: NarrativeDocument) (sectionId: string) =
    doc.Sections |> List.exists (fun s -> s.Id = sectionId)

let private narrativeOf (body: ContentBody option) : NarrativeDocument =
    match body with
    | Some(ContentBody.Narrative doc) -> doc
    | other -> failtestf "Expected a Narrative body; got %A" other

// ─── Phase 91 — KB-as-docs (KnowledgeDocsSource) test scaffolding ────

/// A knowledge-docs provider stub backed by a fixed document map. The
/// list view returns summaries; the per-doc view returns content for a
/// known id, `None` otherwise (unknown / unauthorised doc).
let private fakeDocsProvider (docs: KnowledgeDocContent list) : IKnowledgeDocsProvider =
    { new IKnowledgeDocsProvider with
        member _.ListDocuments _ctx = async {
            return
                docs
                |> List.map (fun d -> {
                    Id = d.Id
                    Title = d.Title
                    Description = d.Description
                    Collection = d.Collection
                })
        }

        member _.GetDocument docId _ctx = async { return docs |> List.tryFind (fun d -> d.Id = docId) }
    }

let private mkDoc (id: string) (title: string) (sections: KnowledgeDocSection list) : KnowledgeDocContent = {
    Id = id
    Title = title
    Description = Some(sprintf "About %s" title)
    Collection = Some "guides"
    Sections = sections
}

// ─── Phase 91 — AI authoring hardening (publisher guardrails) ────────

/// A fresh in-memory `IEntityStore` registered for `PublicPageEntity`,
/// plus a guardrail-configured publisher over it. Returned together so a
/// test can publish then read the stored page back to assert Status /
/// Audience.
let private mkPublisher (guardrails: NarrativePublishGuardrails) : INarrativePagePublisher * IEntityStore =
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register<PublicPageEntity>(PublicPageEntity.registration)
    let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore

    let publisher =
        PublicRenderingNarrativePagePublisher.create store [ LayoutName "page" ] None guardrails

    publisher, store

let private publish (publisher: INarrativePagePublisher) (slug: string) (doc: NarrativeDocument) =
    publisher.PublishAsync(slug, None, None, Some "page", OverwriteExisting, doc)
    |> Async.RunSynchronously

let private readBack (store: IEntityStore) (slug: string) : PublicPage =
    store.Get<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug)
    |> Async.RunSynchronously
    |> function
        | Ok e -> e.Page
        | Error err -> failtestf "expected the page to be stored; got %A" err

let private simpleDoc (sections: int) : NarrativeDocument =
    [ 1..sections ]
    |> List.fold
        (fun acc i ->
            acc
            |> Narrative.section (sprintf "S%d" i) (sprintf "s%d" i) [ Narrative.paragraph [ Narrative.text "x" ] ])
        (Narrative.create "Doc")

let tests =
    testList "KnowledgeSurface (Phase 91)" [

        // ─── RagAnswerSource.queryFromSlug ───────────────────────────

        testCase "queryFromSlug de-slugifies the tail under the prefix"
        <| fun _ ->
            Expect.equal
                (RagAnswerSource.queryFromSlug "answers" "answers/how-do-i-deploy")
                (Some "how do i deploy")
                "hyphens become spaces"

            Expect.equal (RagAnswerSource.queryFromSlug "answers" "guides/x") None "different prefix → None"
            Expect.equal (RagAnswerSource.queryFromSlug "answers" "answers") None "bare prefix → None"

        // ─── Extractive answer (default, grounded by construction) ───

        testCase "grounded matches with no synthesis hook → extractive cited answer"
        <| fun _ ->
            let source =
                RagAnswerSource.create
                    (fakePipeline [ mkMatch "Deploy with run.ps1." 0.9; mkMatch "Set the port band." 0.7 ])
                    (RagAnswerConfig.create "answers" [ VectorScope.Team "kb" ])

            let doc = resolve source "answers/deploy" |> narrativeOf

            Expect.equal doc.Title "deploy" "page title is the query"

            Expect.stringContains
                (sectionText doc "answer")
                "Deploy with run.ps1."
                "lead answer is the top retrieved chunk"

            Expect.isTrue (hasSection doc "sources") "a Sources section is present"
            Expect.stringContains (sectionText doc "sources") "Set the port band." "every grounded match is cited"

        // ─── StrictlyGrounded refusal (the load-bearing safety test) ──

        testCase "StrictlyGrounded + no retrieval hit → no-answer page, never fabricated prose"
        <| fun _ ->
            let source =
                RagAnswerSource.create
                    (fakePipeline [])
                    (RagAnswerConfig.create "answers" [ VectorScope.Team "kb" ]
                     |> RagAnswerConfig.strictlyGrounded)

            let doc = resolve source "answers/unknown-thing" |> narrativeOf

            Expect.isTrue (hasSection doc "no-answer") "renders the no-answer section"
            Expect.isFalse (hasSection doc "sources") "no Sources section — nothing was retrieved"

            Expect.stringContains
                (sectionText doc "no-answer")
                "no content relevant"
                "states the KB has nothing relevant"

        testCase "StrictlyGrounded refuses even when a synthesis hook is present (no speculation)"
        <| fun _ ->
            let hook: RagSynthesisHook =
                fun _q _m -> async { return "A confidently fabricated answer." }

            let source =
                RagAnswerSource.create
                    (fakePipeline [])
                    (RagAnswerConfig.create "answers" [ VectorScope.Team "kb" ]
                     |> RagAnswerConfig.strictlyGrounded
                     |> RagAnswerConfig.withSynthesis hook)

            let doc = resolve source "answers/q" |> narrativeOf
            Expect.isTrue (hasSection doc "no-answer") "strict grounding overrides the hook — no answer shown"

            Expect.isFalse
                (sectionText doc "no-answer" |> fun t -> t.Contains "fabricated")
                "the hook's prose never appears"

        testCase "MinScore drops weak matches → StrictlyGrounded refuses"
        <| fun _ ->
            let source =
                RagAnswerSource.create
                    (fakePipeline [ mkMatch "weakly related" 0.2 ])
                    (RagAnswerConfig.create "answers" [ VectorScope.Team "kb" ]
                     |> RagAnswerConfig.withMinScore 0.5
                     |> RagAnswerConfig.strictlyGrounded)

            let doc = resolve source "answers/q" |> narrativeOf
            Expect.isTrue (hasSection doc "no-answer") "a sub-threshold match does not count as grounded"

        // ─── Synthesis hook (opt-in LLM prose) ───────────────────────

        testCase "synthesis hook supplies the Answer prose; sources still cited"
        <| fun _ ->
            let hook: RagSynthesisHook =
                fun q ms -> async { return sprintf "Synthesized for %s from %d sources." q (List.length ms) }

            let source =
                RagAnswerSource.create
                    (fakePipeline [ mkMatch "chunk one" 0.9 ])
                    (RagAnswerConfig.create "answers" [ VectorScope.Team "kb" ]
                     |> RagAnswerConfig.withSynthesis hook)

            let doc = resolve source "answers/q" |> narrativeOf

            Expect.stringContains
                (sectionText doc "answer")
                "Synthesized for q from 1 sources."
                "synthesized prose is the Answer"

            Expect.stringContains (sectionText doc "sources") "chunk one" "retrieved chunk is still cited"

        // ─── Fall-through ────────────────────────────────────────────

        testCase "a slug outside the prefix falls through (None)"
        <| fun _ ->
            let source =
                RagAnswerSource.create
                    (fakePipeline [ mkMatch "x" 0.9 ])
                    (RagAnswerConfig.create "answers" [ VectorScope.Team "kb" ])

            Expect.isNone (resolve source "blog/hello") "non-prefix slug is not claimed"

        // ─── KB-as-docs (KnowledgeDocsSource) ────────────────────────

        testCase "docs index lists every document as a link into its landing"
        <| fun _ ->
            let source =
                KnowledgeDocsSource.create
                    (fakeDocsProvider [
                        mkDoc "a1" "Deploy guide" [
                            {
                                Heading = "Intro"
                                LocationHint = Some "Page 1"
                                Body = "x"
                            }
                        ]
                        mkDoc "b2" "Ports guide" [
                            {
                                Heading = "Bands"
                                LocationHint = Some "Page 2"
                                Body = "y"
                            }
                        ]
                    ])
                    (KnowledgeDocsConfig.create "docs")

            let indexDoc = resolve source "docs" |> narrativeOf
            Expect.isTrue (hasSection indexDoc "documents") "index has a Documents section"
            Expect.stringContains (sectionText indexDoc "documents") "Deploy guide" "first doc title is listed"
            Expect.stringContains (sectionText indexDoc "documents") "Ports guide" "second doc title is listed"

            // `docs/index` resolves to the same index page.
            let viaIndexSlug = resolve source "docs/index" |> narrativeOf
            Expect.isTrue (hasSection viaIndexSlug "documents") "docs/index also resolves the index"

        testCase "docs landing renders one section per extraction block with a location-hint anchor"
        <| fun _ ->
            let source =
                KnowledgeDocsSource.create
                    (fakeDocsProvider [
                        mkDoc "a1" "Deploy guide" [
                            {
                                Heading = "Overview"
                                LocationHint = Some "Page 4"
                                Body = "Run run.ps1 to deploy."
                            }
                            {
                                Heading = "Ports"
                                LocationHint = Some "Slide 12"
                                Body = "Use the 5000 band."
                            }
                        ]
                    ])
                    (KnowledgeDocsConfig.create "docs")

            let landing = resolve source "docs/a1" |> narrativeOf
            Expect.equal landing.Title "Deploy guide" "landing title is the document title"
            // Anchor derives from the location hint: "Page 4" → "page-4".
            Expect.isTrue (hasSection landing "page-4") "first section anchors on its location hint"
            Expect.isTrue (hasSection landing "slide-12") "second section anchors on its location hint"
            Expect.stringContains (sectionText landing "page-4") "Run run.ps1 to deploy." "section body is rendered"

        testCase "docs landing for an unknown id falls through (None)"
        <| fun _ ->
            let source =
                KnowledgeDocsSource.create (fakeDocsProvider []) (KnowledgeDocsConfig.create "docs")

            Expect.isNone (resolve source "docs/missing") "unknown doc id is not claimed"

        testCase "docs source enumerates the index + every doc route (sitemap / static export)"
        <| fun _ ->
            let source =
                KnowledgeDocsSource.create
                    (fakeDocsProvider [
                        mkDoc "a1" "One" [
                            {
                                Heading = "H"
                                LocationHint = None
                                Body = "b"
                            }
                        ]
                        mkDoc "b2" "Two" [
                            {
                                Heading = "H"
                                LocationHint = None
                                Body = "b"
                            }
                        ]
                    ])
                    (KnowledgeDocsConfig.create "docs")

            let routes = ContentSource.enumerateAll [ source ] |> Async.RunSynchronously
            Expect.contains routes (Slug "docs") "the index route is enumerated"
            Expect.contains routes (Slug "docs/a1") "the first doc route is enumerated"
            Expect.contains routes (Slug "docs/b2") "the second doc route is enumerated"

        testCase "empty docs collection renders a graceful index, not a bare page"
        <| fun _ ->
            let source =
                KnowledgeDocsSource.create (fakeDocsProvider []) (KnowledgeDocsConfig.create "docs")

            let indexDoc = resolve source "docs" |> narrativeOf
            Expect.stringContains (sectionText indexDoc "documents") "No documents" "empty-state callout is shown"

        // ─── Semantic-search SSR (SemanticSearchHandler / SemanticSearch) ─

        testCase "classify enforces empty / too-long / search decisions"
        <| fun _ ->
            let cfg =
                SemanticSearchConfig.create [ VectorScope.Team "kb" ]
                |> SemanticSearchConfig.withMaxQueryChars 8

            Expect.equal (SemanticSearch.classify cfg "   ") SemanticSearch.EmptyQuery "blank query → EmptyQuery"

            Expect.equal
                (SemanticSearch.classify cfg "this is way too long")
                (SemanticSearch.QueryTooLong 8)
                "over-cap query → QueryTooLong with the cap"

            Expect.equal
                (SemanticSearch.classify cfg " hi ")
                (SemanticSearch.Search "hi")
                "in-bounds query is trimmed + searched"

        testCase "buildResultsDoc renders a ranked cited list"
        <| fun _ ->
            let cfg = SemanticSearchConfig.create [ VectorScope.Team "kb" ]

            let doc =
                SemanticSearch.buildResultsDoc cfg "deploy" [ mkMatch "Run run.ps1." 0.9; mkMatch "Set the band." 0.7 ]

            Expect.equal doc.Title "Search: deploy" "title carries the query"
            Expect.isTrue (hasSection doc "results") "a Results section is present"
            Expect.stringContains (sectionText doc "results") "Run run.ps1." "first result content is shown"
            Expect.stringContains (sectionText doc "results") "Set the band." "second result content is shown"

        testCase "buildResultsDoc with no matches renders a no-results callout"
        <| fun _ ->
            let cfg = SemanticSearchConfig.create [ VectorScope.Team "kb" ]
            let doc = SemanticSearch.buildResultsDoc cfg "nothing" []
            Expect.stringContains (sectionText doc "results") "No content" "no-results callout is shown"

        testCase "result links deep-link into KB-as-docs when a link builder is supplied"
        <| fun _ ->
            let cfg =
                SemanticSearchConfig.create [ VectorScope.Team "kb" ]
                |> SemanticSearchConfig.withResultLink (SemanticSearch.docsLinkByChunkId "docs")

            // A chunk id following the KB `{docId}:chunk:{i}` convention.
            let m = {
                mkMatch "content" 0.9 with
                    ChunkId = "doc-abc:chunk:3"
            }

            let doc = SemanticSearch.buildResultsDoc cfg "q" [ m ]

            let linked =
                doc.Sections
                |> List.collect _.Elements
                |> List.collect (fun e ->
                    match e with
                    | BulletList items -> items |> List.collect id
                    | _ -> [])
                |> List.choose (fun sp ->
                    match sp with
                    | Link(href, _) -> Some href
                    | _ -> None)

            Expect.contains linked "/docs/doc-abc" "result deep-links into the KB-as-docs landing"

        testCase "docsLinkByChunkId returns None for a non-conventional chunk id"
        <| fun _ ->
            let link = SemanticSearch.docsLinkByChunkId "docs"

            let m = {
                mkMatch "c" 0.9 with
                    ChunkId = "no-marker-here"
            }

            Expect.isNone (link m) "a chunk id without ':chunk:' yields no link"

        // ─── AI authoring hardening (NarrativePublishGuardrails) ─────

        testCase "default guardrails preserve immediate Published / Public (GP 11)"
        <| fun _ ->
            let publisher, store = mkPublisher NarrativePublishGuardrails.defaults

            match publish publisher "p1" (simpleDoc 1) with
            | PublishSucceeded slug ->
                let page = readBack store slug
                Expect.equal page.Status Published "default lands Published"
                Expect.equal page.Audience PageAudience.Public "default lands Public"
            | other -> failtestf "expected success; got %A" other

        testCase "forced-draft guardrail lands the AI page as Draft (not publicly served)"
        <| fun _ ->
            let publisher, store = mkPublisher NarrativePublishGuardrails.aiHardened

            match publish publisher "p2" (simpleDoc 1) with
            | PublishSucceeded slug ->
                let page = readBack store slug
                Expect.equal page.Status Draft "aiHardened forces a Draft landing"

                Expect.isFalse
                    (PublicPage.isPubliclyVisible System.DateTimeOffset.UtcNow page)
                    "a forced-draft page is not publicly visible until reviewed"
            | other -> failtestf "expected success; got %A" other

        testCase "audience guardrail pins the published page's audience"
        <| fun _ ->
            let guardrails =
                NarrativePublishGuardrails.defaults
                |> NarrativePublishGuardrails.withAudience PageAudience.Authenticated

            let publisher, store = mkPublisher guardrails

            match publish publisher "p3" (simpleDoc 1) with
            | PublishSucceeded slug ->
                Expect.equal (readBack store slug).Audience PageAudience.Authenticated "audience is pinned"
            | other -> failtestf "expected success; got %A" other

        testCase "layout allow-list refuses a disallowed layout hint"
        <| fun _ ->
            let guardrails =
                NarrativePublishGuardrails.defaults
                |> NarrativePublishGuardrails.withAllowedLayouts [ "article" ]

            let publisher, _ = mkPublisher guardrails

            // The helper publishes with layoutHint = Some "page", which is
            // outside the allow-list of ["article"].
            match publish publisher "p4" (simpleDoc 1) with
            | PublishFailed reason -> Expect.stringContains reason "not permitted" "a disallowed layout is refused"
            | other -> failtestf "expected refusal; got %A" other

        testCase "section cap refuses an over-long document"
        <| fun _ ->
            let guardrails =
                NarrativePublishGuardrails.defaults
                |> NarrativePublishGuardrails.withMaxSections 2

            let publisher, _ = mkPublisher guardrails

            match publish publisher "p5" (simpleDoc 5) with
            | PublishFailed reason -> Expect.stringContains reason "exceeding" "an over-long document is refused"
            | other -> failtestf "expected refusal; got %A" other
    ]
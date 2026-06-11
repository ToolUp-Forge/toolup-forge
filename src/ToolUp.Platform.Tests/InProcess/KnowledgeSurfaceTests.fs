module ToolUp.Platform.Tests.InProcess.KnowledgeSurfaceTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline
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
    ]
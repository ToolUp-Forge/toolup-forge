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

/// The text of a narrative section by id (concatenated inline `Text` spans
/// from paragraphs / callouts / blockquotes).
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
            | _ -> [])
        |> List.choose (fun sp ->
            match sp with
            | Text t -> Some t
            | _ -> None)
        |> String.concat " ")
    |> Option.defaultValue ""

let private hasSection (doc: NarrativeDocument) (sectionId: string) =
    doc.Sections |> List.exists (fun s -> s.Id = sectionId)

let private narrativeOf (body: ContentBody option) : NarrativeDocument =
    match body with
    | Some(ContentBody.Narrative doc) -> doc
    | other -> failtestf "Expected a Narrative body; got %A" other

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
    ]
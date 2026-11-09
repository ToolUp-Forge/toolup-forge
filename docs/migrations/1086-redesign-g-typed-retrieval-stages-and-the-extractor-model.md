# 1086 — typed retrieval stages, and one extractor model

**Class:** breaking, in one place: `ScoredPool` gains a field, so a full `ScoredPool` record literal outside the
SDK adds it. Everything else is additive or internal. Rides 0.42.0.

## What changes

**Retrieval.** `RetrievalPipeline.Retrieve` is one composition of named stages — authorise, facts, rewrite,
candidates, filters, normalise, rerank, boosts, MMR, truncate, merge. Each stage returns the marks it records as
`RetrievalStage` cases (new, `ToolUp.RAG.RetrievalPipeline`), and `RetrievalStage.mark` renders them to the
`RetrievalTrace.Stages` strings. **Every mark string is unchanged**: the full mark sequence of nine compositions
is held byte for byte by `src/ToolUp.Platform.Tests/fixtures/retrieval/mark-sequences.txt`. New public types:
`RetrievalStage` (with `mark` and `ordering`), `BoundedFault`, `BoostKind`, `OrderingStep`.

Two behaviours change, both corrections Phase 1009 filed (1009.A, 1009.B):

- **`MergeStrategy.Separate` groups.** It takes the `TopK` best, as before, and returns them grouped by scope in
  the order the request listed the scopes, each scope's matches in rank order. It used to return them in rank
  order, which made it `Interleaved` under another name. When the result spans more than one scope the trace
  names the merge as the final ordering step: `FinalOrder:Separate`. A single-scope `Separate` trace is unchanged.
- **Adaptive-K reads relevance.** With MMR on, adaptive-K counts on the score gaps of the relevance order
  (`ScoredPool.Relevance`) and keeps that many matches in MMR's order. It used to read the gaps of MMR's
  diversity order, where a diverse candidate promoted past a closer one opened a spurious gap and cut the result
  early. With MMR off nothing changes.

**A fused miss is visible.** Under the default (fused) composition the returned pool's best match scores `1.0`
whether the corpus answered or not, so `/health/rag` read every fused retrieval as a hit. The pool now carries an
absolute signal beside the fused order:

```fsharp skip=signature
type ScoredPool = {
    // …
    DenseSimilarity: float option   // the best returned match's dense similarity, before fusion
}
```

`RollingRagTelemetry` reads a fused pool on it: a hit only when the pool also clears `MinScore` **and**
`DenseSimilarity` clears `RagTelemetry.DefaultDenseMissFloor`.

| Decision | Value |
|---|---|
| The signal | The best pre-fusion dense (cosine) similarity among the returned matches. `None` when the dense branch degraded, so the classification falls back to the fused rule. |
| The threshold | `DefaultDenseMissFloor = 0.25`, read in cosine space with `RetrievalScoreSpace.admits Cosine`. |
| What is recorded | For a fused pool carrying the signal, the signal is the recorded top score, so `RetrievalAvgTopScore` is the average best dense similarity of the hits rather than a constant `1.0`. |
| Spaces it applies to | `Fused` only. A cosine pool's own score is already absolute; a reranked pool is read on the reranker's scale, as before. |

A deployment whose embedder runs hotter than the default assumes sets its own floor with
`RollingRagTelemetry(windowSeconds, denseMissFloor)`; the one-argument form keeps the default.

**Bounded stages.** The fact stage, the query rewrite, the derived-disclosure check and the fact-clause planner
(`RAGPromptBuilder`) run under one `Bounded.run` (internal). Nothing a consumer sees changes.

**Extraction (internal to the knowledge base).** Each format is an `Extractor` reading a list of parts, and one
fold turns parts into chunks; the DOCX mode is a field of the extraction's deps, and scanned-ness is judged once,
in the PDF extractor's one open. Every chunk of every format is held byte for byte by
`src/ToolUp.Platform.Tests/fixtures/extractors/`. `DocxExtraction.enableStructuredDocxExtraction` and
`resetToFlatDocxExtraction` are unchanged until Phase 1087 moves the mode onto `KnowledgePolicies`.

## The edit

A `ScoredPool` built field by field adds the field:

```diff
 {
     Space = RetrievalScoreSpace.Cosine
     Unit = 1.0
     Matches = matches
     Relevance = None
+    DenseSimilarity = None
 }
```

`ScoredPool.ofMatches` sets it to `None`, so a pool built that way needs nothing. A caller that relied on
`Separate` returning rank order sorts the result by score itself, or asks for `Interleaved`.

## Verify

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 1086"`
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "RAG telemetry"`

## Rollback

Revert the phase's commits; the mark strings and the chunk bytes are the ones the fixtures hold, so nothing
persisted moves.

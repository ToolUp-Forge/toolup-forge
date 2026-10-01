# Phase 866 — Retrieval scores carry their scale

**Applies to:** every deployment composed with `RAGServerApp` that set `withMinScore` (or a
`MinScore` through `withRetrievalDefaults`); any reader of `VectorMatch.Score` /
`RetrievedSource.Score` from the default retrieval pipeline.
**Breaking:** no wire change and no removed surface. Additive surface on `ToolUp.RAG.Server`
(`RetrievalPipeline.RetrievalScoreSpace` and its module). Behavioural change: the scores the default
pipeline returns under a keyword index (the default composition) are normalised onto `[0, 1]`.
**Action required:** none for a deployment that never set a threshold — result **order** is
unchanged. A deployment that set `withMinScore` re-tunes (below).

## What changes

1. **The default composition's scores were raw reciprocal rank fusion.** `composeRAG` builds a
   keyword index unless told `withoutSparseIndex`, so every default retrieval fused the dense and
   keyword rankings with `1 / (60 + rank)` per list: a score that tops out at `2/61 ≈ 0.033`.
   `withMinScore` was documented as a cosine gate and clamped to `[0.0, 0.99]`, so **any threshold
   above about `0.033` dropped every chunk** and the assistant answered with no retrieved context.
   The additive boosts (`ActiveModuleBoost 0.05`, `SummaryBoost 0.10`, `FactNarrativeJoinBoost 0.15`)
   each exceeded the whole fused range, so a boosted chunk jumped above every unboosted one whatever
   its relevance.
2. **The fused pool is now normalised onto `[0, 1]`** (min-max, within each query's candidate pool,
   after the scoping filters and before the boosts). The pool's best candidate scores `1.0`, its
   weakest `0.0`. The map is affine and increasing, so order is unchanged. The trace records a
   `Normalise:MinMax` stage and ends with `ScoreSpace:<Cosine|Fused|Reranked>`, naming the space of
   the returned scores.
3. **Boosts are nudges.** Acting in the unit range, a boost of `b` lifts a match over one at most `b`
   better and never over one further ahead. The defaults are unchanged in value (5 %, 10 % and 15 %
   of the range).
4. **A startup validator, `rag-min-score-space`,** warns when a threshold cannot be met in the
   composed space, or cannot be confirmed reachable: a composed reranker (`IReranker` promises an
   ordering, not a calibrated scale) or a supplied pipeline (`withRetrievalPipeline`).

## If you set `withMinScore`

- **Default (hybrid) composition.** The threshold is now **relative**: it trims matches whose fused
  relevance is below that fraction of the way from the pool's weakest to its best candidate. It
  never empties a non-empty pool, because the best candidate always scores `1.0`. `0.5` drops the
  lower half of each pool's relevance range; a value tuned against the old raw scores (anything
  below `0.033` that did not silence retrieval) now drops almost nothing and should be raised.
- **Dense-only (`withoutSparseIndex`).** Unchanged: the threshold is a cosine-similarity floor, and it
  is the composition to choose when you need a gate that can refuse a whole pool of weak matches
  (for example under `StrictlyGrounded`).
- **With a reranker.** The threshold reads the reranker's own scale; check it against that
  reranker's documentation. The validator warns until you have.

Readers of `RetrievedSource.Score` (a Sources-panel badge) now see `0`–`1` under the default
composition instead of values near `0.03`.

## Verification

- `SparseIndexCompositionTests`, "Phase 866": under the composed default, `MinScore 0.5` keeps the
  strong match and drops the weak one (red before the change: nothing survived); scores span
  `[0, 1]`; with no threshold and no boosts the fused order equals rank fusion's and the scores are
  its min-max image; a summary outranks an equal-relevance chunk and does not outrank a far better
  one; the validator is silent for a reachable threshold and warns for a reranker, a supplied
  pipeline, and a threshold at the top of the space.
- The retrieval evaluation pack (`src/ToolUp.RAG.Evaluation`, fixtures `platform-readme`,
  `eval-filtered`, `eval-morphology`) reports identical Recall@1/5/10, nDCG@5/10, MRR and per-query
  first-relevant ranks before and after — none of its fixtures carries a summary chunk or an active
  module, so no boost fires and order is all that its metrics read.

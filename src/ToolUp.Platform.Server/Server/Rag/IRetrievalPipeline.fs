module ToolUp.Platform.Infra.IRetrievalPipeline

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

/// High-level facade for the RAG retrieval pipeline. Sits above `IVectorStore`
/// and `IEmbeddingProvider`, handling embedding generation, scope-access
/// validation, and result merging. This is the interface that `SystemPromptBuilder`
/// implementations and modules use — they never call `IVectorStore` directly.
///
/// The pipeline enforces team isolation: a request carrying a `Team` scope
/// is validated against `AccessContext.TeamId` before the query reaches the
/// vector store. A mismatch returns an empty result rather than an error, so
/// callers do not need to handle auth failures from retrieval inline.
type IRetrievalPipeline =
    /// Query the vector store(s) with a natural-language string. The pipeline
    /// embeds the query, validates scope access, searches `IVectorStore`, and
    /// merges results according to `request.Merge`. Returns an empty list when
    /// no relevant chunks are found or when the caller lacks access to all
    /// requested scopes.
    abstract Retrieve: request: RetrievalRequest -> context: AccessContext -> Async<VectorMatch list>

    /// Embed a chunk and store it at `scope`. Used by ingestion code
    /// (e.g. `IngestionBackgroundService`, module post-save hooks) to add
    /// content without constructing the embedding externally.
    abstract Index: chunkId: string -> chunk: TextChunk -> scope: VectorScope -> Async<unit>

    /// Remove all indexed content for the given scope. Called when a document
    /// is deleted from the KnowledgeBase or a team is removed.
    abstract DeleteByScope: scope: VectorScope -> Async<unit>

/// The space a retrieval's scores are in. A score is only meaningful against
/// the stage that produced it, and the stages disagree by orders of
/// magnitude: a cosine similarity sits in `[-1, 1]`, while a raw
/// reciprocal-rank-fusion score tops out at `2 / (60 + 1)`, about `0.033`.
/// The default pipeline carries the space from the producing stage to the
/// stages that read a score, normalises the fused space onto `[0, 1]`, and
/// returns it on the `ScoredPool` (and as a `ScoreSpace:<space>` mark on the
/// trace).
[<RequireQualifiedAccess>]
type RetrievalScoreSpace =
    /// Dense cosine similarity exactly as the vector store scored it — the
    /// dense-only composition (`RagOptions.withoutSparseIndex`). Absolute:
    /// a threshold is a similarity floor, and a pool of weak matches can
    /// fall below it entirely.
    | Cosine
    /// Reciprocal rank fusion of the dense and keyword rankings, min-max
    /// normalised onto `[0, 1]` within the candidate pool (after the scoping
    /// filters, before the boosts) — the default composition, which always
    /// builds a keyword index. Relative: the pool's best candidate scores
    /// `1.0` and its weakest `0.0`, so a threshold trims the weak tail
    /// relative to the best match rather than refusing a pool outright.
    | Fused
    /// A cross-encoder reranker's own scale (`RagOptions.withReranker`).
    /// `IReranker` promises an ordering, not a calibrated scale, so the SDK
    /// cannot say what range these scores occupy. The boosts read it too:
    /// each nudges in units of the reranked pool's range.
    | Reranked

/// THE threshold rule, read by the prompt builder's `MinScore` gate, by the
/// telemetry's hit / low-score classification and by the startup validator.
[<RequireQualifiedAccess>]
module RetrievalScoreSpace =

    /// Whether `score` clears `threshold` in `space`: no threshold admits
    /// every score, and a threshold admits only scores strictly ABOVE it.
    /// The comparison is the same in each space; what the space changes is
    /// what the threshold MEANS — a similarity floor in `Cosine`, a share
    /// of the pool's range below its best match in `Fused`, and the
    /// reranker's own scale in `Reranked` — which is why every reader
    /// states the space it read the score in.
    let admits (space: RetrievalScoreSpace) (threshold: float option) (score: float) : bool =
        match space, threshold with
        | _, None -> true
        | (RetrievalScoreSpace.Cosine | RetrievalScoreSpace.Fused | RetrievalScoreSpace.Reranked), Some t -> score > t

/// One retrieval's result with the space its scores are in: the value a
/// reader gates and classifies by, instead of a trace string.
type ScoredPool = {
    /// The space `Matches`' scores are in.
    Space: RetrievalScoreSpace
    /// The unit a boost is measured in for this pool (`ScoredPool.unitOf`).
    Unit: float
    /// The returned matches, in the order the caller receives them —
    /// exactly what `IRetrievalPipeline.Retrieve` returns.
    Matches: VectorMatch list
    /// The pool in relevance order before MMR diversified it, when MMR
    /// reordered it; `None` when the returned order IS the relevance order.
    Relevance: VectorMatch list option
    /// The best dense (cosine) similarity among `Matches`, read before any
    /// fusion: an absolute relevance reading beside a relative order. A
    /// fused pool's best match is `1.0` whether the corpus answered or not,
    /// so this is what tells a hit from a miss there. `None` when no dense
    /// ranking scored the returned matches (its embed degraded, or the
    /// pipeline does not report one).
    DenseSimilarity: float option
}

/// The scored sibling of `IRetrievalPipeline`: a pipeline that returns the
/// space its scores are in. A capability by probe, never a member — a
/// pipeline that does not implement it still composes, and its readers
/// read its matches in the composed space (`ScoredPool.retrieve`).
type IScoredRetrievalPipeline =
    /// `Retrieve`, with the space its scores are in. `Matches` is exactly
    /// what `Retrieve` returns for the same request.
    abstract RetrieveScored: request: RetrievalRequest -> context: AccessContext -> Async<ScoredPool>

/// Building and reading a `ScoredPool`.
[<RequireQualifiedAccess>]
module ScoredPool =

    /// The unit a boost is measured in, so a boost of `b` is the same nudge
    /// in every space. The normalised fused pool spans `[0, 1]` and the
    /// cosine scale is bounded by `1.0`, so there the unit is `1.0`. A
    /// reranker's scale is uncalibrated, so there the unit is the reranked
    /// pool's own range: `b` lifts a match over one at most `b` of the range
    /// better, and never over one further ahead. A pool with no spread (one
    /// candidate, or every score equal) falls back to `1.0`, where any
    /// positive nudge breaks the tie the same way.
    let unitOf (space: RetrievalScoreSpace) (matches: VectorMatch list) : float =
        match space, matches with
        | RetrievalScoreSpace.Reranked, _ :: _ ->
            let scores = matches |> List.map _.Score
            let range = List.max scores - List.min scores
            if range > 0.0 then range else 1.0
        | _ -> 1.0

    /// A pool of `matches` read in `space`, in relevance order.
    let ofMatches (space: RetrievalScoreSpace) (matches: VectorMatch list) : ScoredPool = {
        Space = space
        Unit = unitOf space matches
        Matches = matches
        Relevance = None
        DenseSimilarity = None
    }

    /// Retrieve through `pipeline`: its own pool when it implements the
    /// scored sibling, otherwise its matches read in `composed` — the space
    /// the deployment's composition implies.
    let retrieve
        (composed: RetrievalScoreSpace)
        (pipeline: IRetrievalPipeline)
        (request: RetrievalRequest)
        (context: AccessContext)
        : Async<ScoredPool> =
        match pipeline with
        | :? IScoredRetrievalPipeline as scored -> scored.RetrieveScored request context
        | _ -> async {
            let! matches = pipeline.Retrieve request context
            return ofMatches composed matches
          }

    /// `pipeline`, declaring `composed` as its space when it declares none
    /// of its own; a scored pipeline is returned unchanged. Lets a
    /// composition hand its readers a pipeline whose space they can read.
    let declaring (composed: RetrievalScoreSpace) (pipeline: IRetrievalPipeline) : IRetrievalPipeline =
        match pipeline with
        | :? IScoredRetrievalPipeline -> pipeline
        | _ ->
            { new IRetrievalPipeline with
                member _.Retrieve request context = pipeline.Retrieve request context
                member _.Index chunkId chunk scope = pipeline.Index chunkId chunk scope
                member _.DeleteByScope scope = pipeline.DeleteByScope scope
              interface IScoredRetrievalPipeline with
                  member _.RetrieveScored request context =
                      retrieve composed pipeline request context
            }
module ToolUp.Platform.ITextSummariser

/// Optional companion to chunking. Summarises a chunk in the context of
/// its parent document, producing a short preamble (typically 50–100
/// tokens) that's prepended to the chunk before embedding. Pattern is
/// "Anthropic contextual retrieval": each chunk gets a sentence-or-two
/// situating it in the document, which dramatically lifts recall on
/// document corpora where chunks are otherwise hard to distinguish from
/// each other (boilerplate-heavy contracts, repetitive financial tables,
/// chaptered books with similar-looking page text).
///
/// Off by default. Summarisation is an AI call per chunk at ingestion
/// time — for a 200-page document chunked at 50 chunks per document, that
/// is 50 inference calls per upload. Deployments that have AI cost budget
/// to spend on ingestion-time quality opt in by supplying an implementation
/// (typically a thin wrapper over an `IAIProvider`).
///
/// Phase 9c portability:
/// 1. Identity by value — both inputs are `string`; no live handles.
/// 2. Async at every boundary — `Summarise` returns `Async<_>`.
/// 3. No callback / supervision hooks.
/// 4. Stateless between calls — each `Summarise` is fully parameterised.
/// 5. No cross-call ordering promises.
/// 6. Precision: implementation-dependent (LLM output isn't deterministic
///    in general; deployments wanting determinism should set their
///    provider's temperature to 0).
type ITextSummariser =
    /// Stable identifier for trace / observability. Never serialised on
    /// the wire — used by the eval harness (Phase 14j) to attribute lift
    /// to a specific summariser configuration.
    abstract Name: string

    /// Produce a short contextual preamble for `chunk` given the
    /// surrounding `documentContext` (typically the document title plus
    /// a 100-200 token excerpt or table of contents). Implementations
    /// MUST keep the output short — typically a single sentence — so the
    /// contextual header never crowds out the chunk's actual content
    /// against the embedder's max input length.
    abstract Summarise: documentContext: string -> chunk: string -> Async<string>
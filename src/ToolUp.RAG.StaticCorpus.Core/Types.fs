// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.RAG.StaticCorpus

open System

// ─── Static-corpus core types ────────────────────────────────────
//
// Pure, Fable-compatible domain types for a build-time-precomputed
// documentation-retrieval index. No serialisation format and no vendor
// type leaks in here — the MessagePack (de)serialisation lives in
// `Serialization`, gated .NET-only. These types ship under `fable/` in
// the nupkg so a Fable-side consumer can model a loaded corpus without a
// server hop.

/// One retrievable chunk of a documentation corpus, carrying its
/// build-time-precomputed embedding. `HeadingPath` is the full ancestor
/// heading chain (e.g. `["# Title"; "## Section"; "### Subsection"]`) so a
/// retrieved chunk always carries its section context. `Metadata` holds
/// producer-stamped extras (source anchor, ordinal, …). The `Id` is the
/// stable chunk identity `{source}:{heading-anchor}:{ordinal}`.
type DocChunk = {
    Id: string
    Source: string
    HeadingPath: string list
    Body: string
    Embedding: float32[]
    Metadata: Map<string, string>
}

/// A whole precomputed corpus: the chunk set plus the embedding-model
/// identity the chunks were embedded under (a query must be embedded by the
/// same model for cosine similarity to mean anything), the dimensionality,
/// the build timestamp, and the packer version (part of the determinism /
/// cache-invalidation contract). Produced at build time by the packer;
/// consumed read-only at runtime by the retrieval pipeline.
type StaticCorpus = {
    Chunks: DocChunk[]
    EmbeddingModel: string
    EmbeddingDimensions: int
    BuiltUtc: DateTime
    PackerVersion: string
}
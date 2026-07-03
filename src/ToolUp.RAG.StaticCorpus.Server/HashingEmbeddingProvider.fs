// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.RAG.StaticCorpus

open System
open ToolUp.Platform.IEmbeddingProvider

// ─── Deterministic offline hashing embedder ──────────────────────
//
// A stateless feature-hashing bag-of-words `IEmbeddingProvider`. Each word
// bumps the dimension it hashes to; the vector is L2-normalised. It needs no
// network, no API key, and no corpus fit — so the same instance produces the
// same vector for the same text on every process and machine (a determinism
// requirement for a build-time-precomputed index), and the pack-time and
// query-time embeddings live in the same space by construction (same impl).
//
// It is a *lexical* embedder — retrieval matches on shared vocabulary, not
// deep semantics. That is enough for the sample, the determinism tests, and
// small offline docs corpora; a production deployment wanting semantic
// retrieval packs with a real model (e.g. the OpenAI embedding companion) and
// composes the same provider at runtime. Marked offline/deterministic here so
// there is no doubt it is not a semantic model.
//
// Distributed-ready by rule 4: no state between calls (the hash is pure).

module HashingEmbeddingProvider =

    /// Default embedding width. Wide enough that word collisions are rare for
    /// a docs-sized vocabulary, small enough to keep the index compact.
    [<Literal>]
    let DefaultDimensions = 512

    /// Stable non-cryptographic hash (FNV-1a, 32-bit) — unlike
    /// `String.GetHashCode`, it is identical across processes / runtimes, so
    /// the embedding is reproducible (`GetHashCode` is randomised per process
    /// in modern .NET and would break determinism).
    let private fnv1a (s: string) : uint32 =
        let mutable h = 2166136261u

        for ch in s do
            h <- h ^^^ uint32 ch
            h <- h * 16777619u

        h

    let private tokenSeparators = [|
        ' '
        '\n'
        '\r'
        '\t'
        '.'
        ','
        ';'
        ':'
        '!'
        '?'
        '('
        ')'
        '['
        ']'
        '{'
        '}'
        '"'
        '\''
        '`'
        '/'
        '\\'
        '#'
        '*'
        '_'
        '-'
        '='
        '<'
        '>'
        '|'
    |]

    /// Embed one text into a unit-length `float32[]` of length `dimensions`.
    let embed (dimensions: int) (text: string) : float32[] =
        let v = Array.zeroCreate<float32> dimensions

        if not (String.IsNullOrWhiteSpace text) then
            let words =
                text.ToLowerInvariant().Split(tokenSeparators, StringSplitOptions.RemoveEmptyEntries)

            for w in words do
                let idx = int (fnv1a w % uint32 dimensions)
                v[idx] <- v[idx] + 1.0f

            // L2-normalise so cosine similarity is a plain dot product and
            // long documents don't dominate by magnitude.
            let mutable mag = 0.0

            for x in v do
                mag <- mag + float x * float x

            let mag = sqrt mag

            if mag > 0.0 then
                for i in 0 .. dimensions - 1 do
                    v[i] <- float32 (float v[i] / mag)

        v

    /// Build a deterministic offline embedder of the given width.
    let create (dimensions: int) : IEmbeddingProvider =
        { new IEmbeddingProvider with
            member _.GenerateEmbedding(text: string) = async { return embed dimensions text }

            member _.GenerateEmbeddings(texts: string seq) = async {
                return texts |> Seq.map (embed dimensions) |> Seq.toArray
            }

            member _.Dimensions = dimensions
            member _.ProviderId = "hashing"
            member _.ModelId = sprintf "hashing-bow-d%d" dimensions
        }

    /// The default-width offline embedder (`DefaultDimensions`).
    let createDefault () : IEmbeddingProvider = create DefaultDimensions
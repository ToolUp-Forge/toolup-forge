module ToolUp.Platform.Tests.RAG.StaticCorpusContract

open System
open Expecto
open ToolUp.RAG.StaticCorpus

// ─── Phase 63 — StaticCorpus contract + determinism ──────────────────
//
// 63.B — MessagePack round-trip: serialise → deserialise reproduces the
//   corpus; re-serialising is byte-identical; metadata insertion order does
//   not affect the bytes (sorted-key write).
// (63.G determinism over the packer + 63.H IRetrievalPipelineContract land
//  in later slices of this file.)

let private mkChunk (i: int) (headings: string list) (md: (string * string) list) : DocChunk = {
    Id = sprintf "doc-%d:sec:%d" i i
    Source = sprintf "docs/file-%d.md" i
    HeadingPath = headings
    Body = sprintf "Body text for chunk %d with some words to embed." i
    Embedding = [| for j in 0..7 -> float32 (i * 10 + j) * 0.5f |]
    Metadata = Map.ofList md
}

let private sampleCorpus: StaticCorpus = {
    Chunks = [|
        mkChunk 0 [ "# Title"; "## Intro" ] [ "anchor", "intro"; "ordinal", "0" ]
        mkChunk 1 [ "# Title"; "## Usage"; "### Details" ] [ "anchor", "details"; "ordinal", "1" ]
        mkChunk 2 [ "# Title" ] []
    |]
    EmbeddingModel = "test-embedder-v1"
    EmbeddingDimensions = 8
    BuiltUtc = DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc)
    PackerVersion = "63.0.0"
}

[<Tests>]
let tests =
    testList "Phase 63 — StaticCorpus" [

        test "serialise → deserialise reproduces the corpus" {
            let bytes = Serialization.serialize sampleCorpus
            let round = Serialization.deserialize bytes

            Expect.equal round.EmbeddingModel sampleCorpus.EmbeddingModel "embedding model preserved"
            Expect.equal round.EmbeddingDimensions sampleCorpus.EmbeddingDimensions "dimensions preserved"
            Expect.equal round.BuiltUtc sampleCorpus.BuiltUtc "build timestamp preserved (UTC ticks)"
            Expect.equal round.PackerVersion sampleCorpus.PackerVersion "packer version preserved"
            Expect.equal round.Chunks.Length sampleCorpus.Chunks.Length "chunk count preserved"

            for orig, r in Array.zip sampleCorpus.Chunks round.Chunks do
                Expect.equal r.Id orig.Id "chunk id preserved"
                Expect.equal r.Source orig.Source "chunk source preserved"
                Expect.equal r.HeadingPath orig.HeadingPath "heading path preserved (order + contents)"
                Expect.equal r.Body orig.Body "chunk body preserved"
                Expect.equal r.Embedding orig.Embedding "embedding vector preserved exactly"
                Expect.equal r.Metadata orig.Metadata "metadata preserved"
        }

        test "re-serialising a round-tripped corpus is byte-identical" {
            let bytes1 = Serialization.serialize sampleCorpus
            let bytes2 = Serialization.serialize (Serialization.deserialize bytes1)
            Expect.equal bytes2 bytes1 "serialise ∘ deserialise ∘ serialise is a fixed point (byte-equal)"
        }

        test "serialising the same corpus twice is deterministic" {
            let a = Serialization.serialize sampleCorpus
            let b = Serialization.serialize sampleCorpus
            Expect.equal a b "same corpus value ⇒ byte-identical output"
        }

        test "metadata insertion order does not affect the bytes" {
            let forwards = mkChunk 9 [ "# H" ] [ "alpha", "1"; "beta", "2"; "gamma", "3" ]

            let backwards = mkChunk 9 [ "# H" ] [ "gamma", "3"; "beta", "2"; "alpha", "1" ]

            let corpusOf c = { sampleCorpus with Chunks = [| c |] }

            Expect.equal
                (Serialization.serialize (corpusOf forwards))
                (Serialization.serialize (corpusOf backwards))
                "metadata is written in sorted-key order, so insertion order is irrelevant"
        }

        test "empty corpus round-trips" {
            let empty = { sampleCorpus with Chunks = [||] }
            let round = Serialization.deserialize (Serialization.serialize empty)
            Expect.equal round.Chunks.Length 0 "no chunks survive round-trip as an empty array"
            Expect.equal round.EmbeddingModel empty.EmbeddingModel "scalars still preserved on an empty corpus"
        }
    ]
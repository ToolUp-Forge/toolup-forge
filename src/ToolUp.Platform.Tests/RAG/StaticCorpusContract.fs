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

        // ── 63.C — Markdig chunker boundaries ───────────────────────
        testList "Chunker" [

            test "chunks on H2 boundaries with the full heading path" {
                let md =
                    "# Guide\n\nIntro paragraph.\n\n## Setup\n\nInstall the thing.\n\n## Usage\n\nRun the thing.\n"

                let chunks = Chunker.chunk "guide.md" Chunker.DefaultMaxChunkChars md

                // Intro (under H1) + Setup + Usage = 3 sections.
                Expect.equal chunks.Length 3 "one chunk per H1-intro / H2 section"

                let setup = chunks |> List.find (fun c -> c.Body.Contains "Install")
                Expect.equal setup.HeadingPath [ "# Guide"; "## Setup" ] "heading path is the H1→H2 ancestor chain"
                Expect.stringContains setup.Id "guide.md:setup:" "id is {source}:{anchor}:{ordinal}"
                Expect.equal (setup.Metadata.TryFind "anchor") (Some "setup") "anchor slug stamped for jump-links"
            }

            test "nested H3 carries the full H1→H2→H3 path" {
                let md = "# T\n\n## Section\n\ntext\n\n### Detail\n\ndeep text\n"
                let chunks = Chunker.chunk "d.md" Chunker.DefaultMaxChunkChars md

                let detail = chunks |> List.find (fun c -> c.Body.Contains "deep text")

                Expect.equal
                    detail.HeadingPath
                    [ "# T"; "## Section"; "### Detail" ]
                    "H3 chunk carries all three ancestor headings"
            }

            test "a fenced code block is never split and stays intact" {
                let fence = "```fsharp\nlet x = 1\n\n// ## not a heading\nlet y = 2\n```"

                let md = sprintf "# T\n\n## Code\n\nBefore.\n\n%s\n\nAfter.\n" fence

                let chunks = Chunker.chunk "c.md" Chunker.DefaultMaxChunkChars md
                let codeChunk = chunks |> List.find (fun c -> c.Body.Contains "let x = 1")

                Expect.stringContains codeChunk.Body "```fsharp" "opening fence preserved"
                Expect.stringContains codeChunk.Body "```" "closing fence preserved"

                Expect.stringContains
                    codeChunk.Body
                    "// ## not a heading"
                    "the '##' inside the fence did not start a new chunk"
            }

            test "an oversize section splits on block boundaries, replaying the heading path" {
                let para n =
                    sprintf "Paragraph %d %s" n (String.replicate 40 "word ")

                let body = [ for i in 1..10 -> para i ] |> String.concat "\n\n"
                let md = sprintf "# T\n\n## Big\n\n%s\n" body

                let chunks = Chunker.chunk "big.md" 300 md

                Expect.isGreaterThan chunks.Length 1 "the >300-char section is split into multiple chunks"

                for c in chunks do
                    Expect.equal c.HeadingPath [ "# T"; "## Big" ] "every split replays the same heading path"

                    Expect.isLessThanOrEqual
                        c.Body.Length
                        900
                        "no split wildly exceeds the budget (whole-block grouping)"

                let ordinals = chunks |> List.map (fun c -> c.Metadata.TryFind "ordinal")
                Expect.equal (List.distinct ordinals |> List.length) chunks.Length "ordinals are unique across splits"
            }

            test "a code fence larger than the budget is still emitted whole" {
                let bigFence = "```\n" + String.replicate 200 "codeline\n" + "```"

                let md = sprintf "# T\n\n## Big\n\n%s\n" bigFence

                let chunks = Chunker.chunk "bf.md" 300 md
                let fenceChunk = chunks |> List.find (fun c -> c.Body.Contains "codeline")

                Expect.stringContains fenceChunk.Body "```" "the fence survives whole even though it exceeds the budget"
                Expect.isGreaterThan fenceChunk.Body.Length 300 "an atomic block is never split to fit the budget"
            }

            test "chunking is a pure function (same input ⇒ same output)" {
                let md = "# T\n\n## A\n\naaa\n\n## B\n\nbbb\n"
                let a = Chunker.chunk "p.md" Chunker.DefaultMaxChunkChars md
                let b = Chunker.chunk "p.md" Chunker.DefaultMaxChunkChars md
                Expect.equal a b "deterministic: identical chunk lists"
            }
        ]
    ]
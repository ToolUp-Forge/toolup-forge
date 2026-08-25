module ToolUp.Platform.Tests.RAG.StaticCorpusContract

open System
open System.IO
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.AI
open ToolUp.RAG.RAGCompose
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

        // ── 63.D / 63.H — StaticCorpusRetrievalPipeline (IRetrievalPipeline) ──
        testList "RetrievalPipeline" [

            // A deterministic bag-of-words embedder: each word bumps the
            // dimension it hashes to. Lexical overlap ⇒ cosine similarity, so
            // a query retrieves the chunk sharing the most words — enough to
            // exercise the pipeline without a real model.
            let dim = 32

            // FNV-1a over the UTF-8 bytes: String.GetHashCode is randomised
            // per process, so bucketing on it reshuffles the projection (and
            // occasionally the ranking) with each run's hash seed.
            let fnv1a (w: string) : uint32 =
                let mutable h = 2166136261u

                for b in Text.Encoding.UTF8.GetBytes w do
                    h <- (h ^^^ uint32 b) * 16777619u

                h

            let bow (text: string) : float32[] =
                let v = Array.zeroCreate<float32> dim

                let words =
                    text
                        .ToLowerInvariant()
                        .Split([| ' '; '\n'; '\t'; '.'; ','; '#' |], StringSplitOptions.RemoveEmptyEntries)

                for w in words do
                    let h = int (fnv1a w % uint32 dim)
                    v[h] <- v[h] + 1.0f

                v

            let embedder =
                { new IEmbeddingProvider with
                    member _.GenerateEmbedding text = async { return bow text }
                    member _.GenerateEmbeddings texts = async { return texts |> Seq.map bow |> Seq.toArray }
                    member _.Dimensions = dim
                    member _.ProviderId = "test"
                    member _.ModelId = "bow-v1"
                }

            let mkCorpusChunk (id: string) (body: string) : DocChunk = {
                Id = id
                Source = sprintf "%s.md" id
                HeadingPath = [ "# Docs"; sprintf "## %s" id ]
                Body = body
                Embedding = bow body
                Metadata = Map.ofList [ "anchor", id ]
            }

            let corpus: StaticCorpus = {
                Chunks = [|
                    mkCorpusChunk "install" "install the setup wizard and configure the database connection"
                    mkCorpusChunk "usage" "run the usage report to see monthly active totals"
                    mkCorpusChunk "billing" "invoices and billing cycles are managed under the account page"
                |]
                EmbeddingModel = "bow-v1"
                EmbeddingDimensions = dim
                BuiltUtc = DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc)
                PackerVersion = "63.0.0"
            }

            let pipeline = StaticCorpusRetrievalPipeline.create embedder corpus
            let access = AccessContext.unrestricted (AnonymousSession "t")

            let request query topK = {
                RetrievalRequest.create query [ Deployment ] topK MergeStrategy.Interleaved with
                    OriginFilter = None
            }

            testAsync "retrieves the lexically-closest chunk first" {
                let! matches = pipeline.Retrieve (request "monthly usage report" 3) access
                Expect.isGreaterThan matches.Length 0 "at least one match"
                Expect.equal (List.head matches).ChunkId "usage" "the 'usage' chunk ranks first for a usage query"
            }

            testAsync "honours TopK" {
                let! matches = pipeline.Retrieve (request "the" 2) access
                Expect.isLessThanOrEqual matches.Length 2 "no more than TopK matches returned"
            }

            testAsync "empty query returns no matches" {
                let! matches = pipeline.Retrieve (request "   " 5) access
                Expect.isEmpty matches "a blank query short-circuits to no retrieval"
            }

            testAsync "matches carry deployment scope + a citation source" {
                let! matches = pipeline.Retrieve (request "install setup" 1) access
                let top = List.head matches
                Expect.equal top.Scope Deployment "static corpus is deployment-scoped"
                Expect.isTrue (top.Metadata.ContainsKey "_source") "a _source citation header is stamped"

                Expect.equal
                    (top.Metadata.TryFind ChunkMetadata.OriginKey)
                    (Some "Document")
                    "chunks report origin Document"
            }

            testAsync "metadata Filters are honoured" {
                let filtered = {
                    request "the" 5 with
                        Filters = Some(Map.ofList [ "anchor", "billing" ])
                }

                let! matches = pipeline.Retrieve filtered access
                Expect.all matches (fun m -> m.ChunkId = "billing") "only chunks matching the metadata filter survive"
            }

            test "Index throws NotSupportedException (read-only corpus)" {
                Expect.throwsT<NotSupportedException>
                    (fun () ->
                        pipeline.Index "id" (Unchecked.defaultof<TextChunk>) Deployment
                        |> Async.RunSynchronously
                        |> ignore)
                    "Index is unsupported on a static corpus"
            }

            test "DeleteByScope throws NotSupportedException (read-only corpus)" {
                Expect.throwsT<NotSupportedException>
                    (fun () -> pipeline.DeleteByScope Deployment |> Async.RunSynchronously |> ignore)
                    "DeleteByScope is unsupported on a static corpus"
            }

            testAsync "an embedding-dimension mismatch fails loudly" {
                let wrongDimEmbedder =
                    { new IEmbeddingProvider with
                        member _.GenerateEmbedding _ = async { return Array.zeroCreate<float32> (dim + 1) }

                        member _.GenerateEmbeddings texts = async {
                            return texts |> Seq.map (fun _ -> Array.zeroCreate<float32> (dim + 1)) |> Seq.toArray
                        }

                        member _.Dimensions = dim + 1
                        member _.ProviderId = "test"
                        member _.ModelId = "wrong"
                    }

                let mismatched = StaticCorpusRetrievalPipeline.create wrongDimEmbedder corpus

                let! ex = Async.Catch(mismatched.Retrieve (request "x" 1) access)

                match ex with
                | Choice2Of2(:? InvalidOperationException) -> ()
                | Choice2Of2 other -> failtestf "expected InvalidOperationException, got %A" other
                | Choice1Of2 _ -> failtest "expected a dimension-mismatch failure"
            }

            testAsync "round-trip through the .scidx bytes preserves retrieval" {
                let bytes = Serialization.serialize corpus

                let loaded =
                    StaticCorpusRetrievalPipeline.loadFromStream embedder (new IO.MemoryStream(bytes))

                let! matches = loaded.Retrieve (request "monthly usage report" 1) access

                Expect.equal
                    (List.head matches).ChunkId
                    "usage"
                    "a loaded corpus retrieves identically to an in-memory one"
            }
        ]

        // ── 63.E / 63.G — packer + determinism over the filesystem ──
        testList "Packer" [

            let tempRoot () =
                let d =
                    Path.Combine(Path.GetTempPath(), "scidx-test-" + Guid.NewGuid().ToString("N"))

                Directory.CreateDirectory d |> ignore
                d

            let writeDocs (dir: string) (files: (string * string) list) =
                for (name, content) in files do
                    let p = Path.Combine(dir, name)
                    Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
                    File.WriteAllText(p, content)

            let mkConfig (dir: string) : Packer.PackConfig = {
                Include = [ "**/*.md" ]
                Exclude = []
                EmbeddingProvider = "hashing"
                Model = ""
                Dimensions = Some 64
                MaxChunkChars = Chunker.DefaultMaxChunkChars
                Output = "out/docs.scidx"
                BaseDir = dir
            }

            let fixedUtc = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            let packEmbedder = HashingEmbeddingProvider.create 64

            let outputBytes (config: Packer.PackConfig) =
                File.ReadAllBytes(Path.Combine(config.BaseDir, config.Output))

            /// A counting decorator so cache-hit behaviour is observable.
            let counting (inner: IEmbeddingProvider) =
                let mutable calls = 0

                let e =
                    { new IEmbeddingProvider with
                        member _.GenerateEmbedding t =
                            calls <- calls + 1
                            inner.GenerateEmbedding t

                        member _.GenerateEmbeddings ts =
                            calls <- calls + Seq.length ts
                            inner.GenerateEmbeddings ts

                        member _.Dimensions = inner.Dimensions
                        member _.ProviderId = inner.ProviderId
                        member _.ModelId = inner.ModelId
                    }

                e, (fun () -> calls)

            test "pack twice over an unchanged corpus → byte-identical .scidx" {
                let dir = tempRoot ()

                try
                    writeDocs dir [
                        "a.md", "# A\n\n## S1\n\ntext one here\n"
                        "b.md", "# B\n\n## S2\n\ntext two here\n"
                    ]

                    let config = mkConfig dir

                    Packer.pack packEmbedder config fixedUtc None
                    |> Async.RunSynchronously
                    |> ignore

                    let bytes1 = outputBytes config

                    Packer.pack packEmbedder config fixedUtc None
                    |> Async.RunSynchronously
                    |> ignore

                    let bytes2 = outputBytes config
                    Expect.equal bytes2 bytes1 "same MDs + model + packer version + builtUtc ⇒ byte-identical .scidx"
                finally
                    Directory.Delete(dir, true)
            }

            test "editing one file changes only that file's chunks" {
                let dir = tempRoot ()

                try
                    writeDocs dir [
                        "a.md", "# A\n\n## S1\n\napple banana\n"
                        "b.md", "# B\n\n## S2\n\ncherry date\n"
                    ]

                    let config = mkConfig dir

                    Packer.pack packEmbedder config fixedUtc None
                    |> Async.RunSynchronously
                    |> ignore

                    let before = Serialization.deserialize (outputBytes config)

                    // Touch only a.md.
                    writeDocs dir [ "a.md", "# A\n\n## S1\n\napple banana elderberry\n" ]

                    Packer.pack packEmbedder config fixedUtc None
                    |> Async.RunSynchronously
                    |> ignore

                    let after = Serialization.deserialize (outputBytes config)

                    let chunkFor (src: string) (c: StaticCorpus) =
                        c.Chunks |> Array.filter (fun ch -> ch.Source = src)

                    Expect.equal
                        (chunkFor "b.md" after)
                        (chunkFor "b.md" before)
                        "b.md's chunks (body + embedding) are untouched"

                    Expect.notEqual (chunkFor "a.md" after) (chunkFor "a.md" before) "a.md's chunks changed"
                finally
                    Directory.Delete(dir, true)
            }

            test "the on-disk embedding cache makes a re-pack embed nothing" {
                let dir = tempRoot ()

                try
                    writeDocs dir [
                        "a.md", "# A\n\n## S1\n\nalpha beta\n"
                        "b.md", "# B\n\n## S2\n\ngamma delta\n"
                    ]

                    let config = mkConfig dir
                    let cacheDir = Some(Path.Combine(dir, ".cache"))

                    let e1, calls1 = counting packEmbedder
                    Packer.pack e1 config fixedUtc cacheDir |> Async.RunSynchronously |> ignore
                    Expect.isGreaterThan (calls1 ()) 0 "first pack embeds every chunk"

                    let e2, calls2 = counting packEmbedder
                    Packer.pack e2 config fixedUtc cacheDir |> Async.RunSynchronously |> ignore
                    Expect.equal (calls2 ()) 0 "second pack hits the disk cache for every chunk — no embedder calls"
                finally
                    Directory.Delete(dir, true)
            }

            test "excludes remove matching files; the corpus records model + packer version" {
                let dir = tempRoot ()

                try
                    writeDocs dir [
                        "keep.md", "# K\n\n## S\n\nkeep me\n"
                        "drafts/skip.md", "# D\n\n## S\n\nskip me\n"
                    ]

                    let config = {
                        mkConfig dir with
                            Exclude = [ "drafts/**" ]
                    }

                    Packer.pack packEmbedder config fixedUtc None
                    |> Async.RunSynchronously
                    |> ignore

                    let corpus = Serialization.deserialize (outputBytes config)

                    Expect.isFalse
                        (corpus.Chunks |> Array.exists (fun c -> c.Source.Contains "skip"))
                        "excluded files contribute no chunks"

                    Expect.isTrue
                        (corpus.Chunks |> Array.exists (fun c -> c.Source = "keep.md"))
                        "included files are packed"

                    Expect.equal corpus.EmbeddingModel packEmbedder.ModelId "corpus records the embedding model id"
                    Expect.equal corpus.PackerVersion Packer.PackerVersion "corpus records the packer version"
                finally
                    Directory.Delete(dir, true)
            }

            test "incremental pack skips when inputs are unchanged and re-packs when they change (63.F)" {
                let dir = tempRoot ()

                try
                    writeDocs dir [ "a.md", "# A\n\n## S1\n\nfirst\n" ]
                    let config = mkConfig dir

                    let first =
                        Packer.packIncremental packEmbedder config fixedUtc None
                        |> Async.RunSynchronously

                    match first with
                    | Packer.Packed _ -> ()
                    | Packer.Skipped -> failtest "the first pack must not be skipped"

                    // Unchanged inputs ⇒ skipped.
                    let second =
                        Packer.packIncremental packEmbedder config fixedUtc None
                        |> Async.RunSynchronously

                    Expect.equal second Packer.Skipped "unchanged inputs ⇒ the second pack is a no-op"

                    // Change a file ⇒ re-packs.
                    writeDocs dir [ "a.md", "# A\n\n## S1\n\nsecond\n" ]

                    let third =
                        Packer.packIncremental packEmbedder config fixedUtc None
                        |> Async.RunSynchronously

                    match third with
                    | Packer.Packed _ -> ()
                    | Packer.Skipped -> failtest "a changed input must force a re-pack"
                finally
                    Directory.Delete(dir, true)
            }
        ]

        // ── 63.A / 63.H — composeRAG DI: ingestion suppression ──────
        testList "Compose" [

            let stubFactory =
                { new IAIProviderFactory with
                    member _.Available = []
                    member _.PlatformDescriptors = []
                    member _.PlatformDescriptor = None
                    member _.Resolve _ = async { return Error NoProviderConfigured }
                    member _.TryResolveByLabel(_, _) = async { return Error NoProviderConfigured }
                    member _.BuildPlatform(_, _, _) = None
                }

            let stubProfile =
                { new IProviderProfile with
                    member _.Get _ = async { return None }
                    member _.Set(_, _) = async { return Ok() }
                    member _.Clear _ = async { return () }
                    member _.ResolveEntry(_, _, _) = async { return None }
                    member _.SetEntryHealth(_, _, _) = async { return Ok() }
                }

            let embedder = HashingEmbeddingProvider.create 32

            let emptyStaticPipeline =
                StaticCorpusRetrievalPipeline.create embedder {
                    Chunks = [||]
                    EmbeddingModel = "hashing-bow-d32"
                    EmbeddingDimensions = 32
                    BuiltUtc = DateTime.UnixEpoch
                    PackerVersion = "test"
                }

            /// Compose the app, apply the RAG service-config to a fresh
            /// container, and list the resolved hosted-service type names.
            let hostedServiceNames (app: RAGServerApp) : string list =
                let composed = composeRAG app
                let sc = ServiceCollection() :> IServiceCollection

                let sc =
                    match composed.Extensions.ServiceConfig with
                    | Some f -> f sc
                    | None -> sc

                let sp = sc.BuildServiceProvider()

                sp.GetServices<IHostedService>()
                |> Seq.map (fun h -> h.GetType().Name)
                |> Seq.toList

            test "withRetrievalPipeline + no VectorisationHandler suppresses the ingestion services (63.A)" {
                let app =
                    RAGServerApp.create stubFactory stubProfile embedder
                    |> RAGServerApp.withRetrievalPipeline emptyStaticPipeline

                let names = hostedServiceNames app

                Expect.isFalse
                    (names |> List.exists (fun n -> n.Contains "Ingestion"))
                    (sprintf "no IngestionBackgroundService should be registered; got %A" names)

                Expect.isFalse
                    (names |> List.exists (fun n -> n.Contains "Reembed"))
                    "no reembedding background service either"
            }

            test "the default pipeline (no override) registers the ingestion service" {
                let app = RAGServerApp.create stubFactory stubProfile embedder
                let names = hostedServiceNames app

                Expect.isTrue
                    (names |> List.exists (fun n -> n.Contains "Ingestion"))
                    (sprintf "the default composition must register the IngestionBackgroundService; got %A" names)
            }
        ]
    ]
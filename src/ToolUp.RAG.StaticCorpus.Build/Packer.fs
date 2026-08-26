// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.RAG.StaticCorpus

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography
open ToolUp.Platform.IEmbeddingProvider

// ─── Static-corpus packer ────────────────────────────────────────
//
// Walks a configured include set, chunks each Markdown file (63.C), embeds
// each chunk once via the supplied `IEmbeddingProvider`, and writes the
// deterministic `.scidx` index (63.B serialisation).
//
// Determinism (63.G): files are enumerated in a stable sorted order; chunk
// ids / heading paths are deterministic (63.C); the embedding of a given text
// is deterministic (the offline hashing embedder, or a real model API which
// is deterministic per text); `BuiltUtc` is an injected value (NOT wall-clock)
// so two packs over an unchanged corpus with the same `builtUtc` produce
// byte-identical output. Embedding responses are cached on disk keyed by
// (model, text) so a re-pack with unchanged text is a no-op even offline.

module Packer =

    /// Packer version — part of the determinism / cache-invalidation contract.
    /// Bump on any change to chunking, embedding-text assembly, or the index
    /// layout so stale caches / indices are rebuilt rather than silently reused.
    [<Literal>]
    let PackerVersion = "63.1.0"

    /// Parsed `staticcorpus.json`.
    type PackConfig = {
        /// Glob patterns (relative to `BaseDir`, `/`-separated). `**` matches
        /// across directories, `*` within a segment.
        Include: string list
        /// Glob patterns removed from the include set.
        Exclude: string list
        /// Embedding provider name resolved by the CLI (`"hashing"` offline
        /// default, or `"openai"`).
        EmbeddingProvider: string
        /// Model id (informational for `"openai"`; ignored by `"hashing"`).
        Model: string
        /// Optional embedding width for the offline `"hashing"` provider.
        Dimensions: int option
        /// Per-chunk character budget (63.C).
        MaxChunkChars: int
        /// Output `.scidx` path (relative to `BaseDir` or absolute).
        Output: string
        /// Root the include/exclude globs resolve against. Defaults to the
        /// directory containing the config file.
        BaseDir: string
    }

    // ── Config JSON ────────────────────────────────────────────────

    /// Parse `staticcorpus.json` text. `configDir` is the directory of the
    /// config file — the default `BaseDir` and the root for a relative
    /// `Output`. Raises on malformed JSON / missing required fields.
    let parseConfig (configDir: string) (json: string) : PackConfig =
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        let stringList (name: string) =
            match root.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Array -> [ for e in v.EnumerateArray() -> e.GetString() ]
            | _ -> []

        let stringOr (name: string) (fallback: string) =
            match root.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> fallback

        let intOpt (name: string) =
            match root.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
            | _ -> None

        let includes = stringList "include"

        if List.isEmpty includes then
            failwith "staticcorpus.json: 'include' must list at least one glob pattern (e.g. [\"**/*.md\"])."

        let output = stringOr "output" ""

        if String.IsNullOrWhiteSpace output then
            failwith "staticcorpus.json: 'output' (the .scidx path) is required."

        {
            Include = includes
            Exclude = stringList "exclude"
            EmbeddingProvider = stringOr "embeddingProvider" "hashing"
            Model = stringOr "model" ""
            Dimensions = intOpt "dimensions"
            MaxChunkChars =
                match intOpt "maxChunkChars" with
                | Some n when n > 0 -> n
                | _ -> Chunker.DefaultMaxChunkChars
            Output = output
            BaseDir =
                let b = stringOr "baseDir" ""

                if String.IsNullOrWhiteSpace b then
                    configDir
                else
                    Path.Combine(configDir, b)
        }

    // ── Globbing ───────────────────────────────────────────────────

    let private globToRegex (glob: string) : Regex =
        let sb = StringBuilder()
        sb.Append "^" |> ignore
        let mutable i = 0

        while i < glob.Length do
            let c = glob[i]

            if c = '*' && i + 1 < glob.Length && glob[i + 1] = '*' then
                // `**/` matches any number of directories (including none);
                // a bare `**` matches anything.
                if i + 2 < glob.Length && glob[i + 2] = '/' then
                    sb.Append "(.*/)?" |> ignore
                    i <- i + 3
                else
                    sb.Append ".*" |> ignore
                    i <- i + 2
            elif c = '*' then
                sb.Append "[^/]*" |> ignore
                i <- i + 1
            elif c = '?' then
                sb.Append "[^/]" |> ignore
                i <- i + 1
            else
                sb.Append(Regex.Escape(string c)) |> ignore
                i <- i + 1

        sb.Append "$" |> ignore
        Regex(sb.ToString(), RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    let private normaliseRel (baseDir: string) (fullPath: string) : string =
        let rel = Path.GetRelativePath(baseDir, fullPath)
        rel.Replace('\\', '/')

    /// Enumerate the include set minus excludes, as `/`-separated paths
    /// relative to `BaseDir`, in a stable sorted order (determinism).
    let enumerateFiles (config: PackConfig) : string list =
        if not (Directory.Exists config.BaseDir) then
            []
        else
            let includeRes = config.Include |> List.map globToRegex
            let excludeRes = config.Exclude |> List.map globToRegex

            Directory.EnumerateFiles(config.BaseDir, "*", SearchOption.AllDirectories)
            |> Seq.map (normaliseRel config.BaseDir)
            |> Seq.filter (fun rel ->
                includeRes |> List.exists (fun r -> r.IsMatch rel)
                && not (excludeRes |> List.exists (fun r -> r.IsMatch rel)))
            |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> List.ofSeq

    // ── Embedding disk cache ───────────────────────────────────────

    let private sha256Hex (s: string) : string =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes s)
        |> Array.map (sprintf "%02x")
        |> String.concat ""

    let private readVecCache (path: string) : float32[] option =
        try
            if File.Exists path then
                let bytes = File.ReadAllBytes path
                let v = Array.zeroCreate<float32> (bytes.Length / 4)
                Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4)
                Some v
            else
                None
        with _ ->
            None

    let private writeVecCache (path: string) (v: float32[]) =
        try
            let bytes = Array.zeroCreate<byte> (v.Length * 4)
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length)
            File.WriteAllBytes(path, bytes)
        with _ ->
            () // cache is best-effort; a write failure just costs a re-embed next time

    // ── Pack ───────────────────────────────────────────────────────

    /// The text embedded for a chunk — the heading path plus the body, so the
    /// vector captures section context, not just the raw prose.
    let embeddingText (rc: RawChunk) : string =
        let headings = String.concat "\n" rc.HeadingPath

        if String.IsNullOrEmpty headings then
            rc.Body
        else
            headings + "\n\n" + rc.Body

    /// Pack the configured corpus into a `StaticCorpus`. `builtUtc` is injected
    /// (not wall-clock) so the result is byte-reproducible; `cacheDir` (when
    /// `Some`) memoises embedding responses on disk keyed by (model, text).
    let buildCorpus
        (embedder: IEmbeddingProvider)
        (config: PackConfig)
        (builtUtc: DateTime)
        (cacheDir: string option)
        : Async<StaticCorpus> =
        async {
            let files = enumerateFiles config

            let rawChunks =
                files
                |> List.collect (fun rel ->
                    let full = Path.Combine(config.BaseDir, rel)
                    let md = File.ReadAllText full
                    Chunker.chunk rel config.MaxChunkChars md)

            cacheDir |> Option.iter (fun d -> Directory.CreateDirectory d |> ignore)

            let embedCached (text: string) : Async<float32[]> = async {
                let cachePath =
                    cacheDir
                    |> Option.map (fun d ->
                        Path.Combine(d, sprintf "%s.vec" (sha256Hex (embedder.ModelId + "\u0000" + text))))

                match cachePath |> Option.bind readVecCache with
                | Some v -> return v
                | None ->
                    let! v = embedder.GenerateEmbedding text
                    cachePath |> Option.iter (fun p -> writeVecCache p v)
                    return v
            }

            let! docChunks =
                rawChunks
                |> List.map (fun rc -> async {
                    let! embedding = embedCached (embeddingText rc)

                    return {
                        Id = rc.Id
                        Source = rc.Source
                        HeadingPath = rc.HeadingPath
                        Body = rc.Body
                        Embedding = embedding
                        Metadata = rc.Metadata
                    }
                })
                |> Async.Sequential

            return {
                Chunks = docChunks
                EmbeddingModel = embedder.ModelId
                EmbeddingDimensions = embedder.Dimensions
                BuiltUtc = builtUtc
                PackerVersion = PackerVersion
            }
        }

    let private resolveOutputPath (config: PackConfig) : string =
        if Path.IsPathRooted config.Output then
            config.Output
        else
            Path.Combine(config.BaseDir, config.Output)

    /// Build the corpus and write the `.scidx` file. Returns the chunk count.
    /// Creates the output directory if needed. Always (re)writes — the
    /// incremental skip lives in `packIncremental`.
    let pack
        (embedder: IEmbeddingProvider)
        (config: PackConfig)
        (builtUtc: DateTime)
        (cacheDir: string option)
        : Async<int> =
        async {
            let! corpus = buildCorpus embedder config builtUtc cacheDir
            let outputPath = resolveOutputPath config

            Path.GetDirectoryName outputPath
            |> Option.ofObj
            |> Option.iter (fun d -> Directory.CreateDirectory d |> ignore)

            File.WriteAllBytes(outputPath, Serialization.serialize corpus)
            return corpus.Chunks.Length
        }

    // ── Incremental rebuild (63.F) ─────────────────────────────────

    /// Content hash over the packer version + the pack-affecting config +
    /// every input file's relative path and bytes. Independent of file
    /// timestamps (git doesn't preserve mtimes), so a fresh checkout + build
    /// still detects "nothing changed" correctly. Written alongside the index
    /// as `<output>.inputs`; a matching sidecar means the pack can be skipped.
    let computeInputHash (config: PackConfig) : string =
        use sha = SHA256.Create()
        use buffer = new MemoryStream()

        let writeLine (s: string) =
            let bytes = Encoding.UTF8.GetBytes(s + "\n")
            buffer.Write(bytes, 0, bytes.Length)

        writeLine (sprintf "packer=%s" PackerVersion)
        writeLine (sprintf "provider=%s" config.EmbeddingProvider)
        writeLine (sprintf "model=%s" config.Model)
        writeLine (sprintf "dimensions=%A" config.Dimensions)
        writeLine (sprintf "maxChunkChars=%d" config.MaxChunkChars)

        for rel in enumerateFiles config do
            writeLine (sprintf "file=%s" rel)
            let bytes = File.ReadAllBytes(Path.Combine(config.BaseDir, rel))
            buffer.Write(bytes, 0, bytes.Length)
            writeLine ""

        buffer.Position <- 0L
        sha.ComputeHash buffer |> Array.map (sprintf "%02x") |> String.concat ""

    /// Outcome of an incremental pack.
    type PackOutcome =
        | Packed of chunkCount: int
        | Skipped

    /// Pack only when the inputs changed. If the output `.scidx` and its
    /// `<output>.inputs` sidecar both exist and the sidecar matches the
    /// current input hash, the pack is skipped (a no-op — the second
    /// `dotnet build` in a row does no work). Otherwise it packs and refreshes
    /// the sidecar.
    let packIncremental
        (embedder: IEmbeddingProvider)
        (config: PackConfig)
        (builtUtc: DateTime)
        (cacheDir: string option)
        : Async<PackOutcome> =
        async {
            let outputPath = resolveOutputPath config
            let sidecarPath = outputPath + ".inputs"
            let inputHash = computeInputHash config

            let upToDate =
                File.Exists outputPath
                && File.Exists sidecarPath
                && File.ReadAllText(sidecarPath).Trim() = inputHash

            if upToDate then
                return Skipped
            else
                let! count = pack embedder config builtUtc cacheDir
                File.WriteAllText(sidecarPath, inputHash)
                return Packed count
        }
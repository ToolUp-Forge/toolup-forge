// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.StaticCorpus.Program

open System
open System.IO
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.RAG.StaticCorpus

// ─── `toolup-rag pack-docs` CLI ──────────────────────────────────
//
// Reads a `staticcorpus.json` config, chunks + embeds the configured docs,
// and writes the `.scidx` index. Exit codes (per Phase 63.E):
//   0 — clean
//   1 — config invalid / missing / bad arguments
//   2 — embedding-provider error
//   3 — output write failure

[<Literal>]
let private ExitOk = 0

[<Literal>]
let private ExitConfigInvalid = 1

[<Literal>]
let private ExitEmbeddingError = 2

[<Literal>]
let private ExitWriteError = 3

/// Resolve the configured embedding provider. `"hashing"` (default) is the
/// offline, deterministic, dependency-free provider — the same one a runtime
/// deployment composes, so pack-time and query-time embeddings share a space.
/// `"openai"` calls the OpenAI embeddings API (key read from `ISecretStore` at
/// `_platform / "openai-api-key"`, supplied here by `EnvironmentSecretStore`).
let private resolveEmbedder (config: Packer.PackConfig) : IEmbeddingProvider =
    match config.EmbeddingProvider.Trim().ToLowerInvariant() with
    | ""
    | "hashing" ->
        config.Dimensions
        |> Option.defaultValue HashingEmbeddingProvider.DefaultDimensions
        |> HashingEmbeddingProvider.create
    | "openai" ->
        let secretStore =
            EnvironmentSecretStore.EnvironmentSecretStore() :> ToolUp.Platform.Secrets.ISecretStore

        if String.IsNullOrWhiteSpace config.Model then
            OpenAIEmbeddingProvider.create secretStore
        else
            // Dimensions must be supplied for a non-default model.
            match config.Dimensions with
            | Some d -> OpenAIEmbeddingProvider.createWithModel secretStore config.Model d
            | None -> OpenAIEmbeddingProvider.create secretStore
    | other -> failwithf "unknown embeddingProvider '%s' (supported: hashing, openai)" other

/// Parse the SOURCE_DATE_EPOCH reproducible-builds env var (Unix seconds) into
/// the corpus `BuiltUtc`. Absent ⇒ the Unix epoch, so the `.scidx` is
/// byte-reproducible by default (a wall-clock stamp would defeat determinism).
let private resolveBuiltUtc () : DateTime =
    match Environment.GetEnvironmentVariable "SOURCE_DATE_EPOCH" with
    | null
    | "" -> DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    | s ->
        match Int64.TryParse s with
        | true, secs -> DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
        | _ -> DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let private usage () =
    eprintfn "usage: toolup-rag pack-docs [--config <staticcorpus.json>]"
    eprintfn "       (default config path: ./staticcorpus.json)"

/// Locate `--config <path>` (or a bare positional path) in the args after the
/// `pack-docs` verb; default `staticcorpus.json`.
let private configPathOf (args: string list) : string =
    let rec find =
        function
        | "--config" :: p :: _ -> Some p
        | _ :: rest -> find rest
        | [] -> None

    match find args with
    | Some p -> p
    | None ->
        match args |> List.filter (fun a -> not (a.StartsWith "--")) with
        | p :: _ -> p
        | [] -> "staticcorpus.json"

let private runPackDocs (args: string list) : int =
    let configPath = configPathOf args

    // ── Config ──
    let config =
        try
            if not (File.Exists configPath) then
                eprintfn "pack-docs: config file not found: %s" configPath
                None
            else
                let dir = Path.GetDirectoryName(Path.GetFullPath configPath)
                Some(Packer.parseConfig dir (File.ReadAllText configPath))
        with ex ->
            eprintfn "pack-docs: invalid config: %s" ex.Message
            None

    match config with
    | None -> ExitConfigInvalid
    | Some config ->
        // ── Embedder ──
        let embedder =
            try
                Ok(resolveEmbedder config)
            with ex ->
                eprintfn "pack-docs: %s" ex.Message
                Error ExitConfigInvalid

        match embedder with
        | Error code -> code
        | Ok embedder ->
            let builtUtc = resolveBuiltUtc ()
            let cacheDir = Some(Path.Combine(config.BaseDir, ".scidx-cache"))

            // ── Pack (incremental — no-op when inputs are unchanged) ──
            try
                match
                    Packer.packIncremental embedder config builtUtc cacheDir
                    |> Async.RunSynchronously
                with
                | Packer.Skipped ->
                    printfn "pack-docs: %s is up to date (inputs unchanged) — nothing to do" config.Output
                | Packer.Packed count ->
                    printfn
                        "pack-docs: wrote %d chunks to %s (provider=%s model=%s)"
                        count
                        config.Output
                        embedder.ProviderId
                        embedder.ModelId

                ExitOk
            with
            | :? IOException as ex ->
                eprintfn "pack-docs: output write failure: %s" ex.Message
                ExitWriteError
            | :? UnauthorizedAccessException as ex ->
                eprintfn "pack-docs: output write failure: %s" ex.Message
                ExitWriteError
            | ex ->
                // Any other failure during embedding/pack is treated as an
                // embedding-provider error (the dominant failure mode — a
                // bad API key, rate limit, or network fault).
                eprintfn "pack-docs: embedding/pack error: %s" ex.Message
                ExitEmbeddingError

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "pack-docs" :: rest -> runPackDocs rest
    | _ ->
        usage ()
        ExitConfigInvalid
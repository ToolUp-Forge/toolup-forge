module ToolUp.Platform.Tests.InProcess.EmbeddingProviderEnvTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.IEmbeddingProvider

// ─── Phase 671 — `EmbeddingProviderEnv.fromEnv` ──────────────────────
//
// The selector's whole contract is one env var and four arms, and the
// arm that matters most is the one that does nothing: an UNSET
// `TOOLUP_EMBEDDING_PROVIDER` must leave an upgrading deployment
// byte-for-byte as it was (GP 11). "Byte-for-byte" is not an assertion
// one can wave at — so it is pinned three ways here: the returned
// provider is REFERENCE-EQUAL to what the composition root built, the
// logger records ZERO lines, and no resolver in the list is invoked.
//
// Every test snapshots and restores the process-global env var in a
// try / finally, and the whole list is wired into Program.fs's
// `testSequencedGroup "env-mutating-config-validators"` (Phase 653) so
// it can never run concurrently with another env-mutating pack — the
// group, not a per-list `testSequenced`, is what serialises across
// lists.

let private withEnv (name: string) (value: string option) (body: unit -> 'a) : 'a =
    let prior = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

/// Set the whole `TOOLUP_EMBEDDING_*` cluster for the duration of
/// `body`, restoring every member afterwards. Any member not named is
/// explicitly CLEARED rather than left alone — a host that happens to
/// export one of these would otherwise silently change what a test
/// measures, and the tests that assert on defaults would be the ones to
/// go quiet about it.
let private withEmbeddingEnv (settings: (string * string option) list) (body: unit -> 'a) : 'a =
    let cluster = [
        ConfigKeys.Names.embeddingProvider
        ConfigKeys.Names.embeddingModel
        ConfigKeys.Names.embeddingDimensions
        ConfigKeys.Names.embeddingBatchSize
    ]

    let priors = cluster |> List.map (fun n -> n, Environment.GetEnvironmentVariable n)

    try
        for name in cluster do
            let value =
                settings |> List.tryPick (fun (n, v) -> if n = name then Some v else None)

            Environment.SetEnvironmentVariable(name, Option.toObj (Option.flatten value))

        body ()
    finally
        for name, prior in priors do
            Environment.SetEnvironmentVariable(name, prior)

/// The OpenAI companion takes an `ISecretStore` but does not touch it at
/// construction — the key is read per embed call. This stub proves that:
/// every member fails loudly, so a construction path that reached for a
/// secret would be a test failure rather than a silent pass.
let private noSecrets: ToolUp.Platform.Secrets.ISecretStore =
    { new ToolUp.Platform.Secrets.ISecretStore with
        member _.GetSecret(_scopeId, _key) =
            failwith "construction must not read a secret — the key is resolved per embed call"

        member _.SetSecret(_scopeId, _key, _value) =
            failwith "construction must not write a secret"

        member _.DeleteSecret(_scopeId, _key) =
            failwith "construction must not delete a secret"

        member _.ListKeys(_scopeId) =
            failwith "construction must not enumerate secrets"
    }

/// Run `body`, returning the exception it raised. Fails the test when it
/// does not raise — `Expect.throws` returns unit, and these cases assert
/// on the refusal MESSAGE (it has to name the variable an operator must
/// fix, or the refusal is not actionable).
let private expectThrows (why: string) (body: unit -> unit) : exn =
    let raised =
        try
            body ()
            None
        with ex ->
            Some ex

    match raised with
    | Some ex -> ex
    | None -> failtest why

/// Records every line the helper logs, at every level, in order. The
/// unset arm's guarantee is that this stays empty.
type private RecordingLogger() =
    let lines = ConcurrentQueue<string>()

    member _.Lines = lines |> Seq.toList

    interface ILogger with
        member _.Debug msg = lines.Enqueue $"DEBUG {msg}"
        member _.Info msg = lines.Enqueue $"INFO {msg}"
        member _.Warn msg = lines.Enqueue $"WARN {msg}"

        member _.Error(msg, _ex) = lines.Enqueue $"ERROR {msg}"

/// A distinguishable stub — `ProviderId` doubles as the identity the
/// assertions read, so a wrong arm is named in the failure rather than
/// merely counted.
let private stubProvider (id: string) : IEmbeddingProvider =
    { new IEmbeddingProvider with
        member _.Dimensions = 8
        member _.ProviderId = id
        member _.ModelId = id + "-model"
        member _.GenerateEmbedding(_text: string) = async { return Array.zeroCreate<float32> 8 }

        member _.GenerateEmbeddings(texts: string seq) = async {
            return texts |> Seq.map (fun _ -> Array.zeroCreate<float32> 8) |> Seq.toArray
        }
    }

let private resolver
    (name: string)
    (result: IEmbeddingProvider option)
    : EmbeddingProviderEnv.EmbeddingProviderResolver =
    {
        Name = name
        Resolve = fun () -> result
    }

[<Tests>]
let tests =
    testList "EmbeddingProviderEnv.fromEnv (Phase 671)" [

        // ── The GP 11 arm ────────────────────────────────────────────
        test "unset cluster returns the composition root's own provider, unchanged" {
            let fallback = stubProvider "composition-root"
            let logger = RecordingLogger()

            let resolved =
                withEnv ConfigKeys.Names.embeddingProvider None (fun () ->
                    EmbeddingProviderEnv.fromEnv logger [ resolver "openai" (Some(stubProvider "openai")) ] (fun () ->
                        fallback))

            Expect.isTrue
                (Object.ReferenceEquals(resolved, fallback))
                "an unset TOOLUP_EMBEDDING_PROVIDER must return the very provider the composition root built — not an equivalent one"

            Expect.isEmpty
                logger.Lines
                "an unset cluster must log NOTHING: the startup-log diff for a deployment that adopts the helper and changes no configuration is the Phase 11.G env-var contract"
        }

        test "unset cluster invokes no resolver" {
            let mutable resolverCalls = 0
            let logger = RecordingLogger()

            let counting: EmbeddingProviderEnv.EmbeddingProviderResolver = {
                Name = "openai"
                Resolve =
                    fun () ->
                        resolverCalls <- resolverCalls + 1
                        Some(stubProvider "openai")
            }

            withEnv ConfigKeys.Names.embeddingProvider None (fun () ->
                EmbeddingProviderEnv.fromEnv logger [ counting ] (fun () -> stubProvider "composition-root"))
            |> ignore

            Expect.equal
                resolverCalls
                0
                "a deployment that never sets the cluster must pay nothing for it (GP 13) — no resolver may run"
        }

        test "an empty-string value reads as unset, exactly as elsewhere in the seam" {
            let fallback = stubProvider "composition-root"
            let logger = RecordingLogger()

            let resolved =
                withEnv ConfigKeys.Names.embeddingProvider (Some "") (fun () ->
                    EmbeddingProviderEnv.fromEnv logger [ resolver "openai" (Some(stubProvider "openai")) ] (fun () ->
                        fallback))

            Expect.isTrue (Object.ReferenceEquals(resolved, fallback)) "empty must be unset, not an unrecognised value"
            Expect.isEmpty logger.Lines "an empty value is the unset arm, so it must be silent too"
        }

        // ── Selection ────────────────────────────────────────────────
        test "a matched resolver supplies the provider" {
            let selected = stubProvider "openai"
            let logger = RecordingLogger()

            let resolved =
                withEnv ConfigKeys.Names.embeddingProvider (Some "openai") (fun () ->
                    EmbeddingProviderEnv.fromEnv
                        logger
                        [
                            resolver "local" (Some(stubProvider "local"))
                            resolver "openai" (Some selected)
                        ]
                        (fun () -> stubProvider "composition-root"))

            Expect.equal resolved.ProviderId "openai" "the named resolver's provider must be the one returned"

            Expect.isTrue
                (logger.Lines
                 |> List.exists (fun l -> l.StartsWith "INFO Embedding provider: openai"))
                $"the selected provider must be announced; logged: %A{logger.Lines}"
        }

        test "the announcement names the model and dimensionality" {
            let logger = RecordingLogger()

            withEnv ConfigKeys.Names.embeddingProvider (Some "openai") (fun () ->
                EmbeddingProviderEnv.fromEnv logger [ resolver "openai" (Some(stubProvider "openai")) ] (fun () ->
                    stubProvider "composition-root"))
            |> ignore

            let announcement = logger.Lines |> List.tryFind _.StartsWith("INFO ")

            match announcement with
            | None -> failtest $"expected an Info announcement; logged: %A{logger.Lines}"
            | Some line ->
                Expect.stringContains line "openai-model" "the model id belongs in the line an operator reads"

                Expect.stringContains
                    line
                    "8 dimensions"
                    "the dimensionality belongs in it too — a silent mismatch is the corpus-corruption failure this cluster can cause"
        }

        // The resolver NAME is deliberately mixed-case here. The helper
        // lower-cases the env value before matching, so a lower-cased
        // resolver name would match under an ordinal comparison too and
        // the case-insensitivity would go unmeasured — which is exactly
        // what a first draft of this test did.
        test "matching is case-insensitive on BOTH sides" {
            let logger = RecordingLogger()

            let resolved =
                withEnv ConfigKeys.Names.embeddingProvider (Some "OpenAI") (fun () ->
                    EmbeddingProviderEnv.fromEnv logger [ resolver "OpenAI" (Some(stubProvider "openai")) ] (fun () ->
                        stubProvider "composition-root"))

            Expect.equal
                resolved.ProviderId
                "openai"
                "a resolver declared with a capitalised Name must still match a TOOLUP_EMBEDDING_PROVIDER value in any casing"
        }

        // ── Fail-soft arms ───────────────────────────────────────────
        test "a resolver that declines falls back with a warning naming the selection" {
            let fallback = stubProvider "composition-root"
            let logger = RecordingLogger()

            let resolved =
                withEnv ConfigKeys.Names.embeddingProvider (Some "openai") (fun () ->
                    EmbeddingProviderEnv.fromEnv logger [ resolver "openai" None ] (fun () -> fallback))

            Expect.isTrue
                (Object.ReferenceEquals(resolved, fallback))
                "a declining resolver must fall back rather than boot with no embedder"

            let warning = logger.Lines |> List.tryFind _.StartsWith("WARN ")

            match warning with
            | None ->
                failtest $"a silent fallback is the failure this warning exists to prevent; logged: %A{logger.Lines}"
            | Some line ->
                Expect.stringContains line "openai" "the warning must name the selection that could not be built"
        }

        test "an unrecognised value falls back and names the recognised ones" {
            let fallback = stubProvider "composition-root"
            let logger = RecordingLogger()

            let resolved =
                withEnv ConfigKeys.Names.embeddingProvider (Some "cohere") (fun () ->
                    EmbeddingProviderEnv.fromEnv
                        logger
                        [
                            resolver "local" (Some(stubProvider "local"))
                            resolver "openai" (Some(stubProvider "openai"))
                        ]
                        (fun () -> fallback))

            Expect.isTrue (Object.ReferenceEquals(resolved, fallback)) "an unrecognised value must fall back"

            let warning =
                logger.Lines |> List.tryFind _.StartsWith("WARN ") |> Option.defaultValue ""

            Expect.stringContains warning "cohere" "the warning must quote the value that was not recognised"
            Expect.stringContains warning "local, openai" "and list what this deployment does recognise"
        }

        test "an unrecognised value with no resolvers wired says so rather than listing nothing" {
            let logger = RecordingLogger()

            withEnv ConfigKeys.Names.embeddingProvider (Some "openai") (fun () ->
                EmbeddingProviderEnv.fromEnv logger [] (fun () -> stubProvider "composition-root"))
            |> ignore

            let warning =
                logger.Lines |> List.tryFind _.StartsWith("WARN ") |> Option.defaultValue ""

            Expect.stringContains
                warning
                "no embedding-provider resolvers"
                $"an empty valid-values list reads as a bug in the SDK rather than a gap in the deployment's wiring; logged: %A{logger.Lines}"
        }

        // ── The companion resolver entry points ──────────────────────
        test "LocalEmbeddingProvider.fromEnv resolves without an IBlobStorage" {
            match LocalEmbeddingProvider.fromEnv None with
            | None -> failtest "the local companion has nothing to decline on — it must always resolve"
            | Some provider ->
                Expect.equal provider.ProviderId "local" "the local companion's provider id"
                Expect.equal provider.Dimensions 512 "the local companion's fixed hashed feature space"
        }

        // The OpenAI companion's `fromEnv` is where the cluster's
        // parameters are actually consumed, and where getting a
        // dimension wrong is unrecoverable rather than merely wrong: a
        // mis-sized vector is indexed under a matching EmbeddingVersion
        // stamp, so the re-embedding pass never fires to repair it.
        // Nothing below reaches the network — construction only.
        test "OpenAI fromEnv with an unset cluster takes the default model at its native size" {
            let resolved =
                withEmbeddingEnv [] (fun () -> OpenAIEmbeddingProvider.fromEnv noSecrets)

            match resolved with
            | None -> failtest "the OpenAI companion has no incomplete state to decline on"
            | Some provider ->
                Expect.equal provider.ModelId OpenAIEmbeddingProvider.defaultModel "the documented default model"
                Expect.equal provider.Dimensions 1536 "its native output size"
        }

        test "OpenAI fromEnv defaults a KNOWN model's dimensionality to that model's native size" {
            let resolved =
                withEmbeddingEnv [ ConfigKeys.Names.embeddingModel, Some "text-embedding-3-large" ] (fun () ->
                    OpenAIEmbeddingProvider.fromEnv noSecrets)

            match resolved with
            | None -> failtest "expected a provider"
            | Some provider ->
                Expect.equal provider.ModelId "text-embedding-3-large" "the selected model"

                Expect.equal
                    provider.Dimensions
                    3072
                    "3-large emits 3072 — defaulting to 1536 here would index half-length vectors under a matching version stamp"
        }

        test "OpenAI fromEnv REFUSES an unknown model with no declared dimensionality" {
            let thrown =
                expectThrows "an unknown model with no dimensionality must refuse startup, not guess" (fun () ->
                    withEmbeddingEnv
                        [ ConfigKeys.Names.embeddingModel, Some "some-model-shipped-next-year" ]
                        (fun () -> OpenAIEmbeddingProvider.fromEnv noSecrets)
                    |> ignore)

            Expect.stringContains
                thrown.Message
                "TOOLUP_EMBEDDING_DIMENSIONS"
                "the refusal must name the variable the operator has to set"
        }

        test "OpenAI fromEnv accepts an unknown model once its dimensionality is declared" {
            let resolved =
                withEmbeddingEnv
                    [
                        ConfigKeys.Names.embeddingModel, Some "some-model-shipped-next-year"
                        ConfigKeys.Names.embeddingDimensions, Some "2048"
                    ]
                    (fun () -> OpenAIEmbeddingProvider.fromEnv noSecrets)

            match resolved with
            | None -> failtest "expected a provider"
            | Some provider ->
                Expect.equal
                    provider.Dimensions
                    2048
                    "a model released after this build is the documented extensibility path"
        }

        test "OpenAI fromEnv REFUSES a dimensionality that contradicts a known model" {
            Expect.throws
                (fun () ->
                    withEmbeddingEnv
                        [
                            ConfigKeys.Names.embeddingModel, Some "text-embedding-3-small"
                            ConfigKeys.Names.embeddingDimensions, Some "512"
                        ]
                        (fun () -> OpenAIEmbeddingProvider.fromEnv noSecrets)
                    |> ignore)
                "the env path must reach the same validateDimensions guard every other construction path does"
        }

        test "OpenAI fromEnv REFUSES a non-integer dimensionality rather than reading it as unset" {
            let thrown =
                expectThrows "a typo must not silently fall through to the default" (fun () ->
                    withEmbeddingEnv [ ConfigKeys.Names.embeddingDimensions, Some "fifteen-thirty-six" ] (fun () ->
                        OpenAIEmbeddingProvider.fromEnv noSecrets)
                    |> ignore)

            Expect.stringContains
                thrown.Message
                "TOOLUP_EMBEDDING_DIMENSIONS"
                "the refusal must name the variable that could not be parsed"
        }
    ]
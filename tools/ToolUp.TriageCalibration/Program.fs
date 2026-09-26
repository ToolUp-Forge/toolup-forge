// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 663 — the calibration run's command line.
///
///   dotnet run --project tools/ToolUp.TriageCalibration -- \
///       --corpus <case-file.json> --out <result.json> --provider claude [--model <id>] \
///       [--timeout-ms <n>] [--max-chars <n>]
///
/// Exit codes: 0 the result file was written; 2 the run could not start
/// (bad arguments, an unreadable case file, a provider that cannot be
/// built). A case whose call failed or timed out is a ROW in the result,
/// never a non-zero exit — the tool measures, it does not gate.
module ToolUp.TriageCalibration.Program

open System
open System.IO
open ToolUp.Platform.AI
open ToolUp.AI.FastPathTriageResolver
open ToolUp.TriageCalibration.Calibration

type CliArgs = {
    Corpus: string option
    Out: string option
    Provider: string option
    Model: string option
    TimeoutMs: int
    MaxChars: int
}

let private usage =
    String.concat "\n" [
        "usage: ToolUp.TriageCalibration --corpus <case-file.json> --out <result.json>"
        "                                --provider claude|openai|gemini [--model <id>]"
        "                                [--timeout-ms <n>] [--max-chars <n>]"
        ""
        "The provider's key is read from its usual variable: ANTHROPIC_API_KEY,"
        "OPENAI_API_KEY or GEMINI_API_KEY. Schemas: tools/ToolUp.TriageCalibration/README.md."
    ]

let rec private parse (acc: CliArgs) (args: string list) : Result<CliArgs, string> =
    let positiveInt (flag: string) (v: string) (k: int -> CliArgs) rest =
        match Int32.TryParse v with
        | true, n when n > 0 -> parse (k n) rest
        | _ -> Error $"{flag} needs a positive integer, got '{v}'"

    match args with
    | [] -> Ok acc
    | "--corpus" :: v :: rest -> parse { acc with Corpus = Some v } rest
    | "--out" :: v :: rest -> parse { acc with Out = Some v } rest
    | "--provider" :: v :: rest -> parse { acc with Provider = Some v } rest
    | "--model" :: v :: rest -> parse { acc with Model = Some v } rest
    | "--timeout-ms" :: v :: rest -> positiveInt "--timeout-ms" v (fun n -> { acc with TimeoutMs = n }) rest
    | "--max-chars" :: v :: rest -> positiveInt "--max-chars" v (fun n -> { acc with MaxChars = n }) rest
    | flag :: _ -> Error $"unrecognised or incomplete argument '{flag}'"

/// The live connectors this tool can build, keyed by `--provider`.
let private providers: (string * string * (string -> IAIProvider) * (string -> string -> IAIProvider)) list = [
    "claude", "ANTHROPIC_API_KEY", ClaudeAIProvider.createWithApiKey, ClaudeAIProvider.createWithApiKeyAndModel
    "openai", "OPENAI_API_KEY", OpenAIProvider.createWithApiKey, OpenAIProvider.createWithApiKeyAndModel
    "gemini", "GEMINI_API_KEY", GeminiAIProvider.createWithApiKey, GeminiAIProvider.createWithApiKeyAndModel
]

/// Build the provider and the per-call options. `--model` builds the
/// connector AT that model, so every call is served on it (`configured`).
/// Without it, a connector that declares a `TriageModelId` is asked for
/// that model through the per-call override — the route the resolver
/// itself takes when no separate triage provider is composed.
let resolveProvider (selector: string) (model: string option) : Result<IAIProvider * AIProviderCallOptions, string> =
    match
        providers
        |> List.tryFind (fun (id, _, _, _) -> id = selector.ToLowerInvariant())
    with
    | None ->
        let known = providers |> List.map (fun (id, _, _, _) -> id) |> String.concat ", "
        Error $"unknown provider '{selector}' (known: {known})"
    | Some(_, keyVar, create, createWithModel) ->
        match Environment.GetEnvironmentVariable keyVar with
        | null
        | "" -> Error $"--provider {selector} needs {keyVar} set"
        | key ->
            match model with
            | Some m -> Ok(createWithModel key m, AIProviderCallOptions.none)
            | None ->
                let provider = create key

                let options =
                    match provider.Capabilities.TriageModelId with
                    | Some triageModel -> AIProviderCallOptions.forModel triageModel
                    | None -> AIProviderCallOptions.none

                Ok(provider, options)

[<EntryPoint>]
let main argv =
    let defaults = {
        Corpus = None
        Out = None
        Provider = None
        Model = None
        TimeoutMs = FastPathTriageConfig.DefaultTimeoutMs
        MaxChars = FastPathTriageConfig.DefaultMaxInstructionChars
    }

    let fail (message: string) =
        eprintfn "error: %s" message
        eprintfn "%s" usage
        2

    match parse defaults (List.ofArray argv) with
    | Error e -> fail e
    | Ok args ->
        match args.Corpus, args.Out, args.Provider with
        | None, _, _ -> fail "--corpus is required"
        | _, None, _ -> fail "--out is required"
        | _, _, None -> fail "--provider is required"
        | Some corpus, Some out, Some selector ->
            match loadCaseFile corpus, resolveProvider selector args.Model with
            | Error e, _
            | _, Error e -> fail e
            | Ok(cases, sha), Ok(provider, options) ->
                let settings = {
                    Floors = defaultFloors
                    MaxInstructionChars = args.MaxChars
                    TimeoutMs = args.TimeoutMs
                }

                if not provider.Capabilities.SupportsTriage then
                    eprintfn
                        "note: %s/%s does not declare SupportsTriage; the resolver would never route triage to it. Calibrating anyway."
                        provider.Capabilities.ProviderName
                        provider.Capabilities.Model

                let result = run provider options settings sha cases |> Async.RunSynchronously

                match Path.GetDirectoryName(Path.GetFullPath out) with
                | null
                | "" -> ()
                | dir -> Directory.CreateDirectory dir |> ignore

                File.WriteAllText(out, encodeResult result)

                let s = result.Summary

                printfn
                    "%d case(s): %d refused by the pre-filter, %d model call(s) (%d timeout, %d provider error, %d unparseable)"
                    s.Cases
                    s.Ineligible
                    s.ModelCalls
                    s.Timeouts
                    s.ProviderErrors
                    s.Unparseable

                for f in s.Floors do
                    let rate (r: float option) =
                        r |> Option.map (sprintf "%.3f") |> Option.defaultValue "-"

                    printfn
                        "  floor %.2f  hits=%d  resolve-rate=%s  false-fire-rate=%s"
                        f.Floor
                        f.Hits
                        (rate f.ResolveRate)
                        (rate f.FalseFireRate)

                printfn "wrote %s" out
                0
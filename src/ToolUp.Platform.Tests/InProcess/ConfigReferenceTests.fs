module ToolUp.Platform.Tests.InProcess.ConfigReferenceTests

open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform.ConfigKeys

// ─── Phase 214 — config-key registry coverage + reference-doc golden ──
//
// Three guarantees over the central `ConfigKeys` registry:
//
//   1. The committed `docs/reference/config-reference.md` is exactly what
//      `ReferenceDoc.render ConfigKeys.all` produces — so the doc is
//      regenerable and never hand-maintained. Set
//      `TOOLUP_REGEN_CONFIG_REFERENCE=1` to rewrite it instead of
//      comparing (mirrors the `TOOLUP_APPROVE_API` idiom).
//   2. Every env var the headline `*FromEnv` dispatch readers consult
//      carries a descriptor — a key without a descriptor fails here.
//   3. The registry itself is well-formed (unique names, non-empty
//      descriptions, all `TOOLUP_`-prefixed).

/// Repo root (toolup-forge): bin/<Config>/net10.0/<dll> → up 5.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private referenceDocPath () =
    Path.Combine(repoRoot (), "docs", "reference", "config-reference.md")

let private regenModeOn () =
    match Environment.GetEnvironmentVariable "TOOLUP_REGEN_CONFIG_REFERENCE" with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

let private normalise (s: string) = s.Replace("\r\n", "\n")

/// The `*FromEnv` dispatch readers whose env-var reads must each have a
/// descriptor. Scanning their source (rather than the whole 179-file
/// surface) keeps the gate stable while still catching the realistic
/// failure mode: a new env var added to a dispatch reader with no
/// registry entry.
let private dispatchReaderSources = [
    "src/ToolUp.Platform.Server/Server/Infra/BlobStorageFromEnv.fs"
    "src/ToolUp.Platform.Server/Server/Infra/SecretStoreFromEnv.fs"
    "src/ToolUp.Platform.Server/Auth/AuthProviderFromEnv.fs"
    "src/ToolUp.Platform.Server/Server/Infra/ConsoleLogger.fs"
]

let private envVarPattern = Regex("TOOLUP_[A-Z0-9_]+", RegexOptions.Compiled)

let tests =
    testList "ConfigReference" [
        testCase "config-reference.md matches the rendered registry (regenerable, covers every key)"
        <| fun _ ->
            let rendered = ReferenceDoc.render all
            let path = referenceDocPath ()

            if regenModeOn () then
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, rendered)
            else
                Expect.isTrue
                    (File.Exists path)
                    (sprintf "%s is missing. Generate it with `dev-scripts/generate-config-reference.ps1`." path)

                let committed = File.ReadAllText path |> normalise

                Expect.equal
                    committed
                    (normalise rendered)
                    "docs/reference/config-reference.md is stale. Regenerate with `dev-scripts/generate-config-reference.ps1`."

        testCase "every env var read by the *FromEnv dispatch readers has a descriptor"
        <| fun _ ->
            let registered = all |> List.map _.EnvVar |> Set.ofList
            let root = repoRoot ()

            let readVars =
                dispatchReaderSources
                |> List.collect (fun rel ->
                    let full = Path.Combine(root, rel)
                    Expect.isTrue (File.Exists full) (sprintf "reader source not found: %s" full)

                    File.ReadAllText full |> envVarPattern.Matches |> Seq.map _.Value |> List.ofSeq)
                |> Set.ofList

            let missing = Set.difference readVars registered

            Expect.isEmpty
                missing
                (sprintf
                    "These env vars are read by a *FromEnv reader but have no ConfigKeyDescriptor in ConfigKeys.all: %s"
                    (missing |> Set.toList |> String.concat ", "))

        testCase "registry is well-formed (unique, TOOLUP_-prefixed, described)"
        <| fun _ ->
            let names = all |> List.map _.EnvVar

            Expect.equal
                (List.length names)
                (names |> List.distinct |> List.length)
                "duplicate EnvVar in ConfigKeys.all"

            for d in all do
                Expect.isTrue (d.EnvVar.StartsWith "TOOLUP_") (sprintf "env var %s is not TOOLUP_-prefixed" d.EnvVar)

                Expect.isFalse
                    (String.IsNullOrWhiteSpace d.Description)
                    (sprintf "%s has an empty description" d.EnvVar)

                Expect.isFalse (String.IsNullOrWhiteSpace d.Category) (sprintf "%s has an empty category" d.EnvVar)
    ]
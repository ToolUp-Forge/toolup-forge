module ToolUp.Platform.Tests.InProcess.NeutralityTokens

// ─── Neutrality-guard token source (OSS boundary) ─────────────────────
//
// The OSS grep-guard tests assert that forge-public artefacts (the toy
// sample, the hosting-seam sources) reference no private vocabulary.
// The vocabulary list itself is private: hardcoding it here would
// publish, inside this public repo, exactly what the guard exists to
// keep out of it. The tests therefore load the list at run time from a
// non-public source:
//
//   1. `neutrality-tokens.local.txt` at the repo root — one token per
//      line; blank lines and `#`-prefixed comment lines are ignored.
//      The file is untracked and gitignored; each dev / CI machine
//      materialises it from its own private side.
//   2. The `TOOLUP_NEUTRALITY_TOKENS` environment variable —
//      semicolon-separated tokens (the CI-friendly channel), consulted
//      when the file is absent.
//
// When neither source is present, the guard tests SKIP LOUDLY (Expecto
// `skiptest` — reported as ignored with a message, never silently
// green). One public-safe canary token stays hardcoded and is always
// enforced, so the scanning mechanism itself remains provable in
// public CI even without the private list.
//
// Matching is case-insensitive: scanned contents and tokens are both
// lowered before the containment check.

open System
open System.IO
open Expecto

/// Public-safe canary — always enforced, even when no external token
/// source is present. Proves the guard mechanism runs in public CI.
[<Literal>]
let Canary = "PRIVATE-VOCAB-CANARY"

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    // bin/Debug/net10.0/ToolUp.Platform.Tests.dll → repo root
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// Repo-root location of the untracked local token file.
let tokenFilePath = Path.Combine(repoRoot (), "neutrality-tokens.local.txt")

let private parseLines (lines: string seq) =
    lines
    |> Seq.map _.Trim()
    |> Seq.filter (fun l -> l <> "" && not (l.StartsWith "#"))
    |> Seq.toList

/// The private token list, when a non-public source supplies one.
/// `None` means neither the local file nor the environment variable is
/// present (or both are empty) — guard tests must then skip loudly.
let externalTokens: string list option =
    if File.Exists tokenFilePath then
        match parseLines (File.ReadAllLines tokenFilePath) with
        | [] -> None
        | tokens -> Some tokens
    else
        match Environment.GetEnvironmentVariable "TOOLUP_NEUTRALITY_TOKENS" with
        | null
        | "" -> None
        | value ->
            (match parseLines (value.Split ';') with
             | [] -> None
             | tokens -> Some tokens)

/// Every token the guard scans for: the hardcoded canary plus whatever
/// the external source supplies.
let activeTokens: string list = Canary :: (externalTokens |> Option.defaultValue [])

/// The loud skip message emitted when no external token source exists.
let absentSourceMessage: string =
    sprintf
        "NEUTRALITY TOKEN SOURCE ABSENT — only the public-safe canary token was enforced. Supply the private banned-vocabulary list via '%s' (one token per line, '#' comments allowed) or the TOOLUP_NEUTRALITY_TOKENS environment variable (semicolon-separated) to run the full OSS-boundary guard. This test result is a SKIP, not a pass."
        tokenFilePath

/// Assert that `contents` carries none of the active tokens
/// (case-insensitive). `label` names the scanned artefact in failure
/// messages.
let assertNoBannedTokens (label: string) (contents: string) =
    let lowered = contents.ToLowerInvariant()

    for token in activeTokens do
        Expect.isFalse
            (lowered.Contains(token.ToLowerInvariant()))
            $"{label} must not reference the banned token '{token}' (GP 1 / open-core)"

/// Call at the end of a guard test body: when no external token source
/// is present, downgrade the (canary-only) run to a loud skip so the
/// guard never reports silently green without the private list.
let skipUnlessExternalSource () =
    match externalTokens with
    | Some _ -> ()
    | None -> Tests.skiptest absentSourceMessage
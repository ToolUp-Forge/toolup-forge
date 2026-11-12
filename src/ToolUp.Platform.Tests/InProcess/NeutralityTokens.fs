module ToolUp.Platform.Tests.InProcess.NeutralityTokens

// ─── Neutrality-guard token source (OSS boundary) ─────────────────────
//
// OSS-BOUNDARY-EXEMPT-FILE: this module IS the guard's denylist
// machinery — the marker literals below are assertion data, not a leak.
//
// The OSS grep-guard tests assert that forge-public artefacts (the toy
// sample, the hosting-seam sources, and — since Phase 477 — the whole
// publishable surface) reference no private vocabulary. The vocabulary
// list itself is private: hardcoding it here would publish, inside this
// public repo, exactly what the guard exists to keep out of it. The
// tests therefore load the list at run time from a non-public source:
//
//   1. `neutrality-tokens.local.txt` at the checkout root — one token
//      per line; blank lines and `#`-prefixed comment lines are ignored.
//      The file is untracked and gitignored; each dev / CI machine
//      materialises it from its own private side.
//   2. The same file at the primary checkout's root (the parent of
//      `git rev-parse --git-common-dir`), so every linked worktree of a
//      clone finds the one list its primary checkout holds.
//   3. The `TOOLUP_NEUTRALITY_TOKENS` environment variable —
//      semicolon-separated tokens (the CI-friendly channel), consulted
//      when neither file exists.
//
// When no source is present, the guard tests SKIP LOUDLY (Expecto
// `skiptest` — reported as ignored with a message, never silently
// green), naming every place they looked. Under the local gate
// (`TOOLUP_NEUTRALITY_REQUIRED=1`) the same case FAILS with the same
// message, so a landing cannot pass a guard it did not run. One
// public-safe canary token stays hardcoded and is always enforced, so
// the scanning mechanism itself remains provable in public CI even
// without the private list.
//
// ─── The matching rule (Phase 477 task C) ─────────────────────────────
//
// A contributor reading a failure needs to know why THIS text matched.
// The rule, in full:
//
//   * Matching is CASE-INSENSITIVE — `fuaran` and `Fuaran` both fire.
//   * A token whose first (last) character is alphanumeric or `_` must
//     sit on a WORD BOUNDARY at that end. So `Concord` does not fire
//     inside `concordance`, while `Fuaran` still fires inside
//     `fuaran.model-execution` because `.` is not a word character.
//     This is what keeps common English substrings from making the
//     gate ignorable — the failure mode the phase exists to avoid.
//   * Internal whitespace in a MULTI-WORD token matches any run of
//     whitespace, so a command phrase wrapped across two comment lines
//     is still caught. Multi-word phrase forms are therefore the
//     preferred denylist shape wherever the bare verb is also ordinary
//     English (`Cook` the command vs `cookbook` the forge doc genre).
//   * A token line beginning `re:` is a RAW .NET regex (still
//     case-insensitive) — the escape hatch for a boundary the default
//     rule gets wrong. The policy stays private; only the mechanism is
//     public.
//
// ─── Exemptions (Phase 477 task D) ────────────────────────────────────
//
// Some files legitimately carry a token: this module and the guard
// tests hold the vocabulary as assertion data, and the estate sanctions
// citing a PUBLIC, Apache-licensed specification by name and URL even
// when its repository slug shares a private layer's name. Both are
// declared in-tree by a stable marker comment, never by a path list
// the next rename silently breaks:
//
//   OSS-BOUNDARY-EXEMPT-FILE: <reason>   anywhere in the file → whole
//                                        file is out of scope
//   OSS-BOUNDARY-EXEMPT: <reason>        anywhere in a BLOCK — a run of
//                                        consecutive non-blank lines →
//                                        that block only
//
// The block, rather than the single line, is the unit because the things
// that legitimately carry a token come in blocks: a doc comment and the
// declaration under it, a markdown paragraph, the rows of a table. A
// per-line marker would have to be repeated on each row of a table it
// cannot be written into without breaking the rendering. A blank line
// ends the block, so the scope stays visible to the eye and a marker
// can never quietly cover a whole file — that is the other marker's
// job, and it says so in its name.
//
// A reason is expected on every marker: an exemption without one is
// indistinguishable from a leak someone silenced.

open System
open System.IO
open System.Text.RegularExpressions
open Expecto

/// Public-safe canary — always enforced, even when no external token
/// source is present. Proves the guard mechanism runs in public CI.
[<Literal>]
let Canary = ToolUp.Platform.Build.Neutrality.Canary

/// Marker that takes a block (a run of consecutive non-blank lines) out
/// of scope, wherever in the block it appears.
[<Literal>]
let ExemptLineMarker = "OSS-BOUNDARY-EXEMPT"

/// Marker that takes a whole file out of scope, wherever it appears.
[<Literal>]
let ExemptFileMarker = "OSS-BOUNDARY-EXEMPT-FILE"

/// Where a contributor reads the policy a failure cites.
[<Literal>]
let PolicyDoc = "docs/platform/open-core-boundary.md"

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    // bin/Debug/net10.0/ToolUp.Platform.Tests.dll → repo root
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// The forge repository root, resolved from the running test assembly.
let repoRootPath = repoRoot ()

/// The environment variable that carries the list when no token file exists.
[<Literal>]
let TokensVariable = "TOOLUP_NEUTRALITY_TOKENS"

/// The environment variable that makes an absent token source a failure
/// rather than a loud skip. The local gate sets it; a developer's run
/// leaves it unset.
[<Literal>]
let RequiredVariable = "TOOLUP_NEUTRALITY_REQUIRED"

/// The private token list, when a non-public source supplies one.
/// `None` means no token file and no environment variable supplies a
/// token — guard tests must then skip loudly, or fail under the gate.
let externalTokens: string list option =
    ToolUp.Platform.Build.Neutrality.externalTokens repoRootPath TokensVariable

/// Every token the guard scans for: the hardcoded canary plus whatever
/// the external source supplies.
let activeTokens: string list = Canary :: (externalTokens |> Option.defaultValue [])

/// Compile one denylist entry into the regex the matching rule above
/// describes — the one rule, in `ToolUp.Platform.Build`, that the template
/// gates' publication-boundary check applies too. Exposed so the guard
/// tests can prove the rule rather than restate it.
let toRegex (token: string) : Regex =
    ToolUp.Platform.Build.Neutrality.toRegex token

/// The active denylist, compiled once: the token as written beside the
/// pattern it became.
let activeRules: (string * Regex) list =
    activeTokens |> List.map (fun t -> t, toRegex t)

/// One leak: which artefact, which line, which denylist entry fired.
/// The offending TEXT is deliberately not carried — file, line and
/// token are enough to fix it, and a public CI log should echo the
/// private vocabulary no more than it must.
type Hit = {
    File: string
    Line: int
    Token: string
}

/// Which lines a block-scoped exemption marker covers: a marker
/// anywhere in a run of consecutive non-blank lines exempts every line
/// of that run. A blank line ends the run.
let private exemptLines (lines: string[]) : bool[] =
    let exempt = Array.zeroCreate<bool> lines.Length
    let mutable blockStart = 0
    let mutable marked = false

    let closeBlock (endExclusive: int) =
        if marked then
            for j in blockStart .. endExclusive - 1 do
                exempt[j] <- true

        marked <- false

    for i in 0 .. lines.Length - 1 do
        if String.IsNullOrWhiteSpace lines[i] then
            closeBlock i
            blockStart <- i + 1
        elif lines[i].Contains ExemptLineMarker then
            marked <- true

    closeBlock lines.Length
    exempt

/// Scan already-split lines. `label` names the artefact in the report.
let scanLines (label: string) (lines: string[]) : Hit list =
    if lines |> Array.exists (fun l -> l.Contains ExemptFileMarker) then
        []
    else
        let exempt = exemptLines lines

        [
            for i in 0 .. lines.Length - 1 do
                if not exempt[i] then
                    for token, rx in activeRules do
                        if rx.IsMatch lines[i] then
                            yield {
                                File = label
                                Line = i + 1
                                Token = token
                            }
        ]

/// Scan a whole text blob (`\r\n` and `\n` both split).
let scanText (label: string) (contents: string) : Hit list =
    scanLines label (contents.Replace("\r\n", "\n").Split '\n')

/// Scan a file on disk, reporting it under `label`.
let scanFile (label: string) (path: string) : Hit list =
    scanLines label (File.ReadAllLines path)

/// Render hits as the failure a contributor can act on without hunting
/// for the policy: what leaked, where, and the two ways to resolve it.
let renderHits (hits: Hit list) : string =
    let shown = hits |> List.truncate 40

    let lines =
        shown
        |> List.map (fun h -> sprintf "  %s:%d  banned token '%s'" h.File h.Line h.Token)

    let elided =
        if hits.Length > shown.Length then
            [ sprintf "  … and %d more" (hits.Length - shown.Length) ]
        else
            []

    String.Join(
        "\n",
        [
            sprintf
                "OSS publication boundary: %d private-vocabulary reference(s) in publicly-shipped artefacts."
                hits.Length
            yield! lines
            yield! elided
            ""
            "This repository is Apache-2.0 public. A shipped source file, doc, sample or README"
            "must not name a private project, product, repository or internal command."
            "Remedy: neutralise the wording, or move the citation to a private-side planning"
            "document and refer to it by number."
            sprintf
                "If the reference is a sanctioned citation of a PUBLIC specification, mark the line `%s: <reason>`"
                ExemptLineMarker
            sprintf "or the whole file `%s: <reason>`." ExemptFileMarker
            sprintf "Policy: %s" PolicyDoc
        ]
    )

/// The message an absent token source reports, naming every place the
/// list was looked for under the checkout at `root`.
let absentSourceMessageFor (root: string) : string =
    sprintf
        "NEUTRALITY TOKEN SOURCE ABSENT — only the public-safe canary token was enforced. The private banned-vocabulary list was looked for, in order, at: %s. Supply it at any of them (a file holds one token per line, '#' comments allowed) to run the full OSS-boundary guard. Outside the gate this result is a SKIP, not a pass; under %s=1 it fails."
        (String.Join("; ", ToolUp.Platform.Build.Neutrality.searched root TokensVariable))
        RequiredVariable

/// The absent-source message for this checkout.
let absentSourceMessage: string = absentSourceMessageFor repoRootPath

/// Assert that `contents` carries none of the active tokens. `label`
/// names the scanned artefact in failure messages.
let assertNoBannedTokens (label: string) (contents: string) =
    match scanText label contents with
    | [] -> ()
    | hits -> failtest (renderHits hits)

/// What an absent token source does to the test reporting it: a failure
/// when `required`, a loud skip otherwise. Never a pass.
let onAbsentSource (required: bool) (message: string) : unit =
    if required then
        failtest message
    else
        Tests.skiptest message

/// Call at the end of a guard test body: when no external token source
/// is present, the (canary-only) run becomes a loud skip — or, when the
/// required variable is set, a failure — so the guard never reports
/// silently green without the private list.
let skipUnlessExternalSource () =
    match externalTokens with
    | Some _ -> ()
    | None -> onAbsentSource (ToolUp.Platform.Build.Neutrality.isRequired RequiredVariable) absentSourceMessage
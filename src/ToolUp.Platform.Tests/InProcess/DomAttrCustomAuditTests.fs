module ToolUp.Platform.Tests.InProcess.DomAttrCustomAuditTests

open System.IO
open System.Text.RegularExpressions
open Expecto

// ─── DOM attr-custom audit ratchet ───────────────────────────────
//
// Walks the forge client tree (`src/*.Client/Client/**/*.fs` +
// `src/*.Client/Components/**/*.fs` + `src/AuthProviders/*Client*/**/*.fs`)
// and flags any `prop.custom ("data-*"|"aria-*"|"role", _)` call
// outside the blessed helper modules (`DataProp.fs`, `AriaProp.fs`,
// `SvgProp.fs`) and this audit's own fixture / test file.
//
// **Why this ratchet exists.** v0.5.0 introduced `DataProp.fs` and
// `AriaProp.fs` as typed wrappers around `prop.custom (kebabName, v)`
// for the `data-*` / `aria-*` / `role` DOM attribute families. React
// preserves those names verbatim today via a hardcoded exception —
// the same kebab-case code path that silently drops other DOM
// attributes (the SVG family covered by `SvgProp.fs`). A future
// React tightening would silently regress every raw site without a
// compiler error. The pre-v0.5.0 sweep retired the existing 7
// offenders; this audit locks the door.
//
// **Heuristic shape.** Text-based scan keyed off the
// `prop.custom\s*\(\s*"<name>"` regex shape, same approach as
// `SubjectWildcardAnalyzer`. Filters string literals that aren't
// actual call sites (rare in F#, but defensible). Trades a few
// false positives for zero infrastructure cost — no Roslyn / FCS
// hook, no analyser package, just `System.IO` + `Regex`.
//
// **Per-finding error message** cites the file path + line number so
// the operator can jump straight to the offender.

// ─── Helpers ──────────────────────────────────────────────────────

let private repoRoot () =
    let asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location
    let asmDir = Path.GetDirectoryName asmLoc
    // bin/Debug/net10.0 → ToolUp.Platform.Tests → src → toolup-forge
    Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

/// Filenames that are the blessed source of `prop.custom ("data-*",
/// _)` / `prop.custom ("aria-*", _)` / `prop.custom ("role", _)`
/// calls. Excluded from the audit by leaf-filename comparison so the
/// helper bodies don't self-flag and so this test file's own literal
/// references (used in error messages + fixture strings) don't flag
/// either.
let private excludedLeafs =
    Set.ofList [
        "DataProp.fs"
        "AriaProp.fs"
        "SvgProp.fs" // sibling helper; same pattern, different attribute family — defensively excluded
        "DomAttrCustomAuditTests.fs" // this file
    ]

/// Pre-filter: walk every `.fs` file under `src/` whose leaf name
/// ends with `.Client.fs` / `Client.fs` shape OR sits under a path
/// containing `Client/` / `Components/`. Cheap text-presence check
/// avoids reading every SDK source on every run.
let private clientFsFiles () =
    let srcRoot = Path.Combine(repoRoot (), "src")

    if not (Directory.Exists srcRoot) then
        []
    else
        Directory.EnumerateFiles(srcRoot, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            let normalised = path.Replace('\\', '/')

            normalised.Contains "/Client/"
            || normalised.Contains "/Components/"
            || normalised.Contains ".Client/")
        |> Seq.filter (fun path ->
            let leaf = Path.GetFileName path
            not (excludedLeafs.Contains leaf))
        |> Seq.filter (fun path ->
            // Cheap pre-filter: only read files whose text contains
            // `prop.custom`. Skips ~95% of files on each run.
            try
                File.ReadAllText(path).Contains "prop.custom"
            with _ ->
                false)
        |> List.ofSeq

let private offenderPattern =
    // Matches `prop.custom ("name"` capturing the literal name. The
    // `\s*` allows the Fantomas-preferred space between `custom` and
    // `(`. Multi-line `prop.custom (` followed by the name on a
    // separate line is also matched because the regex is run against
    // the full file text (no newline restriction) and `.` does not
    // need to span newlines for the `"` to be reached.
    Regex(@"prop\.custom\s*\(\s*""([^""]+)""", RegexOptions.Compiled)

/// One finding the audit surfaces.
type Finding = {
    File: string
    /// 1-indexed line of the offending `prop.custom` call.
    Line: int
    /// The attribute name literal as it appears in source.
    Name: string
}

let private isOffendingName (name: string) =
    name.StartsWith "data-" || name.StartsWith "aria-" || name = "role"

let private lineOf (source: string) (offset: int) =
    let mutable line = 1

    for i in 0 .. min (offset - 1) (source.Length - 1) do
        if source[i] = '\n' then
            line <- line + 1

    line

/// Scan one file's text for offending `prop.custom ("data-*"|"aria-*"|"role", _)`
/// calls. Pure — fixture-friendly, exposed publicly so the
/// anti-regression fixture test below can pin the heuristic.
let scanText (filename: string) (source: string) : Finding list = [
    for m in offenderPattern.Matches(source) do
        let name = m.Groups[1].Value

        if isOffendingName name then
            yield {
                File = filename
                Line = lineOf source m.Index
                Name = name
            }
]

let private formatFinding (f: Finding) =
    sprintf "  %s:%d — prop.custom (\"%s\", _)" f.File f.Line f.Name

// ─── Fixture (anti-regression) ────────────────────────────────────

/// Embedded fixture mimicking a future regression: an unrelated
/// client file that reaches for `data-*` / `aria-*` / `role` via
/// `prop.custom`. Used by the fixture test to prove the heuristic
/// catches what it's meant to catch even if no live source currently
/// trips it.
let private regressionFixture =
    "module SomeFutureClient\n"
    + "\n"
    + "open Feliz\n"
    + "\n"
    + "let view () =\n"
    + "    Html.div [\n"
    + "        prop.custom (\"data-leaked\", \"oops\")\n"
    + "        prop.custom (\"aria-label\", \"untyped\")\n"
    + "        prop.custom (\"role\", \"button\")\n"
    + "    ]\n"

/// Embedded fixture mimicking a SAFE shape: a `data-*` literal
/// inside a string assignment (not a `prop.custom` call) should not
/// flag. The regex anchors on `prop.custom (` so this can't trip.
let private safeFixture =
    "module SomeClient\n"
    + "\n"
    + "let attr = \"data-stuff\" // bare string, not a call site\n"

// ─── Tests ────────────────────────────────────────────────────────

let private liveAuditTest = test "live forge client tree has no `prop.custom (\"data-*\"|\"aria-*\"|\"role\", _)` calls" {
    let candidates = clientFsFiles ()

    let allFindings =
        candidates
        |> List.collect (fun path ->
            let text = File.ReadAllText path
            let relativePath = path.Substring((repoRoot ()).Length).TrimStart([| '/'; '\\' |])

            scanText relativePath text)

    if not (List.isEmpty allFindings) then
        let detail = allFindings |> List.map formatFinding |> String.concat "\n"

        let msg =
            sprintf
                "DOM attr-custom audit found %d offending site(s) — use dataProp / ariaProp helpers from `ToolUp.Platform.{DataProp,AriaProp}` (see docs/platform/dom-props.md):\n%s"
                allFindings.Length
                detail

        failtest msg
}

let private fixtureCatchesRegression = test "regression fixture: a fabricated bad call is caught (heuristic works)" {
    let findings = scanText "fixture.fs" regressionFixture

    // Three call sites: data-leaked, aria-label, role.
    Expect.equal findings.Length 3 "fixture has three offending sites — all three must be flagged"

    let names = findings |> List.map _.Name |> List.sort

    Expect.equal names [ "aria-label"; "data-leaked"; "role" ] "all three attribute families flagged"
}

let private fixtureIgnoresSafeShape = test "safe fixture: a bare string literal containing `data-…` is NOT flagged" {
    let findings = scanText "safe.fs" safeFixture
    Expect.isEmpty findings "non-`prop.custom` `data-…` literal must not trip the audit"
}

[<Tests>]
let tests =
    testList "DOM attr-custom audit ratchet" [ liveAuditTest; fixtureCatchesRegression; fixtureIgnoresSafeShape ]
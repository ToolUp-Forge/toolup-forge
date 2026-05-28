module ToolUp.Platform.Tests.InProcess.RedactionAllowlistParityTests

open System.IO
open System.Text.RegularExpressions
open Expecto

// ─── Phase 9n follow-up — redaction-allowlist parity guard ──────────
//
// `ConfigDriftDetector.fs` (Phase 9q) and `DiagnosticBundleHandler.fs`
// (Phase 9n) each carry an inline copy of the redaction-suffix
// allowlist (`apikey | token | secret | password`). The duplication
// is deliberate — with only two consumers, extracting the list into a
// shared module costs more (extra Compile entry, extra abstraction
// for future readers to trace) than the maintenance saving — but
// without a guard, a future contributor adding a new suffix to one
// file could silently leak values of that shape through the other
// surface (the un-touched site's redactor never sees the new suffix).
//
// This test is the guard: it parses both source files and asserts the
// `redactionSuffixes` lists are byte-equal. Adds ~10ms to the test
// suite; failure mode is loud, with both file paths and the diff
// printed by Expecto.
//
// **Why parse the source instead of reflecting through the binary?**
// Both lists are `let private` at module level, which compiles to a
// CLR-private binding the test assembly cannot read even with
// `InternalsVisibleTo`. Flipping the visibility on production code
// to enable a test is the wrong direction. Reading the .fs files
// directly keeps the test self-contained and asserts the property we
// actually care about — "the SOURCE lists match" — rather than a
// reflection proxy for it.

// ─── Repo-root discovery ────────────────────────────────────────────

let private isRepoRoot (dir: string) =
    File.Exists(Path.Combine(dir, "ToolUp.Forge.sln"))

/// Walk up from the test assembly's directory until we hit the
/// `ToolUp.Forge.sln` marker. Lets the test resolve source paths
/// without hard-coding a `bin/Debug/net10.0/../../..` jump that
/// would silently rot if the test build output moves.
let private findRepoRoot () : string =
    let start =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    let rec walk (dir: string) =
        if isNull dir then
            failwithf
                "Could not locate ToolUp.Forge.sln walking up from %s — RedactionAllowlistParityTests cannot resolve source paths."
                start
        elif isRepoRoot dir then
            dir
        else
            walk (Path.GetDirectoryName dir)

    walk start

// ─── Source parsing ─────────────────────────────────────────────────

let private allowlistRegex =
    Regex(@"let\s+private\s+redactionSuffixes\s*=\s*\[(?<items>[^\]]+)\]", RegexOptions.Compiled)

let private stringLiteralRegex =
    Regex("\"(?<value>[^\"]+)\"", RegexOptions.Compiled)

let private parseAllowlist (path: string) : string list =
    if not (File.Exists path) then
        failwithf "RedactionAllowlistParityTests: expected source file does not exist: %s" path

    let text = File.ReadAllText path
    let m = allowlistRegex.Match text

    if not m.Success then
        failwithf "RedactionAllowlistParityTests: could not locate `let private redactionSuffixes = [ ... ]` in %s" path

    let items = m.Groups["items"].Value

    stringLiteralRegex.Matches items
    |> Seq.map (fun m -> m.Groups["value"].Value)
    |> List.ofSeq

// ─── The test ───────────────────────────────────────────────────────

let tests =
    testList "RedactionAllowlistParity" [

        test "ConfigDriftDetector and DiagnosticBundleHandler share the same redaction-suffix allowlist" {
            let root = findRepoRoot ()

            let driftPath =
                Path.Combine(root, "src", "ToolUp.Platform.Server", "Server", "ConfigDriftDetector.fs")

            let bundlePath =
                Path.Combine(root, "src", "ToolUp.Platform.Server", "Server", "DiagnosticBundleHandler.fs")

            let driftSuffixes = parseAllowlist driftPath
            let bundleSuffixes = parseAllowlist bundlePath

            Expect.isNonEmpty driftSuffixes "ConfigDriftDetector.redactionSuffixes parsed empty"
            Expect.isNonEmpty bundleSuffixes "DiagnosticBundleHandler.redactionSuffixes parsed empty"

            Expect.equal
                bundleSuffixes
                driftSuffixes
                "Redaction-suffix allowlists diverged between ConfigDriftDetector and DiagnosticBundleHandler. Update both copies or accept that one surface will leak values of the new suffix shape."
        }

    ]
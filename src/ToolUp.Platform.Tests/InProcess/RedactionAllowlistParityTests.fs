module ToolUp.Platform.Tests.InProcess.RedactionAllowlistParityTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform

// ─── Phase 9n follow-up — redaction-allowlist parity guard ──────────
//
// **What this used to be.** `ConfigDriftDetector.fs` (9q) and
// `DiagnosticBundleHandler.fs` (9n) each carried an inline copy of the
// redaction-suffix allowlist (`apikey | token | secret | password`), and
// this test parsed both source files and asserted the two lists were
// byte-equal. The duplication was deliberate; the guard was the price.
//
// **Why it changed.** A THIRD copy appeared —
// `ApplianceSupportBundle.SuffixFloor` (488.D) — and it was outside the
// guard, because the guard was written to compare exactly two named
// files. So the property the guard existed to protect ("a suffix added
// to one surface reaches the others") had quietly stopped holding on the
// one surface where redaction is the load-bearing guarantee rather than
// defence-in-depth. The list is now extracted to `RedactionAllowlist`
// and all three consumers read it, which makes parity structural.
//
// **So what is left to test?** Two things a shared module does NOT make
// structural:
//
//   1. That the shared list is the one the surfaces actually mask
//      against — asserted directly against `RedactionAllowlist`, so a
//      future edit that empties or narrows it is loud.
//   2. That nobody re-introduces a private copy. The extraction is only
//      worth what it costs if the next surface reads the module instead
//      of pasting four strings, and that is a SOURCE property no type
//      can enforce. The scan below is the successor to the old parity
//      parse — same technique, applied to the whole Server tree rather
//      than to two hardcoded paths, which is precisely the coverage gap
//      that let the third copy in.

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

// ─── Source scanning ────────────────────────────────────────────────

/// A literal list of the four credential suffixes, in any order, written
/// inline. The shape a re-introduced private copy takes.
let private inlineCopyRegex =
    Regex(
        "\\[\\s*\"(apikey|token|secret|password)\"(\\s*;\\s*\"(apikey|token|secret|password)\"){3}\\s*\\]",
        RegexOptions.Compiled ||| RegexOptions.IgnoreCase
    )

/// The one file allowed to declare the list.
let private declarationSite = "RedactionAllowlist.fs"

let tests =
    testList "RedactionAllowlistParity" [

        test "the shared allowlist still holds the four credential suffixes" {
            // The falsifier for the scan below: if the shared list were
            // emptied, no surface would redact anything and the "nobody
            // re-declares it" scan would still pass, cleanly.
            Expect.equal
                RedactionAllowlist.suffixes
                [ "apikey"; "token"; "secret"; "password" ]
                "the shared credential-suffix floor changed — every redacting surface reads this list, so a narrowing here silently un-redacts all of them"

            Expect.isTrue (RedactionAllowlist.shouldRedact "StripeApiKey") "a credential-shaped name is redacted"

            Expect.isTrue (RedactionAllowlist.shouldRedact "sessiontoken") "matching is case-insensitive on the suffix"

            Expect.isFalse (RedactionAllowlist.shouldRedact "PublicBaseUrl") "an ordinary config name is not"

            Expect.isFalse (RedactionAllowlist.shouldRedact "") "an unnamed property is not a credential"
        }

        test "no source file re-declares the allowlist inline" {
            let root = findRepoRoot ()
            let serverDir = Path.Combine(root, "src", "ToolUp.Platform.Server")

            Expect.isTrue (Directory.Exists serverDir) (sprintf "expected the Server sources at %s" serverDir)

            let offenders =
                Directory.EnumerateFiles(serverDir, "*.fs", SearchOption.AllDirectories)
                |> Seq.filter (fun path -> Path.GetFileName path <> declarationSite)
                |> Seq.filter (fun path -> inlineCopyRegex.IsMatch(File.ReadAllText path))
                |> Seq.map (fun path -> Path.GetRelativePath(root, path))
                |> List.ofSeq

            Expect.isEmpty
                offenders
                "a source file declares its own copy of the credential-suffix allowlist. Read `RedactionAllowlist.suffixes` instead — three copies is how ApplianceSupportBundle.SuffixFloor ended up outside the old parity guard."
        }

    ]
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.SdkManifestTests

open System
open System.IO
open System.Reflection
open Expecto
open ToolUp.Forge

// ─── Phase 326 — the ToolUp.Sdk meta-manifest drift guard ─────────────
//
// `src/ToolUp.Sdk/build/ToolUp.Sdk.props` advertises a version for every
// published `ToolUp.*` package. A consumer imports it into their
// `Directory.Packages.props`, sets one `<ToolUpSdkVersion>`, and every
// `PackageReference` resolves. A published package with NO entry is
// NU1008 for that consumer at restore; an entry for a package nothing
// packs is NU1101. Both are silent from inside this repo — the SDK's own
// build never reads the manifest — which is how it came to carry 59
// entries against the 163 it needed, and how it came to ship for months
// as XML MSBuild would not even parse.
//
// This pack asserts the property the file exists to have: the manifest
// lists EXACTLY the published set, minus the two shapes that are
// excluded by construction and named with their reason. It runs the same
// `SdkManifest` module the `GenerateSdkManifest` FAKE target renders
// from, source-linked rather than copied, so a repair and its check can
// never disagree about what "published" means.
//
// ── Why the vacuity cases below are not padding ──
// The guard's whole content is a set difference, and a set difference
// over two empty sets is clean. So a discovery that silently matched
// nothing — a moved `src/`, a regex that stopped matching, a test
// assembly relocated so the repo-root walk lands above the checkout —
// would report a perfect manifest forever. `discovery is non-empty`,
// `the manifest parses to a non-empty set` and the two `diff` go-red
// cases pin each of those, so a green run here means the comparison
// actually happened.
//
// Zero shipped code: this is test-tier + a repo-root build file (GP 13).

/// Repo root (`toolup-forge`) from the running test assembly:
/// `src/ToolUp.Platform.Build.Tests/bin/<Config>/net10.0` -> up 5. The
/// same walk `PublicApiApproval.repoRoot` and `ArchitectureFitness`
/// use, from a pack at the same depth.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private reconciliation = lazy (SdkManifest.reconcile (repoRoot ()))

// No `[<Tests>]` attribute, matching this pack's other modules: Program.fs
// runs an explicitly-enumerated list, and the Phase 722 registration guard
// is configured for a pack with no attributed bindings.
let tests =
    testList "SdkManifest" [
        // ── The two vacuity pins ──────────────────────────────────────

        test "discovery finds the repo's packable projects" {
            let discovered = SdkManifest.discover (repoRoot ())

            Expect.isGreaterThan
                (List.length discovered)
                100
                (sprintf
                    "SdkManifest.discover found %d packable project(s) under %s/src. This repo publishes well over a hundred; a count this low means the glob or the repo-root walk is broken, and every assertion below would then pass over an empty set."
                    (List.length discovered)
                    (repoRoot ()))
        }

        test "the committed manifest parses to a non-empty id set" {
            let manifest = SdkManifest.manifestPath (repoRoot ())

            Expect.isTrue (File.Exists manifest) (sprintf "the ToolUp.Sdk meta-manifest is missing at %s" manifest)

            let declared = SdkManifest.declaredIdsIn (File.ReadAllText manifest)

            Expect.isNonEmpty
                declared
                (sprintf
                    "parsed 0 <PackageVersion Include=\"...\"> entries out of %s. Either the file was emptied or the parser no longer matches its shape; both read as 'nothing extra' below, so this is pinned separately."
                    manifest)
        }

        // ── The manifest MSBuild actually has to load ─────────────────

        test "the committed manifest is XML MSBuild can import" {
            // Found by this phase, not suspected by it: the file shipped
            // for months with a NESTED XML comment in its header example
            // (`<!-- non-ToolUp deps -->` inside the comment opened at
            // line 2). XML forbids a double hyphen inside a comment, so
            // MSBuild refused the whole import with MSB4024 — verified
            // against a real `<Import>` of a byte-identical copy. The
            // manifest resolved NOTHING for any consumer, and no test in
            // this repo noticed, because nothing here imports its own
            // meta-manifest.
            let manifest = SdkManifest.manifestPath (repoRoot ())
            let text = File.ReadAllText manifest

            match SdkManifest.wellFormednessError text with
            | None -> ()
            | Some why ->
                failtestf
                    "%s is not well-formed XML, so MSBuild refuses the whole import (MSB4024) and the manifest resolves no version at all: %s.\n\nThe usual cause is a nested comment or a literal double hyphen inside a comment."
                    manifest
                    why
        }

        test "a nested comment is detected rather than shrugged off" {
            // The falsifier for the case above: the exact shape that
            // shipped, asserted to be caught.
            let nested =
                "<Project>\n  <!-- outer <!-- inner --> still outer -->\n  <ItemGroup />\n</Project>\n"

            Expect.isSome
                (SdkManifest.wellFormednessError nested)
                "a comment containing a double hyphen must be reported; MSBuild rejects it with MSB4024"

            Expect.isNone
                (SdkManifest.wellFormednessError "<Project>\n  <ItemGroup />\n</Project>\n")
                "ordinary well-formed props must not be reported"
        }

        // ── The guard's own failure path ──────────────────────────────

        test "diff reports a published package the manifest omits" {
            let missing, extra = SdkManifest.diff [ "ToolUp.A"; "ToolUp.B" ] [ "ToolUp.A" ]
            Expect.equal missing [ "ToolUp.B" ] "an omitted published id must be reported as missing"
            Expect.isEmpty extra "nothing was declared beyond the published set"
        }

        test "an illustrative entry inside a comment is not a declaration" {
            // The manifest's header carries a worked example of a
            // CONSUMER's Directory.Packages.props, and that example
            // contains its own <ItemGroup> and two <PackageVersion>
            // lines. Read naively, `ToolUp.X` and `...` register as
            // manifest entries nothing publishes — which is exactly what
            // this module reported on its first run, and what would have
            // sent a future reader hunting a package that never existed.
            let text =
                "<Project>\n"
                + "  <!-- example:\n"
                + "         <PackageVersion Include=\"ToolUp.Illustrative\" Version=\"1.0.0\" />\n"
                + "  -->\n"
                + "  <ItemGroup>\n"
                + "    <PackageVersion Include=\"ToolUp.Real\" Version=\"$(ToolUpSdkVersion)\" />\n"
                + "  </ItemGroup>\n"
                + "</Project>\n"

            Expect.equal
                (SdkManifest.declaredIdsIn text)
                [ "ToolUp.Real" ]
                "only entries outside XML comments are declarations"
        }

        test "the preamble split skips an <ItemGroup> inside a comment" {
            // Same hazard, other half: splitting on the first literal
            // `<ItemGroup>` cuts the header comment in two and destroys
            // the prose above it (observed, on the same first run).
            let text =
                "<Project>\n"
                + "  <!-- KEEPME example:\n"
                + "         <ItemGroup>\n"
                + "           <PackageVersion Include=\"X\" Version=\"1\" />\n"
                + "         </ItemGroup>\n"
                + "  -->\n"
                + "  <ItemGroup>\n"
                + "  </ItemGroup>\n"
                + "</Project>\n"

            let preamble = SdkManifest.preambleOf (Some text)

            Expect.stringContains preamble "KEEPME" "the whole header comment must survive the split"

            Expect.isTrue
                (preamble.TrimEnd().EndsWith "-->")
                (sprintf "the preamble must end after the header comment, not inside it. Got:\n%s" preamble)
        }

        test "diff reports a manifest entry nothing publishes" {
            let missing, extra = SdkManifest.diff [ "ToolUp.A" ] [ "ToolUp.A"; "ToolUp.Ghost" ]
            Expect.isEmpty missing "every published id is declared"
            Expect.equal extra [ "ToolUp.Ghost" ] "an id nothing packs must be reported as extra"
        }

        // ── The property ──────────────────────────────────────────────

        test "the manifest declares every published package" {
            let r = reconciliation.Value

            Expect.isEmpty
                r.Missing
                (sprintf
                    "%d published package id(s) have no <PackageVersion> entry in the ToolUp.Sdk meta-manifest, so a consumer referencing one under central package management gets NU1008: %s.\n\nRegenerate with `%s`."
                    (List.length r.Missing)
                    (String.Join(", ", r.Missing))
                    SdkManifest.regenerateCommand)
        }

        test "the manifest declares nothing the repo does not publish" {
            let r = reconciliation.Value

            Expect.isEmpty
                r.Extra
                (sprintf
                    "%d <PackageVersion> entry/entries name a package nothing in this repo packs, so the entry resolves to NU1101 rather than to anything useful: %s.\n\nRegenerate with `%s`."
                    (List.length r.Extra)
                    (String.Join(", ", r.Extra))
                    SdkManifest.regenerateCommand)
        }

        test "every deliberate exclusion carries a rationale" {
            // The manifest omits two SHAPES on purpose (the meta-package
            // itself; anything <PackAsTool>). An exclusion with no reason
            // is indistinguishable from an omission, which is the defect
            // this phase closed — so the reason is asserted, not assumed.
            let r = reconciliation.Value

            for exclusion in r.Excluded do
                Expect.isGreaterThan
                    exclusion.Rationale.Length
                    20
                    (sprintf "%s is excluded from the meta-manifest with no substantive rationale" exclusion.PackageId)

            Expect.isNonEmpty
                r.Excluded
                "this repo publishes at least the ToolUp.Sdk meta-package itself and one <PackAsTool> CLI, both excluded by shape; an empty exclusion set means the predicates stopped matching."
        }

        test "the generated region round-trips to the same declared set" {
            // `render` is what the repair writes. If rendering the
            // current tree produced a file the reader disagrees with,
            // the repair the failure messages above recommend would not
            // actually fix anything.
            let root = repoRoot ()
            let rendered = SdkManifest.render root
            let declared = SdkManifest.declaredIdsIn rendered

            Expect.sequenceEqual
                declared
                (SdkManifest.expected root)
                "the rendered manifest must declare exactly the expected id set, in order — otherwise `GenerateSdkManifest` writes a file its own check rejects"
        }

        test "a regeneration preserves the hand-authored preamble" {
            // Everything above the BEGIN GENERATED marker is prose a
            // human wrote (the import recipe; the Phase 307 / 344
            // rationale). A generator that silently ate it would be
            // discovered only by whoever next needed to read it.
            let root = repoRoot ()
            let before = File.ReadAllText(SdkManifest.manifestPath root)
            let rendered = SdkManifest.render root

            let preamble = SdkManifest.preambleOf (Some before)

            Expect.isTrue
                (preamble.Contains "ManagePackageVersionsCentrally")
                "the preserved preamble should still carry the consumer import recipe"

            Expect.stringStarts
                rendered
                preamble
                "a regeneration must reproduce the hand-authored preamble byte-for-byte ahead of the generated region"
        }
    ]
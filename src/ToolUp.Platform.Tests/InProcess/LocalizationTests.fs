// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.LocalizationTests

open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform

// ─── Phase 758 — the localization regression gate ─────────────────────
//
// Phase 751 finished the string sweep. This pack is what keeps it
// finished, and it does so in two halves that together state the whole
// property "nothing renders un-localised":
//
//   1. THE CATALOG HALF (`coverageTests`). Every string the catalog can
//      serve — including the ones parameterised messages BUILD — is the
//      pseudo-localisation of its English source. A section the walk
//      cannot reach, or a derivation that quietly skips one, shows up as
//      a leaf that did not move. This is the half that makes the
//      pseudo-locale trustworthy: a gate whose transform has holes
//      reports green while covering less than it claims.
//
//   2. THE VIEW HALF (`sourceGateTests`). No swept view passes a bare
//      string literal where a catalog field belongs. This is Phase
//      444/751's acceptance grep, promoted from a thing a session ran
//      once into a thing every `VerifyAll` runs — which is the actual
//      ask, because the regression the phase names ("the next module
//      view written with a bare literal") happens after the sweep, not
//      during it.
//
// Why the view half is a SOURCE scan and not a render. The natural
// reading of "mount the surfaces under the pseudo-locale and look for a
// plain string" needs a React renderer, and these views are Feliz/React:
// they render under Fable in a browser, not on the .NET in-process tier
// this pack runs on. Rendering them would mean a second harness (a jsdom
// pass in the Fable tier) that could only observe the same defect the
// source scan observes directly and earlier. What the render WOULD add
// over the scan is coverage of strings that reach the DOM by a route the
// scan does not model; the two `rawMatchFloor` guards below are what
// keep that gap from widening silently. The pseudo-locale itself remains
// the runtime instrument — a developer running under `qps-ploc` sees the
// same defect class with their eyes, which is what it is for.

// ─── 1. The catalog half ──────────────────────────────────────────────

/// The root `Locale` field, in `stringLeaves` path spelling. It is the
/// one string in the record that is machinery rather than prose: it
/// reaches `Intl`, so `derive` re-stamps it with the pseudo-locale tag
/// instead of accenting it.
let private localePath = ".Locale"

let private englishLeaves =
    lazy (PseudoLocaleCatalog.stringLeaves MessageCatalog.english)

/// Every leaf of `derived` that is NOT the pseudo-localisation of the
/// matching English leaf, as `path * english * actual`.
///
/// The comparison is leaf-for-leaf against `PseudoLocale.transform`
/// rather than "is it different from English", because a vowel-less
/// string transforms to itself by design — `transform` leaves a token
/// with nothing to accent exactly as it was — and a difference test would
/// report every one of those as a miss.
let private untransformedLeaves (derived: MessageCatalog) : (string * string * string) list =
    let english = englishLeaves.Force()
    let actual = PseudoLocaleCatalog.stringLeaves derived

    if List.length english <> List.length actual then
        failtestf
            "the walk produced %d leaves for the derived catalog and %d for English — the two must have identical shape"
            (List.length actual)
            (List.length english)

    List.zip english actual
    |> List.choose (fun ((path, en), (actualPath, value)) ->
        if path <> actualPath then
            failtestf "leaf order diverged: English says `%s`, derived says `%s`" path actualPath
        elif path = localePath then
            None
        elif value = PseudoLocale.transform en then
            None
        else
            Some(path, en, value))

let private describeMisses (misses: (string * string * string) list) : string =
    misses
    |> List.truncate 10
    |> List.map (fun (path, en, actual) -> $"  {path}: expected `{PseudoLocale.transform en}`, got `{actual}`")
    |> String.concat Environment.NewLine

/// A lower bound on the number of string leaves the catalog serves.
///
/// A floor rather than an exact count, for the reason `VerifyFable`'s
/// case floor is one: adding a catalog field must never need a companion
/// edit here, and the failure this guards against is the walk COLLAPSING
/// (a reflection arm that stops recursing, a record probe that returns
/// nothing) rather than the catalog shrinking. Without it, a walk that
/// found zero leaves would satisfy every assertion in this list
/// vacuously — the whole file would pass over an empty list.
let private leafFloor = 900

let private coverageTests =
    testList "pseudo-locale coverage" [

        test "the walk reaches the whole catalog, not a prefix of it" {
            let count = List.length (englishLeaves.Force())

            Expect.isGreaterThanOrEqual
                count
                leafFloor
                $"the reflection walk found only {count} string leaves; a run this small means it stopped recursing rather than that the catalog shrank"
        }

        test "every catalog leaf is the pseudo-localisation of its English source" {
            let misses = untransformedLeaves (PseudoLocaleCatalog.catalog ())

            Expect.isEmpty
                misses
                $"{List.length misses} catalog leaf/leaves survived the derivation untransformed:{Environment.NewLine}{describeMisses misses}"
        }

        test "the derived catalog is stamped with the pseudo-locale, not an accented tag" {
            let derived = PseudoLocaleCatalog.catalog ()

            Expect.equal
                derived.Locale
                PseudoLocaleCatalog.Tag
                "Locale must be the raw tag — Intl throws on a mangled one"

            Expect.equal derived.Locale "qps-ploc" "the tag is the reserved ICU pseudo-locale"
        }

        test "parameterised messages are transformed, and still take their argument" {
            // A function field is the shape a hand-written pseudo-catalog
            // most easily gets wrong, because it cannot be transformed by
            // assignment — it has to be wrapped.
            let derived = PseudoLocaleCatalog.catalog ()
            // A vowel-less argument round-trips verbatim (`transform`
            // leaves a token with nothing to accent exactly as it was),
            // which is what lets this assert substitution without also
            // asserting the accent map.
            let rendered = derived.Shell.ResultsAvailableIn "XYZ"

            Expect.stringContains rendered "⟦" "a parameterised message is bracketed like every other string"
            Expect.notEqual rendered (MessageCatalog.english.Shell.ResultsAvailableIn "XYZ") "and differs from English"
            Expect.stringContains rendered "XYZ" "the caller's argument still reaches the message"

            // And the documented consequence, pinned so it is a decision
            // rather than a surprise: the argument is interpolated by the
            // original function before the transform sees the result, so
            // a vowel-bearing one is accented along with its sentence.
            let vowelled = derived.Shell.ResultsAvailableIn "Insights"

            Expect.isFalse
                (vowelled.Contains "Insights")
                "a vowel-bearing argument is accented too — there is no separate template to transform"
        }

        test "a nested section skipped by a derivation is reported, naming its path" {
            // GO-RED. This is the acceptance's "deleting a module's
            // catalog section turns the gate red", expressed without
            // sabotaging the tree: a derived catalog whose `Toast`
            // section was left English is exactly what a walk that
            // stopped recursing into one nested record would produce.
            let sabotaged = {
                PseudoLocaleCatalog.catalog () with
                    Toast = MessageCatalog.english.Toast
            }

            let misses = untransformedLeaves sabotaged

            Expect.isNonEmpty misses "a section left in English must be caught"

            Expect.all misses (fun (path, _, _) -> path.StartsWith ".Toast.") "only the sabotaged section is reported"

            Expect.exists
                misses
                (fun (path, _, _) -> path = ".Toast.Info")
                $"the report names the field path; got: {describeMisses misses}"
        }

        test "a consumer's own translation is pseudo-localised too" {
            // `derive` walks whatever it is handed, so a deployment can
            // check ITS coverage and not only the SDK's. That is the
            // property that makes the pseudo-locale useful to a consumer
            // rather than only to this repo.
            let french (c: MessageCatalog) = {
                c with
                    Toast = { c.Toast with Info = "Information" }
            }

            let derived = PseudoLocaleCatalog.derive (french MessageCatalog.english)

            Expect.equal
                derived.Toast.Info
                (PseudoLocale.transform "Information")
                "the consumer's string, not the SDK's, is what gets transformed"
        }

        test "overrideFor serves the pseudo-locale and passes every other language through" {
            let asked = MessageCatalog.forLocale PseudoLocaleCatalog.Tag
            let served = PseudoLocaleCatalog.overrideFor asked

            Expect.equal
                served.Toast.Info
                (PseudoLocale.transform MessageCatalog.english.Toast.Info)
                "qps-ploc is served"

            let french = MessageCatalog.forLocale "fr"
            let passed = PseudoLocaleCatalog.overrideFor french

            Expect.equal
                passed.Toast.Info
                MessageCatalog.english.Toast.Info
                "returning the argument unchanged is the fallback, exactly as a real translation does"
        }

        test "isActive matches the tag case-insensitively and nothing else" {
            Expect.isTrue (PseudoLocaleCatalog.isActive "qps-ploc") "the tag"
            Expect.isTrue (PseudoLocaleCatalog.isActive " QPS-PLOC ") "config values are not always tidy"
            Expect.isFalse (PseudoLocaleCatalog.isActive "en") "English is not the pseudo-locale"
            Expect.isFalse (PseudoLocaleCatalog.isActive "") "a blank tag is not the pseudo-locale"
        }
    ]

// ─── 2. The view half ─────────────────────────────────────────────────

/// Repo root (`toolup-forge`) from the running assembly, the same
/// derivation the composition-baseline guard uses:
/// `bin/<Config>/net10.0/…Tests.dll` → up five.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// The directories Phase 751 swept. A new client package added outside
/// this list is not covered — which is a deliberate limit rather than an
/// oversight: the list is the sweep's own scope, and widening it is the
/// act of sweeping a package, not of editing a test.
let private sweptDirs = [
    Path.Combine("src", "ToolUp.Platform.Client", "Client")
    Path.Combine("src", "ToolUp.KnowledgeBase.Client")
    Path.Combine("src", "AuthProviders")
]

/// The props that put a literal in front of a reader. Deliberately
/// includes the single-line element-list form (`Html.h3 [ prop.className
/// "…"; prop.text "…" ]`) by matching anywhere on the line: Phase 444
/// recorded that a line-anchored pattern hid 17 real misses, and that
/// the confirming result was the least-examined evidence in the phase.
/// No length bound either — the same note records a two-character column
/// header slipping through a `{3,}`.
let private literalProp =
    Regex(@"(?:prop\.(?:text|placeholder|title|ariaLabel)|Html\.text)\s+""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled)

/// A `prop.value "x"` beside a `prop.text "x"` on one line — an
/// `Html.option` whose label IS its wire value. Translating it would
/// change what the form submits, so it is text by position only.
let private wireShapedOption =
    Regex(@"prop\.value\s+""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled)

/// The toolkit-adjacent chrome Phase 444 deferred behind the toolkit
/// extraction (Phases 307 / 344), and Phase 751 was explicitly told not
/// to sweep. `Client/UI/` is Sidebar plus `Toolkit/`, and they are one
/// deferral rather than two: 444's note gives the SAME reason for both —
/// localising them threads the catalog through the exact render surfaces
/// those phases restructure, and the toolkit boundary should decide
/// whether it takes a messages parameter or stays string-free with
/// labels passed in. Excluding them here records the deferral; it does
/// not extend it. When 307/344 land, delete this arm and sweep.
let private deferredDir =
    Path.Combine("Client", "UI") + Path.DirectorySeparatorChar.ToString()

type private Finding = {
    File: string
    Line: int
    Literal: string
}

/// Classify one source line. `None` means nothing to report — either no
/// literal, or one of the three recorded exclusion classes.
///
/// Factored out of the file walk so it can be exercised on synthetic
/// input below: a gate whose classifier is only ever run over a tree
/// that passes is a gate nobody has seen fail.
let private scanLine (relativePath: string) (lineNumber: int) (text: string) : Finding list =
    if relativePath.Replace('/', Path.DirectorySeparatorChar).Contains deferredDir then
        []
    else
        let wireValues =
            wireShapedOption.Matches text
            |> Seq.map (fun m -> m.Groups[1].Value)
            |> Set.ofSeq

        literalProp.Matches text
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Seq.filter (fun literal ->
            // Not text: a glyph, an arrow, a separator, a blank. Phase
            // 444's final audit recorded exactly this residue ("—", "×",
            // "▾") and called it not text; a translation has nothing to
            // say about it.
            literal |> Seq.exists Char.IsLetter
            // Not prose: the label IS the wire value on this line.
            && not (wireValues.Contains literal))
        |> Seq.map (fun literal -> {
            File = relativePath
            Line = lineNumber
            Literal = literal
        })
        |> Seq.toList

let private sweptFiles () =
    let root = repoRoot ()

    sweptDirs
    |> List.map (fun d -> Path.Combine(root, d))
    |> List.filter Directory.Exists
    |> List.collect (fun dir -> Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) |> List.ofSeq)
    |> List.map (fun full -> Path.GetRelativePath(root, full), full)
    |> List.sortBy fst

/// Every line of every swept file, paired with its location. Read once.
let private sweptLines =
    lazy
        (sweptFiles ()
         |> List.collect (fun (relative, full) ->
             File.ReadAllLines full
             |> Array.toList
             |> List.mapi (fun i line -> relative, i + 1, line)))

/// A lower bound on how many literal-carrying props the pattern matches
/// BEFORE exclusions.
///
/// This is the guard that matters most in this file. Every other
/// assertion here is satisfied by a scanner that matches nothing at all
/// — renaming `prop.text`, moving the swept packages, or shipping a
/// regex that quietly stops compiling all read as a clean sweep. The
/// floor turns "found no problems" into "looked, and found the things it
/// expected to find and then excluded them".
let private rawMatchFloor = 20

let private sourceGateTests =
    testList "no un-externalised literal in a swept view" [

        test "the scanner is still looking at real code" {
            let raw =
                sweptLines.Force()
                |> List.sumBy (fun (_, _, line) -> literalProp.Matches(line).Count)

            Expect.isGreaterThanOrEqual
                raw
                rawMatchFloor
                $"the literal pattern matched only {raw} props across the swept packages. That is not a clean sweep — it is a scanner that has stopped seeing the code (a renamed prop, a moved package, a pattern that no longer matches the element-list form)."
        }

        test "the swept packages contain no bare user-facing literal" {
            let findings =
                sweptLines.Force()
                |> List.collect (fun (path, line, text) -> scanLine path line text)

            let report =
                findings
                |> List.truncate 25
                |> List.map (fun f -> $"  {f.File}:{f.Line}: \"{f.Literal}\"")
                |> String.concat Environment.NewLine

            Expect.isEmpty
                findings
                ($"{List.length findings} user-facing string literal(s) bypass the message catalog. Add a field to `MessageCatalog` and render it, as Phase 751 did for every other view:"
                 + Environment.NewLine
                 + report)
        }

        test "the classifier flags a bare literal — go-red" {
            // The falsifying probe. Without it, every assertion above is
            // consistent with a classifier that returns `[]` for
            // everything.
            let findings =
                scanLine
                    "src/ToolUp.Platform.Client/Client/Whatever.fs"
                    42
                    """Html.h3 [ prop.className "x"; prop.text "Save changes" ]"""

            let found = findings |> List.exactlyOne
            Expect.equal found.Literal "Save changes" "the literal is named"
            Expect.equal found.Line 42 "with its line"
        }

        test "the classifier excludes a glyph, a wire-shaped option and the deferred toolkit" {
            Expect.isEmpty (scanLine "a/B.fs" 1 """Html.span [ prop.text "—" ]""") "a dash is not text"
            Expect.isEmpty (scanLine "a/B.fs" 1 """Html.span [ prop.text "" ]""") "nor is a blank"

            Expect.isEmpty
                (scanLine "a/B.fs" 1 """Html.option [ prop.value "auto"; prop.text "auto" ]""")
                "a label that is its own wire value is not prose"

            Expect.isEmpty
                (scanLine
                    (Path.Combine("src", "ToolUp.Platform.Client", "Client", "UI", "Sidebar.fs"))
                    1
                    """Html.span [ prop.text "Powered by ToolUp-Forge" ]""")
                "the Sidebar deferral is recorded, not swept"

            // …and the exclusion is scoped: the same literal elsewhere is
            // still a finding, so the deferral cannot silently spread.
            Expect.isNonEmpty
                (scanLine
                    "src/ToolUp.Platform.Client/Client/Home.fs"
                    1
                    """Html.span [ prop.text "Powered by ToolUp-Forge" ]""")
                "the deferral is a path exclusion, not a literal one"
        }

        test "a wire-shaped exclusion does not hide a real label on the same line" {
            // The `prop.value` exclusion matches on the literal, not on
            // the line, so a genuine label sitting beside an unrelated
            // wire value is still reported.
            let findings =
                scanLine "a/B.fs" 7 """Html.option [ prop.value "auto"; prop.text "Automatic" ]"""

            Expect.equal (findings |> List.exactlyOne).Literal "Automatic" "a different label is still text"
        }
    ]

let tests =
    testList "ToolUp.Platform localization gate (Phase 758)" [ coverageTests; sourceGateTests ]
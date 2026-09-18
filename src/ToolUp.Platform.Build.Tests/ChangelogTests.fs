// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.ChangelogTests

open System.IO
open System.Reflection
open Expecto
open ToolUp.Forge
open ToolUp.Forge.Changelog

// ─── Phase 262 — the generated CHANGELOG ──────────────────────────────
//
// `Changelog` runs at a release, against fifty tags and a git history.
// This pack runs on every commit against strings, and it is where the
// renderer's rules are decided: a fixture diff renders a section with
// Added, Changed and Removed each represented; a member removed and added
// under one name pairs into ONE Changed line rather than two; the render
// is a pure function of its inputs (twice over the same data is
// byte-identical, and so is the same data in a different order); a list
// past its cap ends with the diff that shows the rest; and the two
// whole-package cases collapse to one line each. The last group pins the
// COMMITTED `CHANGELOG.md` as the generator's own — a hand edit reddens
// here rather than being silently regenerated away at the next release.
// Same split, and same reason, as Phase 260's `SemVerBumpTests`: the
// module is source-linked from the repo root, not copied, so the target
// and these proofs cannot come to different conclusions.
//
// Zero shipped code: test tier plus a repo-root build file (GP 13).

/// Repo root (`toolup-forge`) from the running test assembly, the same
/// walk `SemVerBumpTests` and `V1ReadinessTests` use.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

// ─── Fixtures ─────────────────────────────────────────────────────────

let private oldSig = "Demo.T.Alpha(System.String) : System.Int32"
let private newSig = "Demo.T.Alpha(System.String, System.Boolean) : System.Int32"
let private gone = "Demo.T.Beta() : System.String"
let private fresh = "Demo.T.Gamma : System.Int32 { get }"

/// One package that moved in every way at once: `Alpha` retyped, `Beta`
/// removed, `Gamma` added.
let private mixed: SemVerBump.PackageChange = {
    Package = "Demo.Mixed"
    Class = SemVerBump.Breaking
    Removed = [ oldSig; gone ]
    Added = [ newSig; fresh ]
    Withdrawn = false
    Introduced = false
}

let private release (entries: PackageEntry list) (docs: string list) : Release = {
    Version = "0.2.0"
    Tag = Some "v0.2.0"
    Date = Some "2026-01-02"
    Since = Some "v0.1.0"
    Surface = Baselines entries
    MigrationDocs = docs
}

let private renderOne (r: Release) = renderRelease defaultLimits r

let private linesOf (text: string) =
    text.Replace("\r\n", "\n").Split('\n') |> List.ofArray

// ─── Tests ────────────────────────────────────────────────────────────

let tests =
    testList "Phase 262 — generated CHANGELOG" [

        testList "member identity" [
            testCase "a method's key is its name up to the parameter list"
            <| fun () -> Expect.equal (memberKey oldSig) "Demo.T.Alpha" "method key"

            testCase "a property's key is its name up to the type annotation"
            <| fun () -> Expect.equal (memberKey fresh) "Demo.T.Gamma" "property key"

            testCase "a type token's key drops the (class) and (obsolete) markers"
            <| fun () ->
                Expect.equal (memberKey "Demo.T (class)") "Demo.T" "class"
                Expect.equal (memberKey "Demo.T (class)  (obsolete)") "Demo.T" "obsolete class"

            testCase "a generic method keeps its arity suffix in the key"
            <| fun () -> Expect.equal (memberKey "Demo.T.Map`2(A, B) : C") "Demo.T.Map`2" "arity"
        ]

        testList "pairing into Changed" [
            testCase "a member removed and added under one name is ONE signature change"
            <| fun () ->
                let e = entryOf mixed

                Expect.equal
                    e.Changed
                    [
                        {
                            Key = "Demo.T.Alpha"
                            Before = oldSig
                            After = newSig
                        }
                    ]
                    "changed"

                Expect.equal e.Removed [ gone ] "what did not pair stays removed"
                Expect.equal e.Added [ fresh ] "what did not pair stays added"

            testCase "the entry is the same whatever order the tokens arrived in"
            <| fun () ->
                let reversed = {
                    mixed with
                        Removed = List.rev mixed.Removed
                        Added = List.rev mixed.Added
                }

                Expect.equal (entryOf reversed) (entryOf mixed) "order-insensitive"

            testCase "an obsolete marker beside an unchanged token is an addition, not a change"
            <| fun () ->
                // Phase 258's renderer emits the marker as a SEPARATE line; the
                // member's own token stays, so nothing pairs.
                let e =
                    entryOf {
                        mixed with
                            Removed = []
                            Added = [ "Demo.T (class)  (obsolete)" ]
                    }

                Expect.isEmpty e.Changed "no change"
                Expect.equal e.Added [ "Demo.T (class)  (obsolete)" ] "the marker is added"

            testCase "two overloads that both moved report as two changes, not four lines"
            <| fun () ->
                let e =
                    entryOf {
                        mixed with
                            Removed = [ "Demo.T.F(A) : R"; "Demo.T.F(A, B) : R" ]
                            Added = [ "Demo.T.F(A2) : R"; "Demo.T.F(A2, B2) : R" ]
                    }

                Expect.equal e.Changed.Length 2 "two changes"
                Expect.isEmpty e.Removed "nothing left removed"
                Expect.isEmpty e.Added "nothing left added"

            testCase "vacuity: an unchanged package yields an empty entry"
            <| fun () ->
                let e =
                    entryOf {
                        mixed with
                            Removed = []
                            Added = []
                            Class = SemVerBump.Unchanged
                    }

                Expect.isEmpty e.Changed "no changed"
                Expect.isEmpty e.Removed "no removed"
                Expect.isEmpty e.Added "no added"
        ]

        testList "a section from a fixture diff" [
            let section =
                renderOne (release [ entryOf mixed ] [ "docs/migrations/0.2.0-alpha-widening.md" ])

            testCase "Added, Changed and Removed are each represented"
            <| fun () ->
                let ls = linesOf section
                Expect.contains ls "### Added" "Added heading"
                Expect.contains ls "### Changed" "Changed heading"
                Expect.contains ls "### Removed" "Removed heading"
                Expect.contains ls ("  - `" + fresh + "`") "the added member"
                Expect.contains ls ("  - `" + gone + "`") "the removed member"

                Expect.contains
                    ls
                    (sprintf "  - `Demo.T.Alpha` — `%s` → `%s`" oldSig newSig)
                    "the change as before → after"

            testCase "the heading carries the version and the tag's date"
            <| fun () -> Expect.stringStarts section "## [0.2.0] — 2026-01-02\n" "heading"

            testCase "the summary line carries the complete counts and the lockstep class"
            <| fun () ->
                Expect.contains
                    (linesOf section)
                    "_Surface since `v0.1.0`: **breaking** — 1 package moved; 1 member added, 1 member changed, 1 member removed._"
                    "summary"

            testCase "the migration note is linked, ahead of the lists"
            <| fun () ->
                let ls = linesOf section

                Expect.contains ls "- [0.2.0-alpha-widening](docs/migrations/0.2.0-alpha-widening.md)" "link"

                Expect.isLessThan
                    (List.findIndex ((=) "Migration notes:") ls)
                    (List.findIndex ((=) "### Added") ls)
                    "notes come first"

            testCase "an additive-only section carries no Changed or Removed heading"
            <| fun () ->
                let s =
                    renderOne (
                        release [
                            entryOf {
                                mixed with
                                    Removed = []
                                    Added = [ fresh ]
                                    Class = SemVerBump.Additive
                            }
                        ] []
                    )

                let ls = linesOf s
                Expect.contains ls "### Added" "Added"
                Expect.isFalse (List.contains "### Changed" ls) "no Changed"
                Expect.isFalse (List.contains "### Removed" ls) "no Removed"
                Expect.stringContains s "**additive**" "class"
        ]

        testList "idempotence" [
            testCase "rendering the same data twice is byte-identical"
            <| fun () ->
                let r = release [ entryOf mixed ] [ "docs/migrations/x.md" ]
                Expect.equal (render defaultLimits [ r ]) (render defaultLimits [ r ]) "same bytes"

            testCase "packages and docs in a different order render the same bytes"
            <| fun () ->
                let other =
                    entryOf {
                        mixed with
                            Package = "Demo.Aardvark"
                            Removed = []
                            Class = SemVerBump.Additive
                    }

                let a =
                    release [ entryOf mixed; other ] [ "docs/migrations/b.md"; "docs/migrations/a.md" ]

                let b =
                    release [ other; entryOf mixed ] [ "docs/migrations/a.md"; "docs/migrations/b.md" ]

                Expect.equal (renderOne a) (renderOne b) "order-insensitive"

            testCase "the whole file opens with the pinned header and ends with one newline"
            <| fun () ->
                let doc = render defaultLimits [ release [ entryOf mixed ] [] ]
                Expect.stringStarts doc header "header"
                Expect.stringEnds doc "\n" "trailing newline"
                Expect.isFalse (doc.EndsWith "\n\n") "exactly one"
        ]

        testList "caps and whole-package cases" [
            testCase "an Added list past its cap ends with the diff that shows the rest"
            <| fun () ->
                let many = [ for i in 1..25 -> sprintf "Demo.T.M%02d() : R" i ]

                let s =
                    renderOne (
                        release [
                            entryOf {
                                mixed with
                                    Removed = []
                                    Added = many
                                    Class = SemVerBump.Additive
                            }
                        ] []
                    )

                let ls = linesOf s
                Expect.contains ls "- `Demo.Mixed` — 25 members:" "complete count"

                Expect.contains
                    ls
                    "  - … and 5 more — `git diff v0.1.0 v0.2.0 -- api-baselines/Demo.Mixed.approved.txt`"
                    "the rest"

                Expect.isFalse (List.contains "  - `Demo.T.M25() : R`" ls) "past the cap is not listed"

            testCase "a Removed list under its cap is listed in full"
            <| fun () ->
                let many = [ for i in 1..30 -> sprintf "Demo.T.M%02d() : R" i ]

                let s =
                    renderOne (
                        release [
                            entryOf {
                                mixed with
                                    Removed = many
                                    Added = []
                            }
                        ] []
                    )

                let ls = linesOf s
                Expect.contains ls "  - `Demo.T.M30() : R`" "the thirtieth"
                Expect.isFalse (ls |> List.exists (fun l -> l.Contains "… and")) "nothing truncated"

            testCase "a new package is one line with its member count, under Added"
            <| fun () ->
                let s =
                    renderOne (
                        release [
                            entryOf {
                                mixed with
                                    Removed = []
                                    Added = [ fresh; newSig ]
                                    Introduced = true
                                    Class = SemVerBump.Additive
                            }
                        ] []
                    )

                let ls = linesOf s
                Expect.contains ls "- `Demo.Mixed` — new package (2 public members)" "one line"
                Expect.isFalse (List.contains ("  - `" + fresh + "`") ls) "members not listed"
                Expect.stringContains s "1 package new" "counted in the summary"

            testCase "a withdrawn package is one line naming the PackageReference, under Removed"
            <| fun () ->
                let s =
                    renderOne (
                        release [
                            entryOf {
                                mixed with
                                    Removed = [ gone; oldSig ]
                                    Added = []
                                    Withdrawn = true
                            }
                        ] []
                    )

                Expect.stringContains
                    s
                    "- `Demo.Mixed` — package withdrawn (2 public members); a consumer must drop the `PackageReference` before raising its pin"
                    "one line"

                Expect.stringContains s "**breaking**" "a withdrawal is breaking"

            testCase "a release that predates the baselines says so"
            <| fun () ->
                let s =
                    renderOne {
                        release [] [] with
                            Surface = NoBaselines
                    }

                Expect.stringContains s "_No public-API baselines were recorded at this release" "says so"
                Expect.isFalse (s.Contains "### ") "no lists"

            testCase "a release with baselines and no movement says that instead"
            <| fun () ->
                let s = renderOne (release [] [])
                Expect.stringContains s "_No public-surface change since `v0.1.0`._" "no change"

            testCase "the draft is headed unreleased and carries no date"
            <| fun () ->
                let many = [ for i in 1..25 -> sprintf "Demo.T.M%02d() : R" i ]

                let s =
                    renderOne {
                        release [ entryOf { mixed with Added = many } ] [] with
                            Tag = None
                            Date = None
                            Version = "0.3.0"
                    }

                Expect.stringStarts s "## [0.3.0] — unreleased\n" "heading"

                Expect.stringContains
                    s
                    "… and 5 more — `git diff v0.1.0 -- api-baselines/Demo.Mixed.approved.txt`"
                    "the diff hint has one side — the working tree is the other"
        ]

        testList "migration-doc attribution" [
            let tree = [
                "docs/migrations/0.2.0-alpha-widening.md"
                "docs/migrations/0.1.x-line-note.md"
                "docs/migrations/0.9.0-never-tagged.md"
                "docs/migrations/257-scorecard.md"
                "docs/migrations/inputs-pane-width.md"
            ]

            let released = [ "0.1.0"; "0.1.1"; "0.2.0" ]

            testCase "a doc claims the version its name carries"
            <| fun () ->
                Expect.equal (claimedVersion "docs/migrations/0.2.0-alpha-widening.md") (Some "0.2.0") "exact"
                Expect.equal (claimedVersion "docs/migrations/0.1.x-line-note.md") (Some "0.1.x") "line"
                Expect.equal (claimedVersion "docs/migrations/257-scorecard.md") None "phase number"
                Expect.equal (claimedVersion "docs/migrations/inputs-pane-width.md") None "subject"

            testCase "a release links the docs that claim it and the version-less docs its interval introduced"
            <| fun () ->
                Expect.equal
                    (docsForRelease "0.2.0" released tree [ "docs/migrations/257-scorecard.md" ])
                    [
                        "docs/migrations/0.2.0-alpha-widening.md"
                        "docs/migrations/257-scorecard.md"
                    ]
                    "claimed + introduced"

            testCase "an x-line claim attaches to every release on that line"
            <| fun () ->
                Expect.equal (docsForRelease "0.1.1" released tree []) [ "docs/migrations/0.1.x-line-note.md" ] "0.1.1"
                Expect.equal (docsForRelease "0.1.0" released tree []) [ "docs/migrations/0.1.x-line-note.md" ] "0.1.0"

            testCase "a doc claiming ANOTHER release is left to that release even when this interval introduced it"
            <| fun () ->
                Expect.equal
                    (docsForRelease "0.2.0" released tree [ "docs/migrations/0.1.x-line-note.md" ])
                    [ "docs/migrations/0.2.0-alpha-widening.md" ]
                    "not stolen"

            testCase "a claim no release answers to falls back to the interval that introduced it"
            <| fun () ->
                Expect.equal
                    (docsForRelease "0.2.0" released tree [ "docs/migrations/0.9.0-never-tagged.md" ])
                    [
                        "docs/migrations/0.2.0-alpha-widening.md"
                        "docs/migrations/0.9.0-never-tagged.md"
                    ]
                    "orphan attributed"

            testCase "the result is distinct and ordinal-sorted whatever the input order"
            <| fun () ->
                let a =
                    docsForRelease "0.2.0" released (List.rev tree) [
                        "docs/migrations/257-scorecard.md"
                        "docs/migrations/257-scorecard.md"
                    ]

                let b = docsForRelease "0.2.0" released tree [ "docs/migrations/257-scorecard.md" ]
                Expect.equal a b "same"
        ]

        testList "the committed CHANGELOG.md" [
            testCase "is the generator's own — it opens with the pinned header"
            <| fun () ->
                let path = Path.Combine(repoRoot (), "CHANGELOG.md")
                Expect.isTrue (File.Exists path) "CHANGELOG.md is committed at the repo root"
                let text = File.ReadAllText(path).Replace("\r\n", "\n")
                Expect.stringStarts text header "generated header"
                Expect.stringContains text "\n## [" "at least one release section"
        ]
    ]
module ToolUp.Platform.Tests.InProcess.OpenCoreVocabularyNeutralityTests

// ─── Phase 477 — tree-wide open-core vocabulary neutrality ────────────
//
// OSS-BOUNDARY-EXEMPT-FILE: this pack IS the boundary guard; the
// vocabulary it names is assertion data, not a leak.
//
// Before this pack the only neutrality guards scanned the Phase 202
// `samples/ToyTreeBinding/` toy, so a private-vocabulary reference
// anywhere ELSE in shipped source or docs was invisible until someone
// grepped for it. The 2026-07-06 sweep found exactly that: internal
// command names and private product names sitting in shipped
// `ToolUp.*.Server` comments, cleaned by hand in `9433dd8`. A hand
// sweep does not recur; a test does.
//
// What this pack adds is the SURFACE, not a second denylist: the
// vocabulary, the matching rule and the exemption markers all live in
// `NeutralityTokens`, which the Phase 202 toy guard reads too. There is
// one token source in the repo and it is not in the repo — see that
// module's header for why, and for the matching rule a failure cites.
//
// ─── The surface, derived rather than listed ──────────────────────────
//
// A hardcoded path list goes stale the first time someone adds a
// project, and goes stale silently. The surface is therefore computed
// from the same metadata the pack and publish paths already key on
// (`Directory.Build.props` conditions every packed item on
// `IsPackable != false`, and `publish-nuget.yml` packs every such
// project without naming any of them):
//
//   * every `.fs` whose NEAREST enclosing `.fsproj` under `src/` does
//     not opt out with `<IsPackable>false</IsPackable>` — this is both
//     the server-compiled sources and the `fable/`-delivered client
//     sources, because a Fable-delivered project is packable by the
//     same test;
//   * each such project's own `README.md`, which `Directory.Build.props`
//     packs into its nupkg (this is what covers the companion READMEs);
//   * `docs/**/*.md`;
//   * `README.md`, `CONTRIBUTING.md`, `SECURITY.md` at the repo root;
//   * `samples/**` — `.fs` and `.md` alike.
//
// A new packable project is therefore covered on the commit that adds
// it, with no edit here — the same property the publish workflow has.

open System
open System.IO
open Expecto

// ─── Surface enumeration ──────────────────────────────────────────────

let private repoRoot = NeutralityTokens.repoRootPath

let private isBuildOutput (path: string) =
    let n = path.Replace('\\', '/')

    n.Contains "/obj/"
    || n.Contains "/bin/"
    || n.Contains "/node_modules/"
    || n.Contains "/.git/"

/// Repo-relative, forward-slashed — the shape a contributor pastes back
/// into an editor.
let private label (path: string) =
    let full = Path.GetFullPath path

    if full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase) then
        full.Substring(repoRoot.Length).TrimStart([| '\\'; '/' |]).Replace('\\', '/')
    else
        full.Replace('\\', '/')

let private enumerate (dir: string) (pattern: string) =
    if Directory.Exists dir then
        Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
        |> Seq.filter (isBuildOutput >> not)
        |> List.ofSeq
    else
        []

/// Every `.fsproj` under `src/`, paired with whether it packs.
let private projects () =
    enumerate (Path.Combine(repoRoot, "src")) "*.fsproj"
    |> List.map (fun proj ->
        let packable =
            not ((File.ReadAllText proj).Replace(" ", "").Contains "<IsPackable>false</IsPackable>")

        Path.GetDirectoryName proj |> Path.GetFullPath, packable)

/// The nearest enclosing project decides a file's fate — a project
/// directory that contains another project's directory must not drag
/// that project's opt-out (or opt-in) along with it.
let private nearestProject (projects: (string * bool) list) (file: string) =
    let full = Path.GetFullPath file

    projects
    |> List.filter (fun (dir, _) ->
        full.StartsWith(dir + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    |> function
        | [] -> None
        | candidates -> candidates |> List.maxBy (fst >> String.length) |> Some

/// The publishable surface: absolute paths, deduplicated, ordered.
let publishableFiles () : string list =
    let projs = projects ()
    let srcDir = Path.Combine(repoRoot, "src")

    let packableSource =
        enumerate srcDir "*.fs"
        |> List.filter (fun f ->
            match nearestProject projs f with
            | Some(_, packable) -> packable
            | None -> false)

    let packableReadmes =
        projs
        |> List.filter snd
        |> List.map (fun (dir, _) -> Path.Combine(dir, "README.md"))
        |> List.filter File.Exists

    let docs = enumerate (Path.Combine(repoRoot, "docs")) "*.md"

    let rootDocs =
        [ "README.md"; "CONTRIBUTING.md"; "SECURITY.md" ]
        |> List.map (fun n -> Path.Combine(repoRoot, n))
        |> List.filter File.Exists

    let samplesDir = Path.Combine(repoRoot, "samples")
    let samples = enumerate samplesDir "*.fs" @ enumerate samplesDir "*.md"

    packableSource @ packableReadmes @ docs @ rootDocs @ samples
    |> List.map Path.GetFullPath
    |> List.distinctBy _.ToLowerInvariant()
    |> List.sort

// ─── The guard ────────────────────────────────────────────────────────

let private surfaceTests =
    testList "the publishable surface is neutral" [

        testCase "the derived surface is non-empty and covers each declared class"
        <| fun _ ->
            let files = publishableFiles () |> List.map label
            Expect.isNonEmpty files "the publishable surface must be locatable from the test assembly"

            let has (predicate: string -> bool) (what: string) =
                Expect.isTrue (files |> List.exists predicate) $"the surface must include {what}"

            has (fun f -> f.StartsWith "src/" && f.EndsWith ".fs") "packable src sources"
            has (fun f -> f.StartsWith "docs/") "docs/**"
            has (fun f -> f = "README.md") "the root README"
            has (fun f -> f.StartsWith "samples/") "samples/**"
            has (fun f -> f.StartsWith "src/" && f.EndsWith "/README.md") "packable-project READMEs"

            // The guard's own pack opts out of packing, so it is out of
            // scope by construction as well as by marker.
            Expect.isFalse
                (files |> List.exists (fun f -> f.StartsWith "src/ToolUp.Platform.Tests/"))
                "an IsPackable=false project contributes nothing to the surface"

        testCase "no publicly-shipped file references private vocabulary"
        <| fun _ ->
            let hits =
                publishableFiles ()
                |> List.collect (fun path -> NeutralityTokens.scanFile (label path) path)

            match hits with
            | [] -> ()
            | _ -> failtest (NeutralityTokens.renderHits hits)

            NeutralityTokens.skipUnlessExternalSource ()
    ]

// ─── Go-red proofs: the mechanism, not just its verdict ───────────────
//
// A scan that reports clean is worth nothing until it has been shown to
// report a leak. These cases run the real matcher over synthetic
// content, so they prove the guard on every machine — including public
// CI, where only the canary token is active.

/// The token these proofs inject: a real private one where the external
/// source supplies it, the public-safe canary otherwise.
let private probeToken =
    NeutralityTokens.activeTokens
    |> List.tryFind (fun t -> t <> NeutralityTokens.Canary)
    |> Option.defaultValue NeutralityTokens.Canary

let private goRedTests =
    testList "the guard catches what it claims to catch" [

        testCase "an injected reference in a shipped source file is reported with file, line and token"
        <| fun _ ->
            let injected = [|
                "module Sample.Shipped"
                ""
                "/// A perfectly ordinary doc comment."
                $"// see the {probeToken} note for background"
                "let value = 1"
            |]

            let hits = NeutralityTokens.scanLines "src/Sample/Shipped.fs" injected
            Expect.isNonEmpty hits "the injected reference must be reported"
            Expect.all hits (fun h -> h.Line = 4) "every hit is on the injected line"
            Expect.all hits (fun h -> h.File = "src/Sample/Shipped.fs") "every hit names the scanned file"

            Expect.isTrue
                (hits |> List.exists (fun h -> h.Token = probeToken))
                "the offending token is named in the report"

            let rendered = NeutralityTokens.renderHits hits
            Expect.stringContains rendered "src/Sample/Shipped.fs:4" "the message carries file:line"
            Expect.stringContains rendered NeutralityTokens.PolicyDoc "the message points at the policy"

        testCase "a block-level exemption marker covers its block and stops at the blank line"
        <| fun _ ->
            // A table is the shape the block rule exists for: the marker
            // goes on a row that can hold it, and covers the rows that
            // cannot.
            let content = [|
                $"| {probeToken} | a contract id |"
                $"| {probeToken} | another, covered by the same marker |"
                $"| a third row | {NeutralityTokens.ExemptLineMarker}: sanctioned public-spec citation |"
                ""
                $"// {probeToken} past the blank line is NOT exempt"
            |]

            let hits = NeutralityTokens.scanLines "docs/probe.md" content
            Expect.isNonEmpty hits "the line past the block boundary must still be reported"
            Expect.all hits (fun h -> h.Line = 5) "the marker covers its own block and no more"

        testCase "a file-level exemption marker takes the whole file out of scope"
        <| fun _ ->
            let content = [|
                $"// {NeutralityTokens.ExemptFileMarker}: this file is the guard's own data"
                $"// {probeToken}"
                $"// {probeToken}"
            |]

            Expect.isEmpty
                (NeutralityTokens.scanLines "docs/probe.md" content)
                "a file marker suppresses every line in the file"

        testCase "the word-boundary rule keeps a common English substring from firing"
        <| fun _ ->
            // `Concord` is representative of the class: the boundary
            // rule is what stops `concordance` making the gate noisy
            // enough to ignore. Proven on the compiled rule itself, so
            // the assertion holds whatever the private list contains.
            let rx = NeutralityTokens.toRegex "Concord"
            Expect.isFalse (rx.IsMatch "a concordance of terms") "a longer word must not fire"
            Expect.isFalse (rx.IsMatch "unconcord") "a prefixed word must not fire"
            Expect.isTrue (rx.IsMatch "the CONCORD product") "the word itself fires, case-insensitively"

        testCase "a boundary that is not a word character still fires"
        <| fun _ ->
            let rx = NeutralityTokens.toRegex "Widget"
            Expect.isTrue (rx.IsMatch "vnd.widget.model-spec") "dots are not word characters"
            Expect.isTrue (rx.IsMatch "widget-federation-spec") "hyphens are not word characters"

        testCase "a multi-word token matches across a wrapped comment line"
        <| fun _ ->
            let rx = NeutralityTokens.toRegex "Some Command"

            Expect.isTrue
                (rx.IsMatch "// ... invoked by Some    Command at the end")
                "internal whitespace matches any whitespace run"

            Expect.isFalse (rx.IsMatch "// SomeCommand") "the words must still be separate"

        testCase "the re: escape hatch compiles as a raw regex"
        <| fun _ ->
            let rx = NeutralityTokens.toRegex @"re:proj-\d{3}"
            Expect.isTrue (rx.IsMatch "see PROJ-402") "raw patterns stay case-insensitive"
            Expect.isFalse (rx.IsMatch "see proj-4") "raw patterns are honoured verbatim"

        testCase "the canary is always active, with or without a private source"
        <| fun _ ->
            Expect.contains
                NeutralityTokens.activeTokens
                NeutralityTokens.Canary
                "the public-safe canary must be enforced even when no external source exists"
    ]

let tests =
    testList "OpenCoreVocabularyNeutrality (Phase 477)" [ surfaceTests; goRedTests ]
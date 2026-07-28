// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.RegularExpressions
open System.Xml.Linq

// ─── Phase 586 — packaged-module shadow-project conformance ──────────
//
// A packaged module ships its Fable client source *as source*: the
// `.fs` files (and the project file that orders them) are packed into
// the nupkg under `fable/`, and a Fable consumer's package loader
// extracts and compiles them as part of its own client compilation.
// The in-tree convention is the `<Content Include="**\*.fsproj;**\*.fs"
// Exclude="**\*.fs.js;**\bin\**;**\obj\**" PackagePath="fable\" />`
// item every client-tier package carries.
//
// Nothing guards that convention. Four drift classes ship silently and
// are discovered by the CONSUMER's Fable build rather than the module's
// own CI:
//
//   1. a client file the shadow project does not list (or lists but the
//      main project never declares),
//   2. a server-only file leaking into the Fable-compiled set,
//   3. compile-order drift between the main project and the shadow
//      (F# compile order is semantic — a swap is a compile error
//      downstream, not a warning),
//   4. an asset (or the shadow project file itself) that never made it
//      into the packed `fable/` layout.
//
// This module states each as a named LAW over two parsed project files
// plus a pack manifest, and checks them without building anything —
// no MSBuild evaluation, no Fable invocation, no consumer app.
//
// ── On "shadow project" vs the shape actually in the tree ──
// The phase brief describes two distinct project files (main + shadow).
// The convention as it exists in this repo packs the module's OWN
// project file under `fable/`, so for a single-project packaged module
// `main` and `shadow` are the same document — pass it twice and the
// subset / order laws hold trivially while the exclusion and asset-path
// laws still bite (they are the ones that catch a glob packing a
// server-only file, or an icon whose `PackagePath` was never declared).
// The two-document form is what a module using the four-file consumer
// convention needs — there the main project `<Compile>`s the server
// tier and `<None>`s the client files, while a separate shadow project
// (or `.Client.props`) carries the client compile list, and all four
// laws are load-bearing. The checker takes two source lists so both
// shapes are expressible; nothing requires them to be distinct files.

/// Phase 586 — the four packaging laws a packaged module's Fable shadow
/// project must satisfy. Every conformance failure names exactly one.
type ShadowLayoutLaw =
    /// The shadow project's `Compile` set corresponds to the main
    /// project's declared client files — nothing extra, nothing missing.
    | ShadowSubsetLaw
    /// No file the module declares server-only is Fable-compiled or
    /// packed under the Fable root.
    | ShadowExclusionLaw
    /// Files common to both projects appear in the same relative order.
    /// F# compile order is semantic, so a swap is a downstream break.
    | ShadowCompileOrderLaw
    /// The shadow project file, every file it compiles, and every
    /// declared asset are present in the packed `fable/` layout.
    | ShadowAssetPathLaw

module ShadowLayoutLaw =

    /// Stable machine-readable law id, surfaced in every rendered
    /// violation so a failure is greppable and a test can assert on it.
    let name law =
        match law with
        | ShadowSubsetLaw -> "shadow-subset"
        | ShadowExclusionLaw -> "server-exclusion"
        | ShadowCompileOrderLaw -> "compile-order"
        | ShadowAssetPathLaw -> "asset-path"

    /// One-line statement of what the law requires.
    let describe law =
        match law with
        | ShadowSubsetLaw -> "the shadow project's Compile set corresponds to the main project's declared client files"
        | ShadowExclusionLaw -> "declared server-only files are neither Fable-compiled nor packed under the Fable root"
        | ShadowCompileOrderLaw -> "files common to both projects appear in the same relative compile order"
        | ShadowAssetPathLaw ->
            "the shadow project file, its compiled sources and the declared assets are present in the packed layout"

    let all = [
        ShadowSubsetLaw
        ShadowExclusionLaw
        ShadowCompileOrderLaw
        ShadowAssetPathLaw
    ]

/// A single conformance failure: which law broke, over which file, and
/// why.
type ShadowLayoutViolation = {
    /// The law this failure breaks.
    ViolatedLaw: ShadowLayoutLaw
    /// The file / package path the failure is about.
    Subject: string
    /// Human-readable explanation, naming the drift concretely.
    Explanation: string
}

module ShadowLayoutViolation =

    /// `[law-id] subject — explanation`. The law id is always present,
    /// so an operator reading a failed build (or a test asserting on
    /// the output) can attribute the failure without a lookup table.
    let render (v: ShadowLayoutViolation) =
        $"[{ShadowLayoutLaw.name v.ViolatedLaw}] {v.Subject} — {v.Explanation}"

/// A project file's declared source items, parsed from its XML. Both
/// the main project and the shadow project are represented this way;
/// for a single-project packaged module the same value is passed twice.
type ShadowSourceList = {
    /// Label used in violation messages (conventionally the project
    /// file name).
    ProjectLabel: string
    /// Every source file the project DECLARES, in declaration order —
    /// `<Compile>` and `<None>` alike. The four-file module convention
    /// declares its client files as `<None>` in the main project (they
    /// are compiled by the consumer's Fable project, not by this one),
    /// so the client set is drawn from here rather than from
    /// `CompiledFiles`.
    DeclaredOrder: string list
    /// The project's `<Compile>` items only, in declaration order. On a
    /// shadow project this is the Fable compile list.
    CompiledFiles: string list
    /// Include patterns that carried a wildcard and could not be
    /// expanded (no root directory was supplied). Non-empty means the
    /// subset law is UNDECIDABLE, and `check` says so rather than
    /// passing quietly.
    UnresolvedPatterns: string list
}

module ShadowSourceList =

    let empty label = {
        ProjectLabel = label
        DeclaredOrder = []
        CompiledFiles = []
        UnresolvedPatterns = []
    }

/// The set of package paths a nupkg carries (or will carry) — the
/// "pack manifest". Sourced from the produced `.nupkg`, from a staged
/// directory, or — the pre-Pack case — from the main project's own
/// `PackagePath`-bearing item declarations.
type PackagedModuleManifest = {
    /// Label used in violation messages (package id, nupkg file name,
    /// or the project whose declarations produced it).
    ManifestLabel: string
    /// Package paths inside the package, `/`-separated.
    PackagePaths: string list
}

module PackagedModuleManifest =

    let empty label = {
        ManifestLabel = label
        PackagePaths = []
    }

/// What a packaged module declares about its own layout — the contract
/// the four laws are checked against. Every field is a DECLARATION by
/// the module author; the laws check the projects and the pack against
/// it, so nothing here is inferred and nothing is tautological.
type PackagedModuleContract = {
    /// Label used in violation messages.
    ModuleLabel: string
    /// Source files that are server-tier only and must never reach the
    /// Fable compilation (e.g. `Server.fs`).
    ServerOnlyFiles: string list
    /// Directory prefixes whose every file is server-only (e.g.
    /// `Server/`). Matched against the normalised relative path.
    ServerOnlyDirectories: string list
    /// Non-source assets (icons, svg, css) that must be packed
    /// alongside the shadow project, relative to the Fable root.
    RequiredAssets: string list
    /// Package-path root the Fable source ships under. `fable` by
    /// convention — the path Fable's package loader probes.
    FableRoot: string
    /// The shadow project file's path relative to the Fable root, as
    /// it must appear in the packed layout.
    ShadowProjectFile: string
}

module PackagedModuleContract =

    /// A contract that declares nothing beyond the conventional Fable
    /// root — every field a module author is expected to fill in starts
    /// empty, so an unfilled contract checks the laws it can and never
    /// silently asserts something the author did not declare.
    let create moduleLabel shadowProjectFile = {
        ModuleLabel = moduleLabel
        ServerOnlyFiles = []
        ServerOnlyDirectories = []
        RequiredAssets = []
        FableRoot = "fable"
        ShadowProjectFile = shadowProjectFile
    }

/// Where the pack manifest is read from. `FromPackDeclarations` is the
/// pre-Pack source (nothing has been packed yet — the manifest is what
/// the project's `PackagePath` items SAY will be packed); the other two
/// read a real artefact.
type PackagedModuleManifestSource =
    /// Derive the manifest from the main project's own
    /// `PackagePath`-bearing `<Content>` / `<None>` / `<Compile>` items,
    /// expanded against the project directory. The pre-Pack default.
    | FromPackDeclarations
    /// Read the entry list of a produced `.nupkg`.
    | FromNupkg of nupkgPath: string
    /// Read a staged directory whose layout mirrors the package root.
    | FromStagedDirectory of directory: string

/// Everything the check needs, in one record — what the FAKE target and
/// the test helper both bind against.
type PackagedModuleCheckOptions = {
    /// Path to the module's own project file.
    MainProject: string
    /// Path to the Fable-side shadow project file. Equal to
    /// `MainProject` for the single-project source-in-nupkg convention.
    ShadowProject: string
    /// Where the pack manifest comes from.
    ManifestSource: PackagedModuleManifestSource
    /// The module's declared layout contract.
    Contract: PackagedModuleContract
}

module PackagedModuleCheckOptions =

    /// Single-project source-in-nupkg shape: the module's own project
    /// file is also the shadow, and the manifest is derived pre-Pack
    /// from its `PackagePath` declarations.
    let forProject (projectPath: string) =
        let fileName = Path.GetFileName projectPath

        {
            MainProject = projectPath
            ShadowProject = projectPath
            ManifestSource = FromPackDeclarations
            Contract = PackagedModuleContract.create fileName fileName
        }

/// Phase 586 — the conformance check itself. `check` is pure: it takes
/// two already-parsed source lists plus a manifest and returns the
/// violations. `Load` carries the (impure) parsing / archive reading,
/// and `verify` / `assertConformant` / `registerTarget` are the three
/// call shapes on top.
module PackagedModuleConformance =

    // ─── Path normalisation ──────────────────────────────────────────
    //
    // Project files are authored with `\` on Windows and `/` elsewhere;
    // package paths are always `/`. Everything is normalised to `/` and
    // compared case-insensitively — a case-only difference between an
    // fsproj include and a packed entry is a portability defect in its
    // own right, but not one this check is trying to name, and treating
    // it as a mismatch would produce noise on Windows-authored modules.

    let internal normalisePath (p: string) =
        let t = p.Replace('\\', '/').Trim()

        let t =
            if t.StartsWith("./", StringComparison.Ordinal) then
                t.Substring 2
            else
                t

        t.Trim('/')

    let private key (p: string) = (normalisePath p).ToLowerInvariant()

    let private joinPackagePath (root: string) (rel: string) =
        let r = normalisePath root
        let s = normalisePath rel
        if String.IsNullOrEmpty r then s else $"{r}/{s}"

    // ─── Server-only classification ──────────────────────────────────

    /// Does the module's contract declare this file server-only?
    let isServerOnly (contract: PackagedModuleContract) (file: string) =
        let k = key file

        let byFile = contract.ServerOnlyFiles |> List.exists (fun s -> key s = k)

        let byDirectory =
            contract.ServerOnlyDirectories
            |> List.exists (fun d ->
                let dk = (key d).TrimEnd('/') + "/"

                not (String.IsNullOrEmpty(dk.TrimEnd('/')))
                && k.StartsWith(dk, StringComparison.Ordinal))

        byFile || byDirectory

    /// The main project's declared CLIENT files: everything it declares
    /// as source, minus everything the contract calls server-only.
    let clientFiles (contract: PackagedModuleContract) (main: ShadowSourceList) =
        main.DeclaredOrder |> List.filter (isServerOnly contract >> not)

    // ─── The four laws ───────────────────────────────────────────────

    let private violation law subject explanation = {
        ViolatedLaw = law
        Subject = subject
        Explanation = explanation
    }

    /// Law 1 — shadow subset. The shadow's `Compile` set and the main
    /// project's client-file set must correspond: a shadow entry the
    /// main project never declared is a phantom, and a declared client
    /// file the shadow omits is the "missing client file" drift (the
    /// consumer's Fable build fails on an unresolved module).
    ///
    /// A server-only file in the shadow is NOT reported here — that is
    /// the exclusion law's subject, and reporting it twice would make
    /// one defect look like two.
    let checkSubset (contract: PackagedModuleContract) (main: ShadowSourceList) (shadow: ShadowSourceList) =
        let clientSet = clientFiles contract main
        let clientKeys = clientSet |> List.map key |> Set.ofList
        let shadowKeys = shadow.CompiledFiles |> List.map key |> Set.ofList

        [
            // An unexpanded wildcard means the sets are unknown. Say so
            // rather than passing on an empty comparison.
            for p in main.UnresolvedPatterns do
                violation
                    ShadowSubsetLaw
                    p
                    $"'{main.ProjectLabel}' declares this include as a wildcard that was not expanded — the subset law cannot be decided; load the project with a root directory"

            for p in shadow.UnresolvedPatterns do
                violation
                    ShadowSubsetLaw
                    p
                    $"'{shadow.ProjectLabel}' declares this include as a wildcard that was not expanded — the subset law cannot be decided; load the project with a root directory"

            for f in shadow.CompiledFiles do
                if not (clientKeys.Contains(key f)) && not (isServerOnly contract f) then
                    violation
                        ShadowSubsetLaw
                        f
                        $"compiled by the shadow project '{shadow.ProjectLabel}' but never declared by '{main.ProjectLabel}'"

            for f in clientSet do
                if not (shadowKeys.Contains(key f)) then
                    violation
                        ShadowSubsetLaw
                        f
                        $"declared as a client file by '{main.ProjectLabel}' but missing from the shadow project '{shadow.ProjectLabel}' Compile list"
        ]

    /// Law 2 — server exclusion. A file the module declares server-only
    /// must be neither Fable-compiled by the shadow nor present under
    /// the packed Fable root. The second half catches the common
    /// single-project failure: a `**\*.fs` pack glob with no `Exclude`
    /// for the server tier.
    let checkExclusion
        (contract: PackagedModuleContract)
        (shadow: ShadowSourceList)
        (manifest: PackagedModuleManifest)
        =
        let root = normalisePath contract.FableRoot

        let rootPrefix =
            if String.IsNullOrEmpty root then
                ""
            else
                root.ToLowerInvariant() + "/"

        let leakedCompiles = [
            for f in shadow.CompiledFiles do
                if isServerOnly contract f then
                    violation
                        ShadowExclusionLaw
                        f
                        $"declared server-only by '{contract.ModuleLabel}' but compiled by the shadow project '{shadow.ProjectLabel}'"
        ]

        let alreadyNamed = leakedCompiles |> List.map (fun v -> key v.Subject) |> Set.ofList

        let leakedPacks = [
            for entry in manifest.PackagePaths do
                let k = key entry

                if
                    String.IsNullOrEmpty rootPrefix
                    || k.StartsWith(rootPrefix, StringComparison.Ordinal)
                then
                    let relative =
                        if String.IsNullOrEmpty rootPrefix then
                            normalisePath entry
                        else
                            (normalisePath entry).Substring(rootPrefix.Length)

                    if isServerOnly contract relative && not (alreadyNamed.Contains(key relative)) then
                        violation
                            ShadowExclusionLaw
                            entry
                            $"declared server-only by '{contract.ModuleLabel}' but packed under '{root}/' in '{manifest.ManifestLabel}'"
        ]

        leakedCompiles @ leakedPacks

    /// Law 3 — compile order. F# compile order is semantic: a file may
    /// only reference what compiled before it. Any pair of files the
    /// shadow compiles in the opposite relative order to the main
    /// project's declaration order is a break the consumer discovers,
    /// so each inverted ADJACENT pair is named (adjacent, so one swap
    /// reports as one violation rather than as a cascade).
    let checkCompileOrder (contract: PackagedModuleContract) (main: ShadowSourceList) (shadow: ShadowSourceList) =
        let mainIndex =
            clientFiles contract main |> List.mapi (fun i f -> key f, i) |> Map.ofList

        let common =
            shadow.CompiledFiles |> List.filter (fun f -> mainIndex.ContainsKey(key f))

        [
            for (earlier, later) in List.pairwise common do
                if mainIndex[key earlier] > mainIndex[key later] then
                    violation
                        ShadowCompileOrderLaw
                        later
                        $"compiled after '{earlier}' by the shadow project '{shadow.ProjectLabel}', but declared before it by '{main.ProjectLabel}'"
        ]

    /// Law 4 — asset path. Everything the Fable consumer needs must
    /// actually be in the packed layout under the Fable root: the
    /// shadow project file (Fable's loader reads it for the compile
    /// order), every file that project compiles, and every asset the
    /// module declares.
    let checkAssetPaths
        (contract: PackagedModuleContract)
        (shadow: ShadowSourceList)
        (manifest: PackagedModuleManifest)
        =
        let packed = manifest.PackagePaths |> List.map key |> Set.ofList
        let root = normalisePath contract.FableRoot

        let required =
            [ contract.ShadowProjectFile ] @ shadow.CompiledFiles @ contract.RequiredAssets
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> List.distinctBy key

        [
            for r in required do
                let expected = joinPackagePath root r

                if not (packed.Contains(key expected)) then
                    violation
                        ShadowAssetPathLaw
                        r
                        $"expected at '{expected}' in the pack manifest '{manifest.ManifestLabel}', which does not carry it"
        ]

    /// The whole check — pure. Violations are returned law-by-law in a
    /// stable order (subset, exclusion, order, asset-path) so a report
    /// diffs cleanly between runs.
    let check
        (contract: PackagedModuleContract)
        (main: ShadowSourceList)
        (shadow: ShadowSourceList)
        (manifest: PackagedModuleManifest)
        : ShadowLayoutViolation list =
        checkSubset contract main shadow
        @ checkExclusion contract shadow manifest
        @ checkCompileOrder contract main shadow
        @ checkAssetPaths contract shadow manifest

    /// Human-readable multi-line report. Empty violation list renders
    /// as a single conformant line naming the module.
    let report (contract: PackagedModuleContract) (violations: ShadowLayoutViolation list) =
        if List.isEmpty violations then
            $"[packaged-module] '{contract.ModuleLabel}' — conformant ({List.length ShadowLayoutLaw.all} laws checked)."
        else
            let header =
                $"[packaged-module] '{contract.ModuleLabel}' — {List.length violations} conformance violation(s):"

            let lines = violations |> List.map (fun v -> "  " + ShadowLayoutViolation.render v)

            String.Join(Environment.NewLine, header :: lines)

    // ─── Loading (the impure half) ───────────────────────────────────

    /// Parsing project XML and reading package archives. Kept separate
    /// from `check` so the laws stay pure and fixture-testable.
    module Load =

        /// Does this include carry a wildcard?
        let internal hasWildcard (pattern: string) =
            pattern.Contains '*' || pattern.Contains '?'

        /// Translate an MSBuild-style glob to a regex over `/`-separated
        /// relative paths. `**` spans path segments; `*` and `?` do not.
        let internal globToRegex (pattern: string) =
            let p = normalisePath pattern
            let sb = StringBuilder()
            sb.Append '^' |> ignore
            let mutable i = 0

            while i < p.Length do
                if p[i] = '*' && i + 1 < p.Length && p[i + 1] = '*' then
                    i <- i + 2

                    if i < p.Length && p[i] = '/' then
                        // `**/` — zero or more whole segments.
                        i <- i + 1
                        sb.Append "(?:.*/)?" |> ignore
                    else
                        // trailing `**` — anything at all.
                        sb.Append ".*" |> ignore
                elif p[i] = '*' then
                    sb.Append "[^/]*" |> ignore
                    i <- i + 1
                elif p[i] = '?' then
                    sb.Append "[^/]" |> ignore
                    i <- i + 1
                else
                    sb.Append(Regex.Escape(string p[i])) |> ignore
                    i <- i + 1

            sb.Append '$' |> ignore
            Regex(sb.ToString(), RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

        /// Every file under `root`, as normalised relative paths, with
        /// build output and package caches pruned. Sorted, so expansion
        /// is deterministic across machines.
        let internal enumerateFiles (root: string) =
            if not (Directory.Exists root) then
                []
            else
                let pruned = [ "bin/"; "obj/"; "node_modules/"; "output/" ]

                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                |> Seq.map (fun f -> normalisePath (Path.GetRelativePath(root, f)))
                |> Seq.filter (fun rel ->
                    let lower = rel.ToLowerInvariant()

                    pruned
                    |> List.forall (fun p ->
                        not (lower.StartsWith(p, StringComparison.Ordinal) || lower.Contains("/" + p))))
                |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
                |> List.ofSeq

        /// Expand one include attribute (which may be a `;`-separated
        /// list) against `root`, honouring an optional `Exclude`. With
        /// no root, wildcard patterns come back unexpanded in the
        /// second element so the caller can report them.
        let internal expandInclude (root: string option) (includeAttr: string) (excludeAttr: string) =
            let includes =
                includeAttr.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                |> List.ofArray

            let excludes =
                if String.IsNullOrWhiteSpace excludeAttr then
                    []
                else
                    excludeAttr.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                    |> Array.map globToRegex
                    |> List.ofArray

            let excluded (rel: string) =
                excludes |> List.exists (fun r -> r.IsMatch rel)

            let resolved = ResizeArray<string>()
            let unresolved = ResizeArray<string>()

            for inc in includes do
                if hasWildcard inc then
                    match root with
                    | None -> unresolved.Add(normalisePath inc)
                    | Some r ->
                        let rx = globToRegex inc

                        for f in enumerateFiles r do
                            if rx.IsMatch f && not (excluded f) then
                                resolved.Add f
                else
                    let n = normalisePath inc

                    if not (excluded n) then
                        resolved.Add n

            List.ofSeq resolved, List.ofSeq unresolved

        let private attr (e: XElement) (attrName: string) =
            let a = e.Attribute(XName.Get attrName)
            if isNull a then "" else a.Value

        /// Item elements (`Compile` / `None` / `Content` / …) sitting
        /// directly under an `ItemGroup`. Matched on `LocalName`, so a
        /// project authored with or without the legacy MSBuild xmlns
        /// parses identically.
        let private items (doc: XDocument) =
            if isNull doc.Root then
                []
            else
                doc.Root.Descendants()
                |> Seq.filter (fun e -> not (isNull e.Parent) && e.Parent.Name.LocalName = "ItemGroup")
                |> List.ofSeq

        let private isSourceFile (p: string) =
            let lower = (normalisePath p).ToLowerInvariant()

            lower.EndsWith(".fs", StringComparison.Ordinal)
            || lower.EndsWith(".fsi", StringComparison.Ordinal)

        /// Parse a project file's declared source items. `root` is the
        /// directory wildcard includes expand against — `None` leaves
        /// them unexpanded (and the subset law then reports them as
        /// undecidable rather than passing silently).
        let sourceListFromXml (label: string) (root: string option) (xml: string) : ShadowSourceList =
            let doc = XDocument.Parse xml
            let all = items doc

            let compiled = ResizeArray<string>()
            let declared = ResizeArray<string>()
            let unresolved = ResizeArray<string>()

            for e in all do
                let localName = e.Name.LocalName

                if localName = "Compile" || localName = "None" then
                    let resolved, unres = expandInclude root (attr e "Include") (attr e "Exclude")
                    unresolved.AddRange unres

                    for r in resolved do
                        if isSourceFile r then
                            declared.Add r

                            if localName = "Compile" then
                                compiled.Add r

            {
                ProjectLabel = label
                DeclaredOrder = List.ofSeq declared |> List.distinctBy (fun p -> p.ToLowerInvariant())
                CompiledFiles = List.ofSeq compiled |> List.distinctBy (fun p -> p.ToLowerInvariant())
                UnresolvedPatterns = List.ofSeq unresolved |> List.distinct
            }

        /// Read + parse a project file, expanding wildcards against its
        /// own directory.
        let sourceList (projectPath: string) : ShadowSourceList =
            let full = Path.GetFullPath projectPath
            let root = Path.GetDirectoryName full
            sourceListFromXml (Path.GetFileName full) (Some root) (File.ReadAllText full)

        /// Derive the pre-Pack manifest from a project's own
        /// `PackagePath`-bearing items — what the project SAYS it will
        /// pack, which is exactly the thing a pre-Pack gate can check.
        ///
        /// NuGet's path mapping is reproduced: a wildcard include lands
        /// each match under `PackagePath` at its path relative to the
        /// glob's fixed prefix; a literal include lands flattened to its
        /// file name.
        let packDeclarationsFromXml (label: string) (root: string option) (xml: string) : PackagedModuleManifest =
            let doc = XDocument.Parse xml
            let paths = ResizeArray<string>()

            for e in items doc do
                let packagePathAttr = attr e "PackagePath"
                let packAttr = attr e "Pack"

                let packDisabled = packAttr.Equals("false", StringComparison.OrdinalIgnoreCase)

                if not (String.IsNullOrWhiteSpace packagePathAttr) && not packDisabled then
                    let packageRoot = normalisePath packagePathAttr
                    let includeAttr = attr e "Include"
                    let excludeAttr = attr e "Exclude"

                    let singles =
                        includeAttr.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                        |> List.ofArray

                    for single in singles do
                        let resolved, _ = expandInclude root single excludeAttr

                        if hasWildcard single then
                            // Fixed prefix = the segments before the
                            // first wildcard-bearing one; matches keep
                            // their path relative to it.
                            let fixedPrefix =
                                (normalisePath single).Split '/'
                                |> Array.takeWhile (hasWildcard >> not)
                                |> String.concat "/"

                            for r in resolved do
                                let rel =
                                    if
                                        String.IsNullOrEmpty fixedPrefix
                                        || not (r.StartsWith(fixedPrefix + "/", StringComparison.OrdinalIgnoreCase))
                                    then
                                        r
                                    else
                                        r.Substring(fixedPrefix.Length + 1)

                                paths.Add(
                                    if String.IsNullOrEmpty packageRoot then
                                        rel
                                    else
                                        $"{packageRoot}/{rel}"
                                )
                        else
                            for r in resolved do
                                let fileName = Path.GetFileName r

                                paths.Add(
                                    if String.IsNullOrEmpty packageRoot then
                                        fileName
                                    else
                                        $"{packageRoot}/{fileName}"
                                )

            {
                ManifestLabel = label
                PackagePaths = List.ofSeq paths |> List.distinctBy (fun p -> p.ToLowerInvariant())
            }

        /// `packDeclarationsFromXml` over a project file on disk.
        let packDeclarations (projectPath: string) : PackagedModuleManifest =
            let full = Path.GetFullPath projectPath
            let root = Path.GetDirectoryName full

            packDeclarationsFromXml $"{Path.GetFileName full} (declared)" (Some root) (File.ReadAllText full)

        /// Entry list of a produced `.nupkg`.
        let manifestFromNupkg (nupkgPath: string) : PackagedModuleManifest =
            use archive = ZipFile.OpenRead nupkgPath

            {
                ManifestLabel = Path.GetFileName nupkgPath
                PackagePaths =
                    archive.Entries
                    |> Seq.map (fun e -> normalisePath e.FullName)
                    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                    |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
                    |> List.ofSeq
            }

        /// A staged directory whose layout mirrors the package root.
        let manifestFromDirectory (directory: string) : PackagedModuleManifest = {
            ManifestLabel = directory
            PackagePaths = enumerateFiles (Path.GetFullPath directory)
        }

        /// Resolve the configured manifest source.
        let manifest (options: PackagedModuleCheckOptions) : PackagedModuleManifest =
            match options.ManifestSource with
            | FromPackDeclarations -> packDeclarations options.MainProject
            | FromNupkg path -> manifestFromNupkg path
            | FromStagedDirectory dir -> manifestFromDirectory dir

    // ─── Call shapes ─────────────────────────────────────────────────

    /// Load the two projects + the manifest and run the check. Builds
    /// nothing — no MSBuild evaluation, no Fable, no consumer app.
    let verify (options: PackagedModuleCheckOptions) : ShadowLayoutViolation list =
        let main = Load.sourceList options.MainProject

        let shadow =
            if
                String.Equals(
                    Path.GetFullPath options.MainProject,
                    Path.GetFullPath options.ShadowProject,
                    StringComparison.OrdinalIgnoreCase
                )
            then
                main
            else
                Load.sourceList options.ShadowProject

        check options.Contract main shadow (Load.manifest options)

    /// Test-helper shape: bind this in a packaged module's own test
    /// project. Raises with the full report on any violation; returns
    /// unit when conformant. Framework-neutral by construction — the
    /// Build package carries no test-framework dependency, so an
    /// Expecto / xUnit / NUnit pack binds it the same way.
    let assertConformant (options: PackagedModuleCheckOptions) : unit =
        let violations = verify options

        if not (List.isEmpty violations) then
            failwith (report options.Contract violations)

    /// FAKE's `Target` module cannot be reached fully-qualified from
    /// here — `Fake.Core.Target` binds to the `KnownTags.Target` union
    /// case first — so the FAKE surface is reached through a nested
    /// module that opens `Fake.Core` locally. Opening it at file scope
    /// instead would shadow `System.String` with `Fake.Core.String`.
    module private FakeSurface =
        open Fake.Core

        let createTarget (name: string) (body: unit -> unit) = Target.create name (fun _ -> body ())

        let trace (text: string) = Trace.tracefn "%s" text

    /// Register the `VerifyPackagedModule` FAKE target in a packaged
    /// module repo's own `Build.fs`. Call it BEFORE `Pack`:
    ///
    /// ```fsharp
    /// // Build.fs
    /// open ToolUp.Platform
    /// open ToolUp.Platform.Build
    ///
    /// let layout =
    ///     { PackagedModuleCheckOptions.forProject "src/My.Module/My.Module.fsproj" with
    ///         Contract =
    ///             { PackagedModuleContract.create "My.Module" "My.Module.fsproj" with
    ///                 ServerOnlyFiles = [ "Server.fs" ]
    ///                 RequiredAssets = [ "icons/chart.svg" ] } }
    ///
    /// init args
    /// registerTargets config
    /// PackagedModuleConformance.registerTarget layout
    /// execute args
    /// ```
    ///
    /// `dotnet run -- VerifyPackagedModule` then fails the build on any
    /// of the four laws, naming each — before a nupkg exists and
    /// without building the consumer.
    let registerTarget (options: PackagedModuleCheckOptions) : unit =
        FakeSurface.createTarget "VerifyPackagedModule" (fun () ->
            let violations = verify options
            let text = report options.Contract violations

            FakeSurface.trace text

            if not (List.isEmpty violations) then
                failwithf
                    "VerifyPackagedModule: %d shadow-project conformance violation(s) in '%s'.%s%s"
                    (List.length violations)
                    options.Contract.ModuleLabel
                    Environment.NewLine
                    text)
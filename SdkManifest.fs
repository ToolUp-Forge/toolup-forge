// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 326 — the `ToolUp.Sdk` meta-manifest, derived from the tree
/// rather than hand-maintained.
///
/// **The problem this closes.** `src/ToolUp.Sdk/build/ToolUp.Sdk.props`
/// is the one-line coordinated-bump path: a consumer imports it into
/// their `Directory.Packages.props`, sets a single `<ToolUpSdkVersion>`,
/// and every `ToolUp.*` `PackageReference` resolves without a per-package
/// version. A published package with no `<PackageVersion>` entry there is
/// `NU1008` for that consumer — not a missing feature, a broken restore.
/// The manifest was hand-listed, and by the time this phase measured it,
/// it carried 59 entries against the 163 it needed (166 published, three
/// excluded by shape) — 105 omissions, plus one entry naming a package
/// that never existed. The shard that filed the phase named six; the
/// shape of the defect is that nobody could have known the number
/// without computing it.
///
/// **And a worse one underneath.** The file was not well-formed XML: its
/// header comment nested a second comment, which XML forbids, so MSBuild
/// refused the whole import with `MSB4024` and the manifest resolved
/// NOTHING for any consumer. Nothing in this repo imports its own
/// meta-manifest, so nothing here could see it. Hence `wellFormednessError`
/// below, and the render-time refusal that uses it.
///
/// **So it is computed.** This module is the single implementation of
/// "which package ids does this repo publish, and which of them must the
/// manifest carry". `GenerateSdkManifest` (root `Build.fs`) renders the
/// file from it; `SdkManifestTests` (ToolUp.Platform.Build.Tests) asserts
/// the committed file agrees with it; `publish-nuget.yml` runs the check
/// before a release packs. One rule, three readers — a second copy of the
/// rule would be the same drift class one level up.
///
/// **Deliberately FAKE-free and BCL-only.** It is compiled into
/// `Build.fsproj` (which references only Fake.Core.Target /
/// Fake.IO.FileSystem) AND source-linked into the Build test pack (which
/// references neither). A dependency on either side would force the rule
/// to be duplicated for the other.
///
/// **Discovery mirrors the `Pack` / `Publish` glob in
/// `SDK.Build.fs`** — `src/**/*.fsproj` minus the reference-app and
/// private-package directories, minus `<IsPackable>false</IsPackable>`.
/// It is the same shape `PublicApiApproval.discoverPackable` uses for the
/// api-baselines set, and for the same reason: the authoritative question
/// is "what does the release pack", and only the glob answers it. The two
/// differ in their KEY — the baselines key on `<AssemblyName>` (a DLL
/// stem), the manifest keys on `<PackageId>` (a feed id) — which is why
/// this is a sibling rather than a caller.
module ToolUp.Forge.SdkManifest

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Xml.Linq

// ─── The manifest file ───────────────────────────────────────────────

/// `src/ToolUp.Sdk/build/ToolUp.Sdk.props` under `root`.
let manifestPath (root: string) =
    Path.Combine(root, "src", "ToolUp.Sdk", "build", "ToolUp.Sdk.props")

/// Everything above this marker in the manifest is HAND-AUTHORED and is
/// carried through a regeneration byte-for-byte. The per-package prose
/// that used to sit between the entries lives up there now: a comment
/// interleaved with generated lines cannot survive the generator, and
/// deleting it to make the file mechanical would have thrown away the
/// Phase 307 / Phase 344 rationale that explains why two of the entries
/// read the way they do.
///
/// Deliberately pure ASCII: the marker is matched by `IndexOf` against a
/// file whose encoding a future tool may not preserve, and a marker that
/// stops matching silently turns a regeneration into an append.
let generatedBeginMarker =
    "  <!-- BEGIN GENERATED - do not hand-edit below this line."

let generatedEndMarker =
    "  <!-- END GENERATED - do not hand-edit above this line. -->"

/// The command that repairs every finding this module can report.
let regenerateCommand = "dotnet run --project Build.fsproj -- GenerateSdkManifest"

// ─── Packable-project discovery (mirrors the Pack / Publish glob) ────

type PackableProject = {
    /// The feed id: `<PackageId>`, else `<AssemblyName>`, else the
    /// project filename — MSBuild's own defaulting chain.
    PackageId: string
    /// Repo-relative, forward-slashed, for messages that must be
    /// actionable on any platform.
    ProjectPath: string
    ProjectDir: string
    /// `<PackAsTool>true</PackAsTool>` — a `dotnet tool`, installed from
    /// a tool manifest, never a `PackageReference`.
    PacksAsTool: bool
    /// `<AssemblyName>`, else the project filename: the stem of the
    /// project's `api-baselines/<stem>.approved.txt`, which is how a
    /// surface change is traced back to the project, and so to its group.
    AssemblyName: string
    /// A `<Version>` the project file declares for itself, overriding the
    /// one its version group supplies (Phase 1196 keeps these, and checks
    /// each equals its group's version).
    OwnVersion: string option
    /// The `<ProjectReference>` targets, repo-relative and forward-slashed.
    ProjectReferences: string list
}

/// A published package the manifest deliberately does NOT carry, with
/// the reason. Every exclusion is a computed predicate rather than a
/// name on a list: a hand-listed exemption is the same hand-maintained
/// artefact this whole module exists to retire.
type Exclusion = { PackageId: string; Rationale: string }

type Reconciliation = {
    /// Ids the manifest must declare, sorted.
    Expected: string list
    /// Ids it currently declares, in file order.
    Declared: string list
    /// Published, must be declared, is not.
    Missing: string list
    /// Declared, but nothing in the tree publishes it.
    Extra: string list
    Excluded: Exclusion list
}


let private commentPattern =
    Regex("<!--.*?-->", RegexOptions.Compiled ||| RegexOptions.Singleline)

/// The text with every XML comment replaced by spaces of the SAME length,
/// so offsets into the result are offsets into the original.
///
/// **Not a nicety — the manifest's header comment contains a worked
/// example of a consumer's own `Directory.Packages.props`, and that
/// example contains both an `<ItemGroup>` and two `<PackageVersion>`
/// lines** (`Include="..."` and `Include="ToolUp.X"`). Reading the file
/// naively reports those two as manifest entries nothing publishes, and
/// splits the preamble in the middle of the header comment. Both were
/// observed on this module's first run.
let blankComments (text: string) =
    commentPattern.Replace(text, (fun m -> String(' ', m.Length)))

let private projectReferencePattern =
    Regex(@"<ProjectReference\s+Include\s*=\s*""([^""]+)""", RegexOptions.Compiled)

let private forwardSlashed (path: string) = path.Replace('\\', '/')

/// First `<tag>value</tag>` in the project text, trimmed. Deliberately a
/// text read rather than an MSBuild evaluation: this runs inside an
/// Expecto pack and inside a FAKE target on a cold CI checkout, and
/// neither can afford to evaluate 166 projects to answer a question the
/// text already carries.
let private tagValue (tag: string) (text: string) =
    let m =
        Regex.Match(text, sprintf @"<%s>\s*([^<]*?)\s*</%s>" (Regex.Escape tag) (Regex.Escape tag))

    if m.Success && m.Groups[1].Value.Trim() <> "" then
        Some(m.Groups[1].Value.Trim())
    else
        None

let private isFlagged (tag: string) (text: string) =
    match tagValue tag text with
    | Some v -> v.Equals("true", StringComparison.OrdinalIgnoreCase)
    | None -> false

let private isPackableProject (fsprojPath: string) (text: string) =
    let notMarkedUnpackable =
        not (text.Replace(" ", "").Contains "<IsPackable>false</IsPackable>")

    // Directory exclusions NOT expressed via `<IsPackable>false</IsPackable>`:
    // the one declaration the `Pack` / `Publish` globs read (Phase 1096).
    notMarkedUnpackable && not (ToolUp.Platform.PackScope.isExcluded fsprojPath)

/// MSBuild's defaulting chain: an absent `<PackageId>` falls back to
/// `<AssemblyName>`, which falls back to the project filename. Several
/// companions rely on it (`AzureBlobStorage.fsproj` publishes as
/// `ToolUp.Storage.AzureBlob`), so reading only the filename would invent
/// ids no feed has ever served.
let private packageIdOf (fsprojPath: string) (text: string) =
    match tagValue "PackageId" text with
    | Some id -> id
    | None ->
        match tagValue "AssemblyName" text with
        | Some name -> name
        | None -> Path.GetFileNameWithoutExtension fsprojPath

/// Every project under `root/src` that the `Publish` target packs and
/// pushes, sorted by package id. Two projects can share an id only by
/// mistake, so the set is deduped — a duplicate would otherwise render
/// two identical manifest lines.
let discover (root: string) : PackableProject list =
    let srcDir = Path.Combine(root, "src")

    if not (Directory.Exists srcDir) then
        []
    else
        Directory.EnumerateFiles(srcDir, "*.fsproj", SearchOption.AllDirectories)
        |> Seq.choose (fun fsproj ->
            let text = File.ReadAllText fsproj

            if isPackableProject fsproj text then
                let dir = Path.GetDirectoryName fsproj

                Some {
                    PackageId = packageIdOf fsproj text
                    ProjectPath = forwardSlashed (Path.GetRelativePath(root, fsproj))
                    ProjectDir = Path.GetFullPath dir
                    PacksAsTool = isFlagged "PackAsTool" text
                    AssemblyName = defaultArg (tagValue "AssemblyName" text) (Path.GetFileNameWithoutExtension fsproj)
                    OwnVersion = tagValue "Version" (blankComments text)
                    ProjectReferences =
                        projectReferencePattern.Matches text
                        |> Seq.map (fun m ->
                            let target =
                                Path.GetFullPath(
                                    Path.Combine(dir, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))
                                )

                            forwardSlashed (Path.GetRelativePath(root, target)))
                        |> List.ofSeq
                }
            else
                None)
        |> Seq.distinctBy _.PackageId
        |> Seq.sortBy _.PackageId
        |> List.ofSeq

// ─── Version groups ──────────────────────────────────────────────────
//
// Which version line each packable project ships on. `version-groups.json`
// is the record: one lockstep group (the core, whose version IS the
// `<Version>` in `Directory.Build.props` and the number a release tag names)
// and one group per companion family, each on its own line. Membership is by
// repo-relative project-file glob, so a new project in a family directory
// joins its family by pattern, and a project no glob claims is a finding
// rather than a silent default. The rules and the operator's rulings are in
// docs/platform/release-process.md.
//
// Two projections are rendered from the file and checked against it: the
// generated region of `Directory.Build.props` (each project packs at its
// group's version) and the per-group table in the `ToolUp.Sdk` manifest
// (each package resolves through its group's property). Both live here, with
// the manifest, for the reason the module header gives: one rule, compiled
// into the build driver and source-linked into the Build test pack.

/// What one project file said about itself at some revision, read without
/// a checkout of that revision (a release tag, for the bump check).
type ProjectFacts = {
    Path: string
    AssemblyName: string
    OwnVersion: string option
}

/// One version line.
type VersionGroup = {
    /// `core`, `audit-sinks`, … — kebab-case, unique.
    Id: string
    /// The MSBuild property a consumer sets to pin this line, and (for a
    /// companion group) the property `Directory.Build.props` declares it in.
    Property: string
    /// The line's declared version.
    Version: string
    /// True for exactly one group: the core.
    Lockstep: bool
    /// Repo-relative project-file globs: `*` stays inside one path segment,
    /// `**` crosses segments.
    Projects: string list
}

let versionGroupsFileName = "version-groups.json"

let versionGroupsPath (root: string) =
    Path.Combine(root, versionGroupsFileName)

/// The one `<Version>` element `Directory.Build.props` declares outside a
/// comment: the core's version, the number the estate's version ledger edits
/// and a release tag names. An element WITH attributes (the conditioned
/// per-group assignments in the generated region) is not this declaration,
/// and the ledger's own reader skips it the same way.
let declaredVersionTextIn (propsText: string) : Result<string, string> =
    let uncommented =
        Regex.Replace(propsText, @"<!--.*?-->", "", RegexOptions.Singleline)

    let matches = Regex.Matches(uncommented, @"<Version>\s*([^<]+?)\s*</Version>")

    match matches |> Seq.map (fun m -> m.Groups[1].Value) |> List.ofSeq with
    | [ one ] -> Ok one
    | [] -> Error "Directory.Build.props declares no <Version> element outside a comment"
    | many ->
        Error(
            sprintf
                "Directory.Build.props declares %d <Version> elements (%s) — the lockstep SDK version must be a single declaration"
                many.Length
                (String.concat ", " many)
        )

let private groupIdPattern = Regex(@"^[a-z0-9]+(-[a-z0-9]+)*$")
let private propertyPattern = Regex(@"^ToolUp[A-Za-z0-9]+Version$")
let private globCharsPattern = Regex(@"^[A-Za-z0-9._/*-]+$")

let private versionTextPattern = Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")

/// A glob as an anchored regex. Built from `Regex.Escape`, which renders
/// `*` as `\*`: the double star is swapped out first so it is not read as two
/// single ones. The result is also what the generated `Directory.Build.props`
/// region hands to MSBuild's `Regex.IsMatch`, so it ends in `\z` rather than
/// `$` (a dollar there would start a property expansion).
let globPattern (glob: string) =
    "^"
    + Regex.Escape(glob).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace("\u0001", ".*")
    + @"\z"

let globMatches (glob: string) (projectPath: string) =
    Regex.IsMatch(projectPath, globPattern glob)

/// The groups `version-groups.json` declares, or why it cannot be read. Only
/// the SHAPE is checked here; what the groups say about the tree is
/// `versionGroupFindings`.
let parseVersionGroups (text: string) : Result<VersionGroup list, string> =
    try
        use doc = System.Text.Json.JsonDocument.Parse text
        let rootEl = doc.RootElement

        let str (el: System.Text.Json.JsonElement) (name: string) =
            match el.TryGetProperty name with
            | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> v.GetString()
            | _ -> failwithf "a group has no string `%s`" name

        match rootEl.TryGetProperty "groups" with
        | true, groups when groups.ValueKind = System.Text.Json.JsonValueKind.Array ->
            groups.EnumerateArray()
            |> Seq.map (fun g -> {
                Id = str g "id"
                Property = str g "property"
                Version = str g "version"
                Lockstep =
                    match g.TryGetProperty "lockstep" with
                    | true, v -> v.ValueKind = System.Text.Json.JsonValueKind.True
                    | _ -> false
                Projects =
                    match g.TryGetProperty "projects" with
                    | true, v when v.ValueKind = System.Text.Json.JsonValueKind.Array ->
                        v.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq
                    | _ -> []
            })
            |> List.ofSeq
            |> Ok
        | _ -> Error "version-groups.json has no `groups` array"
    with ex ->
        Error(sprintf "version-groups.json cannot be read: %s" ex.Message)

/// The lockstep group, when exactly one is declared.
let coreGroup (groups: VersionGroup list) =
    match groups |> List.filter _.Lockstep with
    | [ core ] -> Some core
    | _ -> None

/// Every group one of whose globs matches `projectPath`.
let groupsMatching (groups: VersionGroup list) (projectPath: string) =
    groups
    |> List.filter (fun g -> g.Projects |> List.exists (fun glob -> globMatches glob projectPath))

/// The group `projectPath` belongs to, when exactly one claims it.
let groupOf (groups: VersionGroup list) (projectPath: string) =
    match groupsMatching groups projectPath with
    | [ g ] -> Some g
    | _ -> None

/// Assembly name → group, for tracing an `api-baselines/<assembly>` file to
/// its line. `fsprojs` pairs each project path with its assembly name.
let groupsByAssembly (groups: VersionGroup list) (fsprojs: (string * string) seq) : Map<string, VersionGroup> =
    fsprojs
    |> Seq.choose (fun (path, assembly) -> groupOf groups path |> Option.map (fun g -> assembly, g))
    |> Map.ofSeq

/// The groups a group's projects reference, itself excluded, sorted. What a
/// group depends on: when one of these breaks, a group that did not move must
/// still re-release, because its last release was compiled against the
/// surface that broke.
let groupDependencies (groups: VersionGroup list) (projects: PackableProject list) : Map<string, string list> =
    let groupOfPath = groupOf groups

    groups
    |> List.map (fun g ->
        let deps =
            projects
            |> List.filter (fun p -> groupOfPath p.ProjectPath |> Option.exists (fun pg -> pg.Id = g.Id))
            |> List.collect _.ProjectReferences
            |> List.choose groupOfPath
            |> List.map _.Id
            |> List.filter ((<>) g.Id)
            |> List.distinct
            |> List.sort

        g.Id, deps)
    |> Map.ofList

/// What is wrong with the groups as a file, independent of the tree.
let groupShapeFindings (groups: VersionGroup list) : string list = [
    if List.isEmpty groups then
        "version-groups.json declares no group."

    match groups |> List.filter _.Lockstep with
    | [ core ] ->
        if core.Property <> "ToolUpSdkVersion" then
            sprintf
                "the lockstep group `%s` pins through `%s`; the core's consumer property is `ToolUpSdkVersion`, the one the ToolUp.Sdk manifest's import path names."
                core.Id
                core.Property
    | [] -> "version-groups.json declares no lockstep group; exactly one (the core) must say `\"lockstep\": true`."
    | many ->
        sprintf
            "version-groups.json declares %d lockstep groups (%s); exactly one, the core, is."
            many.Length
            (many |> List.map _.Id |> String.concat ", ")

    for g in groups do
        if not (groupIdPattern.IsMatch g.Id) then
            sprintf "group id `%s` is not kebab-case." g.Id

        if not (propertyPattern.IsMatch g.Property) then
            sprintf "group `%s` names property `%s`; a group property is `ToolUp<Name>Version`." g.Id g.Property

        if not (versionTextPattern.IsMatch g.Version) then
            sprintf "group `%s` declares version `%s`, which is not `X.Y.Z` or `X.Y.Z-label`." g.Id g.Version

        if List.isEmpty g.Projects then
            sprintf "group `%s` claims no project glob." g.Id

        for glob in g.Projects do
            if not (globCharsPattern.IsMatch glob) then
                sprintf
                    "group `%s` glob `%s` uses a character outside [A-Za-z0-9._/*-]; it is rendered into an MSBuild condition, where the others are special."
                    g.Id
                    glob

    for id, n in groups |> List.countBy _.Id do
        if n > 1 then
            sprintf "group id `%s` is declared %d times." id n

    for prop, n in groups |> List.countBy _.Property do
        if n > 1 then
            sprintf "property `%s` is named by %d groups; each line pins through its own." prop n
]

/// What the groups say about the tree that is not so. Empty means every
/// packable project is on exactly one line, every glob claims something,
/// every project-level `<Version>` agrees with its group, the core's version
/// is the declared `<Version>`, and no core package depends on a companion.
let versionGroupFindings
    (groups: VersionGroup list)
    (projects: PackableProject list)
    (declaredCoreVersion: string option)
    : string list =
    [
        yield! groupShapeFindings groups

        for p in projects do
            match groupsMatching groups p.ProjectPath with
            | [] ->
                sprintf
                    "%s (%s) is packable and in no version group. Add a glob for it to version-groups.json, in the core or in its companion family."
                    p.ProjectPath
                    p.PackageId
            | [ g ] ->
                match p.OwnVersion with
                | Some own when own <> g.Version ->
                    sprintf
                        "%s declares <Version>%s</Version>, but its group `%s` is at %s. A project-level version must equal its group's: move the group, not the project."
                        p.ProjectPath
                        own
                        g.Id
                        g.Version
                | _ -> ()
            | many ->
                sprintf
                    "%s is claimed by %d groups (%s); every packable project is on exactly one line."
                    p.ProjectPath
                    many.Length
                    (many |> List.map _.Id |> String.concat ", ")

        for g in groups do
            for glob in g.Projects do
                if not (projects |> List.exists (fun p -> globMatches glob p.ProjectPath)) then
                    sprintf "group `%s` glob `%s` matches no packable project; a dead glob is removed." g.Id glob

        match coreGroup groups, declaredCoreVersion with
        | Some core, Some declared when core.Version <> declared ->
            sprintf
                "the core group is at %s, but Directory.Build.props declares <Version>%s</Version>. They are one number: the ledger moves <Version>, and the core group follows it."
                core.Version
                declared
        | _ -> ()

        // A core package that depends on a companion would pin a companion
        // line from the lockstep line, and a release-candidate run, which
        // packs the core only, would publish a candidate depending on a
        // package it did not pack. A `dotnet tool` bundles what it
        // references, so it declares no such dependency and is exempt.
        match coreGroup groups with
        | Some core ->
            let byPath = projects |> List.map (fun p -> p.ProjectPath, p) |> Map.ofList

            for p in projects do
                if not p.PacksAsTool && groupOf groups p.ProjectPath = Some core then
                    for target in p.ProjectReferences do
                        match byPath.TryFind target |> Option.bind (fun t -> groupOf groups t.ProjectPath) with
                        | Some g when g.Id <> core.Id ->
                            sprintf
                                "core project %s references %s, on the `%s` line. The core never depends on a companion line: move one of them."
                                p.ProjectPath
                                target
                                g.Id
                        | _ -> ()
        | None -> ()
    ]

// ─── The Directory.Build.props projection ────────────────────────────

let propsRegionBeginMarker =
    "  <!-- BEGIN GENERATED VERSION GROUPS - do not hand-edit below this line."

let propsRegionEndMarker =
    "  <!-- END GENERATED VERSION GROUPS - do not hand-edit above this line. -->"

/// The `Directory.Build.props` region the groups render to. Each companion
/// group's version is a property; each packable project learns its group from
/// its repo-relative path (the same globs, as the same regexes) and packs at
/// that group's version. The per-project `<Version>` elements carry a
/// `Condition`, so the one bare `<Version>` above stays the only declaration
/// `declaredVersionTextIn` (and the version ledger) reads. A project's own
/// `<Version>` is evaluated after this file and still wins.
let renderPropsRegion (groups: VersionGroup list) =
    let nl = "\n"
    let sb = StringBuilder()
    let companions = groups |> List.filter (fun g -> not g.Lockstep)

    sb.Append(propsRegionBeginMarker).Append(nl) |> ignore

    for line in
        [
            "       Rendered from version-groups.json by the GenerateSdkManifest target of"
            "       Build.fsproj; SdkManifestTests fails when the two disagree. Each"
            "       companion family packs at its own group's version; the core packs at"
            "       the <Version> above. On a release-candidate run (Directory.Build.targets)"
            "       every companion group is made non-packable: a candidate is the core's,"
            "       and the companions ride the core's stable release instead."
            "  -->"
        ] do
        sb.Append(line).Append(nl) |> ignore

    sb.Append("  <PropertyGroup>").Append(nl) |> ignore

    sb
        .Append(
            "    <ToolUpProjectRelativePath>$([MSBuild]::MakeRelative('$(MSBuildThisFileDirectory)', '$(MSBuildProjectFullPath)').Replace('\\', '/'))</ToolUpProjectRelativePath>"
        )
        .Append(nl)
    |> ignore

    for g in companions do
        sb.AppendFormat("    <{0}>{1}</{0}>", g.Property, g.Version).Append(nl)
        |> ignore

    sb.Append("  </PropertyGroup>").Append(nl) |> ignore

    for g in groups do
        let pattern = g.Projects |> List.map globPattern |> String.concat "|"

        sb
            .AppendFormat(
                "  <PropertyGroup Condition=\"$([System.Text.RegularExpressions.Regex]::IsMatch('$(ToolUpProjectRelativePath)', '{0}'))\">",
                pattern
            )
            .Append(nl)
            .AppendFormat("    <ToolUpVersionGroup>{0}</ToolUpVersionGroup>", g.Id)
            .Append(nl)
            .Append("  </PropertyGroup>")
            .Append(nl)
        |> ignore

    sb.Append("  <PropertyGroup>").Append(nl) |> ignore

    for g in companions do
        sb
            .AppendFormat(
                "    <Version Condition=\"'$(ToolUpVersionGroup)' == '{0}'\">$({1})</Version>",
                g.Id,
                g.Property
            )
            .Append(nl)
        |> ignore

    sb.Append("  </PropertyGroup>").Append(nl) |> ignore

    match coreGroup groups with
    | Some core ->
        sb
            .AppendFormat(
                "  <PropertyGroup Condition=\"'$(TOOLUP_RELEASE_PRERELEASE)' != '' And '$(ToolUpVersionGroup)' != '' And '$(ToolUpVersionGroup)' != '{0}'\">",
                core.Id
            )
            .Append(nl)
            .Append("    <IsPackable>false</IsPackable>")
            .Append(nl)
            .Append("  </PropertyGroup>")
            .Append(nl)
        |> ignore
    | None -> ()

    sb.Append(propsRegionEndMarker).Append(nl) |> ignore
    sb.ToString()

/// `text` with its region between the two markers (inclusive) replaced by
/// `region`; a text with no region gets it ahead of the closing `</Project>`.
let spliceRegion (beginMarker: string) (endMarker: string) (region: string) (text: string) =
    let b = text.IndexOf(beginMarker, StringComparison.Ordinal)
    let e = text.IndexOf(endMarker, StringComparison.Ordinal)

    if b >= 0 && e > b then
        let afterEnd =
            match text.IndexOf('\n', e) with
            | -1 -> text.Length
            | i -> i + 1

        text.Substring(0, b) + region + text.Substring afterEnd
    else
        let close = text.LastIndexOf("</Project>", StringComparison.Ordinal)

        if close < 0 then
            failwith "spliceRegion: the props text has no closing </Project>"

        text.Substring(0, close) + region + text.Substring close

let buildPropsPath (root: string) =
    Path.Combine(root, "Directory.Build.props")

/// The groups under `root`, or the reason they cannot be read.
let loadVersionGroups (root: string) =
    let path = versionGroupsPath root

    if File.Exists path then
        parseVersionGroups (File.ReadAllText path)
    else
        Error(sprintf "%s is missing" path)

/// Every version-group finding for the tree at `root`: the file's shape, the
/// tree's membership, the core's agreement with `<Version>`, and the
/// `Directory.Build.props` region's agreement with the file.
let groupFindings (root: string) : string list =
    match loadVersionGroups root with
    | Error e -> [ e ]
    | Ok groups ->
        let props = File.ReadAllText(buildPropsPath root)

        [
            yield! versionGroupFindings groups (discover root) (declaredVersionTextIn props |> Result.toOption)

            let normalised = props.Replace("\r\n", "\n")

            if
                spliceRegion propsRegionBeginMarker propsRegionEndMarker (renderPropsRegion groups) normalised
                <> normalised
            then
                "Directory.Build.props's generated version-group region does not match version-groups.json."
        ]

// ─── Which of the published set the manifest must carry ──────────────

/// The project that OWNS the manifest — the one whose pack ships
/// `build/ToolUp.Sdk.props`. Resolved from the file's own location
/// (`.../<owner>/build/ToolUp.Sdk.props`) rather than by name, so moving
/// the meta-package does not silently re-admit it to its own manifest.
let private manifestOwnerDir (root: string) =
    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath root), ".."))

/// A published package the manifest deliberately omits, and why. Both
/// predicates are properties of the project, so a NEW package of either
/// shape is excluded automatically and a package that stops being of
/// that shape is re-admitted automatically.
let exclusionOf (root: string) (p: PackableProject) : Exclusion option =
    if p.ProjectDir = manifestOwnerDir root then
        Some {
            PackageId = p.PackageId
            Rationale =
                "the meta-package that SHIPS this manifest — a consumer must already pin it in their own Directory.Packages.props for the import to resolve, so an entry here could never be the one that resolves it"
        }
    elif p.PacksAsTool then
        Some {
            PackageId = p.PackageId
            Rationale =
                "<PackAsTool> — installed from a tool manifest (`dotnet tool install`), never a <PackageReference>, so central package management never consults a version for it"
        }
    else
        None

/// The ids the manifest must declare: everything `Publish` pushes, minus
/// the excluded shapes.
let expected (root: string) =
    discover root
    |> List.filter (fun p -> (exclusionOf root p).IsNone)
    |> List.map _.PackageId

let exclusions (root: string) =
    discover root |> List.choose (exclusionOf root)

// ─── Reading what the manifest currently declares ────────────────────

/// Escaped rather than triple-quoted on purpose: the pattern both starts
/// and ends with a double quote, and a triple-quoted literal ending in
/// one terminates early and silently swallows the rest of the file.
let private packageVersionPattern =
    Regex("<PackageVersion\\s+Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled)

/// The ids a manifest text declares, in file order. Commented-out and
/// illustrative entries do not count as declarations.
let declaredIdsIn (manifestText: string) =
    packageVersionPattern.Matches(blankComments manifestText)
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> List.ofSeq

/// Pure set difference, split out so the guard's own detection can be
/// driven from synthetic inputs. A drift guard whose failure path is
/// never exercised is indistinguishable from one that cannot fail.
let diff (expectedIds: string seq) (declaredIds: string seq) =
    let e = Set.ofSeq expectedIds
    let d = Set.ofSeq declaredIds
    Set.difference e d |> Set.toList, Set.difference d e |> Set.toList

let reconcile (root: string) : Reconciliation =
    let expectedIds = expected root
    let manifest = manifestPath root

    let declared =
        if File.Exists manifest then
            declaredIdsIn (File.ReadAllText manifest)
        else
            []

    let missing, extra = diff expectedIds declared

    {
        Expected = expectedIds
        Declared = declared
        Missing = missing
        Extra = extra
        Excluded = exclusions root
    }

/// One human-readable account of a reconciliation, or `None` when it is
/// clean. Shared by the FAKE target and the test pack so a finding reads
/// identically wherever it surfaces.
let describe (r: Reconciliation) : string option =
    if List.isEmpty r.Missing && List.isEmpty r.Extra then
        None
    else
        let sb = StringBuilder()

        sb
            .AppendFormat(
                "The ToolUp.Sdk meta-manifest disagrees with the packable set ({0} published id(s) expected, {1} declared).",
                List.length r.Expected,
                List.length r.Declared
            )
            .AppendLine()
        |> ignore

        if not (List.isEmpty r.Missing) then
            sb
                .AppendLine()
                .AppendFormat(
                    "  MISSING ({0}) — published by `Publish`, absent from the manifest, so a consumer referencing one under central package management gets NU1008:",
                    List.length r.Missing
                )
                .AppendLine()
            |> ignore

            for id in r.Missing do
                sb.AppendFormat("    {0}", id).AppendLine() |> ignore

        if not (List.isEmpty r.Extra) then
            sb
                .AppendLine()
                .AppendFormat(
                    "  UNPUBLISHED ({0}) — declared in the manifest, but nothing in the tree packs that id, so the entry resolves to NU1101 at restore:",
                    List.length r.Extra
                )
                .AppendLine()
            |> ignore

            for id in r.Extra do
                sb.AppendFormat("    {0}", id).AppendLine() |> ignore

        sb.AppendLine().AppendFormat("  Regenerate the manifest with: {0}", regenerateCommand).AppendLine()
        |> ignore

        Some(sb.ToString())

// ─── Well-formedness ─────────────────────────────────────────────────

/// `Some why` when `text` is not XML MSBuild can import, `None` when it
/// is. The rule that matters in practice is the one that bit this very
/// file: **an XML comment cannot contain a double hyphen**, so a nested
/// `<!-- ... -->` inside a longer comment terminates the outer one and
/// leaves the rest of the file as garbage. MSBuild reports it as MSB4024
/// and refuses the ENTIRE import; the manifest resolves nothing at all.
///
/// Checked with the BCL's own reader rather than by grepping for the
/// hyphen pair, so every other well-formedness rule is covered too and
/// the message is the parser's.
let wellFormednessError (text: string) : string option =
    try
        XDocument.Parse text |> ignore
        None
    with ex ->
        Some ex.Message

// ─── Rendering ───────────────────────────────────────────────────────

/// The hand-authored preamble of the existing manifest — everything
/// above `generatedBeginMarker`. Falls back to everything above the
/// first `<ItemGroup>` for the pre-Phase-326 shape (the one-time
/// migration), and to a minimal `<Project>` opener when there is no file
/// at all.
let preambleOf (existing: string option) =
    let fallbackPreamble = "<Project>" + Environment.NewLine

    match existing with
    | None -> fallbackPreamble
    | Some text ->
        match text.IndexOf(generatedBeginMarker, StringComparison.Ordinal) with
        | i when i >= 0 -> text.Substring(0, i)
        | _ ->
            // The first REAL `<ItemGroup>` — searched in the
            // comment-blanked projection, whose offsets are the
            // original's, because the header comment carries an
            // illustrative one that would otherwise cut the preamble in
            // half (and did, on the first run).
            match (blankComments text).IndexOf("<ItemGroup>", StringComparison.Ordinal) with
            | i when i >= 0 ->
                // Back up over the indentation on that line so the
                // preamble ends at a line boundary.
                let lineStart = text.LastIndexOf('\n', i)
                text.Substring(0, if lineStart >= 0 then lineStart + 1 else i)
            | _ -> fallbackPreamble

/// The manifest text for `root`: the preserved preamble, then one
/// `<PackageVersion>` per published id in a generated region, then the
/// close. Ids are emitted in sort order — a stable order is what makes
/// the regenerated file's diff readable, and the grouping the file used
/// to carry could not survive a generator.
let render (root: string) =
    let r = reconcile root

    let existing =
        if File.Exists(manifestPath root) then
            Some(File.ReadAllText(manifestPath root))
        else
            None

    let nl = "\n"
    let sb = StringBuilder()
    sb.Append(preambleOf existing) |> ignore
    sb.Append(generatedBeginMarker).Append(nl) |> ignore

    sb
        .Append("       Every id below is derived from the projects the `Publish` target packs")
        .Append(nl)
        .Append("       and pushes (`src/**/*.fsproj`, minus the reference-app directories,")
        .Append(nl)
        .Append("       minus `<IsPackable>false</IsPackable>`).")
        .Append(nl)
        .Append(nl)
        .Append("       Regenerate by running the GenerateSdkManifest target of the repo's")
        .Append(nl)
        .Append("       Build.fsproj. The command cannot be written out here: an XML comment")
        .Append(nl)
        .Append("       cannot contain a double hyphen, and the invocation has two. It is in")
        .Append(nl)
        .Append("       docs/migrations/326-sdk-manifest-reconcile.md, and in the message of")
        .Append(nl)
        .Append("       every failure that asks for it.")
        .Append(nl)
        .Append(nl)
        .Append("       `SdkManifestTests` in the Build pack fails when this region and the")
        .Append(nl)
        .Append("       tree disagree, so an added package cannot ship unadvertised and a")
        .Append(nl)
        .Append("       removed one cannot linger here as an NU1101.")
        .Append(nl)
    |> ignore

    if not (List.isEmpty r.Excluded) then
        sb.Append(nl).Append("       Published, and deliberately NOT listed:").Append(nl)
        |> ignore

        for e in r.Excluded |> List.sortBy _.PackageId do
            sb.Append("         - ").Append(e.PackageId).Append(": ").Append(e.Rationale).Append(nl)
            |> ignore

    sb
        .Append(nl)
        .Append("       Each id resolves through ONE property, its version group's (declared in")
        .Append(nl)
        .Append("       version-groups.json). Core packages read ToolUpSdkVersion, which the")
        .Append(nl)
        .Append("       consumer sets. Each companion family reads its own property, defaulted")
        .Append(nl)
        .Append("       to the version that family carried when this manifest was released, so a")
        .Append(nl)
        .Append("       consumer pinning only ToolUpSdkVersion resolves every package; setting a")
        .Append(nl)
        .Append("       family's property in Directory.Packages.props moves that family alone.")
        .Append(nl)
    |> ignore

    sb.Append("  -->").Append(nl) |> ignore

    let groups =
        match loadVersionGroups root with
        | Ok groups -> groups
        | Error e -> failwithf "SdkManifest.render: %s" e

    let projectsById = discover root |> List.map (fun p -> p.PackageId, p) |> Map.ofList

    let groupOfId (id: string) =
        match projectsById.TryFind id |> Option.bind (fun p -> groupOf groups p.ProjectPath) with
        | Some g -> g
        | None ->
            failwithf
                "SdkManifest.render: %s is in no single version group, so the manifest cannot say which property pins it. Run GenerateSdkManifest with --check for the group findings."
                id

    let used =
        r.Expected
        |> List.map groupOfId
        |> List.filter (fun g -> not g.Lockstep)
        |> List.distinctBy _.Id
        |> List.sortBy _.Property

    if not (List.isEmpty used) then
        sb.Append("  <PropertyGroup>").Append(nl) |> ignore

        for g in used do
            sb.AppendFormat("    <{0} Condition=\"'$({0})' == ''\">{1}</{0}>", g.Property, g.Version).Append(nl)
            |> ignore

        sb.Append("  </PropertyGroup>").Append(nl) |> ignore

    sb.Append("  <ItemGroup>").Append(nl) |> ignore

    for id in r.Expected do
        sb
            .Append("    <PackageVersion Include=\"")
            .Append(id)
            .Append("\" Version=\"$(")
            .Append((groupOfId id).Property)
            .Append(")\" />")
            .Append(nl)
        |> ignore

    sb.Append("  </ItemGroup>").Append(nl) |> ignore
    sb.Append(generatedEndMarker).Append(nl) |> ignore
    sb.Append("</Project>").Append(nl) |> ignore

    let text = sb.ToString()

    // Refuse to hand back a manifest MSBuild cannot import. This is not
    // defensive padding: the file shipped for months carrying a NESTED
    // XML comment in its header example, which is illegal (a comment
    // cannot contain a double hyphen), and MSBuild rejected the whole
    // import with MSB4024 before a single version resolved. Nothing
    // caught it, because nothing in this repo imports its own
    // meta-manifest. A preserved preamble is hand-written text, and a
    // rationale string is authored too, so either can reintroduce the
    // defect — the generator checks its own output instead of trusting
    // them.
    match wellFormednessError text with
    | Some why ->
        failwithf
            "SdkManifest.render produced XML MSBuild would refuse: %s.\n\nThis is the MSB4024 class. Check the hand-authored preamble above the generated marker, and any exclusion rationale, for a nested comment or a literal double hyphen."
            why
    | None -> text

/// Whether the committed manifest is exactly what `render` produces. The id
/// reconciliation above catches a package added or removed; this catches the
/// rest, a package that moved group and a family whose default version moved.
let manifestIsCurrent (root: string) =
    let path = manifestPath root

    File.Exists path && File.ReadAllText(path).Replace("\r\n", "\n") = render root
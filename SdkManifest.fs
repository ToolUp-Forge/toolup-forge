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

/// Directory-based exclusions the `Pack` / `Publish` globs apply that are
/// NOT expressed via `<IsPackable>false</IsPackable>`. Kept verbatim in
/// step with the `--` clauses in `SDK.Build.fs`; most are absent from the
/// OSS tree and are listed for faithfulness to the glob rather than
/// because they match anything here.
let private excludedDirSegments = [
    "ToolUpApp-Server"
    "ToolUpApp-Client"
    "Modules"
    "TestHarness"
    "ToolUp.Algorithms"
    "ToolUp.Platform.Tests"
    "ToolUp.Forms.Tests"
    "ToolUp.Scheduling.Tests"
    "ToolUp.RAG.Evaluation"
    "ToolUp.RAG.Benchmarks"
]

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

    let normalised = forwardSlashed fsprojPath

    let notExcludedDir =
        excludedDirSegments
        |> List.forall (fun seg -> not (normalised.Contains(sprintf "/%s/" seg)))

    notMarkedUnpackable && notExcludedDir

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
                Some {
                    PackageId = packageIdOf fsproj text
                    ProjectPath = forwardSlashed (Path.GetRelativePath(root, fsproj))
                    ProjectDir = Path.GetFullPath(Path.GetDirectoryName fsproj)
                    PacksAsTool = isFlagged "PackAsTool" text
                }
            else
                None)
        |> Seq.distinctBy _.PackageId
        |> Seq.sortBy _.PackageId
        |> List.ofSeq

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

    sb.Append("  -->").Append(nl) |> ignore
    sb.Append("  <ItemGroup>").Append(nl) |> ignore

    for id in r.Expected do
        sb
            .Append("    <PackageVersion Include=\"")
            .Append(id)
            .Append("\" Version=\"$(ToolUpSdkVersion)\" />")
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
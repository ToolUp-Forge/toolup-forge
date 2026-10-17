// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 184 — the fresh-machine published-package smoke gate: the rules
/// behind the `VerifyPublishedPackages` target and the
/// `published-package-smoke` job in `publish-nuget.yml`.
///
/// **The gap this closes.** "A fresh machine can `dotnet add package` the
/// SDK and build" was a Wave-5.5 exit criterion, run ONCE by hand. Nothing
/// re-ran it after any subsequent tag, so a packaging regression — a
/// transitive `PackageReference` a same-repo project-reference build
/// silently supplies, a `fable/`-packed source path that stops extracting
/// — surfaces first in a consumer's install rather than in CI. Everything
/// this repo's other gates prove, they prove about the TREE; a nupkg is a
/// different artefact and the only honest probe of it is a restore from
/// the feed a consumer restores from.
///
/// **Pure and BCL-only, deliberately.** The impure half — HTTP, `dotnet
/// build`, `dotnet fable` — lives in the target. Everything decidable
/// without a network lives here so it is provable OFFLINE, in both
/// directions, from `ToolUp.Platform.Build.Tests`. That split matters more
/// here than for most gates: the gate itself only ever runs on a release
/// tag, so if its rules were only exercised there, they would be first
/// exercised on the release they exist to protect.
///
/// **Compiled into `Build.fsproj` AND source-linked into the Build test
/// pack**, mirroring `SdkManifest.fs`, and FAKE-free for the same reason:
/// the test pack references neither FAKE package, so a dependency on
/// either would force a second copy of the rule.
module ToolUp.Forge.PublishedSmoke

open System
open System.Text.RegularExpressions

// ─── The probe closure ───────────────────────────────────────────────

/// Package ids the DLL-tier probe adds and builds against.
///
/// **`ToolUp.AI` is deliberately absent, and its absence is the finding
/// that shaped this list.** The brief that filed this phase named
/// `ToolUp.Platform.Core` + `ToolUp.AI` + `ToolUp.Platform.Server`. There
/// is no `ToolUp.AI` package: `src/ToolUp.AI/` holds a README and two
/// `.props` files and produces no `.fsproj`, so nothing in the repo packs
/// that id and nuget.org serves no blob for it. A probe declaring it would
/// have failed every release with `NU1101` — a red that says "the release
/// is broken" about a defect in the probe. The AI tier's real ids are
/// `ToolUp.AI.Core` / `.Server` / `.Client`.
///
/// That is the stranger's-package class (Phase 307 / Phase 754) one level
/// up, so it is guarded the same way: `unpublishedProbeIds` reconciles
/// this list against `SdkManifest.expected`, which computes what the
/// release actually pushes rather than restating it.
let dllProbeIds = [
    "ToolUp.Platform.Core"
    "ToolUp.Platform.Server"
    "ToolUp.AI.Core"
    "ToolUp.AI.Server"
]

/// Package ids the Fable-tier probe adds and transpiles.
///
/// One entry, and one is enough: `ToolUp.Platform.Client` packs its whole
/// source tree under `fable/` and pulls the rest of the client tier in
/// transitively, so a Fable compile here drives every `fable/`-packed
/// source the release publishes through the compiler.
let fableProbeIds = [ "ToolUp.Platform.Client" ]

/// Every id the probe resolves, in probe order.
let probeIds = dllProbeIds @ fableProbeIds

/// Declared probe ids the repo's own release does NOT publish, in
/// declaration order. `expectedIds` is `SdkManifest.expected` — the
/// computed packable set, never a second hand-written list.
///
/// Case-insensitive, because NuGet ids are: a case-only difference would
/// otherwise be reported here on one filesystem and pass on another.
let unpublishedProbeIds (expectedIds: string seq) (declared: string seq) : string list =
    let published =
        expectedIds |> Seq.map (fun (id: string) -> id.ToLowerInvariant()) |> Set.ofSeq

    declared
    |> Seq.filter (fun id -> not (published.Contains(id.ToLowerInvariant())))
    |> List.ofSeq

/// The failure message for `unpublishedProbeIds`. Names the class rather
/// than only the ids, because the remedy differs: a typo is fixed in the
/// list above, whereas an id the repo genuinely does not own means the
/// probe would restore a STRANGER'S package of that name — or, as with
/// `ToolUp.AI`, nothing at all.
let unpublishedProbeReport (unpublished: string list) =
    sprintf
        "VerifyPublishedPackages: %d declared probe package id(s) are NOT in the set this repo publishes: %s. The probe restores from nuget.org, so an id the release does not push either fails NU1101 or resolves an UNRELATED package published by someone else (the Phase 307 stranger's-package class). Either the id is misspelled in `PublishedSmoke.dllProbeIds` / `fableProbeIds`, or the project that was meant to emit it is `<IsPackable>false</IsPackable>` or absent. Never silence this by dropping the id without checking which of the two it is."
        (List.length unpublished)
        (String.concat ", " unpublished)

// ─── What the wait awaits: the closure, not the names (Phase 1004) ───

let private ownVersionPattern = Regex(@"<Version>\s*[^<\s]", RegexOptions.Compiled)

/// Does this project file declare a `<Version>` of its own?
///
/// A project that does is versioned INDEPENDENTLY of the release: it is
/// packed and pushed at its own number (`--skip-duplicate` makes the push
/// a no-op once that number exists), so nuget.org will never serve it at
/// the version under test and awaiting it would time out on a healthy
/// release. Comments are blanked first, because a comment that explains
/// the override convention mentions the element without declaring it.
let declaresOwnVersion (fsprojText: string) =
    ownVersionPattern.IsMatch(SdkManifest.blankComments fsprojText)

/// The package ids the index wait must see served before anything is
/// restored, in wait order: the probe ids first, then every other
/// LOCKSTEP package the release publishes. `expectedIds` is
/// `SdkManifest.expected` — the computed packable set, never a list —
/// and `independentlyVersioned` the ids whose project declares its own
/// `<Version>`.
///
/// **Why not only the probe ids.** A restore resolves the probes'
/// TRANSITIVE `ToolUp.*` dependencies at the same version, and nuget.org
/// indexes each push independently, minutes apart. Awaiting only the
/// names the probe declares let the restore outrun the index on a
/// dependency-only package: `v0.25.1` failed NU1102 on
/// `ToolUp.Feliz.AgGrid` (reached through `ToolUp.Platform.Client`) and
/// `v0.26.0` on `ToolUp.Platform.Transport` (reached through
/// `ToolUp.Platform.Server`). Both releases were fine; a re-run passed.
///
/// **Why the whole lockstep set rather than the closure read from the
/// nuspecs.** The lockstep set is a superset of every closure a probe can
/// reach at this version, it is computable offline from the tree that was
/// tagged, and it cannot be wrong in the direction that matters — a
/// missing nuspec edge would silently shrink a nuspec-derived set back
/// into the race. The cost is polling packages no probe needs, which is
/// a wait, never a red.
///
/// Probe ids are kept even if the set would drop one, so the awaited set
/// is never smaller than the probe set it replaces. Case-insensitive, as
/// NuGet ids are.
let awaitedIds (expectedIds: string seq) (independentlyVersioned: string seq) (probes: string seq) : string list =
    let independent =
        independentlyVersioned
        |> Seq.map (fun (id: string) -> id.ToLowerInvariant())
        |> Set.ofSeq

    let lockstep =
        expectedIds
        |> Seq.filter (fun id -> not (independent.Contains(id.ToLowerInvariant())))

    Seq.append probes lockstep
    |> Seq.distinctBy (fun id -> id.ToLowerInvariant())
    |> List.ofSeq

/// The published ids under `root` whose project declares its own
/// `<Version>`. A text read, like `SdkManifest.discover` that it rides on.
let independentlyVersionedIn (root: string) : string list =
    SdkManifest.discover root
    |> List.filter (fun p -> declaresOwnVersion (IO.File.ReadAllText(IO.Path.Combine(root, p.ProjectPath))))
    |> List.map _.PackageId

/// `awaitedIds` over the tree at `root` — the one call the target makes.
let awaitedIdsIn (root: string) =
    awaitedIds (SdkManifest.expected root) (independentlyVersionedIn root) probeIds

// ─── Which version the probe is about ────────────────────────────────

let private versionTagPattern =
    Regex(@"^(?:refs/tags/)?v(\d+\.\d+\.\d+[0-9A-Za-z.\-+]*)$", RegexOptions.Compiled)

let private propsVersionPattern =
    Regex(@"<Version>\s*([^<\s][^<]*?)\s*</Version>", RegexOptions.Compiled)

/// The version the probe restores.
///
/// On the release path it is READ OFF THE TAG that triggered the run —
/// `refs/tags/v0.23.0` → `0.23.0` — because the tag is what the publish
/// job just pushed, so no copy of the number can drift from it. Off the
/// tag path (a `workflow_dispatch` heal, a developer run) it falls back to
/// the repo's single declared `<Version>` in `Directory.Build.props`,
/// which is exactly what a dispatch packs.
///
/// Deliberately NOT a constant of its own. The repo already carries two
/// version knobs that must be kept in step by hand (`<Version>` and
/// `<ToolUpSdkVersion>`, per `publish-nuget.yml`'s header) and
/// `VerifyPackagedModuleTemplate`'s `--sdk-version` is a third thing that
/// has to move at each release. A fourth would be one more chance for the
/// gate to probe a version nobody released.
let versionUnderTest (gitRef: string option) (propsText: string) : Result<string, string> =
    match gitRef with
    | Some ref when not (String.IsNullOrWhiteSpace ref) ->
        let m = versionTagPattern.Match(ref.Trim())

        if m.Success then
            Ok m.Groups[1].Value
        else
            Error(
                sprintf
                    "VerifyPublishedPackages: the ref `%s` is not a `v<major>.<minor>.<patch>` release tag, so no published version can be read from it. The gate runs on the `v*.*.*` tag push; leave the ref unset to fall back to `<Version>` in Directory.Build.props."
                    ref
            )
    | _ ->
        let m = propsVersionPattern.Match propsText

        if m.Success then
            Ok(m.Groups[1].Value)
        else
            Error
                "VerifyPublishedPackages: no `<Version>` element in Directory.Build.props, and no release tag to read one from. The probe has no version to restore."

// ─── nuget.org index availability (the Phase 255 lesson) ─────────────

/// The flat-container index for a package id. Lowercased: the v3 flat
/// container is case-sensitive in its paths even though ids are not.
let flatContainerIndexUrl (packageId: string) =
    sprintf "https://api.nuget.org/v3-flatcontainer/%s/index.json" (packageId.ToLowerInvariant())

let private versionsArrayPattern =
    Regex("\"versions\"\\s*:\\s*\\[(?<body>[^\\]]*)\\]", RegexOptions.Compiled)

let private quotedPattern = Regex("\"([^\"]*)\"", RegexOptions.Compiled)

/// Does this flat-container index body serve `version`?
///
/// Reads ONLY inside the `versions` array. A body that is not that shape
/// answers `false` rather than throwing, and it is a shape the probe meets
/// routinely: for an unpublished id the storage layer returns an XML
/// `BlobNotFound` document, and a naive substring search over it could
/// match a version-shaped run of digits in a request id or timestamp and
/// report a package as available that does not exist.
///
/// Version comparison is ordinal-case-insensitive on the exact string:
/// NuGet normalises versions on push, and the tag this is derived from is
/// the same string the pack used, so an equality that "helpfully"
/// normalised would hide a real mismatch between the tag and what shipped.
let indexServes (version: string) (indexBody: string) : bool =
    if String.IsNullOrWhiteSpace indexBody then
        false
    else
        let m = versionsArrayPattern.Match indexBody

        if not m.Success then
            false
        else
            quotedPattern.Matches(m.Groups["body"].Value)
            |> Seq.exists (fun q -> String.Equals(q.Groups[1].Value, version, StringComparison.OrdinalIgnoreCase))

/// The message for a version the index never served inside the budget.
///
/// It says WAITED rather than MISSING on purpose. nuget.org indexes a
/// push minutes after it is accepted, so a probe that runs immediately
/// and reports "the package does not exist" records a false red about a
/// release that is fine — the Phase 255 lesson. Whether this is a real
/// publish failure or a slow index is not decidable from here, so the
/// message says exactly what was observed and names both readings.
let indexTimeoutReport (packageId: string) (version: string) (waited: TimeSpan) (lastBody: string) =
    sprintf
        "VerifyPublishedPackages: nuget.org did not serve %s %s within %.0f minutes of polling %s. Either the publish did not push this package (check the `publish` job's log), or the index is running slow — a push is accepted before it is indexed, which is why this waits rather than probing once. Last index body (first 400 chars): %s"
        packageId
        version
        waited.TotalMinutes
        (flatContainerIndexUrl packageId)
        (let b =
            (if isNull lastBody then "" else lastBody).Replace("\r", "").Replace("\n", " ")

         if b.Length > 400 then b.Substring(0, 400) + "…" else b)

/// The awaited ids an index round did NOT see served, in input order.
/// `bodies` pairs each polled id with the body its index returned.
let stillPending (version: string) (bodies: (string * string) list) : string list =
    bodies
    |> List.filter (fun (_, body) -> not (indexServes version body))
    |> List.map fst

/// The timeout failure over every id still pending when the budget ran
/// out — one `indexTimeoutReport` per id, so each says what its own index
/// last returned, under a line saying how many of the awaited set it is.
let indexTimeoutReports (version: string) (awaitedCount: int) (waited: TimeSpan) (pending: (string * string) list) =
    let header =
        sprintf
            "VerifyPublishedPackages: %d of the %d awaited package(s) were not served at %s when the wait ran out:"
            (List.length pending)
            awaitedCount
            version

    header
    :: (pending
        |> List.map (fun (id, body) -> "  - " + indexTimeoutReport id version waited body))
    |> String.concat Environment.NewLine

// ─── A restore that still misses a ToolUp package ────────────────────

let private restoreMissPattern =
    Regex(
        @"\b(?<code>NU110[12]):\s*Unable to find package\s+(?<id>ToolUp(?:\.[A-Za-z0-9_\-]+)+)",
        RegexOptions.Compiled
    )

/// The `ToolUp.*` ids a failed restore's output reports as unresolvable
/// (`NU1101` no such id, `NU1102` no such version), deduplicated
/// case-insensitively, in first-seen order. Other ids are not this gate's
/// question: a third-party miss is the restore's own message to read.
let restoreMisses (restoreOutput: string) : string list =
    if String.IsNullOrEmpty restoreOutput then
        []
    else
        restoreMissPattern.Matches restoreOutput
        |> Seq.map (fun m -> m.Groups["id"].Value)
        |> Seq.distinctBy (fun id -> id.ToLowerInvariant())
        |> List.ofSeq

/// What a restore miss MEANS, decided per id from two facts observed at
/// the moment of failure: was the id in the awaited set, and does the
/// index serve it now. Each pairing names a different defect, which is the
/// point — "re-run it" is the remedy for only one of them.
///
/// - awaited, served now: the wait saw it, the restore did not — the
///   restore's own resolution path (registration, CDN) lagged the flat
///   container. Indexing lag; the release is fine.
/// - awaited, NOT served: the wait cannot have passed with it pending, so
///   it stopped being served between the two — the same lag in reverse,
///   recorded rather than guessed at.
/// - not awaited, served now: the awaited set is incomplete — the gate's
///   defect, not the release's.
/// - not awaited, NOT served: nothing pushed it at this version — the
///   release never published it, or it is independently versioned and a
///   lockstep package depends on it at the wrong number.
let restoreMissReport (version: string) (awaited: string list) (misses: (string * bool) list) =
    let awaitedSet =
        awaited |> List.map (fun (id: string) -> id.ToLowerInvariant()) |> Set.ofList

    let describe (id: string, servedNow: bool) =
        let wasAwaited = awaitedSet.Contains(id.ToLowerInvariant())

        let reading =
            match wasAwaited, servedNow with
            | true, true ->
                "awaited, and the index serves it NOW: indexing lag between the flat container the wait polls and the path the restore resolves through. The release is fine; re-run the job."
            | true, false ->
                "awaited, and the index does NOT serve it now: it was served when the wait passed and is not served at the moment of failure — index lag in reverse. Re-run the job, and read the index if it repeats."
            | false, true ->
                "NOT in the awaited set, and the index serves it now: the restore outran a package the wait did not know to wait for — a defect in `PublishedSmoke.awaitedIds`, not in the release."
            | false, false ->
                "NOT in the awaited set, and the index does NOT serve it: nothing published it at this version. Either the release never pushed it, or it is independently versioned and a lockstep package depends on it at the release version."

        sprintf "  - %s %s: %s" id version reading

    sprintf
        "VerifyPublishedPackages: the restore could not resolve %d ToolUp package(s) at %s:%s%s"
        (List.length misses)
        version
        Environment.NewLine
        (misses |> List.map describe |> String.concat Environment.NewLine)

// ─── The "fresh machine" invariant ───────────────────────────────────

let private normaliseDir (path: string) =
    let full = IO.Path.GetFullPath path

    let trimmed =
        full.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)

    trimmed.Replace(IO.Path.AltDirectorySeparatorChar, IO.Path.DirectorySeparatorChar)

/// Is `probeDir` outside the repository working tree?
///
/// This is the whole point of the gate and it is one comparison, so it is
/// stated once and proven both ways rather than assumed by whoever picks
/// the scratch path. A probe UNDER the checkout is not a fresh machine:
/// `nuget.config` files MERGE up the directory tree, so forge's own
/// sources — including the workspace-shared local feed — would join the
/// resolve path and paper over exactly the missing-package defect this
/// exists to catch, and MSBuild's `Directory.Build.props` walk would add
/// the repo's central package versions on top. The failure mode is a
/// GREEN gate over a broken release, which is the worst kind.
///
/// Case-insensitive on Windows only, matching the filesystem: a
/// case-insensitive comparison on Linux would call `/tmp/Repo` inside
/// `/tmp/repo` and refuse a legitimate probe path.
let isOutsideRepo (repoRoot: string) (probeDir: string) : bool =
    let root = normaliseDir repoRoot
    let probe = normaliseDir probeDir

    let comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    not (
        String.Equals(probe, root, comparison)
        || probe.StartsWith(root + string IO.Path.DirectorySeparatorChar, comparison)
    )

let insideRepoReport (repoRoot: string) (probeDir: string) =
    sprintf
        "VerifyPublishedPackages: the probe directory %s is INSIDE the repository at %s, so this is not a fresh-machine restore. nuget.config files merge up the directory tree and MSBuild walks Directory.Build.props upward, so the repo's own sources and central package versions would join the probe's resolve path — and the gate would go green over a package set a real consumer cannot restore. Scaffold the probe under the system temp directory."
        probeDir
        repoRoot

// ─── What the probe scaffold contains ────────────────────────────────

/// The probe's `nuget.config`. `<clear />` is load-bearing twice over: it
/// drops any machine-level source (a dev box may carry the workspace
/// local feed globally) and, together with the outside-the-repo rule
/// above, guarantees nuget.org is the ONLY place the probe can resolve a
/// `ToolUp.*` package from — which is what makes a green run mean "a
/// consumer can install this".
let nugetConfig =
    """<?xml version="1.0" encoding="utf-8"?>
<!-- Phase 184 fresh-machine probe. <clear /> is the gate: nuget.org must
     be the only source, or a green restore proves nothing about what a
     consumer can install. -->
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"""

let private packageReferences (version: string) (ids: string list) =
    ids
    |> List.map (fun id -> sprintf "        <PackageReference Include=\"%s\" Version=\"%s\" />" id version)
    |> String.concat Environment.NewLine

/// The DLL-tier probe project: the published packages, referenced by
/// version, with nothing else on the resolve path.
///
/// `ManagePackageVersionsCentrally=false` is explicit rather than
/// inherited-by-absence, so the project states its own contract instead of
/// depending on there being no `Directory.Packages.props` above it.
let dllProbeProject (version: string) =
    sprintf
        """<?xml version="1.0" encoding="utf-8"?>
<!-- Phase 184 fresh-machine probe (DLL tier). Generated; edit the
     generator in PublishedSmoke.fs, not a copy of this file. -->
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <IsPackable>false</IsPackable>
        <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    </PropertyGroup>
    <ItemGroup>
        <Compile Include="Probe.fs" />
    </ItemGroup>
    <ItemGroup>
%s
    </ItemGroup>
</Project>
"""
        (packageReferences version dllProbeIds)

/// The Fable-tier probe project.
///
/// `<Nullable>disable</Nullable>` matches every Fable-touching project in
/// this repo: the Fable ecosystem pre-dates F# 10 nullable reference
/// types, and enabling it cascades "null / not null inconsistent" errors
/// out of libraries the probe does not own.
let fableProbeProject (version: string) =
    sprintf
        """<?xml version="1.0" encoding="utf-8"?>
<!-- Phase 184 fresh-machine probe (Fable tier). Generated; edit the
     generator in PublishedSmoke.fs, not a copy of this file. -->
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <IsPackable>false</IsPackable>
        <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
        <Nullable>disable</Nullable>
        <DefaultItemExcludes>$(DefaultItemExcludes);output\**;node_modules\**</DefaultItemExcludes>
    </PropertyGroup>
    <ItemGroup>
        <Compile Include="Probe.fs" />
    </ItemGroup>
    <ItemGroup>
%s
    </ItemGroup>
</Project>
"""
        (packageReferences version fableProbeIds)

/// The probe source.
///
/// It `open`s the NAMESPACES the packages contribute and does nothing
/// else, and the restraint is deliberate. An `open` of a namespace no
/// referenced assembly contributes is a compile error, so this proves the
/// packages restored, their transitive closure resolved, and the
/// assemblies loaded — which is the class the gate is for. Naming a TYPE
/// or a member would pin the probe to one release's public surface, and
/// the gate would then go red at the next legitimate rename: a red that
/// says "your release is unusable" about a change that is nothing of the
/// sort. The repo already gates its public surface (the api-baselines
/// approval pack); this gate is about the PACKAGE, not the API.
let probeSource (namespaces: string list) =
    let header = [
        "// Phase 184 fresh-machine probe. Generated; see PublishedSmoke.fs."
        "//"
        "// Opening a namespace no referenced assembly contributes is a compile"
        "// error, so this file failing to build means the published packages did"
        "// not restore, or their transitive closure did not resolve."
        "module Probe"
        ""
    ]

    let opens = namespaces |> List.map (sprintf "open %s")

    String.concat Environment.NewLine (header @ opens @ [ ""; "let probed = true"; "" ])

/// Namespaces the DLL-tier probe opens: `ToolUp.Platform` comes from
/// Core + Server, `ToolUp.AI` from the AI tier.
let dllProbeNamespaces = [ "ToolUp.Platform"; "ToolUp.AI" ]

/// The Fable tier's namespace. `ToolUp.Platform.Client` packs its source
/// under `fable/`, so what this compiles is the packed SOURCE, not the
/// DLL — which is the half a `dotnet build` cannot reach.
let fableProbeNamespaces = [ "ToolUp.Platform" ]

// ─── Workflow lint ───────────────────────────────────────────────────

/// The job id in `publish-nuget.yml`, and the FAKE target it must call.
let smokeJobId = "published-package-smoke"

let smokeTargetName = "VerifyPublishedPackages"

let private jobLinePattern =
    Regex(@"^  (?<id>[A-Za-z0-9_.-]+):\s*$", RegexOptions.Compiled)

/// The lines of one top-level job block in a GitHub workflow, or `None`
/// when no job of that id is declared. Indentation-scoped rather than
/// YAML-parsed: this module is BCL-only by design, and the question asked
/// is small enough that a parser dependency would cost more than it buys.
let jobBlock (jobId: string) (workflowText: string) : string list option =
    let lines = workflowText.Replace("\r\n", "\n").Split('\n') |> List.ofArray

    let rec collect acc inBlock remaining =
        match remaining with
        | [] -> if inBlock then Some(List.rev acc) else None
        | (line: string) :: rest ->
            let m = jobLinePattern.Match line

            if m.Success then
                if m.Groups["id"].Value = jobId then collect [] true rest
                elif inBlock then Some(List.rev acc)
                else collect acc false rest
            elif inBlock then
                collect (line :: acc) true rest
            else
                collect acc false rest

    collect [] false lines

/// What is wrong with the smoke job's declaration in `publish-nuget.yml`,
/// as a list of findings — empty means well-formed.
///
/// **Why a lint and not just the target's own runtime check.** The
/// outside-the-repo guard lives in the target, so it protects any run that
/// GOES THROUGH the target. The way to lose it is not to break it but to
/// bypass it: a later edit that inlines `dotnet add package` as workflow
/// shell steps runs in the checkout, resolves through the repo's own
/// `nuget.config`, and reports green. That edit looks entirely reasonable
/// in a diff. So the workflow is checked for the property the diff would
/// silently drop, and the check runs in a pack that gates every push —
/// unlike the gate itself, which only ever runs on a release tag.
let smokeJobFindings (workflowText: string) : string list =
    match jobBlock smokeJobId workflowText with
    | None -> [
        sprintf
            "publish-nuget.yml declares no `%s` job. The fresh-machine probe is the only check that a released package installs on a clean machine; without it a tag publishes unverified."
            smokeJobId
      ]
    | Some block ->
        let text = String.concat "\n" block

        [
            if not (Regex.IsMatch(text, @"needs:\s*(publish\b|\[[^\]]*\bpublish\b)")) then
                sprintf
                    "the `%s` job does not declare `needs: publish`. Without it the probe races the push and reports a false red against a version nuget.org has not been given yet."
                    smokeJobId

            if not (text.Contains smokeTargetName) then
                sprintf
                    "the `%s` job does not invoke the `%s` target. The gate's rules — the outside-the-repo scaffold, the index-availability wait, the probe closure — live in the target, so a job that does its own thing runs none of them."
                    smokeJobId
                    smokeTargetName

            if Regex.IsMatch(text, @"dotnet\s+(add\s+package|restore|fable)\b") then
                sprintf
                    "the `%s` job runs `dotnet add package` / `restore` / `fable` directly. Those steps execute in the repository checkout, where nuget.config merges the repo's own sources onto the resolve path — the probe would then prove nothing about a fresh machine and would report green. Let the target scaffold the probe outside the tree."
                    smokeJobId
        ]

let smokeJobReport (findings: string list) =
    sprintf
        "The published-package smoke gate is not wired as declared (%d finding(s)):%s%s"
        (List.length findings)
        Environment.NewLine
        (findings |> List.map (sprintf "  - %s") |> String.concat Environment.NewLine)
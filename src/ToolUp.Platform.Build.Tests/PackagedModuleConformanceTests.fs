// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.PackagedModuleConformanceTests

open System
open System.IO
open Expecto
open ToolUp.Platform

// ─── Phase 586 — packaged-module shadow-project conformance ────────────
//
// Fixture tests over the four packaging laws. The pure arm builds the
// two source lists + the manifest by hand and mutates ONE thing per
// test, so each drift class is isolated and the law it breaks is
// asserted by name. The on-disk arm drives the same laws through the
// loaders (project XML + wildcard expansion + the pre-Pack manifest
// derivation), proving the acceptance criterion that the check runs
// without building — no MSBuild evaluation, no Fable, no consumer app.

// ─── The conformant fixture ────────────────────────────────────────────

let private shadowProjectFile = "My.Module.Fable.fsproj"

/// The four-file module convention: the main project compiles the
/// server tier and DECLARES the client files as `<None>` (the consumer's
/// Fable compilation owns them).
let private conformantMain = {
    ProjectLabel = "My.Module.fsproj"
    DeclaredOrder = [ "SharedTypes.fs"; "Server.fs"; "ClientModel.fs"; "Icons.fs"; "ClientView.fs" ]
    CompiledFiles = [ "SharedTypes.fs"; "Server.fs" ]
    UnresolvedPatterns = []
}

let private conformantShadow = {
    ProjectLabel = shadowProjectFile
    DeclaredOrder = [ "SharedTypes.fs"; "ClientModel.fs"; "Icons.fs"; "ClientView.fs" ]
    CompiledFiles = [ "SharedTypes.fs"; "ClientModel.fs"; "Icons.fs"; "ClientView.fs" ]
    UnresolvedPatterns = []
}

let private conformantContract = {
    PackagedModuleContract.create "My.Module" shadowProjectFile with
        ServerOnlyFiles = [ "Server.fs" ]
        RequiredAssets = [ "icons/chart.svg" ]
}

let private conformantManifest = {
    ManifestLabel = "My.Module.1.0.0.nupkg"
    PackagePaths = [
        "My.Module.nuspec"
        "lib/net10.0/My.Module.dll"
        $"fable/{shadowProjectFile}"
        "fable/My.Module.fsproj"
        "fable/SharedTypes.fs"
        "fable/ClientModel.fs"
        "fable/Icons.fs"
        "fable/ClientView.fs"
        "fable/icons/chart.svg"
    ]
}

// ─── Assertion helpers ─────────────────────────────────────────────────

/// The violations naming a given law.
let private under law (violations: ShadowLayoutViolation list) =
    violations |> List.filter (fun v -> v.ViolatedLaw = law)

/// Every law that the violation set names, de-duplicated.
let private lawsNamed (violations: ShadowLayoutViolation list) =
    violations |> List.map (fun v -> v.ViolatedLaw) |> List.distinct

/// Assert that exactly one law fired, that it is `law`, and that its
/// rendered form carries the law id + the expected subject.
let private expectOnly law (subjectFragment: string) (violations: ShadowLayoutViolation list) =
    Expect.equal
        (lawsNamed violations)
        [ law ]
        $"expected only the '{ShadowLayoutLaw.name law}' law to fire, got: %A{violations |> List.map ShadowLayoutViolation.render}"

    let rendered = under law violations |> List.map ShadowLayoutViolation.render

    Expect.isTrue
        (rendered
         |> List.exists (fun r -> r.Contains(subjectFragment, StringComparison.OrdinalIgnoreCase)))
        $"expected a '{ShadowLayoutLaw.name law}' violation naming '{subjectFragment}', got: %A{rendered}"

    Expect.isTrue
        (rendered
         |> List.forall (fun r -> r.StartsWith($"[{ShadowLayoutLaw.name law}]", StringComparison.Ordinal)))
        $"every rendered violation must lead with its law id, got: %A{rendered}"

// ─── On-disk fixture ───────────────────────────────────────────────────

let private withTempDir (f: string -> 'a) =
    let dir =
        Path.Combine(Path.GetTempPath(), "packaged-module-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private mainProjectXml (packExclude: string) =
    $"""<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
    <ItemGroup>
        <Content Include="**\*.fsproj;**\*.fs;**\*.svg" Exclude="{packExclude}" PackagePath="fable\" />
    </ItemGroup>
    <ItemGroup>
        <None Include="README.md" Pack="true" PackagePath="\" />
    </ItemGroup>
    <ItemGroup>
        <Compile Include="SharedTypes.fs" />
        <Compile Include="Server.fs" />
        <None Include="ClientModel.fs" />
        <None Include="Icons.fs" />
        <None Include="ClientView.fs" />
    </ItemGroup>
</Project>"""

let private shadowProjectXml =
    """<Project Sdk="Microsoft.NET.Sdk">
    <ItemGroup>
        <Compile Include="SharedTypes.fs" />
        <Compile Include="ClientModel.fs" />
        <Compile Include="Icons.fs" />
        <Compile Include="ClientView.fs" />
    </ItemGroup>
</Project>"""

/// Materialise a packaged module on disk. `packExclude` is the pack
/// glob's `Exclude` attribute — the one lever that decides whether the
/// server tier leaks into `fable/`.
let private writeModule (dir: string) (packExclude: string) =
    let write name (content: string) =
        let path = Path.Combine(dir, name)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    write "My.Module.fsproj" (mainProjectXml packExclude)
    write shadowProjectFile shadowProjectXml
    write "README.md" "# My.Module"
    write "SharedTypes.fs" "module My.Module.SharedTypes"
    write "Server.fs" "module My.Module.Server"
    write "ClientModel.fs" "module My.Module.ClientModel"
    write "Icons.fs" "module My.Module.Icons"
    write "ClientView.fs" "module My.Module.ClientView"
    write "icons/chart.svg" "<svg/>"

    {
        MainProject = Path.Combine(dir, "My.Module.fsproj")
        ShadowProject = Path.Combine(dir, shadowProjectFile)
        ManifestSource = FromPackDeclarations
        Contract = conformantContract
    }

// ─── Tests ─────────────────────────────────────────────────────────────

let tests =
    testList "PackagedModuleConformance" [

        testList "the conformant baseline" [
            test "a conformant module passes every law" {
                let violations =
                    PackagedModuleConformance.check
                        conformantContract
                        conformantMain
                        conformantShadow
                        conformantManifest

                Expect.isEmpty
                    violations
                    $"the fixture module must be conformant, got: %A{violations |> List.map ShadowLayoutViolation.render}"
            }

            test "the report names the module and the law count when conformant" {
                let text = PackagedModuleConformance.report conformantContract []
                Expect.stringContains text "My.Module" "the report names the module"
                Expect.stringContains text "conformant" "the report says so"
            }

            test "a single-project module checks against itself" {
                // The in-tree source-in-nupkg convention: the module's own
                // project file IS the shadow. Subset + order hold trivially;
                // exclusion + asset-path still bite.
                let single = {
                    ProjectLabel = "Solo.Client.fsproj"
                    DeclaredOrder = [ "Client/A.fs"; "Client/B.fs" ]
                    CompiledFiles = [ "Client/A.fs"; "Client/B.fs" ]
                    UnresolvedPatterns = []
                }

                let contract = PackagedModuleContract.create "Solo.Client" "Solo.Client.fsproj"

                let manifest = {
                    ManifestLabel = "Solo.Client.nupkg"
                    PackagePaths = [ "fable/Solo.Client.fsproj"; "fable/Client/A.fs"; "fable/Client/B.fs" ]
                }

                let violations = PackagedModuleConformance.check contract single single manifest

                Expect.isEmpty
                    violations
                    $"the single-project shape must be conformant, got: %A{violations |> List.map ShadowLayoutViolation.render}"
            }
        ]

        testList "drift class → law" [
            test "a leaked server file fails the server-exclusion law" {
                let shadow = {
                    conformantShadow with
                        CompiledFiles = [ "SharedTypes.fs"; "Server.fs"; "ClientModel.fs"; "Icons.fs"; "ClientView.fs" ]
                }

                // The leaked file is packed too — the realistic shape of
                // the defect (a pack glob with no server Exclude).
                let manifest = {
                    conformantManifest with
                        PackagePaths = "fable/Server.fs" :: conformantManifest.PackagePaths
                }

                PackagedModuleConformance.check conformantContract conformantMain shadow manifest
                |> expectOnly ShadowExclusionLaw "Server.fs"
            }

            test "a server-only DIRECTORY leaking into the pack fails the server-exclusion law" {
                let contract = {
                    conformantContract with
                        ServerOnlyDirectories = [ "Server/" ]
                }

                let manifest = {
                    conformantManifest with
                        PackagePaths = "fable/Server/Handlers.fs" :: conformantManifest.PackagePaths
                }

                PackagedModuleConformance.check contract conformantMain conformantShadow manifest
                |> expectOnly ShadowExclusionLaw "Server/Handlers.fs"
            }

            test "a missing client file fails the shadow-subset law" {
                let shadow = {
                    conformantShadow with
                        CompiledFiles = [ "SharedTypes.fs"; "ClientModel.fs"; "ClientView.fs" ]
                }

                // Icons.fs is still packed — the file shipped, the shadow
                // project just never lists it, which is precisely the
                // failure the consumer's Fable build discovers.
                PackagedModuleConformance.check conformantContract conformantMain shadow conformantManifest
                |> expectOnly ShadowSubsetLaw "Icons.fs"
            }

            test "a shadow file the main project never declared fails the shadow-subset law" {
                let shadow = {
                    conformantShadow with
                        CompiledFiles = conformantShadow.CompiledFiles @ [ "Ghost.fs" ]
                }

                let manifest = {
                    conformantManifest with
                        PackagePaths = "fable/Ghost.fs" :: conformantManifest.PackagePaths
                }

                PackagedModuleConformance.check conformantContract conformantMain shadow manifest
                |> expectOnly ShadowSubsetLaw "Ghost.fs"
            }

            test "an order swap fails the compile-order law" {
                let shadow = {
                    conformantShadow with
                        CompiledFiles = [ "SharedTypes.fs"; "ClientView.fs"; "Icons.fs"; "ClientModel.fs" ]
                }

                PackagedModuleConformance.check conformantContract conformantMain shadow conformantManifest
                |> expectOnly ShadowCompileOrderLaw "ClientModel.fs"
            }

            test "a missing asset fails the asset-path law" {
                let manifest = {
                    conformantManifest with
                        PackagePaths =
                            conformantManifest.PackagePaths
                            |> List.filter (fun p -> p <> "fable/icons/chart.svg")
                }

                PackagedModuleConformance.check conformantContract conformantMain conformantShadow manifest
                |> expectOnly ShadowAssetPathLaw "icons/chart.svg"
            }

            test "a shadow project file absent from the pack fails the asset-path law" {
                let manifest = {
                    conformantManifest with
                        PackagePaths =
                            conformantManifest.PackagePaths
                            |> List.filter (fun p -> p <> $"fable/{shadowProjectFile}")
                }

                PackagedModuleConformance.check conformantContract conformantMain conformantShadow manifest
                |> expectOnly ShadowAssetPathLaw shadowProjectFile
            }

            test "a client file that never got packed fails the asset-path law" {
                let manifest = {
                    conformantManifest with
                        PackagePaths =
                            conformantManifest.PackagePaths
                            |> List.filter (fun p -> p <> "fable/ClientView.fs")
                }

                PackagedModuleConformance.check conformantContract conformantMain conformantShadow manifest
                |> expectOnly ShadowAssetPathLaw "ClientView.fs"
            }

            test "an unexpanded wildcard makes the subset law undecidable rather than passing" {
                let shadow = {
                    conformantShadow with
                        CompiledFiles = []
                        UnresolvedPatterns = [ "Client/**/*.fs" ]
                }

                let violations =
                    PackagedModuleConformance.check conformantContract conformantMain shadow conformantManifest

                let subset = under ShadowSubsetLaw violations
                Expect.isNonEmpty subset "an unexpanded wildcard must not pass silently"

                Expect.isTrue
                    (subset |> List.exists (fun v -> v.Explanation.Contains "cannot be decided"))
                    $"the undecidable case must say so, got: %A{subset |> List.map ShadowLayoutViolation.render}"
            }
        ]

        testList "law vocabulary" [
            test "every law has a distinct id and description" {
                let names = ShadowLayoutLaw.all |> List.map ShadowLayoutLaw.name
                Expect.equal (List.distinct names) names "law ids must be distinct"
                Expect.equal (List.length names) 4 "there are four packaging laws"

                for law in ShadowLayoutLaw.all do
                    Expect.isNotEmpty
                        (ShadowLayoutLaw.describe law)
                        $"law {ShadowLayoutLaw.name law} must describe itself"
            }

            test "server-only classification honours files and directory prefixes" {
                let contract = {
                    PackagedModuleContract.create "M" "M.fsproj" with
                        ServerOnlyFiles = [ "Server.fs" ]
                        ServerOnlyDirectories = [ "Server" ]
                }

                Expect.isTrue (PackagedModuleConformance.isServerOnly contract "Server.fs") "named file"
                Expect.isTrue (PackagedModuleConformance.isServerOnly contract "server.fs") "case-insensitive"
                Expect.isTrue (PackagedModuleConformance.isServerOnly contract "Server/Handlers.fs") "directory prefix"
                Expect.isFalse (PackagedModuleConformance.isServerOnly contract "ServerView.fs") "prefix is per-segment"
                Expect.isFalse (PackagedModuleConformance.isServerOnly contract "Client/View.fs") "client file"
            }
        ]

        testList "loading" [
            test "project XML yields declaration order across Compile and None" {
                let parsed =
                    PackagedModuleConformance.Load.sourceListFromXml "M.fsproj" None (mainProjectXml "**\\bin\\**")

                Expect.equal
                    parsed.DeclaredOrder
                    [ "SharedTypes.fs"; "Server.fs"; "ClientModel.fs"; "Icons.fs"; "ClientView.fs" ]
                    "Compile and None interleave in declaration order; README.md is not a source file"

                Expect.equal parsed.CompiledFiles [ "SharedTypes.fs"; "Server.fs" ] "only Compile items compile"
            }

            test "pre-Pack declarations reproduce NuGet's PackagePath mapping" {
                withTempDir (fun dir ->
                    writeModule dir "**\\Server.fs;**\\bin\\**;**\\obj\\**" |> ignore

                    let manifest =
                        PackagedModuleConformance.Load.packDeclarations (Path.Combine(dir, "My.Module.fsproj"))

                    let paths = manifest.PackagePaths |> List.map (fun p -> p.ToLowerInvariant())

                    Expect.contains
                        paths
                        "fable/clientview.fs"
                        "the recursive glob keeps the relative path under fable/"

                    Expect.contains paths "fable/icons/chart.svg" "nested assets keep their subdirectory"

                    Expect.contains
                        paths
                        $"fable/{shadowProjectFile.ToLowerInvariant()}"
                        "the shadow project ships too"

                    Expect.isFalse
                        (List.contains "fable/server.fs" paths)
                        "the Exclude keeps the server tier out of fable/"

                    Expect.contains paths "readme.md" "a literal include with PackagePath=\\ flattens to its file name")
            }
        ]

        testList "end-to-end, without building anything" [
            test "a conformant module on disk verifies clean" {
                withTempDir (fun dir ->
                    let options = writeModule dir "**\\Server.fs;**\\bin\\**;**\\obj\\**"
                    let violations = PackagedModuleConformance.verify options

                    Expect.isEmpty
                        violations
                        $"the on-disk fixture must be conformant, got: %A{violations |> List.map ShadowLayoutViolation.render}"

                    // The acceptance criterion, stated as a test: no
                    // build output exists — nothing was compiled.
                    Expect.isFalse (Directory.Exists(Path.Combine(dir, "obj"))) "the check builds nothing"
                    Expect.isFalse (Directory.Exists(Path.Combine(dir, "bin"))) "the check builds nothing")
            }

            test "dropping the pack Exclude leaks the server tier and names the law" {
                withTempDir (fun dir ->
                    let options = writeModule dir "**\\bin\\**;**\\obj\\**"

                    PackagedModuleConformance.verify options
                    |> expectOnly ShadowExclusionLaw "Server.fs")
            }

            test "assertConformant raises with the full report" {
                withTempDir (fun dir ->
                    let options = writeModule dir "**\\bin\\**;**\\obj\\**"

                    let message =
                        try
                            PackagedModuleConformance.assertConformant options
                            None
                        with ex ->
                            Some ex.Message

                    match message with
                    | None -> failtest "a non-conformant module must raise"
                    | Some m ->
                        Expect.stringContains
                            m
                            (ShadowLayoutLaw.name ShadowExclusionLaw)
                            "the raised report names the broken law")
            }

            test "a conformant module's assertConformant is silent" {
                withTempDir (fun dir ->
                    let options = writeModule dir "**\\Server.fs;**\\bin\\**;**\\obj\\**"
                    PackagedModuleConformance.assertConformant options)
            }
        ]
    ]
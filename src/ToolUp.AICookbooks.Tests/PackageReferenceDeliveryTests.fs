// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AICookbooks.Tests.PackageReferenceDeliveryTests

// Phase 768 — the two companions deliver their COOKBOOK.md to a
// PackageReference consumer, not only to a ProjectReference one.
//
// The builders resolve the cookbook assembly-relatively
// (`Path.Combine(asmDir, CookbookFileName)`). A ProjectReference consumer
// gets the file there through the Content item's CopyToOutputDirectory; a
// PackageReference consumer only ever got the legacy `content\` folder,
// which NuGet does not deliver to PackageReference projects, so both
// builders warned once and served an empty cookbook. The packs now also
// carry the file under `contentFiles/any/any/` with `copyToOutput`.
//
// This is the end-to-end contract: pack the companion (already built — no
// rebuild), restore it into a throwaway consumer from a private scratch
// feed, build the consumer, and assert the cookbook lands BESIDE the
// companion assembly under the builder's file name, then that the builder
// reads it to non-empty guidance with no warning. The pack uses a
// throwaway version and suppresses dependencies, so the restore needs
// nothing but the scratch feed: no network, no shared feed, and no
// global-packages pollution (the consumer restores into its own folder).

open System
open System.Diagnostics
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.AI.SystemPromptBuilder

/// A logger that records every Warn/Error, so "emits no warning" is an
/// assertion rather than an absence of console noise.
type private CapturingLogger() =
    let problems = ResizeArray<string>()
    member _.Problems = List.ofSeq problems

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn m = problems.Add("WARN " + m)
        member _.Error(m, _) = problems.Add("ERROR " + m)

/// The forge `src/` directory, found by walking up from the test assembly.
let private srcRoot () =
    let rec up (d: DirectoryInfo) =
        if isNull d then
            failwithf "could not find src/AICookbooks above %s" AppContext.BaseDirectory
        elif Directory.Exists(Path.Combine(d.FullName, "AICookbooks")) then
            d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// The configuration this test assembly was built in (`bin/<Config>/<tfm>/`),
/// which is the configuration the referenced companions were built in too —
/// the pack below is `--no-build`, so it must name the one whose output exists.
let private buildConfiguration () =
    let tfmDir =
        DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/'))

    let configDir = tfmDir.Parent

    if isNull configDir || isNull configDir.Parent || configDir.Parent.Name <> "bin" then
        failwithf "expected the test assembly under bin/<Config>/<tfm>/, found %s" tfmDir.FullName

    configDir.Name

/// Run `dotnet <args>` in `workDir`; fail with the captured output on a
/// non-zero exit or a timeout. MSBuild variables inherited from a parent
/// `dotnet run` / FAKE host are cleared so the child resolves its own SDK.
let private dotnet (workDir: string) (args: string list) =
    let psi = ProcessStartInfo("dotnet")

    for a in args do
        psi.ArgumentList.Add a

    psi.WorkingDirectory <- workDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false

    for v in
        [
            "MSBUILD_EXE_PATH"
            "MSBuildExtensionsPath"
            "MSBuildSDKsPath"
            "MSBuildLoadMicrosoftTargetsReadOnly"
        ] do
        psi.Environment.Remove v |> ignore

    psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
    psi.Environment["DOTNET_NOLOGO"] <- "1"

    use p = new Process(StartInfo = psi)
    let out = StringBuilder()

    p.OutputDataReceived.Add(fun e ->
        if not (isNull e.Data) then
            lock out (fun () -> out.AppendLine e.Data |> ignore))

    p.ErrorDataReceived.Add(fun e ->
        if not (isNull e.Data) then
            lock out (fun () -> out.AppendLine e.Data |> ignore))

    p.Start() |> ignore
    p.BeginOutputReadLine()
    p.BeginErrorReadLine()

    if not (p.WaitForExit(TimeSpan.FromMinutes 5.0)) then
        try
            p.Kill true
        with _ ->
            ()

        failwithf "dotnet %s timed out after 5 minutes:\n%O" (String.Join(" ", args)) out

    p.WaitForExit()

    if p.ExitCode <> 0 then
        failwithf "dotnet %s exited %d:\n%O" (String.Join(" ", args)) p.ExitCode out

/// Pack `projectRel` into a scratch feed, restore it into a consumer, build
/// the consumer, and return the consumer's output directory.
let private deliverThroughPackageReference (scratch: string) (projectRel: string) (packageId: string) =
    let version = "0.0.0-t768." + Guid.NewGuid().ToString("N").Substring(0, 8)
    let feed = Path.Combine(scratch, "feed")
    let consumer = Path.Combine(scratch, "consumer")
    Directory.CreateDirectory feed |> ignore
    Directory.CreateDirectory consumer |> ignore

    dotnet (srcRoot ()) [
        "pack"
        Path.Combine(srcRoot (), projectRel)
        "--no-build"
        "--nologo"
        "-c"
        buildConfiguration ()
        "-o"
        feed
        "-p:PackageVersion=" + version
        "-p:SuppressDependenciesWhenPacking=true"
        "-p:IncludeSymbols=false"
    ]

    File.WriteAllText(
        Path.Combine(consumer, "nuget.config"),
        $"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="scratch" value="{feed}" />
  </packageSources>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
</configuration>
"""
    )

    // A C# application consumer (an app, not a library, so the package's
    // runtime assets are copied to its output as they are for a deployed
    // server): it needs no package beyond the one under test (an F#
    // project would also need FSharp.Core from a real feed). The SDK's
    // content-file handling is language-neutral. Isolated from any
    // Directory.Build.* / central-package-management file above the scratch dir.
    File.WriteAllText(
        Path.Combine(consumer, "Consumer.csproj"),
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
    <ImportDirectoryPackagesProps>false</ImportDirectoryPackagesProps>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RestorePackagesPath>{Path.Combine(scratch, "packages")}</RestorePackagesPath>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="{packageId}" Version="{version}" />
  </ItemGroup>
</Project>
"""
    )

    File.WriteAllText(
        Path.Combine(consumer, "Consumer.cs"),
        "namespace Consumer { public static class Program { public static void Main() { } } }\n"
    )

    // Both output shapes a deployment runs from: the build output (`dotnet
    // run`, a dev loop) and the publish output (the deployed server). The
    // consumer workaround this phase retires copied to both, so both are
    // asserted — the migration's "one deletion" is only true if neither
    // regresses.
    let buildOut = Path.Combine(consumer, "out")
    let publishOut = Path.Combine(consumer, "pub")
    dotnet consumer [ "build"; "Consumer.csproj"; "--nologo"; "-c"; "Debug"; "-o"; buildOut ]
    dotnet consumer [ "publish"; "Consumer.csproj"; "--nologo"; "-c"; "Release"; "-o"; publishOut ]
    [ "build", buildOut; "publish", publishOut ]

let private withScratch (f: string -> unit) =
    let scratch =
        Path.Combine(Path.GetTempPath(), "toolup-768-" + Guid.NewGuid().ToString("N").Substring(0, 12))

    Directory.CreateDirectory scratch |> ignore

    try
        f scratch
    finally
        try
            Directory.Delete(scratch, true)
        with _ ->
            ()

let private runBuilder (b: SystemPromptBuilder) : string =
    b (Unchecked.defaultof<PromptContext>) |> Async.RunSynchronously

/// The cookbook sits beside the companion assembly in `dir` under
/// `fileName` — which is exactly the builder's assembly-relative candidate
/// path — and the builder reads it to non-empty guidance with no warning.
let private expectDelivered
    (shape: string, dir: string)
    (assemblyName: string)
    (fileName: string)
    (build: string -> ILogger option -> SystemPromptBuilder)
    =
    Expect.isTrue
        (File.Exists(Path.Combine(dir, assemblyName + ".dll")))
        $"{shape}: the companion assembly reached the consumer's output"

    let cookbook = Path.Combine(dir, fileName)
    Expect.isTrue (File.Exists cookbook) $"{shape}: {fileName} lands beside the assembly"

    let logger = CapturingLogger()
    let prompt = runBuilder (build cookbook (Some(logger :> ILogger)))
    Expect.isNonEmpty prompt $"{shape}: the builder returns non-empty guidance"
    Expect.isEmpty logger.Problems $"{shape}: the builder emits no warning"

let tests =
    testList "Phase 768 — PackageReference delivery of the cookbooks" [
        test "a PackageReference consumer of the Community pack gets its cookbook beside the assembly" {
            withScratch (fun scratch ->
                let outputs =
                    deliverThroughPackageReference
                        scratch
                        (Path.Combine("AICookbooks", "AgChart", "AgChartAICookbook.fsproj"))
                        "ToolUp.AICookbooks.AgChart"

                for output in outputs do
                    expectDelivered
                        output
                        "ToolUp.AICookbooks.AgChart"
                        AgChartAICookbook.CookbookFileName
                        (AgChartAICookbook.buildFromFile AgChartAICookbook.Heading AgChartAICookbook.extractedHeaders))
        }

        test "a PackageReference consumer of the Enterprise pack gets its cookbook beside the assembly" {
            withScratch (fun scratch ->
                let outputs =
                    deliverThroughPackageReference
                        scratch
                        (Path.Combine("AICookbooks", "AgGridEnterprise", "AgGridEnterpriseAICookbook.fsproj"))
                        "ToolUp.AICookbooks.AgGridEnterprise"

                for output in outputs do
                    expectDelivered
                        output
                        "ToolUp.AICookbooks.AgGridEnterprise"
                        AgGridEnterpriseAICookbook.CookbookFileName
                        AgGridEnterpriseAICookbook.buildFromFile)
        }
    ]
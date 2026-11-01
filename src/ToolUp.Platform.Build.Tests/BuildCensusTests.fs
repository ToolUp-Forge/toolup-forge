// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.BuildCensusTests

open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Build

// ─── The build census ─────────────────────────────────────────────────
//
// `Census` reads the performance summary of a graph build into one cost per
// project, joins it with line counts and the reference graph, and writes
// build-census.json. This pack holds the reader against an excerpt of a real
// summary, the join, the rendering, and the `--check` rule — including that
// the committed census names exactly the solution's projects.

/// An excerpt of the `-flp:PerformanceSummary;Verbosity=quiet` log of the
/// census build of this tree (`-graph --no-incremental`), paths shortened.
/// The warning line, the two project sections, the target rows under each
/// project and the following `Target Performance Summary` are kept as MSBuild
/// wrote them, because those are the shapes the reader must step over.
let private fixtureLog =
    String.concat "\n" [
        @"C:\repo\src\A\A.fs(1,1): warning FS0046: The identifier 'include' is reserved for future use by F# [C:\repo\src\A\A.fsproj]"
        ""
        "Project Evaluation Performance Summary:"
        @"      161 ms  C:\repo\src\ToolUp.Platform.Transport\ToolUp.Platform.Transport.fsproj   3 calls"
        @"      290 ms  C:\repo\src\ToolUp.Platform.Core\ToolUp.Platform.Core.fsproj   3 calls"
        @"       79 ms  C:\repo\ToolUp.Forge.sln.metaproj   1 calls"
        ""
        "Project Performance Summary:"
        @"     8741 ms  C:\repo\src\ToolUp.Platform.Transport\ToolUp.Platform.Transport.fsproj 1001 calls"
        "                  0 ms  _IsProjectRestoreSupported                 2 calls"
        "                 21 ms  _GenerateRestoreProjectPathWalk            2 calls"
        "               8700 ms  Rebuild;GetTargetFrameworksWithPlatformForSingleTargetFramework;Clean;Build;GetNativeManifest   1 calls"
        @"    36195 ms  C:\repo\src\ToolUp.Platform.Core\ToolUp.Platform.Core.fsproj 1114 calls"
        "                  1 ms  GetTargetFrameworks                        1 calls"
        @"     2319 ms  C:\repo\src\ToolUp.Sdk\ToolUp.Sdk.fsproj   6 calls"
        @"    11733 ms  C:\repo\ToolUp.Forge.sln   1 calls"
        ""
        "Target Performance Summary:"
        "    3111159 ms  CoreCompile                              234 calls"
    ]

let private root = @"C:\repo"

let private core = "src/ToolUp.Platform.Core/ToolUp.Platform.Core.fsproj"

let private transport =
    "src/ToolUp.Platform.Transport/ToolUp.Platform.Transport.fsproj"

let private build = "src/ToolUp.Platform.Build/ToolUp.Platform.Build.fsproj"

let private solutionOf (projects: string list) =
    projects
    |> List.mapi (fun i p ->
        sprintf
            "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"P%d\", \"%s\", \"{00000000-0000-0000-0000-00000000000%d}\"\nEndProject"
            i
            (p.Replace('/', '\\'))
            i)
    |> String.concat "\n"

let private censusOf (projects: string list) =
    Census.render {
        TreeSha = "0000000000000000000000000000000000000000"
        Machine = "test"
        MeasuredAt = "2026-11-01T00:00:00Z"
        WallSeconds = 1.0
        Command = "dotnet build"
        Projects =
            projects
            |> List.map (fun p -> {
                Project = p
                CompileSeconds = 1.0
                EvaluationSeconds = 0.1
                Lines = 10
                FanIn = 0
                FanOut = 0
            })
    }

[<Tests>]
let tests =
    testList "Build census" [

        testCase "the reader takes each project's own time and its evaluation time"
        <| fun () ->
            let costs = Census.readSummary fixtureLog

            let byName =
                costs
                |> List.map (fun c -> c.Project.Split('\\') |> Array.last, c)
                |> Map.ofList

            Expect.equal
                (byName |> Map.keys |> List.ofSeq |> List.sort)
                [
                    "ToolUp.Platform.Core.fsproj"
                    "ToolUp.Platform.Transport.fsproj"
                    "ToolUp.Sdk.fsproj"
                ]
                "the project rows only: no target row, and no solution or metaproject row"

            Expect.equal byName["ToolUp.Platform.Core.fsproj"].CompileSeconds 36.195 "Core's build seconds"
            Expect.equal byName["ToolUp.Platform.Core.fsproj"].EvaluationSeconds 0.29 "Core's evaluation seconds"
            Expect.equal byName["ToolUp.Platform.Transport.fsproj"].CompileSeconds 8.741 "Transport's build seconds"
            Expect.equal byName["ToolUp.Sdk.fsproj"].EvaluationSeconds 0.0 "no evaluation row reads as 0"

        testCase "text without a summary reads as no projects"
        <| fun () -> Expect.isEmpty (Census.readSummary "Build succeeded.\n    0 Warning(s)\n") "no summary, no costs"

        testCase "the join keeps the solution's projects, counts the graph and sorts by cost"
        <| fun () ->
            let costs = Census.readSummary fixtureLog

            let entries =
                Census.assemble root costs [
                    transport, [], 100
                    core, [ transport; "src/Elsewhere/NotInSolution.fsproj" ], 200
                    build, [ core ], 50
                ]

            Expect.equal (entries |> List.map _.Project) [ core; transport; build ] "most expensive first"

            let row p =
                entries |> List.find (fun e -> e.Project = p)

            Expect.equal
                ((row core).FanIn, (row core).FanOut)
                (1, 1)
                "Core: Build references it; the outside ref is not counted"

            Expect.equal ((row transport).FanIn, (row transport).FanOut) (1, 0) "Transport: a leaf with one dependant"
            Expect.equal (row build).CompileSeconds 0.0 "a project the summary does not name costs 0"
            Expect.equal (row core).Lines 200 "lines come from the graph input"

        testCase "a project file's references and single-file compile items are read"
        <| fun () ->
            let xml =
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="A.fs" />
    <Compile Include="..\Shared\B.fs" Link="B.fs" />
    <Compile Include="Generated\*.fs" />
    <Compile Include="$(SharedDir)C.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core\Core.fsproj" />
  </ItemGroup>
</Project>"""

            Expect.equal (Census.compileItems xml) [ "A.fs"; @"..\Shared\B.fs" ] "no wildcard, no property"
            Expect.equal (Census.references xml) [ @"..\Core\Core.fsproj" ] "the project reference"

        testCase "the rendered census reads back as the same project set"
        <| fun () ->
            let text = censusOf [ core; transport ]
            Expect.equal (Census.projectSet text) (Ok [ core; transport ]) "round trip"
            Expect.stringContains text "\"compileSeconds\": 1" "seconds are written"
            Expect.isFalse (text.Contains "\r") "LF line ends"

        testCase "--check is green when the census names exactly the solution's projects"
        <| fun () ->
            Expect.isEmpty (Census.check (censusOf [ core; transport ]) (solutionOf [ transport; core ])) "same set"

        testCase "--check is red for a project added without a regeneration, and for one removed"
        <| fun () ->
            let added = Census.check (censusOf [ core ]) (solutionOf [ core; transport ])
            Expect.equal added [ sprintf "%s is in the solution but not in build-census.json." transport ] "added"

            let removed = Census.check (censusOf [ core; transport ]) (solutionOf [ core ])

            Expect.equal
                removed
                [
                    sprintf "%s is in build-census.json but no longer in the solution." transport
                ]
                "removed"

        testCase "--check is red when the census does not parse"
        <| fun () ->
            match Census.check "{ not json" (solutionOf [ core ]) with
            | [ finding ] -> Expect.stringContains finding "cannot be read" "names the file as unreadable"
            | other -> failtestf "expected one finding, got %A" other

        testCase "the committed build-census.json names every project in ToolUp.Forge.sln"
        <| fun () ->
            let read name =
                File.ReadAllText(Path.Combine(GateReconcileTests.repoRoot, name))

            Expect.isEmpty
                (Census.check (read Census.fileName) (read "ToolUp.Forge.sln"))
                "regenerate with `dotnet run --project Build.fsproj -- Census`"
    ]
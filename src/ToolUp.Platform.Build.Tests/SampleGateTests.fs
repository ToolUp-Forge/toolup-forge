// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.SampleGateTests

open System
open System.IO
open Expecto
open ToolUp.Forge

// ─── Phase 904 — every sample builds under a gate ────────────────────
//
// The gate's MECHANISM (`dotnet build` / `dotnet fable` per sample) is
// exercised by `VerifySamples` itself; what is proven here is the pure
// half that decides WHAT it builds and whether the count is honest. Each
// property that asserts silence has a twin that asserts the finding, so
// an implementation returning `[]` unconditionally cannot pass.

let private ex project reason : SampleGate.Exclusion = { Project = project; Reason = reason }

let private onDisk = [ "samples/A/A.fsproj"; "samples/B/B.fsproj"; "samples/C/C.fsproj" ]

let private repoRoot () =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failwith "no repository root (ToolUp.Forge.sln) above the test's base directory"
        elif File.Exists(Path.Combine(dir.FullName, "ToolUp.Forge.sln")) then
            dir.FullName
        else
            up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let tests =
    testList "SampleGate" [

        test "every project built and nothing excluded reconciles clean" {
            Expect.isEmpty (SampleGate.reconcile onDisk [] onDisk) "all three on disk, all three built"
        }

        test "a sample on disk that was not built is named (go-red: discovery found fewer)" {
            let findings =
                SampleGate.reconcile onDisk [] [ "samples/A/A.fsproj"; "samples/B/B.fsproj" ]

            Expect.isNonEmpty findings "a missing build must not read as a pass"

            Expect.isTrue
                (findings |> List.exists (fun f -> f.Contains "samples/C/C.fsproj"))
                "the finding names the sample that was skipped"
        }

        test "an exclusion accounts for a sample that was not built" {
            let exclusions = [ ex "samples/C/C.fsproj" "needs a GPU" ]

            Expect.isEmpty
                (SampleGate.reconcile onDisk exclusions [ "samples/A/A.fsproj"; "samples/B/B.fsproj" ])
                "a listed exclusion is a decision, not a gap"
        }

        test "a stale exclusion is a finding (go-red: it names nothing on disk)" {
            let exclusions = [ ex "samples/Gone/Gone.fsproj" "long deleted" ]
            let findings = SampleGate.reconcile onDisk exclusions onDisk

            Expect.isTrue
                (findings |> List.exists (fun f -> f.Contains "samples/Gone/Gone.fsproj"))
                "an exclusion outliving its sample must be removed"
        }

        test "a stale exclusion cannot cancel a skipped project out of the count" {
            // One project skipped AND one stale exclusion: the naive
            // `built + excluded = onDisk` arithmetic balances (2 + 1 = 3).
            let exclusions = [ ex "samples/Gone/Gone.fsproj" "long deleted" ]

            let findings =
                SampleGate.reconcile onDisk exclusions [ "samples/A/A.fsproj"; "samples/B/B.fsproj" ]

            Expect.isTrue
                (findings |> List.exists (fun f -> f.Contains "samples/C/C.fsproj"))
                "the skipped project is still named"
        }

        test "a project built that the directory does not list is a finding" {
            let findings = SampleGate.reconcile onDisk [] (onDisk @ [ "samples/Z/Z.fsproj" ])

            Expect.isNonEmpty findings "the gate built something the enumeration cannot see"
        }

        test "plan splits build from excluded" {
            let p = SampleGate.plan onDisk [ ex "samples/B/B.fsproj" "why" ]

            Expect.equal p.ToBuild [ "samples/A/A.fsproj"; "samples/C/C.fsproj" ] "excluded is not built"
            Expect.isEmpty p.StaleExclusions "the exclusion names a real project"
        }

        test "isFableProject: Fable.Core, or the client tier, both mark a Fable sample" {
            Expect.isTrue
                (SampleGate.isFableProject """<PackageReference Include="Fable.Core" />""")
                "declares Fable.Core"

            Expect.isTrue
                (SampleGate.isFableProject
                    """<ProjectReference Include="..\..\src\ToolUp.Platform.Client\ToolUp.Platform.Client.fsproj" />""")
                "references the client tier"

            Expect.isFalse
                (SampleGate.isFableProject
                    """<ProjectReference Include="..\..\src\ToolUp.Platform.Server\ToolUp.Platform.Server.fsproj" />""")
                "a server-only sample is not Fable-compiled"
        }

        test "discover skips build output and package folders" {
            let root =
                Path.Combine(Path.GetTempPath(), "toolup-sg-" + Guid.NewGuid().ToString "N")

            try
                for rel in
                    [
                        "samples/A/A.fsproj"
                        "samples/A/obj/Copy.fsproj"
                        "samples/A/node_modules/pkg/Pkg.fsproj"
                        "samples/B/src/B.fsproj"
                    ] do
                    let full = Path.Combine(root, rel)
                    Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
                    File.WriteAllText(full, "<Project />")

                Expect.equal
                    (SampleGate.discover root (Path.Combine(root, "samples")))
                    [ "samples/A/A.fsproj"; "samples/B/src/B.fsproj" ]
                    "only real projects, relative and forward-slashed"
            finally
                if Directory.Exists root then
                    Directory.Delete(root, true)
        }

        // The real tree: the count the gate asserts is the count on disk,
        // and FormsAndAI — the sample that was broken — is among them.
        test "the shipped samples directory is discovered and every exclusion names a real project" {
            let root = repoRoot ()
            let found = SampleGate.discover root (Path.Combine(root, "samples"))

            Expect.isGreaterThan found.Length 10 "the samples directory is not empty"

            Expect.contains
                found
                "samples/FormsAndAI/src/FormsAndAI.Server/FormsAndAI.Server.fsproj"
                "the sample this phase repaired is under the gate"

            let p = SampleGate.plan found SampleGate.excluded
            Expect.isEmpty p.StaleExclusions "no stale exclusion in the committed list"

            for e in SampleGate.excluded do
                Expect.isNotEmpty e.Reason "an exclusion carries its reason"
        }
    ]
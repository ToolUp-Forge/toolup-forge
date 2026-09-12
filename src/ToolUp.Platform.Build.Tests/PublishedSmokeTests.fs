// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.PublishedSmokeTests

open System
open System.IO
open Expecto
open ToolUp.Forge

// ─── Phase 184 — the fresh-machine smoke gate's rules, proven ────────
// ─── offline and in both directions ──────────────────────────────────
//
// `VerifyPublishedPackages` only ever runs on a release tag. That is
// right — the version it probes does not exist before the push — and it
// means the gate's own rules would otherwise be exercised for the first
// time on the release they exist to protect, with nobody watching. So
// every rule that can be decided without a network is decided here, in a
// pack that gates every push.
//
// Each property is probed the OTHER way as well. The reason is concrete
// rather than stylistic: the outside-the-repo check, the index-serves
// check and the workflow lint all have a trivially-passing shape (`fun _
// _ -> true`, `fun _ -> []`), and the passing direction alone cannot tell
// that shape from the real one. A gate whose green is unfalsifiable is
// the failure this phase exists to remove, one level up.

let private repoRoot =
    // The test binary runs from bin/<cfg>/<tfm>; the repo root is six
    // levels up (src/ToolUp.Platform.Build.Tests/bin/Debug/net10.0).
    // Located by a marker file rather than by counting, so a
    // configuration or TFM change does not silently point this at the
    // wrong tree — and asserted, because a wrong root would make the
    // workflow-lint tests vacuously green on a file they never read.
    let rec walkUp (dir: DirectoryInfo) =
        if isNull (box dir) then
            None
        elif File.Exists(Path.Combine(dir.FullName, "ToolUp.Forge.sln")) then
            Some dir.FullName
        else
            walkUp dir.Parent

    walkUp (DirectoryInfo AppContext.BaseDirectory)

let private publishWorkflow () =
    match repoRoot with
    | None -> None
    | Some root ->
        let path = Path.Combine(root, ".github", "workflows", "publish-nuget.yml")

        if File.Exists path then
            Some(File.ReadAllText path)
        else
            None

/// A well-formed index body, in the shape nuget.org actually returns.
let private index (versions: string list) =
    versions
    |> List.map (sprintf "    \"%s\"")
    |> String.concat ",\n"
    |> sprintf "{\n  \"versions\": [\n%s\n  ]\n}"

/// What the flat container returns for an id nobody published — the
/// literal shape the `ToolUp.AI` probe met while this phase was written.
let private blobNotFound =
    "<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>BlobNotFound</Code><Message>The specified blob does not exist.\nRequestId:a3525836-b01e-0065-67c3-4213b3000000\nTime:2026-09-12T14:33:30.2472167Z</Message></Error>"

[<Tests>]
let tests =
    testList "PublishedSmoke" [

        // ── The probe closure names only packages this repo publishes ─
        testList "probe closure" [
            test "a closure the release publishes reports nothing" {
                let published = [ "ToolUp.Platform.Core"; "ToolUp.Platform.Server"; "Unrelated.Thing" ]

                Expect.equal
                    (PublishedSmoke.unpublishedProbeIds published [ "ToolUp.Platform.Core" ])
                    []
                    "an id in the published set must be silent"
            }

            test "an id the repo does not publish is NAMED" {
                // The live instance: the shard filed against this phase
                // named `ToolUp.AI`, which no project here produces.
                Expect.equal
                    (PublishedSmoke.unpublishedProbeIds [ "ToolUp.AI.Core"; "ToolUp.AI.Server" ] [ "ToolUp.AI" ])
                    [ "ToolUp.AI" ]
                    "an id outside the published set must be reported, not skipped"
            }

            test "case differs but the id is the same package" {
                Expect.equal
                    (PublishedSmoke.unpublishedProbeIds [ "toolup.platform.core" ] [ "ToolUp.Platform.Core" ])
                    []
                    "NuGet ids are case-insensitive; a case-only difference is not a stranger's package"
            }

            test "the SHIPPED closure is non-empty and covers both tiers" {
                // A closure that silently emptied would make the gate
                // pass by probing nothing — the vacuous-green shape.
                Expect.isNonEmpty PublishedSmoke.dllProbeIds "the DLL tier must probe something"
                Expect.isNonEmpty PublishedSmoke.fableProbeIds "the Fable tier must probe something"

                Expect.equal
                    (List.length PublishedSmoke.probeIds)
                    (List.length PublishedSmoke.dllProbeIds
                     + List.length PublishedSmoke.fableProbeIds)
                    "probeIds is both tiers"
            }

            test "the shipped closure does not name ToolUp.AI" {
                // Pinned rather than left to review: the id is the one a
                // reader restoring the shard's task list would re-add.
                Expect.isFalse
                    (PublishedSmoke.probeIds
                     |> List.exists (fun id -> id.Equals("ToolUp.AI", StringComparison.OrdinalIgnoreCase)))
                    "no project in this repo packs ToolUp.AI; the AI ids are ToolUp.AI.Core / .Server / .Client"
            }

            test "the report names every offender" {
                let message = PublishedSmoke.unpublishedProbeReport [ "ToolUp.AI"; "ToolUp.Nope" ]
                Expect.stringContains message "ToolUp.AI" "the first id"
                Expect.stringContains message "ToolUp.Nope" "the second id"
                Expect.stringContains message "2 declared" "the count"
            }
        ]

        // ── Which version the probe is about ─────────────────────────
        testList "versionUnderTest" [
            let props =
                "<Project><PropertyGroup><Version>0.23.0</Version></PropertyGroup></Project>"

            test "a full tag ref yields the version it names" {
                Expect.equal
                    (PublishedSmoke.versionUnderTest (Some "refs/tags/v0.23.0") props)
                    (Ok "0.23.0")
                    "refs/tags form"
            }

            test "a bare tag yields the same" {
                Expect.equal (PublishedSmoke.versionUnderTest (Some "v0.23.0") props) (Ok "0.23.0") "bare tag form"
            }

            test "a pre-release tag keeps its suffix" {
                Expect.equal
                    (PublishedSmoke.versionUnderTest (Some "refs/tags/v0.23.0-rc.1") props)
                    (Ok "0.23.0-rc.1")
                    "a pre-release publishes under its full version and must be probed under it"
            }

            test "no ref falls back to the declared <Version>" {
                Expect.equal
                    (PublishedSmoke.versionUnderTest None props)
                    (Ok "0.23.0")
                    "the workflow_dispatch path packs <Version>"
            }

            test "the TAG WINS over <Version> when both are present" {
                // The load-bearing direction: the two knobs can disagree
                // (main advances <Version> the moment a release is cut),
                // and probing <Version> on a tag run would aim the gate at
                // a version nobody published.
                Expect.equal
                    (PublishedSmoke.versionUnderTest (Some "refs/tags/v0.22.0") props)
                    (Ok "0.22.0")
                    "the tag is what the publish job just pushed"
            }

            test "a ref that is not a release tag is REFUSED, not guessed" {
                match PublishedSmoke.versionUnderTest (Some "refs/heads/main") props with
                | Ok v -> failtestf "a branch ref must not yield a version; got %s" v
                | Error message -> Expect.stringContains message "refs/heads/main" "the refusal names the ref"
            }

            test "no ref and no <Version> is an error, not an empty string" {
                match PublishedSmoke.versionUnderTest None "<Project />" with
                | Ok v -> failtestf "expected a refusal; got %s" v
                | Error message -> Expect.stringContains message "Version" "the refusal says what is missing"
            }
        ]

        // ── nuget.org index availability ─────────────────────────────
        testList "indexServes" [
            test "a served version is found" {
                Expect.isTrue (PublishedSmoke.indexServes "0.22.0" (index [ "0.21.0"; "0.22.0" ])) "0.22.0 is listed"
            }

            test "an unserved version is NOT found" {
                Expect.isFalse
                    (PublishedSmoke.indexServes "0.23.0" (index [ "0.21.0"; "0.22.0" ]))
                    "the pre-index answer must be false — this is what makes the gate wait instead of failing"
            }

            test "a BlobNotFound body answers false rather than throwing" {
                Expect.isFalse
                    (PublishedSmoke.indexServes "0.22.0" blobNotFound)
                    "an unpublished id is a poll result, not a crash"
            }

            test "a version-shaped run OUTSIDE the versions array is not a match" {
                // The reason this reads the array rather than searching
                // the body: the BlobNotFound document carries a request
                // id and a timestamp, and a substring search over it can
                // match digits and report a package as available that
                // does not exist.
                Expect.isFalse
                    (PublishedSmoke.indexServes "0065" blobNotFound)
                    "a digit run in a request id is not a published version"

                Expect.isFalse
                    (PublishedSmoke.indexServes "9.9.9" "{ \"note\": \"9.9.9\", \"versions\": [ \"1.0.0\" ] }")
                    "only the versions array counts"
            }

            test "an empty or garbage body answers false" {
                Expect.isFalse (PublishedSmoke.indexServes "0.22.0" "") "empty"
                Expect.isFalse (PublishedSmoke.indexServes "0.22.0" "(request failed: timeout)") "a poll error string"
            }

            test "the index url is lowercased" {
                Expect.equal
                    (PublishedSmoke.flatContainerIndexUrl "ToolUp.Platform.Core")
                    "https://api.nuget.org/v3-flatcontainer/toolup.platform.core/index.json"
                    "the v3 flat container's paths are case-sensitive even though ids are not"
            }

            test "the timeout report says WAITED, not MISSING" {
                let message =
                    PublishedSmoke.indexTimeoutReport
                        "ToolUp.Platform.Core"
                        "0.23.0"
                        (TimeSpan.FromMinutes 20.0)
                        blobNotFound

                Expect.stringContains message "did not serve" "it reports an observation"
                Expect.stringContains message "20 minutes" "how long it waited"

                Expect.stringContains
                    message
                    "index is running slow"
                    "the second reading — a push is accepted before it is indexed"
            }

            test "the timeout report truncates a huge body instead of pasting it" {
                let message =
                    PublishedSmoke.indexTimeoutReport "X" "1.0.0" (TimeSpan.FromMinutes 1.0) (String.replicate 5000 "z")

                Expect.isLessThan message.Length 1200 "a failure message must stay readable"
            }
        ]

        // ── The fresh-machine invariant ──────────────────────────────
        testList "isOutsideRepo" [
            let root = Path.Combine(Path.GetTempPath(), "tu-fake-repo")

            test "a sibling temp directory is outside" {
                Expect.isTrue
                    (PublishedSmoke.isOutsideRepo root (Path.Combine(Path.GetTempPath(), "tu-pubsmoke")))
                    "the scaffold path the target uses must be accepted"
            }

            test "a directory UNDER the repo is refused" {
                Expect.isFalse
                    (PublishedSmoke.isOutsideRepo root (Path.Combine(root, "obj", "probe")))
                    "a probe under the checkout resolves through the repo's own nuget.config and would go green over a broken release"
            }

            test "the repo root itself is refused" {
                Expect.isFalse (PublishedSmoke.isOutsideRepo root root) "not merely 'not a strict child'"
            }

            test "a trailing separator does not change the answer" {
                Expect.isFalse
                    (PublishedSmoke.isOutsideRepo
                        (root + string Path.DirectorySeparatorChar)
                        (Path.Combine(root, "probe")))
                    "paths arrive with and without a trailing separator"
            }

            test "a sibling whose name merely PREFIXES the repo's is outside" {
                // The off-by-one a naive StartsWith gets wrong:
                // `tu-fake-repo-probe` is not inside `tu-fake-repo`.
                Expect.isTrue
                    (PublishedSmoke.isOutsideRepo root (root + "-probe"))
                    "a shared name prefix is not containment"
            }

            test "the refusal names both paths" {
                let message = PublishedSmoke.insideRepoReport root (Path.Combine(root, "probe"))
                Expect.stringContains message root "the repo"
                Expect.stringContains message "nuget.config" "why it matters"
            }
        ]

        // ── The generated probe scaffold ─────────────────────────────
        testList "probe scaffold" [
            test "the nuget.config CLEARS inherited sources" {
                // Without <clear /> a machine-level source (a dev box may
                // carry the workspace local feed globally) joins the
                // resolve path and the probe stops being a fresh machine.
                Expect.stringContains PublishedSmoke.nugetConfig "<clear />" "the clear is the gate"

                Expect.stringContains
                    PublishedSmoke.nugetConfig
                    "https://api.nuget.org/v3/index.json"
                    "nuget.org is the only source"
            }

            test "the nuget.config names no other source" {
                let adds =
                    Text.RegularExpressions.Regex.Matches(PublishedSmoke.nugetConfig, "<add key=")

                Expect.equal adds.Count 1 "exactly one source, or a green restore proves nothing about a consumer"
            }

            test "each probe project pins every id at the version under test" {
                let project = PublishedSmoke.dllProbeProject "0.22.0"

                for id in PublishedSmoke.dllProbeIds do
                    Expect.stringContains
                        project
                        (sprintf "Include=\"%s\" Version=\"0.22.0\"" id)
                        ("probe references " + id)
            }

            test "the probe projects opt OUT of central package management" {
                // Stated rather than inherited-by-absence: the probe must
                // not depend on there being no Directory.Packages.props
                // above whatever temp path it lands in.
                for project in
                    [
                        PublishedSmoke.dllProbeProject "1.0.0"
                        PublishedSmoke.fableProbeProject "1.0.0"
                    ] do
                    Expect.stringContains
                        project
                        "<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>"
                        "the probe states its own version contract"
            }

            test "the Fable probe disables nullable reference types" {
                Expect.stringContains
                    (PublishedSmoke.fableProbeProject "1.0.0")
                    "<Nullable>disable</Nullable>"
                    "the Fable ecosystem pre-dates F# 10 nullability; enabling it cascades errors out of libraries the probe does not own"
            }

            test "the probe source opens the declared namespaces and nothing else" {
                let source = PublishedSmoke.probeSource PublishedSmoke.dllProbeNamespaces

                for ns in PublishedSmoke.dllProbeNamespaces do
                    Expect.stringContains source ("open " + ns) ("opens " + ns)

                // No member reference, deliberately: naming a type would
                // pin the gate to one release's public surface and turn a
                // legitimate rename into "your release is unusable".
                Expect.isFalse (source.Contains "()") "the probe calls nothing"
            }

            test "the probe source is a compilable module, not a fragment" {
                let source = PublishedSmoke.probeSource [ "ToolUp.Platform" ]
                Expect.stringContains source "module Probe" "it needs a module declaration to compile"
                Expect.stringContains source "let probed" "and a binding, so the file is not empty"
            }
        ]

        // ── The workflow lint ────────────────────────────────────────
        //
        // The target's own guards protect any run that GOES THROUGH the
        // target. What they cannot protect against is a later edit that
        // bypasses it — inlining the probe as workflow shell steps, which
        // run in the checkout and report green. That edit looks entirely
        // reasonable in a diff, so the workflow is checked for the
        // property the diff would silently drop.
        testList "workflow lint" [
            let wellFormed =
                """
name: publish-nuget
jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - run: dotnet run --project Build.fsproj -- Publish
  published-package-smoke:
    needs: publish
    runs-on: ubuntu-latest
    steps:
      - run: dotnet run --project Build.fsproj -- VerifyPublishedPackages
"""

            test "a well-formed workflow reports nothing" {
                Expect.equal (PublishedSmoke.smokeJobFindings wellFormed) [] "the shipped shape must be silent"
            }

            test "a missing smoke job is reported" {
                let withoutJob =
                    "name: publish-nuget\njobs:\n  publish:\n    runs-on: ubuntu-latest\n"

                let findings = PublishedSmoke.smokeJobFindings withoutJob
                Expect.equal (List.length findings) 1 "one finding"
                Expect.stringContains findings[0] "declares no" "it says the job is absent"
            }

            test "a smoke job without `needs: publish` is reported" {
                let findings =
                    PublishedSmoke.smokeJobFindings (wellFormed.Replace("    needs: publish\n", ""))

                Expect.isTrue
                    (findings |> List.exists (fun f -> f.Contains "needs: publish"))
                    "without it the probe races the push"
            }

            test "`needs: [publish]` list form is accepted" {
                let listForm = wellFormed.Replace("needs: publish", "needs: [publish, other]")
                Expect.equal (PublishedSmoke.smokeJobFindings listForm) [] "both YAML forms are legitimate"
            }

            test "a smoke job that does not call the target is reported" {
                let findings =
                    PublishedSmoke.smokeJobFindings (wellFormed.Replace("VerifyPublishedPackages", "Something-Else"))

                Expect.isTrue
                    (findings |> List.exists (fun f -> f.Contains "VerifyPublishedPackages"))
                    "the gate's rules live in the target"
            }

            test "a smoke job that inlines the probe in the checkout is reported" {
                // The bypass this lint exists for.
                let inlined =
                    wellFormed.Replace(
                        "      - run: dotnet run --project Build.fsproj -- VerifyPublishedPackages",
                        "      - run: dotnet add package ToolUp.Platform.Core --version 0.23.0\n      - run: dotnet build"
                    )

                let findings = PublishedSmoke.smokeJobFindings inlined
                Expect.isNonEmpty findings "an inlined probe must not pass the lint"

                Expect.isTrue
                    (findings |> List.exists (fun f -> f.Contains "repository checkout"))
                    "the finding must say WHY an inlined probe is worthless"
            }

            test "the lint reads the smoke job only, not the whole file" {
                // The publish job legitimately runs `dotnet run -- Publish`
                // and other jobs may restore; a lint that scanned the file
                // would fire on them and be turned off.
                let withRestoreElsewhere =
                    wellFormed.Replace(
                        "      - run: dotnet run --project Build.fsproj -- Publish",
                        "      - run: dotnet restore\n      - run: dotnet run --project Build.fsproj -- Publish"
                    )

                Expect.equal
                    (PublishedSmoke.smokeJobFindings withRestoreElsewhere)
                    []
                    "another job's restore is not this job's business"
            }

            test "jobBlock stops at the next job" {
                match PublishedSmoke.jobBlock "publish" wellFormed with
                | None -> failtest "the publish job must be found"
                | Some block ->
                    let text = String.concat "\n" block
                    Expect.stringContains text "-- Publish" "its own steps"
                    Expect.isFalse (text.Contains "VerifyPublishedPackages") "not the next job's"
            }

            // ── And the SHIPPED workflow, not only fixtures ──────────
            //
            // Every case above is a fixture, and a lint proven only
            // against fixtures says nothing about the file it governs.

            test "the shipped publish-nuget.yml passes its own lint" {
                match publishWorkflow () with
                | None ->
                    failtest
                        "could not read .github/workflows/publish-nuget.yml from the repo root — this test would otherwise be vacuously green"
                | Some text ->
                    match PublishedSmoke.smokeJobFindings text with
                    | [] -> ()
                    | findings -> failtest (PublishedSmoke.smokeJobReport findings)
            }
        ]
    ]
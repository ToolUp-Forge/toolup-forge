// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.ReleaseChannelTests

open System
open System.IO
open Expecto
open ToolUp.Forge
open ToolUp.Forge.SemVerBump
open ToolUp.Forge.ReleaseChannel

// ─── Phase 263 — the release-candidate channel and its soak gate ─────
//
// `VerifyReleaseChannel` runs only on a release and `VerifyRcPromotion`
// only before one, so every rule they apply is decided here, offline, in
// a pack that gates every push — and each is probed in the refusing
// direction as well, because every one of these checks has a trivially
// passing shape (`Ok`, `[]`, `false`) that the passing direction alone
// cannot tell from the real one.

let private repoRoot =
    // Located by marker file rather than by counting levels, for the
    // reason PublishedSmokeTests gives: a wrong root would make the
    // shipped-file tests vacuously green on files they never read.
    let rec walkUp (dir: DirectoryInfo) =
        if isNull (box dir) then
            None
        elif File.Exists(Path.Combine(dir.FullName, "ToolUp.Forge.sln")) then
            Some dir.FullName
        else
            walkUp dir.Parent

    walkUp (DirectoryInfo AppContext.BaseDirectory)

let private readRepoFile (relative: string) =
    match repoRoot with
    | None ->
        failtest "could not locate the repo root (ToolUp.Forge.sln) — this test would otherwise be vacuously green"
    | Some root ->
        let path = Path.Combine(root, relative)

        if File.Exists path then
            File.ReadAllText path
        else
            failtestf "could not read %s from the repo root — this test would otherwise be vacuously green" relative

let private v (ma, mi, pa) = { Major = ma; Minor = mi; Patch = pa }

let private props (version: string) =
    sprintf "<Project>\n  <!-- <Version>9.9.9</Version> in prose -->\n  <Version>%s</Version>\n</Project>" version

let private candidate core n = { Core = v core; Channel = Candidate n }
let private stable core = { Core = v core; Channel = Stable }

let private change (package: string) (cls: SurfaceClass) = {
    Package = package
    Class = cls
    Removed = []
    Added = []
    Withdrawn = false
    Introduced = false
}

let private now = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)

/// An evidence record that passes every mechanical rule — each test below
/// breaks exactly one field, so a finding can only come from that rule.
let private eligible = {
    Version = stable (1, 0, 0)
    Candidate = Some "v1.0.0-rc.2"
    CandidateIsAncestor = true
    Publication = Published(now - TimeSpan.FromDays 20.0)
    SurfaceSinceCandidate = [ change "ToolUp.Platform.Core" Unchanged ]
    Now = now
}

let private rcTags = [ "v0.23.0"; "v1.0.0-rc.1"; "v1.0.0-rc.2" ]

let private leaf (listed: string) (published: string) =
    sprintf
        """{"@id":"https://api.nuget.org/v3/registration5-gz-semver2/toolup.platform.core/1.0.0-rc.1.json","listed":%s,"published":"%s"}"""
        listed
        published

let private expectFinding (findings: string list) (needle: string) =
    Expect.isNonEmpty findings "a finding is expected"

    Expect.isTrue
        (findings |> List.exists (fun f -> f.Contains needle))
        (sprintf "a finding naming `%s`, got: %A" needle findings)

/// The shipped workflow's relevant shape, as a fixture to break.
let private workflow (trigger: string) (publishSteps: string) =
    sprintf
        "name: publish-nuget\n\non:\n%s\n\npermissions:\n  contents: read\n\njobs:\n  publish:\n    runs-on: ubuntu-latest\n    steps:\n%s\n\n  published-package-smoke:\n    needs: publish\n    steps:\n      - run: dotnet run --project Build.fsproj -- VerifyPublishedPackages\n"
        trigger
        publishSteps

let private tagTrigger =
    "  push:\n    tags:\n      - \"v*.*.*\"\n  workflow_dispatch:"

let private goodSteps =
    "      - run: dotnet run --project Build.fsproj -- VerifyReleaseChannel\n      - uses: NuGet/login@v1\n      - run: dotnet run --project Build.fsproj -- Publish"

let tests =
    testList "ReleaseChannel" [
        // ── tag shapes ──
        testList "parseTag" [
            testCase "a stable tag and a candidate tag parse, refs/tags/ stripped" (fun _ ->
                Expect.equal (parseTag "v1.0.0") (Ok(stable (1, 0, 0))) "stable"
                Expect.equal (parseTag "refs/tags/v1.0.0-rc.3") (Ok(candidate (1, 0, 0) 3)) "candidate from GITHUB_REF"
                Expect.equal (parseTag "v0.23.0-rc.12") (Ok(candidate (0, 23, 0) 12)) "multi-digit candidate")

            testCase "every other shape the v*.*.* trigger admits is refused" (fun _ ->
                for bad in
                    [
                        "v1.0.0-rc1"
                        "v1.0.0-rc.0"
                        "v1.0.0-rc.01"
                        "v1.0.0-beta.1"
                        "v1.0.0-rc.1+sha"
                        "v1.0.0+sha"
                        "v01.0.0"
                        "1.0.0"
                        "v1.0"
                        "nightly"
                    ] do
                    Expect.isError (parseTag bad) (sprintf "`%s` must not be accepted" bad))

            testCase "the refusal names the tag and the remedy" (fun _ ->
                match parseTag "v1.0.0-beta.1" with
                | Error e ->
                    Expect.stringContains e "v1.0.0-beta.1" "names the tag"
                    Expect.stringContains e "git push --delete origin" "names the remedy"
                | Ok _ -> failtest "must refuse")

            testCase "render and tagName round-trip" (fun _ ->
                Expect.equal (ReleaseVersion.render (candidate (1, 0, 0) 2)) "1.0.0-rc.2" "candidate"
                Expect.equal (ReleaseVersion.tagName (stable (1, 0, 0))) "v1.0.0" "stable tag"
                Expect.equal (ReleaseVersion.prereleaseLabel (candidate (1, 0, 0) 2)) (Some "rc.2") "label"
                Expect.equal (ReleaseVersion.prereleaseLabel (stable (1, 0, 0))) None "no label on stable")
        ]

        // ── what a run releases ──
        testList "resolve" [
            testCase "no tag releases the declared version on the stable channel" (fun _ ->
                Expect.equal (resolve (props "1.0.0") None) (Ok(stable (1, 0, 0))) "branch dispatch")

            testCase "a candidate tag of the declared version releases the candidate" (fun _ ->
                Expect.equal
                    (resolve (props "1.0.0") (Some "refs/tags/v1.0.0-rc.1"))
                    (Ok(candidate (1, 0, 0) 1))
                    "candidate")

            testCase "a tag that disagrees with the tree is refused, naming both" (fun _ ->
                // The defect this closes: before Phase 263 this tag
                // published the TREE's version, whatever the tag said.
                match resolve (props "0.23.0") (Some "refs/tags/v1.0.0-rc.1") with
                | Error e ->
                    Expect.stringContains e "1.0.0" "the tag's version"
                    Expect.stringContains e "0.23.0" "the tree's version"
                | Ok v -> failtestf "must refuse, got %A" v)

            testCase "a prerelease label written into the tree is refused" (fun _ ->
                for declared in [ "1.0.0-rc.1"; "1.0.0-alpha"; "1.0.0+build" ] do
                    match resolve (props declared) None with
                    | Error e -> Expect.stringContains e "belongs on the release TAG" "names where the label goes"
                    | Ok v -> failtestf "`%s` must be refused, got %A" declared v)

            testCase "a malformed tag is refused even when its core matches" (fun _ ->
                Expect.isError (resolve (props "1.0.0") (Some "v1.0.0-rc1")) "rc1 is not rc.1")
        ]

        // ── the tag namespace ──
        testList "candidates" [
            testCase "candidates sort numerically, not lexically" (fun _ ->
                let tags = [ "v1.0.0-rc.10"; "v1.0.0-rc.2"; "v1.0.0-rc.1"; "v0.23.0-rc.9"; "v1.0.0" ]

                Expect.equal
                    (candidatesOf (v (1, 0, 0)) tags |> List.map snd)
                    [ 1; 2; 10 ]
                    "rc.10 is the newest, and other versions' candidates are not counted"

                Expect.equal (nextCandidateTag (v (1, 0, 0)) tags) "v1.0.0-rc.11" "next after rc.10"
                Expect.equal (nextCandidateTag (v (2, 0, 0)) tags) "v2.0.0-rc.1" "first candidate")

            testCase "a clean candidate has no findings" (fun _ ->
                Expect.isEmpty (candidateFindings (candidate (1, 0, 0) 3) rcTags) "rc.3 after rc.2"
                Expect.isEmpty (candidateFindings (stable (1, 0, 0)) rcTags) "stable runs have none")

            testCase "a candidate of an already-released version is refused" (fun _ ->
                expectFinding (candidateFindings (candidate (0, 23, 0) 1) rcTags) "already released")

            testCase "a candidate behind an existing candidate is refused" (fun _ ->
                expectFinding (candidateFindings (candidate (1, 0, 0) 1) rcTags) "v1.0.0-rc.2")
        ]

        // ── when the gate applies ──
        testList "promotionRequired" [
            testCase "the cases" (fun _ ->
                Expect.isTrue (promotionRequired (stable (1, 0, 0)) []) "1.0.0 must come through a candidate"
                Expect.isTrue (promotionRequired (stable (2, 0, 0)) []) "so must every later major"

                Expect.isTrue
                    (promotionRequired (stable (0, 23, 0)) [ "v0.23.0-rc.1" ])
                    "a version with a candidate answers to it"

                Expect.isFalse (promotionRequired (stable (0, 23, 0)) rcTags) "a 0.x with no candidate is unchanged"

                Expect.isFalse
                    (promotionRequired (stable (1, 1, 0)) rcTags)
                    "a 1.x minor with no candidate is unchanged"

                Expect.isFalse (promotionRequired (candidate (1, 0, 0) 3) rcTags) "a candidate is not a promotion")
        ]

        // ── the soak gate ──
        testList "promotionFindings" [
            testCase "the eligible fixture passes" (fun _ ->
                Expect.isEmpty (promotionFindings soakWindow rcTags eligible) "every rule met")

            testCase "no candidate: refused, naming the first candidate tag" (fun _ ->
                expectFinding (promotionFindings soakWindow [] { eligible with Candidate = None }) "v1.0.0-rc.1")

            testCase "inside the window: refused, naming when it becomes eligible" (fun _ ->
                let young = {
                    eligible with
                        Publication = Published(now - TimeSpan.FromDays 13.0)
                }

                let findings = promotionFindings soakWindow rcTags young
                expectFinding findings "promotion-eligible from"
                expectFinding findings "v1.0.0-rc.2")

            testCase "exactly at the window: eligible" (fun _ ->
                let boundary = {
                    eligible with
                        Publication = Published(now - soakWindow)
                }

                Expect.isEmpty (promotionFindings soakWindow rcTags boundary) "≥ the window, not >")

            testCase "an unlisted, unpublished or unreadable candidate has not soaked" (fun _ ->
                for publication, needle in
                    [
                        Unlisted, "unlisted"
                        NotPublished, "does not serve"
                        Unreadable "timeout", "cannot be established"
                    ] do
                    expectFinding
                        (promotionFindings soakWindow rcTags {
                            eligible with
                                Publication = publication
                        })
                        needle)

            testCase "a commit that does not descend from the candidate is refused" (fun _ ->
                expectFinding
                    (promotionFindings soakWindow rcTags {
                        eligible with
                            CandidateIsAncestor = false
                    })
                    "does not descend")

            testCase "ANY surface movement since the candidate re-rolls it, not only a break" (fun _ ->
                for cls in [ Additive; Breaking ] do
                    let moved = {
                        eligible with
                            SurfaceSinceCandidate = [
                                change "ToolUp.Platform.Core" Unchanged
                                change "ToolUp.AI.Core" cls
                            ]
                    }

                    let findings = promotionFindings soakWindow rcTags moved
                    expectFinding findings "ToolUp.AI.Core"
                    expectFinding findings "v1.0.0-rc.3")

            testCase "the soak window is two weeks" (fun _ ->
                // A threshold is a reviewed decision; a change to it should
                // be a deliberate edit to this line as well.
                Expect.equal soakWindow (TimeSpan.FromDays 14.0) "14 days")

            testCase "the scorecard half: all-green passes, anything else is refused" (fun _ ->
                Expect.isEmpty (scorecardFindings true []) "ready"
                expectFinding (scorecardFindings false [ "adoption-pending" ]) "adoption-pending")
        ]

        // ── nuget.org's record ──
        testList "publication" [
            testCase "a listed leaf reads its published time" (fun _ ->
                match publicationOf (leaf "true" "2026-08-27T05:01:44.27+00:00") with
                | Published at -> Expect.equal at (DateTimeOffset(2026, 8, 27, 5, 1, 44, 270, TimeSpan.Zero)) "parsed"
                | other -> failtestf "expected Published, got %A" other)

            testCase "an unlisted leaf, a leaf with no time, and non-JSON are not Published" (fun _ ->
                Expect.equal (publicationOf (leaf "false" "1900-01-01T00:00:00+00:00")) Unlisted "unlisted"

                match publicationOf """{"listed":true}""" with
                | Unreadable _ -> ()
                | other -> failtestf "no `published`: expected Unreadable, got %A" other

                match publicationOf "<html>" with
                | Unreadable _ -> ()
                | other -> failtestf "not JSON: expected Unreadable, got %A" other)

            testCase "the leaf URL is the SemVer-2 hive, lowercased" (fun _ ->
                Expect.equal
                    (registrationLeafUrl "ToolUp.Platform.Core" "1.0.0-RC.1")
                    "https://api.nuget.org/v3/registration5-gz-semver2/toolup.platform.core/1.0.0-rc.1.json"
                    "url")

            testCase "the slowest package sets the soak start; any gap decides the whole" (fun _ ->
                let early = DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
                let late = early.AddHours 2.0

                Expect.equal (combinePublications [ Published early; Published late ]) (Published late) "latest"

                Expect.equal
                    (combinePublications [ Published early; NotPublished; Unlisted ])
                    NotPublished
                    "a missing package decides"

                Expect.equal (combinePublications [ Published early; Unlisted ]) Unlisted "an unlisted one decides"

                match combinePublications [] with
                | Unreadable _ -> ()
                | other -> failtestf "nothing read: expected Unreadable, got %A" other)
        ]

        // ── SemVerBump's two prerelease-aware rules ──
        testList "SemVerBump prerelease" [
            testCase "isPrerelease tells a label from build metadata" (fun _ ->
                Expect.isTrue (isPrerelease "v1.0.0-rc.1") "rc"
                Expect.isTrue (isPrerelease "1.0.0-rc.1+sha") "rc with metadata"
                Expect.isFalse (isPrerelease "v1.0.0") "stable"
                Expect.isFalse (isPrerelease "v1.0.0+sha") "metadata only")

            testCase "a stable release outranks its own candidates as the release point" (fun _ ->
                // `git tag --list` sorts `v1.0.0` BEFORE `v1.0.0-rc.1`, and
                // all three parse to 1.0.0 — without the tie-break the
                // bump for 1.0.1 would be measured from a candidate.
                match releasePoint (v (1, 0, 1)) [ "v1.0.0"; "v1.0.0-rc.1"; "v1.0.0-rc.2"; "v0.23.0" ] with
                | Some(tag, _) -> Expect.equal tag "v1.0.0" "the stable release"
                | None -> failtest "a release point must be found")

            testCase "a candidate is not a release point for its own promotion" (fun _ ->
                match releasePoint (v (1, 0, 0)) [ "v0.23.0"; "v1.0.0-rc.1" ] with
                | Some(tag, _) -> Expect.equal tag "v0.23.0" "the last stable below 1.0.0"
                | None -> failtest "a release point must be found")

            testCase "declaredVersionTextIn keeps the suffix that declaredVersionIn discards" (fun _ ->
                Expect.equal (declaredVersionTextIn (props "1.0.0-rc.1")) (Ok "1.0.0-rc.1") "raw"
                Expect.equal (declaredVersionIn (props "1.0.0-rc.1")) (Ok(v (1, 0, 0))) "parsed")
        ]

        // ── the workflow, linted ──
        testList "publishWorkflowFindings" [
            testCase "a well-formed fixture passes" (fun _ ->
                Expect.isEmpty (publishWorkflowFindings (workflow tagTrigger goodSteps)) "fixture")

            testCase "a branch-push trigger is refused — it would make a push to main a release" (fun _ ->
                let trigger =
                    "  push:\n    branches:\n      - main\n    tags:\n      - \"v*.*.*\"\n  workflow_dispatch:"

                expectFinding (publishWorkflowFindings (workflow trigger goodSteps)) "branches")

            testCase "pull_request, schedule, workflow_run and release triggers are refused" (fun _ ->
                for extra in [ "pull_request"; "schedule"; "workflow_run"; "release" ] do
                    let trigger = tagTrigger + sprintf "\n  %s:" extra
                    expectFinding (publishWorkflowFindings (workflow trigger goodSteps)) extra)

            testCase "a trigger with no tags filter is refused" (fun _ ->
                expectFinding (publishWorkflowFindings (workflow "  workflow_dispatch:" goodSteps)) "tags:")

            testCase "a publish job that never runs the channel target is refused" (fun _ ->
                let steps =
                    "      - uses: NuGet/login@v1\n      - run: dotnet run --project Build.fsproj -- Publish"

                expectFinding (publishWorkflowFindings (workflow tagTrigger steps)) "STABLE")

            testCase "the channel target after the login step is refused" (fun _ ->
                let steps =
                    "      - uses: NuGet/login@v1\n      - run: dotnet run --project Build.fsproj -- VerifyReleaseChannel\n      - run: dotnet run --project Build.fsproj -- Publish"

                expectFinding (publishWorkflowFindings (workflow tagTrigger steps)) "before a credential")

            // ── And the SHIPPED files, not only fixtures ──

            testCase "the shipped publish-nuget.yml passes its own lint" (fun _ ->
                match publishWorkflowFindings (readRepoFile ".github/workflows/publish-nuget.yml") with
                | [] -> ()
                | findings -> failtest (findingsReport "publish-nuget.yml" findings))

            testCase "the shipped Directory.Build.targets reads exactly the variables the target exports" (fun _ ->
                let targets = readRepoFile "Directory.Build.targets"

                for name in [ coreEnvVar; prereleaseEnvVar ] do
                    Expect.stringContains targets (sprintf "$(%s)" name) (sprintf "reads %s" name)

                Expect.stringContains
                    targets
                    "<IsPackable>false</IsPackable>"
                    "an RC run skips the independently versioned packages")
        ]
    ]
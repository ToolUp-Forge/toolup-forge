// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 904 — every sample builds under a gate.
///
/// The samples under `samples/` are not in `ToolUp.Forge.sln`, so no gate
/// compiled them: `FormsAndAI.Server` sat broken (FS0764, a record field
/// added in Phase 6g.F) for the whole span between that phase and this one
/// and nothing went red. A sample is the first code a new consumer copies,
/// so a sample that does not build is a broken front door.
///
/// This module is the PURE half of the `VerifySamples` target (and of the
/// `Samples` leg `VerifyAll` composes): finding the sample projects on
/// disk, deciding which of them Fable must also transpile, and reconciling
/// what the gate built against what is on disk. It is FAKE-free and
/// source-linked into `ToolUp.Platform.Build.Tests`, so the target and its
/// go-red proofs run one implementation — the same arrangement as
/// `TemplateGatePackages.fs` and `VerifyGate.fs`.
///
/// **The count is the point.** A discovery that quietly found fewer
/// projects (a moved directory, a glob that stopped matching) would build
/// fewer samples and still exit 0. So the gate compares what it built with
/// what an independent enumeration of the directory says is there, and a
/// sample that is deliberately not built must be named in `excluded` with
/// its reason — never simply absent.
module ToolUp.Forge.SampleGate

open System
open System.IO

/// A sample project that is on disk but deliberately NOT built by the
/// gate, with the reason. Empty today: every sample builds. An entry here
/// is a decision recorded where a reader of a green run will look, not a
/// silent omission.
type Exclusion = {
    /// Path of the project, relative to the repository root, forward slashes.
    Project: string
    /// Why the gate does not build it.
    Reason: string
}

/// The deliberate exclusions. See `Exclusion`.
let excluded: Exclusion list = []

/// Directory names never descended into when enumerating the samples: build
/// output and package folders a previous build left behind, which can hold
/// copies of a project file that are not samples.
let private skippedDirectories =
    set [ "obj"; "bin"; "node_modules"; "output"; ".fable"; ".git" ]

/// Every `*.fsproj` under `samplesDir`, as a path relative to
/// `repoRoot` with forward slashes, sorted. `samplesDir` is absolute.
let discover (repoRoot: string) (samplesDir: string) : string list =
    let rec walk (dir: string) : string seq = seq {
        for file in Directory.EnumerateFiles(dir, "*.fsproj") do
            yield file

        for sub in Directory.EnumerateDirectories dir do
            if not (skippedDirectories.Contains(Path.GetFileName sub)) then
                yield! walk sub
    }

    walk samplesDir
    |> Seq.map (fun p -> Path.GetRelativePath(repoRoot, p).Replace('\\', '/'))
    |> Seq.sort
    |> Seq.toList

/// True when the project is compiled by Fable for the browser, so a plain
/// `dotnet build` is not enough: it declares `Fable.Core` directly, or it
/// references the client tier (`ToolUp.Platform.Client`), which is only
/// ever consumed by Fable. Pure over the project file's text.
let isFableProject (fsprojText: string) : bool =
    fsprojText.Contains("Include=\"Fable.Core\"", StringComparison.Ordinal)
    || fsprojText.Contains("ToolUp.Platform.Client.fsproj", StringComparison.Ordinal)

/// What the gate should do with the projects on disk.
type Plan = {
    /// Projects the gate builds, in order.
    ToBuild: string list
    /// Exclusions whose project is NOT on disk — a stale entry, which is a
    /// finding: an exclusion outliving its sample would silently license
    /// the next sample that reuses the path.
    StaleExclusions: string list
}

/// Split the projects found on disk into those the gate builds and those
/// it deliberately does not, and report exclusions that name nothing.
let plan (onDisk: string list) (exclusions: Exclusion list) : Plan =
    let excludedSet = exclusions |> List.map _.Project |> Set.ofList
    let diskSet = Set.ofList onDisk

    {
        ToBuild = onDisk |> List.filter (fun p -> not (excludedSet.Contains p))
        StaleExclusions =
            exclusions
            |> List.map _.Project
            |> List.filter (fun p -> not (diskSet.Contains p))
    }

/// The findings of comparing the run with the directory: an empty list is
/// a pass. `built` is what the gate actually built (a leg that failed
/// still counts as attempted, so a red build is reported by the build, not
/// double-reported here); `onDisk` is an independent enumeration.
///
/// Three things are checked: the counts add up (built + excluded = on
/// disk), no exclusion is stale, and nothing on disk was neither built nor
/// excluded. The last is what a count alone cannot say when a stale
/// exclusion and a skipped project cancel each other out.
let reconcile (onDisk: string list) (exclusions: Exclusion list) (built: string list) : string list = [
    let p = plan onDisk exclusions

    for stale in p.StaleExclusions do
        yield sprintf "exclusion `%s` names a project that is not on disk — remove it from `SampleGate.excluded`." stale

    for missing in p.ToBuild |> List.filter (fun x -> not (List.contains x built)) do
        yield sprintf "sample `%s` is on disk but was not built and is not in `SampleGate.excluded`." missing

    for phantom in built |> List.filter (fun x -> not (List.contains x onDisk)) do
        yield sprintf "the gate built `%s`, which the directory enumeration does not list." phantom

    let excludedOnDisk = exclusions.Length - p.StaleExclusions.Length

    if built.Length + excludedOnDisk <> onDisk.Length then
        yield
            sprintf
                "built %d + excluded %d <> %d project(s) on disk — the gate's coverage of `samples/` has drifted."
                built.Length
                excludedOnDisk
                onDisk.Length
]
// ─── Phase 946 — the samples that are meant to RUN are run ───────────────
//
// Building a sample proves it compiles; it does not prove it works. The
// HelloWorld-AOT sample is a program whose exit code IS its verdict (every
// pinned remoting fixture through generated code only), and it sat exiting
// 1 while the build gate stayed green: two corpus fixtures had no echo
// method in its contract. A sample whose purpose is a run is listed here,
// and the gate runs it after the build and fails on a non-zero exit.

/// A sample project the gate RUNS after building it, with the reason it
/// is meant to run. The run is `dotnet run --no-build`, so it is the
/// build the gate just made that runs.
type Runnable = {
    /// Path of the project, relative to the repository root, forward slashes.
    Project: string
    /// Arguments passed to the program, after `--`.
    Arguments: string list
    /// Why this sample is meant to run under the gate.
    Reason: string
}

/// The samples the gate runs. See `Runnable`.
let runnable: Runnable list = [
    {
        Project = "samples/HelloWorld-AOT/HelloWorld.AOT/HelloWorld.AOT.fsproj"
        Arguments = []
        Reason =
            "its exit code is the proof: every pinned remoting-corpus fixture through generated code, and the recorded refusal set exact"
    }
]

/// The `dotnet` arguments that run one runnable sample from the repository
/// root, against the build the gate already made.
let runArguments (r: Runnable) : string list =
    [ "run"; "--project"; r.Project; "--no-build"; "-v:q" ]
    @ (if List.isEmpty r.Arguments then [] else "--" :: r.Arguments)

/// The findings of checking the run list against the build plan: an empty
/// list is a pass. A runnable the gate does not build (not on disk, or
/// excluded) could only be run against a stale or absent build, so it is
/// named rather than run; a runnable with no reason is a silent decision.
let runFindings (p: Plan) (runnables: Runnable list) : string list = [
    for r in runnables do
        if not (List.contains r.Project p.ToBuild) then
            yield
                sprintf
                    "runnable sample `%s` is not built by the gate (not on disk, or excluded) — remove it from `SampleGate.runnable` or build it."
                    r.Project

        if String.IsNullOrWhiteSpace r.Reason then
            yield sprintf "runnable sample `%s` carries no reason." r.Project
]
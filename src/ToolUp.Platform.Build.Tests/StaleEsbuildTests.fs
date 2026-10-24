// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.StaleEsbuildTests

open System
open System.Diagnostics
open System.IO
open Expecto
open ToolUp.Platform.Build

// ─── Phase 1108 — a build stops only its own project's esbuild ───
//
// Real processes: each case copies a long-running system binary to a file
// named `esbuild` in a scratch directory and starts it, so the process name is
// `esbuild` and its main module is the copy. Before the phase the target
// stopped every `esbuild` by name; the decoy case is the one that proves the
// scope, and its twin proves the lock fix survives.

let private esbuildName =
    if OperatingSystem.IsWindows() then
        "esbuild.exe"
    else
        "esbuild"

/// A long-running program to copy, and the arguments that keep it alive.
///
/// The copy runs under the name `esbuild`, so it must be a binary that does
/// not read its own name. `sleep` is not one everywhere: on a system whose
/// coreutils is a multi-call binary (uutils, the default on recent Ubuntu
/// and so on the CI runner) the copy dispatches on `argv[0]`, finds no
/// applet called `esbuild`, prints `coreutils: unknown program 'esbuild'`
/// and exits at once — the decoy is gone before the stop runs, and both
/// cases fail. `bash` only consults its name to decide whether it is `sh`,
/// so a copy of it stays alive for the `sleep` child it runs, with the copy
/// as the process's main module and `esbuild` as its name, which is what
/// `StaleEsbuild.stopUnder` reads.
let private longRunner () =
    if OperatingSystem.IsWindows() then
        Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1"
    else
        let shell = [ "/bin/bash"; "/usr/bin/bash"; "/bin/sh" ] |> List.find File.Exists

        shell, "-c \"sleep 120\""

/// Start a fake `esbuild` whose executable lives in `dir`.
let private startEsbuildIn (dir: string) =
    Directory.CreateDirectory dir |> ignore
    let source, args = longRunner ()
    let target = Path.Combine(dir, esbuildName)
    File.Copy(source, target, true)

    if not (OperatingSystem.IsWindows()) then
        File.SetUnixFileMode(target, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    let psi = ProcessStartInfo(target, args)
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.CreateNoWindow <- true
    Process.Start psi

let private waitGone (p: Process) = p.WaitForExit 10000

let private withScratch (f: string -> unit) =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-1108-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory root |> ignore

    try
        f root
    finally
        try
            Directory.Delete(root, true)
        with _ ->
            ()

/// Run `body` with a project's node_modules and a decoy directory, killing
/// whatever the case left running afterwards.
let private withProcesses (body: string -> Process -> Process -> unit) =
    withScratch (fun root ->
        let nodeModules = Path.Combine(root, "client", "node_modules")
        let inside = startEsbuildIn (Path.Combine(nodeModules, "@esbuild", "bin"))

        let outside =
            startEsbuildIn (Path.Combine(root, "other-project", "node_modules", "@esbuild", "bin"))

        try
            body nodeModules inside outside
        finally
            for p in [ inside; outside ] do
                try
                    if not p.HasExited then
                        p.Kill()
                with _ ->
                    ()

                p.Dispose())

[<Tests>]
let tests =
    testList "Phase 1108 - KillStaleProcesses is scoped to the project" [
        testList "isUnder" [
            test "a file below the root is under it" {
                Expect.isTrue (StaleEsbuild.isUnder "/a/b/node_modules" "/a/b/node_modules/x/esbuild") "below"
            }

            test "a sibling whose name merely starts with the root is not under it" {
                Expect.isFalse
                    (StaleEsbuild.isUnder "/a/b/node_modules" "/a/b/node_modules-other/esbuild")
                    "a prefix of the name is not a prefix of the path"
            }

            test "the root itself is not under itself" {
                Expect.isFalse (StaleEsbuild.isUnder "/a/b/node_modules" "/a/b/node_modules") "not strictly under"
            }

            test "a trailing separator on the root changes nothing" {
                Expect.isTrue (StaleEsbuild.isUnder "/a/b/node_modules/" "/a/b/node_modules/e") "normalised"
            }
        ]

        testList "the stop" [
            test "a decoy esbuild outside the project survives" {
                withProcesses (fun nodeModules inside outside ->
                    let logs = Collections.Generic.List<string>()
                    StaleEsbuild.stopUnder nodeModules logs.Add |> ignore
                    waitGone inside |> ignore
                    Expect.isFalse outside.HasExited "another project's esbuild is left running")
            }

            test "an esbuild under the project's node_modules is stopped" {
                withProcesses (fun nodeModules inside _ ->
                    let logs = Collections.Generic.List<string>()
                    let stopped = StaleEsbuild.stopUnder nodeModules logs.Add
                    Expect.isTrue (waitGone inside) "the project's own esbuild exits"
                    Expect.isGreaterThanOrEqual stopped 1 "and is counted"
                    Expect.isTrue (logs |> Seq.exists (fun l -> l.Contains "stopped esbuild")) "and is reported")
            }
        ]
    ]
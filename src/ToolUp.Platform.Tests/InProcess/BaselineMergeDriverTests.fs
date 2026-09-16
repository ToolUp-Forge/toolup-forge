// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.BaselineMergeDriverTests

// ─── Phase 805 — the generated baselines' three-way merge driver ─────
//
// `dev-scripts/merge-baselines.ps1` computes what a regeneration would
// produce for the case git's line merge cannot: two branches that each
// ADDED to the same sorted region of `api-baselines/<Assembly>.approved.txt`
// or `api-baselines/doc-coverage.approved.txt`.
//
// The driver is PowerShell and the generator is F#, so they cannot share a
// function call. They are bound here instead, in two links that together
// make drift impossible to land silently:
//
//   1. `sortSurfaceBody` (Contracts/PublicApiApproval.fs) is asserted to be
//      the IDENTITY on every committed baseline's body. That pins the F#
//      specification of the order to what `renderSurfaceDetail` really
//      emits — 167 files' worth of evidence, not a restatement.
//   2. the driver's output is asserted EQUAL to what that same function
//      produces. That pins the PowerShell to the specification.
//
// Break either end and a case here goes red. A `.ps1` rewritten to sort
// culture-aware, or a generator that changed its own order without moving
// `sortSurfaceBody`, both land in link 1 or link 2 rather than in a
// baseline diff six merges later.
//
// The conflict cases are the more important half. A merge driver that
// resolves a REMOVAL, or reconciles two coverage deltas whose arithmetic
// cannot both be right, is worse than no driver at all: it presents a
// judgement as a merge. Those cases assert exit 1 AND the standard markers
// in the output, because either alone can be produced by accident.

open System
open System.Diagnostics
open System.IO
open Expecto
open ToolUp.Platform.Tests.Contracts.PublicApiApproval

let private root = repoRoot ()

let private driverPath = Path.Combine(root, "dev-scripts", "merge-baselines.ps1")

/// `pwsh` on PATH, or `None`. The driver runs inside `git merge` under
/// whatever shell the machine has, so the tests invoke it the same way
/// rather than reimplementing it.
let private pwsh =
    lazy
        (let names =
            if OperatingSystem.IsWindows() then
                [ "pwsh.exe"; "powershell.exe" ]
            else
                [ "pwsh" ]

         let dirs =
             match Environment.GetEnvironmentVariable "PATH" with
             | null -> [||]
             | path -> path.Split(Path.PathSeparator)

         names
         |> List.collect (fun name ->
             dirs
             |> Array.toList
             |> List.map (fun dir -> Path.Combine(dir, name))
             |> List.filter File.Exists)
         |> List.tryHead)

/// One driver invocation: its exit code, the merged bytes it left in `%A`,
/// and whatever it said on stderr.
type private Run = {
    Exit: int
    Merged: string
    Stderr: string
}

let private run (exe: string) (args: string list) (workingDir: string) =
    let psi =
        ProcessStartInfo(exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)

    for a in args do
        psi.ArgumentList.Add a

    psi.WorkingDirectory <- workingDir

    use p = Process.Start psi
    let stdout = p.StandardOutput.ReadToEnd()
    let stderr = p.StandardError.ReadToEnd()
    p.WaitForExit()
    p.ExitCode, stdout, stderr

/// Write the three sides to a scratch directory, invoke the driver, read
/// back what it left in `%A`.
let private mergeWith (mode: string) (baseText: string) (oursText: string) (theirsText: string) : Run =
    let shell = pwsh.Value |> Option.defaultWith (fun () -> failwith "pwsh not on PATH")

    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-805-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    try
        let write name (text: string) =
            let p = Path.Combine(dir, name)
            File.WriteAllText(p, text)
            p

        let b = write "base.txt" baseText
        let o = write "ours.txt" oursText
        let t = write "theirs.txt" theirsText

        let exit, _, stderr =
            run
                shell
                [
                    "-NoProfile"
                    "-File"
                    driverPath
                    "-Mode"
                    mode
                    b
                    o
                    t
                    "api-baselines/Fixture.approved.txt"
                ]
                dir

        {
            Exit = exit
            Merged = File.ReadAllText(o).Replace("\r\n", "\n")
            Stderr = stderr
        }
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private conflicted (r: Run) =
    r.Merged.Contains "<<<<<<< "
    && r.Merged.Contains "======="
    && r.Merged.Contains ">>>>>>> "

// ─── Fixtures ────────────────────────────────────────────────────────

let private memberHeader =
    "# Public API baseline — Fixture\n# Generated by Phase 175 PublicApiApproval — regenerate with TOOLUP_APPROVE_API=1.\n"

let private membersFixture (body: string list) =
    memberHeader + (body |> String.concat "\n") + "\n"

let private baseBody = [
    "Demo.T (class)"
    "Demo.T.Alpha() : System.Int32"
    "Demo.U (class)"
    "Demo.U.Zed : System.String { get }"
]

let private coverageHeader = "# Public XML-doc coverage floor — Phase 261.\n"

let private coverageFixture (rows: string list) =
    coverageHeader + (rows |> String.concat "\n") + "\n"

// ─── Link 1: the spec IS the generator's order ───────────────────────

let private committedBaselines =
    lazy
        (if Directory.Exists(baselineDir root) then
             Directory.GetFiles(baselineDir root, "*.approved.txt")
             |> Array.filter (fun f -> Path.GetFileName f <> "doc-coverage.approved.txt")
         else
             [||])

let private specTests =
    testList "the generator's order, as one shared definition" [
        test "sortSurfaceBody is the identity on every committed baseline" {
            let files = committedBaselines.Value

            // Non-vacuity: quantifying over an empty set would pass this
            // trivially, and a moved baseline directory is exactly how that
            // would happen.
            Expect.isGreaterThan
                files.Length
                50
                "api-baselines/ should hold the committed per-assembly baselines — an empty sweep would pass the check below over nothing"

            let drifted =
                files
                |> Array.choose (fun file ->
                    let _, body = splitBaselineHeader (File.ReadAllText file)

                    if sortSurfaceBody body = body then
                        None
                    else
                        Some(Path.GetFileName file))

            Expect.isEmpty
                drifted
                (sprintf
                    "sortSurfaceBody disagrees with what the generator emitted for %d baseline(s): %s. The shared definition of the baseline's order and the renderer have drifted — fix one of them before the merge driver is trusted."
                    drifted.Length
                    (String.concat ", " (drifted |> Array.truncate 5)))
        }

        test "sortSurfaceBody actually sorts — it is not the identity function" {
            // The falsifier for the case above: if `sortSurfaceBody` simply
            // returned its input, that case would be green and meaningless.
            let scrambled = [
                "Demo.U (class)"
                "Demo.U.Zed : System.String { get }"
                "Demo.T (class)"
                "Demo.T.Beta() : System.Int32"
                "Demo.T.Alpha() : System.Int32"
            ]

            let expected = [
                "Demo.T (class)"
                "Demo.T.Alpha() : System.Int32"
                "Demo.T.Beta() : System.Int32"
                "Demo.U (class)"
                "Demo.U.Zed : System.String { get }"
            ]

            Expect.equal
                (sortSurfaceBody scrambled)
                expected
                "types by ordinal FullName, members by ordinal token within their block"
        }

        test "an obsolete marker stays with the token it marks" {
            let scrambled = [
                "Demo.T (class)"
                "Demo.T.Beta() : System.Int32"
                "Demo.T.Alpha() : System.Int32"
                "Demo.T.Alpha() : System.Int32  (obsolete)"
            ]

            let expected = [
                "Demo.T (class)"
                "Demo.T.Alpha() : System.Int32"
                "Demo.T.Alpha() : System.Int32  (obsolete)"
                "Demo.T.Beta() : System.Int32"
            ]

            Expect.equal
                (sortSurfaceBody scrambled)
                expected
                "the Phase 258 marker is a separate line that must follow its member, never sort away from it"
        }
    ]

// ─── Link 2: the driver agrees with the spec ─────────────────────────

let private driverTests =
    testList "the merge driver" [
        test "members: disjoint additions on both sides merge with no conflict" {
            let ours =
                membersFixture [
                    "Demo.T (class)"
                    "Demo.T.Alpha() : System.Int32"
                    "Demo.T.Beta() : System.Int32"
                    "Demo.U (class)"
                    "Demo.U.Zed : System.String { get }"
                ]

            let theirs =
                membersFixture [
                    "Demo.T (class)"
                    "Demo.T.Aardvark() : System.Int32"
                    "Demo.T.Alpha() : System.Int32"
                    "Demo.U (class)"
                    "Demo.U.Zed : System.String { get }"
                    "Demo.V (class)"
                    "Demo.V.W : System.Int32 (literal)"
                ]

            let r = mergeWith "members" (membersFixture baseBody) ours theirs

            Expect.equal r.Exit 0 (sprintf "expected a clean merge; stderr was: %s" r.Stderr)

            let expected =
                membersFixture (
                    sortSurfaceBody [
                        "Demo.T (class)"
                        "Demo.T.Alpha() : System.Int32"
                        "Demo.T.Beta() : System.Int32"
                        "Demo.T.Aardvark() : System.Int32"
                        "Demo.U (class)"
                        "Demo.U.Zed : System.String { get }"
                        "Demo.V (class)"
                        "Demo.V.W : System.Int32 (literal)"
                    ]
                )

            Expect.equal
                r.Merged
                expected
                "the merged body is both sides' additions, in the order a regeneration would emit them"
        }

        test "members: an addition on one side only is taken" {
            let ours = membersFixture baseBody

            let theirs =
                membersFixture [
                    "Demo.T (class)"
                    "Demo.T.Alpha() : System.Int32"
                    "Demo.U (class)"
                    "Demo.U.Zed : System.String { get }"
                    "Demo.V (class)"
                    "Demo.V.W : System.Int32 (literal)"
                ]

            let r = mergeWith "members" (membersFixture baseBody) ours theirs
            Expect.equal r.Exit 0 (sprintf "expected a clean merge; stderr was: %s" r.Stderr)
            Expect.equal r.Merged theirs "one side untouched means the other side's addition is the answer"
        }

        test "members: a removal on one side stays CONFLICTED" {
            let ours =
                membersFixture [
                    "Demo.T (class)"
                    "Demo.T.Beta() : System.Int32"
                    "Demo.U (class)"
                    "Demo.U.Zed : System.String { get }"
                ]

            let r = mergeWith "members" (membersFixture baseBody) ours (membersFixture baseBody)

            Expect.equal r.Exit 1 "a removal is BREAKING (Phase 618) and must reach a human"
            Expect.isTrue (conflicted r) "the driver leaves standard conflict markers so git records a conflict"

            Expect.stringContains
                r.Stderr
                "Demo.T.Alpha() : System.Int32"
                "the reason names the member that was lost, not just that something was"
        }

        test "coverage: both sides' deltas are summed per row" {
            let r =
                mergeWith
                    "coverage"
                    (coverageFixture [ "Alpha 10/20"; "Beta 5/9" ])
                    (coverageFixture [ "Alpha 14/26"; "Beta 5/9" ])
                    (coverageFixture [ "Alpha 10/20"; "Beta 7/12"; "Gamma 1/3" ])

            Expect.equal r.Exit 0 (sprintf "expected a clean merge; stderr was: %s" r.Stderr)

            Expect.equal
                r.Merged
                (coverageFixture [ "Alpha 14/26"; "Beta 7/12"; "Gamma 1/3" ])
                "base + (ours - base) + (theirs - base) per row; a row on one side only is taken as-is"
        }

        test "coverage: a row whose arithmetic goes negative stays CONFLICTED" {
            let r =
                mergeWith
                    "coverage"
                    (coverageFixture [ "Alpha 10/20" ])
                    (coverageFixture [ "Alpha 0/20" ])
                    (coverageFixture [ "Alpha 3/20" ])

            Expect.equal r.Exit 1 "two deltas that cannot both be right are a disagreement, not a merge"
            Expect.isTrue (conflicted r) "the driver leaves standard conflict markers so git records a conflict"
            Expect.stringContains r.Stderr "Alpha" "the reason names the row"
        }

        test "coverage: more documented members than members stays CONFLICTED" {
            let r =
                mergeWith
                    "coverage"
                    (coverageFixture [ "Alpha 10/20" ])
                    (coverageFixture [ "Alpha 19/20" ])
                    (coverageFixture [ "Alpha 19/20" ])

            Expect.equal r.Exit 1 "28 documented of 20 is not a coverage figure"
            Expect.isTrue (conflicted r) "the driver leaves standard conflict markers so git records a conflict"
        }

        test "members: a real committed baseline round-trips byte-identically" {
            // base = ours = theirs over a file nobody touched. The answer is
            // the file itself, and anything else means the driver's re-emission
            // has drifted from the generator's.
            match committedBaselines.Value |> Array.tryHead with
            | None -> failtest "api-baselines/ holds no per-assembly baseline to round-trip"
            | Some file ->
                let text = File.ReadAllText(file).Replace("\r\n", "\n")
                let r = mergeWith "members" text text text
                Expect.equal r.Exit 0 (sprintf "expected a clean merge; stderr was: %s" r.Stderr)

                Expect.equal
                    r.Merged
                    text
                    (sprintf "%s did not survive an untouched three-way merge" (Path.GetFileName file))
        }
    ]

// ─── The wiring, end to end, through a real `git merge` ──────────────

let private gitMergeTest =
    testList "registered in a real clone" [
        test "two branches adding disjoint members merge cleanly through git" {
            let shell = pwsh.Value |> Option.defaultWith (fun () -> failwith "pwsh not on PATH")

            let dir =
                Path.Combine(Path.GetTempPath(), "toolup-805-git-" + Guid.NewGuid().ToString "N")

            Directory.CreateDirectory dir |> ignore

            try
                let git args =
                    let exit, out, err = run "git" args dir
                    Expect.equal exit 0 (sprintf "git %s failed: %s%s" (String.concat " " args) out err)

                let baselineFile = Path.Combine(dir, "api-baselines", "Fixture.approved.txt")
                Directory.CreateDirectory(Path.Combine(dir, "api-baselines")) |> ignore

                git [ "init"; "-b"; "main" ]
                git [ "config"; "user.email"; "phase805@example.invalid" ]
                git [ "config"; "user.name"; "Phase 805" ]

                // Exactly the two lines this phase adds to the repo's own
                // `.gitattributes`, and exactly the registration `Setup`
                // writes — so this case fails if either spelling drifts.
                File.WriteAllText(
                    Path.Combine(dir, ".gitattributes"),
                    "api-baselines/*.approved.txt            merge=toolup-members\napi-baselines/doc-coverage.approved.txt merge=toolup-coverage\n"
                )

                git [
                    "config"
                    "merge.toolup-members.driver"
                    sprintf "\"%s\" -NoProfile -File \"%s\" -Mode members %%O %%A %%B %%P" shell driverPath
                ]

                File.WriteAllText(baselineFile, membersFixture baseBody)
                git [ "add"; "-A" ]
                git [ "commit"; "-m"; "base" ]

                git [ "checkout"; "-b"; "theirs" ]

                File.WriteAllText(
                    baselineFile,
                    membersFixture [
                        "Demo.T (class)"
                        "Demo.T.Aardvark() : System.Int32"
                        "Demo.T.Alpha() : System.Int32"
                        "Demo.U (class)"
                        "Demo.U.Zed : System.String { get }"
                    ]
                )

                git [ "commit"; "-am"; "theirs adds Aardvark" ]
                git [ "checkout"; "main" ]

                File.WriteAllText(
                    baselineFile,
                    membersFixture [
                        "Demo.T (class)"
                        "Demo.T.Alpha() : System.Int32"
                        "Demo.T.Beta() : System.Int32"
                        "Demo.U (class)"
                        "Demo.U.Zed : System.String { get }"
                    ]
                )

                git [ "commit"; "-am"; "ours adds Beta" ]

                let exit, out, err = run "git" [ "merge"; "--no-edit"; "theirs" ] dir

                Expect.equal
                    exit
                    0
                    (sprintf
                        "the registered driver should merge two disjoint additions with no conflict — git said:\n%s%s"
                        out
                        err)

                let merged = File.ReadAllText(baselineFile).Replace("\r\n", "\n")

                Expect.equal
                    merged
                    (membersFixture [
                        "Demo.T (class)"
                        "Demo.T.Aardvark() : System.Int32"
                        "Demo.T.Alpha() : System.Int32"
                        "Demo.T.Beta() : System.Int32"
                        "Demo.U (class)"
                        "Demo.U.Zed : System.String { get }"
                    ])
                    "both additions survive, in the order a regeneration would emit them"
            finally
                try
                    // git leaves read-only objects on Windows.
                    for f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) do
                        File.SetAttributes(f, FileAttributes.Normal)

                    Directory.Delete(dir, true)
                with _ ->
                    ()
        }
    ]

[<Tests>]
let tests =
    testList "Phase 805 — generated-baseline merge drivers" [
        specTests

        // The driver is a `.ps1`. Where no `pwsh` exists there is nothing to
        // invoke — but the link-1 cases above are pure F# and still run, so
        // the generator's own order stays pinned on any machine.
        if pwsh.Value.IsSome then
            driverTests
            gitMergeTest
        else
            testList "the merge driver" [ ptest "pwsh is not on PATH — the merge-driver cases cannot run here" { () } ]
    ]
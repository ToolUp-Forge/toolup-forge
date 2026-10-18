// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.NugetPushTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform.Build
open ToolUp.Platform.Transport

// ─── Phase 1016 — a slow push is retried, not fatal to the release ───
//
// `NugetPush.pushAll` is the `Publish` target's push loop with the
// network taken out: the push runner, the sleep and the log are all
// parameters, so every case here drives a scripted fake runner. Nothing
// is packed, pushed or slept on.
//
// Each passing direction has a twin in the other direction: a loop that
// never retries passes "a clean run pushes everything", and a loop that
// retries forever passes "a transient failure is healed", so both the
// heal and the give-up are asserted, with exact attempt counts.

/// A fake runner. `script` maps a package file name to the exit codes of
/// its successive attempts; attempts past the script's end succeed.
/// Returns the runner and the ordered log of (package, attempt) calls.
let private scripted (script: Map<string, int list>) =
    let calls = Collections.Generic.List<string>()

    let push (pkg: string) =
        let name = Path.GetFileName pkg
        let n = calls |> Seq.filter ((=) name) |> Seq.length
        calls.Add name

        match Map.tryFind name script with
        | Some codes when n < codes.Length -> codes[n]
        | _ -> 0

    push, calls

let private noSleep (_: TimeSpan) = ()

let private run policy script packages =
    let push, calls = scripted script
    let logs = Collections.Generic.List<string>()
    let sleeps = Collections.Generic.List<TimeSpan>()

    let outcome = NugetPush.pushAll policy sleeps.Add logs.Add push packages

    outcome, List.ofSeq calls, List.ofSeq logs, List.ofSeq sleeps

let private pkgs = [
    "artifacts/A.1.0.0.nupkg"
    "artifacts/B.1.0.0.nupkg"
    "artifacts/C.1.0.0.nupkg"
]

let private countOf name calls =
    calls |> List.filter ((=) name) |> List.length

let private env (pairs: (string * string) list) =
    let m = Map.ofList pairs
    fun name -> Map.tryFind name m |> Option.defaultValue null

let private repoRoot =
    let rec walkUp (dir: DirectoryInfo) =
        if isNull (box dir) then
            None
        elif File.Exists(Path.Combine(dir.FullName, "ToolUp.Forge.sln")) then
            Some dir.FullName
        else
            walkUp dir.Parent

    walkUp (DirectoryInfo AppContext.BaseDirectory)

[<Tests>]
let tests =
    testList "Phase 1016 — the Publish push is retried, not fatal to the release" [

        testList "the default budget" [
            test "is the first push plus three retries, 900 s each" {
                Expect.equal NugetPush.defaultPolicy.MaxAttempts 4 "three retries after the first push"
                Expect.equal NugetPush.defaultPolicy.Timeout (Some(TimeSpan.FromSeconds 900.0)) "900 s per push"

                Expect.isOk
                    (RetryPolicy.validate NugetPush.defaultPolicy)
                    "the default must be a policy the transport tier's validator accepts"
            }
        ]

        testList "push arguments" [
            test "every push passes --timeout and --skip-duplicate" {
                let args =
                    NugetPush.pushArgs NugetPush.defaultPolicy "https://feed.example/index.json" "fake-key" "A.nupkg"

                let after flag =
                    args |> List.skipWhile ((<>) flag) |> List.tryItem 1

                Expect.equal (after "--timeout") (Some "900") "the push client's 300 s default is what failed"
                Expect.contains args "--skip-duplicate" "the retry's safety rests on --skip-duplicate"
                Expect.equal (after "--source") (Some "https://feed.example/index.json") "the source is passed through"
            }

            test "a policy with no timeout passes none, deferring to the client" {
                let args =
                    NugetPush.pushArgs
                        {
                            NugetPush.defaultPolicy with
                                Timeout = None
                        }
                        "s"
                        "k"
                        "A.nupkg"

                Expect.isFalse (List.contains "--timeout" args) "no --timeout without a policy timeout"
            }
        ]

        testList "the environment overrides" [
            test "unset means the default policy" {
                Expect.equal (NugetPush.policyFromEnv (env [])) (Ok NugetPush.defaultPolicy) "no override, no change"
            }

            test "both overrides apply, retries counting AFTER the first push" {
                let policy =
                    NugetPush.policyFromEnv (env [ NugetPush.TimeoutVariable, "1200"; NugetPush.RetriesVariable, "0" ])

                match policy with
                | Ok p ->
                    Expect.equal p.Timeout (Some(TimeSpan.FromSeconds 1200.0)) "the timeout override"
                    Expect.equal p.MaxAttempts 1 "zero retries is one attempt"
                | Error e -> failtestf "a valid override was refused: %s" e
            }

            test "a malformed or out-of-range value is refused by name, never defaulted" {
                for name, raw in
                    [
                        NugetPush.TimeoutVariable, "fifteen minutes"
                        NugetPush.TimeoutVariable, "0"
                        NugetPush.RetriesVariable, "-1"
                        NugetPush.RetriesVariable, "50"
                    ] do
                    match NugetPush.policyFromEnv (env [ name, raw ]) with
                    | Ok _ -> failtestf "%s=%s was accepted" name raw
                    | Error e -> Expect.stringContains e name "the refusal names the variable"
            }
        ]

        testList "the retry loop" [
            test "a clean run pushes everything, once each, without sleeping" {
                let outcome, calls, _, sleeps = run NugetPush.defaultPolicy Map.empty pkgs
                Expect.equal outcome.Pushed pkgs "every package pushed, in order"
                Expect.isNone outcome.Failed "nothing failed"
                Expect.isEmpty outcome.Unpushed "nothing left"
                Expect.equal calls.Length 3 "one push per package"
                Expect.isEmpty sleeps "no backoff on a clean run"
            }

            test "a transient failure is retried and succeeds, and the retry is logged" {
                let outcome, calls, logs, sleeps =
                    run NugetPush.defaultPolicy (Map.ofList [ "B.1.0.0.nupkg", [ 1 ] ]) pkgs

                Expect.equal outcome.Pushed pkgs "the release completes"
                Expect.isNone outcome.Failed "a healed package is not a failure"
                Expect.equal (countOf "B.1.0.0.nupkg" calls) 2 "B was pushed twice: the failure and the retry"
                Expect.equal sleeps [ TimeSpan.FromSeconds 15.0 ] "one backoff, the policy's initial one"

                Expect.exists
                    logs
                    (fun l -> l.Contains "B.1.0.0.nupkg" && l.Contains "retry succeeded")
                    "a retry that succeeds is logged"
            }

            test "a symbol-package failure is retried like any other, and the release completes" {
                // `dotnet nuget push` pushes the .snupkg beside the .nupkg in the
                // same invocation, so a cancelled symbol upload is a non-zero exit
                // of THAT package's push. The retry re-runs it; the already-accepted
                // .nupkg is skipped as a duplicate and the symbol push goes again.
                let outcome, calls, _, sleeps =
                    run NugetPush.defaultPolicy (Map.ofList [ "A.1.0.0.nupkg", [ 1; 1 ] ]) pkgs

                Expect.equal outcome.Pushed pkgs "the release completes"
                Expect.equal (countOf "A.1.0.0.nupkg" calls) 3 "two failures, then the push that succeeded"

                Expect.equal
                    sleeps
                    [ TimeSpan.FromSeconds 15.0; TimeSpan.FromSeconds 30.0 ]
                    "the backoff grows between attempts"
            }

            test "a push that throws counts as a failed attempt and is retried" {
                let mutable n = 0

                let push (_: string) =
                    n <- n + 1
                    if n = 1 then failwith "connection reset" else 0

                let outcome =
                    NugetPush.pushAll NugetPush.defaultPolicy noSleep ignore push [ "A.1.0.0.nupkg" ]

                Expect.isNone outcome.Failed "the throw was healed by the retry"
                Expect.equal n 2 "one throw, one retry"
            }

            test "a persistent failure fails after exactly N attempts and stops the loop" {
                let outcome, calls, _, sleeps =
                    run NugetPush.defaultPolicy (Map.ofList [ "B.1.0.0.nupkg", List.replicate 10 7 ]) pkgs

                Expect.equal outcome.Failed (Some("artifacts/B.1.0.0.nupkg", 7)) "B failed, with its last exit code"
                Expect.equal (countOf "B.1.0.0.nupkg" calls) 4 "the first push plus three retries, and no more"
                Expect.equal (countOf "C.1.0.0.nupkg" calls) 0 "nothing after the failed package was attempted"
                Expect.equal sleeps.Length 3 "a backoff before each retry"
                Expect.equal outcome.Pushed [ "artifacts/A.1.0.0.nupkg" ] "what got through before the failure"
            }

            test "a policy of one attempt does not retry at all" {
                let policy = {
                    NugetPush.defaultPolicy with
                        MaxAttempts = 1
                }

                let outcome, calls, _, sleeps =
                    run policy (Map.ofList [ "A.1.0.0.nupkg", [ 1 ] ]) pkgs

                Expect.isSome outcome.Failed "one failure is final"
                Expect.equal calls [ "A.1.0.0.nupkg" ] "one push, no retry"
                Expect.isEmpty sleeps "no backoff"
            }
        ]

        testList "the remainder report" [
            test "names the failed package and every package left unpushed, and none that were pushed" {
                let outcome, _, _, _ =
                    run NugetPush.defaultPolicy (Map.ofList [ "B.1.0.0.nupkg", List.replicate 10 1 ]) pkgs

                Expect.equal
                    outcome.Unpushed
                    [ "artifacts/B.1.0.0.nupkg"; "artifacts/C.1.0.0.nupkg" ]
                    "the failed package, then everything never reached"

                let report = NugetPush.remainderReport NugetPush.defaultPolicy outcome
                Expect.stringContains report "B.1.0.0.nupkg failed on all 4 attempt(s)" "what failed, and how often"
                Expect.stringContains report "2 are left unpushed" "how many a re-run will push"
                Expect.stringContains report "B.1.0.0.nupkg, C.1.0.0.nupkg" "which ones a re-run will push"
                Expect.isFalse (report.Contains "A.1.0.0.nupkg") "a pushed package is not in the remainder"
            }

            test "is empty for a clean run" {
                let outcome, _, _, _ = run NugetPush.defaultPolicy Map.empty pkgs
                Expect.equal (NugetPush.remainderReport NugetPush.defaultPolicy outcome) "" "nothing to report"
            }
        ]

        testList "the shipped publish workflow" [
            test "states the push budget, and its job timeout sits above one package's worst case under it" {
                let text =
                    match repoRoot with
                    | Some root ->
                        let path = Path.Combine(root, ".github", "workflows", "publish-nuget.yml")

                        if File.Exists path then
                            File.ReadAllText path
                        else
                            failtestf "%s is missing - this test would otherwise be vacuously green" path
                    | None -> failtest "could not locate the repo root from the test binary"

                let publishJob =
                    let start = text.IndexOf "\n  publish:\n"
                    let stop = text.IndexOf("\n  published-package-smoke:", start + 1)
                    Expect.isGreaterThan start -1 "the publish job is present"
                    Expect.isGreaterThan stop start "the smoke job follows it"
                    text.Substring(start, stop - start)

                let envValue name =
                    let m = Regex.Match(publishJob, sprintf "\n\\s+%s:\\s*\"?(\\d+)\"?" name)
                    Expect.isTrue m.Success (sprintf "the push step sets %s" name)
                    m.Groups[1].Value

                let policy =
                    match
                        NugetPush.policyFromEnv (
                            env [
                                NugetPush.TimeoutVariable, envValue NugetPush.TimeoutVariable
                                NugetPush.RetriesVariable, envValue NugetPush.RetriesVariable
                            ]
                        )
                    with
                    | Ok p -> p
                    | Error e -> failtestf "the workflow's push budget is refused: %s" e

                let jobTimeout =
                    let m = Regex.Match(publishJob, "\n    timeout-minutes:\\s*(\\d+)")
                    Expect.isTrue m.Success "the publish job declares timeout-minutes"
                    TimeSpan.FromMinutes(float (int m.Groups[1].Value))

                let worstCase =
                    let pushes =
                        TimeSpan.FromTicks(policy.Timeout.Value.Ticks * int64 policy.MaxAttempts)

                    let backoff =
                        [ 2 .. policy.MaxAttempts ]
                        |> List.sumBy (fun n -> (RetryPolicy.delayFor policy n).Ticks)
                        |> TimeSpan.FromTicks

                    pushes + backoff

                // Ten minutes for the pack ahead of the push loop.
                let floor = worstCase + TimeSpan.FromMinutes 10.0

                Expect.isGreaterThan
                    jobTimeout
                    floor
                    (sprintf
                        "timeout-minutes (%.0f) must exceed one package's worst case (%.1f min) plus the pack"
                        jobTimeout.TotalMinutes
                        worstCase.TotalMinutes)
            }
        ]
    ]
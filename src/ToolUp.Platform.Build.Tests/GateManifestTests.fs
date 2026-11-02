// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.GateManifestTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Build
open ToolUp.Forge
open ToolUp.Forge.TestLane

// ─── The gate manifest's renders, and their parity with the workflows ──
//
// `GateManifest.rows` is the one list of what the forge gate runs; CI's
// `EXPECTED_PACKS`, the CI table in `CLAUDE.md`, the pure lane and the
// `verify-all` floor are renders of it. Each render is held here against
// the tree, and each parity rule is shown red by one perturbation of the
// manifest or the workflow text, so a green here is not vacuous.

let private root = GateReconcileTests.repoRoot

let private rows = GateManifest.rows GateManifest.unbound

let private read (relative: string) =
    File.ReadAllText(Path.Combine(root, relative))

/// Every workflow under `.github/workflows`, by file name.
let private workflows () =
    Directory.GetFiles(Path.Combine(root, ".github", "workflows"), "*.yml")
    |> Array.map (fun f -> Path.GetFileName f, File.ReadAllText f)
    |> List.ofArray

/// The parity between the rows' `Where` and the workflows: every
/// `checks.yml` job is a row's `Ci` site or a named CI-only reason; every
/// row's `Ci` job exists in some workflow and runs one of the row's tokens;
/// every `Release` row is invoked by `publish-nuget.yml`.
let parityFindings (rows: GateLeg<Lane> list) (workflows: (string * string) list) : string list =
    let text name =
        workflows
        |> List.tryFind (fst >> (=) name)
        |> Option.map snd
        |> Option.defaultValue ""

    let checksJobs = GateManifest.jobIds (text "checks.yml")
    let rowJobs = rows |> List.collect GateManifest.jobsOf |> List.distinct
    let ciOnly = GateManifest.ciOnlyJobs |> List.map fst

    [
        for job in checksJobs do
            if not (List.contains job rowJobs) && not (List.contains job ciOnly) then
                sprintf
                    "job `%s` in checks.yml is neither a row's `Ci` site nor a named CI-only reason: a job in no row is a gate the manifest cannot see"
                    job

        for job in ciOnly do
            if not (List.contains job checksJobs) then
                sprintf "the CI-only reason names job `%s`, which checks.yml does not declare" job

        for row in rows do
            for job in GateManifest.jobsOf row do
                match workflows |> List.tryPick (fun (_, t) -> PublishedSmoke.jobBlock job t) with
                | None -> sprintf "row `%s` says `Ci \"%s\"`, and no workflow declares that job" row.Name job
                | Some block ->
                    let body = String.concat "\n" block

                    if not (GateManifest.runTokens job row |> List.exists body.Contains) then
                        sprintf
                            "job `%s` runs none of row `%s`'s tokens (%s): the row says the job runs it and the job does not"
                            job
                            row.Name
                            (String.concat " / " (GateManifest.runTokens job row))

            if Set.contains Site.Release row.Where then
                let release = text "publish-nuget.yml"

                if not (GateManifest.runTokens "" row |> List.exists release.Contains) then
                    sprintf "row `%s` says `Release`, and publish-nuget.yml invokes none of its tokens" row.Name
    ]

/// The pure lane's summary block as characterised on 2026-11-02 over tree
/// 71864484, before the manifest existed: the byte oracle the fold's
/// tokens are held to. Every pack row with `Reads = []`, in row order,
/// then the `GateReconcile` row, which runs in every lane.
let characterisedPureBlock = [
    "VerifyAll summary:"
    "  PASS — Voice"
    "  PASS — AlgorithmProviders"
    "  PASS — AssetStore"
    "  PASS — Reporting"
    "  PASS — Webhooks"
    "  PASS — GateReconcile"
]

let tests =
    testList "the gate manifest" [
        test "every row names something that exists, has a lane, and no name repeats" {
            Expect.isGreaterThanOrEqual rows.Length 30 "the manifest carries the packs and the legs"
            Expect.equal (rows |> List.map _.Name |> List.distinct |> List.length) rows.Length "row names are unique"

            for row in rows do
                Expect.isNonEmpty (Set.toList row.Lanes) (sprintf "row `%s` names at least one lane" row.Name)

                match row.Kind with
                | LegKind.ExpectoPack project ->
                    Expect.isTrue
                        (File.Exists(Path.Combine(root, project)))
                        (sprintf "row `%s` names project %s, which is not on disk" row.Name project)
                | LegKind.Script path ->
                    Expect.isTrue
                        (File.Exists(Path.Combine(root, path)))
                        (sprintf "row `%s` names script %s, which is not on disk" row.Name path)
                | LegKind.Body _ -> ()
        }

        test "the pack view is the Local pack rows, and a row that reads nothing is in the pure lane" {
            let packs = Gate.packs rows
            Expect.equal packs.Length 35 "thirty-five packs run locally"

            for row in rows do
                match row.Kind with
                | LegKind.ExpectoPack _ when Set.contains Site.Local row.Where ->
                    Expect.equal
                        (Set.contains Lane.Pure row.Lanes)
                        (List.isEmpty row.Reads)
                        (sprintf "pack `%s` is in the pure lane exactly when its reads are empty" row.Name)
                | _ -> ()
        }

        test "the verify-all floor is the count of rows VerifyAll runs in the full lane, and checks.yml declares it" {
            let floor = Gate.ciFloor GateManifest.VerifyAllJob rows
            Expect.equal floor (GateManifest.localRows rows).Length "every Local full-lane row is a `verify-all` row"

            Expect.equal
                (GateManifest.readExpectedPacks (read ".github/workflows/checks.yml"))
                (Some floor)
                "EXPECTED_PACKS in checks.yml is the manifest's render — `dotnet run -- Gate --render` writes it"
        }

        test "the CLAUDE.md CI table is the render of Where" {
            let claude = read "CLAUDE.md"
            let jobs = GateManifest.jobIds (read ".github/workflows/checks.yml")
            let expected = GateManifest.ciTable jobs rows

            match GateManifest.regionLines GateManifest.CiTableBegin GateManifest.CiTableEnd claude with
            | None -> failtest "CLAUDE.md carries no gate-manifest:ci-table markers"
            | Some actual ->
                Expect.equal actual expected "the generated region is the manifest's render — `Gate --render` writes it"
        }

        test "every checks.yml job is a row or a named reason, and every row's job runs it" {
            match parityFindings rows (workflows ()) with
            | [] -> ()
            | found -> failtestf "parity findings:\n  %s" (String.concat "\n  " found)
        }

        test
            "the parity is red for a job with no row, a row with no job, a job that does not run its row, and a drifted floor" {
            let real = workflows ()

            let checks = real |> List.find (fst >> (=) "checks.yml") |> snd

            let withJob =
                real
                |> List.map (fun (n, t) ->
                    if n = "checks.yml" then
                        n, t + "\n  stranger:\n    runs-on: ubuntu-latest\n"
                    else
                        n, t)

            Expect.exists
                (parityFindings rows withJob)
                (fun f -> f.Contains "`stranger`")
                "a job added without a row is red"

            let orphan =
                rows
                @ [
                    {
                        rows.Head with
                            Name = "Orphan"
                            Where = set [ Site.Ci "no-such-job" ]
                    }
                ]

            Expect.exists
                (parityFindings orphan real)
                (fun f -> f.Contains "`Orphan`")
                "a row naming a job no workflow has is red"

            let unrun =
                rows
                @ [
                    {
                        rows.Head with
                            Name = "Unrun"
                            Where = set [ Site.Ci "fantomas" ]
                    }
                ]

            Expect.exists
                (parityFindings unrun real)
                (fun f -> f.Contains "`Unrun`")
                "a row whose job runs none of its tokens is red"

            Expect.isTrue (checks.Contains "EXPECTED_PACKS") "the floor is in the workflow"
            let drifted = GateManifest.renderExpectedPacks 1 checks

            Expect.notEqual
                (GateManifest.readExpectedPacks drifted)
                (Some(Gate.ciFloor GateManifest.VerifyAllJob rows))
                "a drifted floor reads back different from the manifest's, which is what `Gate --check` refuses"
        }

        test "a Pending verdict is never counted as a pass" {
            let token = Gate.statusToken (Verdict.Pending "ANTHROPIC_API_KEY unset")
            Expect.isFalse (token.Contains "PASS") "not a PASS token"
            Expect.isFalse (token.Contains "FAIL") "not a FAIL token"
            Expect.stringStarts token "PENDING" "it says so by name"

            Expect.equal
                (Gate.judge "x" (Some(Floor.AtLeast 1)) (fun _ -> 0) (Verdict.Pending "unset"))
                (Verdict.Pending "unset")
                "a floor does not turn a pending arm into anything else"
        }

        test "the fold's floors go red: below AtLeast, off Exactly, below a HighWater mark, and a floor over no count" {
            let pass n = Verdict.Pass(Some n)

            let isFail v =
                match v with
                | Verdict.Fail _ -> true
                | _ -> false

            Expect.equal
                (Gate.judge "f" (Some(Floor.AtLeast 100)) (fun _ -> 0) (pass 258))
                (pass 258)
                "above the floor passes"

            Expect.isTrue
                (isFail (Gate.judge "f" (Some(Floor.AtLeast 100)) (fun _ -> 0) (pass 3)))
                "below AtLeast is red"

            Expect.equal (Gate.judge "b" (Some(Floor.Exactly 8)) (fun _ -> 0) (pass 8)) (pass 8) "exactly passes"

            Expect.isTrue
                (isFail (Gate.judge "b" (Some(Floor.Exactly 8)) (fun _ -> 0) (pass 7)))
                "one short of Exactly is red"

            Expect.isTrue
                (isFail (Gate.judge "d" (Some(Floor.HighWater "m")) (fun _ -> 600) (pass 599)))
                "below the mark is red"

            Expect.equal
                (Gate.judge "d" (Some(Floor.HighWater "m")) (fun _ -> 600) (pass 600))
                (pass 600)
                "at the mark passes"

            Expect.isTrue
                (isFail (Gate.judge "n" (Some(Floor.AtLeast 1)) (fun _ -> 0) (Verdict.Pass None)))
                "a floor over an unknown count is red"

            Expect.equal
                (Gate.judge "n" None (fun _ -> 0) (Verdict.Pass None))
                (Verdict.Pass None)
                "no floor, no judgement"
        }

        test "a pack is launched with every other row's Env input cleared and its own kept" {
            let registered: Gate.Registered list =
                rows
                |> List.map (fun r -> {
                    Name = r.Name
                    Kind = r.Kind
                    Where = r.Where
                    Reads = r.Reads
                    Budget = r.Budget
                    Floor = r.Floor
                    Selected = true
                })

            let find name =
                registered |> List.find (fun r -> r.Name = name)

            let forms = Gate.scrubbedFor registered (find "Forms")
            let providers = Gate.scrubbedFor registered (find "AIProviders")
            Expect.contains forms "ANTHROPIC_API_KEY" "a pack that declares no key has every key cleared"
            Expect.isFalse (List.contains "ANTHROPIC_API_KEY" providers) "the pack that declares the key keeps it"
            Expect.contains providers "TOOLUP_PARITY_AZURITE" "and loses the ones it does not declare"
        }

        test "the pure lane's summary block reproduces the characterisation, token for token" {
            let pureRows =
                rows
                |> List.filter (fun r -> Set.contains Site.Local r.Where && Set.contains Lane.Pure r.Lanes)

            let block =
                "VerifyAll summary:"
                :: (pureRows
                    |> List.map (fun r -> sprintf "  %s — %s" (Gate.statusToken (Verdict.Pass None)) r.Name))

            Expect.equal block characterisedPureBlock "the rows the pure lane selects print the characterised block"
        }

        test "the exit-code and raise adapters keep the historical tokens" {
            Expect.equal (Gate.statusToken (Gate.verdictOfExit 0)) "PASS" "exit 0"

            Expect.equal
                (Gate.statusToken (Gate.verdictOfExit 3))
                "FAIL (exit 3)"
                "a non-zero exit, as the block always printed it"

            Expect.equal
                (Gate.verdictOf (fun () -> failwith "red"))
                (Verdict.Fail "red")
                "a failwith is the body's own red"

            Expect.throws
                (fun () -> Gate.verdictOf (fun () -> raise (InvalidOperationException "harness")) |> ignore)
                "any other exception is a harness fault"
        }
    ]
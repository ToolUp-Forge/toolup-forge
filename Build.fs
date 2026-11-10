module ToolUpApp.Build

open System.IO
open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open ToolUp.Platform
open ToolUp.Platform.Build
// Phase 326 — the ToolUp.Sdk meta-manifest reconciler (repo-root
// SdkManifest.fs, compiled ahead of this file).
open ToolUp.Forge

/// Wait for `task`. A build driver may block; this is the one place it does.
let private block (task: System.Threading.Tasks.Task<'T>) : 'T = task.GetAwaiter().GetResult()

/// The gate manifest's rows with their bodies unbound: the DATA the
/// driver's configuration, its lane announcement and the standalone
/// targets' floors are read from. `main` binds the bodies and registers
/// the rows (`GateManifest.rows bodies`) once every body is defined.
let private manifestData = GateManifest.rows GateManifest.unbound

let config = {
    BuildConfig.defaults with
        Port = 5000
        // Derived: the `Local` pack rows of the gate manifest, in row order.
        // A new pack is a row in GateManifest.fs, never an entry here.
        // `GateManifest.exclusions` names the test projects that are
        // deliberately not rows, and the `GateReconcile` row holds the two
        // together.
        TestPacks = Gate.packs manifestData
}

// ─── Phase 762 — gate lanes (`TOOLUP_TEST_LANE`) ────────────────────
//
// A full `VerifyAll` is tens of minutes, nearly all of it inside the
// Platform pack, and the `Samples` leg (Phase 904) — every sample built
// and the Fable ones transpiled — is part of every full lane. No figure
// is restated here: one stated six ways went stale six ways, and none
// counted the samples leg. `VerifyAll` prints the measured figure itself
// at the end of every run, and `gate-timings.json` is the committed fold of
// the recorded runs (`dotnet run -- GateTimings --record`). A dispatched worker has to spend all of it before
// it learns whether its own change is green, and then spend it AGAIN on
// the folded tree, which is the only run a ship may cite.
//
// The lane rides an environment variable rather than a `verify.ps1`
// switch because that script is a hard-denied path on the forge roadmap
// side (`RM-FOOTPRINT-FORBIDDEN`): the recorded gate-script hash must
// not move, so the lane travels inside the recorded gate COMMAND
// instead — `$env:TOOLUP_TEST_LANE='fast'; pwsh ./verify.ps1`.
//
// Two levels, because the measurement said so. The slow set is entirely
// INSIDE one pack, so `fast` is a LIST-level decision made in that
// pack's own entry point (`src/ToolUp.Platform.Tests/Program.fs`) and
// changes nothing here — every pack still runs. `pure` is a PACK-level
// decision, and it is DERIVED: the pack rows of the gate manifest whose
// `Reads` is empty (their test sources touch no filesystem, process,
// socket or environment variable — the grep `PureLaneTests` re-runs over
// every such row). Each row's `Lanes` carries the result, and the lane
// this process runs selects the rows at registration (end of `main`).
//
// Unset ⇒ `full` ⇒ every `Local` row: the pre-phase gate, unchanged.

/// The `affected` lane's lists file and the selection manifest it names,
/// read once — `None` in every other lane. An `Error` (no lists file, a
/// file naming nothing) fails `VerifyAll` before it builds anything.
let private affectedPlan: Result<TestLane.AffectedPlan, string> option =
    match TestLane.current with
    | TestLane.Lane.Affected -> Some(TestLane.readAffectedPlan ())
    | TestLane.Lane.Pure
    | TestLane.Lane.Fast
    | TestLane.Lane.Full -> None

/// The selection manifest the lists file names, when it names a readable one.
let private affectedManifest =
    match affectedPlan with
    | Some(Ok { Manifest = Some(Ok m) }) -> Some m
    | _ -> None

/// The lane every pack and leg decision below runs on. It is the variable's
/// lane, except that an `affected` selection a forcing path turned into the
/// full lane (its lists file keeps `*`) runs every leg as `full` does — the
/// Platform pack keeps every list from the same file, so the run is whole.
let private verifyLane =
    match TestLane.current, affectedPlan with
    | TestLane.Lane.Affected, Some(Ok plan) when plan.Lists.Everything -> TestLane.Lane.Full
    | lane, _ -> lane

/// Did client-tier code move, as far as the `affected` lane knows? With no
/// manifest to say (a lists file written by hand) the answer is yes: a leg
/// skipped on a guess is the direction that ships a broken client tier.
let private affectedClientTierMoved =
    match affectedManifest with
    | Some m -> m.ClientTierMoved
    | None -> true

/// How many pack rows this lane runs, for the announcement. `fast` runs
/// every pack (the slow set it drops is inside the Platform pack and is
/// that pack's own declaration), `affected` too; `pure` runs the rows
/// that read nothing.
let private selectedPackCount =
    manifestData
    |> List.filter (fun r ->
        Set.contains Site.Local r.Where
        && Set.contains verifyLane r.Lanes
        && (match r.Kind with
            | LegKind.ExpectoPack _ -> true
            | LegKind.Body _
            | LegKind.Script _ -> false))
    |> List.length

[<EntryPoint>]
let main args =
    // The command line, parsed once against every option a target here reads;
    // an option no target declares is refused by name rather than ignored.
    let flags =
        match Flags.parse VerifyGate.driverFlags args with
        | Ok flags -> flags
        | Error why ->
            eprintfn "Build.fs: %s" why
            exit 2

    init args

    // A VerifyAll runs every pack and leg under a temp root of its own: the
    // children inherit TEMP / TMP / TMPDIR, so a fixture directory a run
    // leaves behind lands there, where the temp hygiene check below finds it
    // by family, rather than accumulating in the machine's temp directory
    // (and a sibling session's directories in that shared root are never
    // mistaken for this run's). The root is deleted when the driver exits.
    let runTempRoot =
        if flags.Target = "VerifyAll" then
            let root =
                Path.Combine(
                    Path.GetTempPath(),
                    $"{ToolUp.Platform.Testing.TempDir.Prefix}gate-{System.Guid.NewGuid():N}"
                )

            Directory.CreateDirectory root |> ignore

            for name in [ "TEMP"; "TMP"; "TMPDIR" ] do
                System.Environment.SetEnvironmentVariable(name, root)

            System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
                try
                    Directory.Delete(root, true)
                with _ ->
                    ())

            Some root
        else
            None

    // Phase 762 — name the lane once, so a log cannot be mistaken for a
    // full run. Silent under `full`, so the shipping gate's output is
    // byte-for-byte what it was.
    TestLane.announce (sprintf "VerifyAll will run %d of %d pack(s)" selectedPackCount (List.length config.TestPacks))

    // The `affected` lane says what it did not run, in the gate output,
    // before anything runs — so a log cannot be mistaken for a full run.
    match affectedPlan with
    | _ when flags.Target <> "VerifyAll" -> ()
    | None -> ()
    | Some(Error why) ->
        eprintfn "VerifyAll: %s" why
        exit 2
    | Some(Ok plan) ->
        printfn "%s=%s" TestLane.ListsVariableName plan.ListsPath

        match plan.Manifest with
        | Some(Ok m) -> TestLane.Selection.summaryLines m |> List.iter (printfn "%s")
        | Some(Error why) -> printfn "  no readable selection manifest (%s); every leg runs" why
        | None -> printfn "  no selection manifest (the lists file was written by hand); every leg runs"

    registerTargets config

    /// The mark a `HighWater` floor file holds, read from the repository.
    let readHighWater (file: string) =
        int ((File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, file))).Trim())

    /// A row's verdict with the row's floor applied — the same judgement
    /// `VerifyAll` makes, for the row's standalone target.
    let judged (name: string) (verdict: Verdict) =
        let floor =
            manifestData |> List.tryFind (fun r -> r.Name = name) |> Option.bind _.Floor

        Gate.judge name floor readHighWater verdict

    /// A standalone target's exit: its row's verdict, judged; anything but a
    /// pass is the target's failure, named.
    let ensure (name: string) (verdict: Verdict) =
        match judged name verdict with
        | Verdict.Pass _ -> ()
        | Verdict.Fail reason -> failwithf "Verify%s: %s" name reason
        | Verdict.Pending reason -> failwithf "Verify%s: pending — %s" name reason

    /// The verdict of a `node --test --test-reporter=tap` run, from its TAP
    /// counts rather than its exit status: `node --test` exits 0 when it
    /// matched no test file at all, so a harness that silently stopped
    /// emitting cases — a moved output path, a Fable compile that wrote
    /// elsewhere, an entry point that registered nothing — is
    /// indistinguishable from a green run by exit status alone. The row's
    /// floor (a lower bound, so adding a case never needs an edit) is
    /// applied by the fold.
    let nodeTestVerdict (label: string) (result: ProcResult) : Verdict =
        let output = result.StdOut + result.StdErr
        printfn "%s" output

        let tapCount (name: string) =
            let prefix = sprintf "# %s " name

            output.Split('\n')
            |> Array.map _.Trim()
            |> Array.tryPick (fun line ->
                if line.StartsWith prefix then
                    match System.Int32.TryParse(line.Substring prefix.Length) with
                    | true, n -> Some n
                    | _ -> None
                else
                    None)

        match tapCount "pass", tapCount "fail" with
        | Some passed, Some failed ->
            Trace.tracefn ""
            Trace.tracefn "%s summary: %d passed, %d failed." label passed failed

            if failed > 0 then
                Verdict.Fail(sprintf "%d node:test case(s) failed." failed)
            elif result.ExitCode <> 0 then
                Verdict.Fail(sprintf "node exited %d." result.ExitCode)
            else
                Verdict.Pass(Some passed)
        | _ ->
            Verdict.Fail
                "node:test printed no TAP `# pass` / `# fail` summary — the harness did not run. `node --test` exits 0 in that case, which is why the counts are checked and not just the exit status."

    /// `node --test` over a Fable-compiled harness in `dir`, TAP-reported.
    let nodeTest (dir: string) (args: string list) =
        let node =
            Proc.onPath (sprintf "`%s` was not found on PATH. The Fable-tier harnesses need Node.js (>= 20).") "node"

        Proc.redirected node ([ "--test"; "--test-reporter=tap" ] @ args) dir

    /// The case counts an Expecto `--junit-summary` file reports — its
    /// `<testcase`, `<failure` / `<error` and `<skipped` elements (the
    /// summary carries no count attributes). A structured read, never a
    /// scrape of the coloured console line.
    let junitCounts (path: string) : (int * int * int) option =
        if File.Exists path then
            let text = File.ReadAllText path

            let count (element: string) =
                System.Text.RegularExpressions.Regex.Matches(text, sprintf @"<%s\b" element).Count

            Some(count "testcase", count "failure" + count "error", count "skipped")
        else
            None

    /// The fixture families whose directories a full run is known to leave
    /// behind, each with the reason. A contract pack whose factory hands back
    /// a store with no disposal hook cannot dispose the directory the store
    /// lives in, so the directory outlives its case; the remedy is the
    /// contract's factory answering an `IDisposable`, after which the family
    /// leaves this list (a listed family a full run did NOT leave behind is
    /// itself a finding, so the list only shrinks).
    let tempLeakAllowance: Map<string, string> =
        let factory =
            "a contract factory with no disposal hook: the store it returns outlives the case"

        let background =
            "a scheduler fixture: background jobs still write into the directory when the case ends"

        let unswept =
            "a pack that does not reference the Testing package keeps its own temp root for an empty secret store"

        [
            "cas-resilient-tests", factory
            "cas-tests", factory
            "compute-budget-fit", background
            "consent-test", factory
            "cs-test", factory
            "do-test", factory
            "ds-test", factory
            "entity-audit", factory
            "entity-stale", factory
            "entity-test", factory
            "eval-resolve", factory
            "eval-test", factory
            "flags-test", factory
            "fleet-test", factory
            "grantconsent", factory
            "idem", factory
            "ingstatus", factory
            "jobsched-handoff", background
            "jobsched-restart", background
            "jobsched-tests", background
            "jobstore-restart", factory
            "jobstore-tests", factory
            "metadata-filter", factory
            "modelexec-tests", factory
            "multisite-tests", factory
            "oauth-refresher-tests", factory
            "parquet-test", factory
            "persistent-event-store-tests", factory
            "providerprofile-tests", factory
            "publicrendering-tests", factory
            "rs-test", factory
            "secret-test", factory
            "serviceaccount-tests", factory
            "sharetoken-tests", factory
            "storeseam", factory
            "tag-filter", factory
            "test", factory
            "tests-addrbook", factory
            "tests-meta-whatsapp", unswept
            "tests-sg", unswept
            "tests-twilio", unswept
            "tests-twilio-wa", unswept
            "tests-webpush", unswept
            "transfer-test", factory
            "usage-log-tests", factory
        ]
        |> Map.ofList

    /// A fixture that creates a directory owns its deletion. After the packs
    /// have run, every `TempDir`-shaped directory left in this run's temp root
    /// is a fixture that did not dispose its directory: it is named by family
    /// and removed, and a family outside `tempLeakAllowance` fails the check.
    /// Under the full lane a listed family that left nothing fails it too.
    /// Outside a VerifyAll run (no run temp root) there is nothing to read.
    let runTempHygiene () : Verdict =
        match runTempRoot with
        | None -> Verdict.Pass None
        | Some root ->
            Trace.tracefn "▶ VerifyAll: TempHygiene (fixture directories the packs left in %s)" root

            let left =
                Directory.EnumerateDirectories root
                |> Seq.choose (fun dir ->
                    ToolUp.Platform.Testing.TempDir.familyOf (Path.GetFileName dir)
                    |> Option.map (fun family -> family, dir))
                |> List.ofSeq

            let byFamily = left |> List.countBy fst |> List.sortBy fst

            for family, count in byFamily do
                Trace.tracefn "  TempHygiene: %s left %d director%s" family count (if count = 1 then "y" else "ies")

            for _, dir in left do
                try
                    Directory.Delete(dir, true)
                with ex ->
                    Trace.traceImportant (sprintf "TempHygiene: could not remove %s: %s" dir ex.Message)

            let leaked =
                byFamily
                |> List.filter (fun (family, _) -> not (tempLeakAllowance.ContainsKey family))

            let stale =
                if verifyLane = TestLane.Lane.Full then
                    tempLeakAllowance
                    |> Map.toList
                    |> List.filter (fun (family, _) -> not (byFamily |> List.exists (fst >> (=) family)))
                    |> List.map fst
                else
                    []

            for family, count in leaked do
                Trace.traceError (
                    sprintf
                        "TempHygiene: family '%s' left %d fixture director%s behind. Dispose it (`use dir = TempDir.create \"%s\"`); see docs/testing/temp-directories.md."
                        family
                        count
                        (if count = 1 then "y" else "ies")
                        family
                )

            for family in stale do
                Trace.traceError (
                    sprintf
                        "TempHygiene: family '%s' is allowed to leak but a full run left nothing behind: remove it from tempLeakAllowance."
                        family
                )

            match leaked, stale with
            | [], [] -> Verdict.Pass None
            | _ ->
                Verdict.Fail(
                    sprintf "%d leaking famil(ies), %d stale allowance(s), named above." leaked.Length stale.Length
                )

    /// Every test project runs or is excluded by name, and `gate-reads.json`
    /// is the render of the manifest's read declarations. Pure file reading
    /// (the solution, three directory scans and the record), so its row runs in every
    /// lane, `pure` included. Reconciles the FULL pack list, not the lane's
    /// projection of it: a lane narrows what runs, it does not make an
    /// undeclared project declared.
    let runGateReconcile () : Verdict =
        Trace.tracefn
            "▶ VerifyAll: GateReconcile (every test project is a pack or a named exclusion; gate-reads.json is the manifest's render; gate-timings.json measures its rows)"

        let readsRecord =
            let path = Path.getFullName GateManifest.GateReadsFile

            if File.exists path then
                Some(File.readAsString path)
            else
                None

        let timingsFindings, timingsWarnings =
            let path = Path.getFullName GateManifest.GateTimingsFile

            GateManifest.timingsFindings
                (GateManifest.rows GateManifest.unbound)
                System.DateTimeOffset.UtcNow
                (if File.exists path then
                     Some(File.readAsString path)
                 else
                     None)

        for w in timingsWarnings do
            Trace.traceImportant (sprintf "GateReconcile: %s" w)

        let readsFindings =
            GateManifest.gateReadsFindings (GateManifest.rows GateManifest.unbound) readsRecord
            @ timingsFindings

        match Gate.reconcile (Path.getFullName ".") config.TestPacks GateManifest.exclusions, readsFindings with
        | Ok(), [] -> Verdict.Pass None
        | reconciled, readsFindings ->
            let findings =
                (match reconciled with
                 | Ok() -> []
                 | Error found -> found)
                @ readsFindings

            for f in findings do
                Trace.traceError (sprintf "GateReconcile: %s" f)

            Verdict.Fail(sprintf "%d finding(s), named above." findings.Length)

    // The `affected` lane's selection, written as a manifest and the lists
    // file the Platform family's packs read.
    //
    // Usage: `dotnet run --project Build.fsproj -- Select
    //   (--base <sha> --head <sha> | --paths <file>) [--declared <file>]
    //   [--source declared|graph] --out <manifest>`
    //
    // `--base`/`--head` select over `git diff --name-only --no-renames`
    // between the two (a rename is its old path deleted and its new one
    // added, so both sides are seen). `--paths` takes one path per line and
    // is the prediction over a change's declared files before it exists; a
    // path given relative to a directory above this repository is re-rooted
    // at its first segment that names an entry of the repository root.
    // `--declared` names lists the change is known to reach, one per line
    // (`<pack>/<list>`, or a bare Platform list by label or binding). The
    // lists file is written beside `--out`, with the extension `.lists`.
    // `--source graph` selects whole packs instead: those whose project
    // closure (the pack's project and every project it references) owns a
    // changed path, with their last recorded case counts, and names the rest
    // as skipped; `declared` (the default) is the list-level selection.
    Target.create "Select" (fun _ ->
        let root = Path.getFullName "."

        let lines (path: string) =
            File.ReadAllLines path
            |> Array.map _.Trim()
            |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#"))
            |> List.ofArray

        let rootEntries =
            Directory.EnumerateFileSystemEntries root |> Seq.map Path.GetFileName |> set

        let reroot (raw: string) =
            let segments = (TestLane.Selection.normalise raw).Split('/') |> List.ofArray

            let rec drop (s: string list) =
                match s with
                | first :: rest when not (rootEntries.Contains first) && not rest.IsEmpty -> drop rest
                | _ -> s

            match drop segments with
            | s when rootEntries.Contains(List.head s) -> String.concat "/" s
            | _ -> TestLane.Selection.normalise raw

        let baseSha, headSha, paths =
            match Flags.value "--base" flags, Flags.value "--head" flags, Flags.value "--paths" flags with
            | Some b, Some h, None ->
                let changed =
                    Git.read root [ "diff"; "--name-only"; "--no-renames"; b; h ] |> Lines.ofOutput

                Some b, Some h, changed
            | None, None, Some file -> None, None, lines file |> List.map reroot
            | _ -> failwith "Select: give exactly one of `--base <sha> --head <sha>` or `--paths <file>`."

        let out =
            match Flags.value "--out" flags with
            | Some o -> Path.GetFullPath o
            | None -> failwith "Select: `--out <manifest>` is required."

        let listsFile = Path.ChangeExtension(out, ".lists")

        let packProjects =
            config.TestPacks
            |> List.map (fun p -> p.Name, TestLane.Selection.normalise p.Project)

        // The case count each pack's newest recorded run reported.
        let recordedCases () =
            match VerifyGate.commonGitDir root with
            | Ok dir ->
                let file = VerifyGate.Runs.path dir

                let runs, _ =
                    VerifyGate.Runs.parse (if File.Exists file then File.ReadAllText file else "")

                runs
                |> List.sortBy _.At
                |> List.collect _.Legs
                |> List.choose (fun l -> l.Cases |> Option.map (fun c -> l.Name, c))
                |> Map.ofList
            | Error _ -> Map.empty

        let graph =
            match Flags.value "--source" flags |> Option.defaultValue "declared" with
            | "declared" -> None
            | "graph" ->
                Some {
                    TestLane.Selection.GraphInputs.PackProjects = packProjects
                    TestLane.Selection.GraphInputs.Cases = recordedCases ()
                }
            | other ->
                failwithf "Select: `--source %s` is not a source; give `declared` (the default) or `graph`." other

        let manifest =
            TestLane.Selection.select {
                Base = baseSha
                Head = headSha
                Paths = paths
                Declared = Flags.value "--declared" flags |> Option.map lines |> Option.defaultValue []
                Packs = config.TestPacks |> List.map _.Name
                Family = TestLane.Selection.familyOf packProjects
                Graph = graph
                Read =
                    fun relative ->
                        let full = Path.Combine(root, relative)

                        if File.Exists full then
                            Some(File.ReadAllText full)
                        else
                            None
                ListsFile = listsFile
                Command =
                    "dotnet run --project Build.fsproj -- "
                    + String.concat " " (args |> Array.skipWhile ((<>) "Select"))
            }

        Directory.CreateDirectory(Path.GetDirectoryName out) |> ignore
        File.WriteAllText(out, TestLane.Selection.toJson manifest)

        File.WriteAllText(listsFile, TestLane.Lists.render (Some out) manifest.Forced manifest.Lists)

        TestLane.Selection.summaryLines manifest |> List.iter (Trace.tracefn "%s")

        Trace.tracefn
            "▶ Select: wrote %s and %s. Run the lane with TOOLUP_TEST_LANE=affected and %s=%s."
            out
            listsFile
            TestLane.ListsVariableName
            listsFile)

    // Phase 614 — the Fable-tier test gate as ONE invocation.
    //
    // `VerifyAll` (in ToolUp.Platform.Build) covers the twelve .NET
    // Expecto packs. The client tier's harness is a different shape —
    // Fable-transpiled F# run under Node's built-in `node:test` — and
    // was until now a four-step recipe every caller reproduced by hand
    // from `CLAUDE.md`. Two transcriptions of one recipe drift; a single
    // target cannot, so CI and a developer necessarily run the same
    // thing.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyFable`
    //
    // The last step deliberately reads the TAP COUNTS, not just the exit
    // code (`nodeTestVerdict` above), and the `Fable` row's floor is a
    // LOWER BOUND the fold applies: adding a case never needs a companion
    // edit, and it fires only when the harness has collapsed rather than
    // shrunk — the exact shape of the local `--filter` incident that
    // silently matched 0 Expecto tests.
    //
    // The body is a function rather than the target's lambda so it can be
    // BOTH the standalone `VerifyFable` target and the `Fable` row
    // `VerifyAll` folds: one recipe, two entry points, no drift.
    let runVerifyFable () : Verdict =
        let testDir = Path.getFullName "src/ToolUp.AI.Client.Tests"

        let npm =
            Proc.onPath
                (sprintf
                    "VerifyFable: `%s` was not found on PATH. The Fable-tier harness needs the .NET SDK and Node.js (>= 20).")
                "npm"

        Trace.tracefn "▶ VerifyFable (1/4): dotnet tool restore"
        Proc.exec "dotnet" [ "tool"; "restore" ] testDir |> Proc.ensure |> ignore

        // `ci`, not `install` — the lockfile is committed, so this is the
        // reproducible form and it is the same one CI runs.
        Trace.tracefn "▶ VerifyFable (2/4): npm ci"

        Proc.exec npm [ "ci"; "--no-fund"; "--no-audit" ] testDir
        |> Proc.ensure
        |> ignore

        Trace.tracefn "▶ VerifyFable (3/4): dotnet fable -o output --noCache"

        Proc.exec "dotnet" [ "fable"; "-o"; "output"; "--noCache" ] testDir
        |> Proc.ensure
        |> ignore

        Trace.tracefn "▶ VerifyFable (4/4): node --test output/Program.js"

        // `--test-reporter=tap` is pinned rather than inherited: node
        // picks `spec` on a TTY and `tap` otherwise, and the summary
        // counts `nodeTestVerdict` reads only exist in the TAP form.
        nodeTest testDir [ "--import"; "./register-loader.mjs"; "output/Program.js" ]
        |> nodeTestVerdict "VerifyFable"

    Target.create "VerifyFable" (fun _ -> ensure "Fable" (runVerifyFable ()))

    /// The wire layer's Fable twin under node:test, after a Fable compile
    /// in the harness directory (the `fable-wire-smoke` job's second row).
    let runWireFable (dir: string) (entry: string) (label: string) : Verdict =
        let harness = Path.getFullName dir
        Trace.tracefn "▶ %s (1/3): dotnet tool restore" label
        Proc.exec "dotnet" [ "tool"; "restore" ] harness |> Proc.ensure |> ignore
        Trace.tracefn "▶ %s (2/3): dotnet fable -o output --noCache" label

        Proc.exec "dotnet" [ "fable"; "-o"; "output"; "--noCache" ] harness
        |> Proc.ensure
        |> ignore

        Trace.tracefn "▶ %s (3/3): node --test %s" label entry
        nodeTest harness [ entry ] |> nodeTestVerdict label

    // The same recipe is the `Fable` row of the gate manifest, folded by
    // `VerifyAll` after the packs (outside the `pure` lane, and skipped
    // under `affected` when nothing in the client tier moved), so
    // `verify.ps1` cannot go green on a tree whose Fable tier does not
    // compile.

    // Phase 904 — every sample builds under a gate.
    //
    // `samples/` is not in the solution, so nothing compiled it:
    // `FormsAndAI.Server` was broken (FS0764) from Phase 6g.F until this
    // phase and no gate noticed, and `samples/MinimalClient`'s Fable
    // compile "has never been a gate" (CLAUDE.md). This is that gate.
    //
    // Every `*.fsproj` under `samples/` is BUILT (`dotnet build`, one
    // project at a time, aggregated so a red run names every broken
    // sample rather than the first); every sample Fable compiles for the
    // browser is also TRANSPILED (`dotnet fable`), because a .NET build
    // cannot see what Fable refuses. Afterwards `SampleGate.reconcile`
    // compares what was built with an independent enumeration of the
    // directory, so a discovery that quietly found fewer projects goes
    // red rather than green — and a sample that is deliberately skipped
    // is named, with its reason, in `SampleGate.excluded`.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifySamples`
    let runVerifySamples () =
        let root = Path.getFullName "."
        let samplesDir = Path.Combine(root, "samples")
        let onDisk = SampleGate.discover root samplesDir
        let plan = SampleGate.plan onDisk SampleGate.excluded

        for stale in plan.StaleExclusions do
            Trace.traceError (sprintf "VerifySamples: exclusion `%s` names a project that is not on disk." stale)

        for ex in SampleGate.excluded do
            Trace.tracefn "▶ VerifySamples: excluding %s — %s" ex.Project ex.Reason

        // A Fable transpile of one sample costs ~1.5-2 minutes (it cracks
        // and compiles the whole client tier, ~400 source files), so the
        // six Fable samples are ~10 minutes: the FULL lane only. The fast
        // lane still BUILDS every sample on .NET and asserts the count.
        let transpile =
            match verifyLane with
            | TestLane.Lane.Full -> true
            | TestLane.Lane.Affected -> affectedClientTierMoved
            | TestLane.Lane.Pure
            | TestLane.Lane.Fast -> false

        // Fable resolves its own tool from the nearest `.config/
        // dotnet-tools.json` above the directory it runs in, and it must
        // run IN the sample's directory: pointed at a project from
        // elsewhere (or with `--cwd`) it mis-resolves the Feliz
        // references and fails with hundreds of errors. So each Fable
        // sample carries its own manifest, and a Fable sample without one
        // is a named failure rather than a mystery one.
        let hasToolManifest (projDir: string) =
            let rec up (dir: DirectoryInfo) =
                if isNull dir || dir.FullName.Length <= root.Length then
                    false
                elif File.Exists(Path.Combine(dir.FullName, ".config", "dotnet-tools.json")) then
                    true
                else
                    up dir.Parent

            up (DirectoryInfo projDir)

        let built = ResizeArray<string>()
        let transpileDeferred = ResizeArray<string>()

        let legs =
            plan.ToBuild
            |> List.map (fun proj ->
                Aggregate.leg proj (fun () ->
                    Trace.tracefn "▶ VerifySamples: building %s" proj
                    built.Add proj

                    let build =
                        (Proc.exec "dotnet" [ "build"; proj; "-m:1"; "--nologo"; "-v:q" ] root).ExitCode

                    if build <> 0 then
                        build
                    elif SampleGate.isFableProject (File.ReadAllText(Path.Combine(root, proj))) then
                        let projDir = Path.GetDirectoryName(Path.Combine(root, proj))

                        if not transpile then
                            transpileDeferred.Add proj

                            Trace.tracefn
                                "▶ VerifySamples: %s is a Fable sample; transpile runs in the full lane only."
                                proj

                            0
                        elif not (hasToolManifest projDir) then
                            Trace.traceError (
                                sprintf
                                    "VerifySamples: %s is a Fable sample with no `.config/dotnet-tools.json` at or above its directory, so `dotnet fable` cannot run there. Add a manifest pinning `fable`."
                                    proj
                            )

                            1
                        else
                            Proc.exec "dotnet" [ "tool"; "restore" ] projDir |> ignore
                            Trace.tracefn "▶ VerifySamples: transpiling %s (Fable)" proj

                            // `-o output`, relative, and nothing else: an
                            // absolute output directory outside the sample
                            // makes Fable mis-resolve the Feliz references
                            // (hundreds of FS0039s, measured on 2026-09-29),
                            // which no sample deserves to be blamed for.
                            // `output/` is gitignored; it is removed either
                            // way so a gate run leaves the tree as it found it.
                            let outDir = Path.Combine(projDir, "output")
                            Shell.deleteDir outDir

                            try
                                (Proc.exec "dotnet" [ "fable"; "-o"; "output"; "--noCache" ] projDir).ExitCode
                            finally
                                Shell.deleteDir outDir
                    else
                        0))

        // The count leg is a leg of its own so a mismatch shows up in the
        // summary block by name, alongside any red sample.
        let legs =
            legs
            @ [
                Aggregate.leg "sample count matches the directory" (fun () ->
                    let findings = SampleGate.reconcile onDisk SampleGate.excluded (List.ofSeq built)

                    Trace.tracefn
                        "▶ VerifySamples: %d sample project(s) on disk, %d built, %d excluded; %d Fable transpile(s) deferred to the full lane."
                        onDisk.Length
                        built.Count
                        SampleGate.excluded.Length
                        transpileDeferred.Count

                    for f in findings do
                        Trace.traceError ("VerifySamples: " + f)

                    if List.isEmpty findings then 0 else 1)
            ]

        // Phase 946 — the samples meant to RUN are run, after every build
        // leg, against the build just made (`--no-build`). A sample whose
        // exit code is its verdict (HelloWorld-AOT) could otherwise exit 1
        // under a green build gate, which is what happened. The run list is
        // checked against the build plan first, so a runnable that is not
        // built is a named finding rather than a run of a stale binary.
        let legs =
            legs
            @ [
                Aggregate.leg "runnable samples are all built" (fun () ->
                    let findings = SampleGate.runFindings plan SampleGate.runnable

                    for f in findings do
                        Trace.traceError ("VerifySamples: " + f)

                    if List.isEmpty findings then 0 else 1)
            ]
            @ (SampleGate.runnable
               |> List.filter (fun r -> List.contains r.Project plan.ToBuild)
               |> List.map (fun r ->
                   Aggregate.leg ("run " + r.Project) (fun () ->
                       Trace.tracefn "▶ VerifySamples: running %s — %s" r.Project r.Reason
                       (Proc.exec "dotnet" (SampleGate.runArguments r) root).ExitCode)))

        Aggregate.runAll "VerifySamples" "sample leg" legs

    Target.create "VerifySamples" (fun _ -> runVerifySamples ())

    // Phase 776 — the analyser rule set runs over the SDK's own tree.
    //
    // `ToolUp.Remoting.Analyzers` carries the TUA containment rules
    // (container resolution outside registration, an `HttpClient` built
    // without the egress handler, `Async.RunSynchronously` outside an entry
    // point). This leg parses every tracked `src/**/*.fs` with them and
    // reconciles the sites against the committed inventory
    // (`src/ToolUp.Remoting.Analyzers/analyzer-inventory.json`), then checks
    // that `docs/platform/analyzer-rules.md` is the render of the rule
    // metadata. Any finding is red: a new site, an inventory entry the tree
    // no longer needs, a stale exclusion, a file that does not parse, a
    // hand-edited rule document.
    //
    // It runs the RemotingAnalyzers pack's built dll in its `--scan-tree`
    // mode: that exe already loads the analyser and the compiler service,
    // so this file takes no compiler dependency, and the build the packs
    // ran has produced it. One more `PASS` line in the summary, which the
    // `Analyzers` row of the gate manifest counts into CI's floor.
    //
    // `fast` and `full`, not `pure`: it reads the tree and asks git for the
    // tracked set, which is the opposite of the pure lane's claim. About
    // six seconds over ~1,700 in-scope files, so the fast lane keeps it —
    // a new site is cheapest to hear about before the merge.
    let runVerifyAnalyzers () =
        let project =
            "src/ToolUp.Remoting.Analyzers.Tests/ToolUp.Remoting.Analyzers.Tests.fsproj"

        match TestPackHost.resolveTargetPath project with
        | Error message ->
            Trace.traceError (sprintf "VerifyAll: Analyzers — %s" message)
            1
        | Ok dll -> (Proc.exec "dotnet" [ dll; "--scan-tree"; Path.getFullName "." ] ".").ExitCode

    // Usage: `dotnet run --project Build.fsproj -- VerifyAnalyzers`, after a
    // build of the RemotingAnalyzers pack.
    Target.create "VerifyAnalyzers" (fun _ ->
        match runVerifyAnalyzers () with
        | 0 -> ()
        | code -> failwithf "VerifyAnalyzers: the analyser run exited %d; its findings are printed above." code)

    // Phase 761 — the browser-level smoke gate.
    //
    // The repo's first REAL-BROWSER tier. Two scenarios, both of which
    // shipped verified structurally or by hand because nothing here
    // could open a browser: Phase 535's "two sessions co-edit the
    // sample doc live", and Phase 24's "drop the network, make a
    // change, restore it".
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyBrowserSmoke`
    //
    // ── Why it is a target of its own, and not a `TestPacks` entry ──
    //
    // Because `VerifyAll`'s wall-clock is a shared budget and this
    // launches browsers. The harness project is deliberately outside
    // `ToolUp.Forge.sln` AND outside `BuildConfig.TestPacks`, so the
    // default gate is untouched by construction rather than by
    // convention — there is no list a later edit could add it to by
    // accident.
    //
    // ── It cannot pass vacuously ────────────────────────────────────
    //
    // The scenarios are gated on a locally-installed Chromium and
    // report Pending, not Failed, without one (the `BrowserGate`
    // convention from Phase 126). That is right for a developer's
    // checkout and catastrophic for CI: a runner with no browser would
    // report every scenario skipped and the job green. So this body
    // reads the PASSED count out of the harness's JUnit summary and the
    // `BrowserSmoke` row's floor holds it — the same guard the `Fable`
    // row puts on its TAP counts, and for the same reason. Unlike that
    // one the floor is the EXACT current count rather than a slack lower
    // bound: there are eight scenarios and each is expensive, so losing
    // one silently is exactly what this must catch. Adding or removing a
    // scenario edits the row's floor in the same commit.
    let runVerifyBrowserSmoke () : Verdict =
        let fixtureDir = Path.getFullName "tests/BrowserSmoke/fixture"

        let harnessProject =
            Path.getFullName "tests/BrowserSmoke/ToolUp.BrowserSmoke.Tests/ToolUp.BrowserSmoke.Tests.fsproj"

        let onPath =
            Proc.onPath (
                sprintf
                    "VerifyBrowserSmoke: `%s` was not found on PATH. The harness needs the .NET SDK, Node.js (>= 20) and PowerShell 7 (for Playwright's browser installer)."
            )

        let npm = onPath "npm"

        // ── 0: the compile set this gate is CLAIMED to cover ────────
        //
        // Phase 345. `ToolUp.Offline.Client` reaches the Fable compiler
        // in CI HERE AND NOWHERE ELSE. Exactly two projects in the tree
        // reference it — this fixture and `samples/MinimalClient` — and
        // no CI job compiles the sample. (`ToolUp.Platform.Client` is
        // the better-covered case: `VerifyFable` already drives it
        // transitively through `ToolUp.AI.Client`. It is asserted below
        // anyway, because the fixture link-compiles the sample sources
        // that need it and because a gate should not depend on another
        // job's reference graph for a claim it makes about itself.)
        //
        // A dropped `<ProjectReference>` would therefore not fail — it
        // would NARROW this gate and stay green, which is the one
        // outcome a gate must never have. Assert the compile set
        // instead: a deliberate change to what this covers edits this
        // list, the CI table in the repo's CLAUDE.md and the tier table
        // in `docs/platform/testing-conventions.md`, in one commit.
        let fixtureProject = Path.combine fixtureDir "BrowserSmokeFixture.fsproj"

        if not (File.Exists fixtureProject) then
            failwithf
                "VerifyBrowserSmoke: the fixture project `%s` is missing. Nothing below can compile the client tiers this gate claims to cover."
                fixtureProject

        let fixtureProjectText = File.ReadAllText fixtureProject

        let gatedClientTiers = [ "ToolUp.Platform.Client"; "ToolUp.Offline.Client" ]

        let missingTiers =
            gatedClientTiers
            |> List.filter (fun tier ->
                not (fixtureProjectText.Contains(sprintf "%s\\%s.fsproj" tier tier))
                && not (fixtureProjectText.Contains(sprintf "%s/%s.fsproj" tier tier)))

        if not missingTiers.IsEmpty then
            failwithf
                "VerifyBrowserSmoke: the fixture no longer project-references %s. That does not fail this gate on its own — it SHRINKS it, silently, to whatever is left, while `docs/platform/testing-conventions.md` and CLAUDE.md still say those tiers are transpiled here. For ToolUp.Offline.Client there is no other CI transpile at all. Restore the reference in `%s`, or change what those two documents claim in the same commit."
                (String.concat " and " missingTiers)
                fixtureProject

        // ── 1-4: the page the browser will be pointed at ────────────
        //
        // `ci`, not `install` — the lockfile is committed, so the bundle
        // a CI run serves is reproducible from the tree. The Fable
        // compile and the Vite bundle are separate steps because they
        // fail differently: the first is an F# compile against the real
        // client tiers, the second is module resolution against the npm
        // tree.
        Trace.tracefn "▶ VerifyBrowserSmoke (1/6): dotnet tool restore (fixture)"
        Proc.exec "dotnet" [ "tool"; "restore" ] fixtureDir |> Proc.ensure |> ignore

        Trace.tracefn "▶ VerifyBrowserSmoke (2/6): npm ci (fixture)"

        Proc.exec npm [ "ci"; "--no-fund"; "--no-audit" ] fixtureDir
        |> Proc.ensure
        |> ignore

        Trace.tracefn "▶ VerifyBrowserSmoke (3/6): dotnet fable -o output --noCache (fixture)"

        Proc.exec "dotnet" [ "fable"; "-o"; "output"; "--noCache" ] fixtureDir
        |> Proc.ensure
        |> ignore

        Trace.tracefn "▶ VerifyBrowserSmoke (4/6): npm run build (fixture bundle)"
        Proc.exec npm [ "run"; "build" ] fixtureDir |> Proc.ensure |> ignore

        // ── 5: the harness, and the browser it drives ───────────────
        //
        // Built and then invoked as a DLL rather than through `dotnet
        // run --project`, per Phase 731: that launch path intermittently
        // hangs before the suite starts, and a hang costs far more than
        // a failure because nothing about it says which it is.
        Trace.tracefn "▶ VerifyBrowserSmoke (5/6): build the harness + ensure Chromium"

        // Build FIRST, then ask. `-getProperty:TargetPath` makes MSBuild
        // EVALUATE the project and print the property without running
        // the build, so on a clean runner the query below answers with a
        // path to a dll that does not exist — which is exactly how the
        // `browser-smoke` job failed on every push from the day it
        // landed, while passing locally in worktrees whose harness had
        // already been built by an earlier step. The checked build here
        // is what `VerifyAll` does before its own per-pack queries.
        Proc.exec "dotnet" [ "build"; harnessProject; "--nologo" ] "."
        |> Proc.ensure
        |> ignore

        let targetPath =
            (Proc.redirected "dotnet" [ "build"; harnessProject; "--nologo"; "-getProperty:TargetPath" ] "."
             |> Proc.ensure)
                .StdOut.Trim()

        if String.isNullOrWhiteSpace targetPath || not (File.exists targetPath) then
            failwithf
                "VerifyBrowserSmoke: could not resolve the harness assembly (MSBuild reported %A). Nothing can run without it."
                targetPath

        // Playwright ships its installer beside the built assembly. Best
        // effort: a failure here is reported and not fatal, because the
        // floor check below turns "no browser" into a named failure with
        // a much better message than a download error.
        let installer = Path.combine (Path.getDirectory targetPath) "playwright.ps1"

        if File.exists installer then
            try
                Proc.exec (onPath "pwsh") [ installer; "install"; "chromium" ] "."
                |> Proc.ensure
                |> ignore
            with ex ->
                Trace.traceImportantfn
                    "VerifyBrowserSmoke: Playwright's Chromium install step failed (%s). Continuing — the scenario floor below will report it if no browser is available."
                    ex.Message

        Trace.tracefn "▶ VerifyBrowserSmoke (6/6): run the scenarios"

        // The counts come from the JUnit summary the harness writes, a
        // structured record: Expecto colours every count on its console
        // line, and a regex over that text once reported "the harness did
        // not run" on a run that passed every scenario.
        let summary =
            Path.Combine(Scratch.root "toolup-verifyall", "BrowserSmoke.junit.xml")

        Directory.CreateDirectory(Path.GetDirectoryName summary) |> ignore
        File.Delete summary
        let result = Proc.exec "dotnet" [ targetPath; "--junit-summary"; summary ] "."

        match junitCounts summary with
        | Some(tests, failed, skipped) ->
            let passed = tests - failed - skipped
            Trace.tracefn ""
            Trace.tracefn "VerifyBrowserSmoke summary: %d passed, %d ignored." passed skipped

            if skipped > 0 then
                Trace.traceImportantfn
                    "VerifyBrowserSmoke: %d scenario(s) were skipped. A skipped scenario almost always means Chromium is not installed on this machine — run `pwsh %s install chromium`. Do not lower the row's floor to make this green."
                    skipped
                    installer

            if result.ExitCode <> 0 then
                Verdict.Fail(sprintf "the harness exited %d." result.ExitCode)
            else
                Verdict.Pass(Some passed)
        | None ->
            Verdict.Fail
                "the harness wrote no JUnit summary — it did not run. The counts are checked rather than the exit status alone precisely because a harness that never started can still exit 0."

    Target.create "VerifyBrowserSmoke" (fun _ -> ensure "BrowserSmoke" (runVerifyBrowserSmoke ()))

    // The template-content compile gate. Its rationale and its spec are
    // `TemplateGatePackages.verifyTemplates`; the mechanism is `TemplateGate.run`.
    Target.create "VerifyTemplates" (fun _ -> TemplateGate.run TemplateGatePackages.verifyTemplates)

    // The three documentation gates live in `DocGates.fs`, which holds what
    // each checks and why. Each target here is its gate's verdict: the gate
    // prints its census and findings through the sink, and a refusal is the
    // target's failure.
    //
    //   dotnet run --project Build.fsproj -- VerifyDocSnippets [--update-baseline]
    //   dotnet run --project Build.fsproj -- VerifySourceCitations
    //   dotnet run --project Build.fsproj -- VerifyUploadResults [--root <dir>]
    let docGateSink: DocGates.Sink = {
        Trace = Trace.trace
        Error = Trace.traceError
    }

    let docGateVerdict (cases: 'census -> int option) (result: Result<'census, DocGates.Refusal>) =
        match result with
        | Ok census -> Verdict.Pass(cases census)
        | Error refusal -> Verdict.Fail refusal.Message

    // The snippet gate's count is the blocks it compiled: its row's floor is
    // the high-water mark in `docs-snippets/corpus-floor.txt`.
    let runVerifyDocSnippets () =
        DocGates.docSnippets docGateSink __SOURCE_DIRECTORY__ flags Proc.redirected
        |> docGateVerdict (fun c -> Some c.Compiled)

    let runVerifySourceCitations () =
        DocGates.sourceCitations docGateSink __SOURCE_DIRECTORY__
        |> docGateVerdict (fun _ -> None)

    let runVerifyUploadResults () =
        DocGates.uploadResults docGateSink __SOURCE_DIRECTORY__ flags
        |> docGateVerdict (fun _ -> None)

    /// A documentation gate's standalone target: a refusal fails it with the
    /// gate's own verdict line.
    let refuseUnlessPass (verdict: Verdict) =
        match verdict with
        | Verdict.Pass _ -> ()
        | Verdict.Fail reason
        | Verdict.Pending reason -> failwith reason

    Target.create "VerifyDocSnippets" (fun _ -> refuseUnlessPass (runVerifyDocSnippets ()))
    Target.create "VerifySourceCitations" (fun _ -> refuseUnlessPass (runVerifySourceCitations ()))
    Target.create "VerifyUploadResults" (fun _ -> refuseUnlessPass (runVerifyUploadResults ()))

    // The registry of generated artefacts, `projections.json` (read by
    // `Projections` in Approval.fs, which ProjectionsTests holds to the tree),
    // and the render it drives here. `dev-scripts/regen.ps1 <stem>` reads the
    // same registry to regenerate a stem's rows.
    //
    //   dotnet run --project Build.fsproj -- Projections [--check]  refuse a .fantomasignore that is not the render
    //   dotnet run --project Build.fsproj -- Projections --render   write it
    Target.create "Projections" (fun _ ->
        let root = __SOURCE_DIRECTORY__
        let path = Path.Combine(root, ".fantomasignore")

        let rendered =
            ToolUp.Platform.Tests.Contracts.Projections.read root
            |> ToolUp.Platform.Tests.Contracts.Projections.renderFantomasIgnore

        if Flags.has "--render" flags then
            File.WriteAllText(path, rendered)
            Trace.tracefn "Projections: wrote .fantomasignore from projections.json."
        elif
            not (File.Exists path)
            || File.ReadAllText(path).Replace("\r\n", "\n") <> rendered
        then
            failwith
                "Projections: .fantomasignore is not the render of projections.json. Change the row, then run `dotnet run --project Build.fsproj -- Projections --render` and commit both."
        else
            Trace.tracefn "Projections: .fantomasignore is the render of projections.json.")

    // The scope grammar has ONE home.
    //
    // `ScopeKey` (src/ToolUp.Platform.Core/Shared/Types/ScopeKey.fs) renders a
    // scope's storage container (`team-{id}` / `user-{id}` / `session-{id}` /
    // `_platform`) in one function and reads a container at rest back in one
    // parser. Before it, the grammar was minted by string interpolation and
    // parsed back by prefix tests at a dozen sites, so two spellings of one
    // scope could be produced and a reader could disagree with a writer about
    // which scope a container named. A helper beside the literals would not
    // have stopped the next one (the 863 shape: the fix is a pattern the build
    // refuses, not an alternative it offers).
    //
    // The check is textual: a string literal that IS `"_platform"`, or that
    // opens with `team-` / `user-` / `session-` followed directly by the
    // closing quote, an interpolation hole or a format specifier (the shapes
    // that build or test a container: `"team-"`, `$"team-{id}"`,
    // `sprintf "user-%s"`). A literal that merely begins with the word
    // (`"team-role"`, `"user-agent"`) is not a site. Scope: production code
    // under `src/` — directories named `*.Tests` are fixtures and are
    // excluded, as are comment lines and `ScopeKey.fs` itself.
    //
    // A ZERO, with named exceptions. Every scope under `src/` is rendered
    // through `ScopeKey.container` / `ScopeKey.scopeId` (the platform shard's
    // string is `ScopeKey.PlatformContainer`) and read back through
    // `ScopeKey.tryParseContainer` / `StorageScope.tryKey`; a package holding
    // a literal is red. The tree reached zero package by package under a
    // per-package ratchet, retired once every package read zero.
    //
    // A literal that has the grammar's spelling but is NOT a scope container
    // — or one in a file another change holds — is a NAMED exception in
    // `scopeLiteralExceptions`: its file, how many sites it excuses there,
    // and why. Exactly that many sites in that file are excused, never more;
    // an exception its file has outgrown prints the count to record.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyScopeLiterals`
    //        `… -- VerifyScopeLiterals --root <dir>` scans `<dir>/src` (how
    //        the check is shown red on a planted literal without touching
    //        the tree). Also a leg of `VerifyAll` in the fast and full lanes.
    let scopeLiteralExceptions: (string * int * string) list = [
        "src/EmbeddingProviders/Local/LocalEmbeddingProvider.fs",
        2,
        "the local embedder's persisted per-scope STATE key, built over an escaped id; not a storage container, and it must not move if the container grammar does"
        "src/ToolUp.Cli/Commands/Users.fs",
        1,
        "the CLI depends on FSharp.Core alone and reads a deployment over its wire; it names the container the deployment stores a user under"
        "src/ToolUp.Cli/Cli/MembershipsDoctorCommand.fs",
        1,
        "the CLI depends on FSharp.Core alone; it opens the platform container's directory under a file-storage data root"
        "src/ToolUp.Platform.Core/Shared/ComponentRequirements.fs",
        1,
        "compiles ahead of ScopeKey.fs in the Core project; ScopeKeyTests pins it equal to ScopeKey.PlatformContainer"
        "src/ToolUp.Platform.Core/Shared/Types/ConfigKeyDescriptor.fs",
        1,
        "the module key of the platform-defaults configuration SCHEMA, not a scope container"
        "src/ToolUp.Platform.Core/Shared/Types/EgressPolicyTypes.fs",
        1,
        "a ComponentId (the platform as the component an outbound call is attributed to), not a scope; it compiles ahead of ScopeKey.fs"
    ]

    let runVerifyScopeLiterals (root: string) : int =
        let srcDir = Path.Combine(root, "src")

        if not (Directory.Exists srcDir) then
            Trace.traceError (
                sprintf "VerifyScopeLiterals: no `src` directory under %s — nothing to scan is not a pass." root
            )

            1
        else
            let rec walk (dir: string) = seq {
                for d in Directory.EnumerateDirectories dir do
                    let name = Path.GetFileName d

                    if
                        name <> "bin"
                        && name <> "obj"
                        && name <> "output"
                        && name <> "node_modules"
                        && not (name.EndsWith ".Tests")
                    then
                        yield! walk d

                yield! Directory.EnumerateFiles(dir, "*.fs")
            }

            let literal =
                System.Text.RegularExpressions.Regex("\"_platform\"|\"(team|user|session)-(\"|\\{|%)")

            let isComment (line: string) =
                let t = line.TrimStart()
                t.StartsWith "//" || t.StartsWith "(*"

            // The package a file belongs to: the nearest enclosing directory
            // holding an `.fsproj`, named by that project file.
            let rec packageOf (dir: string) =
                if isNull dir || not (dir.StartsWith srcDir) then
                    "(no project)"
                else
                    match Directory.GetFiles(dir, "*.fsproj") |> Array.sort |> Array.tryHead with
                    | Some proj -> Path.GetFileNameWithoutExtension proj
                    | None -> packageOf (Path.GetDirectoryName dir)

            let sites = [
                for file in walk srcDir do
                    let rel = Paths.toSlash (Path.GetRelativePath(root, file))

                    if not (rel.EndsWith "ToolUp.Platform.Core/Shared/Types/ScopeKey.fs") then
                        for i, line in File.ReadAllLines file |> Array.indexed do
                            if not (isComment line) then
                                for _ in literal.Matches line do
                                    yield packageOf (Path.GetDirectoryName file), rel, i + 1, line.Trim()
            ]

            let excusedIn =
                scopeLiteralExceptions |> List.map (fun (file, n, _) -> file, n) |> Map.ofList

            // The first `n` sites of an excepted file are excused; the rest
            // count against its package like any other literal.
            let sites, excused =
                sites
                |> List.groupBy (fun (_, rel, _, _) -> rel)
                |> List.collect (fun (rel, fileSites) ->
                    let n = excusedIn |> Map.tryFind rel |> Option.defaultValue 0
                    fileSites |> List.mapi (fun i site -> i < n, site))
                |> List.partition (fst >> not)
                |> fun (counted, excused) -> List.map snd counted, List.map snd excused

            let counts = sites |> List.countBy (fun (pkg, _, _, _) -> pkg) |> Map.ofList

            let over = counts |> Map.toList

            Trace.tracefn ""
            Trace.tracefn "VerifyScopeLiterals summary:"
            Trace.tracefn "  scope literals : %d in %d package(s)" sites.Length counts.Count

            Trace.tracefn
                "  excepted       : %d site(s) in %d named file(s)"
                excused.Length
                scopeLiteralExceptions.Length

            for file, n, _ in scopeLiteralExceptions do
                let held = excused |> List.filter (fun (_, rel, _, _) -> rel = file) |> List.length

                if held < n then
                    Trace.tracefn
                        "  exception      : %s holds %d against an exception of %d — lower its row in scopeLiteralExceptions"
                        file
                        held
                        n

            if over.IsEmpty then
                Trace.tracefn ""
                Trace.tracefn "VerifyScopeLiterals: OK — no scope literal outside ScopeKey and the named exceptions."
                0
            else
                Trace.tracefn ""

                for pkg, n in over do
                    Trace.traceError (sprintf "  %s: %d scope literal(s)" pkg n)

                    for (_, rel, line, text) in sites |> List.filter (fun (p, _, _, _) -> p = pkg) do
                        Trace.traceError (sprintf "    %s:%d: %s" rel line text)

                Trace.traceError (
                    sprintf
                        "VerifyScopeLiterals: %d package(s) spell a scope container by hand. Render a scope through `ScopeKey.container` / `ScopeKey.scopeId` (`ScopeKey.PlatformContainer` for the reserved shard), and read a container back through `ScopeKey.tryParseContainer` or `StorageScope.tryKey`, never a hand-written container literal. A literal that is not a scope is a named exception in scopeLiteralExceptions, with its reason."
                        over.Length
                )

                1

    // ── VerifyAccessFallbacks — no site states `Unrestricted` from an absence ──
    //
    // An access context's module permissions are a closed type, and "every
    // module" is a stated case (`ModulePermissions.Unrestricted`) decided in
    // exactly three files: the type's own module (the named system and
    // scope-carrying contexts), the permission store (a team that never
    // configured permissions) and the scope-resolution middleware (a subject
    // in no team). A fourth production site constructing it — or the retired
    // `AccessContext.unrestricted` fallback a handler used for a request it
    // could not resolve — is red. A match arm reading the case (`|
    // ModulePermissions.Unrestricted ->`) is not a construction. Tests build
    // their contexts through the fixture and are not scanned (`*.Tests`
    // directories).
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyAccessFallbacks`
    //        `… -- VerifyAccessFallbacks --root <dir>` scans `<dir>`'s
    //        `src`, `samples` and `templates`. Also run by the `ScopeLiterals`
    //        leg of `VerifyAll`, in the fast and full lanes.
    let unrestrictedHomes = [
        "src/ToolUp.Platform.Core/Shared/Types/AccessContext.fs"
        "src/ToolUp.Platform.Server/Server/Scope/PermissionStore.fs"
        "src/ToolUp.Platform.Server/Server/Middleware.fs"
    ]

    /// Files that spell the old and new forms as TEXT, never as code: the
    /// codemod's rules match and rewrite them.
    let accessFallbackSpellers = [ "src/ToolUp.Platform.Build/Build/SDK.Codemod.fs" ]

    let runVerifyAccessFallbacks (root: string) : int =
        let rec walk (dir: string) = seq {
            for d in Directory.EnumerateDirectories dir do
                let name = Path.GetFileName d

                if
                    name <> "bin"
                    && name <> "obj"
                    && name <> "output"
                    && name <> "node_modules"
                    && not (name.EndsWith ".Tests")
                then
                    yield! walk d

            yield! Directory.EnumerateFiles(dir, "*.fs")
        }

        let roots =
            [ "src"; "samples"; "templates" ]
            |> List.map (fun d -> Path.Combine(root, d))
            |> List.filter Directory.Exists

        if roots.IsEmpty then
            Trace.traceError (
                sprintf
                    "VerifyAccessFallbacks: no src, samples or templates under %s — nothing scanned is not a pass."
                    root
            )

            1
        else
            let retired =
                System.Text.RegularExpressions.Regex @"\bAccessContext\.unrestricted\b"

            let stated =
                System.Text.RegularExpressions.Regex @"\bModulePermissions\.Unrestricted\b(?!\s*->)"

            // The request's principal is ONE item; these keys carried its
            // pieces separately and are retired. The principal's own key is
            // spelled only by its constant.
            let retiredKey =
                System.Text.RegularExpressions.Regex
                    "\"ToolUp\\.(User|UserId|Subject|StorageScope|ScopeResult|ModulePermissions|ModuleExposure|PlatformRole|PublicationGrants|ServiceAccountPermissions|AccessContext)\""

            let isComment (line: string) =
                let t = line.TrimStart()
                t.StartsWith "//" || t.StartsWith "(*"

            let files = roots |> Seq.collect walk |> Seq.toList

            let sites = [
                for file in files do
                    let rel = Paths.toSlash (Path.GetRelativePath(root, file))
                    let home = unrestrictedHomes |> List.contains rel

                    if not (accessFallbackSpellers |> List.contains rel) then
                        for i, line in File.ReadAllLines file |> Array.indexed do
                            if not (isComment line) then
                                if retired.IsMatch line then
                                    yield rel, i + 1, "the retired fallback", line.Trim()
                                elif not home && stated.IsMatch line then
                                    yield rel, i + 1, "Unrestricted stated outside its homes", line.Trim()
                                elif retiredKey.IsMatch line && not (rel.EndsWith "Shared/Types/AccessContext.fs") then
                                    yield rel, i + 1, "a retired request item key", line.Trim()
            ]

            Trace.tracefn ""
            Trace.tracefn "VerifyAccessFallbacks summary:"
            Trace.tracefn "  files scanned : %d" files.Length
            Trace.tracefn "  sites         : %d" sites.Length

            if sites.IsEmpty then
                Trace.tracefn
                    "VerifyAccessFallbacks: OK — no access fallback, and Unrestricted is stated only in its homes."

                0
            else
                for rel, line, what, text in sites do
                    Trace.traceError (sprintf "  %s:%d: %s: %s" rel line what text)

                Trace.traceError
                    "VerifyAccessFallbacks: a request with no resolved principal is served under `AccessContext.unresolved` (which grants nothing); work outside a request names `AccessContext.system` or `AccessContext.carrying`; `ModulePermissions.Unrestricted` is decided only where `unrestrictedHomes` says; and a request's identity, scope, user and grants are read from its ONE stamped principal (`AccessContext.ofItems` and its accessors), never from a key of their own. See docs/migrations/882-access-context-refuses.md."

                1

    Target.create "VerifyAccessFallbacks" (fun _ ->
        let root =
            match Flags.value "--root" flags with
            | Some r -> Path.GetFullPath r
            | None -> __SOURCE_DIRECTORY__

        match runVerifyAccessFallbacks root with
        | 0 -> ()
        | code -> failwithf "VerifyAccessFallbacks: exited %d; the sites are printed above." code)

    Target.create "VerifyScopeLiterals" (fun _ ->
        let root =
            match Flags.value "--root" flags with
            | Some r -> Path.GetFullPath r
            | None -> __SOURCE_DIRECTORY__

        match runVerifyScopeLiterals root with
        | 0 -> ()
        | code -> failwithf "VerifyScopeLiterals: exited %d; the sites are printed above." code)

    // Phase 1052 — the closure-fold is written once.
    //
    // `ComposeExtensions.ServiceConfig` is an option of a function, so every
    // contributor that wanted to add a registration used to fold by hand —
    // `match x.ServiceConfig with None -> Some f | Some g -> Some(fun s -> f (g s))`
    // — and the census before this phase found 66 copies across 33 files under
    // `src/`, plus four private `withServiceConfig` helpers and `run`'s
    // `appendRegistration`. The fold now lives once, as `ServiceConfig.append`
    // in `Server/Compose/ComposeExtensions.fs`; a companion contributes
    // through `Contribution.service`, a hook through `ServerApp.addServiceConfig`.
    //
    // The check is textual over every `.fs` under `src/` (tests included — a
    // test that folds by hand is the same copy): an option arm that rebuilds
    // `Some` from a composition with the arm's own function, in any of the
    // three spellings the tree used — `| Some g -> Some(fun s -> f (g s))`,
    // `| Some g -> Some(g >> f)`, `| Some g -> Some(f << g)`. The one allowed
    // site is `ComposeExtensions.fs`. A ZERO, not a ratchet: the sweep left
    // none, so any site is red.
    //
    // NOT SCANNED, deliberately: `templates/`. A template is consumer code
    // pinned to a released SDK by its own `sdk-version` default (the
    // dataplatform template defaults to 0.28.0), which predates
    // `ServiceConfig.append`; its two folds can move only when that pin does.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyServiceConfigFold`
    //        `… -- VerifyServiceConfigFold --root <dir>` scans `<dir>/src`
    //        (how the check is shown red on a planted fold without touching
    //        the tree). Also a leg of `VerifyAll` in the fast and full lanes.
    let runVerifyServiceConfigFold (root: string) : int =
        let srcDir = Path.Combine(root, "src")

        if not (Directory.Exists srcDir) then
            Trace.traceError (
                sprintf "VerifyServiceConfigFold: no `src` directory under %s — nothing to scan is not a pass." root
            )

            1
        else
            let rec walk (dir: string) = seq {
                for d in Directory.EnumerateDirectories dir do
                    let name = Path.GetFileName d

                    if name <> "bin" && name <> "obj" && name <> "output" && name <> "node_modules" then
                        yield! walk d

                yield! Directory.EnumerateFiles(dir, "*.fs")
            }

            let fold =
                System.Text.RegularExpressions.Regex(
                    @"\|\s*Some\s+(?<g>\w+)\s*->\s*Some\s*\(\s*(?:fun\s+(?<s>\w+)\s*->\s*\w+\s*\(\s*\k<g>\s+\k<s>\s*\)|\k<g>\s*>>\s*\w+|\w+\s*<<\s*\k<g>)\s*\)"
                )

            let sites = [
                for file in walk srcDir do
                    let rel = Path.GetRelativePath(root, file).Replace('\\', '/')

                    if not (rel.EndsWith "ToolUp.Platform.Server/Server/Compose/ComposeExtensions.fs") then
                        let text = File.ReadAllText file

                        for m in fold.Matches text do
                            let line = text.Substring(0, m.Index).Split('\n').Length
                            yield rel, line, m.Value.Trim()
            ]

            Trace.tracefn ""
            Trace.tracefn "VerifyServiceConfigFold summary:"
            Trace.tracefn "  hand-written folds : %d" sites.Length

            if sites.IsEmpty then
                Trace.tracefn ""

                Trace.tracefn
                    "VerifyServiceConfigFold: OK — the ServiceConfig fold occurs once, as ServiceConfig.append."

                0
            else
                Trace.tracefn ""

                for rel, line, text in sites do
                    Trace.traceError (sprintf "  %s:%d: %s" rel line text)

                Trace.traceError (
                    sprintf
                        "VerifyServiceConfigFold: %d hand-written fold(s) of an option of a function. Append a registration with `ServiceConfig.append` (ComposeExtensions.fs); a companion contributes with `ServerApp.addContribution (Contribution.service owner register)`, a hook with `ServerApp.addServiceConfig register`."
                        sites.Length
                )

                1

    Target.create "VerifyServiceConfigFold" (fun _ ->
        let root =
            match Flags.value "--root" flags with
            | Some r -> Path.GetFullPath r
            | None -> __SOURCE_DIRECTORY__

        match runVerifyServiceConfigFold root with
        | 0 -> ()
        | code -> failwithf "VerifyServiceConfigFold: exited %d; the sites are printed above." code)

    // The download allow-list: `BlobDocumentStore` is the one read-modify-write
    // policy in the server tier.
    //
    // A file under `src/ToolUp.Platform.Server/Server/` that calls
    // `IBlobStorage.Download` decides for itself what an absent blob, an
    // unreadable one and an undecodable one mean, which is the defect class the
    // document store exists to end (a fault read as "empty", then written back).
    // Every such file is either a named home below, with its reason, or on the
    // allow-list of files not yet moved onto the store. The lists only shrink:
    // a file that calls `Download` and is on neither list is red, and so is a
    // file on either list that no longer calls it, so moving a store means
    // deleting its line in the same commit.
    //
    // The scan is textual (`.Download(` or `.Download (`), over the server
    // tier's own sources; companions are outside it. A member DEFINITION
    // named `Download` (`member _.Download(`, a store's own verb) is not a
    // call and does not count.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyBlobDownloads`
    //        `… -- VerifyBlobDownloads --root <dir>` scans that tree instead.
    //        Also run by the `ScopeLiterals` leg of `VerifyAll`, in every lane
    //        that runs it.
    let blobDownloadHomes = [
        "BlobMapStore.fs",
        "the document store itself: the one place absence, decode failure and the ETag probe are decided"
        "DataObjectStore.fs",
        "the append-only slot claim over versioned objects, kept by design beside the document store"
        "EncryptedBlobStorage.fs", "an IBlobStorage decorator: it implements Download, it does not read documents"
        "Infra/ResilientBlobStorage.fs", "an IBlobStorage decorator"
        "BackupCoordinator.fs",
        "the backup plane copies and hashes opaque bytes (and decorates IBlobStorage); its one document, the manifest, already answers absent, unreadable and undecodable with typed BackupError cases"
        "RestoreDrillVerifier.fs",
        "hashes restored opaque bytes against the manifest; an unreadable blob is a recorded mismatch, never absence"
        "ConfigValidator.fs", "a backend round-trip probe: it reads back the sentinel it just wrote"
        "DataProtectionBackendValidator.fs", "a backend round-trip probe: it reads back the sentinel it just wrote"
        "SmokeTests.fs", "a backend round-trip probe: it reads back the sentinel it just wrote"
        "MemoizedComputeDispatcher.fs",
        "a memo cache: an unreadable entry is a miss, and a miss recomputes the deterministic result it would have served"
        "SecondaryIndex.fs",
        "index refs are hints whose canonical document lives elsewhere; a lost ref is drift that IndexConsistencyCheck reports, never authority"
        "BlobXmlRepository.fs",
        "the ASP.NET Core IXmlRepository adapter: it hands the framework's opaque key XML to the key manager, which owns the policy for an unreadable key"
        "IdempotencySweepJob.fs",
        "a maintenance sweep that deletes only what it read and proved expired; an unreadable entry is left for the next pass"
        "ResultStore.fs",
        "the lineage outbox is a queue, not a document store: a relay pass reads each staged intent once, leaves an unreadable one in place for the next pass, and MOVES a poison one out of the queue (lineage-outbox-poison/) so the relay drains; the document store's leave-in-place quarantine would wedge it"
    ]

    let blobDownloadAllowList = [
        // Excluded from the move by Phase 1061's own rule until 976 and 979 ship.
        "EntityOutbox.fs"
    ]

    let runVerifyBlobDownloads (root: string) : int =
        let serverDir = Path.Combine(root, "src", "ToolUp.Platform.Server", "Server")

        if not (Directory.Exists serverDir) then
            Trace.traceError (
                sprintf
                    "VerifyBlobDownloads: no `src/ToolUp.Platform.Server/Server` under %s — nothing to scan is not a pass."
                    root
            )

            1
        else
            let download =
                System.Text.RegularExpressions.Regex(@"(?<!\bmember\s+\w+)\.Download\s*\(")

            let callers =
                Directory.EnumerateFiles(serverDir, "*.fs", SearchOption.AllDirectories)
                |> Seq.filter (fun file -> download.IsMatch(File.ReadAllText file))
                |> Seq.map (fun file -> Path.GetRelativePath(serverDir, file).Replace('\\', '/'))
                |> Set.ofSeq

            let homes = blobDownloadHomes |> List.map fst |> Set.ofList
            let allowed = Set.ofList blobDownloadAllowList
            let unlisted = Set.difference callers (Set.union homes allowed)
            let stale = Set.difference (Set.union homes allowed) callers
            let listedTwice = Set.intersect homes allowed

            Trace.tracefn ""
            Trace.tracefn "VerifyBlobDownloads summary:"
            Trace.tracefn "  files calling IBlobStorage.Download : %d" callers.Count
            Trace.tracefn "  named homes                         : %d" homes.Count
            Trace.tracefn "  still to move onto the store        : %d" (Set.intersect allowed callers).Count

            if unlisted.IsEmpty && stale.IsEmpty && listedTwice.IsEmpty then
                Trace.tracefn ""

                Trace.tracefn
                    "VerifyBlobDownloads: OK — every Download caller is a named home or on the shrinking allow-list."

                0
            else
                Trace.tracefn ""

                for file in unlisted do
                    Trace.traceError (
                        sprintf
                            "  %s calls IBlobStorage.Download. Read and write its documents through BlobDocumentStore<'T> (BlobMapStore.fs) instead; the allow-list only shrinks."
                            file
                    )

                for file in stale do
                    Trace.traceError (
                        sprintf
                            "  %s is listed but no longer calls Download: delete its line from blobDownloadAllowList or blobDownloadHomes."
                            file
                    )

                for file in listedTwice do
                    Trace.traceError (sprintf "  %s is both a named home and on the allow-list." file)

                1

    Target.create "VerifyBlobDownloads" (fun _ ->
        let root =
            match Flags.value "--root" flags with
            | Some r -> Path.GetFullPath r
            | None -> __SOURCE_DIRECTORY__

        match runVerifyBlobDownloads root with
        | 0 -> ()
        | code -> failwithf "VerifyBlobDownloads: exited %d; the files are printed above." code)

    // GenerateConfigReference — regenerate every projection of the config-key
    // registry (`ConfigKeys.keys`): `docs/reference/config-reference.md`,
    // `docs/reference/toolup.config.schema.json`, the Description column of
    // `docs/operations/env-vars.md`, and the generated region of
    // `DEPLOYMENT.md` (which keys refuse a typo, and what the boot seal
    // covers).
    //
    // It builds the Platform pack and runs its `ConfigReference` list in
    // regeneration mode, where each golden-file case writes its artefact
    // instead of comparing it. The gate is the same list run normally, in
    // every lane of the Platform pack under `VerifyAll`: a committed
    // projection that differs from the registry is red, naming this target.
    //
    // Usage: `dotnet run --project Build.fsproj -- GenerateConfigReference`
    Target.create "GenerateConfigReference" (fun _ ->
        let project = "src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj"

        Proc.exec "dotnet" [ "build"; project; "--nologo"; "-v:q" ] "."
        |> Proc.ensure
        |> ignore

        match TestPackHost.resolveTargetPath project with
        | Error message -> failwithf "GenerateConfigReference: %s" message
        | Ok dll ->
            Proc.execWith
                {
                    Proc.options with
                        Environment = [ "TOOLUP_APPROVE", "config-reference" ]
                }
                "dotnet"
                [ dll; "--filter-test-list"; "ConfigReference" ]
                "."
            |> Proc.ensure
            |> ignore

            Trace.tracefn
                "GenerateConfigReference: regenerated the reference, the schema, the operations descriptions and the DEPLOYMENT.md region. Commit them with the registry change.")

    // The Fuaran co-resolution rule and the co-build probe.
    //
    // This repository's Fuaran substrate floors (`FuaranCoreVersion`,
    // `FuaranComputeVersion`) against the newest PUBLISHED Fuaran.UI release's:
    // a floor ahead of the UI tier's across a breaking boundary lifts every
    // consumer composing both onto a substrate its UI tier was not compiled for,
    // and nothing in this repository's own build can see it. The rule lives in
    // FuaranCoResolution.fs (pure, proved offline in ToolUp.Platform.Build.Tests);
    // its three inputs are committed files, so the gate never fetches:
    //
    // - `Directory.Packages.props` — the pins;
    // - `fuaran-ui-floors.json` — the snapshot of the newest published Fuaran.UI
    //   release's floors, written only by `RefreshFuaranUiFloors`;
    // - `docs/platform/open-core-boundary.md` — the declared leads.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyFuaranCoResolution`
    //        `… [--props <file>] [--snapshot <file>] [--boundary <file>]` reads
    //        the named file instead (how the rule is shown red on a planted
    //        drift without touching the tree). A leg of `VerifyAll` in the fast
    //        and full lanes.
    let fuaranFloorsFile = Path.Combine(__SOURCE_DIRECTORY__, "fuaran-ui-floors.json")

    let readFuaranSnapshot (path: string) =
        match FuaranCoResolution.snapshotOfJson (File.ReadAllText path) with
        | Ok s -> s
        | Error e -> failwithf "%s: %s" path e

    let runVerifyFuaranCoResolution (propsPath: string) (snapshotPath: string) (boundaryPath: string) : int =
        let inputs =
            match
                FuaranCoResolution.pinsOfPackagesProps (File.ReadAllText propsPath),
                FuaranCoResolution.snapshotOfJson (File.ReadAllText snapshotPath),
                FuaranCoResolution.leadsOfMarkdown (File.ReadAllText boundaryPath)
            with
            | Ok pins, Ok snapshot, Ok leads -> Ok(pins, snapshot, leads)
            | pins, snapshot, leads ->
                Error [
                    match pins with
                    | Error es -> yield! es |> List.map (sprintf "%s: %s" propsPath)
                    | Ok _ -> ()
                    match snapshot with
                    | Error e -> yield sprintf "%s: %s" snapshotPath e
                    | Ok _ -> ()
                    match leads with
                    | Error e -> yield sprintf "%s: %s" boundaryPath e
                    | Ok _ -> ()
                ]

        match inputs with
        | Error errors ->
            for e in errors do
                Trace.traceError ("VerifyFuaranCoResolution: " + e)

            1
        | Ok(pins, snapshot, leads) ->
            let verdicts = FuaranCoResolution.evaluate pins snapshot leads
            Trace.tracefn ""
            Trace.tracefn "VerifyFuaranCoResolution: against the published Fuaran.UI %s" snapshot.Release

            for v in verdicts do
                match FuaranCoResolution.severity v with
                | FuaranCoResolution.Pass -> Trace.tracefn "  ok      %s" (FuaranCoResolution.describe v)
                | FuaranCoResolution.Warning -> Trace.traceImportant ("  warning " + FuaranCoResolution.describe v)
                | FuaranCoResolution.Refusal -> Trace.traceError ("  REFUSED " + FuaranCoResolution.describe v)

            match FuaranCoResolution.refusals verdicts with
            | [] ->
                Trace.tracefn "VerifyFuaranCoResolution: OK — every Fuaran floor resolves beside the published UI tier."
                0
            | refused ->
                Trace.traceError (sprintf "VerifyFuaranCoResolution: %d refusal(s), named above." refused.Length)
                1

    Target.create "VerifyFuaranCoResolution" (fun _ ->
        let file name fallback =
            Flags.value name flags
            |> Option.map Path.GetFullPath
            |> Option.defaultValue fallback

        match
            runVerifyFuaranCoResolution
                (file "--props" (Path.Combine(__SOURCE_DIRECTORY__, "Directory.Packages.props")))
                (file "--snapshot" fuaranFloorsFile)
                (file "--boundary" (Path.Combine(__SOURCE_DIRECTORY__, "docs", "platform", "open-core-boundary.md")))
        with
        | 0 -> ()
        | code -> failwithf "VerifyFuaranCoResolution: exited %d; the verdicts are printed above." code)

    // The snapshot's ONE writer. Reads nuget.org's flat container: the newest
    // released Fuaran.UI, or `--release <v>`, refused when nuget.org does not
    // serve it, and every tier package's nuspec at that version. Run it when a
    // Fuaran.UI release ships (and when the rule says the snapshot is behind a
    // pin), then commit the file.
    //
    // Usage: `dotnet run --project Build.fsproj -- RefreshFuaranUiFloors [--release <v>]`
    Target.create "RefreshFuaranUiFloors" (fun _ ->
        let registry = "https://api.nuget.org/v3-flatcontainer/"

        use http =
            new System.Net.Http.HttpClient(Timeout = System.TimeSpan.FromSeconds 60.0)

        let get (url: string) =
            use response = block (http.GetAsync(url))

            if response.IsSuccessStatusCode then
                Some(block (response.Content.ReadAsStringAsync()))
            elif response.StatusCode = System.Net.HttpStatusCode.NotFound then
                None
            else
                failwithf "RefreshFuaranUiFloors: %s answered %d." url (int response.StatusCode)

        let index (id: string) =
            match get (sprintf "%s%s/index.json" registry (id.ToLowerInvariant())) with
            | Some json ->
                use doc = System.Text.Json.JsonDocument.Parse json

                doc.RootElement.GetProperty("versions").EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq
            | None -> failwithf "RefreshFuaranUiFloors: nuget.org does not list %s." id

        let release =
            match FuaranCoResolution.chooseRelease (Flags.value "--release" flags) (index "Fuaran.UI") with
            | Ok r -> r
            | Error e -> failwith ("RefreshFuaranUiFloors: " + e)

        let packages =
            FuaranCoResolution.uiTierPackages
            |> List.map (fun id ->
                let lower = id.ToLowerInvariant()

                match get (sprintf "%s%s/%s/%s.nuspec" registry lower release lower) with
                | Some nuspec -> id, FuaranCoResolution.floorsOfNuspec nuspec
                | None ->
                    failwithf
                        "RefreshFuaranUiFloors: %s %s is not published; the tier releases together, so the snapshot is not written."
                        id
                        release)
            |> Map.ofList

        let snapshot: FuaranCoResolution.UiSnapshot = {
            Release = release
            Packages = packages
        }

        let json = FuaranCoResolution.snapshotToJson snapshot registry

        let previous =
            if File.Exists fuaranFloorsFile then
                File.ReadAllText fuaranFloorsFile
            else
                ""

        if previous = json then
            Trace.tracefn "RefreshFuaranUiFloors: unchanged — Fuaran.UI %s." release
        else
            File.WriteAllText(fuaranFloorsFile, json)
            Trace.tracefn "RefreshFuaranUiFloors: wrote fuaran-ui-floors.json for Fuaran.UI %s. Commit it." release)

    // The co-build probe: the shape that failed, built for real. A scratch
    // consumer composing the fact tier (ToolUp.Facts.Server + ToolUp.Facts.Client)
    // beside the published Fuaran.UI + Fuaran.UI.Renderer.Server (server) and
    // Fuaran.UI.Renderer (client) restores, builds on .NET and Fable-compiles its
    // client, from this tree's own packs plus nuget.org and nothing else.
    //
    // In the template gates' shape: the tree's closure is packed at a throwaway
    // version into a scratch feed, the consumer restores into a package folder
    // private to this checkout (under obj/), and the consumer lives OUTSIDE the
    // repository with a nuget.config that clears every inherited source, so no
    // machine-local feed can paper over a missing package. Only the throwaway
    // `toolup.*` entries are evicted between runs: every other package in the
    // folder is a published, immutable version. A downgrade (NU1605) and a
    // fallback past the throwaway version (NU1603) are errors.
    //
    // A leg of `VerifyAll` in the FULL lane: the defect is introduced by a commit
    // that moves a floor, not by a release tag, so it is gated where every
    // landing is. Not in `fast`: it packs sixteen projects and runs a Fable
    // compile, minutes the fast lane exists to save.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyFuaranCoBuild`
    //        `… --ui-version <v>` probes another published Fuaran.UI (default:
    //        the snapshot's release); `… --toolup-version <v>` restores a
    //        PUBLISHED ToolUp release from nuget.org instead of packing the tree
    //        (how a past release is shown red or green).
    let fuaranCoBuildVersion = "0.0.0-fuarancobuild"

    let fuaranCoBuildRoots = [
        "src/ToolUp.Facts.Server/ToolUp.Facts.Server.fsproj"
        "src/ToolUp.Facts.Client/ToolUp.Facts.Client.fsproj"
    ]

    let runVerifyFuaranCoBuild (uiVersion: string option) (toolUpVersion: string option) =
        let root = __SOURCE_DIRECTORY__

        let ui =
            uiVersion |> Option.defaultValue (readFuaranSnapshot fuaranFloorsFile).Release

        let feedDir = Path.Combine(root, "obj", "fuaran-cobuild-feed")
        let packages = Path.Combine(root, "obj", "fuaran-cobuild-packages")

        let scratch =
            let key =
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(root.ToLowerInvariant())
                )
                |> System.Convert.ToHexString

            Path.Combine(Path.GetTempPath(), "toolup-fuaran-cobuild-" + key.Substring(0, 12).ToLowerInvariant())

        // The tool resolver cache is machine-wide (`~/.dotnet/toolResolverCache`)
        // and records the path a local tool was restored to. Restored under this
        // leg's private NUGET_PACKAGES, the Fable entry pointed into a worktree's
        // `obj/`; once that worktree was removed, every `dotnet fable` on the
        // machine answered "Run dotnet tool restore" while restore kept reporting
        // success. The cache lives in the scratch directory instead, and goes
        // with it.
        let cliHome = Path.Combine(scratch, "cli-home")

        let run (workDir: string) (exe: string) (argv: string list) =
            let result =
                Proc.execWith
                    {
                        Proc.options with
                            Environment = [ "NUGET_PACKAGES", packages; "DOTNET_CLI_HOME", cliHome ]
                            Redirect = true
                    }
                    exe
                    argv
                    workDir

            let output = result.StdOut + result.StdErr
            printfn "%s" output

            if result.ExitCode <> 0 && FuaranCoResolution.CoBuild.isSubstrateMismatch output then
                Trace.traceError (
                    sprintf
                        "VerifyFuaranCoBuild: the FS0193 class — the fact tier at %s beside the published Fuaran.UI %s resolves a Fuaran substrate one side was not compiled against."
                        (toolUpVersion |> Option.defaultValue "this tree")
                        ui
                )

            result.ExitCode

        let toolUp =
            match toolUpVersion with
            | Some v -> v
            | None ->
                // The closure, walked from the two roots' ProjectReferences, so a
                // reference added to the fact tier is packed by the commit that
                // adds it.
                let closure =
                    let seen =
                        System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)

                    let rec walk (proj: string) =
                        if seen.Add proj then
                            let doc = System.Xml.Linq.XDocument.Load proj

                            for r in doc.Descendants() do
                                if r.Name.LocalName = "ProjectReference" then
                                    let inc = r.Attribute(System.Xml.Linq.XName.Get "Include")

                                    if not (isNull inc) then
                                        // An `Include` is spelt with backslashes in most of
                                        // the tree; MSBuild reads it on every OS, and so
                                        // must this walk (the gate ran only on Windows
                                        // before this line).
                                        let rel = inc.Value.Replace('\\', Path.DirectorySeparatorChar)
                                        walk (Path.GetFullPath(Path.Combine(Path.GetDirectoryName proj, rel)))

                    for r in fuaranCoBuildRoots do
                        walk (Path.GetFullPath(Path.Combine(root, r)))

                    List.ofSeq seen

                Shell.deleteDir feedDir
                Directory.ensure feedDir

                for proj in closure do
                    Trace.tracefn
                        "▶ VerifyFuaranCoBuild: packing %s @ %s"
                        (Path.GetRelativePath(root, proj))
                        fuaranCoBuildVersion

                    Proc.exec
                        "dotnet"
                        [
                            "pack"
                            proj
                            sprintf "-p:Version=%s" fuaranCoBuildVersion
                            "-o"
                            feedDir
                            "--nologo"
                        ]
                        root
                    |> Proc.ensure
                    |> ignore

                fuaranCoBuildVersion

        // Evict the throwaway version only; the folder is this checkout's own.
        Directory.ensure packages

        for dir in Directory.EnumerateDirectories(packages, "toolup.*") do
            Shell.deleteDir dir

        Shell.deleteDir scratch
        let serverDir = Path.Combine(scratch, "Server")
        let clientDir = Path.Combine(scratch, "Client")
        Directory.ensure serverDir
        Directory.ensure (Path.Combine(clientDir, ".config"))
        Directory.ensure cliHome

        let ownFeed =
            match toolUpVersion with
            | None -> Some(feedDir.Replace('\\', '/'))
            | Some _ -> None

        let fableVersion =
            let manifest =
                Path.Combine(root, "samples", "MinimalClient", ".config", "dotnet-tools.json")

            use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText manifest)
            doc.RootElement.GetProperty("tools").GetProperty("fable").GetProperty("version").GetString()

        File.WriteAllText(Path.Combine(scratch, "nuget.config"), FuaranCoResolution.CoBuild.nugetConfig ownFeed)

        File.WriteAllText(
            Path.Combine(serverDir, "CoBuild.Server.fsproj"),
            FuaranCoResolution.CoBuild.serverProject toolUp ui
        )

        File.WriteAllText(Path.Combine(serverDir, "Program.fs"), FuaranCoResolution.CoBuild.serverSource)

        File.WriteAllText(
            Path.Combine(clientDir, "CoBuild.Client.fsproj"),
            FuaranCoResolution.CoBuild.clientProject toolUp ui
        )

        File.WriteAllText(Path.Combine(clientDir, "App.fs"), FuaranCoResolution.CoBuild.clientSource)

        File.WriteAllText(
            Path.Combine(clientDir, ".config", "dotnet-tools.json"),
            FuaranCoResolution.CoBuild.toolManifest fableVersion
        )

        Trace.tracefn
            "▶ VerifyFuaranCoBuild: the fact tier at %s beside the published Fuaran.UI %s, in %s"
            toolUp
            ui
            scratch

        try
            Aggregate.runAll "VerifyFuaranCoBuild" "co-build leg" [
                Aggregate.leg "server: restore and build on .NET" (fun () ->
                    match run serverDir "dotnet" [ "restore"; "--nologo" ] with
                    | 0 -> run serverDir "dotnet" [ "build"; "--no-restore"; "--nologo"; "-v:q" ]
                    | code -> code)
                Aggregate.leg "server: render and encode a page" (fun () ->
                    run serverDir "dotnet" [ FuaranCoResolution.CoBuild.serverAssembly ])
                Aggregate.leg "client: restore and build on .NET" (fun () ->
                    match run clientDir "dotnet" [ "restore"; "--nologo" ] with
                    | 0 -> run clientDir "dotnet" [ "build"; "--no-restore"; "--nologo"; "-v:q" ]
                    | code -> code)
                Aggregate.leg "client: Fable compile" (fun () ->
                    match run clientDir "dotnet" [ "tool"; "restore" ] with
                    | 0 -> run clientDir "dotnet" [ "fable"; "-o"; "output"; "--noCache" ]
                    | code -> code)
                // Phase 1200 — the question Vite asks of this output, without a Vite: every
                // relative dynamic import a compiled package source spells names a file. A
                // consumer compiles the packed sources under fable_modules as `.fs.js` while
                // Fable spells `importValueDynamic` from the `--outDir` extension (`.js`),
                // so a package whose runtime file ships no `.js` shim (PublishedSmoke, the
                // packaging rule) fails here, as 0.38.0 failed its first feed consumer.
                Aggregate.leg "client: every on-demand import the packed sources spell resolves on disk" (fun () ->
                    let outputDir = Path.Combine(clientDir, "output")

                    match PublishedSmoke.unresolvedDynamicImportsIn outputDir with
                    | [] -> 0
                    | unresolved ->
                        Trace.traceError (PublishedSmoke.unresolvedDynamicImportsReport outputDir unresolved)
                        1)
            ]
        finally
            Shell.deleteDir scratch

    Target.create "VerifyFuaranCoBuild" (fun _ ->
        runVerifyFuaranCoBuild (Flags.value "--ui-version" flags) (Flags.value "--toolup-version" flags))

    // Phase 326 — the ToolUp.Sdk meta-manifest, emitted from the tree.
    //
    // `src/ToolUp.Sdk/build/ToolUp.Sdk.props` is the one-line
    // coordinated-bump path a consumer imports into their
    // `Directory.Packages.props`. A published package with no
    // `<PackageVersion>` entry there is NU1008 for that consumer, and the
    // file was hand-listed: when this phase measured it, it declared 57
    // of the 166 ids the `Publish` target pushes. Hand-maintaining a list
    // that must equal a glob is the defect, not the 109 omissions.
    //
    //   dotnet run -- GenerateSdkManifest           rewrite the generated
    //                                               region in place
    //   dotnet run -- GenerateSdkManifest --check   report drift, exit 1,
    //                                               mutate nothing
    //
    // The `--check` arm is what `publish-nuget.yml` runs before it packs,
    // so a release cannot ship a package the manifest does not advertise.
    // `SdkManifestTests` in the Build Expecto pack asserts the same
    // property from `VerifyAll`, over the same module — the two are one
    // implementation with two entry points, which is the only shape that
    // cannot itself drift.
    //
    // Hand-authored prose above the BEGIN GENERATED marker is preserved
    // verbatim by a rewrite; everything below it is derived.
    Target.create "GenerateSdkManifest" (fun _ ->
        let root = Path.getFullName "."
        // `System.` qualified deliberately: this file opens Fake.Core but
        // not System, so a bare `Environment` binds to Fake's module.
        let checkOnly = Flags.has "--check" flags
        let reconciliation = SdkManifest.reconcile root

        // A discovery that finds nothing would report a clean manifest
        // for the wrong reason — every expected id absent, so no id
        // missing. Refuse instead: the glob is broken, not the manifest.
        if List.isEmpty reconciliation.Expected then
            failwithf
                "GenerateSdkManifest: discovered NO packable project under %s/src. The Publish glob cannot be empty in this repo, so this is a broken discovery, not an empty manifest — fix SdkManifest.discover rather than trusting a clean result."
                root

        for exclusion in reconciliation.Excluded do
            Trace.tracefn "GenerateSdkManifest: excluding %s — %s" exclusion.PackageId exclusion.Rationale

        // The version groups first: both generated regions are projections
        // of `version-groups.json`, so a file the tree disagrees with is
        // refused before either is written. The props region is rewritten
        // here (outside `--check`) before its own agreement is judged.
        let groups =
            match SdkManifest.loadVersionGroups root with
            | Ok groups -> groups
            | Error e -> failwithf "GenerateSdkManifest: %s" e

        let propsPath = SdkManifest.buildPropsPath root

        if not checkOnly then
            let props = File.readAsString propsPath

            let rendered =
                SdkManifest.spliceRegion
                    SdkManifest.propsRegionBeginMarker
                    SdkManifest.propsRegionEndMarker
                    (SdkManifest.renderPropsRegion groups)
                    (props.Replace("\r\n", "\n"))

            if rendered <> props then
                File.writeString false propsPath rendered
                Trace.tracefn "GenerateSdkManifest: rewrote the version-group region of %s." propsPath

        match SdkManifest.groupFindings root with
        | [] ->
            Trace.tracefn
                "GenerateSdkManifest: %d version group(s) place every packable project exactly once."
                (List.length groups)
        | findings ->
            failwithf
                "GenerateSdkManifest: the version groups do not describe this tree:\n  - %s"
                (String.concat "\n  - " findings)

        if
            checkOnly
            && not (SdkManifest.manifestIsCurrent root)
            && SdkManifest.describe reconciliation = None
        then
            failwithf
                "GenerateSdkManifest --check: %s lists the right ids but is not the render of version-groups.json (a package moved group, or a family's version moved). Regenerate with: %s"
                (SdkManifest.manifestPath root)
                SdkManifest.regenerateCommand

        match SdkManifest.describe reconciliation, checkOnly with
        | None, _ ->
            Trace.tracefn
                "GenerateSdkManifest: the manifest lists exactly the %d published package id(s) (%d excluded by shape)."
                (List.length reconciliation.Expected)
                (List.length reconciliation.Excluded)

            if not checkOnly then
                File.writeString false (SdkManifest.manifestPath root) (SdkManifest.render root)
                Trace.tracefn "GenerateSdkManifest: rewrote the generated region anyway (formatting is derived too)."
        | Some report, true -> failwithf "GenerateSdkManifest --check: %s" report
        | Some report, false ->
            Trace.tracefn "%s" report
            File.writeString false (SdkManifest.manifestPath root) (SdkManifest.render root)

            Trace.tracefn
                "GenerateSdkManifest: wrote %s with %d entry/entries."
                (SdkManifest.manifestPath root)
                (List.length reconciliation.Expected))

    // Phase 72 — template-pack packaging.
    //
    // The standard Pack target (in ToolUp.Platform.Build) walks
    // `src/**/*.fsproj` and packs each SDK fsproj as a NuGet assembly
    // package. Templates live under `templates/` and need their own
    // packaging shape (`<PackageType>Template</PackageType>` + content-
    // only packing), so they don't fit the standard Pack glob.
    //
    // `PackTemplates` packs each template-pack csproj into
    // ../local-nuget-feed for consumers to `dotnet new install
    // <PackageId>` from the same feed they consume the SDK from.
    //
    // Single-template-per-package convention: each template ships as
    // its own NuGet (e.g. ToolUp.Templates.SAFER ships only
    // `toolup-safer`). Add new template-packs by extending the
    // `templatePackProjects` glob.
    Target.create "PackTemplates" (fun _ ->
        let outputDir = Path.getFullName "../../local-nuget-feed"
        Directory.ensure outputDir

        let templatePackProjects = !!"templates/**/ToolUp.Templates.*.fsproj"

        for proj in templatePackProjects do
            Trace.tracefn "Packing template-pack %s..." proj

            Proc.exec "dotnet" [ "pack"; proj; "-c"; "Release"; "-o"; outputDir; "--nologo" ] "."
            |> Proc.ensure
            |> ignore)

    // Phase 167 — pack the `toolup` CLI as a tool-manifest-installable
    // `dotnet tool` into the shared local feed. The standard `Pack`
    // target (in ToolUp.Platform.Build) already walks `src/**/*.fsproj`
    // and picks up ToolUp.Cli too — `PackCli` is the fast single-package
    // path for iterating on the CLI and then `dotnet tool install
    // --add-source ../../local-nuget-feed ToolUp.Cli`.
    //
    // Usage: `dotnet run -- PackCli`
    Target.create "PackCli" (fun _ ->
        let outputDir = Path.getFullName "../../local-nuget-feed"
        Directory.ensure outputDir

        Proc.exec
            "dotnet"
            [
                "pack"
                "src/ToolUp.Cli/ToolUp.Cli.fsproj"
                "-c"
                "Release"
                "-o"
                outputDir
                "--nologo"
            ]
            "."
        |> Proc.ensure
        |> ignore)

    // Phase 626 — unreferenced-definition report.
    //
    // Nothing in this repo detected a definition with no call sites, and
    // the compiler does not fill the gap: `--warnon:1182` fires only for
    // unused LOCAL bindings, so a module-level `let private` with zero
    // callers is silent. That is how `DatadogLogsAuditSink`'s
    // `extractEventScopeId` drifted 51 of 132 match arms behind without
    // anyone noticing — nothing called it, so nothing failed.
    //
    // A REPORT, not a gate, and that is a deliberate choice rather than
    // a staging post: the tool cannot distinguish "dead" from "not yet
    // used", and in this SDK both are legitimate — a seam shipped ahead
    // of its first implementor is exactly the shape it would flag. A
    // gate makes keeping such a thing cost a suppression mechanism, and
    // the pressure then runs toward deleting seams to get green. Pass
    // `--fail-on-dead` to gate anyway; see tools/ToolUp.DeadCode/README.md
    // for the promotion criteria and the analysis's documented limits.
    //
    // Usage: `dotnet run --project Build.fsproj -- DeadCodeReport`
    Target.create "DeadCodeReport" (fun _ ->
        let passthrough =
            [ "--fail-on-dead"; "--verbose"; "--json" ]
            |> List.filter (fun flag -> Flags.has flag flags)

        let toolArgs =
            [ "run"; "--project"; "tools/ToolUp.DeadCode"; "--" ]
            @ [ "--repo-root"; __SOURCE_DIRECTORY__ ]
            @ passthrough

        Proc.exec "dotnet" toolArgs __SOURCE_DIRECTORY__ |> Proc.ensure |> ignore)

    // Phase 213 — Lighthouse / Core-Web-Vitals budget gate. The deciding
    // half only: it reads a committed budget file plus the Lighthouse
    // JSON reports a run already produced, and fails on any breach. The
    // measuring half (build the sample site, serve it on a throwaway
    // port, drive Lighthouse over the budgeted page set) is
    // dev-scripts/cwv-budget-gate.ps1, which sets TOOLUP_CWV_BUDGET /
    // TOOLUP_CWV_REPORTS and invokes this target last.
    //
    // Registration is unconditional and reads no environment at startup,
    // so every other target stays runnable with none of the variables
    // set; the target body resolves and reports its own missing inputs.
    CoreWebVitalsBudgetGate.registerTarget ()

    // Phase 192 — cold-start / hot-path perf-budget gate. Same split as
    // the Core-Web-Vitals gate directly above, for the same reason: this
    // is the deciding half only. It reads the committed perf-budgets.json
    // plus the measurement file a run already wrote, and fails on any
    // breach — including a budgeted metric the run did not measure and a
    // measurement the runner could not confirm measured anything. The
    // measuring half (build samples/MinimalApp, boot it repeatedly to its
    // ready line, drive the anonymous hot-path request) is
    // dev-scripts/perf-budget-gate.ps1, which sets TOOLUP_PERF_BUDGET /
    // TOOLUP_PERF_MEASUREMENTS and invokes this target last.
    //
    // Registration is unconditional and reads no environment at startup,
    // so every other target stays runnable with neither variable set; the
    // target body resolves and reports its own missing inputs.
    PerfBudgetGate.registerTarget ()

    // The two instantiating template gates. Their rationale and their specs
    // are `TemplateGatePackages.verifyPackagedModuleTemplate` and
    // `.verifyDataPlatformTemplate`; the mechanism is `TemplateGate.run`.
    Target.create "VerifyPackagedModuleTemplate" (fun _ ->
        TemplateGate.run TemplateGatePackages.verifyPackagedModuleTemplate)

    Target.create "VerifyDataPlatformTemplate" (fun _ ->
        TemplateGate.run TemplateGatePackages.verifyDataPlatformTemplate)

    // ─── Phase 184 — the fresh-machine published-package smoke gate ──
    //
    // "A fresh machine can `dotnet add package` the SDK and build it"
    // was a Wave-5.5 exit criterion, run ONCE by hand and never again.
    // Every other gate in this repo proves something about the TREE; a
    // nupkg is a different artefact, and the classes that only appear in
    // it — a transitive `PackageReference` that a same-repo project
    // reference silently supplies, a `fable/`-packed source path that
    // stops extracting — surface first in a consumer's install.
    //
    // Usage:
    //   dotnet run --project Build.fsproj -- VerifyPublishedPackages
    //   TOOLUP_PUBLISHED_REF=v0.22.0 dotnet run ... (probe a past release)
    //
    // ── Deliberately NOT in `TestPacks` and NOT in `verify.ps1` ──────
    //
    // It needs the network and it needs the version under test to be
    // PUBLISHED, so on a developer's checkout — where `<Version>` is by
    // construction the next, unreleased number — the honest answer is
    // "nothing to probe yet". A gate that is red on a fresh clone for a
    // reason nobody can fix is one people learn to skip. It runs on the
    // release tag, where the version exists, and its offline-decidable
    // rules live in `PublishedSmoke.fs` where the Build pack gates them
    // on every push. The `VerifyBrowserSmoke` precedent: outside the
    // default gate by CONSTRUCTION, not by convention.
    //
    // ── Why the target owns the mechanism ────────────────────────────
    //
    // The `VerifyFable` precedent, stated twice in `checks.yml`: the
    // workflow job is a thin `dotnet run`, so CI and a developer
    // necessarily run the same thing. Here it also protects the gate's
    // one load-bearing property — that the probe is scaffolded OUTSIDE
    // this repository — which workflow shell steps in the checkout would
    // silently lose. `PublishedSmoke.smokeJobFindings` lints the workflow
    // for exactly that bypass.
    let runVerifyPublishedPackages () =
        let repoRoot = Path.getFullName "."

        // `diagnose` reads a failed step's captured output and returns an
        // account to print beside it ("" for none) — the restore steps use
        // it to say which ToolUp id missed and why (Phase 1004).
        let runCheckedInWith (diagnose: string -> string) label exe args dir =
            Trace.tracefn "▶ VerifyPublishedPackages: %s — %s %s" label exe (String.concat " " args)
            let r = Proc.redirected exe args dir

            if r.ExitCode <> 0 then
                let output = r.StdOut + r.StdErr
                printfn "%s" output

                match diagnose output with
                | "" -> ()
                | account -> printfn "%s" account

                failwithf
                    "VerifyPublishedPackages: %s failed — `%s %s` exited %d in %s. This is the fresh-machine probe: the packages restored from nuget.org do not %s. Read the captured output above; it is a consumer's first experience of this release."
                    label
                    exe
                    (String.concat " " args)
                    r.ExitCode
                    dir
                    label

        let runCheckedIn = runCheckedInWith (fun _ -> "")

        // ── 1. Which version ─────────────────────────────────────────
        //
        // Read off the tag that triggered the run, so no copy of the
        // number can drift from the one the publish job just pushed.
        // `TOOLUP_PUBLISHED_REF` lets a developer aim the probe at a past
        // release; `GITHUB_REF` is only consulted when it IS a tag, so a
        // `workflow_dispatch` falls through to `<Version>` — which is
        // what a dispatch packs.
        let tagRef =
            [ "TOOLUP_PUBLISHED_REF"; "GITHUB_REF" ]
            |> List.tryPick (fun name ->
                match Environment.environVarOrNone name with
                | Some v when not (System.String.IsNullOrWhiteSpace v) -> Some(v.Trim())
                | _ -> None)
            |> Option.filter (fun r -> r.StartsWith "refs/tags/" || r.StartsWith "v")

        let propsText = File.ReadAllText(Path.Combine(repoRoot, "Directory.Build.props"))

        let version =
            match PublishedSmoke.versionUnderTest tagRef propsText with
            | Ok v -> v
            | Error message -> failwith message

        Trace.tracefn
            "▶ VerifyPublishedPackages: probing %s (from %s)"
            version
            (match tagRef with
             | Some r -> "ref " + r
             | None -> "Directory.Build.props <Version>")

        // ── 2. Every probe id is one this repo actually publishes ────
        //
        // Reconciled against `SdkManifest.expected`, which COMPUTES the
        // packable set from the same glob `Publish` pushes — never a
        // second hand-written list. The shard that filed this phase named
        // `ToolUp.AI`, which no project here produces and nuget.org does
        // not serve; that is the stranger's-package class one level up
        // and this is the guard for it.
        match PublishedSmoke.unpublishedProbeIds (SdkManifest.expected repoRoot) PublishedSmoke.probeIds with
        | [] -> ()
        | unpublished -> failwith (PublishedSmoke.unpublishedProbeReport unpublished)

        // ── 3. Wait for nuget.org to INDEX the push ──────────────────
        //
        // A push is accepted before it is served. Probing immediately
        // records a false red about a release that is fine (the Phase 255
        // lesson), so this waits, and says how long it waited when it
        // gives up rather than claiming the package does not exist.
        let timeout =
            match Environment.environVarOrNone "TOOLUP_PUBLISHED_INDEX_TIMEOUT_MINUTES" with
            | Some v ->
                match System.Double.TryParse v with
                | true, m when m > 0.0 -> System.TimeSpan.FromMinutes m
                | _ ->
                    failwithf
                        "VerifyPublishedPackages: TOOLUP_PUBLISHED_INDEX_TIMEOUT_MINUTES=%s is not a positive number."
                        v
            | None -> System.TimeSpan.FromMinutes 20.0

        use http =
            new System.Net.Http.HttpClient(Timeout = System.TimeSpan.FromSeconds 30.0)

        let fetchIndex (packageId: string) =
            try
                block (http.GetStringAsync(PublishedSmoke.flatContainerIndexUrl packageId))
            with e ->
                // A transient 404 IS the expected pre-index answer
                // and the storage layer returns it as a throwing
                // status, so this is a poll result, not a failure.
                sprintf "(request failed: %s)" e.Message

        // The CLOSURE the restore resolves, not only the ids the probe
        // names (Phase 1004): a restore pulls the probes' transitive
        // `ToolUp.*` dependencies at this version too, and nuget.org
        // indexes each push independently. `v0.25.1` failed NU1102 on
        // `ToolUp.Feliz.AgGrid`, `v0.26.0` on `ToolUp.Platform.Transport`
        // — neither a probe id, both fine minutes later. One deadline over
        // the whole set, so the budget is the same as before however many
        // packages it covers.
        let awaited = PublishedSmoke.awaitedIdsIn repoRoot

        Trace.tracefn
            "▶ VerifyPublishedPackages: awaiting %d package(s) at %s — every lockstep package the release publishes (the %d probe ids and their closure)"
            (List.length awaited)
            version
            (List.length PublishedSmoke.probeIds)

        let started = System.DateTime.UtcNow
        let mutable pending = awaited
        let mutable lastBodies = Map.empty

        while not pending.IsEmpty && System.DateTime.UtcNow - started < timeout do
            let bodies = pending |> List.map (fun id -> id, fetchIndex id)
            lastBodies <- bodies |> List.fold (fun m (id, body) -> Map.add id body m) lastBodies
            let stillPending = PublishedSmoke.stillPending version bodies

            for id in pending |> List.except stillPending do
                Trace.tracefn "▶ VerifyPublishedPackages: nuget.org serves %s %s" id version

            pending <- stillPending

            if not pending.IsEmpty then
                Trace.tracefn
                    "  … %d package(s) not indexed yet at %s (%s); waiting"
                    (List.length pending)
                    version
                    (String.concat ", " pending)

                System.Threading.Thread.Sleep(System.TimeSpan.FromSeconds 30.0)

        if not pending.IsEmpty then
            failwith (
                PublishedSmoke.indexTimeoutReports
                    version
                    (List.length awaited)
                    (System.DateTime.UtcNow - started)
                    (pending
                     |> List.map (fun id -> id, Map.tryFind id lastBodies |> Option.defaultValue ""))
            )

        // A restore that still misses a `ToolUp.*` id after the wait says
        // which, and whether the index serves it NOW — which separates
        // indexing lag from a package the release never pushed.
        let diagnoseRestore (output: string) =
            match PublishedSmoke.restoreMisses output with
            | [] -> ""
            | misses ->
                PublishedSmoke.restoreMissReport
                    version
                    awaited
                    (misses
                     |> List.map (fun id -> id, PublishedSmoke.indexServes version (fetchIndex id)))

        let runRestoreIn label dir =
            runCheckedInWith diagnoseRestore label "dotnet" [ "restore"; "--nologo" ] dir

        // ── 4. Scaffold OUTSIDE the repository ───────────────────────
        //
        // nuget.config files merge UP the directory tree and MSBuild
        // walks `Directory.Build.props` upward, so a probe under the
        // checkout would resolve through forge's own sources — including
        // the workspace-shared local feed — and go green over a package
        // set no consumer can restore. A short temp path also keeps
        // Windows MAX_PATH away from fsc, which fails by emitting no dll
        // and no error.
        let probeRoot = Scratch.root "tu-pubsmoke"

        if not (PublishedSmoke.isOutsideRepo repoRoot probeRoot) then
            failwith (PublishedSmoke.insideRepoReport repoRoot probeRoot)

        Shell.deleteDir probeRoot
        Directory.ensure probeRoot
        File.WriteAllText(Path.Combine(probeRoot, "nuget.config"), PublishedSmoke.nugetConfig)

        // The repo's own SDK band, so the probe compiles under the same
        // toolchain the release was built with. Copied rather than
        // restated: one declared version.
        File.Copy(Path.Combine(repoRoot, "global.json"), Path.Combine(probeRoot, "global.json"))

        let writeProbe (name: string) (projectText: string) (namespaces: string list) =
            let dir = Path.Combine(probeRoot, name)
            Directory.ensure dir
            File.WriteAllText(Path.Combine(dir, "Probe.fsproj"), projectText)
            File.WriteAllText(Path.Combine(dir, "Probe.fs"), PublishedSmoke.probeSource namespaces)
            dir

        // ── 5. DLL tier: restore + build ─────────────────────────────
        //
        // The transitive-dependency probe. A package whose nuspec omits a
        // dependency that a project reference used to supply restores
        // fine in this repo and fails here, which is the whole point.
        let dllDir =
            writeProbe "dll" (PublishedSmoke.dllProbeProject version) PublishedSmoke.dllProbeNamespaces

        runRestoreIn "restore the published packages" dllDir
        runCheckedIn "build against the published packages" "dotnet" [ "build"; "--no-restore"; "--nologo" ] dllDir

        // ── 6. Fable tier: the packed SOURCE, not the DLL ────────────
        //
        // `ToolUp.Platform.Client` ships its source under `fable/` and
        // Fable compiles that, so this is the only check that the packed
        // layout extracts and transpiles. It catches a server-only file
        // leaking into the Fable set and a `PackagePath` regression that
        // a DLL-only build cannot see.
        let fableDir =
            writeProbe "fable" (PublishedSmoke.fableProbeProject version) PublishedSmoke.fableProbeNamespaces

        // The Fable tool version is the repo's own, copied from the
        // client-tier harness's manifest rather than restated here: a
        // second pinned version would drift from the compiler the tree is
        // actually verified against.
        let toolManifestSource =
            Path.Combine(repoRoot, "src/ToolUp.AI.Client.Tests/.config/dotnet-tools.json")

        if not (File.Exists toolManifestSource) then
            failwithf
                "VerifyPublishedPackages: no Fable tool manifest at %s to copy. The probe pins the same Fable version the tree is verified against rather than restating one; re-point this path if the client-tier harness moved."
                toolManifestSource

        Directory.ensure (Path.Combine(fableDir, ".config"))
        File.Copy(toolManifestSource, Path.Combine(fableDir, ".config", "dotnet-tools.json"))

        runCheckedIn "restore the Fable tool" "dotnet" [ "tool"; "restore" ] fableDir
        runRestoreIn "restore the published client tier" fableDir

        runCheckedIn "transpile the packed fable/ sources" "dotnet" [ "fable"; "-o"; "output"; "--noCache" ] fableDir

        // Fable exits 0 having written nothing if it matched no sources,
        // the same vacuous-green shape `VerifyFable` guards with its TAP
        // counts and `VerifyDocSnippets` with its extraction floor.
        let emitted =
            let outputDir = Path.Combine(fableDir, "output")

            if Directory.Exists outputDir then
                Directory.GetFiles(outputDir, "*.js", SearchOption.AllDirectories).Length
            else
                0

        if emitted = 0 then
            failwithf
                "VerifyPublishedPackages: `dotnet fable` exited 0 but wrote no .js under %s/output. Fable exits 0 when it matched no source, so an empty output is indistinguishable from a pass by exit status alone — it means the published package's `fable/` payload did not extract."
                fableDir

        Trace.tracefn
            "▶ VerifyPublishedPackages: %s installs and compiles from nuget.org on a clean resolve path (%d .js emitted from the packed client tier)"
            version
            emitted

    Target.create "VerifyPublishedPackages" (fun _ -> runVerifyPublishedPackages ())

    // ── Phase 260 — the release bump, derived from the api-baselines diff ──
    //
    // At 1.0 the version becomes a promise rather than a label, and the
    // way that promise gets broken is not malice — it is a hand-typed
    // number decided at the end of a long release day. This target
    // computes what the surface actually did since the last release and
    // refuses a version that claims less than that.
    //
    //   dotnet run --project Build.fsproj -- VerifySemVerBump
    //   dotnet run --project Build.fsproj -- VerifySemVerBump --propose
    //
    // The default fails on an under-bump; `--propose` only reports, which
    // is the mode for deciding a number rather than defending one.
    //
    // WHAT IT DIFFS, and why it is not what the phase shard asked for.
    // The shard (2026-06-27) asked for the working surface against the
    // approved baselines. Phase 618 has since made the approval gate fail
    // in BOTH directions, so those two are identical on every tree that
    // passes `verify.ps1` — a bump derived from that difference would say
    // "unchanged" forever and this check, which only fails on an
    // under-bump, could never fire. The measurement that survives 618 is
    // between RELEASES: the committed baselines at the last release tag
    // against the working tree. See `SemVerBump.fs` for why 618 is what
    // makes that faithful rather than approximate.
    //
    // It is registered here and NOT added to `verify.ps1`. That script is
    // a hard-denied path on the roadmap side (its hash is recorded gate
    // evidence), and the question this asks — "is the number you are
    // about to tag big enough?" — is a release question, not a
    // per-commit one. The pure decision logic is exercised on every
    // commit by the Build test pack; the live reading of git runs at
    // release, from `publish-nuget.yml`, beside Phase 326's manifest
    // check and for the same reason: it is the moment the two facts meet.
    // The per-package surface classification between a tag and the
    // WORKING tree — VerifySemVerBump's measurement, shared since Phase
    // 263 with the release-candidate soak gate, which takes the same
    // reading from a candidate tag. One reader, so the bump check and the
    // soak gate cannot disagree about whether the surface moved.
    //
    // Only the baselines that MOVED need reading: a blob identical on
    // both sides contributes an unchanged classification by definition,
    // and this keeps the cost proportional to the churn rather than to
    // the ~165-package baseline set. `Git.read` does not echo, because the
    // read is one `git show` per moved baseline, and echoing each would
    // bury the classification the caller exists to print.
    let surfaceChangesSince (root: string) (tag: string) : SemVerBump.PackageChange list =
        let changedPaths =
            Git.read root [ "diff"; "--name-only"; tag; "--"; "api-baselines" ]
            |> Lines.ofOutput

        let pathsAtTag =
            Git.read root [ "ls-tree"; "-r"; "--name-only"; tag; "--"; "api-baselines" ]
            |> Lines.ofOutput
            |> Set.ofList

        changedPaths
        |> List.map (fun path ->
            let taggedText =
                if pathsAtTag.Contains path then
                    Some(Git.read root [ "show"; sprintf "%s:%s" tag path ])
                else
                    None

            SemVerBump.classifyPackage
                (SemVerBump.packageOfBaselinePath path)
                taggedText
                (SemVerBump.currentBaseline root path))

    // What a release tag's project files said, read in two git calls rather
    // than one per project: the paths, then every `<AssemblyName>` and
    // `<Version>` line among them. Feeds the per-group bump check, which must
    // place a package withdrawn since the tag and must know which projects
    // declared their own version before version groups existed.
    let projectFactsAt (root: string) (tag: string) : SdkManifest.ProjectFacts list =
        let paths =
            Git.read root [ "ls-tree"; "-r"; "--name-only"; tag; "--"; "src" ]
            |> Lines.ofOutput
            |> List.filter (fun p -> p.EndsWith ".fsproj")

        let grep =
            Git.run root [ "grep"; "-E"; "<(AssemblyName|Version)>"; tag; "--"; "src/*.fsproj" ]

        if grep.ExitCode > 1 then
            failwithf "git grep over %s's project files failed (exit %d)." tag grep.ExitCode

        let tagged =
            Lines.ofOutput grep.StdOut
            |> List.choose (fun line ->
                // `<tag>:<path>:<line>`
                let rest = line.Substring(tag.Length + 1)

                match rest.IndexOf ':' with
                | -1 -> None
                | i ->
                    let m =
                        System.Text.RegularExpressions.Regex.Match(
                            rest.Substring(i + 1),
                            @"<(AssemblyName|Version)>\s*([^<\s][^<]*?)\s*</"
                        )

                    if m.Success then
                        Some(rest.Substring(0, i), m.Groups[1].Value, m.Groups[2].Value)
                    else
                        None)

        paths
        |> List.map (fun path ->
            let valueOf tagName =
                tagged
                |> List.tryPick (fun (p, t, v) -> if p = path && t = tagName then Some v else None)

            {
                SdkManifest.ProjectFacts.Path = path
                SdkManifest.ProjectFacts.AssemblyName =
                    defaultArg (valueOf "AssemblyName") (Path.GetFileNameWithoutExtension path)
                SdkManifest.ProjectFacts.OwnVersion = valueOf "Version"
            })

    // `version-groups.json` as a tag recorded it; `None` before groups existed.
    let versionGroupsAt (root: string) (tag: string) : string option =
        let spec = sprintf "%s:%s" tag SdkManifest.versionGroupsFileName

        if (Git.run root [ "cat-file"; "-e"; spec ]).ExitCode = 0 then
            Some(Git.read root [ "show"; spec ])
        else
            None

    let runVerifySemVerBump () =
        let root = Path.getFullName "."
        let proposeOnly = Flags.has "--propose" flags


        let propsPath = Path.Combine(root, "Directory.Build.props")

        let declared =
            match SemVerBump.declaredVersionIn (System.IO.File.ReadAllText propsPath) with
            | Ok v -> v
            | Error e -> failwithf "VerifySemVerBump: cannot read the declared SDK version — %s (%s)." e propsPath

        // The release point. `TOOLUP_SEMVER_BASE` names a tag to measure
        // from explicitly; its NAME must parse as a version, because the
        // released version is what selects the 0.x-versus-1.x policy
        // table and what the declared version's advance is measured
        // against. Without it, the newest tag STRICTLY BELOW the declared
        // version is used — see `SemVerBump.releasePoint` for why "below"
        // rather than "newest": on the publish workflow the tag being
        // released already exists.
        let tags =
            match System.Environment.GetEnvironmentVariable "TOOLUP_SEMVER_BASE" with
            | null
            | "" -> ReleaseChannel.Tags.released root
            | explicitBase -> [ explicitBase ]

        match SemVerBump.releasesAhead declared tags with
        | [] -> ()
        | ahead -> failwithf "%s" (SemVerBump.behindReleaseReport declared ahead)

        let releaseTag, released =
            match SemVerBump.releasePoint declared tags with
            | Some pair -> pair
            | None -> failwithf "%s" (SemVerBump.noReleasePointReport declared (List.length tags))

        // A tag name that parses as a version but names no commit is the
        // TOOLUP_SEMVER_BASE typo path, and it is the only way to reach
        // it. Answering it here costs one process and turns a raw
        // non-zero from the first `git diff` into a sentence.
        if
            (Git.run root [ "rev-parse"; "--verify"; "--quiet"; releaseTag + "^{commit}" ]).ExitCode
            <> 0
        then
            failwithf
                "VerifySemVerBump: '%s' parses as a version but names no commit in this checkout. If it came from TOOLUP_SEMVER_BASE, check the spelling; otherwise the tag is unfetched — `git fetch --tags --force`."
                releaseTag

        let changes = surfaceChangesSince root releaseTag

        // Per version group (`version-groups.json`): each line's packages
        // decide that line's floor. A group the file cannot place, or a tree
        // the groups do not describe, is refused before anything is measured.
        let groups =
            match SdkManifest.loadVersionGroups root with
            | Ok groups -> groups
            | Error e -> failwithf "VerifySemVerBump: %s" e

        match SdkManifest.groupFindings root with
        | [] -> ()
        | findings ->
            failwithf
                "VerifySemVerBump: the version groups do not describe this tree:\n  - %s\n\nRegenerate with: %s"
                (String.concat "\n  - " findings)
                SdkManifest.regenerateCommand

        let core = (SdkManifest.coreGroup groups).Value
        let projects = SdkManifest.discover root
        let atTag = projectFactsAt root releaseTag

        // A baseline traces to its project through the assembly name. The
        // tag's projects are read too, so a package withdrawn since the tag
        // still lands on its line; one nothing places is counted to the core,
        // the line whose promise is widest.
        let groupOfPackage =
            let byAssembly =
                SdkManifest.groupsByAssembly
                    groups
                    (Seq.append
                        (atTag |> Seq.map (fun f -> f.Path, f.AssemblyName))
                        (projects |> Seq.map (fun p -> p.ProjectPath, p.AssemblyName)))

            fun (package: string) -> byAssembly.TryFind package |> Option.defaultValue core

        let groupsAtTag =
            match versionGroupsAt root releaseTag with
            | None -> None
            | Some text ->
                match SdkManifest.parseVersionGroups text with
                | Ok g -> Some g
                | Error e -> failwithf "VerifySemVerBump: %s at %s: %s" SdkManifest.versionGroupsFileName releaseTag e

        let ownAtTag = atTag |> List.map (fun f -> f.Path, f.OwnVersion) |> Map.ofList

        let dependencies = SdkManifest.groupDependencies groups projects

        let inputs =
            groups
            |> List.map (fun g ->
                let declaredHere =
                    if g.Lockstep then
                        declared
                    else
                        match SemVerBump.parseVersion g.Version with
                        | Ok v -> v
                        | Error e -> failwithf "VerifySemVerBump: group `%s`: %s" g.Id e

                let members =
                    projects
                    |> List.filter (fun p -> SdkManifest.groupOf groups p.ProjectPath = Some g)
                    |> List.map (fun p -> ownAtTag.TryFind p.ProjectPath |> Option.flatten)

                {
                    SemVerBump.GroupInput.Group = g.Id
                    SemVerBump.GroupInput.Lockstep = g.Lockstep
                    SemVerBump.GroupInput.Released = SemVerBump.releasedGroupVersion released groupsAtTag g.Id members
                    SemVerBump.GroupInput.Declared = declaredHere
                    SemVerBump.GroupInput.Changes =
                        changes |> List.filter (fun c -> (groupOfPackage c.Package).Id = g.Id)
                    SemVerBump.GroupInput.DependsOn = defaultArg (dependencies.TryFind g.Id) []
                })

        let assessments = SemVerBump.assessGroups releaseTag inputs

        for a in assessments do
            Trace.tracefn "%s" (SemVerBump.describeGroup a)

        match assessments |> List.choose SemVerBump.groupDefect, proposeOnly with
        | [], _ ->
            Trace.tracefn
                "▶ VerifySemVerBump: every one of %d version group(s) meets the bump its own surface diff since %s demands."
                assessments.Length
                releaseTag
        | reports, true ->
            Trace.traceImportantfn
                "VerifySemVerBump --propose: %d group(s) under-bumped:\n\n%s\n\n(--propose reports only; the same run without it FAILS.)"
                reports.Length
                (String.concat "\n\n" reports)
        | reports, false ->
            failwithf "VerifySemVerBump: %d group(s) under-bumped:\n\n%s" reports.Length (String.concat "\n\n" reports)

    Target.create "VerifySemVerBump" (fun _ -> runVerifySemVerBump ())


    // Phase 805 — register the generated files' three-way merge drivers in
    // THIS CLONE's `.git/config`.
    //
    // `.gitattributes` can name a driver per path but cannot define what it
    // runs: git reads `merge.<name>.driver` from the config only, because a
    // repository must not be able to hand an arbitrary command to everyone
    // who clones it. So the registration is a per-clone act, and this is it.
    // A clone that never runs Setup resolves the name to nothing and gets
    // git's built-in text merge — today's plain conflict, unchanged (GP 11).
    //
    // The config is SHARED by every worktree of the clone, so the script it
    // names must outlive any one of them. It is resolved through the clone's
    // common directory (`git rev-parse --git-common-dir`, whose parent is
    // the primary checkout), never through the checkout that ran Setup: a
    // registration written from a worktree that was later removed sent every
    // baseline merge in every worktree to a script that no longer existed,
    // and git reported each one as a conflict.
    //
    // Usage: `dotnet run --project Build.fsproj -- Setup`
    //        `dotnet run --project Build.fsproj -- Setup --check` writes
    //        nothing, and fails naming every driver that is unregistered or
    //        registered to a shell or script that does not exist.

    /// Driver name, `merge-baselines.ps1` mode, and the `merge.<name>.name`
    /// git shows for it.
    let mergeDrivers = [
        "toolup-members", "members", "ToolUp members-baseline three-way merge"
        "toolup-coverage", "coverage", "ToolUp coverage-baseline three-way merge"
        "toolup-draftnote", "draftnote", "ToolUp append-only draft-note merge"
    ]

    /// The two absolute paths a registered command names: the quoted shell
    /// it opens with, and the script after `-File`. The shape is the one
    /// `Setup` writes; anything else names no path this target can check.
    let driverPathsOf (command: string) =
        let shell = System.Text.RegularExpressions.Regex.Match(command, "^\"([^\"]+)\"")

        let script =
            System.Text.RegularExpressions.Regex.Match(command, "-File\\s+\"([^\"]+)\"")

        (if shell.Success then Some shell.Groups[1].Value else None),
        (if script.Success then Some script.Groups[1].Value else None)

    let unregistered = "it is not registered"

    /// Every expected driver with what is wrong with its registration in the
    /// clone `root` belongs to, or `None` when it names a shell and a script
    /// that both exist.
    let mergeDriverProblems (root: string) =
        mergeDrivers
        |> List.map (fun (name, _, _) ->
            let registered = Git.run root [ "config"; "--get"; sprintf "merge.%s.driver" name ]

            match registered.ExitCode, registered.StdOut.Trim() with
            | 0, command when command <> "" ->
                let problem =
                    match driverPathsOf command with
                    | Some shell, Some script ->
                        match
                            [
                                if not (File.Exists shell) then
                                    sprintf "its shell %s does not exist" shell
                                if not (File.Exists script) then
                                    sprintf "its script %s does not exist" script
                            ]
                        with
                        | [] -> None
                        | missing -> Some(String.concat " and " missing)
                    | _ -> Some(sprintf "its command names no shell and `-File` script Setup would write: %s" command)

                name, problem
            | _ -> name, Some unregistered)

    Target.create "Setup" (fun _ ->
        let root = Path.getFullName "."

        if Flags.has "--check" flags then
            let problems = mergeDriverProblems root

            for name, problem in problems do
                match problem with
                | None -> Trace.tracefn "  ok      merge.%s.driver" name
                | Some p -> Trace.traceError (sprintf "  NOT OK  merge.%s.driver — %s" name p)

            match problems |> List.filter (snd >> Option.isSome) with
            | [] ->
                Trace.tracefn "▶ Setup --check: every merge driver is registered to a shell and a script that exist."
            | dead ->
                failwithf
                    "Setup --check: %d merge driver(s) unregistered or registered to a missing path, named above. Run `dotnet run --project Build.fsproj -- Setup` (from any checkout of this clone: every one resolves the same script)."
                    dead.Length
        else
            let commonDir =
                let r = Git.run root [ "rev-parse"; "--path-format=absolute"; "--git-common-dir" ]

                match r.ExitCode, r.StdOut.Trim() with
                | 0, dir when dir <> "" -> dir
                | code, _ -> failwithf "Setup: `git rev-parse --git-common-dir` exited %d in %s." code root

            let primary = Path.GetDirectoryName(commonDir.TrimEnd('/', '\\'))
            let driver = Path.Combine(primary, "dev-scripts", "merge-baselines.ps1")

            if not (File.Exists driver) then
                failwithf
                    "Setup: %s is missing. The drivers are registered to the PRIMARY checkout's script (the parent of the clone's common directory, %s), because the config is shared by every worktree and a worktree can be removed. Check the script out there, then re-run."
                    driver
                    commonDir

            // A primary checkout behind this one runs an older driver for
            // every worktree; say so rather than register silently.
            let here = Path.Combine(root, "dev-scripts", "merge-baselines.ps1")

            if
                Path.GetFullPath here <> Path.GetFullPath driver
                && File.Exists here
                && File.ReadAllText here <> File.ReadAllText driver
            then
                Trace.traceImportant (
                    sprintf
                        "Setup: %s differs from this checkout's copy. Every worktree of the clone runs the primary checkout's script; pull the primary checkout so it runs the driver you expect."
                        driver
                )

            // Resolved to an absolute path rather than left as a bare `pwsh`:
            // git runs a merge driver through a shell whose PATH is not
            // necessarily the one this target was launched with.
            let shell =
                Proc.onPath
                    (fun _ ->
                        "Setup: `pwsh` is not on PATH. The merge drivers are PowerShell 7 scripts — they run inside `git merge`, in a tree that may not build, so they deliberately depend on nothing else. Install PowerShell 7 and re-run.")
                    "pwsh"

            let gitConfig (key: string) (value: string) =
                Proc.exec "git" [ "config"; key; value ] root |> Proc.ensure |> ignore

            for name, mode, title in mergeDrivers do
                // Forward slashes on both paths: the command string is
                // interpreted by git's own shell, where a backslash is an
                // escape character.
                let command =
                    sprintf
                        "\"%s\" -NoProfile -File \"%s\" -Mode %s %%O %%A %%B %%P"
                        (Paths.toSlash shell)
                        (Paths.toSlash driver)
                        mode

                gitConfig (sprintf "merge.%s.driver" name) command
                gitConfig (sprintf "merge.%s.name" name) title
                Trace.tracefn "  merge.%s.driver = %s" name command

            Trace.tracefn
                "▶ Setup: the merge drivers are registered in this clone, to the primary checkout's script. Two branches that each ADD disjoint public members, or each append a draft-note line, now merge without a hand resolution; a disagreement no three-way rule can decide is still left as a conflict for a human.")

    // Warn-only: an unregistered driver is a clone's choice (git falls back
    // to its text merge), but one registered to a missing path turns every
    // merge of its files into a conflict, in every worktree of the clone.
    let runMergeDrivers () : Verdict =
        let dead =
            mergeDriverProblems (Path.getFullName ".")
            |> List.choose (fun (name, problem) ->
                match problem with
                | Some p when p <> unregistered -> Some(sprintf "merge.%s.driver: %s" name p)
                | _ -> None)

        if not (List.isEmpty dead) then
            Trace.traceImportant (
                sprintf
                    "▶ VerifyAll: WARNING — a merge driver is registered to a missing path, so git reports every merge of its files as a conflict (%s). Re-run `dotnet run --project Build.fsproj -- Setup`; `Setup --check` names each one."
                    (String.concat "; " dead)
            )

        Verdict.Pass None

    // Phase 805 — the regenerate-from-the-merged-tree fallback.
    //
    // The driver refuses the cases it cannot compute, and the honest answer to
    // each of them is the same: regenerate the projection from the tree that
    // came out of the merge, so the merge commit carries the bytes a clean
    // regen would. This is that, scoped — it regenerates EXACTLY the
    // assemblies it was handed and never touches one it was not, which is the
    // whole point of the `api` stem taking names.
    //
    // Usage:
    //   dotnet run --project Build.fsproj -- MergeBaselines --assemblies ToolUp.AI.Core,ToolUp.AI.Server
    Target.create "MergeBaselines" (fun _ ->
        let root = Path.getFullName "."

        let assemblies =
            Flags.value "--assemblies" flags
            |> Option.map (fun csv ->
                csv.Split([| ','; ';' |], System.StringSplitOptions.RemoveEmptyEntries)
                |> Array.map _.Trim()
                |> Array.filter (System.String.IsNullOrWhiteSpace >> not))
            |> Option.defaultValue [||]

        if Array.isEmpty assemblies then
            failwith
                "MergeBaselines: name the assemblies to regenerate — `dotnet run --project Build.fsproj -- MergeBaselines --assemblies ToolUp.AI.Core,ToolUp.AI.Server`. It deliberately has no all-of-them mode: an unscoped regen folds in every unrelated drift the merged tree happens to be carrying, which is the failure the scoping exists to prevent."

        // The renderer reads BUILT DLLs, so the merged tree has to compile
        // before it can be measured. The approval gate's own pack references
        // every packable project, so building it builds exactly the surface it
        // renders, and running it runs the approval lists alone: no
        // whole-solution build, and no other pack's cases.
        let scope = String.concat "," assemblies
        Trace.tracefn "MergeBaselines: building the packable projects and regenerating %s" scope

        Proc.execWith
            {
                Proc.options with
                    Environment = [
                        "TOOLUP_APPROVE", assemblies |> Seq.map (sprintf "api:%s") |> String.concat ","
                    ]
            }
            "dotnet"
            [
                "run"
                "--project"
                "src/ToolUp.Platform.Tests.PublicApi/ToolUp.Platform.Tests.PublicApi.fsproj"
            ]
            root
        |> Proc.ensure
        |> ignore

        // Then hand the regenerated bytes back to the driver, base = ours =
        // theirs. The merge of a file against itself is that file, so any
        // difference means the driver's re-emission and the generator's have
        // drifted — and a merge commit produced by a drifted driver is exactly
        // what this target exists to keep out of the history.
        let shell =
            Proc.onPath
                (fun _ ->
                    "MergeBaselines: `pwsh` is not on PATH, so the regenerated baselines cannot be re-checked against the driver.")
                "pwsh"

        let driver = Path.Combine(root, "dev-scripts", "merge-baselines.ps1")

        let roundTrips (mode: string) (path: string) =
            let scratch =
                Path.Combine(Path.GetTempPath(), "toolup-mergebaselines-" + System.Guid.NewGuid().ToString "N")

            Directory.CreateDirectory scratch |> ignore

            try
                let copy = Path.Combine(scratch, Path.GetFileName path)
                File.Copy(path, copy, true)

                let result =
                    Proc.exec shell [ "-NoProfile"; "-File"; driver; "-Mode"; mode; path; copy; path; path ] root

                if result.ExitCode <> 0 then
                    failwithf
                        "MergeBaselines: the driver refused %s merged against itself (exit %d)."
                        path
                        result.ExitCode

                let before = File.ReadAllText(path).Replace("\r\n", "\n")
                let after = File.ReadAllText(copy).Replace("\r\n", "\n")

                if before <> after then
                    failwithf
                        "MergeBaselines: %s does not survive a merge against itself — the driver's re-emission disagrees with what the generator has just written. Do NOT commit this merge; fix dev-scripts/merge-baselines.ps1 or `sortSurfaceBody` first."
                        path
            finally
                try
                    Directory.Delete(scratch, true)
                with _ ->
                    ()

        for assembly in assemblies do
            let path = Path.Combine(root, "api-baselines", assembly + ".approved.txt")

            if File.Exists path then
                roundTrips "members" path
            else
                Trace.traceImportantfn
                    "MergeBaselines: %s has no baseline file — the regeneration did not discover that assembly. Check the name."
                    path

        let coverage = Path.Combine(root, "api-baselines", "doc-coverage.approved.txt")

        if File.Exists coverage then
            roundTrips "coverage" coverage

        Trace.tracefn
            "▶ MergeBaselines: %s regenerated from the merged tree and re-checked against the driver. Commit api-baselines/ with the merge."
            scope)

    // ── Phase 257 — the v1.0 readiness scorecard ──
    //
    // "Are we 1.0 yet?" as a regenerated table rather than a judgement
    // call. Six discrete checks, each pass/fail with its measured value
    // and the threshold it was graded against, written to
    // `docs/reference/v1-readiness.md` — the document the operator reads
    // before tagging. The render is gitignored and never committed (Phase
    // 1096): a committed copy went nine releases stale, because no workflow
    // runs this target. What each row reads and why is in `V1Readiness.fs`;
    // this target only does the READING (git, files, the environment)
    // and hands data to the pure checks, so a fixture can score every row
    // without a checkout.
    //
    //   dotnet run --project Build.fsproj -- V1Readiness
    //   dotnet run --project Build.fsproj -- V1Readiness --require-ready
    //
    // The default writes the document and exits 0 whatever it says — the
    // scorecard is a report, and a report that fails the build on the day
    // it is first generated is one people learn to step over.
    // `--require-ready` is the graduation gate itself: exit 1 unless every
    // row passes, for the release workflow at the 1.0 cut.
    //
    // An input this tree cannot supply — no release tag, a sidecar not yet
    // generated, the adoption matrix not pointed at — is an explicit
    // `not yet` row, never a crash: the generator's job is to say what is
    // missing, and it cannot say so from inside an exception.
    //
    // Registered here and NOT added to `verify.ps1`, for the reason
    // VerifySemVerBump gives: the question is a release question, the
    // pure checks run on every commit in the Build test pack, and the
    // stability row's reading of git is not a per-commit fact.
    // The Phase 257 scorecard's READING, shared since Phase 263: the
    // V1Readiness target renders it, and VerifyRcPromotion requires it
    // all-green before a candidate may be promoted. One reader, so the
    // document the operator reads and the gate that acts on it cannot
    // score different rows.
    let scoreV1Readiness (root: string) =
        // ── thresholds ──
        let thresholdsPath = Path.Combine(root, V1Readiness.thresholdsFileName)

        let thresholds, thresholdsSource =
            if File.Exists thresholdsPath then
                match V1Readiness.parseThresholds (File.ReadAllText thresholdsPath) with
                | Ok t -> t, sprintf "`%s`" V1Readiness.thresholdsFileName
                | Error e -> failwithf "V1Readiness: %s" e
            else
                V1Readiness.defaults, sprintf "the built-in defaults (`%s` is absent)" V1Readiness.thresholdsFileName

        let treeSha =
            try
                Git.read root [ "rev-parse"; "--short"; "HEAD" ]
                |> Lines.ofOutput
                |> List.tryHead
                |> Option.defaultValue "unknown"
            with _ ->
                "unknown"

        // 1. baseline-stable: every release tag reachable from HEAD, newest
        //    first, diffed against the WORKING tree's api-baselines. The
        //    doc-coverage sidecar is excluded — it moves with documentation,
        //    not with the surface. The walk stops at the first release that
        //    differs, so the cost is one `git diff` per stable release plus
        //    one.
        let releases =
            try
                let tags =
                    ReleaseChannel.Tags.released root
                    |> SemVerBump.releaseTags
                    |> List.sortWith (fun (_, a) (_, b) -> SemVerBump.Version.compare b a)

                let identical (tag: string) =
                    let result =
                        Git.run root [
                            "diff"
                            "--quiet"
                            tag
                            "--"
                            "api-baselines"
                            ":(exclude)api-baselines/doc-coverage.approved.txt"
                        ]

                    result.ExitCode = 0

                let rec walk acc =
                    function
                    | [] -> List.rev acc
                    | (tag, _) :: rest ->
                        if identical tag then
                            walk ((tag, true) :: acc) rest
                        else
                            List.rev ((tag, false) :: acc)

                V1Readiness.Available(walk [] tags)
            with e ->
                V1Readiness.Unavailable(sprintf "git could not be read (%s)" e.Message)

        // 2. conformance-coverage: Phase 259's own derivation.
        let conformance =
            try
                let r = ConformanceCoverage.reconcile root
                V1Readiness.Available(r.PackedSeamCount, r.SeamCount)
            with e ->
                V1Readiness.Unavailable(sprintf "the conformance derivation failed (%s)" e.Message)

        let readIfExists (path: string) (what: string) =
            if File.Exists path then
                V1Readiness.Available(File.ReadAllText path)
            else
                V1Readiness.Unavailable(sprintf "%s is absent (%s)" what (Path.GetFileName path))

        // 3. doc-coverage: Phase 261's sidecar.
        let docSidecar =
            readIfExists
                (Path.Combine(root, "api-baselines", "doc-coverage.approved.txt"))
                "the Phase 261 doc-coverage sidecar"

        // 4. undecided-renames: Phase 256's decision table.
        let renameDoc =
            readIfExists
                (Path.Combine(root, "docs", "migrations", "256-public-surface-minimization.md"))
                "the Phase 256 migration doc"

        // 5. adoption-pending: the generated matrix and the consumer set,
        //    both supplied from outside — they are facts about private
        //    consumers, so neither is located or named here.
        let matrix =
            match System.Environment.GetEnvironmentVariable V1Readiness.matrixEnvVar with
            | null
            | "" ->
                V1Readiness.Unavailable(
                    sprintf "%s is not set — point it at the generated adoption matrix" V1Readiness.matrixEnvVar
                )
            | path when File.Exists path -> V1Readiness.Available(File.ReadAllText path)
            | path -> V1Readiness.Unavailable(sprintf "%s names no file (%s)" V1Readiness.matrixEnvVar path)

        let consumers =
            System.Environment.GetEnvironmentVariable V1Readiness.consumersEnvVar
            |> V1Readiness.parseConsumers

        // 6. open-deprecations: the `(obsolete)` markers in the baselines.
        let baselines =
            let dir = Path.Combine(root, "api-baselines")

            if Directory.Exists dir then
                Directory.GetFiles(dir, "*.approved.txt")
                |> Array.filter (fun p -> Path.GetFileName p <> "doc-coverage.approved.txt")
                |> Array.sort
                |> Array.map (fun p -> Path.GetFileName p, File.ReadAllText p)
                |> List.ofArray
                |> V1Readiness.Available
            else
                V1Readiness.Unavailable "api-baselines/ is absent"

        let rows = [
            V1Readiness.baselineStability thresholds.StableReleases releases
            V1Readiness.conformanceCoverage thresholds.ConformanceCoveragePct conformance
            V1Readiness.docCoverage thresholds.DocCoveragePct docSidecar
            V1Readiness.undecidedRenames renameDoc
            V1Readiness.adoptionPending consumers matrix
            V1Readiness.openDeprecations thresholds.OpenDeprecations baselines
        ]

        rows, thresholdsSource, treeSha, matrix, consumers

    Target.create "V1Readiness" (fun _ ->
        let root = Path.getFullName "."

        let requireReady = Flags.has "--require-ready" flags

        let rows, thresholdsSource, treeSha, matrix, consumers = scoreV1Readiness root

        let generatedAt = System.DateTime.UtcNow.ToString "yyyy-MM-dd"
        let document = V1Readiness.render generatedAt treeSha thresholdsSource rows
        let outPath = Path.Combine(root, "docs", "reference", "v1-readiness.md")
        Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
        File.WriteAllText(outPath, document)

        // The per-consumer split goes to the console only — the rendered
        // document is consumer-blind on purpose.
        match matrix with
        | V1Readiness.Available text ->
            for name, result in V1Readiness.pendingPerConsumer consumers text do
                match result with
                | Ok n -> Trace.tracefn "  adoption-pending: %s → %d pending" name n
                | Error e -> Trace.traceImportantfn "  adoption-pending: %s → %s" name e
        | V1Readiness.Unavailable _ -> ()

        for r in rows do
            let verdict =
                match r.Verdict with
                | V1Readiness.Pass -> "pass"
                | V1Readiness.Fail -> "FAIL"
                | V1Readiness.NotYet -> "NOT YET"

            Trace.tracefn "  %-22s %-8s %s (threshold %s)" r.Id verdict r.Measured r.Threshold

        let failing = rows |> List.filter (fun r -> r.Verdict <> V1Readiness.Pass)
        let failingIds = failing |> List.map _.Id |> String.concat ", "

        if V1Readiness.ready rows then
            Trace.tracefn "▶ V1Readiness: READY — every precondition passes. Written to %s." outPath
        elif requireReady then
            failwithf
                "V1Readiness --require-ready: NOT READY — %d of %d precondition(s) fail (%s). The scorecard at %s says what each one needs."
                failing.Length
                rows.Length
                failingIds
                outPath
        else
            Trace.traceImportantfn
                "▶ V1Readiness: NOT READY — %d of %d precondition(s) fail (%s). Written to %s."
                failing.Length
                rows.Length
                failingIds
                outPath)

    // ── Phase 262 — the generated CHANGELOG ──
    //
    // One `## [version]` section per release tag, newest first, each
    // carrying the public-surface movement since the release before it
    // (Added / Changed / Removed) and the migration notes that belong to
    // it, written to `CHANGELOG.md` at the repo root. What a section
    // says and why is in `Changelog.fs`; this target only does the
    // READING — the tags, each tag's committed baselines, the migration
    // docs each interval introduced — and hands the data to the pure
    // renderer, so a fixture can render a section without a checkout.
    //
    //   dotnet run --project Build.fsproj -- Changelog
    //   dotnet run --project Build.fsproj -- Changelog --check
    //   dotnet run --project Build.fsproj -- Changelog --draft
    //
    // The tracked file holds the RELEASED sections only, so it changes
    // once per release tag and never under a change that merely moves a
    // baseline. Rendering the unreleased section into it made the file a
    // merge conflict between any two changes that each moved a surface,
    // because both rewrote the same draft section. The publish workflow
    // renders it on the tag, before the pack, and uploads it as the run's
    // `changelog` artefact; the release's follow-up commit carries it to
    // `main`. Run locally after `git fetch --tags` it renders the same
    // bytes, because both read the same tags, baselines and migration
    // docs. The default writes the file and exits 0; `--check` writes
    // nothing and exits 1 when the committed file is not the released
    // render of this tree.
    //
    // `--draft` prints the unreleased section to stdout and writes
    // nothing: the declared `<Version>`'s movement since the newest tag,
    // over the WORKING tree's baselines, the same interval the
    // scorecard's stability row and the bump check read. It is for
    // reading ahead of a release; nothing commits it.
    //
    // The diff per interval is the one VerifySemVerBump takes at a
    // release, read the same way: `git diff --name-only` between the two
    // tags names the baselines that MOVED, and only those are read on
    // both sides, so the cost tracks the churn rather than the ~165-file
    // baseline set times the tag count. A tag that predates
    // `api-baselines/` renders as a section saying so.
    //
    // Not a leg of `VerifyAll`: the released render moves only at a tag,
    // so there is no per-change fact to gate. The renderer's rules run on
    // every commit in the Build test pack.
    Target.create "Changelog" (fun _ ->
        let root = Path.getFullName "."

        let checkOnly = Flags.has "--check" flags
        let draftOnly = Flags.has "--draft" flags

        let isBaseline (path: string) =
            path.StartsWith "api-baselines/"
            && path.EndsWith ".approved.txt"
            && not (path.EndsWith "doc-coverage.approved.txt")

        // ── the release line, oldest first ──
        //
        // Release candidates (Phase 263) are not releases here: each
        // parses to the version it is a candidate for, so it would render
        // a second `## [1.0.0]` claiming a release that has not happened.
        // The stable section covers the whole interval they soaked in.
        let tags =
            ReleaseChannel.Tags.released root
            |> List.filter (SemVerBump.isPrerelease >> not)
            |> SemVerBump.releaseTags
            |> List.sortWith (fun (_, a) (_, b) -> SemVerBump.Version.compare a b)

        let dateOf (tag: string) =
            Git.read root [ "log"; "-1"; "--format=%cs"; tag ]
            |> Lines.ofOutput
            |> List.head

        let baselinesAt (rev: string) =
            Git.read root [ "ls-tree"; "-r"; "--name-only"; rev; "--"; "api-baselines" ]
            |> Lines.ofOutput
            |> List.filter isBaseline
            |> Set.ofList

        let docsAt (rev: string) =
            Git.read root [ "ls-tree"; "-r"; "--name-only"; rev; "--"; "docs/migrations" ]
            |> Lines.ofOutput
            |> List.filter (fun p -> p.EndsWith ".md")
            |> Set.ofList

        // The migration docs a section may LINK are the ones in the
        // working tree — a doc a tag carried and a later commit deleted
        // would render as a dead link. A release's docs are therefore the
        // working-tree docs whose name claims its version, plus the
        // version-less ones its interval introduced that still exist.
        let workingDocs =
            Directory.GetFiles(Path.Combine(root, "docs", "migrations"), "*.md")
            |> Array.map (fun p -> "docs/migrations/" + Path.GetFileName p)
            |> Set.ofArray

        let releasedVersions = tags |> List.map (snd >> SemVerBump.Version.render)

        let showAt (rev: string) (pathsAtRev: Set<string>) (path: string) =
            if pathsAtRev.Contains path then
                Some(Git.read root [ "show"; sprintf "%s:%s" rev path ])
            else
                None

        // The entries for one interval: the baselines that moved, each
        // read on both sides only because it moved. `readSince` reads the
        // near side (a tag); `readCurrent` the far side — a tag for a
        // release, the working tree for the draft.
        let entriesBetween
            (changed: string list)
            (readSince: string -> string option)
            (readCurrent: string -> string option)
            =
            changed
            |> List.filter isBaseline
            |> List.map (fun path ->
                SemVerBump.classifyPackage (SemVerBump.packageOfBaselinePath path) (readSince path) (readCurrent path)
                |> Changelog.entryOf)

        // The version lines a tag shipped, from the `version-groups.json` it
        // carried; none for a tag from before groups, whose section renders
        // as the one line it was.
        let groupLinesAt (tag: string) =
            match versionGroupsAt root tag with
            | None -> []
            | Some text ->
                match SdkManifest.parseVersionGroups text with
                | Ok groups ->
                    Changelog.groupLinesOf
                        groups
                        (projectFactsAt root tag |> List.map (fun f -> f.Path, f.AssemblyName))
                | Error e -> failwithf "Changelog: %s at %s: %s" SdkManifest.versionGroupsFileName tag e

        let releases () =
            tags
            |> List.mapi (fun i (tag, version) ->
                let since = if i = 0 then None else Some(fst tags[i - 1])
                let pathsHere = baselinesAt tag

                let surface =
                    if Set.isEmpty pathsHere then
                        Changelog.NoBaselines
                    else
                        match since with
                        | Some s ->
                            let changed =
                                Git.read root [ "diff"; "--name-only"; s; tag; "--"; "api-baselines" ]
                                |> Lines.ofOutput

                            Changelog.Baselines(
                                entriesBetween changed (showAt s (baselinesAt s)) (showAt tag pathsHere)
                            )
                        | None ->
                            Changelog.Baselines(
                                entriesBetween (Set.toList pathsHere) (fun _ -> None) (showAt tag pathsHere)
                            )

                let docsIntroduced =
                    let here = docsAt tag

                    match since with
                    | Some s -> Set.difference here (docsAt s)
                    | None -> here
                    |> Set.intersect workingDocs

                let docs =
                    Changelog.docsForRelease
                        (SemVerBump.Version.render version)
                        releasedVersions
                        workingDocs
                        docsIntroduced

                {
                    Changelog.Version = SemVerBump.Version.render version
                    Changelog.Tag = Some tag
                    Changelog.Date = Some(dateOf tag)
                    Changelog.Since = since
                    Changelog.Surface = surface
                    Changelog.MigrationDocs = docs
                    Changelog.Groups = groupLinesAt tag
                })

        // ── the draft: the declared version since the newest tag ──
        //
        // `None` when there is no tag yet, or nothing moved since the
        // newest one. Read only by `--draft`: the tracked file never
        // carries it.
        let draft () =
            match List.tryLast tags with
            | None -> None
            | Some(newest, newestVersion) ->
                let propsPath = Path.Combine(root, "Directory.Build.props")

                let declared =
                    match SemVerBump.declaredVersionIn (File.ReadAllText propsPath) with
                    | Ok v -> v
                    | Error e -> failwithf "Changelog: cannot read the declared SDK version — %s (%s)." e propsPath

                let changed =
                    Git.read root [ "diff"; "--name-only"; newest; "--"; "api-baselines" ]
                    |> Lines.ofOutput
                    |> List.filter isBaseline

                let docsIntroduced = Set.difference workingDocs (docsAt newest)

                let docs =
                    Changelog.docsForRelease
                        (SemVerBump.Version.render declared)
                        (SemVerBump.Version.render declared :: releasedVersions)
                        workingDocs
                        docsIntroduced

                if List.isEmpty changed && Set.isEmpty docsIntroduced then
                    None
                else
                    let surface =
                        Changelog.Baselines(
                            entriesBetween
                                changed
                                (showAt newest (baselinesAt newest))
                                (SemVerBump.currentBaseline root)
                        )

                    let heading =
                        if SemVerBump.Version.compare declared newestVersion > 0 then
                            SemVerBump.Version.render declared
                        else
                            "Unreleased"

                    Some {
                        Changelog.Version = heading
                        Changelog.Tag = None
                        Changelog.Date = None
                        Changelog.Since = Some newest
                        Changelog.Surface = surface
                        Changelog.MigrationDocs = docs
                        Changelog.Groups =
                            match SdkManifest.loadVersionGroups root with
                            | Ok groups ->
                                Changelog.groupLinesOf
                                    groups
                                    (SdkManifest.discover root |> List.map (fun p -> p.ProjectPath, p.AssemblyName))
                            | Error e -> failwithf "Changelog: %s" e
                    }

        if draftOnly then
            // stdout, not the trace: the section is the output, and a
            // reader pipes it into a file or a pager.
            match draft () with
            | Some section -> stdout.Write(Changelog.renderRelease Changelog.defaultLimits section)
            | None ->
                match List.tryLast tags with
                | Some(newest, _) ->
                    stdout.WriteLine(sprintf "_No unreleased public-surface change since `%s`._" newest)
                | None -> stdout.WriteLine "_No release tag is reachable from HEAD, so there is no draft to render._"
        else
            let releases = releases ()

            let document = Changelog.render Changelog.defaultLimits (List.rev releases)

            let outPath = Path.Combine(root, "CHANGELOG.md")

            let existing =
                if File.Exists outPath then
                    Some(File.ReadAllText(outPath).Replace("\r\n", "\n"))
                else
                    None

            let render =
                "it is rendered on the release tag (the publish run's `changelog` artefact), or locally with `git fetch --tags` then `dotnet run --project Build.fsproj -- Changelog`; commit CHANGELOG.md"

            match checkOnly, existing with
            | true, Some current when current = document ->
                Trace.tracefn "▶ Changelog --check: CHANGELOG.md is current (%d release(s))." releases.Length
            | true, Some _ ->
                failwithf
                    "Changelog --check: CHANGELOG.md is not the released render of this tree — %s. A section headed `unreleased` in it is the pre-release-render form; the render drops it."
                    render
            | true, None -> failwithf "Changelog --check: CHANGELOG.md is absent — %s." render
            | false, Some current when current = document ->
                Trace.tracefn "▶ Changelog: CHANGELOG.md unchanged (%d release(s))." releases.Length
            | false, _ ->
                File.WriteAllText(outPath, document)

                Trace.tracefn
                    "▶ Changelog: CHANGELOG.md rendered (%d release(s)). Commit it with the release; `-- Changelog --draft` prints the unreleased section."
                    releases.Length)

    // ── Phase 263 — the release-candidate channel and its soak gate ──
    //
    // `1.0.0` is a one-way commitment; a release candidate lets consumers
    // validate the frozen surface before it is made. The rules are in
    // `ReleaseChannel.fs` (pure, proven offline by the Build test pack);
    // the two targets below only READ — git, nuget.org, the scorecard —
    // and hand the data in. The operator's flow is in
    // docs/platform/release-process.md.
    //
    //   dotnet run --project Build.fsproj -- VerifyReleaseChannel
    //   dotnet run --project Build.fsproj -- VerifyRcPromotion
    //
    // VerifyReleaseChannel runs in publish-nuget.yml BEFORE the credential
    // is minted, on every release run. It resolves what the run releases
    // from the tag (`TOOLUP_RELEASE_REF` overrides `GITHUB_REF` for a
    // local dry run; a non-tag ref releases `<Version>` on the stable
    // channel, as a branch dispatch always has), refuses a tag the channel
    // does not accept, applies the mechanical half of the soak gate to a
    // stable release that must be promoted from a candidate, and — for a
    // candidate — hands the version to the pack through `GITHUB_ENV`,
    // where Directory.Build.targets reads it.
    //
    // VerifyRcPromotion is the operator's pre-tag check: the whole soak
    // gate for the declared `<Version>`, INCLUDING the Phase 257
    // scorecard. The scorecard is not in the workflow because its
    // adoption row reads private consumer facts the public workflow must
    // not reach (see `V1Readiness.matrixEnvVar`); the workflow therefore
    // enforces the window, the no-break surface rule and the lineage, and the
    // scorecard is enforced here, before the tag exists.
    //
    // Neither is in `verify.ps1`, for the reason VerifySemVerBump gives:
    // they answer release questions, and their rules run on every commit
    // in the Build test pack.
    // What nuget.org says about a candidate, read from the registration
    // leaf of every package the published-package smoke probes — the ids
    // that job already restores, so no second list of "representative"
    // packages exists to drift.
    let candidatePublication (version: string) : ReleaseChannel.Publication =
        use handler =
            new System.Net.Http.HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)

        use client = new System.Net.Http.HttpClient(handler)
        client.Timeout <- System.TimeSpan.FromSeconds 60.0

        PublishedSmoke.probeIds
        |> List.map (fun id ->
            try
                use response =
                    block (client.GetAsync(ReleaseChannel.registrationLeafUrl id version))

                if response.StatusCode = System.Net.HttpStatusCode.NotFound then
                    ReleaseChannel.NotPublished
                elif not response.IsSuccessStatusCode then
                    ReleaseChannel.Unreadable(sprintf "%s: HTTP %d" id (int response.StatusCode))
                else
                    match ReleaseChannel.publicationOf (block (response.Content.ReadAsStringAsync())) with
                    | ReleaseChannel.Unreadable why -> ReleaseChannel.Unreadable(sprintf "%s: %s" id why)
                    | p -> p
            with e ->
                ReleaseChannel.Unreadable(sprintf "%s: %s" id e.Message))
        |> ReleaseChannel.combinePublications

    let promotionEvidence (root: string) (version: ReleaseChannel.ReleaseVersion) (tags: string list) =
        let now = System.DateTimeOffset.UtcNow

        match ReleaseChannel.candidatesOf version.Core tags |> List.tryLast with
        | None -> {
            ReleaseChannel.PromotionEvidence.Version = version
            ReleaseChannel.PromotionEvidence.Candidate = None
            ReleaseChannel.PromotionEvidence.CandidateIsAncestor = false
            ReleaseChannel.PromotionEvidence.Publication = ReleaseChannel.NotPublished
            ReleaseChannel.PromotionEvidence.SurfaceSinceCandidate = []
            ReleaseChannel.PromotionEvidence.Now = now
          }
        | Some(tag, _) ->
            Trace.tracefn "▶ promotion: measuring candidate %s" tag

            {
                ReleaseChannel.PromotionEvidence.Version = version
                ReleaseChannel.PromotionEvidence.Candidate = Some tag
                ReleaseChannel.PromotionEvidence.CandidateIsAncestor =
                    (Git.run root [ "merge-base"; "--is-ancestor"; tag; "HEAD" ]).ExitCode = 0
                // `v1.0.0-rc.2` → `1.0.0-rc.2`: the tag shape is already
                // validated, so the version is the tag minus its `v`.
                ReleaseChannel.PromotionEvidence.Publication = candidatePublication (tag.Substring 1)
                // The doc-coverage sidecar moves with documentation, not
                // with the surface — excluded as the scorecard excludes it.
                // The soak is the core's (docs/platform/release-process.md):
                // a companion line rides the core's release act and is not
                // held to a candidate's surface, so only core packages count.
                ReleaseChannel.PromotionEvidence.SurfaceSinceCandidate =
                    let companionAssemblies =
                        match SdkManifest.loadVersionGroups root with
                        | Ok groups ->
                            SdkManifest.discover root
                            |> List.filter (fun p ->
                                SdkManifest.groupOf groups p.ProjectPath
                                |> Option.exists (fun g -> not g.Lockstep))
                            |> List.map _.AssemblyName
                            |> Set.ofList
                        | Error e -> failwithf "promotion: %s" e

                    surfaceChangesSince root tag
                    |> List.filter (fun c -> c.Package <> "doc-coverage")
                    |> ReleaseChannel.coreSurfaceOnly companionAssemblies
                ReleaseChannel.PromotionEvidence.Now = now
            }

    // The mechanical half of the soak gate for one major, shared by both
    // targets: the window in force (from release-channel.json, printed with
    // where it came from, so an override's reason is in every run's log),
    // the evidence, and the findings. Additive surface growth since the
    // candidate is printed but does not block (operator decision
    // 2026-09-22); a break does.
    let soakFindings (root: string) (version: ReleaseChannel.ReleaseVersion) (tags: string list) =
        let fileText =
            let path = Path.Combine(root, ReleaseChannel.releaseChannelFileName)

            if File.Exists path then
                Some(File.ReadAllText path)
            else
                None

        let window =
            match ReleaseChannel.soakWindowFor fileText version.Core with
            | Ok w -> w
            | Error e -> failwithf "soak window: %s" e

        Trace.tracefn
            "▶ soak window for %s: %s days, from %s"
            (ReleaseChannel.ReleaseVersion.render version)
            (window.Window.TotalDays.ToString(System.Globalization.CultureInfo.InvariantCulture))
            window.Source

        let evidence = promotionEvidence root version tags

        match
            evidence.SurfaceSinceCandidate
            |> List.filter (fun c -> c.Class = SemVerBump.Additive)
        with
        | [] -> ()
        | additive ->
            Trace.tracefn
                "▶ additive surface since the candidate (does not block promotion): %s"
                (additive |> List.map _.Package |> String.concat ", ")

        ReleaseChannel.promotionFindings window.Window tags evidence

    let runVerifyReleaseChannel () =
        let root = Path.getFullName "."

        let tagRef =
            [ "TOOLUP_RELEASE_REF"; "GITHUB_REF" ]
            |> List.tryPick (fun name ->
                match Environment.environVarOrNone name with
                | Some v when not (System.String.IsNullOrWhiteSpace v) -> Some(v.Trim())
                | _ -> None)
            |> Option.filter (fun r -> r.StartsWith "refs/tags/" || r.StartsWith "v")

        let propsText = File.ReadAllText(Path.Combine(root, "Directory.Build.props"))

        let version =
            match ReleaseChannel.resolve propsText tagRef with
            | Ok v -> v
            | Error e -> failwithf "VerifyReleaseChannel: %s" e

        let tags = ReleaseChannel.Tags.released root

        Trace.tracefn
            "▶ VerifyReleaseChannel: this run releases %s on the %s channel (from %s)"
            (ReleaseChannel.ReleaseVersion.render version)
            (match version.Channel with
             | ReleaseChannel.Stable -> "stable"
             | ReleaseChannel.Candidate _ -> "release-candidate")
            (match tagRef with
             | Some r -> "ref " + r
             | None -> "Directory.Build.props <Version>")

        match ReleaseChannel.candidateFindings version tags with
        | [] -> ()
        | findings ->
            failwith (ReleaseChannel.findingsReport "VerifyReleaseChannel: this candidate may not publish" findings)

        if ReleaseChannel.promotionRequired version then
            match soakFindings root version tags with
            | [] ->
                Trace.tracefn
                    "▶ VerifyReleaseChannel: promotion of %s — the candidate soaked its window, no breaking surface change landed since it, and this commit descends from it. (The Phase 257 scorecard is enforced before the tag, by VerifyRcPromotion.)"
                    (ReleaseChannel.ReleaseVersion.render version)
            | findings ->
                failwith (
                    ReleaseChannel.findingsReport
                        (sprintf
                            "VerifyReleaseChannel: %s may not be promoted yet"
                            (ReleaseChannel.ReleaseVersion.render version))
                        findings
                )

        let exported = [ ReleaseChannel.coreEnvVar; ReleaseChannel.prereleaseEnvVar ]

        match ReleaseChannel.ReleaseVersion.prereleaseLabel version with
        | None ->
            // A stable run must pack the stable version. Either variable
            // left set in the environment would make Directory.Build.targets
            // stamp a candidate label onto it.
            match exported |> List.filter (Environment.environVarOrNone >> Option.isSome) with
            | [] -> ()
            | set ->
                failwithf
                    "VerifyReleaseChannel: %s is set, but this run releases the STABLE %s — the pack would stamp a candidate label onto it. Unset it."
                    (String.concat " and " set)
                    (ReleaseChannel.ReleaseVersion.render version)
        | Some label ->
            let core = SemVerBump.Version.render version.Core

            match Environment.environVarOrNone "GITHUB_ENV" with
            | Some envFile when not (System.String.IsNullOrWhiteSpace envFile) ->
                File.AppendAllText(
                    envFile,
                    sprintf "%s=%s\n%s=%s\n" ReleaseChannel.coreEnvVar core ReleaseChannel.prereleaseEnvVar label
                )

                Trace.tracefn
                    "▶ VerifyReleaseChannel: exported %s=%s %s=%s — the lockstep packages pack as %s; independently versioned ones are skipped for this run."
                    ReleaseChannel.coreEnvVar
                    core
                    ReleaseChannel.prereleaseEnvVar
                    label
                    (ReleaseChannel.ReleaseVersion.render version)
            | _ ->
                Trace.tracefn
                    "▶ VerifyReleaseChannel: not on GitHub Actions — to pack this candidate locally, set %s=%s and %s=%s before `dotnet pack`."
                    ReleaseChannel.coreEnvVar
                    core
                    ReleaseChannel.prereleaseEnvVar
                    label

    Target.create "VerifyReleaseChannel" (fun _ -> runVerifyReleaseChannel ())

    Target.create "VerifyRcPromotion" (fun _ ->
        let root = Path.getFullName "."
        let propsText = File.ReadAllText(Path.Combine(root, "Directory.Build.props"))

        let version =
            match ReleaseChannel.resolve propsText None with
            | Ok v -> v
            | Error e -> failwithf "VerifyRcPromotion: %s" e

        let rendered = ReleaseChannel.ReleaseVersion.render version
        let tags = ReleaseChannel.Tags.released root

        match ReleaseChannel.stableTagOf version.Core tags with
        | Some stable ->
            failwithf "VerifyRcPromotion: %s is already released (`%s`) — there is nothing to promote." rendered stable
        | None -> ()

        if not (ReleaseChannel.promotionRequired version) then
            Trace.tracefn
                "▶ VerifyRcPromotion: %s is not a major release, so it has no release candidate and publishes stable directly — there is no soak to check. (Candidates exist only for majors from 1.0 on.)"
                rendered
        else
            let mechanical = soakFindings root version tags

            let rows, _, _, _, _ = scoreV1Readiness root

            let failing =
                rows |> List.filter (fun r -> r.Verdict <> V1Readiness.Pass) |> List.map _.Id

            match mechanical @ ReleaseChannel.scorecardFindings (V1Readiness.ready rows) failing with
            | [] ->
                Trace.tracefn
                    "▶ VerifyRcPromotion: %s is ELIGIBLE — the candidate soaked, no breaking surface change landed since it, this commit descends from it, and the Phase 257 scorecard is all-green. Promote with `git tag -a v%s -m v%s` on this commit and push the tag."
                    rendered
                    rendered
                    rendered
            | findings ->
                failwith (
                    ReleaseChannel.findingsReport (sprintf "VerifyRcPromotion: %s is NOT eligible" rendered) findings
                ))

    // The per-project build census: rebuild the solution once as a graph
    // build, read each project's own build time off the performance summary,
    // join it with line counts and the reference graph, and write
    // `build-census.json`, most expensive first. `--check` builds nothing: it
    // is red when the file's project set differs from the solution's, so a
    // project added or removed without a regeneration cannot leave the file
    // silently stale. Timings are one machine's and dated; see
    // docs/platform/testing-conventions.md.
    //
    // Usage: `dotnet run --project Build.fsproj -- Census [--check]`
    // Phase 1198 — the server tier's per-part namespaces. Three forge-side
    // acts over the scan `SubsystemNamespaces.scan` performs on the part
    // project files and their sources: `--write-table` regenerates the
    // name table the Codemod target's 1198 migration ships as an embedded
    // resource; `--apply-declarations` rewrites each tier file's header to
    // the namespace the map says (idempotent); `--map <file>` writes the
    // migration doc's file table. With no option it checks the committed
    // table against a fresh scan and fails on a drift.
    Target.create "SubsystemNamespaces" (fun _ ->
        let root = Path.getFullName "."

        let tablePath =
            Path.Combine(root, "src", "ToolUp.Platform.Build", "Build", SubsystemNamespaces.resourceName)

        let moves, entries = SubsystemNamespaces.scan root
        let rendered = SubsystemNamespaces.render entries
        let mutable acted = false

        if Flags.has "--write-table" flags then
            File.WriteAllText(tablePath, rendered, System.Text.UTF8Encoding false)
            Trace.tracefn "▶ SubsystemNamespaces: wrote %d entries to %s" entries.Length tablePath

            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName tablePath, SubsystemNamespaces.reachResourceName),
                SubsystemNamespaces.renderReach (SubsystemNamespaces.scanReach root),
                System.Text.UTF8Encoding false
            )

            acted <- true

        if Flags.has "--apply-declarations" flags then
            let written = SubsystemNamespaces.applyDeclarations root moves
            Trace.tracefn "▶ SubsystemNamespaces: rewrote the header of %d file(s)." written.Length
            acted <- true

        match Flags.value "--map" flags with
        | Some out ->
            File.WriteAllText(out, SubsystemNamespaces.renderMap moves, System.Text.UTF8Encoding false)
            Trace.tracefn "▶ SubsystemNamespaces: wrote the %d-file map to %s" moves.Length out
            acted <- true
        | None -> ()

        if not acted then
            let committed =
                if File.Exists tablePath then
                    File.ReadAllText tablePath
                else
                    ""

            if committed.Replace("\r\n", "\n") <> rendered then
                failwithf
                    "SubsystemNamespaces: %s does not match a fresh scan of the tier. Regenerate with `dotnet run --project Build.fsproj -- SubsystemNamespaces --write-table`."
                    SubsystemNamespaces.resourceName

            Trace.tracefn "▶ SubsystemNamespaces: the committed table matches the tier (%d entries)." entries.Length)

    Target.create "Census" (fun _ ->
        let root = Path.getFullName "."
        let solution = "ToolUp.Forge.sln"
        let solutionText = File.ReadAllText(Path.Combine(root, solution))
        let censusPath = Path.Combine(root, Census.fileName)

        if Flags.has "--check" flags then
            let findings =
                if File.Exists censusPath then
                    Census.check (File.ReadAllText censusPath) solutionText
                else
                    [ sprintf "%s does not exist." Census.fileName ]

            match findings with
            | [] -> Trace.tracefn "▶ Census --check: %s names every project in %s." Census.fileName solution
            | found ->
                for f in found do
                    Trace.traceError (sprintf "Census --check: %s" f)

                failwithf
                    "Census --check: %d finding(s), named above. Regenerate with `dotnet run --project Build.fsproj -- Census`."
                    found.Length
        else
            let scratch = Scratch.root "toolup-census"
            Directory.create scratch
            let log = Path.Combine(scratch, "performance-summary.log")
            let result = Proc.exec "dotnet" (Census.buildArgs solution log) root |> Proc.ensure

            let costs = Census.readSummary (File.ReadAllText log)

            let census = {
                TreeSha = (Git.read root [ "rev-parse"; "HEAD" ]).Trim()
                Machine = Census.machine ()
                MeasuredAt = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                WallSeconds = result.Elapsed.TotalSeconds
                Command = "dotnet " + String.concat " " (Census.buildArgs solution "<log>")
                Projects = Census.assemble root costs (Census.projectGraph root (Gate.solutionProjects solutionText))
            }

            File.WriteAllText(censusPath, Census.render census)

            Trace.tracefn
                "▶ Census: %d projects, %.0f s wall, written to %s."
                census.Projects.Length
                census.WallSeconds
                Census.fileName)

    // ─── The gate manifest: bodies bound, rows registered ──────────────
    //
    // Every leg body above is bound to its row here, once all of them are
    // defined, and every row is registered with the lane this process runs:
    // `VerifyAll` folds the selected `Local` rows and names the rest in its
    // trailer. A new gate is a row in GateManifest.fs and one binding here.
    let bodies: GateManifest.Bodies = {
        // The temp hygiene check rides the GateReconcile leg: the first leg
        // after the packs, in every lane, and the gate's other "the run
        // reconciles with the tree" check.
        GateReconcile =
            fun () ->
                match runGateReconcile (), runTempHygiene () with
                | Verdict.Fail a, Verdict.Fail b -> Verdict.Fail(a + " " + b)
                | Verdict.Fail a, _
                | _, Verdict.Fail a -> Verdict.Fail a
                | reconciled, _ -> reconciled
        Fable =
            fun () ->
                Trace.tracefn "▶ VerifyAll: Fable (src/ToolUp.AI.Client.Tests, via the VerifyFable recipe)"
                runVerifyFable ()
        Samples =
            fun () ->
                Trace.tracefn "▶ VerifyAll: Samples (samples/, via the VerifySamples recipe)"
                Gate.verdictOf runVerifySamples
        Analyzers =
            fun () ->
                Trace.tracefn "▶ VerifyAll: Analyzers (TUA rules over src/, via the RemotingAnalyzers pack)"
                Gate.verdictOfExit (runVerifyAnalyzers ())
        ScopeLiterals =
            fun () ->
                Trace.tracefn
                    "▶ VerifyAll: ScopeLiterals (the scope grammar's one home, via VerifyScopeLiterals; the document store's, via VerifyBlobDownloads; the access context's, via VerifyAccessFallbacks)"

                let scopeLiterals = runVerifyScopeLiterals __SOURCE_DIRECTORY__
                let blobDownloads = runVerifyBlobDownloads __SOURCE_DIRECTORY__
                let accessFallbacks = runVerifyAccessFallbacks __SOURCE_DIRECTORY__
                Gate.verdictOfExit (List.max [ scopeLiterals; blobDownloads; accessFallbacks ])
        ServiceConfigFold =
            fun () ->
                Trace.tracefn
                    "▶ VerifyAll: ServiceConfigFold (the closure-fold's one home, via VerifyServiceConfigFold)"

                Gate.verdictOfExit (runVerifyServiceConfigFold __SOURCE_DIRECTORY__)
        FuaranCoResolution =
            fun () ->
                Trace.tracefn "▶ VerifyAll: FuaranCoResolution (Fuaran floors beside the published Fuaran.UI)"

                Gate.verdictOfExit (
                    runVerifyFuaranCoResolution
                        (Path.Combine(__SOURCE_DIRECTORY__, "Directory.Packages.props"))
                        fuaranFloorsFile
                        (Path.Combine(__SOURCE_DIRECTORY__, "docs", "platform", "open-core-boundary.md"))
                )
        FuaranCoBuild =
            fun () ->
                Trace.tracefn
                    "▶ VerifyAll: FuaranCoBuild (the fact tier beside the published Fuaran.UI, via VerifyFuaranCoBuild)"

                Gate.verdictOf (fun () -> runVerifyFuaranCoBuild None None)
        MergeDrivers = runMergeDrivers
        WireFable = fun () -> runWireFable "src/ToolUp.AI.Wire.Fable.Tests" "output/Program.js" "WireFable"
        WireConformanceFable =
            fun () ->
                runWireFable "src/ToolUp.AI.Wire.Conformance/Fable" "output/ProgramFable.js" "WireConformanceFable"
        DocSnippets = runVerifyDocSnippets
        SourceCitations = runVerifySourceCitations
        UploadResults = runVerifyUploadResults
        Templates = fun () -> Gate.verdictOf (fun () -> TemplateGate.run TemplateGatePackages.verifyTemplates)
        PackagedModuleTemplate =
            fun () -> Gate.verdictOf (fun () -> TemplateGate.run TemplateGatePackages.verifyPackagedModuleTemplate)
        DataPlatformTemplate =
            fun () -> Gate.verdictOf (fun () -> TemplateGate.run TemplateGatePackages.verifyDataPlatformTemplate)
        BrowserSmoke = runVerifyBrowserSmoke
        SemVerBump = fun () -> Gate.verdictOf runVerifySemVerBump
        ReleaseChannel = fun () -> Gate.verdictOf runVerifyReleaseChannel
        PublishedPackages = fun () -> Gate.verdictOf runVerifyPublishedPackages
    }

    let manifest = GateManifest.rows bodies

    for row in manifest do
        // The `affected` lane transpiles the client tier only when the diff
        // reached it: a leg skipped on a guess is the direction that ships
        // a broken client tier, so with no manifest to say, it runs.
        let row =
            let graphSkipped =
                match affectedManifest with
                | Some m when not m.Forced ->
                    m.Graph
                    |> Option.exists (fun g -> g.Skipped |> List.exists (fun (pack, _) -> pack = row.Name))
                | _ -> false

            // The graph source names the packs no changed path reaches: they
            // run nothing under the lane that selected them.
            if (row.Name = "Fable" && not affectedClientTierMoved) || graphSkipped then
                {
                    row with
                        Lanes = Set.remove TestLane.Lane.Affected row.Lanes
                }
            else
                row

        Gate.register verifyLane row

    // The manifest's renders, checked or written, and one row run alone.
    //
    //   dotnet run -- Gate                refuse a render that drifted
    //   dotnet run -- Gate --render       write EXPECTED_PACKS into checks.yml,
    //                                     the CI table into CLAUDE.md and the
    //                                     record of what the gate reads into
    //                                     gate-reads.json
    //   dotnet run -- Gate --run <row>    run one row (build what it names,
    //                                     apply its floor), which is how the
    //                                     CI-only jobs invoke theirs
    Target.create "Gate" (fun _ ->
        let root = __SOURCE_DIRECTORY__
        let checksPath = Path.Combine(root, ".github", "workflows", "checks.yml")
        let claudePath = Path.Combine(root, "CLAUDE.md")
        let readsPath = Path.Combine(root, GateManifest.GateReadsFile)

        match Flags.value "--run" flags with
        | Some name ->
            let row =
                match manifest |> List.tryFind (fun r -> r.Name = name) with
                | Some r -> r
                | None ->
                    failwithf
                        "Gate --run: no row named `%s`. The rows: %s."
                        name
                        (manifest |> List.map _.Name |> String.concat ", ")

            Trace.tracefn
                "▶ Gate --run %s (%s; reads %s)"
                row.Name
                (match row.Where |> Set.toList |> List.map Gate.siteName with
                 | [] -> "operator-run"
                 | sites -> String.concat ", " sites)
                (match row.Reads with
                 | [] -> "nothing"
                 | reads -> reads |> List.map Gate.inputName |> String.concat ", ")

            let verdict =
                match row.Kind with
                | LegKind.ExpectoPack project ->
                    // Alone, the row builds what it names and reads the
                    // environment it was given; the scrub is `VerifyAll`'s.
                    Proc.exec "dotnet" [ "build"; project; "--nologo" ] root
                    |> Proc.ensure
                    |> ignore

                    TestPackHost.runPack [] [] (TestPack.create row.Name project)
                | LegKind.Body run -> run ()
                | LegKind.Script path ->
                    Gate.verdictOfExit (Proc.exec "pwsh" [ "-NoProfile"; "-File"; path ] root).ExitCode

            Trace.tracefn "Gate --run %s: %s" row.Name (Gate.statusToken (judged row.Name verdict))
            ensure row.Name verdict
        | None ->
            let floor = Gate.ciFloor GateManifest.VerifyAllJob manifest
            let checksText = File.ReadAllText checksPath
            let claudeText = File.ReadAllText claudePath
            let table = GateManifest.ciTable (GateManifest.jobIds checksText) manifest

            if Flags.has "--render" flags then
                File.WriteAllText(checksPath, GateManifest.renderExpectedPacks floor checksText)

                match GateManifest.spliceRegion GateManifest.CiTableBegin GateManifest.CiTableEnd table claudeText with
                | Some text -> File.WriteAllText(claudePath, text)
                | None ->
                    failwith "Gate --render: CLAUDE.md carries no gate-manifest:ci-table markers to render between."

                File.WriteAllText(readsPath, GateManifest.renderGateReads ())

                Trace.tracefn
                    "▶ Gate --render: EXPECTED_PACKS: %d written to checks.yml; the CI table (%d job(s)) written to CLAUDE.md; %s written (%d read(s))."
                    floor
                    (table.Length - 2)
                    GateManifest.GateReadsFile
                    (GateManifest.gateReads ()).Length
            else
                let findings = [
                    match GateManifest.readExpectedPacks checksText with
                    | Some n when n = floor -> ()
                    | Some n ->
                        sprintf
                            "checks.yml declares EXPECTED_PACKS: %d; the manifest's `verify-all` rows count %d"
                            n
                            floor
                    | None -> "checks.yml declares no EXPECTED_PACKS"

                    match GateManifest.regionLines GateManifest.CiTableBegin GateManifest.CiTableEnd claudeText with
                    | Some lines when lines = table -> ()
                    | Some _ -> "CLAUDE.md's generated CI table is not the render of the manifest's `Where`"
                    | None -> "CLAUDE.md carries no gate-manifest:ci-table markers"

                    yield!
                        GateManifest.gateReadsFindings
                            manifest
                            (if File.Exists readsPath then
                                 Some(File.ReadAllText readsPath)
                             else
                                 None)

                    let timingsPath = Path.Combine(root, GateManifest.GateTimingsFile)

                    let timingsErrors, timingsWarnings =
                        GateManifest.timingsFindings
                            manifest
                            System.DateTimeOffset.UtcNow
                            (if File.Exists timingsPath then
                                 Some(File.ReadAllText timingsPath)
                             else
                                 None)

                    for w in timingsWarnings do
                        Trace.traceImportant (sprintf "Gate: %s" w)

                    yield! timingsErrors
                ]

                match findings with
                | [] ->
                    Trace.tracefn
                        "▶ Gate: OK — EXPECTED_PACKS: %d, the CI table and gate-reads.json are the manifest's renders, and gate-timings.json measures its rows (%d row(s))."
                        floor
                        manifest.Length
                | drifted ->
                    failwithf
                        "Gate: %d render(s) drifted — %s. Run `dotnet run --project Build.fsproj -- Gate --render` and commit the result; the rows in GateManifest.fs are the source."
                        drifted.Length
                        (String.concat "; " drifted))

    // The recorded gate runs, folded: each run's admission line, then the
    // per-lane and per-row medians. `--record` writes the fold to
    // `gate-timings.json`, the committed record the rows' budgets are read
    // from: the window is `--since` days (default seven) ending at the
    // newest recorded run, so the same journal folds to the same record.
    //
    //   dotnet run -- GateTimings [--since <days>] [--record]
    Target.create "GateTimings" (fun _ ->
        let root = __SOURCE_DIRECTORY__

        let since =
            Flags.value "--since" flags
            |> Option.map (fun d -> System.TimeSpan.FromDays(float d))

        match VerifyGate.commonGitDir (Path.getFullName ".") with
        | Error why -> failwithf "GateTimings: %s" why
        | Ok dir ->
            let file = VerifyGate.Runs.path dir
            let text = if File.Exists file then File.ReadAllText file else ""
            let runs, skipped = VerifyGate.Runs.parse text

            if skipped > 0 then
                Trace.traceImportantfn "GateTimings: %d line(s) of %s were not records and were skipped." skipped file

            if not (Flags.has "--record" flags) then
                for line in VerifyGate.Runs.report System.DateTimeOffset.UtcNow since runs do
                    Trace.tracefn "%s" line
            else
                let path = Path.Combine(root, GateManifest.GateTimingsFile)
                let rows = GateManifest.rows GateManifest.unbound
                let width = since |> Option.defaultValue (System.TimeSpan.FromDays 7.)

                match
                    VerifyGate.Runs.fold
                        System.Environment.MachineName
                        (GateManifest.localRows rows |> List.map _.Name)
                        width
                        runs
                with
                | Error why -> Trace.traceImportantfn "GateTimings --record: %s in %s; nothing written." why file
                | Ok folded ->
                    let existing =
                        if File.Exists path then
                            GateManifest.parseTimings (File.ReadAllText path) |> Result.toOption
                        else
                            None

                    match existing with
                    | Some e when { e with FoldedAt = None } = folded ->
                        Trace.tracefn
                            "▶ GateTimings --record: %s already holds this fold (%d run(s)); not rewritten."
                            GateManifest.GateTimingsFile
                            folded.Runs
                    | _ ->
                        let record = {
                            folded with
                                FoldedAt = VerifyGate.headCommit root
                        }

                        let text = GateManifest.renderTimings record

                        match GateManifest.timingsFindings rows System.DateTimeOffset.UtcNow (Some text) with
                        | [], _ ->
                            File.WriteAllText(path, text)

                            Trace.tracefn
                                "▶ GateTimings --record: %s written — %d run(s) from %s to %s on %s; %d lane(s), %d row(s)."
                                GateManifest.GateTimingsFile
                                record.Runs
                                (record.Since.ToString "yyyy-MM-dd HH:mm")
                                (record.Until.ToString "yyyy-MM-dd HH:mm")
                                (String.concat ", " record.Machines)
                                record.Lanes.Length
                                record.Rows.Length
                        | errors, _ ->
                            failwithf
                                "GateTimings --record: the fold would fail GateReconcile, so nothing was written — %s. Widen --since to a window the rows ran in, or declare a seed."
                                (String.concat "; " errors))


    // Phase 735 — `VerifyAll` serialises itself machine-wide, per repository:
    // a named OS mutex keyed on `git rev-parse --git-common-dir`, so every
    // worktree of this clone contends for ONE gate and a second run waits
    // visibly instead of thrashing the first. The dispatcher's gate queue
    // serialises the CITABLE runs; this covers the ones it cannot see (a
    // worker's advisory lane, an operator's hand `verify.ps1`). Taken here,
    // around the whole run, so the build-once step is inside it too. Every
    // other target passes straight through; `TOOLUP_VERIFY_NO_GATE_MUTEX=1`
    // opts out. See `VerifyGate.fs` for the design and its bounds.
    VerifyGate.around (Directory.GetCurrentDirectory()) args (fun () -> execute args)
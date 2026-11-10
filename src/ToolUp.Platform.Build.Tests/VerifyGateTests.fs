// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.VerifyGateTests

open System
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Testing
open ToolUp.Forge
open ToolUp.Forge.VerifyGate

// ─── Phase 735 — the VerifyAll gate's admission rules ─────────────────
//
// The gate runs once per `VerifyAll`, in the FAKE driver, against a
// kernel object whose other holders are other processes. This pack runs
// on every commit, in one process, against the same `acquire` — the
// module is source-linked from the repo root, so the driver and these
// proofs are one implementation, and a mutex is thread-affine, so a
// second THREAD holding the name is the same wait a second process
// would be handed.
//
// ── Every name is unique per case, deliberately ──
// A named mutex outlives the test that created it only while a handle is
// open, but two cases sharing a name would still race each other's
// holders under `--parallel`. Each case mints its own, so no case can
// observe another's state — and none of them can ever observe the REAL
// gate a concurrent `verify.ps1` on this machine holds.
//
// ── The bound is exercised for real, in milliseconds ──
// `Options.Bound` is a `TimeSpan`, not a minute count, precisely so the
// stale-holder path can be walked here in a fraction of a second rather
// than argued about. The falsifier is the same case with the holder
// released before the bound: it must then report `Acquired`, not
// `PastStaleHolder`, or the bound is not what decided the first one.

/// A holder on its own thread: takes `name`, signals `held`, and keeps it
/// until `release` is set — the shape of another process mid-run.
let private holdOnAnotherThread (name: string) =
    let held = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)

    let thread =
        Thread(fun () ->
            use m = new Mutex(false, name)
            m.WaitOne() |> ignore
            held.Set()
            release.Wait()
            m.ReleaseMutex())

    thread.IsBackground <- true
    thread.Start()
    held.Wait()

    {|
        Release =
            fun () ->
                release.Set()
                thread.Join()
    |}

let private uniqueName () =
    mutexName ("test-" + Guid.NewGuid().ToString("N").Substring(0, 12))

let private options (bound: TimeSpan) (slice: TimeSpan) (lines: ResizeArray<string>) = {
    Bound = bound
    Slice = slice
    Report = lines.Add
    DescribeHolder = fun () -> "the test's own holder"
}

let private quick lines =
    options (TimeSpan.FromMilliseconds 400.) (TimeSpan.FromMilliseconds 50.) lines

/// Run `body` with the opt-out variable set, restoring it afterwards even
/// on failure — the pack is sequenced, so no sibling reads it meanwhile.
let private withOptOut (value: string) (body: unit -> unit) =
    let previous = Environment.GetEnvironmentVariable OptOutVariable
    Environment.SetEnvironmentVariable(OptOutVariable, value)

    try
        body ()
    finally
        Environment.SetEnvironmentVariable(OptOutVariable, previous)

let private withWaitBound (value: string) (body: unit -> unit) =
    let previous = Environment.GetEnvironmentVariable WaitBoundVariable
    Environment.SetEnvironmentVariable(WaitBoundVariable, value)

    try
        body ()
    finally
        Environment.SetEnvironmentVariable(WaitBoundVariable, previous)

/// A known journal: three green runs on two machines (one line naming no
/// machine, which the folder claims), one run with a failed leg, a leg
/// naming no row of the manifest, and one run outside a three-hour window.
let private journal =
    String.concat "\n" [
        """{"at":"2026-10-20T12:00:00Z","target":"VerifyAll","lane":"full","admission":"acquired","waitedSeconds":0,"elapsedSeconds":1600,"exit":0,"legs":[{"name":"Platform","verdict":"pass","seconds":900},{"name":"Build","verdict":"pass","seconds":40},{"name":"Retired","verdict":"pass","seconds":5}]}"""
        """{"at":"2026-10-27T09:00:00Z","target":"VerifyAll","lane":"fast","admission":"acquired","waitedSeconds":0,"elapsedSeconds":700,"exit":0,"legs":[{"name":"Build","verdict":"fail","seconds":30}]}"""
        """{"at":"2026-10-27T10:00:00Z","target":"VerifyAll","lane":"fast","admission":"acquired","waitedSeconds":12,"elapsedSeconds":600.4,"exit":0,"machine":"BUILDER-2","legs":[{"name":"Build","verdict":"pass","seconds":35}]}"""
        """{"at":"2026-10-27T11:00:00Z","target":"VerifyAll","lane":"full","admission":"acquired","waitedSeconds":0,"elapsedSeconds":1700.2,"exit":0,"machine":"BUILDER-1","legs":[{"name":"Platform","verdict":"pass","seconds":910},{"name":"Build","verdict":"pass","seconds":45}]}"""
        ""
    ]

/// The record `journal` folds to over seven days, stamped at a fixed commit.
let private expectedRecord =
    String.concat "\n" [
        "{"
        "  \"kind\": \"gateTimings\","
        "  \"_\": \"GENERATED by `dotnet run --project Build.fsproj -- GateTimings --record` from the gate's admission journal (<common git dir>/fuaran-gate/verify-runs.jsonl); do not edit by hand. Seconds. lanes: median and p90 wall clock of the green VerifyAll runs per lane. rows: median of each row over the green full-lane runs, which is the Budget the gate's stale-holder bound sums. machines: hashed labels (the first 8 hex digits of the SHA-256 of the upper-cased host name), never host names. The window is the given width ending at the newest recorded run. A re-fold that changes nothing but foldedAt leaves the file as it is. Gate-inert: listed in gate-reads.json's notRead.\","
        "  \"since\": \"2026-10-20T11:00:00Z\","
        "  \"until\": \"2026-10-27T11:00:00Z\","
        "  \"runs\": 4,"
        "  \"machines\": [\"6e376cf2\", \"ce057696\"],"
        "  \"foldedAt\": \"0123456789abcdef0123456789abcdef01234567\","
        "  \"lanes\": {"
        "    \"fast\": { \"median\": 600.4, \"p90\": 600.4, \"runs\": 1 },"
        "    \"full\": { \"median\": 1650.1, \"p90\": 1700.2, \"runs\": 2 }"
        "  },"
        "  \"rows\": {"
        "    \"Build\": { \"median\": 42.5, \"runs\": 2 },"
        "    \"Platform\": { \"median\": 905.0, \"runs\": 2 }"
        "  }"
        "}"
        ""
    ]

let tests =
    testList "VerifyGate" [
        testList "acquire and release" [
            testCase "a free gate is acquired without waiting, and released"
            <| fun () ->
                let name = uniqueName ()
                let lines = ResizeArray()
                let held = acquire name (quick lines)

                match held.Admission with
                | Admission.Acquired waited -> Expect.equal waited TimeSpan.Zero "a free gate costs no wait"
                | other -> failtestf "expected Acquired, got %A" other

                Expect.isEmpty
                    lines
                    "a free gate prints nothing — the full lane's output must be byte-for-byte what it was"

                held.Release()

                // Released means the NEXT taker gets it without waiting — the
                // property the whole serialisation rests on.
                let again = acquire name (quick lines)

                match again.Admission with
                | Admission.Acquired waited -> Expect.equal waited TimeSpan.Zero "the released gate is free again"
                | other -> failtestf "expected Acquired after release, got %A" other

                again.Release()

            testCase "Release is idempotent"
            <| fun () ->
                let held = acquire (uniqueName ()) (quick (ResizeArray()))
                held.Release()
                held.Release()
        ]

        testList "a held gate" [
            testCase "waits, reports progress, and is acquired when the holder releases within the bound"
            <| fun () ->
                let name = uniqueName ()
                let holder = holdOnAnotherThread name
                let lines = ResizeArray()

                // Release the holder part-way through a generous bound: the
                // waiter must come back `Acquired`, having waited, with the
                // progress it printed meanwhile.
                let releaser =
                    Thread(fun () ->
                        Thread.Sleep 150
                        holder.Release())

                releaser.Start()

                let held =
                    acquire name (options (TimeSpan.FromSeconds 10.) (TimeSpan.FromMilliseconds 50.) lines)

                releaser.Join()

                match held.Admission with
                | Admission.Acquired waited ->
                    Expect.isGreaterThan waited TimeSpan.Zero "the wait was real"

                    Expect.isLessThan
                        waited
                        (TimeSpan.FromSeconds 10.)
                        "and it ended when the holder released, not at the bound"
                | other -> failtestf "expected Acquired after the holder released, got %A" other

                held.Release()

                Expect.isTrue
                    (lines |> Seq.exists (fun l -> l.Contains "another VerifyAll holds it"))
                    "the first line names the wait"

                Expect.isTrue
                    (lines |> Seq.exists (fun l -> l.Contains "the test's own holder"))
                    "the progress line says who holds it — that is what a thirty-minute wait needs to name"

                Expect.isTrue
                    (lines |> Seq.exists (fun l -> l.Contains "acquired after waiting"))
                    "and the acquisition is announced with how long it took"

            testCase "past the bound the holder is treated as stale and the run proceeds unserialised"
            <| fun () ->
                let name = uniqueName ()
                let holder = holdOnAnotherThread name
                let lines = ResizeArray()

                try
                    let held = acquire name (quick lines)

                    match held.Admission with
                    | Admission.PastStaleHolder waited ->
                        Expect.isGreaterThanOrEqual
                            waited
                            (TimeSpan.FromMilliseconds 350.)
                            "the whole bound was waited before giving up on the holder"
                    | other -> failtestf "expected PastStaleHolder, got %A" other

                    held.Release()

                    Expect.isTrue
                        (lines |> Seq.exists (fun l -> l.Contains "STALE" && l.Contains "UNSERIALISED"))
                        "proceeding past the bound is said LOUDLY"

                    // The holder still holds it: proceeding past the bound
                    // took nothing from the holder, which is what makes the
                    // stale verdict safe to be wrong about.
                    let stillHeld = acquire name (quick (ResizeArray()))

                    match stillHeld.Admission with
                    | Admission.PastStaleHolder _ -> ()
                    | other -> failtestf "the original holder should still own the gate, got %A" other

                    stillHeld.Release()
                finally
                    holder.Release()

            testCase "the bound is what decides it — the same holder released in time yields Acquired (falsifier)"
            <| fun () ->
                let name = uniqueName ()
                let holder = holdOnAnotherThread name
                holder.Release()
                let held = acquire name (quick (ResizeArray()))

                match held.Admission with
                | Admission.Acquired _ -> ()
                | other -> failtestf "expected Acquired once the holder had released, got %A" other

                held.Release()

            testCase "a holder that dies without releasing hands the gate over as Recovered"
            <| fun () ->
                let name = uniqueName ()
                let died = new ManualResetEventSlim(false)

                // Take the mutex and let the THREAD end while owning it: the
                // kernel then reports it abandoned to the next waiter, which is
                // exactly what a killed `verify.ps1` looks like from outside.
                let thread =
                    Thread(fun () ->
                        let m = new Mutex(false, name)
                        m.WaitOne() |> ignore
                        died.Set())

                thread.Start()
                died.Wait()
                thread.Join()

                let lines = ResizeArray()

                let held =
                    acquire name (options (TimeSpan.FromSeconds 5.) (TimeSpan.FromMilliseconds 50.) lines)

                match held.Admission with
                | Admission.Recovered _ -> ()
                | Admission.Acquired _ ->
                    // Some hosts release an abandoned named mutex silently
                    // rather than reporting abandonment; the property that
                    // matters — a dead holder cannot wedge the gate — holds
                    // either way.
                    ()
                | other -> failtestf "a dead holder must not wedge the gate; got %A" other

                held.Release()
        ]

        testList "opt-out" [
            testCase "TOOLUP_VERIFY_NO_GATE_MUTEX=1 takes nothing, even while another run holds the gate"
            <| fun () ->
                let name = uniqueName ()
                let holder = holdOnAnotherThread name

                try
                    withOptOut "1" (fun () ->
                        let lines = ResizeArray()
                        let held = acquire name (quick lines)

                        match held.Admission with
                        | Admission.Disabled -> ()
                        | other -> failtestf "expected Disabled, got %A" other

                        Expect.isTrue
                            (lines |> Seq.exists (fun l -> l.Contains OptOutVariable))
                            "the opt-out is announced by the variable's name"

                        held.Release())
                finally
                    holder.Release()

            testCase "absent, empty and 0 all mean gate as normal"
            <| fun () ->
                for value in [ null; ""; "0" ] do
                    withOptOut value (fun () ->
                        Expect.isFalse (optedOut ()) (sprintf "%A must not read as an opt-out" value))

                withOptOut "true" (fun () -> Expect.isTrue (optedOut ()) "any other value opts out")
        ]

        testList "the admission record and GateTimings" [
            testCase "a run round-trips through one JSON line, and the fold reproduces its admission line"
            <| fun () ->
                let run: Runs.Run = {
                    At = DateTimeOffset.Parse("2026-11-02T01:02:03Z", Globalization.CultureInfo.InvariantCulture)
                    Target = "VerifyAll"
                    Lane = "full"
                    Admission = "acquired"
                    WaitedSeconds = 192.
                    ElapsedSeconds = 1571.5
                    Exit = 0
                    Machine = Some "BUILDER-1"
                    Legs = [
                        {
                            Name = "Platform"
                            Verdict = "pass"
                            Cases = Some 10325
                            Seconds = 884.8
                        }
                        {
                            Name = "FuaranCoBuild"
                            Verdict = "pending"
                            Cases = None
                            Seconds = 0.2
                        }
                    ]
                }

                let line = Runs.render run
                Expect.isFalse (line.Contains "\n") "one line per record"
                let parsed, skipped = Runs.parse (line + "\nnot a record\n" + line)
                Expect.equal skipped 1 "a line that is not a record is counted, never fatal"
                Expect.equal parsed [ run; run ] "the record reads back as written"

                let report =
                    Runs.report
                        (DateTimeOffset.Parse("2026-11-03T00:00:00Z", Globalization.CultureInfo.InvariantCulture))
                        None
                        parsed

                Expect.contains
                    report
                    "  2026-11-02 01:02 VerifyAll gate: acquired after waiting 3:12 — full lane, 26:11, exit 0"
                    "the admission line is reproduced from the record"

                Expect.exists report (fun l -> l.Contains "full" && l.Contains "median 26:11") "the lane's median"
                Expect.exists report (fun l -> l.Contains "Platform" && l.Contains "median 14:44") "the row's median"

            testCase "the fold's window excludes an older run and says so when nothing is left"
            <| fun () ->
                let at (iso: string) =
                    DateTimeOffset.Parse(iso, Globalization.CultureInfo.InvariantCulture)

                let run t : Runs.Run = {
                    At = at t
                    Target = "VerifyAll"
                    Lane = "pure"
                    Admission = "acquired"
                    WaitedSeconds = 0.
                    ElapsedSeconds = 40.
                    Exit = 0
                    Machine = None
                    Legs = []
                }

                let runs = [ run "2026-10-01T00:00:00Z"; run "2026-11-01T00:00:00Z" ]
                let now = at "2026-11-02T00:00:00Z"
                let week = Some(TimeSpan.FromDays 7.)

                Expect.equal
                    (Runs.report now week runs).Head
                    "GateTimings: 1 recorded run(s)."
                    "the older run is outside the window"

                Expect.equal
                    (Runs.report now (Some(TimeSpan.FromHours 1.)) runs)
                    [ "GateTimings: no recorded runs in the window." ]
                    "an empty window says so"

            testCase
                "the fold of a known journal reproduces the record byte for byte, and folds it again to the same record"
            <| fun () ->
                let folded =
                    match
                        Runs.fold "BUILDER-1" [ "Build"; "Platform" ] (TimeSpan.FromDays 7.) (fst (Runs.parse journal))
                    with
                    | Ok t -> t
                    | Error why -> failtestf "the journal holds runs: %s" why

                let record = {
                    folded with
                        FoldedAt = Some "0123456789abcdef0123456789abcdef01234567"
                }

                Expect.equal (GateManifest.renderTimings record) expectedRecord "the record, byte for byte"

                match GateManifest.parseTimings expectedRecord with
                | Ok parsed ->
                    Expect.equal parsed record "the record reads back as written"
                    Expect.equal { parsed with FoldedAt = None } folded "a re-fold differs only in the folding commit"
                | Error why -> failtestf "the record parses: %s" why

            testCase
                "the window is the given width ending at the newest run, so the oldest run falls out of a narrow fold"
            <| fun () ->
                match
                    Runs.fold "BUILDER-1" [ "Build"; "Platform" ] (TimeSpan.FromHours 3.) (fst (Runs.parse journal))
                with
                | Ok t ->
                    Expect.equal t.Runs 3 "the run of 2026-10-20 is outside three hours"

                    Expect.equal
                        t.Machines
                        [ "6e376cf2"; "ce057696" ]
                        "one machine named on its lines, one by the folder, each as its hashed label"

                    Expect.equal
                        (Runs.machineLabel "builder-1")
                        "ce057696"
                        "the label folds case and is SHA-256's first eight hex digits"

                    Expect.isFalse
                        (t.Machines |> List.exists (fun m -> m.Contains "BUILDER"))
                        "no host name reaches the record"
                | Error why -> failtestf "the window holds runs: %s" why

            testCase "a journal with no runs writes nothing and says so"
            <| fun () ->
                match Runs.fold "BUILDER-1" [ "Build" ] (TimeSpan.FromDays 7.) [] with
                | Error why -> Expect.stringContains why "no runs" "it says so"
                | Ok t -> failtestf "an empty journal folds to nothing, got %A" t

            testCase "a green run is exit 0 with no failed leg, whatever the exit line says"
            <| fun () ->
                let runs, _ = Runs.parse journal
                Expect.equal (runs |> List.filter Runs.green |> List.length) 3 "the run with a failed leg is not green"

            testCase "the manifest reads a derived Budget from the record and falls back to the seed"
            <| fun () ->
                let rows = GateManifest.rows GateManifest.unbound

                let row name =
                    rows |> List.find (fun r -> r.Name = name)

                let timings =
                    match GateManifest.parseTimings expectedRecord with
                    | Ok t -> Some t
                    | Error why -> failtestf "the record parses: %s" why

                Expect.equal
                    (GateManifest.budgetSource timings (row "Platform"))
                    (GateManifest.BudgetSource.Measured(TimeSpan.FromSeconds 905., 2))
                    "a measured row reads the record's median"

                let derived = GateManifest.withRecordedBudgets timings rows

                let derivedRow name =
                    derived |> List.find (fun r -> r.Name = name)

                Expect.equal (derivedRow "Platform").Budget (Some(TimeSpan.FromSeconds 905.)) "the derived budget"

                Expect.equal
                    (GateManifest.budgetSource timings (row "Samples"))
                    (GateManifest.BudgetSource.Seed(TimeSpan.FromMinutes 10.))
                    "a row the record does not measure keeps its seed, marked so"

                Expect.equal (derivedRow "Samples").Budget (row "Samples").Budget "and its budget is the seed"

                Expect.equal
                    (GateManifest.budgetSource timings (row "Forms"))
                    GateManifest.BudgetSource.Unbudgeted
                    "neither measured nor seeded"

                Expect.equal (derivedRow "Forms").Budget None "carries no budget rather than a guess"
        ]

        testList "configuration" [
            testCase "the wait bound is read in whole minutes and a bad value falls back to the default, never to zero"
            <| fun () ->
                let fallback = TimeSpan.FromMinutes 42.

                withWaitBound "5" (fun () ->
                    Expect.equal (waitBoundFromEnvironment fallback) (TimeSpan.FromMinutes 5.) "five minutes")

                for bad in [ ""; "0"; "-3"; "soon"; "1.5" ] do
                    withWaitBound bad (fun () ->
                        Expect.equal
                            (waitBoundFromEnvironment fallback)
                            fallback
                            (sprintf "%A is not a bound; the default stands" bad))

            testCase "the gated set is derived: VerifyAll, and the manifest's tree-reading rows over a minute"
            <| fun () ->
                let rows = GateManifest.rows GateManifest.unbound
                let gatedTargets = GateManifest.gatedTargets rows
                Expect.isTrue (Set.contains "VerifyAll" gatedTargets) "VerifyAll takes the gate"

                for row in rows do
                    let budgeted =
                        match row.Kind, row.Budget with
                        | LegKind.Body _, Some b -> b > TimeSpan.FromMinutes 1.
                        | _ -> false

                    let expected = budgeted && List.contains Input.Tree row.Reads

                    Expect.equal
                        (Set.contains (GateManifest.target row) gatedTargets)
                        expected
                        (sprintf "%s is gated exactly when it is a Body reading the tree for over a minute" row.Name)

                Expect.isTrue (Set.contains "VerifyFable" gatedTargets) "the Fable row's seed admits it"

                Expect.equal
                    (requestedTarget [| "VerifyAll" |])
                    "VerifyAll"
                    "the first non-option argument is the target"

                Expect.equal (requestedTarget [| "--module"; "x" |]) "Run" "no target ⇒ Run, as the SDK runner reads it"

                for target in [ "Run"; "Pack"; "Format"; "VerifyTemplates"; "GateTimings" ] do
                    Expect.isFalse (Set.contains target gatedTargets) (sprintf "%s stays ungated" target)

            testCase "the default bound is twice the full lane's summed budget, never a literal"
            <| fun () ->
                let rows = GateManifest.rows GateManifest.unbound
                let full = GateManifest.fullBudget rows
                Expect.isGreaterThan full TimeSpan.Zero "the full lane carries budgets"
                Expect.equal (defaultWaitBound rows) (full + full) "two lanes: the holder's and one queued ahead"

            testCase "the key folds path spelling the way the host does, and differs across repositories"
            <| fun () ->
                let root = Environment.CurrentDirectory

                Expect.equal
                    (keyFor root)
                    (keyFor (root + string IO.Path.DirectorySeparatorChar))
                    "a trailing separator is not a second gate"

                if OperatingSystem.IsWindows() then
                    Expect.equal
                        (keyFor root)
                        (keyFor (root.ToUpperInvariant()))
                        "on Windows, case is not a second gate"

                Expect.notEqual
                    (keyFor root)
                    (keyFor (IO.Path.Combine(root, "elsewhere")))
                    "a different directory is a different gate"

                Expect.equal (keyFor root).Length 16 "sixteen hex digits"

            testCase "the mutex name is per platform"
            <| fun () ->
                let name = mutexName "abc"

                if OperatingSystem.IsWindows() then
                    Expect.stringStarts name "Global\\" "Windows: the Global namespace, so logon sessions share it"
                else
                    Expect.isFalse (name.Contains "\\") "elsewhere a backslash is not a namespace"
        ]

        testList "around" [
            testCase "an ungated target runs the body and touches no mutex"
            <| fun () ->
                let ran = ref false

                let result =
                    around Environment.CurrentDirectory [| "Format" |] (fun () ->
                        ran.Value <- true
                        7)

                Expect.equal result 7 "the body's result is returned"
                Expect.isTrue ran.Value "and the body ran"

            testCase
                "a gated target runs the body under the gate, writes the holder note meanwhile, and releases both afterwards"
            <| fun () ->
                // A minted name, never the real key: this pack RUNS INSIDE a
                // `VerifyAll` that holds this checkout's gate, so contending
                // for it here would wait on our own parent.
                let name = uniqueName ()

                use scratch = TempDir.create "verify-gate-test"
                let note = scratch.File "holder-note"

                let lines = ResizeArray()
                let seenDuring = ref ""

                let result =
                    aroundWith name (Some note) (quick lines) Environment.CurrentDirectory ignore (fun () ->
                        seenDuring.Value <- readHolderNote note
                        42)

                Expect.equal result 42 "the body's result is returned"

                Expect.stringContains
                    seenDuring.Value
                    (sprintf "pid %d" Environment.ProcessId)
                    "while the body ran, the note named this process"

                Expect.stringContains seenDuring.Value Environment.CurrentDirectory "and the directory it ran in"
                Expect.isFalse (IO.File.Exists note) "the note is cleared on release"

                // After `aroundWith` returns, the gate must be free: a leak
                // here would serialise every later VerifyAll on this machine
                // behind a holder that no longer exists.
                let probe = acquire name (quick (ResizeArray()))

                match probe.Admission with
                | Admission.Acquired _ -> ()
                | other -> failtestf "the gate should be free after `aroundWith` returns; got %A" other

                probe.Release()

            testCase "a body that throws still releases the gate and clears the note"
            <| fun () ->
                let name = uniqueName ()

                use scratch = TempDir.create "verify-gate-test"
                let note = scratch.File "holder-note"

                Expect.throws
                    (fun () ->
                        aroundWith
                            name
                            (Some note)
                            (quick (ResizeArray()))
                            Environment.CurrentDirectory
                            ignore
                            (fun () -> failwith "red")
                        |> ignore)
                    "the body's failure propagates"

                Expect.isFalse (IO.File.Exists note) "the note is cleared even when the body failed"
                let probe = acquire name (quick (ResizeArray()))

                match probe.Admission with
                | Admission.Acquired _ -> ()
                | other -> failtestf "the gate should be free after a failing body; got %A" other

                probe.Release()

            testCase "the real driver key resolves from this checkout's common git directory"
            <| fun () ->
                // Read-only: resolves the key the driver would use here and
                // checks its shape, without taking the gate.
                match commonGitDir Environment.CurrentDirectory with
                | Ok dir ->
                    Expect.isTrue (IO.Path.IsPathRooted dir) "the common dir is a full path"
                    Expect.equal (keyFor dir).Length 16 "and keys to sixteen hex digits"
                | Error reason ->
                    failtestf "this pack runs inside a git checkout, so the common dir must resolve: %s" reason
        ]
    ]
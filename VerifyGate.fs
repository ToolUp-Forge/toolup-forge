// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 735 — the `VerifyAll` gate serialises itself, machine-wide, per
/// repository.
///
/// **What this is for.** On 2026-08-27 three to four worktrees of this
/// repository ran the ~9,000-test Platform pack at once and each crawled
/// at a few percent of a core: a gate of tens of minutes on a quiet
/// machine became more than two hours, and two runs were abandoned. That campaign
/// worked around it with a lock-directory protocol written into the
/// orchestration briefs. A convention held only in briefs is not a fix —
/// the gate itself has to own its serialisation — and this module is that
/// ownership: before the `VerifyAll` target runs, the process takes a
/// NAMED OS MUTEX keyed on the repository's common git directory, so every
/// worktree of one clone contends for one gate, and a second run WAITS
/// visibly instead of thrashing the first.
///
/// **What the dispatcher's gate queue already covers, and what it cannot
/// see.** Since this phase was authored the roadmap engine grew a
/// dispatcher-owned gate queue (`roadmapctl dispatch gate run`, journal
/// under the side's `.git/fuaran-gate/`) that serialises the CITABLE runs
/// of a campaign — the post-merge full-lane run a ship cites, the fold's
/// re-gate, the landing. It does not, and structurally cannot, see the
/// runs that never join it: a worker's advisory pre-merge lane (which the
/// dispatch opener tells it to run OUTSIDE the queue), an operator's hand
/// `pwsh ./verify.ps1`, or a bare `dotnet run -- VerifyAll`. Those are
/// exactly the runs that thrashed on 2026-08-27, and they are what this
/// mutex is for. The two mechanisms compose rather than compete: the
/// queue decides WHICH citable run goes next; the mutex makes sure no
/// uncitable run is chewing the same CPU while it does.
///
/// **The key is the repository, not the machine.** `git rev-parse
/// --git-common-dir` names the main repository's `.git` from any of its
/// worktrees, so all worktrees of one clone agree on one key — the same
/// resolution the engine's queue uses for the same reason. Two SEPARATE
/// clones of the repository on one machine resolve different keys and are
/// not serialised against each other; the estate runs campaigns as
/// worktrees (`wt\<NN>`), which is the case the 2026-08-27 numbers were
/// measured on. Outside a git repository the key degrades to the working
/// directory itself and the gate says so.
///
/// **The bound is a stale-HOLDER bound, and it proceeds rather than
/// fails.** An OS mutex is released by the kernel when its holder dies
/// (the waiter is handed it as ABANDONED and told so), so a crashed run
/// cannot wedge the gate the way a lock directory could. What remains is
/// a holder that is alive and stuck — a test host wedged at 0% CPU — and
/// for that the wait is bounded (`TOOLUP_VERIFY_GATE_WAIT_MINUTES`; the
/// default is DERIVED, twice the full lane's summed row `Budget` — never a
/// literal, which went stale the moment a leg was added — and each budget
/// is the median the committed `gate-timings.json` measures for its row,
/// or the row's declared seed where the record has none). Past the bound the run PROCEEDS
/// unserialised with a loud line, because the pre-phase behaviour is the
/// floor: a gate that fails BECAUSE it is guarding against slowness has
/// traded a slow run for no run.
///
/// **Opt-out.** `TOOLUP_VERIFY_NO_GATE_MUTEX=1` skips the gate entirely.
/// For CI runners, which are isolated and must never wait on a phantom
/// holder, and for anyone who has decided to pay the contention
/// deliberately.
///
/// **Which targets take the gate.** `VerifyAll` — in every lane, because
/// every lane runs the Platform pack (the `fast` lane drops its slow set,
/// not the pack) and a lane that runs seconds waits seconds — and every
/// `Body` row of the gate manifest that reads the tree for longer than a
/// minute, run through its own `Verify<Name>` target: the set is DERIVED
/// (`GateManifest.gatedTargets`), so a row's measured budget is what
/// admits it, never an edit here. A single pack run directly
/// (`dotnet <pack>.dll --filter …`) never passes through this driver.
///
/// **The admission record.** Every gated run appends one line to
/// `<common git dir>/fuaran-gate/verify-runs.jsonl` — when, which target
/// and lane, how it was admitted and how long it waited, how long it ran,
/// how it exited, which machine ran it, and each row's verdict and
/// seconds — and `dotnet run -- GateTimings [--since <days>]` folds those
/// lines into per-lane and per-row medians. With `--record` it writes them
/// to `gate-timings.json` (`Runs.fold`), the committed record the budgets
/// are read from.
///
/// **Why this file is at the repository root and FAKE-free.** The
/// `VerifyAll` target's body is published SDK surface
/// (`ToolUp.Platform.Build`), frozen at 0.23.0 for release; the gate is
/// forge's own build hygiene, so it lives beside `SdkManifest.fs`,
/// `SemVerBump.fs` and `TestLane.fs` — compiled into `Build.fsproj`,
/// source-linked into `ToolUp.Platform.Build.Tests` so the driver and its
/// go-red proofs run one implementation, and taking nothing beyond
/// `System` and the SDK's own `Git` and `Flags` (`ToolUp.Platform.Build`,
/// which that pack already references).
module ToolUp.Forge.VerifyGate

open System
open System.Diagnostics
open System.IO
open ToolUp.Platform
open ToolUp.Platform.Build
open System.Security.Cryptography
open System.Text
open System.Threading

/// Set to any non-empty value other than `0` to skip the gate.
[<Literal>]
let OptOutVariable = "TOOLUP_VERIFY_NO_GATE_MUTEX"

/// The stale-holder bound in whole minutes; unset ⇒ `defaultWaitBound`.
[<Literal>]
let WaitBoundVariable = "TOOLUP_VERIFY_GATE_WAIT_MINUTES"

/// The manifest's rows, bodies unbound, with the budgets the timings
/// record at the repository root `root` measures: the data this module
/// derives from.
let manifestAt (root: string) =
    GateManifest.rows GateManifest.unbound
    |> GateManifest.withRecordedBudgets (GateManifest.readTimings root)

/// Twice the full lane's summed budget: one lane for the holder to finish
/// and one for an advisory lane queued ahead. See the header.
let defaultWaitBound (rows: GateLeg<TestLane.Lane> list) =
    let full = GateManifest.fullBudget rows

    if full > TimeSpan.Zero then
        full + full
    else
        TimeSpan.FromMinutes 60.

/// How often a waiting run reports that it is still waiting.
let DefaultSlice = TimeSpan.FromSeconds 30.

/// The target `argv` requests, read by the same rule `execute` applies
/// (`Flags.targetOf`), so the gate can never gate a different target from
/// the runner it wraps.
let requestedTarget (argv: string[]) = Flags.targetOf argv

/// Every option a target in the forge driver reads. `Build.fs` parses its
/// command line once against this, and an option no target declares is
/// refused by name. Here rather than in `Build.fs` so the Build test pack
/// can pin each one (`FlagsTests`).
let driverFlags: FlagSpec =
    Flags.union Flags.sdk {
        Switches = [
            "--propose"
            "--update-baseline"
            "--require-ready"
            "--draft"
            "--fail-on-dead"
            "--verbose"
            "--json"
            "--render"
            "--write-table"
            "--apply-declarations"
            "--record"
        ]
        Valued = [
            "--root"
            "--assemblies"
            "--base"
            "--head"
            "--paths"
            "--declared"
            "--source"
            "--out"
            "--release"
            "--ui-version"
            "--toolup-version"
            "--props"
            "--snapshot"
            "--boundary"
            "--run"
            "--since"
            "--map"
        ]
    }

/// Is the opt-out set? Absent, empty and `0` all mean "gate as normal" —
/// the shape the estate's other opt-outs take, so an unset variable can
/// never read as a yes.
let optedOut () =
    match Environment.GetEnvironmentVariable OptOutVariable with
    | null
    | ""
    | "0" -> false
    | _ -> true

/// The bound from the environment, or the default. A value that is not a
/// positive whole number of minutes is IGNORED with the default in its
/// place, never read as zero: a typo that produced a zero bound would make
/// every wait proceed immediately, which is the opt-out spelled wrong.
let waitBoundFromEnvironment (fallback: TimeSpan) =
    match Environment.GetEnvironmentVariable WaitBoundVariable with
    | null
    | "" -> fallback
    | raw ->
        match Int32.TryParse(raw.Trim()) with
        | true, minutes when minutes > 0 -> TimeSpan.FromMinutes(float minutes)
        | _ -> fallback

/// `git rev-parse --git-common-dir` from `workingDir`, resolved to a full
/// path. `Error` names why it could not be had — git missing, not a
/// repository, a hung child — and the caller degrades rather than fails.
let commonGitDir (workingDir: string) : Result<string, string> =
    try
        let r =
            Git.within (TimeSpan.FromSeconds 15.) workingDir [ "rev-parse"; "--git-common-dir" ]

        if r.ExitCode <> 0 then
            Error(sprintf "git rev-parse --git-common-dir exited %d (not a git repository?)" r.ExitCode)
        else
            match r.StdOut.Trim() with
            | "" -> Error "git rev-parse --git-common-dir printed nothing"
            | raw when Path.IsPathRooted raw -> Ok(Path.GetFullPath raw)
            | raw -> Ok(Path.GetFullPath(Path.Combine(workingDir, raw)))
    with e ->
        Error(sprintf "git could not be started, or did not return within 15 s: %s" e.Message)

/// `git rev-parse HEAD` from `workingDir`: the commit a timings record is
/// folded at. `None` when it cannot be had.
let headCommit (workingDir: string) : string option =
    try
        let r = Git.within (TimeSpan.FromSeconds 15.) workingDir [ "rev-parse"; "HEAD" ]

        match r.ExitCode, r.StdOut.Trim() with
        | 0, sha when sha <> "" -> Some sha
        | _ -> None
    with _ ->
        None

/// A short, filesystem- and kernel-safe token for a path: the first 16
/// hex digits of its SHA-256, over the path normalised the way the host
/// compares it (case-folded on Windows, where two spellings of one
/// directory must not be two gates).
let keyFor (path: string) =
    let normalised =
        let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)

        if OperatingSystem.IsWindows() then
            full.ToUpperInvariant()
        else
            full

    let digest = SHA256.HashData(Encoding.UTF8.GetBytes normalised)
    Convert.ToHexString(digest, 0, 8).ToLowerInvariant()

/// The kernel object's name for a key. `Global\` on Windows so two logon
/// sessions on one machine share the gate; plain elsewhere, where the
/// prefix is not a namespace and a backslash is not a separator.
let mutexName (key: string) =
    if OperatingSystem.IsWindows() then
        "Global\\ToolUp.VerifyGate." + key
    else
        "ToolUp.VerifyGate." + key

/// How the gate admitted this run. Every case means the body RAN; the
/// case says under what conditions, so the log line — and a test — can
/// tell a clean acquisition from a wait, a recovery, an opt-out, or a
/// proceed-past-the-bound.
[<RequireQualifiedAccess>]
type Admission =
    /// The gate was free, or became free within the bound.
    | Acquired of waited: TimeSpan
    /// The previous holder died without releasing; the kernel handed the
    /// gate over and this run took it. Not a fault of this run.
    | Recovered of waited: TimeSpan
    /// `TOOLUP_VERIFY_NO_GATE_MUTEX` is set; nothing was taken.
    | Disabled
    /// Waited the whole bound; the holder is treated as stale and the run
    /// proceeds UNSERIALISED — the pre-phase behaviour, loudly.
    | PastStaleHolder of waited: TimeSpan
    /// The OS refused the mutex (an access-control difference between two
    /// users, a name the platform will not take); the run proceeds
    /// unserialised, naming the reason.
    | Unavailable of reason: string

/// The knobs a caller — the driver, or a test — sets.
type Options = {
    /// How long to wait for a holder before treating it as stale.
    Bound: TimeSpan
    /// How long each wait attempt lasts; a progress line follows each.
    Slice: TimeSpan
    /// Where progress and admission lines go. The driver prints them; a
    /// test collects them.
    Report: string -> unit
    /// What the progress line says the gate is held by, when it is. The
    /// driver reads the holder note; a test passes a constant.
    DescribeHolder: unit -> string
}

/// The held gate: releasing it is the ONE thing a caller must do, on the
/// thread that acquired it (a mutex is thread-affine), and exactly once.
type Held = {
    /// How this run was admitted.
    Admission: Admission
    /// Release the gate. Idempotent, and a no-op when nothing was taken.
    Release: unit -> unit
}

let private minutes (t: TimeSpan) =
    sprintf "%d:%02d" (int t.TotalMinutes) t.Seconds

/// Take the named gate under `options`, or decide not to. Never throws for
/// a reason to do with the gate itself: a mutex the platform will not
/// give is reported as `Unavailable` and the run goes on.
let acquire (name: string) (options: Options) : Held =
    let noop = fun () -> ()

    if optedOut () then
        options.Report(sprintf "VerifyAll gate: %s is set — running without the gate." OptOutVariable)

        {
            Admission = Admission.Disabled
            Release = noop
        }
    else
        let mutex =
            try
                Ok(new Mutex(false, name))
            with
            | :? UnauthorizedAccessException as e -> Error(sprintf "access denied to mutex %s: %s" name e.Message)
            | :? IOException as e -> Error(sprintf "the platform refused mutex %s: %s" name e.Message)
            | :? PlatformNotSupportedException as e -> Error(sprintf "named mutexes are unsupported here: %s" e.Message)

        match mutex with
        | Error reason ->
            options.Report(sprintf "VerifyAll gate: %s — proceeding WITHOUT serialisation." reason)

            {
                Admission = Admission.Unavailable reason
                Release = noop
            }
        | Ok mutex ->
            let clock = Stopwatch.StartNew()
            let released = ref false

            let release () =
                if not released.Value then
                    released.Value <- true

                    try
                        mutex.ReleaseMutex()
                    with _ ->
                        ()

                    mutex.Dispose()

            // One attempt: `Ok true` took it, `Ok false` timed out, `Error`
            // took it ABANDONED — the kernel's word that the holder died.
            let attempt (wait: TimeSpan) =
                try
                    Ok(mutex.WaitOne wait)
                with :? AbandonedMutexException ->
                    Error()

            let held admission = {
                Admission = admission
                Release = release
            }

            match attempt TimeSpan.Zero with
            | Ok true -> held (Admission.Acquired TimeSpan.Zero)
            | Error() ->
                options.Report
                    "VerifyAll gate: the previous holder exited without releasing it (the run died); taking it over."

                held (Admission.Recovered TimeSpan.Zero)
            | Ok false ->
                options.Report(
                    sprintf
                        "VerifyAll gate: another VerifyAll holds it (%s) — waiting up to %s. Set %s=1 to run unserialised."
                        (options.DescribeHolder())
                        (minutes options.Bound)
                        OptOutVariable
                )

                let rec wait () =
                    let remaining = options.Bound - clock.Elapsed

                    if remaining <= TimeSpan.Zero then
                        options.Report(
                            sprintf
                                "VerifyAll gate: waited %s and the holder has not released it — treating it as STALE and proceeding UNSERIALISED. If it is still running, this run will contend with it."
                                (minutes clock.Elapsed)
                        )

                        release ()

                        {
                            Admission = Admission.PastStaleHolder clock.Elapsed
                            Release = noop
                        }
                    else
                        let slice =
                            if remaining < options.Slice then
                                remaining
                            else
                                options.Slice

                        match attempt slice with
                        | Ok true ->
                            options.Report(sprintf "VerifyAll gate: acquired after waiting %s." (minutes clock.Elapsed))
                            held (Admission.Acquired clock.Elapsed)
                        | Error() ->
                            options.Report(
                                sprintf
                                    "VerifyAll gate: the holder exited without releasing it after %s; taking it over."
                                    (minutes clock.Elapsed)
                            )

                            held (Admission.Recovered clock.Elapsed)
                        | Ok false ->
                            options.Report(
                                sprintf
                                    "VerifyAll gate: still held (%s) — waited %s of %s."
                                    (options.DescribeHolder())
                                    (minutes clock.Elapsed)
                                    (minutes options.Bound)
                            )

                            wait ()

                wait ()

// ─── The holder note ─────────────────────────────────────────────────
//
// The kernel says only that the mutex is held, not by whom. A holder
// writes a one-line note beside the repository's common git directory —
// its pid, when it started, which worktree — and a waiter reads it for
// the progress line, so a thirty-minute wait names what it is waiting
// for. Diagnostic ONLY: the note never decides admission (the mutex
// does), so a note left behind by a killed run misleads a log line for
// one gate and wedges nothing.

/// Where the note lives for a given common git directory.
let holderNotePath (commonDir: string) =
    Path.Combine(commonDir, "toolup-verify-gate", "holder")

/// Write the note. Best-effort: a read-only `.git` costs the diagnostic,
/// not the gate.
let writeHolderNote (path: string) (workingDir: string) =
    try
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        File.WriteAllText(
            path,
            sprintf
                "pid %d started %s in %s"
                Environment.ProcessId
                (DateTimeOffset.Now.ToString "HH:mm 'on' yyyy-MM-dd")
                workingDir
        )
    with _ ->
        ()

/// Read the note, or say that there is none.
let readHolderNote (path: string) =
    try
        if File.Exists path then
            File.ReadAllText(path).Trim()
        else
            "no holder note — a run started before this gate existed, or one whose note could not be written"
    with _ ->
        "holder note unreadable"

/// Delete the note. Best-effort, as the write is.
let clearHolderNote (path: string) =
    try
        if File.Exists path then
            File.Delete path
    with _ ->
        ()

// ─── The admission record ────────────────────────────────────────────

/// One gated run as `verify-runs.jsonl` records it, and the fold
/// `GateTimings` runs over the records.
module Runs =

    /// The file, under the repository's common git directory.
    let path (commonDir: string) =
        Path.Combine(commonDir, "fuaran-gate", "verify-runs.jsonl")

    type Leg = {
        Name: string
        /// `pass` | `fail` | `pending`.
        Verdict: string
        Cases: int option
        Seconds: float
    }

    type Run = {
        At: DateTimeOffset
        Target: string
        Lane: string
        /// `acquired` | `recovered` | `disabled` | `past-stale-holder` | `unavailable`.
        Admission: string
        WaitedSeconds: float
        ElapsedSeconds: float
        Exit: int
        /// The machine that ran it. `None` on a line written before the
        /// journal recorded it; the fold reads such a line as the folding
        /// machine's, since the journal lives in one clone's git directory.
        Machine: string option
        Legs: Leg list
    }

    let admissionName (admission: Admission) =
        match admission with
        | Admission.Acquired _ -> "acquired"
        | Admission.Recovered _ -> "recovered"
        | Admission.Disabled -> "disabled"
        | Admission.PastStaleHolder _ -> "past-stale-holder"
        | Admission.Unavailable _ -> "unavailable"

    let waited (admission: Admission) =
        match admission with
        | Admission.Acquired w
        | Admission.Recovered w
        | Admission.PastStaleHolder w -> w
        | Admission.Disabled
        | Admission.Unavailable _ -> TimeSpan.Zero

    let private verdictName (verdict: Verdict) =
        match verdict with
        | Verdict.Pass _ -> "pass"
        | Verdict.Fail _ -> "fail"
        | Verdict.Pending _ -> "pending"

    /// The legs of an aggregate's results, as recorded.
    let legsOf (results: (string * Verdict * TimeSpan) list) : Leg list = [
        for name, verdict, elapsed in results do
            {
                Name = name
                Verdict = verdictName verdict
                Cases =
                    match verdict with
                    | Verdict.Pass cases -> cases
                    | Verdict.Fail _
                    | Verdict.Pending _ -> None
                Seconds = elapsed.TotalSeconds
            }
    ]

    /// One record as one JSON line.
    let render (run: Run) : string =
        use stream = new MemoryStream()

        do
            use w = new Text.Json.Utf8JsonWriter(stream)
            w.WriteStartObject()
            w.WriteString("at", run.At.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
            w.WriteString("target", run.Target)
            w.WriteString("lane", run.Lane)
            w.WriteString("admission", run.Admission)
            w.WriteNumber("waitedSeconds", Math.Round(run.WaitedSeconds, 1))
            w.WriteNumber("elapsedSeconds", Math.Round(run.ElapsedSeconds, 1))
            w.WriteNumber("exit", run.Exit)
            run.Machine |> Option.iter (fun m -> w.WriteString("machine", m))
            w.WriteStartArray "legs"

            for leg in run.Legs do
                w.WriteStartObject()
                w.WriteString("name", leg.Name)
                w.WriteString("verdict", leg.Verdict)
                leg.Cases |> Option.iter (fun c -> w.WriteNumber("cases", c))
                w.WriteNumber("seconds", Math.Round(leg.Seconds, 1))
                w.WriteEndObject()

            w.WriteEndArray()
            w.WriteEndObject()

        Encoding.UTF8.GetString(stream.ToArray())

    /// Parse the file's text: one record per non-blank line; a line that is
    /// not a record is skipped and counted, never fatal — the file is a log.
    let parse (text: string) : Run list * int =
        let lines =
            text.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.Trim() <> "")

        let parsed =
            lines
            |> Array.map (fun line ->
                try
                    use doc = Text.Json.JsonDocument.Parse line
                    let r = doc.RootElement

                    let str (name: string) =
                        match r.TryGetProperty name with
                        | true, v -> v.GetString()
                        | _ -> ""

                    let num (name: string) =
                        match r.TryGetProperty name with
                        | true, v -> v.GetDouble()
                        | _ -> 0.

                    let legs =
                        match r.TryGetProperty "legs" with
                        | true, arr -> [
                            for l in arr.EnumerateArray() do
                                {
                                    Name = l.GetProperty("name").GetString()
                                    Verdict = l.GetProperty("verdict").GetString()
                                    Cases =
                                        match l.TryGetProperty "cases" with
                                        | true, c -> Some(c.GetInt32())
                                        | _ -> None
                                    Seconds = l.GetProperty("seconds").GetDouble()
                                }
                          ]
                        | _ -> []

                    Some {
                        At = DateTimeOffset.Parse(str "at", Globalization.CultureInfo.InvariantCulture)
                        Target = str "target"
                        Lane = str "lane"
                        Admission = str "admission"
                        WaitedSeconds = num "waitedSeconds"
                        ElapsedSeconds = num "elapsedSeconds"
                        Exit = int (num "exit")
                        Machine =
                            match r.TryGetProperty "machine" with
                            | true, v -> Some(v.GetString())
                            | _ -> None
                        Legs = legs
                    }
                with _ ->
                    None)

        List.ofArray (Array.choose id parsed), (parsed |> Array.filter Option.isNone |> Array.length)

    /// Append one record. Best-effort, as the holder note is: a read-only
    /// `.git` costs the record, not the gate.
    let append (commonDir: string) (run: Run) =
        try
            let file = path commonDir
            Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
            File.AppendAllText(file, render run + "\n")
        with _ ->
            ()

    let private median (xs: float list) =
        match List.sort xs with
        | [] -> 0.
        | sorted ->
            let n = sorted.Length

            if n % 2 = 1 then
                sorted[n / 2]
            else
                (sorted[n / 2 - 1] + sorted[n / 2]) / 2.

    let private clock (seconds: float) =
        let t = TimeSpan.FromSeconds seconds
        sprintf "%d:%02d" (int t.TotalMinutes) t.Seconds

    /// The admission line a run would have printed, reproduced from its record.
    let admissionLine (run: Run) =
        sprintf
            "%s %s gate: %s after waiting %s — %s lane, %s, exit %d"
            (run.At.ToUniversalTime().ToString "yyyy-MM-dd HH:mm")
            run.Target
            run.Admission
            (clock run.WaitedSeconds)
            run.Lane
            (clock run.ElapsedSeconds)
            run.Exit

    /// The fold `GateTimings` prints: the runs since `since` (all when
    /// `None`), each run's admission line, then per lane the count and
    /// median wall clock, then per row the median seconds over the runs
    /// that ran it — the figure a row's `Budget` is re-derived from.
    let report (now: DateTimeOffset) (since: TimeSpan option) (runs: Run list) : string list =
        let runs =
            match since with
            | Some window -> runs |> List.filter (fun r -> now - r.At <= window)
            | None -> runs

        match runs with
        | [] -> [ "GateTimings: no recorded runs in the window." ]
        | runs -> [
            sprintf "GateTimings: %d recorded run(s)." runs.Length

            for run in runs do
                "  " + admissionLine run

            "Per lane (median wall clock over the runs that completed, exit 0):"

            for lane, group in runs |> List.groupBy _.Lane do
                let green = group |> List.filter (fun r -> r.Exit = 0) |> List.map _.ElapsedSeconds

                sprintf
                    "  %-9s %d run(s), %d green, median %s, median wait %s"
                    lane
                    group.Length
                    green.Length
                    (clock (median green))
                    (clock (median (group |> List.map _.WaitedSeconds)))

            "Per row (median seconds over the runs that ran it):"

            for name, legs in runs |> List.collect _.Legs |> List.groupBy _.Name do
                sprintf "  %-24s %d run(s), median %s" name legs.Length (clock (median (legs |> List.map _.Seconds)))
          ]

    /// A run that passed: exit 0 and no leg failed. Both, because a line
    /// written before the gate recorded the driver's exit code carries
    /// `exit 0` over a failed leg.
    let green (run: Run) =
        run.Exit = 0 && not (run.Legs |> List.exists (fun l -> l.Verdict = "fail"))

    /// Nearest-rank 90th percentile.
    let private p90 (xs: float list) =
        match List.sort xs with
        | [] -> 0.
        | sorted -> sorted[int (Math.Ceiling(0.9 * float sorted.Length)) - 1]

    let private tenth (x: float) =
        Math.Round(x, 1, MidpointRounding.AwayFromZero)

    /// The record `GateTimings --record` writes, folded from `runs`: the
    /// window is `width` ending at the newest run (so the same journal folds
    /// to the same record whenever it is folded); lanes are the green
    /// `VerifyAll` runs per lane; rows are the passing legs of the green
    /// full-lane `VerifyAll` runs, for the rows named in `rowNames` (the
    /// rows `VerifyAll` runs in the full lane). A run with no machine on
    /// its line is `machine`'s. `Error` when the window holds no run; the
    /// record carries no commit (`FoldedAt`), which the caller stamps.
    let fold
        (machine: string)
        (rowNames: string list)
        (width: TimeSpan)
        (runs: Run list)
        : Result<GateManifest.Timings, string> =
        match runs with
        | [] -> Error "the journal holds no runs"
        | runs ->
            let until = runs |> List.map _.At |> List.max
            let since = until - width
            let window = runs |> List.filter (fun r -> r.At >= since)
            let verifyAll = window |> List.filter (fun r -> r.Target = "VerifyAll" && green r)
            let known = set rowNames

            Ok {
                Since = since
                Until = until
                Runs = window.Length
                Machines =
                    window
                    |> List.map (fun r -> r.Machine |> Option.defaultValue machine)
                    |> List.distinct
                    |> List.sort
                Lanes = [
                    for lane, group in verifyAll |> List.groupBy _.Lane |> List.sortBy fst do
                        let wall = group |> List.map _.ElapsedSeconds

                        lane,
                        {
                            GateManifest.LaneTiming.Median = tenth (median wall)
                            P90 = tenth (p90 wall)
                            Runs = wall.Length
                        }
                ]
                Rows = [
                    let legs =
                        verifyAll
                        |> List.filter (fun r -> r.Lane = "full")
                        |> List.collect _.Legs
                        |> List.filter (fun l -> l.Verdict = "pass" && Set.contains l.Name known)

                    for name, legs in legs |> List.groupBy _.Name |> List.sortBy fst do
                        name,
                        {
                            GateManifest.RowTiming.Median = tenth (median (legs |> List.map _.Seconds))
                            Runs = legs.Length
                        }
                ]
                FoldedAt = None
            }

// ─── The driver's entry point ────────────────────────────────────────

/// Run `body` under the gate `name` with `options`, writing the holder
/// note at `notePath` (when there is one) for as long as the gate is
/// held. The seam the driver and the tests share: the driver resolves
/// the name from the repository, a test mints one of its own — so a test
/// never contends for the gate the `VerifyAll` that is RUNNING it holds.
let aroundWith
    (name: string)
    (notePath: string option)
    (options: Options)
    (workingDir: string)
    (record: Admission * TimeSpan * int -> unit)
    (body: unit -> 'a)
    : 'a =
    let held = acquire name options

    let owns =
        match held.Admission with
        | Admission.Acquired _
        | Admission.Recovered _ -> true
        | Admission.Disabled
        | Admission.PastStaleHolder _
        | Admission.Unavailable _ -> false

    if owns then
        notePath |> Option.iter (fun p -> writeHolderNote p workingDir)

    let clock = Stopwatch.StartNew()
    let mutable exit = 1

    try
        let result = body ()
        exit <- 0
        result
    finally
        if owns then
            notePath |> Option.iter clearHolderNote

        held.Release()

        record (held.Admission, clock.Elapsed, exit)

/// Run `body` under the gate when `argv` requests a gated target, else
/// run it plainly. The key is resolved from `workingDir` — the directory
/// `Build.fsproj` was run in, which `verify.ps1` sets to the repository
/// root — and the wait is reported on stdout, where the gate's transcript
/// already goes.
let around (workingDir: string) (argv: string[]) (body: unit -> int) : int =
    let target = requestedTarget argv
    let rows = manifestAt workingDir

    if not (Set.contains target (GateManifest.gatedTargets rows)) then
        body ()
    else
        let key, commonDir =
            match commonGitDir workingDir with
            | Ok dir -> keyFor dir, Some dir
            | Error reason ->
                printfn
                    "VerifyAll gate: %s — keying the gate on the working directory instead, so only runs from this exact directory serialise."
                    reason

                keyFor workingDir, None

        let notePath = commonDir |> Option.map holderNotePath

        let options = {
            Bound = waitBoundFromEnvironment (defaultWaitBound rows)
            Slice = DefaultSlice
            Report = printfn "%s"
            DescribeHolder =
                fun () ->
                    match notePath with
                    | Some p -> readHolderNote p
                    | None -> "holder unknown — not a git repository"
        }

        // The driver reports a failed target by its exit code, not by
        // throwing, so the record takes the code the body returned.
        let code = ref 0

        let record (admission, elapsed: TimeSpan, thrown) =
            let exit = if thrown <> 0 then thrown else code.Value

            commonDir
            |> Option.iter (fun dir ->
                Runs.append dir {
                    At = DateTimeOffset.UtcNow
                    Target = target
                    Lane = TestLane.name TestLane.current
                    Admission = Runs.admissionName admission
                    WaitedSeconds = (Runs.waited admission).TotalSeconds
                    ElapsedSeconds = elapsed.TotalSeconds
                    Exit = exit
                    Machine = Some Environment.MachineName
                    Legs = Runs.legsOf (Gate.lastRun ())
                })

        aroundWith (mutexName key) notePath options workingDir record (fun () ->
            code.Value <- body ()
            code.Value)
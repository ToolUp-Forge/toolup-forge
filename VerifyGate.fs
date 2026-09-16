// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 735 — the `VerifyAll` gate serialises itself, machine-wide, per
/// repository.
///
/// **What this is for.** On 2026-08-27 three to four worktrees of this
/// repository ran the ~9,000-test Platform pack at once and each crawled
/// at a few percent of a core: a quiet-machine gate of ~17–21 minutes
/// became more than two hours, and two runs were abandoned. That campaign
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
/// for that the wait is bounded (`TOOLUP_VERIFY_GATE_WAIT_MINUTES`,
/// default 60: comfortably past a full lane's ~16 minutes plus two of the
/// advisory lanes that might be queued ahead of it, and short of the
/// queue's own 90-minute claim bound). Past the bound the run PROCEEDS
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
/// not the pack) and a lane that runs seconds waits seconds. Nothing else
/// does: `verify.ps1`'s other steps are a format check and a build, the
/// CI-only verify targets (`VerifyFable`, `VerifyTemplates`,
/// `VerifyPublishedPackages`, …) are not part of the gate script and did
/// not feature in the measured contention, and a single pack run directly
/// (`dotnet <pack>.dll --filter …`) never passes through this driver at
/// all. Widening the set is one edit to `gatedTargets` below.
///
/// **Why this file is at the repository root and FAKE-free.** The
/// `VerifyAll` target's body is published SDK surface
/// (`ToolUp.Platform.Build`), frozen at 0.23.0 for release; the gate is
/// forge's own build hygiene, so it lives beside `SdkManifest.fs`,
/// `SemVerBump.fs` and `TestLane.fs` — compiled into `Build.fsproj`,
/// source-linked into `ToolUp.Platform.Build.Tests` so the driver and its
/// go-red proofs run one implementation, and taking nothing beyond
/// `System` so that pack can compile it without FAKE.
module ToolUp.Forge.VerifyGate

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading

/// Set to any non-empty value other than `0` to skip the gate.
[<Literal>]
let OptOutVariable = "TOOLUP_VERIFY_NO_GATE_MUTEX"

/// The stale-holder bound in whole minutes; unset ⇒ `DefaultWaitBound`.
[<Literal>]
let WaitBoundVariable = "TOOLUP_VERIFY_GATE_WAIT_MINUTES"

/// See the header for the derivation.
let DefaultWaitBound = TimeSpan.FromMinutes 60.

/// How often a waiting run reports that it is still waiting.
let DefaultSlice = TimeSpan.FromSeconds 30.

/// The targets that take the gate. See the header for why this is
/// `VerifyAll` alone.
let gatedTargets = set [ "VerifyAll" ]

/// The target `argv` requests, read the way `ToolUp.Platform.Build.execute`
/// reads it: the first argument that is not an option, else `Run`. One
/// parser would be better than two, but that one is inside the frozen SDK
/// and a gate that consulted a different rule from the runner it wraps
/// would gate the wrong target.
let requestedTarget (argv: string[]) =
    match List.ofArray argv with
    | t :: _ when not (t.StartsWith("--", StringComparison.Ordinal)) -> t
    | _ -> "Run"

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
let waitBoundFromEnvironment () =
    match Environment.GetEnvironmentVariable WaitBoundVariable with
    | null
    | "" -> DefaultWaitBound
    | raw ->
        match Int32.TryParse(raw.Trim()) with
        | true, minutes when minutes > 0 -> TimeSpan.FromMinutes(float minutes)
        | _ -> DefaultWaitBound

/// `git rev-parse --git-common-dir` from `workingDir`, resolved to a full
/// path. `Error` names why it could not be had — git missing, not a
/// repository, a hung child — and the caller degrades rather than fails.
let commonGitDir (workingDir: string) : Result<string, string> =
    try
        let psi = ProcessStartInfo("git", "rev-parse --git-common-dir")
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use proc = Process.Start psi
        let output = proc.StandardOutput.ReadToEnd()

        if not (proc.WaitForExit 15000) then
            proc.Kill true
            Error "git rev-parse --git-common-dir did not return within 15 s"
        elif proc.ExitCode <> 0 then
            Error(sprintf "git rev-parse --git-common-dir exited %d (not a git repository?)" proc.ExitCode)
        else
            match output.Trim() with
            | "" -> Error "git rev-parse --git-common-dir printed nothing"
            | raw when Path.IsPathRooted raw -> Ok(Path.GetFullPath raw)
            | raw -> Ok(Path.GetFullPath(Path.Combine(workingDir, raw)))
    with e ->
        Error(sprintf "git could not be started: %s" e.Message)

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

    try
        body ()
    finally
        if owns then
            notePath |> Option.iter clearHolderNote

        held.Release()

/// Run `body` under the gate when `argv` requests a gated target, else
/// run it plainly. The key is resolved from `workingDir` — the directory
/// `Build.fsproj` was run in, which `verify.ps1` sets to the repository
/// root — and the wait is reported on stdout, where the gate's transcript
/// already goes.
let around (workingDir: string) (argv: string[]) (body: unit -> 'a) : 'a =
    let target = requestedTarget argv

    if not (Set.contains target gatedTargets) then
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
            Bound = waitBoundFromEnvironment ()
            Slice = DefaultSlice
            Report = printfn "%s"
            DescribeHolder =
                fun () ->
                    match notePath with
                    | Some p -> readHolderNote p
                    | None -> "holder unknown — not a git repository"
        }

        aroundWith (mutexName key) notePath options workingDir body
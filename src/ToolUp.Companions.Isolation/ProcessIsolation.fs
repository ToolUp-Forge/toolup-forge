// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Companions.Isolation

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading.Tasks

// ─── Phase 687 — the host side ────────────────────────────────────────
//
// Starts a worker, hands it one request over stdin, reads one response
// off stdout, and stands over it with a stopwatch and a memory sampler.
// Everything that can go wrong is a case of `IsolationRefusal`; nothing
// that happens in the child reaches the host as an exception, and a
// child that dies is a value.
//
// **How the worker is found — and why it needs no consumer hook.** The
// worker is this very assembly, started as
// `dotnet exec --runtimeconfig <host>.runtimeconfig.json --depsfile
// <host>.deps.json ToolUp.Companions.Isolation.dll --worker`. Handing
// the child the HOST's runtime config and dependency manifest means it
// resolves exactly the host's closure — every companion assembly and
// every `runtimes/<rid>/native/*` the host can load, the worker can
// load — without the consumer registering anything, shipping a second
// executable, or routing a magic argument through its own `main`. A
// layout the resolver cannot see (a single-file publish has no
// `deps.json` on disk) is `WorkerUnavailable` with the reason named,
// and `IsolationMode.OutOfProcessWith` takes an explicit launcher.

/// The out-of-process implementation of `ICompanionIsolation`.
[<RequireQualifiedAccess>]
module ProcessIsolation =
    /// How much of the worker's stderr is kept for the crash
    /// diagnostic. A tail, because a native abort message is at the end.
    [<Literal>]
    let DiagnosticTailChars = 4096

    /// How often the watchdog samples the worker's resident set and
    /// checks the clock.
    let private samplePeriod = TimeSpan.FromMilliseconds 10.0

    let private isDotnetMuxer (path: string) =
        let name = Path.GetFileNameWithoutExtension path
        String.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)

    let private existingFile (path: string) =
        if not (String.IsNullOrWhiteSpace path) && File.Exists path then
            Some path
        else
            None

    /// Locate the `dotnet` muxer: the explicit `DOTNET_HOST_PATH`, the
    /// current process when it IS the muxer, `DOTNET_ROOT`, the shared
    /// framework's own root, then `PATH` — in that order.
    let resolveDotnetHost () : Result<string, string> =
        let exeName =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                "dotnet.exe"
            else
                "dotnet"

        let fromEnv name =
            existingFile (Environment.GetEnvironmentVariable name)

        let fromRoot (root: string) =
            if String.IsNullOrWhiteSpace root then
                None
            else
                existingFile (Path.Combine(root, exeName))

        let fromProcess () =
            match Environment.ProcessPath with
            | null -> None
            | path when isDotnetMuxer path -> existingFile path
            | _ -> None

        let fromRuntimeDirectory () =
            // <root>/shared/Microsoft.NETCore.App/<version>/ → <root>
            let runtimeDir =
                RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)

            try
                let root = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."))
                fromRoot root
            with _ ->
                None

        let fromPath () =
            match Environment.GetEnvironmentVariable "PATH" with
            | null -> None
            | path ->
                path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryPick (fun dir ->
                    try
                        existingFile (Path.Combine(dir, exeName))
                    with _ ->
                        None)

        [
            fun () -> fromEnv "DOTNET_HOST_PATH"
            fromProcess
            fun () -> fromRoot (Environment.GetEnvironmentVariable "DOTNET_ROOT")
            fromRuntimeDirectory
            fromPath
        ]
        |> List.tryPick (fun probe -> probe ())
        |> function
            | Some path -> Ok path
            | None ->
                Error
                    "no dotnet host found: set DOTNET_HOST_PATH or DOTNET_ROOT, or compose IsolationMode.OutOfProcessWith an explicit WorkerLauncher"

    /// Locate the host's `deps.json`: the manifests the runtime was
    /// started with (`APP_CONTEXT_DEPS_FILES`), else the entry
    /// assembly's own beside it.
    let private resolveDepsFile () : Result<string, string> =
        let fromContext =
            match AppContext.GetData "APP_CONTEXT_DEPS_FILES" with
            | :? string as files ->
                files.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryPick existingFile
            | _ -> None

        let fromEntryAssembly () =
            match System.Reflection.Assembly.GetEntryAssembly() with
            | null -> None
            | entry ->
                let name = entry.GetName().Name
                existingFile (Path.Combine(AppContext.BaseDirectory, name + ".deps.json"))

        match fromContext |> Option.orElseWith fromEntryAssembly with
        | Some deps -> Ok deps
        | None ->
            Error
                "no deps.json found beside the host (a single-file or trimmed publish?); compose IsolationMode.OutOfProcessWith an explicit WorkerLauncher"

    /// Resolve the launcher for THIS host: the muxer, the host's
    /// runtime config + dependency manifest, and this assembly as the
    /// worker.
    let resolveLauncher () : Result<WorkerLauncher, string> =
        match resolveDotnetHost (), resolveDepsFile () with
        | Error e, _
        | _, Error e -> Error e
        | Ok dotnet, Ok deps ->
            let runtimeConfig =
                deps.Substring(0, deps.Length - ".deps.json".Length) + ".runtimeconfig.json"

            let worker = typeof<IsolatedRequest>.Assembly.Location

            if not (File.Exists runtimeConfig) then
                Error $"no runtimeconfig.json beside the host's deps.json ({runtimeConfig})"
            elif String.IsNullOrEmpty worker || not (File.Exists worker) then
                Error
                    "the isolation assembly has no on-disk location (a single-file publish?); compose IsolationMode.OutOfProcessWith an explicit WorkerLauncher"
            else
                Ok {
                    FileName = dotnet
                    Arguments = [
                        "exec"
                        "--runtimeconfig"
                        runtimeConfig
                        "--depsfile"
                        deps
                        worker
                        IsolationWorker.WorkerFlag
                    ]
                }

    /// A bounded tail of text, for the stderr diagnostic.
    type private Tail(capacity: int) =
        let buffer = StringBuilder()
        let gate = obj ()

        member _.Append(line: string) =
            lock gate (fun () ->
                buffer.AppendLine line |> ignore

                if buffer.Length > capacity then
                    buffer.Remove(0, buffer.Length - capacity) |> ignore)

        override _.ToString() = lock gate (fun () -> buffer.ToString())

    [<RequireQualifiedAccess>]
    type private Watch =
        | Answered
        | Killed of IsolationRefusal

    let private sampleResidentSet (proc: Process) : int64 option =
        try
            proc.Refresh()
            if proc.HasExited then None else Some proc.WorkingSet64
        with _ ->
            None

    let private kill (proc: Process) =
        try
            if not proc.HasExited then
                proc.Kill true
        with _ ->
            ()

    // ─── The kernel-enforced memory cap (Windows Job Object) ──────────
    //
    // The FIRST line of the memory cap. The resident-set sampler below
    // is a poll: a native parser that commits memory faster than the
    // host samples it — the recorded instance is a MusicXML `<forward>`
    // with a duration of 2^31-1 that libverovio allocated against at
    // about a gigabyte a second, to 126 GB inside one process, four
    // times, taking the whole machine each time because Windows has no
    // OOM killer — overshoots the cap
    // by however much it can commit between two samples, and a
    // sampler is exactly as fast as the host's scheduler lets it be.
    // A Job Object is not a poll: the kernel refuses the commit that
    // would cross `JobMemoryLimit` / `ProcessMemoryLimit`, in the
    // allocating thread, before any byte is touched. The refused
    // allocation is reported to the host on the job's I/O completion
    // port (`JOB_OBJECT_MSG_*_MEMORY_LIMIT`), and the host kills the
    // worker and answers `MemoryCapExceeded`. `KILL_ON_JOB_CLOSE`
    // ties the worker's life to the job handle, so a host that dies
    // takes its worker with it rather than orphaning it.
    //
    // What a refused commit looks like from INSIDE the worker: a
    // managed allocation is an `OutOfMemoryException`, which the worker
    // catches and answers as `Rejected`; a native `malloc` returns
    // NULL and the parser does whatever it does next. Either way the
    // worker may answer, cleanly, before the host has read the port —
    // so the port is consulted on EVERY exit path, and a cap hit
    // overrides whatever the worker said: the answer was produced by a
    // process the kernel had already refused, and is not evidence.
    //
    // The limit is on COMMIT charge, not resident set — stricter than
    // the sampler, which is what a first line should be. The
    // assignment window: `Process.Start` returns before the child
    // runs any user code, and the job is assigned immediately after;
    // the muxer's own start-up (hostfxr, the runtime) is what fills
    // that window, and no request bytes have reached the worker yet.
    // Non-Windows hosts have no Job Object; there the sampler and the
    // runtime's own `GCHeapHardLimit` are the whole cap, and the
    // README says so.
    module private Kernel32 =
        [<Literal>]
        let InfoClassAssociateCompletionPort = 7

        [<Literal>]
        let InfoClassExtendedLimit = 9

        [<Literal>]
        let JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100u

        [<Literal>]
        let JOB_OBJECT_LIMIT_JOB_MEMORY = 0x200u

        [<Literal>]
        let JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000u

        [<Literal>]
        let JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT = 9u

        [<Literal>]
        let JOB_OBJECT_MSG_JOB_MEMORY_LIMIT = 10u

        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type IoCounters =
            val mutable ReadOperationCount: uint64
            val mutable WriteOperationCount: uint64
            val mutable OtherOperationCount: uint64
            val mutable ReadTransferCount: uint64
            val mutable WriteTransferCount: uint64
            val mutable OtherTransferCount: uint64

        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type JobObjectBasicLimitInformation =
            val mutable PerProcessUserTimeLimit: int64
            val mutable PerJobUserTimeLimit: int64
            val mutable LimitFlags: uint32
            val mutable MinimumWorkingSetSize: unativeint
            val mutable MaximumWorkingSetSize: unativeint
            val mutable ActiveProcessLimit: uint32
            val mutable Affinity: unativeint
            val mutable PriorityClass: uint32
            val mutable SchedulingClass: uint32

        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type JobObjectExtendedLimitInformation =
            val mutable BasicLimitInformation: JobObjectBasicLimitInformation
            val mutable IoInfo: IoCounters
            val mutable ProcessMemoryLimit: unativeint
            val mutable JobMemoryLimit: unativeint
            val mutable PeakProcessMemoryUsed: unativeint
            val mutable PeakJobMemoryUsed: unativeint

        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type JobObjectAssociateCompletionPort =
            val mutable CompletionKey: nativeint
            val mutable CompletionPort: nativeint

        [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
        extern nativeint CreateJobObjectW(nativeint lpJobAttributes, string lpName)

        [<DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetInformationJobObject")>]
        extern bool SetInformationJobObjectLimits(
            nativeint hJob,
            int infoClass,
            JobObjectExtendedLimitInformation& info,
            uint32 size
        )

        [<DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetInformationJobObject")>]
        extern bool SetInformationJobObjectPort(
            nativeint hJob,
            int infoClass,
            JobObjectAssociateCompletionPort& info,
            uint32 size
        )

        [<DllImport("kernel32.dll", SetLastError = true)>]
        extern bool QueryInformationJobObject(
            nativeint hJob,
            int infoClass,
            JobObjectExtendedLimitInformation& info,
            uint32 size,
            nativeint returnLength
        )

        [<DllImport("kernel32.dll", SetLastError = true)>]
        extern bool AssignProcessToJobObject(nativeint hJob, nativeint hProcess)

        [<DllImport("kernel32.dll", SetLastError = true)>]
        extern nativeint CreateIoCompletionPort(
            nativeint fileHandle,
            nativeint existingPort,
            unativeint completionKey,
            uint32 concurrentThreads
        )

        [<DllImport("kernel32.dll", SetLastError = true)>]
        extern bool GetQueuedCompletionStatus(
            nativeint port,
            uint32& bytes,
            unativeint& key,
            nativeint& overlapped,
            uint32 timeoutMs
        )

        [<DllImport("kernel32.dll", SetLastError = true)>]
        extern bool CloseHandle(nativeint handle)

        let lastError () =
            ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message

    /// Whether this host can put the worker under a kernel-enforced
    /// memory cap. True on Windows (a Job Object); false elsewhere,
    /// where the cap is the resident-set sampler plus the worker's own
    /// `GCHeapHardLimit`.
    let kernelMemoryCapSupported = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    /// A Job Object around one worker, with its completion port.
    type private JobObject(job: nativeint, port: nativeint, cap: int64 option) =
        let mutable hit = false

        /// Whether the kernel has refused a commit against the cap —
        /// sticky, because reading the port consumes the message.
        member _.MemoryLimitHit =
            if hit then
                true
            else
                let mutable bytes = 0u
                let mutable key = 0un
                let mutable overlapped = 0n
                let mutable draining = true

                while draining do
                    if Kernel32.GetQueuedCompletionStatus(port, &bytes, &key, &overlapped, 0u) then
                        if
                            bytes = Kernel32.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT
                            || bytes = Kernel32.JOB_OBJECT_MSG_JOB_MEMORY_LIMIT
                        then
                            hit <- true
                    else
                        // A false return with no packet is the empty
                        // queue; with one it is a failed packet, which a
                        // job port never posts — either way, done.
                        draining <- false

                hit

        /// The highest commit charge the job reached — what the cap was
        /// measured against — or 0 when the query fails.
        member _.PeakCommit: int64 =
            let mutable info = Kernel32.JobObjectExtendedLimitInformation()

            if
                Kernel32.QueryInformationJobObject(
                    job,
                    Kernel32.InfoClassExtendedLimit,
                    &info,
                    uint32 (Marshal.SizeOf<Kernel32.JobObjectExtendedLimitInformation>()),
                    0n
                )
            then
                int64 (uint64 info.PeakJobMemoryUsed)
            else
                0L

        member _.Cap = cap

        /// Put `proc` under the job.
        member _.Assign(proc: Process) : Result<unit, string> =
            if Kernel32.AssignProcessToJobObject(job, proc.Handle) then
                Ok()
            else
                Error(Kernel32.lastError ())

        interface IDisposable with
            member _.Dispose() =
                // Closing the last handle to a KILL_ON_JOB_CLOSE job
                // terminates anything still in it.
                Kernel32.CloseHandle port |> ignore
                Kernel32.CloseHandle job |> ignore

        /// Create a job carrying `cap` (when given) as both the
        /// per-process and the job-wide commit limit, killing on close,
        /// with a completion port to hear the limit messages on.
        static member TryCreate(cap: int64 option) : Result<JobObject, string> =
            let job = Kernel32.CreateJobObjectW(0n, null)

            if job = 0n then
                Error $"CreateJobObject: {Kernel32.lastError ()}"
            else
                let mutable limits = Kernel32.JobObjectExtendedLimitInformation()
                limits.BasicLimitInformation.LimitFlags <- Kernel32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

                match cap with
                | Some cap ->
                    limits.BasicLimitInformation.LimitFlags <-
                        limits.BasicLimitInformation.LimitFlags
                        ||| Kernel32.JOB_OBJECT_LIMIT_PROCESS_MEMORY
                        ||| Kernel32.JOB_OBJECT_LIMIT_JOB_MEMORY

                    limits.ProcessMemoryLimit <- unativeint (uint64 cap)
                    limits.JobMemoryLimit <- unativeint (uint64 cap)
                | None -> ()

                let limitsSet =
                    Kernel32.SetInformationJobObjectLimits(
                        job,
                        Kernel32.InfoClassExtendedLimit,
                        &limits,
                        uint32 (Marshal.SizeOf<Kernel32.JobObjectExtendedLimitInformation>())
                    )

                if not limitsSet then
                    let reason = Kernel32.lastError ()
                    Kernel32.CloseHandle job |> ignore
                    Error $"SetInformationJobObject(limits): {reason}"
                else
                    let port = Kernel32.CreateIoCompletionPort(-1n, 0n, 0un, 1u)

                    if port = 0n then
                        let reason = Kernel32.lastError ()
                        Kernel32.CloseHandle job |> ignore
                        Error $"CreateIoCompletionPort: {reason}"
                    else
                        let mutable association = Kernel32.JobObjectAssociateCompletionPort()
                        association.CompletionKey <- job
                        association.CompletionPort <- port

                        let portSet =
                            Kernel32.SetInformationJobObjectPort(
                                job,
                                Kernel32.InfoClassAssociateCompletionPort,
                                &association,
                                uint32 (Marshal.SizeOf<Kernel32.JobObjectAssociateCompletionPort>())
                            )

                        if not portSet then
                            let reason = Kernel32.lastError ()
                            Kernel32.CloseHandle port |> ignore
                            Kernel32.CloseHandle job |> ignore
                            Error $"SetInformationJobObject(completion port): {reason}"
                        else
                            Ok(new JobObject(job, port, cap))

    /// Run `entry` over `request` in a fresh worker started by
    /// `launcher`, under `limits`.
    let run
        (limits: IsolationLimits)
        (launcher: WorkerLauncher)
        (entry: Type)
        (request: IsolatedRequest)
        : Async<Result<byte[], IsolationRefusal>> =
        async {
            let entryTypeName =
                match entry.AssemblyQualifiedName with
                | null -> entry.FullName
                | name -> name

            let psi = ProcessStartInfo(launcher.FileName)

            for argument in launcher.Arguments do
                psi.ArgumentList.Add argument

            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true

            match limits.MemoryCap with
            | Some cap ->
                // The managed half of the cap: the runtime refuses to grow
                // the GC heap past it, so a managed allocation burst is an
                // OutOfMemoryException inside the worker (→ `EntryFailed`)
                // rather than a wait for the sampler.
                psi.Environment["DOTNET_GCHeapHardLimit"] <- sprintf "0x%X" cap
            | None -> ()

            // The kernel cap comes first, before the child exists. A
            // host that asked for a cap and cannot get the kernel to
            // hold it is refused, not degraded: the sampler alone is
            // the configuration that lost a machine. With no cap the
            // job still buys KILL_ON_JOB_CLOSE, best-effort.
            let job: Result<JobObject option, string> =
                if kernelMemoryCapSupported then
                    match JobObject.TryCreate limits.MemoryCap, limits.MemoryCap with
                    | Ok job, _ -> Ok(Some job)
                    | Error reason, Some _ -> Error $"the memory cap cannot be kernel-enforced on this host: {reason}"
                    | Error _, None -> Ok None
                else
                    Ok None

            let disposeJob (job: JobObject option) =
                job |> Option.iter (fun j -> (j :> IDisposable).Dispose())

            let started: Result<Process * JobObject option, string> =
                match job with
                | Error reason -> Error reason
                | Ok job ->
                    try
                        Ok(Process.Start psi, job)
                    with ex ->
                        disposeJob job
                        Error $"{launcher.FileName}: {ex.Message}"

            let assigned: Result<Process * JobObject option, string> =
                match started with
                | Ok(proc, Some job) ->
                    match job.Assign proc, limits.MemoryCap with
                    | Ok(), _ -> Ok(proc, Some job)
                    | Error reason, Some _ ->
                        kill proc
                        proc.Dispose()
                        disposeJob (Some job)

                        Error
                            $"the memory cap cannot be kernel-enforced on this host (AssignProcessToJobObject): {reason}"
                    | Error _, None ->
                        disposeJob (Some job)
                        Ok(proc, None)
                | other -> other

            match assigned with
            | Error reason -> return Error(IsolationRefusal.WorkerUnavailable reason)
            | Ok(proc, job) ->
                use proc = proc

                use _ = job |> Option.map (fun j -> j :> IDisposable) |> Option.defaultValue null

                let capHit () =
                    job |> Option.exists (fun j -> j.MemoryLimitHit)

                let observedPeak () =
                    job |> Option.map (fun j -> j.PeakCommit) |> Option.defaultValue 0L

                let diagnostic = Tail DiagnosticTailChars

                proc.ErrorDataReceived.Add(fun args ->
                    match args.Data with
                    | null -> ()
                    | line -> diagnostic.Append line)

                proc.BeginErrorReadLine()

                // The request goes down on its own task so a child that
                // dies before reading it all cannot wedge the writer: a
                // broken pipe there is an IOException we discard, and the
                // read side reports what actually happened.
                let writer =
                    Task.Run(fun () ->
                        try
                            let stdin = proc.StandardInput.BaseStream
                            IsolationProtocol.writeRequest stdin entryTypeName request
                            stdin.Close()
                        with _ ->
                            ())

                let reader =
                    Task.Run(fun () ->
                        try
                            Ok(IsolationProtocol.readResponse proc.StandardOutput.BaseStream)
                        with ex ->
                            Error ex)

                let clock = Stopwatch.StartNew()
                let mutable watch = None

                while watch.IsNone do
                    if reader.IsCompleted then
                        watch <- Some Watch.Answered
                    elif clock.Elapsed > limits.Timeout then
                        kill proc
                        watch <- Some(Watch.Killed(IsolationRefusal.TimedOut limits.Timeout))
                    else
                        let tripped =
                            match limits.MemoryCap with
                            | None -> None
                            | Some cap when capHit () ->
                                // First line: the kernel refused a commit.
                                Some(cap, observedPeak ())
                            | Some cap ->
                                // Second line: the resident-set sampler.
                                sampleResidentSet proc
                                |> Option.filter (fun observed -> observed > cap)
                                |> Option.map (fun observed -> cap, observed)

                        match tripped with
                        | Some(cap, observed) ->
                            kill proc
                            watch <- Some(Watch.Killed(IsolationRefusal.MemoryCapExceeded(cap, observed)))
                        | None ->
                            let! _ = Task.WhenAny(reader, Task.Delay samplePeriod) |> Async.AwaitTask
                            ()

                // Let the child finish on its own once it has answered; a
                // worker that answers and then hangs is killed at the
                // deadline like any other.
                let remaining = limits.Timeout - clock.Elapsed

                if not (proc.WaitForExit(max 0 (int remaining.TotalMilliseconds))) then
                    kill proc

                proc.WaitForExit()
                do! writer |> Async.AwaitTask

                // The kernel may have refused a commit AFTER the last
                // poll and BEFORE the worker answered or died — a
                // managed OutOfMemoryException is caught inside the
                // worker and answered as a rejection, and a native
                // parser handed a NULL from malloc does whatever it
                // does next. Whatever it said, it said it as a process
                // the cap had already been enforced against; the cap
                // is the outcome.
                let capHitAfterAll =
                    match watch, limits.MemoryCap with
                    | Some(Watch.Killed _), _
                    | _, None -> None
                    | _, Some cap when capHit () -> Some(cap, observedPeak ())
                    | _, Some _ -> None

                match watch, capHitAfterAll with
                | _, Some(cap, observed) -> return Error(IsolationRefusal.MemoryCapExceeded(cap, observed))
                | Some(Watch.Killed refusal), _ -> return Error refusal
                | Some Watch.Answered, _
                | None, _ ->
                    let! outcome = reader |> Async.AwaitTask

                    match outcome with
                    | Ok(WorkerResponse.Answered bytes) -> return Ok bytes
                    | Ok(WorkerResponse.Rejected message) -> return Error(IsolationRefusal.EntryFailed message)
                    | Ok(WorkerResponse.Unresolvable reason) ->
                        return
                            Error(
                                IsolationRefusal.WorkerUnavailable
                                    $"the worker could not resolve the entry point: {reason}"
                            )
                    | Error ex ->
                        if proc.ExitCode <> WorkerExitCode.Answered then
                            return Error(IsolationRefusal.WorkerCrashed(proc.ExitCode, diagnostic.ToString()))
                        else
                            return
                                Error(
                                    IsolationRefusal.ProtocolViolation
                                        $"{ex.Message}; stderr: {diagnostic.ToString().Trim()}"
                                )
        }

    /// An `ICompanionIsolation` over an explicit launcher.
    let createWith (limits: IsolationLimits) (launcher: WorkerLauncher) : ICompanionIsolation =
        { new ICompanionIsolation with
            member _.Mode = IsolationMode.OutOfProcessWith(limits, launcher)
            member _.Run(entry, request) = run limits launcher entry request
        }

    /// An `ICompanionIsolation` over the launcher resolved from this
    /// host's layout. Resolution happens per call, so a host whose
    /// layout is unresolvable answers `WorkerUnavailable` per call
    /// rather than failing at composition — the composition-time
    /// question is `resolveLauncher`'s to ask, and
    /// `CompanionIsolation.preflight` asks it.
    let create (limits: IsolationLimits) : ICompanionIsolation =
        { new ICompanionIsolation with
            member _.Mode = IsolationMode.OutOfProcess limits

            member _.Run(entry, request) =
                match resolveLauncher () with
                | Error reason -> async { return Error(IsolationRefusal.WorkerUnavailable reason) }
                | Ok launcher -> run limits launcher entry request
        }
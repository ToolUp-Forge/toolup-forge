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

            let started =
                try
                    Ok(Process.Start psi)
                with ex ->
                    Error $"{launcher.FileName}: {ex.Message}"

            match started with
            | Error reason -> return Error(IsolationRefusal.WorkerUnavailable reason)
            | Ok proc ->
                use proc = proc
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
                        match limits.MemoryCap, sampleResidentSet proc with
                        | Some cap, Some observed when observed > cap ->
                            kill proc
                            watch <- Some(Watch.Killed(IsolationRefusal.MemoryCapExceeded(cap, observed)))
                        | _ ->
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

                match watch with
                | Some(Watch.Killed refusal) -> return Error refusal
                | Some Watch.Answered
                | None ->
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
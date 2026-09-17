// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Companions.Isolation

open System
open System.IO

// ─── Phase 687 — the worker side ──────────────────────────────────────
//
// The sacrificial process. It reads ONE request frame from stdin,
// instantiates the named entry point, invokes it, writes ONE response
// frame to stdout and exits. It holds no state between calls because
// there is exactly one call: a fresh process per request is what makes
// a crash containable and a memory cap meaningful, and the ~100 ms a
// .NET process costs to start is the price of that on an upload path
// that already spent longer receiving the bytes.
//
// Stdout is the pipe, so the worker redirects `Console.Out` to stderr
// before the entry point runs: a companion that logs to the console
// must not corrupt the response frame. Native code writing straight to
// file descriptor 1 is outside what a managed redirect can stop, and is
// what `IsolationRefusal.ProtocolViolation` exists to report.

/// The worker's exit codes. The response frame carries the typed
/// outcome; these are what the host sees when there is no frame.
[<RequireQualifiedAccess>]
module WorkerExitCode =
    /// A response frame was written.
    [<Literal>]
    let Answered = 0

    /// The request frame could not be read (truncated, wrong magic,
    /// wrong version). Nothing was invoked.
    [<Literal>]
    let BadRequest = 64

    /// The named entry point could not be instantiated. A response
    /// frame carrying `Unresolvable` was still written.
    [<Literal>]
    let Unresolvable = 65

/// The worker's main loop, factored so a test can run it over in-memory
/// streams without a process.
[<RequireQualifiedAccess>]
module IsolationWorker =
    /// The argument the launcher passes to select worker mode. Anything
    /// else on the command line is ignored.
    [<Literal>]
    let WorkerFlag = "--worker"

    /// Instantiate the entry point named by an assembly-qualified type
    /// name. The type resolves through the worker's own dependency
    /// closure — which is the HOST's, because the launcher hands the
    /// worker the host's `deps.json` — so any companion the host
    /// references is loadable here, natives included.
    let resolveEntry (entryTypeName: string) : Result<IIsolatedEntryPoint, string> =
        match Type.GetType(entryTypeName, false) with
        | null -> Error $"entry point type '{entryTypeName}' could not be loaded in the worker"
        | entryType when not (typeof<IIsolatedEntryPoint>.IsAssignableFrom entryType) ->
            Error $"entry point type '{entryType.FullName}' does not implement IIsolatedEntryPoint"
        | entryType ->
            try
                match Activator.CreateInstance entryType with
                | :? IIsolatedEntryPoint as entry -> Ok entry
                | _ -> Error $"entry point type '{entryType.FullName}' did not construct as IIsolatedEntryPoint"
            with ex ->
                Error
                    $"entry point type '{entryType.FullName}' could not be constructed: {ex.GetType().Name}: {ex.Message}"

    /// Invoke an already-resolved entry point, translating its `Error`
    /// and any exception into a `Rejected` response. A native fault is
    /// not an exception and does not come back through here — it ends
    /// the process, which is the behaviour the host is built around.
    let invoke (entry: IIsolatedEntryPoint) (request: IsolatedRequest) : WorkerResponse =
        try
            match entry.Invoke request with
            | Ok bytes -> WorkerResponse.Answered bytes
            | Error message -> WorkerResponse.Rejected message
        with ex ->
            WorkerResponse.Rejected $"{ex.GetType().Name}: {ex.Message}"

    /// Serve one request from `input`, answering on `output`. Returns
    /// the process exit code. Pure over the two streams apart from the
    /// entry point's own effects.
    let serve (input: Stream) (output: Stream) : int =
        match
            (try
                Ok(IsolationProtocol.readRequest input)
             with
             | :? EndOfStreamException as ex -> Error ex.Message
             | :? InvalidDataException as ex -> Error ex.Message)
        with
        | Error _ -> WorkerExitCode.BadRequest
        | Ok(entryTypeName, request) ->
            match resolveEntry entryTypeName with
            | Error reason ->
                IsolationProtocol.writeResponse output (WorkerResponse.Unresolvable reason)
                WorkerExitCode.Unresolvable
            | Ok entry ->
                IsolationProtocol.writeResponse output (invoke entry request)
                WorkerExitCode.Answered

    /// The process entry: bind the pipe to the standard streams, move
    /// `Console.Out` aside, serve. Called from `Program.main` when the
    /// worker flag is present.
    let runProcess () : int =
        use input = Console.OpenStandardInput()
        use output = Console.OpenStandardOutput()
        // Anything the entry point writes to the console goes to stderr,
        // where the host collects it as the crash diagnostic tail. The
        // response frame is the only thing that may reach stdout.
        Console.SetOut Console.Error
        serve input output
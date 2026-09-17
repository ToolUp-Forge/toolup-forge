// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the native-boundary isolation seam: the pipe, the
/// worker, the process host under each of its refusals, and the
/// composition-profile gate. Every out-of-process case starts a REAL
/// child through the resolved launcher — the same `dotnet exec` a
/// deployment's host would start — so what is pinned is the mechanism,
/// not a simulation of it.
module ToolUp.Companions.Isolation.Tests.IsolationTests

open System
open System.IO
open Expecto
open ToolUp.Companions.Isolation
open ToolUp.Companions.Isolation.Tests.Entries
open ToolUp.Platform

let private request (args: string list) (input: byte[]) : IsolatedRequest = { Args = args; Input = input }

/// Every byte value once, so a text-mode pipe or an encoding slip
/// would corrupt it.
let private allBytes = Array.init 256 byte

let private limits (timeout: TimeSpan) (cap: int64 option) = { Timeout = timeout; MemoryCap = cap }

let private outOfProcess (timeout: TimeSpan) (cap: int64 option) =
    ProcessIsolation.create (limits timeout cap)

let private expectEcho (args: string list) (input: byte[]) (outcome: Result<byte[], IsolationRefusal>) =
    match outcome with
    | Ok bytes ->
        let expected =
            Array.append (Text.Encoding.UTF8.GetBytes(String.Join("|", args) + "\n")) (Array.rev input)

        Expect.equal bytes expected "the echo entry's answer crossed the pipe intact"
    | Error refusal -> failtestf "expected the echo to answer; got %s" (IsolationRefusal.describe refusal)

let private protocolTests =
    testList "IsolationProtocol" [
        testCase "a request frame round-trips its type name, args and every byte value"
        <| fun () ->
            use stream = new MemoryStream()
            IsolationProtocol.writeRequest stream "Some.Type, Some.Assembly" (request [ "a"; ""; "ünïcode" ] allBytes)
            stream.Position <- 0L
            let typeName, decoded = IsolationProtocol.readRequest stream
            Expect.equal typeName "Some.Type, Some.Assembly" "type name"
            Expect.equal decoded.Args [ "a"; ""; "ünïcode" ] "args, including an empty one"
            Expect.equal decoded.Input allBytes "input bytes"
            Expect.equal stream.Position stream.Length "the frame was consumed exactly"

        testCase "each response shape round-trips"
        <| fun () ->
            for response in
                [
                    WorkerResponse.Answered allBytes
                    WorkerResponse.Answered [||]
                    WorkerResponse.Rejected "no"
                    WorkerResponse.Unresolvable "who?"
                ] do
                use stream = new MemoryStream()
                IsolationProtocol.writeResponse stream response
                stream.Position <- 0L
                Expect.equal (IsolationProtocol.readResponse stream) response "response"

        testCase "a truncated frame is an EndOfStreamException, never a partial value"
        <| fun () ->
            use full = new MemoryStream()
            IsolationProtocol.writeResponse full (WorkerResponse.Answered allBytes)
            let bytes = full.ToArray()
            use cut = new MemoryStream(bytes[.. bytes.Length - 10])

            Expect.throwsT<EndOfStreamException>
                (fun () -> IsolationProtocol.readResponse cut |> ignore)
                "a frame cut short"

        testCase "bytes that are not a frame are an InvalidDataException naming the expected frame"
        <| fun () ->
            use stream =
                new MemoryStream(Text.Encoding.ASCII.GetBytes "Welcome to dotnet 10.0.1\n")

            let message =
                try
                    IsolationProtocol.readResponse stream |> ignore
                    failtest "read a frame out of a banner"
                with :? InvalidDataException as ex ->
                    ex.Message

            Expect.stringContains message "response frame" "names the frame it expected"
    ]

let private workerTests =
    let serve (entry: Type) (req: IsolatedRequest) =
        use input = new MemoryStream()
        IsolationProtocol.writeRequest input entry.AssemblyQualifiedName req
        input.Position <- 0L
        use output = new MemoryStream()
        let exit = IsolationWorker.serve input output
        output.Position <- 0L

        exit,
        (if output.Length = 0L then
             None
         else
             Some(IsolationProtocol.readResponse output))

    testList "IsolationWorker (over in-memory streams)" [
        testCase "an answering entry point → Answered, exit 0"
        <| fun () ->
            let exit, response = serve typeof<EchoEntry> (request [ "x" ] allBytes)
            Expect.equal exit WorkerExitCode.Answered "exit code"

            match response with
            | Some(WorkerResponse.Answered bytes) -> expectEcho [ "x" ] allBytes (Ok bytes)
            | other -> failtestf "expected Answered; got %A" other

        testCase "an entry point's Error → Rejected, exit 0 (a rejection is an answer)"
        <| fun () ->
            let exit, response = serve typeof<RejectingEntry> (request [] allBytes)
            Expect.equal exit WorkerExitCode.Answered "exit code"
            Expect.equal response (Some(WorkerResponse.Rejected "rejected 256 bytes")) "response"

        testCase "an entry point that raises → Rejected carrying the exception, exit 0"
        <| fun () ->
            let exit, response = serve typeof<ThrowingEntry> (request [] [||])
            Expect.equal exit WorkerExitCode.Answered "exit code"

            match response with
            | Some(WorkerResponse.Rejected message) ->
                Expect.stringContains message "InvalidOperationException" "exception type"
                Expect.stringContains message "the entry point raised" "exception message"
            | other -> failtestf "expected Rejected; got %A" other

        testCase "a type without a parameterless constructor → Unresolvable, exit 65"
        <| fun () ->
            let exit, response = serve typeof<UnconstructibleEntry> (request [] [||])
            Expect.equal exit WorkerExitCode.Unresolvable "exit code"

            match response with
            | Some(WorkerResponse.Unresolvable reason) ->
                Expect.stringContains reason "UnconstructibleEntry" "names the type"
            | other -> failtestf "expected Unresolvable; got %A" other

        testCase "a type that is not an entry point → Unresolvable"
        <| fun () ->
            let _, response = serve typeof<string> (request [] [||])

            match response with
            | Some(WorkerResponse.Unresolvable reason) ->
                Expect.stringContains reason "does not implement IIsolatedEntryPoint" "reason"
            | other -> failtestf "expected Unresolvable; got %A" other

        testCase "garbage on stdin → exit 64 and no response frame"
        <| fun () ->
            use input = new MemoryStream(Text.Encoding.ASCII.GetBytes "not a frame")
            use output = new MemoryStream()
            Expect.equal (IsolationWorker.serve input output) WorkerExitCode.BadRequest "exit code"
            Expect.equal output.Length 0L "nothing written"
    ]

let private processTests =
    let generous = TimeSpan.FromSeconds 60.0

    testList "ProcessIsolation (real child processes)" [
        testCase "the launcher resolves from this host's own layout"
        <| fun () ->
            match ProcessIsolation.resolveLauncher () with
            | Error reason -> failtestf "launcher did not resolve: %s" reason
            | Ok launcher ->
                Expect.isTrue (File.Exists launcher.FileName) "the muxer exists"
                Expect.equal (List.last launcher.Arguments) IsolationWorker.WorkerFlag "ends with the worker flag"

                for argument in launcher.Arguments do
                    if argument.EndsWith ".json" || argument.EndsWith ".dll" then
                        Expect.isTrue (File.Exists argument) $"{argument} exists"

        testCase "an answering entry point answers, with every byte value intact"
        <| fun () ->
            let outcome =
                CompanionIsolation.run<EchoEntry> (outOfProcess generous None) (request [ "musicxml"; "" ] allBytes)
                |> Async.RunSynchronously

            expectEcho [ "musicxml"; "" ] allBytes outcome

        testCase "in-process and out-of-process answer byte-for-byte alike for trusted input"
        <| fun () ->
            let req = request [ "a"; "b" ] allBytes

            let inProcess =
                CompanionIsolation.run<EchoEntry> InProcessIsolation.instance req
                |> Async.RunSynchronously

            let outOfProc =
                CompanionIsolation.run<EchoEntry> (outOfProcess generous None) req
                |> Async.RunSynchronously

            Expect.equal outOfProc inProcess "same answer either side of the boundary"

        testCase "a clean rejection is EntryFailed with the entry point's message"
        <| fun () ->
            let outcome =
                CompanionIsolation.run<RejectingEntry> (outOfProcess generous None) (request [] allBytes)
                |> Async.RunSynchronously

            Expect.equal outcome (Error(IsolationRefusal.EntryFailed "rejected 256 bytes")) "typed rejection"

        testCase "an access violation in the worker is WorkerCrashed — and this process is still here"
        <| fun () ->
            let outcome =
                CompanionIsolation.run<AccessViolationEntry> (outOfProcess generous None) (request [] allBytes)
                |> Async.RunSynchronously

            match outcome with
            | Error(IsolationRefusal.WorkerCrashed(exitCode, diagnostic)) ->
                Expect.notEqual exitCode 0 "a crash is never exit 0"
                Expect.stringContains diagnostic "AccessViolationEntry" "the worker's stderr tail is the diagnostic"
            | other -> failtestf "expected WorkerCrashed; got %A" other

        testCase "a fail-fast in the worker is WorkerCrashed with the abort message in the diagnostic"
        <| fun () ->
            let outcome =
                CompanionIsolation.run<FailFastEntry> (outOfProcess generous None) (request [] [||])
                |> Async.RunSynchronously

            match outcome with
            | Error(IsolationRefusal.WorkerCrashed(exitCode, diagnostic)) ->
                Expect.notEqual exitCode 0 "a crash is never exit 0"
                Expect.stringContains diagnostic "simulated native abort" "diagnostic"
            | other -> failtestf "expected WorkerCrashed; got %A" other

        testCase "a hang is TimedOut at the limit, and the worker is killed"
        <| fun () ->
            let limit = TimeSpan.FromSeconds 2.0
            let clock = Diagnostics.Stopwatch.StartNew()

            let outcome =
                CompanionIsolation.run<SleepingEntry> (outOfProcess limit None) (request [ "60000" ] [||])
                |> Async.RunSynchronously

            Expect.equal outcome (Error(IsolationRefusal.TimedOut limit)) "typed timeout"
            Expect.isLessThan clock.Elapsed (TimeSpan.FromSeconds 20.0) "did not wait for the sleep to end"

        testCase "a native allocation burst is MemoryCapExceeded, and the worker is killed"
        <| fun () ->
            let cap = 256L * 1024L * 1024L

            let outcome =
                CompanionIsolation.run<AllocatingEntry> (outOfProcess generous (Some cap)) (request [] [||])
                |> Async.RunSynchronously

            match outcome with
            | Error(IsolationRefusal.MemoryCapExceeded(reported, observed)) ->
                Expect.equal reported cap "the cap it was run under"

                if ProcessIsolation.kernelMemoryCapSupported then
                    // The Job Object refuses the commit that would cross
                    // the cap, so the peak the kernel recorded is AT OR
                    // UNDER it: a run that ever observed more than the
                    // cap was not kernel-enforced and this must go red.
                    Expect.isLessThanOrEqual observed cap "the kernel refused before the cap was crossed"
                    Expect.isGreaterThan observed (cap / 2L) "the peak is the cap's neighbourhood, not a stale zero"
                else
                    Expect.isGreaterThan observed cap "the resident-set sample that tripped the sampler"
            | other -> failtestf "expected MemoryCapExceeded; got %A" other

        testCase "an entry point the worker cannot construct is WorkerUnavailable naming it"
        <| fun () ->
            let outcome =
                (outOfProcess generous None).Run(typeof<UnconstructibleEntry>, request [] [||])
                |> Async.RunSynchronously

            match outcome with
            | Error(IsolationRefusal.WorkerUnavailable reason) ->
                Expect.stringContains reason "UnconstructibleEntry" "names the type"
            | other -> failtestf "expected WorkerUnavailable; got %A" other

        testCase "a launcher that starts something other than the worker is ProtocolViolation"
        <| fun () ->
            let dotnet =
                match ProcessIsolation.resolveDotnetHost () with
                | Ok path -> path
                | Error reason -> failtestf "no dotnet host: %s" reason

            let isolation =
                ProcessIsolation.createWith (limits generous None) {
                    FileName = dotnet
                    Arguments = [ "--version" ]
                }

            let outcome =
                CompanionIsolation.run<EchoEntry> isolation (request [] [||])
                |> Async.RunSynchronously

            match outcome with
            | Error(IsolationRefusal.ProtocolViolation _) -> ()
            | other -> failtestf "expected ProtocolViolation; got %A" other

        testCase "a launcher that cannot start is WorkerUnavailable"
        <| fun () ->
            let isolation =
                ProcessIsolation.createWith (limits generous None) {
                    FileName = Path.Combine(Path.GetTempPath(), "no-such-worker-" + Guid.NewGuid().ToString "N")
                    Arguments = []
                }

            let outcome =
                CompanionIsolation.run<EchoEntry> isolation (request [] [||])
                |> Async.RunSynchronously

            match outcome with
            | Error(IsolationRefusal.WorkerUnavailable _) -> ()
            | other -> failtestf "expected WorkerUnavailable; got %A" other
    ]

let private compositionTests =
    testList "CompanionIsolation (composition + profile)" [
        testCase "InProcess is the mode the in-process instance reports, and it runs the entry here"
        <| fun () ->
            let isolation = CompanionIsolation.ofMode IsolationMode.InProcess
            Expect.equal isolation.Mode IsolationMode.InProcess "mode"
            Expect.isFalse (CompanionIsolation.isOutOfProcess isolation) "not out of process"

            CompanionIsolation.run<EchoEntry> isolation (request [ "t" ] allBytes)
            |> Async.RunSynchronously
            |> expectEcho [ "t" ] allBytes

        testCase "in-process, a rejection and a raise are EntryFailed exactly as they are across the pipe"
        <| fun () ->
            let isolation = InProcessIsolation.instance

            Expect.equal
                (CompanionIsolation.run<RejectingEntry> isolation (request [] allBytes)
                 |> Async.RunSynchronously)
                (Error(IsolationRefusal.EntryFailed "rejected 256 bytes"))
                "rejection"

            match
                CompanionIsolation.run<ThrowingEntry> isolation (request [] [||])
                |> Async.RunSynchronously
            with
            | Error(IsolationRefusal.EntryFailed message) ->
                Expect.stringContains message "the entry point raised" "raise"
            | other -> failtestf "expected EntryFailed; got %A" other

        testCase "the verified profile refuses InProcess and names the remedy"
        <| fun () ->
            match CompanionIsolation.forProfile CompositionProfile.Verified IsolationMode.InProcess with
            | Error IsolationProfileRefusal.InProcessUnderVerifiedProfile as refusal ->
                let described =
                    refusal
                    |> Result.mapError IsolationProfileRefusal.describe
                    |> function
                        | Error text -> text
                        | Ok _ -> ""

                Expect.stringContains described "OutOfProcess" "names the remedy"
            | other -> failtestf "expected InProcessUnderVerifiedProfile; got %A" other

        testCase "the standard profile admits InProcess (GP 11)"
        <| fun () ->
            match CompanionIsolation.forProfile CompositionProfile.Standard IsolationMode.InProcess with
            | Ok isolation -> Expect.equal isolation.Mode IsolationMode.InProcess "mode"
            | Error refusal -> failtestf "unexpected refusal: %A" refusal

        testCase "the verified profile admits OutOfProcess on a host whose launcher resolves"
        <| fun () ->
            match
                CompanionIsolation.forProfile
                    CompositionProfile.Verified
                    (IsolationMode.OutOfProcess IsolationLimits.defaults)
            with
            | Ok isolation -> Expect.isTrue (CompanionIsolation.isOutOfProcess isolation) "out of process"
            | Error refusal -> failtestf "unexpected refusal: %A" refusal

        testCase "the boot preflight validator is Ok for a resolvable out-of-process isolation"
        <| fun () ->
            let validator =
                CompanionIsolation.configValidator (
                    CompanionIsolation.ofMode (IsolationMode.OutOfProcess IsolationLimits.defaults)
                )

            Expect.equal validator.Name "CompanionIsolation" "name"
            Expect.equal (validator.Validate() |> Async.RunSynchronously) ConfigValidation.ValidationResult.Ok "verdict"

        testCase "withIsolation registers the singleton through the shared ServiceConfig seam and adds the validator"
        <| fun () ->
            let isolation = CompanionIsolation.ofMode IsolationMode.InProcess

            let app = ServerApp.empty |> CompanionIsolation.withIsolation isolation

            let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()

            match app.Extensions.ServiceConfig with
            | Some configure -> configure services |> ignore
            | None -> failtest "ServiceConfig was not extended"

            let provider =
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider
                    services

            Expect.isTrue
                (obj.ReferenceEquals(provider.GetService(typeof<ICompanionIsolation>), isolation))
                "the composed instance resolves"

            Expect.isTrue
                (app.ConfigValidators |> List.exists (fun v -> v.Name = "CompanionIsolation"))
                "the preflight validator is registered"
    ]

let tests =
    testList "Phase 687 — companion native-boundary isolation" [
        protocolTests
        workerTests
        processTests
        compositionTests
    ]
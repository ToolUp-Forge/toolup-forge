// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the `ICompanionIsolation` contract pack: the laws every
/// implementation of the native-boundary isolation seam must satisfy,
/// whichever side of a process boundary it runs the entry point on.
///
/// Bound by `InProcessIsolation.instance` and by `ProcessIsolation.create`
/// (see `ToolUp.Companions.Isolation.Tests`), which is the whole point:
/// the seam's promise is that a caller cannot tell the two apart on a
/// well-behaved entry point, and can only tell them apart on a
/// misbehaving one by the typed refusal it gets instead of a dead host.
///
/// **This file is link-compiled into the binding test project rather
/// than into `ToolUp.Platform.Tests`**, because the seam lives in
/// `ToolUp.Companions.Isolation`, which the Platform pack does not (and
/// should not) reference. The pack's own entry points are public types
/// with parameterless constructors, as the seam requires, so an
/// out-of-process binding can instantiate them by name inside the host's
/// dependency closure.
module ToolUp.Platform.Tests.Contracts.ICompanionIsolationContract

open System
open System.Text
open Expecto
open ToolUp.Companions.Isolation

/// Answers with the args joined by `|`, a newline, then the input
/// reversed — so both halves of the request are provably intact.
type ContractEchoEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            let prefix = Encoding.UTF8.GetBytes(String.Join("|", request.Args) + "\n")
            Ok(Array.append prefix (Array.rev request.Input))

/// The clean rejection — a parser refusing malformed input.
type ContractRejectingEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            Error $"contract-rejected {request.Input.Length} bytes"

/// A managed failure the seam must translate, never propagate.
type ContractThrowingEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke _ = invalidOp "contract-raised"

/// No parameterless constructor: the seam cannot build it.
type ContractUnconstructibleEntry(_marker: int) =
    interface IIsolatedEntryPoint with
        member _.Invoke _ = Ok [||]

let private allBytes = Array.init 256 byte

let private run<'Entry when 'Entry :> IIsolatedEntryPoint and 'Entry: (new: unit -> 'Entry)>
    (isolation: ICompanionIsolation)
    (args: string list)
    (input: byte[])
    =
    CompanionIsolation.run<'Entry> isolation { Args = args; Input = input }
    |> Async.RunSynchronously

/// Bind the pack: `name` labels the binding, `factory` builds the
/// implementation under test.
let tests (name: string) (factory: unit -> ICompanionIsolation) =
    testList $"{name} — ICompanionIsolation contract" [
        testCase "Mode reports a mode (a preflight can read what it is getting)"
        <| fun () ->
            let isolation = factory ()

            match isolation.Mode with
            | IsolationMode.InProcess
            | IsolationMode.OutOfProcess _
            | IsolationMode.OutOfProcessWith _ -> ()

        testCase "an answering entry point answers with its bytes — args and every byte value intact"
        <| fun () ->
            match run<ContractEchoEntry> (factory ()) [ "musicxml"; ""; "ünïcode" ] allBytes with
            | Ok bytes ->
                let expected =
                    Array.append (Encoding.UTF8.GetBytes "musicxml||ünïcode\n") (Array.rev allBytes)

                Expect.equal bytes expected "the answer is the entry point's bytes, verbatim"
            | Error refusal -> failtestf "expected an answer; got %A" refusal

        testCase "an empty request answers (zero bytes and no args are a request, not a protocol error)"
        <| fun () ->
            match run<ContractEchoEntry> (factory ()) [] [||] with
            | Ok bytes -> Expect.equal bytes (Encoding.UTF8.GetBytes "\n") "the empty echo"
            | Error refusal -> failtestf "expected an answer; got %A" refusal

        testCase "a clean rejection is EntryFailed carrying the entry point's message verbatim"
        <| fun () ->
            Expect.equal
                (run<ContractRejectingEntry> (factory ()) [] allBytes)
                (Error(IsolationRefusal.EntryFailed "contract-rejected 256 bytes"))
                "typed rejection"

        testCase "a raising entry point is EntryFailed naming the exception — never an exception out of Run"
        <| fun () ->
            match run<ContractThrowingEntry> (factory ()) [] [||] with
            | Error(IsolationRefusal.EntryFailed message) ->
                Expect.stringContains message "contract-raised" "the exception's message is carried"
            | other -> failtestf "expected EntryFailed; got %A" other

        testCase "an entry point the seam cannot construct is WorkerUnavailable naming it"
        <| fun () ->
            let outcome =
                (factory ()).Run(typeof<ContractUnconstructibleEntry>, { Args = []; Input = [||] })
                |> Async.RunSynchronously

            match outcome with
            | Error(IsolationRefusal.WorkerUnavailable reason) ->
                Expect.stringContains reason "ContractUnconstructibleEntry" "names the type"
            | other -> failtestf "expected WorkerUnavailable; got %A" other

        testCase "the same request answers the same way twice (no state between calls — GP 12 rule 4)"
        <| fun () ->
            let isolation = factory ()
            let first = run<ContractEchoEntry> isolation [ "a" ] allBytes
            let second = run<ContractEchoEntry> isolation [ "a" ] allBytes
            Expect.equal second first "stateless between calls"
    ]
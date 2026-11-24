// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.RemoteEffectTests

// ─── Remote calls as effect values ───────────────────────────────────
//
// Run on the TRANSPILED client, where the ported modules run:
//
//   * a call made through the performer passes through the interceptor
//     chain, is retried under its retry policy and honours its declared
//     read policy, each beside a control that shows what goes red
//     without it;
//   * the adapter dispatches exactly one message per effect, and a
//     follow-up as the message it names;
//   * an effect value compares by method, argument and retry numbers, so a
//     test can state it;
//   * a ported `update` returns the effect values a test can state, and
//     keeps returning the commands its callers read.

open Fuaran.MVU
open ToolUp.Elmish
open ToolUp.Remoting.Client
open ToolUp.Platform
open ToolUp.Facts
open ToolUp.Facts.Client.FactBrowseModel
open ToolUp.AI.Client.Tests.NodeTest

type private Msg =
    | Got of int
    | Failed of string

let private run (dispatch: Msg -> unit) (cmd: Cmd<Msg>) =
    for effect in cmd do
        effect dispatch

let private same (actual: 'T) (expected: 'T) (message: string) : unit =
    Expect.isTrue (actual = expected) (sprintf "%s\n  actual:   %A\n  expected: %A" message actual expected)

let private reply (answer: Result<int, exn>) : Msg =
    match answer with
    | Ok value -> Got value
    | Error ex -> Failed ex.Message

/// Records one method's interceptor events in order, and optionally
/// replaces its exceptions.
type private Recording(methodName: string, replaceWith: string option) =
    member val Events = ResizeArray<string>()

    interface Cmd.OfRemoting.IRemotingInterceptor with
        member this.OnCalling info =
            if info.MethodName = methodName then
                this.Events.Add "calling"

        member this.OnSuccess(info, _) =
            if info.MethodName = methodName then
                this.Events.Add "success"

        member this.OnError(info, _) =
            if info.MethodName = methodName then
                this.Events.Add "error"
                replaceWith |> Option.map exn
            else
                None

/// Register an interceptor for the duration of one case's assertions.
let private recording (methodName: string) (replaceWith: string option) : Recording =
    let interceptor = Recording(methodName, replaceWith)
    Cmd.OfRemoting.Interceptors.register interceptor
    interceptor

let private events (interceptor: Recording) : string list =
    Cmd.OfRemoting.Interceptors.unregister interceptor
    List.ofSeq interceptor.Events

// ─── A declared read, without a transport ────────────────────────────
//
// The proxy's own half of a declared read (`ReadPolicies.invoke`) over a
// counting `send`, so the read policy runs exactly as a generated proxy
// runs it, with no request leaving the page.

[<Literal>]
let private ProbeApi = "ToolUp.AI.Client.Tests.RemoteEffectTests.ProbeApi"

let private registerProbe () =
    ReadPolicies.register ProbeApi [ "Shared", ReadPolicy.cacheable 0; "Cached", ReadPolicy.cacheable 60 ]

let private declaredRead (methodName: string) (policy: ReadPolicy) (sent: int ref) (answer: int ref) =
    fun (key: string) ->
        ReadPolicies.invoke ProbeApi methodName policy (Some key) (fun () -> async {
            sent.Value <- sent.Value + 1
            let value = answer.Value
            do! Async.Sleep 5
            return box value
        })

let private unboxed (answer: Result<obj, exn>) : Msg =
    answer |> Result.map unbox<int> |> reply

// ─── The ported modules ──────────────────────────────────────────────

let private noCall () : Async<'T> = async { return failwith "not called in this test" }

/// Never invoked: a transition only builds effect values.
let private fakeApi: IFactBrowseApi = {
    ListTables = fun () -> noCall ()
    GetTable = fun _ -> noCall ()
    ListRuns = fun _ -> noCall ()
    GetRunMovers = fun _ -> noCall ()
    QueryRows = fun _ -> noCall ()
    GetFact = fun _ -> noCall ()
    QuerySeries = fun _ -> noCall ()
    QuerySlice = fun _ -> noCall ()
}

/// The expected value of a call: its proxy and reply take no part in
/// equality, so any will do.
let private expectCall (methodName: string) (arg: 'a) : Step<'msg, RemoteEffect<'msg>> =
    Do(
        RemoteEffect.call methodName (fun (_: 'a) -> noCall ()) arg (fun (_: Result<obj, exn>) ->
            Unchecked.defaultof<'msg>)
    )

let private pendingDocument: SharedTypes.KnowledgeDocument = {
    Id = "doc-1"
    FileName = "handbook.pdf"
    FileType = "pdf"
    UploadedAt = System.DateTimeOffset(2026, 8, 1, 9, 30, 0, System.TimeSpan.Zero)
    UploadedBy = "sam"
    Status = SharedTypes.IngestionStatus.Queued
    SizeBytes = 1L
    ChunkCount = 0
    Source = SharedTypes.KnowledgeSource.UploadedFile
    ContentHash = None
    Version = 1
    Tags = []
}

let tests =
    testList "Remote calls as effect values" [

        testList "the performer keeps what Cmd.OfRemoting carries" [
            testCaseDeferred "a performed call passes through the interceptor chain" 30 (fun () ->
                let interceptor = recording "Probe.Plain" None
                let seen = ResizeArray<Msg>()

                RemoteEffect.toCmd [
                    Do(RemoteEffect.call "Probe.Plain" (fun (x: int) -> async { return x * 2 }) 21 reply)
                ]
                |> run seen.Add

                fun () ->
                    same (events interceptor) [ "calling"; "success" ] "OnCalling, then OnSuccess"
                    same (List.ofSeq seen) [ Got 42 ] "one reply")

            testCaseDeferred "a failure reaches the reply as the chain replaced it" 30 (fun () ->
                let interceptor = recording "Probe.Failing" (Some "replaced by the chain")
                let seen = ResizeArray<Msg>()

                RemoteEffect.toCmd [
                    Do(
                        RemoteEffect.call
                            "Probe.Failing"
                            (fun (_: int) -> async { return failwith "transport down" })
                            1
                            reply
                    )
                ]
                |> run seen.Add

                fun () ->
                    same (events interceptor) [ "calling"; "error" ] "OnCalling, then OnError"
                    same (List.ofSeq seen) [ Failed "replaced by the chain" ] "the replaced exception")

            testCaseDeferred "a call is retried under its retry policy; the same call without one is not" 120 (fun () ->
                let interceptor = recording "Probe.Flaky" None

                let policy = {
                    Cmd.RetryPolicy.defaultPolicy with
                        InitialDelayMs = 1
                }

                let flaky (attempts: int ref) =
                    fun (_: int) -> async {
                        attempts.Value <- attempts.Value + 1

                        if attempts.Value < 3 then
                            return failwith "transient"
                        else
                            return attempts.Value
                    }

                let retried = ref 0
                let once = ref 0
                let seenRetried = ResizeArray<Msg>()
                let seenOnce = ResizeArray<Msg>()

                RemoteEffect.toCmd [ Do(RemoteEffect.callWithRetry "Probe.Flaky" policy (flaky retried) 0 reply) ]
                |> run seenRetried.Add

                RemoteEffect.toCmd [ Do(RemoteEffect.call "Probe.Once" (flaky once) 0 reply) ]
                |> run seenOnce.Add

                fun () ->
                    same retried.Value 3 "three attempts under the policy"
                    same (events interceptor) [ "calling"; "calling"; "calling"; "success" ] "OnCalling per attempt"
                    same (List.ofSeq seenRetried) [ Got 3 ] "one reply, after the retries"
                    same once.Value 1 "the control: one attempt without a policy"
                    same (List.ofSeq seenOnce) [ Failed "transient" ] "and its failure")

            testCaseDeferred
                "a declared read is honoured: two identical calls in flight share one request and one chain"
                60
                (fun () ->
                    registerProbe ()
                    ReadPolicies.clear ()
                    let interceptor = recording "Probe.Shared" None
                    let sent = ref 0
                    let read = declaredRead "Shared" (ReadPolicy.cacheable 0) sent (ref 7)
                    let seen = ResizeArray<Msg>()
                    let effect = RemoteEffect.call "Probe.Shared" read "shared-key" unboxed

                    RemoteEffect.toCmd [ Do effect; Do effect ] |> run seen.Add

                    fun () ->
                        same sent.Value 1 "one request"
                        same (events interceptor) [ "calling"; "success" ] "the request's one chain"
                        same (List.ofSeq seen) [ Got 7; Got 7 ] "each effect its one reply")

            testCaseDeferred "a read served from the cache answers once; Cmd.OfRemoting answers twice" 150 (fun () ->
                registerProbe ()
                ReadPolicies.clear ()
                let sent = ref 0
                let answer = ref 1
                let read = declaredRead "Cached" (ReadPolicy.cacheable 60) sent answer
                let viaEffect = ResizeArray<Msg>()
                let viaCmd = ResizeArray<Msg>()

                Async.StartImmediate(
                    async {
                        let! _ = read "cached-key"
                        answer.Value <- 2

                        RemoteEffect.toCmd [ Do(RemoteEffect.call "Probe.Cached" read "cached-key" unboxed) ]
                        |> run viaEffect.Add

                        do! Async.Sleep 30
                        answer.Value <- 3

                        Cmd.OfRemoting.callWithName "Probe.Cached" read "cached-key" (unbox<int> >> Got) (fun ex ->
                            Failed ex.Message)
                        |> run viaCmd.Add
                    }
                )

                fun () ->
                    same (List.ofSeq viaEffect) [ Got 1 ] "the cached value, and nothing after it"
                    same (List.ofSeq viaCmd) [ Got 2; Got 3 ] "the control: cached, then fresh"
                    same sent.Value 3 "the prime and both refreshes were sent")
        ]

        testList "the adapter" [
            testCaseDeferred "one message per effect, and a follow-up as the message it names" 30 (fun () ->
                let seen = ResizeArray<Msg>()

                let cmd =
                    RemoteEffect.toCmd [
                        Then(Got 0)
                        Do(RemoteEffect.call "Probe.One" (fun (x: int) -> async { return x }) 1 reply)
                        Do(RemoteEffect.call "Probe.Two" (fun (x: int) -> async { return x }) 2 reply)
                    ]

                same (List.length cmd) 3 "one command element per step"
                run seen.Add cmd

                fun () -> same (List.ofSeq seen) [ Got 0; Got 1; Got 2 ] "three messages, in order")

            testCase "a body that dispatches twice delivers once" (fun () ->
                let seen = ResizeArray<Msg>()

                RemoteEffect.toCmd [
                    Do(
                        RemoteEffect.legacy "Probe.Twice" (fun dispatch ->
                            dispatch (Got 1)
                            dispatch (Got 2))
                    )
                ]
                |> run seen.Add

                same (List.ofSeq seen) [ Got 1 ] "the second answer is dropped")

            testCase "an empty step list is an empty command" (fun () ->
                same (List.isEmpty (RemoteEffect.toCmd ([]: Step<Msg, RemoteEffect<Msg>> list))) true "nothing to run")

            testCaseDeferred "a mapped effect replies in the parent's message" 30 (fun () ->
                let seen = ResizeArray<string>()

                RemoteEffect.toCmd [
                    Do(
                        RemoteEffect.call "Probe.Mapped" (fun (x: int) -> async { return x }) 5 reply
                        |> RemoteEffect.map (sprintf "%A")
                    )
                ]
                |> List.iter (fun effect -> effect seen.Add)

                fun () -> same (List.ofSeq seen) [ "Got 5" ] "the child's reply, lifted")
        ]

        testList "an effect value can be stated" [
            testCase "equal by method, argument and retry numbers; the proxy and reply take no part" (fun () ->
                let a = RemoteEffect.call "Probe.Eq" (fun (x: int) -> async { return x }) 1 reply

                let b =
                    RemoteEffect.call "Probe.Eq" (fun (x: int) -> async { return x + 1 }) 1 (fun _ -> Got 0)

                same (a = b) true "same method and argument"

                same
                    (a = RemoteEffect.call "Probe.Eq" (fun (x: int) -> async { return x }) 2 reply)
                    false
                    "another argument"

                same
                    (a = RemoteEffect.call "Probe.Other" (fun (x: int) -> async { return x }) 1 reply)
                    false
                    "another method"

                let retried =
                    RemoteEffect.callWithRetry
                        "Probe.Eq"
                        Cmd.RetryPolicy.defaultPolicy
                        (fun (x: int) -> async { return x })
                        1
                        reply

                same (a = retried) false "a retried call is another effect"

                same
                    (RemoteEffect.map (sprintf "%A") a = RemoteEffect.map (sprintf "%A") b)
                    true
                    "map keeps the identity"

                same
                    (RemoteEffect.legacy "Probe.Site" ignore = RemoteEffect.legacy "Probe.Site" (fun d -> d (Got 1)))
                    true
                    "a legacy body compares by its site")
        ]

        testList "a ported update states its effects" [
            testCase "the fact browse: opening a table reads its detail and its first page of runs" (fun () ->
                let _, steps = stepWith fakeApi (OpenTable "prices") empty

                same
                    steps
                    [
                        expectCall "IFactBrowseApi.GetTable" "prices"
                        expectCall "IFactBrowseApi.ListRuns" ("prices", 0, FactBrowseApi.DefaultPageSize)
                    ]
                    "two calls, as values"

                let _, cmd = updateWith fakeApi (OpenTable "prices") empty
                same (List.length cmd) 2 "and updateWith still returns them as two commands")

            testCase "the fact browse: opening a run's movers names the table and the run" (fun () ->
                let selected = {
                    empty with
                        SelectedTable = Some "prices"
                }

                let _, steps = stepWith fakeApi (ToggleMovers "run-1") selected
                same steps [ expectCall "IFactBrowseApi.GetRunMovers" ("prices", "run-1") ] "one call")

            testCase "the knowledge base: opening a document's history reads its versions" (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let _, steps =
                    ClientModel.step (ClientModel.OpenVersionHistory("doc-1", "handbook.pdf")) m0

                same steps [ expectCall "KnowledgeApi.GetDocumentVersions" "doc-1" ] "one call")

            testCase "the knowledge base: a document still ingesting schedules the poll timer" (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let _, steps =
                    ClientModel.step (ClientModel.LoadDocuments(Finished [ pendingDocument ])) m0

                same steps [ Do(RemoteEffect.legacy "KnowledgeBase.schedulePoll" ignore) ] "the timer, by its site")

            testCase "the platform knowledge admin: a delete names its document" (fun () ->
                let m0, _ = PlatformKnowledgeAdminUI.init ()

                let _, steps =
                    PlatformKnowledgeAdminUI.step (PlatformKnowledgeAdminUI.RequestDelete "doc-9") m0

                same steps [ expectCall "IPlatformKnowledgeApi.DeletePlatformDocument" "doc-9" ] "one call")
        ]
    ]
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.RemoteEffectTests

// `RemoteEffect.legacy` is obsolete; this pack still pins what it does.
#nowarn "44"

// ─── Remote calls as effect values ───────────────────────────────────
//
// Run on the TRANSPILED client, where the ported modules run:
//
//   * a call made through the performer passes through the interceptor
//     chain, is retried under its retry policy and honours its declared
//     read policy, each beside a control that shows what goes red
//     without it;
//   * mounted on a `Fuaran.MVU` runtime with forge's interpreter, each
//     effect answers exactly one message and a follow-up runs as the
//     message it names;
//   * an effect value compares by method, argument and retry numbers, so a
//     test can state it;
//   * a ported `update` returns the effect values a test can state, and
//     a wait or a file read answers with the message the module names for
//     its result and for its failure.

open Fuaran.MVU
open ToolUp.Remoting.Client
open ToolUp.Platform
open ToolUp.Facts
open ToolUp.Facts.Client.FactBrowseModel
open ToolUp.AI.Client.Tests.NodeTest

type private Msg =
    | Got of int
    | Failed of string

let private run (dispatch: Msg -> unit) (steps: Step<Msg, RemoteEffect<Msg>> list) = StepDriver.runSteps dispatch steps

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

    interface IRemotingInterceptor with
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
    RemoteEffect.Interceptors.register interceptor
    interceptor

let private events (interceptor: Recording) : string list =
    RemoteEffect.Interceptors.unregister interceptor
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

/// The one wait or read a step list asks for, and the messages its result
/// and its failure answer with, read without performing it.
let private answers (result: obj) (steps: Step<'msg, RemoteEffect<'msg>> list) : RemoteEffect<'msg> * 'msg * 'msg =
    match
        steps
        |> List.choose (function
            | Do(Await call as effect) -> Some(effect, call)
            | _ -> None)
    with
    | [ effect, call ] -> effect, call.Answer(Ok result), call.Answer(Error(exn "it failed"))
    | other -> failwithf "expected one awaited effect, got %d" (List.length other)

/// A file the reader chose, as the browser hands it over.
let private chosenFile (name: string) : Browser.Types.File =
    Fable.Core.JsInterop.createObj [ "name", box name ] |> unbox

let private narrativeView: ToolUp.AI.NarrativePromptView = {
    RunKey = "summary"
    DisplayName = "Summary"
    Title = "Summary"
    DeclaredPrompt = "Write it."
    DeclaredSections = []
    Current = None
    LatestVersion = 0
    Rules = ""
    LastRun = None
}

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

/// What the interceptor chain makes of every failure of the call under test.
[<Literal>]
let private TransportDown = "transport down"

/// Send `msg` through the knowledge base's real `update` with the call
/// `methodName` failing, collect the replies, and hand back the model before
/// the replies and a function that folds them through `update`. The page has
/// no server, so the call fails in transport; the chain replaces the
/// exception so the reason is known.
let private kbFailing (methodName: string) (msg: ClientModel.Msg) (model: ClientModel.Model) =
    let interceptor = recording methodName (Some TransportDown)
    let seen = ResizeArray<ClientModel.Msg>()
    let requested, steps = ClientModel.update msg model

    StepDriver.runSteps seen.Add steps

    requested,
    fun () ->
        same (events interceptor) [ "calling"; "error" ] (sprintf "%s failed in transport" methodName)
        let replies = List.ofSeq seen
        same (List.length replies) 1 "one reply"
        replies |> List.fold (fun m reply -> fst (ClientModel.update reply m)) requested

let tests =
    testList "Remote calls as effect values" [

        testList "the performer keeps the interceptor chain, the retry policy and the read policy" [
            testCaseDeferred "a performed call passes through the interceptor chain" 30 (fun () ->
                let interceptor = recording "Probe.Plain" None
                let seen = ResizeArray<Msg>()

                [
                    Do(RemoteEffect.call "Probe.Plain" (fun (x: int) -> async { return x * 2 }) 21 reply)
                ]
                |> run seen.Add

                fun () ->
                    same (events interceptor) [ "calling"; "success" ] "OnCalling, then OnSuccess"
                    same (List.ofSeq seen) [ Got 42 ] "one reply")

            testCaseDeferred "a failure reaches the reply as the chain replaced it" 30 (fun () ->
                let interceptor = recording "Probe.Failing" (Some "replaced by the chain")
                let seen = ResizeArray<Msg>()

                [
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
                    RemoteRetryPolicy.defaultPolicy with
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

                [ Do(RemoteEffect.callWithRetry "Probe.Flaky" policy (flaky retried) 0 reply) ]
                |> run seenRetried.Add

                [ Do(RemoteEffect.call "Probe.Once" (flaky once) 0 reply) ] |> run seenOnce.Add

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

                    [ Do effect; Do effect ] |> run seen.Add

                    fun () ->
                        same sent.Value 1 "one request"
                        same (events interceptor) [ "calling"; "success" ] "the request's one chain"
                        same (List.ofSeq seen) [ Got 7; Got 7 ] "each effect its one reply")

            testCaseDeferred "a read served from the cache answers once, and its refresh is still sent" 150 (fun () ->
                registerProbe ()
                ReadPolicies.clear ()
                let sent = ref 0
                let answer = ref 1
                let read = declaredRead "Cached" (ReadPolicy.cacheable 60) sent answer
                let viaEffect = ResizeArray<Msg>()

                Async.StartImmediate(
                    async {
                        let! _ = read "cached-key"
                        answer.Value <- 2

                        [ Do(RemoteEffect.call "Probe.Cached" read "cached-key" unboxed) ]
                        |> run viaEffect.Add
                    }
                )

                fun () ->
                    same (List.ofSeq viaEffect) [ Got 1 ] "the cached value, and nothing after it"
                    same sent.Value 2 "the prime and the refresh were sent")
        ]

        testList "on the runtime" [
            testCaseDeferred "a follow-up runs as the message it names, and each effect answers once" 30 (fun () ->
                let seen = ResizeArray<Msg>()

                let update (msg: Msg) (model: unit) : unit * Step<Msg, RemoteEffect<Msg>> list =
                    seen.Add msg

                    match msg with
                    | Failed "go" ->
                        (),
                        [
                            Then(Got 0)
                            Do(RemoteEffect.call "Probe.One" (fun (x: int) -> async { return x }) 1 reply)
                            Do(RemoteEffect.call "Probe.Two" (fun (x: int) -> async { return x }) 2 reply)
                        ]
                    | _ -> (), []

                let comp = Component.plain (fun () -> (), []) update (fun _ -> Map.empty) ignore

                let runtime =
                    Runtime(comp, RemoteEffect.interpreter, Host.create (fun () -> 0L) 0UL ignore, ())

                runtime.Start()
                runtime.Dispatch(Failed "go", User "local") |> ignore

                fun () ->
                    same
                        (List.ofSeq seen)
                        [ Failed "go"; Got 0; Got 1; Got 2 ]
                        "the follow-up, then one reply per call"

                    same runtime.Refusals [] "every kind registered, nothing refused")

            testCase "a body that dispatches twice delivers once" (fun () ->
                let seen = ResizeArray<Msg>()

                [
                    Do(
                        RemoteEffect.legacy "Probe.Twice" (fun dispatch ->
                            dispatch (Got 1)
                            dispatch (Got 2))
                    )
                ]
                |> run seen.Add

                same (List.ofSeq seen) [ Got 1 ] "the second answer is dropped")

            testCaseDeferred "a mapped effect replies in the parent's message" 30 (fun () ->
                let seen = ResizeArray<string>()

                [
                    Do(
                        RemoteEffect.call "Probe.Mapped" (fun (x: int) -> async { return x }) 5 reply
                        |> RemoteEffect.map (sprintf "%A")
                    )
                ]
                |> StepDriver.runSteps seen.Add

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
                        RemoteRetryPolicy.defaultPolicy
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

        testList "the interpreter registers every kind it names" [
            testCase "each effect case and the subscription name a registered kind, each its own" (fun () ->
                let effects: RemoteEffect<Msg> list = [
                    RemoteEffect.call "Probe.Call" (fun (_: int) -> async { return 1 }) 0 reply
                    RemoteEffect.await "Probe.Await" (fun (_: int) -> async { return 1 }) 0 reply
                    RemoteEffect.legacy "Probe.Legacy" ignore
                    RemoteEffect.signal "Probe.Signal" ignore
                ]

                let interpreter = RemoteEffect.interpreter<Msg>

                let effectKinds =
                    effects |> List.map (fun e -> (interpreter.Effect e).Invocation.Kind)

                let subscriptionKind =
                    (interpreter.Resource (ResourceKey [ "probe" ]) (Subscription("Probe.Sub", fun _ -> null)))
                        .Invocation.Kind

                let named = effectKinds @ [ subscriptionKind ]

                for kind in named do
                    same (Interpreter.tryKind kind interpreter |> Option.isSome) true (kind + " is registered")

                same (List.length (List.distinct named)) 5 "five distinct kinds"
                same (List.length interpreter.Kinds) 5 "and no kind registered that nothing names")
        ]

        testList "a ported update states its effects" [
            testCase "the fact browse: opening a table reads its detail and its first page of runs" (fun () ->
                let _, steps = updateWith fakeApi (OpenTable "prices") empty

                same
                    steps
                    [
                        expectCall "IFactBrowseApi.GetTable" "prices"
                        expectCall "IFactBrowseApi.ListRuns" ("prices", 0, FactBrowseApi.DefaultPageSize)
                    ]
                    "two calls, as values")

            testCase "the fact browse: opening a run's movers names the table and the run" (fun () ->
                let selected = {
                    empty with
                        SelectedTable = Some "prices"
                }

                let _, steps = updateWith fakeApi (ToggleMovers "run-1") selected
                same steps [ expectCall "IFactBrowseApi.GetRunMovers" ("prices", "run-1") ] "one call")

            testCase "the knowledge base: opening a document's history reads its versions" (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let _, steps =
                    ClientModel.update (ClientModel.OpenVersionHistory("doc-1", "handbook.pdf")) m0

                same steps [ expectCall "KnowledgeApi.GetDocumentVersions" "doc-1" ] "one call")

            testCase "the knowledge base: a document still ingesting schedules the poll timer" (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let _, steps =
                    ClientModel.update (ClientModel.LoadDocuments(Finished [ pendingDocument ])) m0

                let effect, elapsed, interrupted = answers (box ()) steps
                same effect (RemoteEffect.delay "KnowledgeBase.schedulePoll" 2000 ClientModel.PollStatuses) "the wait"
                same elapsed ClientModel.PollStatuses "the wait ends in a status poll"
                same interrupted ClientModel.PollStatuses "and so does an interrupted wait")

            testCase "the platform knowledge admin: a delete names its document" (fun () ->
                let m0, _ = PlatformKnowledgeAdminUI.init ()

                let _, steps =
                    PlatformKnowledgeAdminUI.update (PlatformKnowledgeAdminUI.RequestDelete "doc-9") m0

                same steps [ expectCall "IPlatformKnowledgeApi.DeletePlatformDocument" "doc-9" ] "one call")
        ]

        testList "a wait and a file read are values" [
            testCase "the data-subject admin: a queued export waits, then polls its ticket" (fun () ->
                let m0, _ = DataSubjectRequestAdminUI.init ClientModuleContext.empty

                let _, steps =
                    DataSubjectRequestAdminUI.update (DataSubjectRequestAdminUI.AsyncExportStarted(Ok "ticket-1")) m0

                let effect, elapsed, interrupted = answers (box ()) steps

                same
                    effect
                    (RemoteEffect.delay "DataSubjectRequestAdminUI.pollDelay" 1500 DataSubjectRequestAdminUI.PollTicket)
                    "the wait"

                same elapsed DataSubjectRequestAdminUI.PollTicket "the wait ends in a ticket poll"
                same interrupted DataSubjectRequestAdminUI.PollTicket "and so does an interrupted wait")

            testCase "the jobs admin: an accepted re-run waits, then polls the job" (fun () ->
                let id = System.Guid.NewGuid()
                let m0, _ = JobsAdminUI.init ()
                let _, steps = JobsAdminUI.update (JobsAdminUI.RerunAccepted(id, Ok())) m0
                let effect, elapsed, interrupted = answers (box ()) steps

                same
                    effect
                    (RemoteEffect.delay
                        ("JobsAdminUI.poll " + string id)
                        JobsAdminUI.pollIntervalMs
                        (JobsAdminUI.PollJob id))
                    "the wait"

                same elapsed (JobsAdminUI.PollJob id) "the wait ends in a job poll"
                same interrupted (JobsAdminUI.PollJob id) "and so does an interrupted wait")

            testCase "the narrative prompt admin: an accepted regeneration waits, then looks at its run" (fun () ->
                let m0, _ = ToolUp.AI.Client.NarrativePromptAdminUI.init ()

                let opened = {
                    m0 with
                        Open = Some(ToolUp.AI.Client.NarrativePromptAdminUI.Loaded narrativeView)
                }

                let _, steps =
                    ToolUp.AI.Client.NarrativePromptAdminUI.update
                        (ToolUp.AI.Client.NarrativePromptAdminUI.RegenerateAccepted(Ok(System.Guid.NewGuid())))
                        opened

                let effect, elapsed, interrupted = answers (box ()) steps

                same
                    effect
                    (RemoteEffect.delay
                        "NarrativePromptAdminUI.pollDelay"
                        ToolUp.AI.Client.NarrativePromptAdminUI.pollIntervalMs
                        (ToolUp.AI.Client.NarrativePromptAdminUI.Poll "summary"))
                    "the wait"

                same
                    elapsed
                    (ToolUp.AI.Client.NarrativePromptAdminUI.Poll "summary")
                    "the wait ends in a look at the run"

                same
                    interrupted
                    (ToolUp.AI.Client.NarrativePromptAdminUI.Poll "summary")
                    "and so does an interrupted wait")

            testCase "the file manager: a chosen file is read, then uploaded; a failed read is reported" (fun () ->
                let file = chosenFile "sales.csv"
                let m0, _ = FileManagerUI.init ClientModuleContext.empty
                let _, steps = FileManagerUI.update (FileManagerUI.SelectFile(Start file)) m0
                let effect, read, failed = answers (box "a,b") steps

                same
                    effect
                    (RemoteEffect.readFile "FileManagerUI.readFile" file (fun _ -> Unchecked.defaultof<_>))
                    "the read, of that file"

                same
                    read
                    (FileManagerUI.SelectFile(
                        Finished {
                            filename = "sales.csv"
                            contents = "a,b"
                            dataType = "UnrecognisedData"
                        }
                    ))
                    "the text, as an upload"

                same failed (FileManagerUI.ApiError(m0.Messages.FileReadError "sales.csv")) "the failure, by file")

            testCase "the mapping manager: a chosen file is read; a failed read is reported" (fun () ->
                let file = chosenFile "rates.csv"
                let m0, _ = MappingDataManagerUI.init ClientModuleContext.empty

                let _, steps =
                    MappingDataManagerUI.update [] (MappingDataManagerUI.SelectFile file) m0

                let effect, read, failed = answers (box "x,y") steps

                same
                    effect
                    (RemoteEffect.readFile "MappingDataManagerUI.readFile" file (fun _ -> Unchecked.defaultof<_>))
                    "the read, of that file"

                same read (MappingDataManagerUI.FileChosen("rates.csv", "x,y")) "the text, by file"

                same
                    failed
                    (MappingDataManagerUI.ApiError(m0.Messages.FileReadFailed "rates.csv"))
                    "the failure, by file")

            testCase "the assistant: a submission reads its ids and clock in one awaited effect" (fun () ->
                let m0, _ = ToolUp.AI.Client.AIAssistantUI.init ()

                let _, steps =
                    ToolUp.AI.Client.AIAssistantUI.update (ToolUp.AI.Client.AIAssistantUI.SubmitMessage "hello") m0

                let prepared: ToolUp.AI.Client.AIAssistantUI.PreparedSubmission = {
                    Content = "hello"
                    ConversationId = System.Guid.NewGuid()
                    MessageId = System.Guid.NewGuid()
                    Timestamp = System.DateTime(2026, 11, 26, 9, 0, 0, System.DateTimeKind.Utc)
                    WatchdogId = System.Guid.NewGuid()
                }

                let effect, ready, failed = answers (box prepared) steps

                same
                    effect
                    (RemoteEffect.await
                        "AIAssistantUI.prepareSubmission"
                        (fun (_: string * System.Guid option) -> noCall ())
                        ("hello", None)
                        (fun _ -> Unchecked.defaultof<_>))
                    "one read, of the message and the conversation"

                // The assistant's messages carry functions, so they are matched.
                match ready with
                | ToolUp.AI.Client.AIAssistantUI.SubmitMessagePrepared answered ->
                    same answered prepared "the prepared submission"
                | other -> failwithf "expected the prepared submission, got %A" other

                match failed with
                | ToolUp.AI.Client.AIAssistantUI.ApiError reason -> same reason "it failed" "the failure, as an error"
                | other -> failwithf "expected an error, got %A" other)
        ]

        testList "the knowledge base surfaces a failed call" [
            testCaseDeferred "a failed index reset clears its spinner and reports the reset failure" 200 (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let requested, settle =
                    kbFailing "KnowledgeApi.ResetIndex" ClientModel.ResetIndexRequested m0

                same requested.Resetting true "the spinner runs while the call is in flight"

                fun () ->
                    let settled = settle ()
                    same settled.Resetting false "the spinner clears"
                    same settled.UploadError (Some(m0.Messages.Errors.ResetFailed TransportDown)) "the reason is shown")

            testCaseDeferred "a failed context refresh clears its spinner and reports the reason" 200 (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let requested, settle =
                    kbFailing "KnowledgeApi.RefreshAIContext" ClientModel.RefreshAIContextRequested m0

                same requested.RefreshingAIContext true "the spinner runs while the call is in flight"

                fun () ->
                    let settled = settle ()
                    same settled.RefreshingAIContext false "the spinner clears"
                    same settled.UploadError (Some TransportDown) "the reason is shown")

            testCaseDeferred "a failed new note clears its spinner and reports the reason" 200 (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let requested, settle =
                    kbFailing "KnowledgeApi.AddNote" (ClientModel.SaveNote(ClientModel.CreateNew, "t", "b")) m0

                same requested.SavingNote true "the spinner runs while the call is in flight"

                fun () ->
                    let settled = settle ()
                    same settled.SavingNote false "the spinner clears"
                    same settled.NoteSaveError (Some TransportDown) "the reason is shown")

            testCaseDeferred "a failed note edit clears its spinner and reports the reason" 200 (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let requested, settle =
                    kbFailing
                        "KnowledgeApi.UpdateNote"
                        (ClientModel.SaveNote(ClientModel.EditExisting "doc-1", "t", "b"))
                        m0

                same requested.SavingNote true "the spinner runs while the call is in flight"

                fun () ->
                    let settled = settle ()
                    same settled.SavingNote false "the spinner clears"
                    same settled.NoteSaveError (Some TransportDown) "the reason is shown")

            testCaseDeferred "a failed context save clears its spinner and reports the reason" 200 (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let requested, settle =
                    kbFailing "KnowledgeApi.SetAIContext" (ClientModel.SaveAIContext "body") m0

                same requested.SavingAIContext true "the spinner runs while the call is in flight"

                fun () ->
                    let settled = settle ()
                    same settled.SavingAIContext false "the spinner clears"
                    same settled.AIContextSaveError (Some TransportDown) "the reason is shown")

            testCaseDeferred "a failed document list read reports the reason" 200 (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let _, settle =
                    kbFailing "KnowledgeApi.GetDocuments" (ClientModel.LoadDocuments(Start())) m0

                fun () -> same (settle ()).LoadError (Some TransportDown) "the reason is shown")

            testCaseDeferred "a failed context read reports the reason" 200 (fun () ->
                let m0, _ = ClientModel.init ClientModuleContext.empty

                let _, settle =
                    kbFailing "KnowledgeApi.GetAIContext" (ClientModel.LoadAIContext(Start())) m0

                fun () -> same (settle ()).AIContextLoadError (Some TransportDown) "the reason is shown")

            testCaseDeferred
                "a failed status poll is silent: the document keeps its status for the next cycle"
                200
                (fun () ->
                    let m0, _ = ClientModel.init ClientModuleContext.empty

                    let loaded =
                        fst (ClientModel.update (ClientModel.LoadDocuments(Finished [ pendingDocument ])) m0)

                    let requested, settle =
                        kbFailing "KnowledgeApi.GetStatus" ClientModel.PollStatuses loaded

                    fun () ->
                        let settled = settle ()
                        same settled.Documents requested.Documents "the document keeps its status"
                        same settled.LoadError None "and nothing is reported")
        ]
    ]
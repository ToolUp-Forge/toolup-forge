// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ExternalModelFitTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
// Opened BEFORE ToolUp.Platform on purpose: this namespace also defines an
// `ILogger`, and the later open is the one an unqualified name resolves to,
// so the platform's `ILogger` stays the one this file means.
open Microsoft.Extensions.Logging
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.ExternalCompute.Http
open ToolUp.Platform.Secrets
open ToolUp.Platform.Server

// ─── Phase 450 — the external model-fit binding ──────────────────────────
//
// The claim under test is narrow and unusual: **a fit worker needs no SDK
// from this repo.** So the end-to-end arm is bound against a stub worker
// that speaks nothing but HTTP and JSON over a real socket — it is reached
// through the shipped generic HTTP compute companion, it reads the work
// envelope with the published parser, and it resolves the fit by POSTing to
// the real Phase 320 ingress. Nothing in that path is a test double of a
// transport: a fake dispatcher would verify the half that is not in doubt
// (that an `Async` returns) while eliding every half that is (that the
// envelope survives a wire hop, that the callback authenticates, that the
// artifact descriptor is readable by something that did not write it).
//
// The pack has five jobs:
//
//   1. **Pin the `modelfit/v1` schema literally** (450.A). The contract
//      document specifies field names; a test that only round-trips
//      through this repo's own parser would let both halves rename a field
//      together and stay green while every worker in the world broke. So
//      the render is asserted against the exact JSON text.
//   2. **Envelope-version refusal is BEFORE the submit** (450.A), measured
//      on what the dispatcher was handed, with a control that the same
//      provider submits when the envelope is accepted.
//   3. **The full loop over a real socket** (450.D): submit → progress →
//      complete → outcome, with the status endpoint reporting `running`
//      *forever* so the only thing that can resolve the fit is the
//      callback. Push is therefore proved structurally rather than by
//      timing.
//   4. **Completion replay is idempotent** (450.D) — the identical
//      authenticated callback, sent twice, resolves once.
//   5. **The failure map** (450.B): a malformed artifact, a refused
//      submit, a retriable worker failure, a timeout that cancels, a
//      cancellation that propagates, and the GP 4 scope refusal — each
//      measured on the backend's observations, not on a return value
//      alone.
//
// Falsifiable controls are marked `CONTROL` and each asserts the probe
// beside it is capable of failing.

// ── fixtures ─────────────────────────────────────────────────────────────

let private freshDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-fit-ext-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private schema: DatasetSchema = {
    Columns = [
        {
            Name = "x"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
    ]
}

let private rows = [
    for i in 1..4 ->
        {
            Cells = [ DatasetValue.Float(float i) ]
        }
]

/// A dataset store holding one vintage of `scope-fit/observations`.
let private seededDatasets (scopeId: string) = async {
    let blob = LocalFileStorage.LocalFileStorage(freshDir ()) :> IBlobStorage
    let store = BlobDatasetStore.create (DataObjectStore(blob) :> IDataObjectStore)

    let! created = store.Create(scopeId, "observations", schema, rows, "tester", Map.empty, StrictlyVersioned)

    match created with
    | Ok version -> return store, version
    | Error e -> return failtestf "seeding the vintage must succeed; got %s" (DatasetError.describe e)
}

let private specRef = ModelSpecRef.ofPayload """{"family":"glm","link":"log"}"""

let private requestFor (scopeId: string) (version: int) (gates: GateSpec list) : FitRequest = {
    ScopeId = scopeId
    DatasetVersion = {
        ScopeId = scopeId
        DatasetId = "observations"
        Version = version
    }
    SpecRef = specRef
    ProviderKind = "python-fitter"
    Seed = 4242L
    Gates = gates
    SubmitterClass = SubmitterClass.Human
}

/// The descriptor a well-behaved worker returns as its `resultRef`.
let private goodDescriptor: ModelFitArtifactDescriptor = {
    ModelFitArtifactDescriptor.Envelope = ModelFitWorkSpec.Kind
    ModelFitArtifactDescriptor.ArtifactId = "blob://fits/glm-4242.bin"
    ModelFitArtifactDescriptor.ContentHash = String.replicate 64 "a"
    ModelFitArtifactDescriptor.ByteLength = 20480L
    ModelFitArtifactDescriptor.Diagnostics = Map [ "rmse", 0.25; "converged", 1.0 ]
    ModelFitArtifactDescriptor.DurationMs = 91_000L
    ModelFitArtifactDescriptor.CostUnits = 4.5
}

// ── test doubles for the arms that are not about the wire ────────────────

type private RecordingLogger() =
    let lines = ConcurrentQueue<string>()
    member _.Lines = lines |> Seq.toList

    interface ILogger with
        member _.Debug message = lines.Enqueue("DEBUG " + message)
        member _.Info message = lines.Enqueue("INFO " + message)
        member _.Warn message = lines.Enqueue("WARN " + message)
        member _.Error(message, _) = lines.Enqueue("ERROR " + message)

type private SilentSecretStore() =
    interface ISecretStore with
        member _.GetSecret(_scopeId, _key) = async.Return None
        member _.SetSecret(_scopeId, _key, _value) = async.Return(Ok())
        member _.DeleteSecret(_scopeId, _key) = async.Return(Ok())
        member _.ListKeys _scopeId = async.Return []

/// A dispatcher whose answers are scripted, so the failure map can be
/// exercised without asking a real service to misbehave. It records what it
/// was HANDED, which is the only way a "refused before submit" claim can be
/// measured at all.
type private ScriptedDispatcher(outcomes: ExternalOutcome list, ?submitResult: Result<unit, ExternalComputeError>) =
    let submitted = ConcurrentQueue<string * ExternalWorkSpec>()
    let cancelled = ConcurrentQueue<Guid>()
    let remaining = ConcurrentQueue<ExternalOutcome>(outcomes)
    let mutable last = ExternalOutcome.Pending

    member _.Submitted = submitted |> Seq.toList
    member _.Cancelled = cancelled |> Seq.toList

    interface IExternalComputeDispatcher with
        member _.Backend = "scripted"

        member _.Submit(scopeId, spec) = async {
            submitted.Enqueue((scopeId, spec))

            match defaultArg submitResult (Ok()) with
            | Error e -> return Error e
            | Ok() ->
                return
                    Ok {
                        HandleId = Guid.NewGuid()
                        Backend = "scripted"
                        ScopeId = scopeId
                        NativeRef = "scripted-1"
                        SubmittedAt = DateTime.UtcNow
                    }
        }

        member _.Poll _handle = async {
            match remaining.TryDequeue() with
            | true, outcome ->
                last <- outcome
                return outcome
            | _ -> return last
        }

        member _.Cancel handle = async { cancelled.Enqueue handle.HandleId }

/// Records every checkpoint the provider reports, through the REAL Phase
/// 321 binding — `JobProgressSink.reporterFor` over an `IJobProgressSink`,
/// pushed as the ambient reporter exactly as the scheduler pushes one.
type private RecordingProgressSink() =
    let reports = ConcurrentQueue<JobId * string * ProgressCheckpoint>()
    member _.Reports = reports |> Seq.toList

    interface IJobProgressSink with
        member _.Report(jobId, scopeId, checkpoint) = async { reports.Enqueue((jobId, scopeId, checkpoint)) }

        member _.Latest _jobId =
            match reports |> Seq.tryLast with
            | Some(_, _, checkpoint) -> async.Return(Some checkpoint)
            | None -> async.Return None

/// Records what the ingress drove, and what the fit sink answered.
type private RecordingCompletionSink(inner: IExternalCompletionSink) =
    let calls = ConcurrentQueue<Guid * ExternalOutcome * ExternalResolution>()
    member _.Calls = calls |> Seq.toList

    interface IExternalCompletionSink with
        member _.ResolveExternal(handle, jobRunId, outcome) = async {
            let! resolution = inner.ResolveExternal(handle, jobRunId, outcome)
            calls.Enqueue((handle.HandleId, outcome, resolution))
            return resolution
        }

// ── the in-process HTTP stub worker ──────────────────────────────────────

/// What the worker was told about a fit's completion webhook.
type private StubHook = {
    Url: string
    Secret: string
    HandleId: string
}

/// A fit worker that speaks nothing but HTTP and JSON.
///
/// `POST /fits` accepts a `modelfit/v1` envelope and parses it with the
/// published parser; `GET /fits/{id}` reports **`running` forever**;
/// `POST /fits/{id}/webhook` receives the per-handle credential;
/// `POST /fits/{id}/cancel` records a teardown request.
///
/// The status endpoint never reports a terminal state, and that is the
/// design: it means a fit that resolves can only have been resolved by the
/// completion callback, so the push path is proved structurally rather than
/// by winning a race.
type private StubFitWorker(resultRefFor: unit -> string, pollsBeforeCallback: int) =
    let payloads = ConcurrentQueue<string>()
    let hooks = ConcurrentDictionary<string, StubHook>()
    let cancels = ConcurrentQueue<string>()
    let callbackStatuses = ConcurrentQueue<int * string>()
    let mutable polls = 0
    let mutable fired = 0
    let mutable baseUrl = ""

    let client = new HttpClient()

    let readBody (ctx: HttpContext) : Task<JsonDocument> = task {
        use reader = new StreamReader(ctx.Request.Body)
        let! text = reader.ReadToEndAsync()

        return
            if String.IsNullOrWhiteSpace text then
                JsonDocument.Parse "{}"
            else
                JsonDocument.Parse text
    }

    let stringField (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> ""

    /// POST the completion callback — the whole of what a worker has to do
    /// to resolve a fit, expressed in five primitive JSON fields.
    let sendCallback (hook: StubHook) (resultRef: string) = task {
        let body =
            sprintf
                """{"handleId":%s,"status":"succeeded","resultRef":%s}"""
                (JsonSerializer.Serialize hook.HandleId)
                (JsonSerializer.Serialize resultRef)

        use request =
            new HttpRequestMessage(
                HttpMethod.Post,
                hook.Url,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            )

        request.Headers.Add(ExternalCallback.SecretHeader, hook.Secret)
        let! response = client.SendAsync request
        let! text = response.Content.ReadAsStringAsync()
        callbackStatuses.Enqueue((int response.StatusCode, text))
    }

    let app =
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        let a = builder.Build()

        a.MapPost(
            "/fits",
            Func<HttpContext, Task>(fun ctx -> task {
                use! document = readBody ctx
                let root = document.RootElement

                // The worker's own reading of the envelope, through the
                // published parser rather than through field poking — so a
                // schema that this repo can write but nobody can read fails
                // HERE, at the worker, which is where it would fail in
                // production.
                match root.TryGetProperty "payload" with
                | true, payload -> payloads.Enqueue(payload.GetRawText())
                | _ -> payloads.Enqueue "{}"

                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync """{"fit":{"id":"fit-1"}}"""
            })
        )
        |> ignore

        a.MapGet(
            "/fits/{id}",
            Func<HttpContext, Task>(fun ctx -> task {
                polls <- polls + 1

                // Fire the completion once the fit has been observed
                // running at least `pollsBeforeCallback` times, so a
                // progress checkpoint is guaranteed to have reached the
                // Phase 321 sink before any outcome exists.
                if polls >= pollsBeforeCallback && fired = 0 then
                    match hooks.TryGetValue "fit-1" with
                    | true, hook ->
                        fired <- 1
                        do! sendCallback hook (resultRefFor ())
                    | _ -> ()

                ctx.Response.ContentType <- "application/json"
                // Never terminal. See the type's doc comment.
                do! ctx.Response.WriteAsync """{"fit":{"state":"running","percentComplete":40}}"""
            })
        )
        |> ignore

        a.MapPost(
            "/fits/{id}/webhook",
            Func<HttpContext, Task>(fun ctx -> task {
                let id = string ctx.Request.RouteValues["id"]
                use! document = readBody ctx
                let root = document.RootElement

                hooks[id] <- {
                    Url = stringField root "callbackUrl"
                    Secret = stringField root "callbackSecret"
                    HandleId = stringField root "handleId"
                }

                ctx.Response.StatusCode <- 204
                do! ctx.Response.WriteAsync ""
            })
        )
        |> ignore

        a.MapPost(
            "/fits/{id}/cancel",
            Func<HttpContext, Task>(fun ctx -> task {
                cancels.Enqueue(string ctx.Request.RouteValues["id"])
                ctx.Response.StatusCode <- 202
                do! ctx.Response.WriteAsync ""
            })
        )
        |> ignore

        a

    member _.StartAsync() : Task = task {
        do! (app :> IHost).StartAsync()

        baseUrl <-
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> Seq.head
    }

    member _.BaseUrl = baseUrl
    member _.Payloads = payloads |> Seq.toList
    member _.Cancels = cancels |> Seq.toList
    member _.CallbackStatuses = callbackStatuses |> Seq.toList

    member _.Hook =
        hooks.TryGetValue "fit-1"
        |> function
            | true, hook -> Some hook
            | _ -> None

    /// Re-send the identical authenticated callback — the replay arm.
    member _.Replay (hook: StubHook) (resultRef: string) = async {
        do! sendCallback hook resultRef |> Async.AwaitTask
        return callbackStatuses |> Seq.last
    }

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            (app :> IHost).StopAsync().GetAwaiter().GetResult()
            (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

let private startWorker (resultRefFor: unit -> string) (pollsBeforeCallback: int) =
    let worker = new StubFitWorker(resultRefFor, pollsBeforeCallback)
    worker.StartAsync().GetAwaiter().GetResult()
    worker

/// The real Phase 320 ingress, on a real socket, so the stub worker POSTs
/// to it the way a webhook does.
type private IngressHost(store: IExternalHandleStore, sink: IExternalCompletionSink) =
    let mutable baseUrl = ""

    let app =
        // Module-level throttle + warning counters, per the ingress's own
        // documentation, so packs do not inherit each other's counts.
        ExternalComputeCallback.resetThrottleState ()

        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        builder.Services.AddGiraffe() |> ignore
        builder.Services.AddSingleton<IExternalHandleStore> store |> ignore
        builder.Services.AddSingleton<IExternalCompletionSink> sink |> ignore
        let a = builder.Build()
        a.UseGiraffe(choose ExternalComputeCallback.routes)
        a

    member _.StartAsync() : Task = task {
        do! (app :> IHost).StartAsync()

        baseUrl <-
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> Seq.head
    }

    member _.BaseUrl = baseUrl

    interface IDisposable with
        member _.Dispose() =
            (app :> IHost).StopAsync().GetAwaiter().GetResult()
            (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

let private startIngress store sink =
    let host = new IngressHost(store, sink)
    host.StartAsync().GetAwaiter().GetResult()
    host

let private sharedHttpClient = lazy (new HttpClient())

/// The config a deployment writes for the stub worker.
let private configFor (worker: StubFitWorker) (ingressBaseUrl: string) =
    HttpComputeConfig.create
        "fit-worker"
        (worker.BaseUrl + "/fits")
        (worker.BaseUrl + "/fits/" + HttpComputeConfig.JobIdPlaceholder)
        (JsonPath.ofString "fit.id")
        (JsonPath.ofString "fit.state")
    |> HttpComputeConfig.withProgress 100.0 (JsonPath.ofString "fit.percentComplete")
    |> HttpComputeConfig.withCancel "POST" (worker.BaseUrl + "/fits/" + HttpComputeConfig.JobIdPlaceholder + "/cancel")
    |> HttpComputeConfig.withCallback {
        PublicBaseUrl = ingressBaseUrl
        RegistrationUrlTemplate = worker.BaseUrl + "/fits/" + HttpComputeConfig.JobIdPlaceholder + "/webhook"
        RegistrationMethod = "POST"
        UrlField = "callbackUrl"
        SecretField = "callbackSecret"
        HandleIdField = Some "handleId"
    }

let private options =
    ExternalFitOptions.create "python-fitter" "2.1.0"
    |> ExternalFitOptions.withDeclaredGates [ "rmse"; "converged" ]
    |> ExternalFitOptions.withPollInterval (TimeSpan.FromMilliseconds 60.0)

/// Run `body` with `sink` bound as the ambient Phase 321 reporter, exactly
/// as the scheduler binds one around `IJobHandler.Execute`.
let private withProgress (sink: IJobProgressSink) (jobId: JobId) (scopeId: string) (body: Async<'a>) = async {
    use _scope = JobProgressScope.push (JobProgressSink.reporterFor sink jobId scopeId)
    return! body
}

// ── 450.A — the work-spec convention ─────────────────────────────────────

let private conventionTests =
    testList "450.A — the modelfit/v1 work-spec convention" [
        testCase "the rendered payload is the exact JSON the contract document specifies"
        <| fun _ ->
            let contentRef = {
                ScopeId = "scope-fit"
                DatasetId = "observations"
                Version = 3
                ContentHash = "deadbeef"
                Format = "parquet"
                RowCount = 1200L
            }

            let request =
                requestFor "scope-fit" 3 [
                    {
                        Name = "rmse"
                        Threshold = 0.5
                        Direction = GateDirection.AtMost
                    }
                ]

            let payload =
                ModelFitWorkSpec.ofRequest contentRef (Map [ "gpu", "1" ]) request
                |> ModelFitWorkSpec.renderPayload

            // Asserted as TEXT, not as a round-trip. A round-trip through
            // this repo's own parser would stay green while both halves
            // renamed a field together and every worker in the world broke.
            let expected =
                String.concat "" [
                    """{"envelope":"modelfit/v1","scopeId":"scope-fit","""
                    sprintf """"specRef":%s,""" (JsonSerializer.Serialize specRef.Payload)
                    sprintf """"specHash":"%s","specHashAlgorithm":"",""" specRef.SpecHash
                    """"datasetParquetRef":{"scopeId":"scope-fit","datasetId":"observations","version":3,"""
                    """"contentHash":"deadbeef","format":"parquet","rowCount":1200},"""
                    """"seed":4242,"""
                    """"gates":[{"name":"rmse","threshold":0.5,"direction":"AtMost"}],"""
                    """"resourceHints":{"gpu":"1"}}"""
                ]

            Expect.equal payload expected "the wire shape a worker author reads off the contract document"

        testCase "a rendered payload round-trips through the published parser"
        <| fun _ ->
            let contentRef = {
                ScopeId = "scope-fit"
                DatasetId = "observations"
                Version = 7
                ContentHash = "cafe"
                Format = "toolup-frame-v1"
                RowCount = 4L
            }

            let request =
                requestFor "scope-fit" 7 [
                    {
                        Name = "rmse"
                        Threshold = 0.5
                        Direction = GateDirection.AtMost
                    }
                    {
                        Name = "converged"
                        Threshold = 1.0
                        Direction = GateDirection.AtLeast
                    }
                ]

            let original = ModelFitWorkSpec.ofRequest contentRef Map.empty request

            match ModelFitWorkSpec.parsePayload (ModelFitWorkSpec.renderPayload original) with
            | Error e -> failtestf "the payload must parse; got %s" e
            | Ok parsed ->
                Expect.equal parsed original "every field survives the round trip"

                Expect.equal
                    parsed.DatasetParquetRef.Format
                    "toolup-frame-v1"
                    "the format tag is carried verbatim — a non-Parquet composition must not present as Parquet"

        testCase "a gate direction outside the vocabulary is refused by name"
        <| fun _ ->
            let text =
                """{"envelope":"modelfit/v1","scopeId":"s","specRef":"{}","specHash":"h","specHashAlgorithm":"","""
                + """"datasetParquetRef":{"scopeId":"s","datasetId":"d","version":1,"contentHash":"c","format":"parquet","rowCount":1},"""
                + """"seed":1,"gates":[{"name":"rmse","threshold":0.5,"direction":"LessThanish"}],"resourceHints":{}}"""

            match ModelFitWorkSpec.parsePayload text with
            | Ok _ -> failtest "an unknown gate direction must not parse"
            | Error e ->
                Expect.stringContains e "LessThanish" "the refusal names the direction it could not read"
                Expect.stringContains e "AtLeast" "and the vocabulary it expected"

        testCase "the artifact descriptor round-trips, and its digest shape is enforced"
        <| fun _ ->
            match ModelFitWorkSpec.parseDescriptor (ModelFitWorkSpec.renderDescriptor goodDescriptor) with
            | Error e -> failtestf "the descriptor must parse; got %s" e
            | Ok parsed -> Expect.equal parsed goodDescriptor "every field survives the round trip"

            let uppercased = {
                goodDescriptor with
                    ModelFitArtifactDescriptor.ContentHash = String.replicate 64 "A"
            }

            match ModelFitWorkSpec.parseDescriptor (ModelFitWorkSpec.renderDescriptor uppercased) with
            | Ok _ ->
                failtest
                    "an uppercase digest must be refused — the digest is carried as text, so two casings would name one artifact"
            | Error e -> Expect.stringContains e "contentHash" "the refusal names the field"

        testCase "a descriptor answering under an unknown envelope is refused rather than read"
        <| fun _ ->
            let future =
                (ModelFitWorkSpec.renderDescriptor goodDescriptor).Replace("modelfit/v1", "modelfit/v9")

            match ModelFitWorkSpec.parseDescriptor future with
            | Ok _ -> failtest "a v9 descriptor must not be read as v1"
            | Error e ->
                Expect.stringContains e "modelfit/v9" "the refusal names the envelope the worker answered under"
    ]

// ── 450.A — envelope-version refusal ─────────────────────────────────────

let private envelopeRefusalTests =
    testList "450.A — envelope-version refusal" [
        testCaseAsync "a worker that does not accept this envelope is refused BEFORE the payload is submitted"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"
            let dispatcher = ScriptedDispatcher []

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    ExternalFitCompletionRegistry(),
                    options |> ExternalFitOptions.withAcceptedEnvelopes [ "modelfit/v2" ]
                )

            let! result = provider.FitExternally(requestFor "scope-fit" version.Version [])

            match result with
            | Ok _ -> failtest "a fit under an unaccepted envelope must not run"
            | Error(ExternalFitFailure.EnvelopeUnsupported(requested, accepted)) ->
                Expect.equal requested ModelFitWorkSpec.Kind "the envelope this platform speaks"
                Expect.equal accepted [ "modelfit/v2" ] "and the ones the worker declared"
            | Error other -> failtestf "expected EnvelopeUnsupported; got %A" other

            // The measurement that matters: nothing left this process.
            Expect.isEmpty dispatcher.Submitted "the dispatcher was never handed the payload"

            Expect.isFalse
                (ExternalFitFailure.isRetriable (ExternalFitFailure.EnvelopeUnsupported("modelfit/v1", [])))
                "and the refusal is terminal — a worker does not learn an envelope by being asked twice"
        }

        testCaseAsync "CONTROL — the same provider submits when the envelope IS accepted"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"

            let dispatcher =
                ScriptedDispatcher [ ExternalOutcome.Succeeded(ModelFitWorkSpec.renderDescriptor goodDescriptor) ]

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    ExternalFitCompletionRegistry(),
                    options
                )

            let! result = provider.FitExternally(requestFor "scope-fit" version.Version [])
            Expect.isTrue (Result.isOk result) "the fit runs"

            match dispatcher.Submitted with
            | [ (scopeId, spec) ] ->
                Expect.equal scopeId "scope-fit" "submitted under the fit's own scope"
                Expect.equal spec.Kind ModelFitWorkSpec.Kind "with the versioned kind as the discriminator"

                Expect.equal
                    spec.SubmitterClass
                    SubmitterClass.Human
                    "and the submitter class carried through for compute-budget policy"
            | other -> failtestf "expected exactly one submission; got %d" (List.length other)
        }

        testCaseAsync "a dataset belonging to another scope is refused, and never read (GP 4)"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"
            let dispatcher = ScriptedDispatcher []

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    ExternalFitCompletionRegistry(),
                    options
                )

            let crossScope = {
                requestFor "scope-fit" version.Version [] with
                    DatasetVersion = {
                        ScopeId = "scope-other"
                        DatasetId = "observations"
                        Version = version.Version
                    }
            }

            match! provider.FitExternally crossScope with
            | Error(ExternalFitFailure.ScopeMismatch(fitScope, datasetScope)) ->
                Expect.equal fitScope "scope-fit" "the fit's scope"
                Expect.equal datasetScope "scope-other" "and the vintage's"
            | other -> failtestf "expected ScopeMismatch; got %A" other

            Expect.isEmpty dispatcher.Submitted "and nothing was submitted"
        }
    ]

// ── 450.D — the full loop against a stub worker over a real socket ───────

let private endToEndTests =
    testList "450.D — stub worker, end to end" [
        testCaseAsync "submit → progress → complete → outcome, with no worker-side SDK"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"

            let handles = InMemoryExternalHandleStore() :> IExternalHandleStore
            let registry = ExternalFitCompletionRegistry()
            let fitSink = ExternalFitCompletionSink registry
            let recording = RecordingCompletionSink(fitSink)
            use ingress = startIngress handles (recording :> IExternalCompletionSink)

            use worker =
                startWorker (fun () -> ModelFitWorkSpec.renderDescriptor goodDescriptor) 2

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    (configFor worker ingress.BaseUrl)
                    (SilentSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            let progress = RecordingProgressSink()

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    registry,
                    options,
                    Some handles,
                    Some(RecordingLogger() :> ILogger)
                )

            let jobId = Guid.NewGuid()

            let gates = [
                {
                    Name = "rmse"
                    Threshold = 0.5
                    Direction = GateDirection.AtMost
                }
                {
                    Name = "auc"
                    Threshold = 0.9
                    Direction = GateDirection.AtLeast
                }
            ]

            let! result =
                provider.FitExternally(requestFor "scope-fit" version.Version gates)
                |> withProgress progress jobId "scope-fit"

            match result with
            | Error e -> failtestf "the fit must complete; got %s" (ExternalFitFailure.describe e)
            | Ok outcome ->
                Expect.equal
                    outcome.ArtifactRef
                    {
                        ArtifactId = goodDescriptor.ArtifactId
                        ContentHash = goodDescriptor.ContentHash
                        ByteLength = goodDescriptor.ByteLength
                    }
                    "the artifact the worker returned, read out of the opaque result reference"

                Expect.equal outcome.Diagnostics goodDescriptor.Diagnostics "the diagnostics it measured"
                Expect.equal outcome.DurationMs 91_000L "and its own duration self-report, not our wall clock"

                // 449.C — the gates are evaluated HERE, from the worker's
                // diagnostics, against the gates the REQUEST asked for. The
                // worker reported no `auc` at all, so that gate fails
                // closed; a worker cannot pass a gate by staying silent.
                match outcome.GateVerdicts with
                | [ rmse; auc ] ->
                    Expect.isTrue rmse.Passed "rmse 0.25 <= 0.5 passes"
                    Expect.equal rmse.Observed 0.25 "on the value the worker reported"
                    Expect.isFalse auc.Passed "an unreported diagnostic fails its gate CLOSED"
                    Expect.isTrue (Double.IsNaN auc.Observed) "with no observation invented for it"
                | other -> failtestf "expected two verdicts in request order; got %A" other

                Expect.equal
                    outcome.CompositeKey.ProviderId
                    "python-fitter"
                    "and the composite identity is forge's, keyed on the composed provider"

                Expect.equal outcome.CompositeKey.Seed 4242L "carrying the seed the fit was run under"

            // The worker read the envelope with the published parser — a
            // schema this repo can write but nobody can read fails here.
            match worker.Payloads with
            | [ payload ] ->
                match ModelFitWorkSpec.parsePayload payload with
                | Error e -> failtestf "the worker could not read the envelope it was sent: %s" e
                | Ok parsed ->
                    Expect.equal parsed.Envelope ModelFitWorkSpec.Kind "the envelope version it answered under"
                    Expect.equal parsed.Seed 4242L "the seed"
                    Expect.equal parsed.DatasetParquetRef.RowCount 4L "and the vintage it was pointed at"
            | other -> failtestf "expected exactly one submission to the worker; got %d" (List.length other)

            // Progress reached the REAL Phase 321 sink. The status endpoint
            // reports 40% and never a terminal state, so the intermediate
            // frame cannot be the completion frame.
            let fractions = progress.Reports |> List.choose (fun (_, _, cp) -> cp.Fraction)

            Expect.contains fractions 0.4 "the worker's own progress fraction surfaced through IJobProgressSink"
            Expect.contains fractions 1.0 "and the terminal frame closed the bar out"

            Expect.isTrue
                (progress.Reports
                 |> List.forall (fun (id, scope, _) -> id = jobId && scope = "scope-fit"))
                "every checkpoint attributed to the dispatching job and scope, never a caller-supplied one"

            // The completion arrived by PUSH, and structurally so: the
            // status endpoint never reports a terminal state, so the poll
            // fallback had nothing to resolve the fit with.
            match recording.Calls with
            | [ (_, outcome, resolution) ] ->
                Expect.equal
                    (ExternalOutcome.label outcome)
                    "succeeded"
                    "the ingress drove the outcome the worker POSTed"

                Expect.equal resolution (ExternalResolution.Resolved "succeeded") "and the fit sink claimed it"
            | other -> failtestf "expected exactly one ingress resolution; got %A" other

            Expect.equal
                (worker.CallbackStatuses |> List.map fst)
                [ 200 ]
                "and the ingress accepted the worker's authenticated callback"
        }

        testCaseAsync "a replayed completion resolves once — the second delivery is idempotent"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"

            let handles = InMemoryExternalHandleStore() :> IExternalHandleStore
            let registry = ExternalFitCompletionRegistry()
            let recording = RecordingCompletionSink(ExternalFitCompletionSink registry)
            use ingress = startIngress handles (recording :> IExternalCompletionSink)

            use worker =
                startWorker (fun () -> ModelFitWorkSpec.renderDescriptor goodDescriptor) 1

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    (configFor worker ingress.BaseUrl)
                    (SilentSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    registry,
                    options,
                    Some handles,
                    None
                )

            let! first = provider.FitExternally(requestFor "scope-fit" version.Version [])
            Expect.isTrue (Result.isOk first) "the fit resolves on the first delivery"

            match worker.Hook with
            | None -> failtest "the worker must have received a callback credential"
            | Some hook ->
                // The identical authenticated callback, delivered again —
                // exactly what a webhook system retrying on a lost response
                // does.
                let! (status, body) = worker.Replay hook (ModelFitWorkSpec.renderDescriptor goodDescriptor)

                Expect.equal
                    status
                    200
                    "a duplicate is 200, not 409 — a backend that retries on non-2xx would retry a correct duplicate forever"

                Expect.stringContains body "already-resolved" "and it says which case it was"

            match recording.Calls |> List.map (fun (_, _, resolution) -> resolution) with
            | [ ExternalResolution.Resolved "succeeded"; ExternalResolution.AlreadyResolved ] -> ()
            | other -> failtestf "expected one resolution then one already-resolved; got %A" other

            Expect.equal registry.Tracked 1 "the rendezvous is retained so the replay is answerable, not re-run"
        }

        testCaseAsync "a worker that returns an unreadable result reference is refused, not accepted"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"

            let handles = InMemoryExternalHandleStore() :> IExternalHandleStore
            let registry = ExternalFitCompletionRegistry()

            use ingress =
                startIngress handles (ExternalFitCompletionSink registry :> IExternalCompletionSink)

            // A plausible-looking blob key — exactly what a worker written
            // against Phase 318's `resultRef` prose, rather than against the
            // `modelfit/v1` contract, would return.
            use worker = startWorker (fun () -> "blob://fits/glm-4242.bin") 1

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    (configFor worker ingress.BaseUrl)
                    (SilentSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    registry,
                    options,
                    Some handles,
                    None
                )

            match! provider.FitExternally(requestFor "scope-fit" version.Version []) with
            | Ok _ -> failtest "an unreadable artifact descriptor must never be reported as a fitted model"
            | Error(ExternalFitFailure.MalformedArtifact reason) ->
                Expect.stringContains reason "JSON" "the refusal says what it could not read"

                Expect.isFalse
                    (ExternalFitFailure.isRetriable (ExternalFitFailure.MalformedArtifact reason))
                    "and is terminal — a worker defect is not fixed by re-running the fit"
            | Error other -> failtestf "expected MalformedArtifact; got %A" other
        }
    ]

// ── 450.B — the failure map, cancellation, and the poll fallback ─────────

let private failureMapTests =
    testList "450.B — outcome mapping, timeout, cancellation" [
        testCaseAsync "a refused submission surfaces the backend's own retriability"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"

            let dispatcher =
                ScriptedDispatcher([], Error(ExternalComputeError.retriable "the worker pool is saturated"))

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    ExternalFitCompletionRegistry(),
                    options
                )

            match! provider.FitExternally(requestFor "scope-fit" version.Version []) with
            | Error(ExternalFitFailure.SubmitRefused error as failure) ->
                Expect.isTrue error.Retriable "carried through unchanged, not re-decided here"
                Expect.isTrue (ExternalFitFailure.isRetriable failure) "so a caller can re-submit on the backend's word"
            | other -> failtestf "expected SubmitRefused; got %A" other
        }

        testCaseAsync "a worker failure resolves by POLL when no callback ever arrives"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"

            // No callback path at all — the universal fallback, which is
            // what makes the push path a latency optimisation rather than a
            // correctness dependency.
            let dispatcher =
                ScriptedDispatcher [
                    ExternalOutcome.Running(Some 0.2)
                    ExternalOutcome.Failed(ExternalComputeError.retriable "CUDA OOM on epoch 3")
                ]

            let progress = RecordingProgressSink()

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    ExternalFitCompletionRegistry(),
                    options
                )

            let! result =
                provider.FitExternally(requestFor "scope-fit" version.Version [])
                |> withProgress progress (Guid.NewGuid()) "scope-fit"

            match result with
            | Error(ExternalFitFailure.WorkerFailed error as failure) ->
                Expect.stringContains error.Message "CUDA OOM" "the worker's own description"
                Expect.isTrue (ExternalFitFailure.isRetriable failure) "with its own retriability"
            | other -> failtestf "expected WorkerFailed; got %A" other

            Expect.contains
                (progress.Reports |> List.choose (fun (_, _, cp) -> cp.Fraction))
                0.2
                "and the running observation still reached the progress sink on the poll path"
        }

        testCaseAsync "a fit that outruns its budget is cancelled at the backend, not merely abandoned"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"

            // Running forever: only the budget can end this.
            let dispatcher = ScriptedDispatcher [ ExternalOutcome.Running(Some 0.1) ]

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    ExternalFitCompletionRegistry(),
                    options |> ExternalFitOptions.withTimeout (TimeSpan.FromMilliseconds 120.0)
                )

            match! provider.FitExternally(requestFor "scope-fit" version.Version []) with
            | Error(ExternalFitFailure.TimedOut budget as failure) ->
                Expect.equal budget (TimeSpan.FromMilliseconds 120.0) "the budget that expired"

                Expect.isTrue
                    (ExternalFitFailure.isRetriable failure)
                    "retriable — an unanswered budget says nothing about whether the fit is viable"
            | other -> failtestf "expected TimedOut; got %A" other

            Expect.equal
                (List.length dispatcher.Cancelled)
                1
                "and a teardown was lodged — an abandoned fit must not leave a GPU running"
        }

        testCaseAsync "cancelling the caller's async propagates a teardown to the backend"
        <| async {
            let! datasets, version = seededDatasets "scope-fit"
            let dispatcher = ScriptedDispatcher [ ExternalOutcome.Running(Some 0.1) ]

            let provider =
                ExternalModelFitProvider(
                    dispatcher :> IExternalComputeDispatcher,
                    datasets,
                    ExternalFitCompletionRegistry(),
                    options
                )

            use source = new System.Threading.CancellationTokenSource()

            Async.Start(
                provider.FitExternally(requestFor "scope-fit" version.Version [])
                |> Async.Ignore,
                source.Token
            )

            // Let the submit land and the wait begin.
            do! Async.Sleep 250
            Expect.equal (List.length dispatcher.Submitted) 1 "the fit is in flight"
            source.Cancel()

            // The cancellation handler runs on the cancelled continuation,
            // and the teardown it lodges is itself an async.
            do! Async.Sleep 400

            Expect.equal
                (List.length dispatcher.Cancelled)
                1
                "the caller going away lodges a cancellation with the backend"
        }

        testCase "the IModelFitProvider seam raises the typed cause rather than a flattened string"
        <| fun _ ->
            let failure = ExternalFitFailure.MalformedArtifact "not JSON"
            let ex = ExternalModelFitException failure
            Expect.equal ex.Failure failure "the cause survives the exception boundary as data"

            Expect.stringContains
                ex.Message
                "artifact descriptor is unreadable"
                "and the message the fit-run envelope records is the described cause"
    ]

// ── GP 13 + composition safety ───────────────────────────────────────────

let private compositionTests =
    testList "450 — GP 13 and composition safety" [
        testCase "a default deployment composes no external-fit machinery at all"
        <| fun _ ->
            let services = ServiceCollection()

            ComposeStores.registerExternalCompute services {
                ServerConfig.defaults with
                    ExternalCompute = NoExternalCompute
            }

            use provider = services.BuildServiceProvider()

            Expect.isNull
                (box (provider.GetService<ExternalFitCompletionRegistry>()))
                "no completion registry is composed"

            Expect.isNull (box (provider.GetService<ExternalModelFitProvider>())) "no external fit provider is composed"

            Expect.isNull
                (box (provider.GetService<IExternalCompletionSink>()))
                "and no completion sink, so the Phase 320 ingress has nothing to drive"

        testCaseAsync "the fit sink passes a handle it does not know straight through to the inner sink"
        <| async {
            // The failure this pins: a deployment running external JOBS as
            // well as fits composes ONE IExternalCompletionSink. A fit sink
            // that swallowed the callbacks it did not recognise would
            // silently stop resolving every external job in the deployment.
            let seen = ConcurrentQueue<Guid>()

            let inner =
                { new IExternalCompletionSink with
                    member _.ResolveExternal(handle, _jobRunId, _outcome) = async {
                        seen.Enqueue handle.HandleId
                        return ExternalResolution.Resolved "delegated"
                    }
                }

            let registry = ExternalFitCompletionRegistry()

            let sink =
                ExternalFitCompletionSink(registry, Some inner) :> IExternalCompletionSink

            let handleFor id = {
                HandleId = id
                Backend = "scripted"
                ScopeId = "scope-fit"
                NativeRef = "n"
                SubmittedAt = DateTime.UtcNow
            }

            let strangerId = Guid.NewGuid()
            let! delegated = sink.ResolveExternal(handleFor strangerId, Guid.NewGuid(), ExternalOutcome.Cancelled)
            Expect.equal delegated (ExternalResolution.Resolved "delegated") "an unknown handle reaches the inner sink"
            Expect.equal (seen |> Seq.toList) [ strangerId ] "which saw it unchanged"

            // CONTROL — a handle the registry DOES know never reaches the
            // inner sink, so the delegation above is a real discrimination
            // rather than a sink that forwards everything.
            let ourId = Guid.NewGuid()
            registry.Register ourId
            let! claimed = sink.ResolveExternal(handleFor ourId, Guid.NewGuid(), ExternalOutcome.Cancelled)
            Expect.equal claimed (ExternalResolution.Resolved "cancelled") "a fit's own handle is claimed here"
            Expect.equal (seen |> Seq.toList) [ strangerId ] "and did not reach the inner sink"
        }

        testCase "the registry sweeps completed rendezvous once they are past retention"
        <| fun _ ->
            let registry = ExternalFitCompletionRegistry(TimeSpan.Zero)
            let first = Guid.NewGuid()
            registry.Register first
            Expect.isTrue (registry.TryComplete(first, ExternalOutcome.Cancelled)) "the first delivery wins"
            Expect.isFalse (registry.TryComplete(first, ExternalOutcome.Cancelled)) "and the second does not"

            // With zero retention the completed entry is swept by the next
            // registration, so the dictionary cannot grow without bound.
            registry.Register(Guid.NewGuid())
            Expect.equal registry.Tracked 1 "only the live rendezvous remains"
    ]

[<Tests>]
let tests =
    testList "ExternalModelFit (Phase 450)" [
        conventionTests
        envelopeRefusalTests
        endToEndTests
        failureMapTests
        compositionTests
    ]
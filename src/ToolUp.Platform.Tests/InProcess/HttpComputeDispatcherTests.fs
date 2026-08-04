// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.HttpComputeDispatcherTests

open System
open System.Collections.Concurrent
open System.Net
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
open ToolUp.Platform.ExternalCompute.Http
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.Platform.Server
open ToolUp.Platform.Tests.Contracts.IExternalComputeDispatcherContract

// ─── Phase 322 — the HTTP/REST external-compute companion ─────────────
//
// Bound against a **real socket**, not an `HttpMessageHandler` stub. The
// whole companion is "speak HTTP correctly to something that speaks HTTP
// back", so a fake transport would verify the half that is not in doubt
// while eliding the half that is: header names actually reaching the
// wire, a 404 arriving as a status code rather than as a thrown
// exception, a request timeout expiring against a server that really does
// not answer. `MockOidcServer` set this precedent in this pack for the
// same reason.
//
// The pack has four jobs:
//
//   1. **Pass the Phase 324 contract pack UNMODIFIED** (322.F). The
//      binding declares `HonoursIdempotency = false` and
//      `ValidatesHandleScope = false` — see `HttpComputeDispatcher`'s
//      header for why each is the honest answer for a generic HTTP
//      backend, and note the pack asserts a real fallback law in both
//      cases rather than skipping.
//   2. **Pin the per-call credential read** with a rotation the
//      dispatcher must pick up mid-run, plus the control that the probe
//      could have failed.
//   3. **Pin the error classification**, which IS the retry contract:
//      5xx / 408 / 429 / timeout retriable, other 4xx terminal, a 404 on
//      a status read terminal and never a fabricated `Cancelled`.
//   4. **Prove push completion end to end** (320): the credential
//      reaches the service, the service calls the real ingress back over
//      a real socket, and the run resolves.
//
// Falsifiable controls are marked `CONTROL` and each one asserts the
// probe beside it is capable of failing — a check that agrees with what
// you expected is the least-examined kind of evidence.

// ── the stub compute service ───────────────────────────────────────────

/// One unit the stub service holds. Immutable; replaced under the
/// dictionary key, so a concurrent poll never observes a torn value.
type private StubUnit = {
    Kind: string
    Status: string
    Percent: float option
    ResultRef: string option
    Error: string option
    Retriable: bool option
    Scope: string option
    Idempotency: string option
    Hints: Map<string, string>
    SubmitCallbackUrl: string option
    TimeoutSeconds: int option
}

/// What the service was told about a unit's completion webhook.
type private StubWebhook = {
    Url: string
    Secret: string
    HandleId: string option
}

/// An in-process HTTP compute service in the request/response shape:
/// `POST /jobs` accepts, `GET /jobs/{id}` answers with the current state,
/// `POST /jobs/{id}/cancel` tears down, `POST /jobs/{id}/webhook`
/// receives the completion credential, `GET /healthz` answers a probe.
///
/// Binds `127.0.0.1:0` so the OS assigns a free port — no fixed-port
/// clash across parallel test processes.
type private StubComputeService() =
    let units = ConcurrentDictionary<string, StubUnit>()
    let webhooks = ConcurrentDictionary<string, StubWebhook>()
    let byIdempotency = ConcurrentDictionary<string * string, string>()
    let authHeaders = ConcurrentQueue<string>()
    let mutable baseUrl = ""

    /// Force the next N submits to answer this status code. `None`
    /// accepts normally.
    let mutable forcedSubmitStatus: int option = None
    /// Answer a submit with a body carrying no job id at all.
    let mutable omitJobId = false
    /// Delay every submit by this much — used to expire a per-request
    /// budget against a server that genuinely does not answer in time.
    let mutable submitDelay = TimeSpan.Zero
    /// Return the existing job id for a (scope, idempotency key) already
    /// accepted. Backend-side dedupe, which is where Phase 318 puts it.
    let mutable dedupeIdempotency = false

    let recordAuth (ctx: HttpContext) =
        match ctx.Request.Headers.TryGetValue "Authorization" with
        | true, values -> authHeaders.Enqueue(string values)
        | _ ->
            match ctx.Request.Headers.TryGetValue "X-Api-Key" with
            | true, values -> authHeaders.Enqueue(string values)
            | _ -> ()

    let readBody (ctx: HttpContext) : Task<JsonDocument> = task {
        use reader = new IO.StreamReader(ctx.Request.Body)
        let! text = reader.ReadToEndAsync()

        return
            if String.IsNullOrWhiteSpace text then
                JsonDocument.Parse "{}"
            else
                JsonDocument.Parse text
    }

    let stringField (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None

    let statusJson (unit: StubUnit) =
        let parts = [
            sprintf "\"state\":%s" (JsonSerializer.Serialize unit.Status)

            match unit.Percent with
            | Some percent -> sprintf "\"percentComplete\":%s" (JsonSerializer.Serialize percent)
            | None -> ()

            match unit.ResultRef with
            | Some reference -> sprintf "\"artifact\":{\"uri\":%s}" (JsonSerializer.Serialize reference)
            | None -> ()

            match unit.Error with
            | Some message ->
                sprintf
                    "\"failure\":{\"message\":%s,\"retriable\":%s}"
                    (JsonSerializer.Serialize message)
                    (if unit.Retriable = Some true then "true" else "false")
            | None -> ()
        ]

        sprintf "{\"job\":{%s}}" (String.concat "," parts)

    let app =
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        let a = builder.Build()

        a.MapPost(
            "/jobs",
            Func<HttpContext, Task>(fun ctx -> task {
                recordAuth ctx
                use! document = readBody ctx
                let root = document.RootElement

                if submitDelay > TimeSpan.Zero then
                    do! Task.Delay submitDelay

                if forcedSubmitStatus.IsSome then
                    let code = forcedSubmitStatus.Value
                    ctx.Response.StatusCode <- code
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(sprintf "{\"detail\":\"stub forced HTTP %d\"}" code)
                elif omitJobId then
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync "{\"job\":{}}"
                else
                    let scope = stringField root "scope"
                    let idempotency = stringField root "idempotencyKey"

                    let existing =
                        if not dedupeIdempotency then
                            None
                        else
                            match scope, idempotency with
                            | Some s, Some key ->
                                match byIdempotency.TryGetValue((s, key)) with
                                | true, id -> Some id
                                | _ -> None
                            | _ -> None

                    let id =
                        match existing with
                        | Some id -> id
                        | None ->
                            let minted = sprintf "job-%s" (Guid.NewGuid().ToString "N")

                            let hints =
                                match root.TryGetProperty "resources" with
                                | true, value when value.ValueKind = JsonValueKind.Object ->
                                    value.EnumerateObject()
                                    |> Seq.choose (fun property ->
                                        if property.Value.ValueKind = JsonValueKind.String then
                                            Some(property.Name, property.Value.GetString())
                                        else
                                            None)
                                    |> Map.ofSeq
                                | _ -> Map.empty

                            units[minted] <- {
                                Kind = stringField root "kind" |> Option.defaultValue ""
                                Status = "queued"
                                Percent = None
                                ResultRef = None
                                Error = None
                                Retriable = None
                                Scope = scope
                                Idempotency = idempotency
                                Hints = hints
                                SubmitCallbackUrl = stringField root "callbackUrl"
                                TimeoutSeconds =
                                    match root.TryGetProperty "timeoutSeconds" with
                                    | true, value when value.ValueKind = JsonValueKind.Number -> Some(value.GetInt32())
                                    | _ -> None
                            }

                            match scope, idempotency with
                            | Some s, Some key when dedupeIdempotency -> byIdempotency[(s, key)] <- minted
                            | _ -> ()

                            minted

                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(sprintf "{\"job\":{\"id\":%s}}" (JsonSerializer.Serialize id))
            })
        )
        |> ignore

        a.MapGet(
            "/jobs/{id}",
            Func<HttpContext, Task>(fun ctx -> task {
                recordAuth ctx
                let id = string ctx.Request.RouteValues["id"]

                match units.TryGetValue id with
                | true, unit ->
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(statusJson unit)
                | _ ->
                    ctx.Response.StatusCode <- 404
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync "{\"detail\":\"no such job\"}"
            })
        )
        |> ignore

        a.MapPost(
            "/jobs/{id}/cancel",
            Func<HttpContext, Task>(fun ctx -> task {
                recordAuth ctx
                let id = string ctx.Request.RouteValues["id"]

                match units.TryGetValue id with
                | true, unit ->
                    // A terminal unit is NOT clobbered — a cancel
                    // racing a completion must not discard the
                    // result.
                    let terminal = [ "succeeded"; "failed"; "cancelled" ] |> List.contains unit.Status

                    if not terminal then
                        units[id] <- { unit with Status = "cancelled" }

                    ctx.Response.StatusCode <- 202
                    do! ctx.Response.WriteAsync ""
                | _ ->
                    ctx.Response.StatusCode <- 404
                    do! ctx.Response.WriteAsync ""
            })
        )
        |> ignore

        a.MapPost(
            "/jobs/{id}/webhook",
            Func<HttpContext, Task>(fun ctx -> task {
                recordAuth ctx
                let id = string ctx.Request.RouteValues["id"]
                use! document = readBody ctx
                let root = document.RootElement

                match stringField root "callbackUrl", stringField root "callbackSecret" with
                | Some url, Some secret ->
                    webhooks[id] <- {
                        Url = url
                        Secret = secret
                        HandleId = stringField root "handleId"
                    }

                    ctx.Response.StatusCode <- 204
                | _ -> ctx.Response.StatusCode <- 400

                do! ctx.Response.WriteAsync ""
            })
        )
        |> ignore

        a.MapGet(
            "/healthz",
            Func<HttpContext, Task>(fun ctx ->
                recordAuth ctx
                ctx.Response.ContentType <- "application/json"
                ctx.Response.WriteAsync "{\"ok\":true}")
        )
        |> ignore

        // A status endpoint answering HTML — a proxy error page, a login
        // redirect, an ingress default backend. The shape a deployment
        // actually hits, and one a JSON parser must refuse rather than
        // throw over.
        a.MapGet(
            "/notjson/{id}",
            Func<HttpContext, Task>(fun ctx ->
                ctx.Response.ContentType <- "text/html"
                ctx.Response.WriteAsync "<html><body>502 Bad Gateway</body></html>")
        )
        |> ignore

        a

    member _.StartAsync() : Task = task {
        do! (app :> IHost).StartAsync()

        baseUrl <-
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> Seq.head
    }

    /// Base URL (e.g. `http://127.0.0.1:54321`). Valid after `StartAsync`.
    member _.BaseUrl = baseUrl

    /// The harness lever the Phase 324 pack pulls: bring the unit behind
    /// `nativeRef` to `outcome`. A table write, so it is visible to the
    /// very next poll — the immediate end of the `Drive` + `settle`
    /// seam's range.
    member _.Drive (nativeRef: string) (outcome: ExternalOutcome) = async {
        match units.TryGetValue nativeRef with
        | true, unit ->
            let updated =
                match outcome with
                | ExternalOutcome.Pending -> {
                    unit with
                        Status = "queued"
                        Percent = None
                  }
                | ExternalOutcome.Running progress -> {
                    unit with
                        Status = "running"
                        // Reported as a PERCENTAGE, so the config's
                        // ProgressScale is exercised rather than
                        // bypassed by a 1.0-scaled fixture.
                        Percent = progress |> Option.map (fun fraction -> fraction * 100.0)
                  }
                | ExternalOutcome.Succeeded reference -> {
                    unit with
                        Status = "succeeded"
                        ResultRef = Some reference
                  }
                | ExternalOutcome.Failed error -> {
                    unit with
                        Status = "failed"
                        Error = Some error.Message
                        Retriable = Some error.Retriable
                  }
                | ExternalOutcome.Cancelled -> { unit with Status = "cancelled" }

            units[nativeRef] <- updated
        | _ -> ()
    }

    /// Set an arbitrary status label, including one the config does not
    /// declare — `Drive` cannot express that, because it takes a typed
    /// outcome.
    member _.SetRawStatus (nativeRef: string) (status: string) (resultRef: string option) =
        match units.TryGetValue nativeRef with
        | true, unit ->
            units[nativeRef] <- {
                unit with
                    Status = status
                    ResultRef = resultRef
            }
        | _ -> ()

    /// Every `Authorization` / `X-Api-Key` value the service has seen, in
    /// arrival order.
    member _.AuthHeaders = authHeaders |> Seq.toList

    /// What the service was told about a unit's completion webhook.
    member _.Webhook(nativeRef: string) =
        match webhooks.TryGetValue nativeRef with
        | true, hook -> Some hook
        | _ -> None

    /// What the service recorded about an accepted unit.
    member _.Unit(nativeRef: string) =
        match units.TryGetValue nativeRef with
        | true, unit -> Some unit
        | _ -> None

    member _.ForceSubmitStatus(code: int option) = forcedSubmitStatus <- code
    member _.SetOmitJobId(value: bool) = omitJobId <- value
    member _.SetSubmitDelay(value: TimeSpan) = submitDelay <- value
    member _.SetDedupeIdempotency(value: bool) = dedupeIdempotency <- value

    /// The service calling the platform back, exactly as a real webhook
    /// would: a POST to the URL it was handed, carrying the secret it was
    /// handed in the header the platform named.
    member _.SendCallback (hook: StubWebhook) (statusLabel: string) (resultRef: string option) = async {
        use client = new HttpClient()

        let body =
            let parts = [
                sprintf "\"handleId\":%s" (JsonSerializer.Serialize(hook.HandleId |> Option.defaultValue ""))
                sprintf "\"status\":%s" (JsonSerializer.Serialize statusLabel)

                match resultRef with
                | Some reference -> sprintf "\"resultRef\":%s" (JsonSerializer.Serialize reference)
                | None -> ()
            ]

            sprintf "{%s}" (String.concat "," parts)

        use request =
            new HttpRequestMessage(
                HttpMethod.Post,
                hook.Url,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            )

        request.Headers.Add(ExternalCallback.SecretHeader, hook.Secret)
        let! response = client.SendAsync request |> Async.AwaitTask
        return int response.StatusCode
    }

    interface IDisposable with
        member _.Dispose() =
            (app :> IHost).StopAsync().GetAwaiter().GetResult()
            (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

let private startStub () =
    let service = new StubComputeService()
    service.StartAsync().GetAwaiter().GetResult()
    service

// ── test doubles ───────────────────────────────────────────────────────

/// A writable in-memory `ISecretStore`, so a rotation is a real store
/// mutation rather than a swapped dependency.
type private RotatingSecretStore() =
    let secrets = ConcurrentDictionary<string * string, string>()

    member _.Set (scope: string) (key: string) (value: string) = secrets[(scope, key)] <- value

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match secrets.TryGetValue((scopeId, key)) with
            | true, value -> return Some value
            | _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            secrets[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            secrets.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                secrets.Keys
                |> Seq.filter (fun (scope, _) -> scope = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

type private RecordingLogger() =
    let lines = ConcurrentQueue<string>()
    member _.Lines = lines |> Seq.toList

    member _.Contains(needle: string) =
        lines |> Seq.exists (fun line -> line.Contains needle)

    interface ILogger with
        member _.Debug message = lines.Enqueue("DEBUG " + message)
        member _.Info message = lines.Enqueue("INFO " + message)
        member _.Warn message = lines.Enqueue("WARN " + message)
        member _.Error(message, _) = lines.Enqueue("ERROR " + message)

/// Records every resolution the ingress drives, so the push path is
/// asserted on the OBSERVABLE EFFECT rather than on an HTTP 200.
type private RecordingCompletionSink() =
    let calls = ConcurrentQueue<Guid * Guid * ExternalOutcome>()
    member _.Calls = calls |> Seq.toList

    interface IExternalCompletionSink with
        member _.ResolveExternal(handle, jobRunId, outcome) = async {
            calls.Enqueue((handle.HandleId, jobRunId, outcome))
            return ExternalResolution.Resolved(ExternalOutcome.label outcome)
        }

/// The real Phase 320 ingress, on a real socket, so the stub service can
/// POST to it the way a webhook does.
type private IngressHost(store: IExternalHandleStore, sink: RecordingCompletionSink) =
    let mutable baseUrl = ""

    let app =
        // Module-level throttle + warning counters, per the ingress's own
        // documentation, so they are reset per host or the tests inherit
        // each other's counts.
        ExternalComputeCallback.resetThrottleState ()

        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        builder.Services.AddGiraffe() |> ignore
        builder.Services.AddSingleton<IExternalHandleStore> store |> ignore

        builder.Services.AddSingleton<IExternalCompletionSink>(sink :> IExternalCompletionSink)
        |> ignore

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

// ── config against the stub ────────────────────────────────────────────

let private sharedHttpClient = lazy (new HttpClient())

/// The config a deployment would write for the stub service.
let private configFor (stub: StubComputeService) =
    HttpComputeConfig.create
        "http-stub"
        (stub.BaseUrl + "/jobs")
        (stub.BaseUrl + "/jobs/" + HttpComputeConfig.JobIdPlaceholder)
        (JsonPath.ofString "job.id")
        (JsonPath.ofString "job.state")
    |> HttpComputeConfig.withResultRef (JsonPath.ofString "job.artifact.uri")
    // The stub reports a PERCENTAGE, so ProgressScale is exercised.
    |> HttpComputeConfig.withProgress 100.0 (JsonPath.ofString "job.percentComplete")
    |> HttpComputeConfig.withFailureDetail
        (JsonPath.ofString "job.failure.message")
        (Some(JsonPath.ofString "job.failure.retriable"))
    |> HttpComputeConfig.withCancel "POST" (stub.BaseUrl + "/jobs/" + HttpComputeConfig.JobIdPlaceholder + "/cancel")
    |> HttpComputeConfig.withHealthUrl (stub.BaseUrl + "/healthz")

let private dispatcherFor (stub: StubComputeService) =
    HttpComputeDispatcher.createTyped
        (configFor stub)
        (RotatingSecretStore() :> ISecretStore)
        sharedHttpClient.Value
        (RecordingLogger() :> ILogger)

// ── 322.F — the Phase 324 contract pack, unmodified ────────────────────

/// One stub service for the whole binding. The pack calls `create` per
/// law, and each law submits its own units against fresh job ids, so
/// there is nothing to leak between them; a Kestrel host per law would
/// buy isolation the ids already provide.
let private contractStub = lazy (startStub ())

/// What this companion honestly claims. Both `false` values are
/// substantive rather than convenient — the pack asserts a real fallback
/// law for each, and `HttpComputeDispatcher`'s header records why a
/// generic HTTP backend cannot claim more.
let private stubConformance = {
    ExternalComputeConformance.strict with
        // The service may dedupe an idempotency key, but the HANDLE id is
        // platform-minted per Submit, so a resubmit cannot return the
        // same handle. Phase 318 words this as SHOULD; the memoizing
        // decorator is the portable answer.
        HonoursIdempotency = false
        // Poll / Cancel address the service by the opaque NativeRef,
        // which is all the service gave us, so a re-scoped handle is
        // indistinguishable from here. GP 4 is structural a layer up.
        ValidatesHandleScope = false
}

let private conformanceTests =
    contractFor "HttpComputeDispatcher over a stub HTTP compute service (Phase 322)" stubConformance (fun () ->
        let stub = contractStub.Value

        {
            Dispatcher = dispatcherFor stub :> IExternalComputeDispatcher
            Drive = fun handle outcome -> stub.Drive handle.NativeRef outcome
        })

// ── 322.A — selectors ──────────────────────────────────────────────────

let private selectorTests =
    let parse (json: string) = JsonDocument.Parse json

    testList "322.A — dotted-path JSON selectors" [

        test "a nested property path reads the value" {
            use document = parse """{"job":{"status":"running","id":8812}}"""

            Expect.equal
                (JsonPath.selectString (JsonPath.ofString "job.status") document.RootElement)
                (Some "running")
                "property navigation"

            Expect.equal
                (JsonPath.selectString (JsonPath.ofString "job.id") document.RootElement)
                (Some "8812")
                "a numeric id is read as its raw text — the NativeRef is an opaque string either way"
        }

        test "array indices navigate, and compose with properties in both directions" {
            use document =
                parse """{"items":[{"phase":"Pending"},{"phase":"Succeeded","refs":["a","b"]}]}"""

            Expect.equal
                (JsonPath.selectString (JsonPath.ofString "items[1].phase") document.RootElement)
                (Some "Succeeded")
                "index then property"

            Expect.equal
                (JsonPath.selectString (JsonPath.ofString "items[1].refs[0]") document.RootElement)
                (Some "a")
                "property then index"
        }

        test "an absent step, a wrong-shaped step and an out-of-range index all read None" {
            use document = parse """{"job":{"status":"running"},"items":[1]}"""
            let root = document.RootElement

            Expect.isNone (JsonPath.selectString (JsonPath.ofString "job.missing") root) "absent property"

            Expect.isNone
                (JsonPath.selectString (JsonPath.ofString "job.status.deeper") root)
                "a property step against a string is not an error, it is absence"

            Expect.isNone (JsonPath.selectString (JsonPath.ofString "items[3]") root) "out-of-range index"
            Expect.isNone (JsonPath.selectString (JsonPath.ofString "job[0]") root) "an index against an object"
        }

        test "a composite value is not stringified into a selector result" {
            use document = parse """{"job":{"artifact":{"uri":"s3://x"}}}"""

            Expect.isNone
                (JsonPath.selectString (JsonPath.ofString "job.artifact") document.RootElement)
                "an object is not a scalar — reading one as a job id would produce a NativeRef that addresses nothing"
        }

        test "numbers and booleans are read from either their JSON form or a string carrying one" {
            use document = parse """{"a":0.42,"b":"0.42","c":true,"d":"TRUE","e":"nope"}"""
            let root = document.RootElement
            Expect.equal (JsonPath.selectFloat (JsonPath.ofString "a") root) (Some 0.42) "JSON number"
            Expect.equal (JsonPath.selectFloat (JsonPath.ofString "b") root) (Some 0.42) "number as string"
            Expect.equal (JsonPath.selectBool (JsonPath.ofString "c") root) (Some true) "JSON boolean"
            Expect.equal (JsonPath.selectBool (JsonPath.ofString "d") root) (Some true) "boolean as string, any case"
            Expect.isNone (JsonPath.selectBool (JsonPath.ofString "e") root) "an unparseable string is not a boolean"

            Expect.isNone
                (JsonPath.selectBool (JsonPath.ofString "a") root)
                "a NUMBER is not read as a boolean — `0` meaning false is a convention, and guessing it wrong on the retriability flag re-submits work forever"
        }

        test "malformed selectors are refused with a reason, not silently reinterpreted" {
            let malformed = [
                "", "empty"
                "   ", "whitespace"
                ".a", "leading dot"
                "a.", "trailing dot"
                "a..b", "empty element"
                "a[", "unclosed bracket"
                "a[x]", "non-numeric index"
                "a[-1]", "negative index"
                "a[1", "unclosed bracket after an index"
            ]

            for selector, why in malformed do
                match JsonPath.parse selector with
                | Ok path -> failtestf "'%s' (%s) must be refused; parsed as %A" selector why path.Segments
                | Error _ -> ()
        }

        test "CONTROL — a well-formed selector IS accepted, so the refusals above are not vacuous" {
            for selector in [ "a"; "a.b"; "a[0]"; "a[0][1]"; "a[0].b.c[2]"; "  a.b  " ] do
                match JsonPath.parse selector with
                | Ok _ -> ()
                | Error e -> failtestf "'%s' must parse; got %s" selector e
        }

        test "the retained Text is what the operator wrote, so a diagnostic can name it" {
            Expect.equal (JsonPath.ofString "job.artifact.uri").Text "job.artifact.uri" "verbatim"
        }
    ]

// ── 322.A — the status vocabulary ──────────────────────────────────────

let private statusMapTests =
    testList "322.A / 322.C — the status vocabulary" [

        test "the defaults classify the labels real services use, case- and space-insensitively" {
            let cases = [
                "queued", HttpStatusClass.Pending
                "  RUNNING  ", HttpStatusClass.Running
                "Succeeded", HttpStatusClass.Succeeded
                "COMPLETE", HttpStatusClass.Succeeded
                "failed", HttpStatusClass.Failed
                "Canceled", HttpStatusClass.Cancelled
            ]

            for label, expected in cases do
                Expect.equal
                    (HttpComputeStatusMap.classify HttpComputeStatusMap.defaults label)
                    (Some expected)
                    (sprintf "'%s'" label)
        }

        test "an undeclared label classifies as None — it is never guessed" {
            Expect.isNone
                (HttpComputeStatusMap.classify HttpComputeStatusMap.defaults "WORKING")
                "every available guess is a claim about whether the work finished"
        }

        test "a label declared under two classes is reported as ambiguous" {
            let overlapping = {
                HttpComputeStatusMap.defaults with
                    Failed = "done" :: HttpComputeStatusMap.defaults.Failed
            }

            Expect.contains
                (HttpComputeStatusMap.ambiguous overlapping)
                "done"
                "'done' is declared as both Succeeded and Failed; which wins would be an accident of list order"

            Expect.isEmpty
                (HttpComputeStatusMap.ambiguous HttpComputeStatusMap.defaults)
                "CONTROL — the shipped defaults are unambiguous, so the detector is not simply always positive"
        }
    ]

// ── 322.A / 322.E — config validation ──────────────────────────────────

let private configTests =
    let baseConfig () =
        HttpComputeConfig.create
            "http"
            "https://compute.example/jobs"
            ("https://compute.example/jobs/" + HttpComputeConfig.JobIdPlaceholder)
            (JsonPath.ofString "id")
            (JsonPath.ofString "status")

    testList "322.A — config validation refuses at compose, not at first submission" [

        test "a well-formed config has no problems" {
            Expect.isEmpty (HttpComputeConfig.problems (baseConfig ())) "the baseline is usable"
        }

        test "a status template with no {jobId} is refused — every poll would read the same URL" {
            let broken = {
                baseConfig () with
                    StatusUrlTemplate = "https://compute.example/jobs/latest"
            }

            let problems = HttpComputeConfig.problems broken
            Expect.isNonEmpty problems "refused"

            Expect.isTrue
                (problems |> List.exists (fun p -> p.Contains HttpComputeConfig.JobIdPlaceholder))
                "and the diagnostic names the missing placeholder"
        }

        test "an auth format with no {secret} is refused — the credential would never reach the header" {
            let broken =
                baseConfig ()
                |> HttpComputeConfig.withAuth {
                    HeaderName = "Authorization"
                    SecretScope = "_platform"
                    SecretKey = "token"
                    ValueFormat = "Bearer <token>"
                }

            Expect.isNonEmpty (HttpComputeConfig.problems broken) "refused"
        }

        test "a relative URL, a non-http scheme and an empty backend label are each refused" {
            let cases = [
                {
                    baseConfig () with
                        SubmitUrl = "/jobs"
                },
                "relative submit URL"
                {
                    baseConfig () with
                        SubmitUrl = "ftp://compute.example/jobs"
                },
                "non-http scheme"
                { baseConfig () with Backend = "  " }, "empty backend label"
                {
                    baseConfig () with
                        ProgressScale = 0.0
                },
                "non-positive progress scale"
                {
                    baseConfig () with
                        RequestTimeout = TimeSpan.Zero
                },
                "non-positive request timeout"
            ]

            for config, why in cases do
                Expect.isNonEmpty (HttpComputeConfig.problems config) why
        }

        test "create RAISES on a malformed config, in front of the operator who wrote it" {
            let broken = {
                baseConfig () with
                    StatusUrlTemplate = "https://compute.example/jobs/latest"
            }

            Expect.throwsT<ArgumentException>
                (fun () ->
                    HttpComputeDispatcher.create
                        broken
                        (RotatingSecretStore() :> ISecretStore)
                        sharedHttpClient.Value
                        (RecordingLogger() :> ILogger)
                    |> ignore)
                "a config whose every poll would read the same URL must not produce a dispatcher"
        }

        test "{jobId} expansion URL-escapes the opaque native ref" {
            Expect.equal
                (HttpComputeConfig.expandJobId "a b/c?d" ("https://x/jobs/" + HttpComputeConfig.JobIdPlaceholder))
                "https://x/jobs/a%20b%2Fc%3Fd"
                "the ref is backend-minted and opaque, so it is escaped rather than trusted to be path-safe"
        }
    ]

// ── 322.E — mode wiring + GP 13 ────────────────────────────────────────

let private composeTests =
    testList "322.E — composition + GP 11 / GP 13" [

        test "the default deployment is untouched: ExternalCompute stays NoExternalCompute" {
            Expect.equal
                ServerConfig.defaults.ExternalCompute
                NoExternalCompute
                "a deployment that never composes this companion keeps the Phase 318 default"
        }

        test "withHttpCompute flips the mode to CustomExternalCompute" {
            use stub = startStub ()

            let app =
                ServerApp.empty
                |> HttpComputeCompose.withHttpCompute
                    (configFor stub)
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            Expect.equal
                app.Config.ExternalCompute
                CustomExternalCompute
                "under the NoExternalCompute default, compose registers the no-op dispatcher — and a later registration of the same service type is what GetService resolves, so leaving the mode alone would make whether real work is submitted depend on registration ORDER"
        }

        test "withHttpCompute registers the dispatcher singleton, and it is the HTTP one" {
            use stub = startStub ()

            let app =
                ServerApp.empty
                |> HttpComputeCompose.withHttpCompute
                    (configFor stub)
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            let services = ServiceCollection()

            match app.Extensions.ServiceConfig with
            | Some register -> register services |> ignore
            | None -> failtest "the compose helper must contribute a ServiceConfig"

            use provider = services.BuildServiceProvider()
            let resolved = provider.GetService<IExternalComputeDispatcher>()
            Expect.isNotNull (box resolved) "the seam resolves"
            Expect.equal resolved.Backend "http-stub" "and it is this companion's dispatcher"
        }

        test "withHttpCompute registers the readiness probe and the startup preflight" {
            use stub = startStub ()

            let app =
                ServerApp.empty
                |> HttpComputeCompose.withHttpCompute
                    (configFor stub)
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            Expect.isTrue
                (app.HealthChecks
                 |> List.exists (fun probe -> probe.Name = "external_compute:http-stub"))
                "the readiness probe is registered"

            Expect.isTrue
                (app.ConfigValidators
                 |> List.exists (fun validator -> validator.Name.StartsWith "external-compute-http"))
                "the preflight is registered"
        }

        test "no readiness probe is invented when the config names no health endpoint" {
            use stub = startStub ()

            let noHealth = { configFor stub with HealthUrl = None }

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    noHealth
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            Expect.isNone
                (HttpComputeHealth.tryHealthCheck dispatcher)
                "probing the submit URL would SUBMIT WORK on every readiness poll, and a status URL needs a job id there is no safe value for — an absent probe is honest"

            Expect.isTrue
                (HttpComputeHealth.tryHealthCheck (dispatcherFor stub) |> Option.isSome)
                "CONTROL — the probe IS produced when a health URL is configured, so the None above is about the config and not about the helper"
        }

        test "fromEnv is None unless the companion is selected (GP 13)" {
            let previous = Environment.GetEnvironmentVariable HttpComputeConfig.SelectorEnvVar

            try
                Environment.SetEnvironmentVariable(HttpComputeConfig.SelectorEnvVar, null)

                Expect.isNone
                    (HttpComputeConfig.fromEnv ())
                    "an unset selector means this companion contributes nothing at all"

                Environment.SetEnvironmentVariable(HttpComputeConfig.SelectorEnvVar, "kubernetes")

                Expect.isNone
                    (HttpComputeConfig.fromEnv ())
                    "another backend being selected is not this one being misconfigured"

                Environment.SetEnvironmentVariable(HttpComputeConfig.SelectorEnvVar, "http")

                match HttpComputeConfig.fromEnv () with
                | None -> failtest "selecting http must produce a verdict, not silence"
                | Some(Ok config) -> failtestf "with no URLs configured this cannot be usable; got %A" config
                | Some(Error problems) ->
                    Expect.isNonEmpty problems "and the verdict names what is missing, all of it at once"
            finally
                Environment.SetEnvironmentVariable(HttpComputeConfig.SelectorEnvVar, previous)
        }

        test "the dispatcher declares NO isolation posture, so an Isolated spec is refused" {
            use stub = startStub ()
            let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher

            Expect.equal
                (ExecutionProfileGate.postureOf dispatcher)
                IsolationPosture.standardOnly
                "a generic HTTP endpoint cannot honestly assert no-egress, so it claims nothing"

            match
                ExecutionProfileGate.check dispatcher (ExternalWorkSpec.create "k" "{}" |> ExternalWorkSpec.isolated)
            with
            | Ok() -> failtest "an Isolated spec must be refused by a backend that declares no posture"
            | Error e ->
                Expect.isFalse e.Retriable "and terminally — a backend does not become isolating by being asked twice"

            match ExecutionProfileGate.check dispatcher (ExternalWorkSpec.create "k" "{}") with
            | Ok() -> ()
            | Error e ->
                failtestf
                    "CONTROL — a Standard spec must pass the same gate untouched (Phase 318 exactly); got %s"
                    e.Message
        }
    ]

// ── 322.A — the per-call credential read ───────────────────────────────

let private authTests =
    testList "322.A — the credential is read PER CALL, never snapshotted" [

        testCaseAsync "rotating the secret mid-run is picked up on the next request"
        <| async {
            use stub = startStub ()
            let secrets = RotatingSecretStore()
            secrets.Set "_platform" "compute-token" "token-v1"

            let config =
                configFor stub
                |> HttpComputeConfig.withAuth (HttpComputeAuth.bearer "compute-token")

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (secrets :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match! dispatcher.Submit("team-rotate", ExternalWorkSpec.create "train" "{}") with
            | Error e -> failtestf "the first submission must be accepted; got %s" e.Message
            | Ok first ->
                Expect.equal
                    (List.tryLast stub.AuthHeaders)
                    (Some "Bearer token-v1")
                    "the first request presents the credential that was in the store when it was made"

                // The rotation. A real store mutation, not a swapped
                // dependency — the dispatcher instance is the same one.
                secrets.Set "_platform" "compute-token" "token-v2"

                let! _ = dispatcher.Poll first

                Expect.equal
                    (List.tryLast stub.AuthHeaders)
                    (Some "Bearer token-v2")
                    "the NEXT request presents the rotated credential — no restart, no cache invalidation"

                match! dispatcher.Submit("team-rotate", ExternalWorkSpec.create "train" "{}") with
                | Error e -> failtestf "a submission after rotation must be accepted; got %s" e.Message
                | Ok _ ->
                    Expect.equal
                        (List.tryLast stub.AuthHeaders)
                        (Some "Bearer token-v2")
                        "including a submit, not only a poll"

                Expect.isTrue
                    (stub.AuthHeaders |> List.contains "Bearer token-v1")
                    "CONTROL — the v1 header really was sent before the rotation, so the assertion above measures a CHANGE rather than a value that was always v2"
        }

        testCaseAsync "a configured credential that is absent from the store is a TERMINAL refusal"
        <| async {
            use stub = startStub ()

            let config =
                configFor stub
                |> HttpComputeConfig.withAuth (HttpComputeAuth.bearer "never-set")

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match! dispatcher.Submit("team-nosecret", ExternalWorkSpec.create "train" "{}") with
            | Ok handle -> failtestf "an unresolvable credential must not submit work; got %A" handle
            | Error e ->
                Expect.isFalse e.Retriable "no retry puts a secret in the store"
                Expect.stringContains e.Message "never-set" "and the diagnostic names the key to set"

            Expect.isEmpty
                stub.AuthHeaders
                "and the service was never contacted — the refusal is decided before the request leaves"
        }

        testCaseAsync "a non-standard vendor header reaches the wire"
        <| async {
            use stub = startStub ()
            let secrets = RotatingSecretStore()
            secrets.Set "_platform" "api-key" "k-123"

            let config =
                configFor stub
                |> HttpComputeConfig.withAuth (HttpComputeAuth.apiKey "X-Api-Key" "api-key")

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (secrets :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            let! result = dispatcher.Submit("team-apikey", ExternalWorkSpec.create "train" "{}")
            Expect.isTrue (Result.isOk result) "accepted"

            Expect.equal
                (List.tryLast stub.AuthHeaders)
                (Some "k-123")
                "an unknown request header needs the non-validating add — the validating overload would reject X-Api-Key outright"
        }
    ]

// ── 322.B — the submit request ─────────────────────────────────────────

let private submitTests =
    testList "322.B — Submit" [

        testCaseAsync "the spec reaches the service: kind, payload, scope, hints, timeout, idempotency key"
        <| async {
            use stub = startStub ()
            let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher

            let spec =
                ExternalWorkSpec.create "train-forecast" """{"series":"sales","horizon":12}"""
                |> ExternalWorkSpec.withHint "gpu" "1"
                |> ExternalWorkSpec.withHint "accelerator" "a100"
                |> ExternalWorkSpec.withTimeout (TimeSpan.FromMinutes 90.0)
                |> ExternalWorkSpec.withIdempotency "sales-h12-v3"

            match! dispatcher.Submit("team-body", spec) with
            | Error e -> failtestf "accepted submission expected; got %s" e.Message
            | Ok handle ->
                match stub.Unit handle.NativeRef with
                | None -> failtest "the service must hold the unit it just accepted"
                | Some unit ->
                    Expect.equal unit.Kind "train-forecast" "Kind"

                    Expect.equal
                        unit.Scope
                        (Some "team-body")
                        "the scope rides the request, so the service can partition its own records (GP 4)"

                    Expect.equal unit.Hints.["gpu"] "1" "hints arrive as a flat object"
                    Expect.equal unit.Hints.["accelerator"] "a100" "every hint, not just the first"
                    Expect.equal unit.TimeoutSeconds (Some 5400) "the advisory budget, in whole seconds"

                    Expect.equal
                        unit.Idempotency
                        (Some "sales-h12-v3")
                        "the idempotency key is forwarded to the service, which is where Phase 318 puts the dedupe"
        }

        testCaseAsync "the handle is well-formed and stamped with the configured backend label"
        <| async {
            use stub = startStub ()
            let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher

            match! dispatcher.Submit("team-handle", ExternalWorkSpec.create "render" "{}") with
            | Error e -> failtestf "accepted submission expected; got %s" e.Message
            | Ok handle ->
                Expect.notEqual handle.HandleId Guid.Empty "a real platform-minted id"

                Expect.equal
                    handle.Backend
                    "http-stub"
                    "the configured label, so a routed fleet can tell two HTTP backends apart"

                Expect.equal handle.ScopeId "team-handle" "the submitting scope"
                Expect.stringStarts handle.NativeRef "job-" "the service's own token, verbatim"
                Expect.equal handle.SubmittedAt.Kind DateTimeKind.Utc "UTC, not a local time mislabelled as one"
        }

        testCaseAsync "a payload that is not JSON is a terminal caller error, named"
        <| async {
            use stub = startStub ()
            let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher

            match! dispatcher.Submit("team-badpayload", ExternalWorkSpec.create "train" "not json at all") with
            | Ok handle -> failtestf "a malformed payload must not be shipped; got %A" handle
            | Error e ->
                Expect.isFalse e.Retriable "re-sending the identical malformed payload cannot help"

                Expect.stringContains
                    e.Message
                    "PayloadAsRawJson"
                    "and the diagnostic names the knob that decides how a payload is carried"
        }

        testCaseAsync "PayloadAsRawJson = false carries an arbitrary payload as an opaque string"
        <| async {
            use stub = startStub ()

            let config = {
                configFor stub with
                    Submit = {
                        HttpComputeSubmitFields.defaults with
                            PayloadAsRawJson = false
                    }
            }

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            let! result = dispatcher.Submit("team-opaque", ExternalWorkSpec.create "train" "not json at all")

            Expect.isTrue
                (Result.isOk result)
                "the escape hatch for a service that wants a blob — and evidence the refusal above is about the DECLARED mode, not about the payload"
        }

        testCaseAsync "an accepted submission whose job id is unreadable is TERMINAL, and says why"
        <| async {
            use stub = startStub ()
            stub.SetOmitJobId true
            let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher

            match! dispatcher.Submit("team-nojobid", ExternalWorkSpec.create "train" "{}") with
            | Ok handle -> failtestf "a handle with no native ref addresses nothing; got %A" handle
            | Error e ->
                Expect.isFalse
                    e.Retriable
                    "the work may well be RUNNING — a retriable flag here would start a second unit while the first ran on unobserved"

                Expect.stringContains e.Message "job.id" "and the diagnostic names the selector to fix"
        }

        testCaseAsync "a service that dedupes an idempotency key returns one unit — under two handle ids"
        <| async {
            // The honest picture behind `HonoursIdempotency = false`: the
            // SERVICE can dedupe, and the native ref proves it did, but the
            // handle id is minted per Submit so the platform-side
            // memoization decorator remains the portable answer.
            use stub = startStub ()
            stub.SetDedupeIdempotency true
            let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher

            let spec =
                ExternalWorkSpec.create "train" "{}"
                |> ExternalWorkSpec.withIdempotency "one-key"

            let! first = dispatcher.Submit("team-dedupe", spec)
            let! second = dispatcher.Submit("team-dedupe", spec)

            match first, second with
            | Ok a, Ok b ->
                Expect.equal b.NativeRef a.NativeRef "the SERVICE deduped: one unit exists"

                Expect.notEqual
                    b.HandleId
                    a.HandleId
                    "and the handle id is still minted per Submit, which is exactly why this dispatcher declares HonoursIdempotency = false rather than claiming the guarantee"
            | _ -> failtestf "both submissions must be accepted; got %A / %A" first second
        }
    ]

// ── 322.B / 322.C — error classification ───────────────────────────────

let private classificationTests =
    let submitAgainst (stub: StubComputeService) = async {
        let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher
        return! dispatcher.Submit("team-classify", ExternalWorkSpec.create "train" "{}")
    }

    testList "322.B — transport / status classification IS the retry contract" [

        testCaseAsync "5xx, 408 and 429 are retriable; other non-2xx are terminal"
        <| async {
            use stub = startStub ()

            let expectations = [
                500, true, "a server error is the service's own admission that this is its problem"
                503, true, "unavailable now says nothing about later"
                408, true, "Request Timeout literally means ask again"
                429, true, "rate-limited work must not be abandoned exactly when a queue is deepest"
                400, false, "a bad request is a statement about the request"
                403, false, "forbidden is not transient"
                404, false, "the endpoint is not there"
                422, false, "an unprocessable entity stays unprocessable"
            ]

            for code, retriable, why in expectations do
                stub.ForceSubmitStatus(Some code)
                let! result = submitAgainst stub

                match result with
                | Ok handle -> failtestf "HTTP %d must not mint a handle; got %A" code handle
                | Error e ->
                    Expect.equal e.Retriable retriable (sprintf "HTTP %d: %s" code why)
                    Expect.stringContains e.Message (string code) "and the diagnostic carries the status code"

            stub.ForceSubmitStatus None
            let! recovered = submitAgainst stub

            Expect.isTrue
                (Result.isOk recovered)
                "CONTROL — the same stub accepts work once the forced status is cleared, so the failures above are the forced codes and not a broken fixture"
        }

        testCaseAsync "a request that outlives its budget is retriable, and names the budget"
        <| async {
            use stub = startStub ()
            stub.SetSubmitDelay(TimeSpan.FromSeconds 2.0)

            let config =
                configFor stub
                |> HttpComputeConfig.withRequestTimeout (TimeSpan.FromMilliseconds 150.0)

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match! dispatcher.Submit("team-timeout", ExternalWorkSpec.create "train" "{}") with
            | Ok handle -> failtestf "an unanswered request must not mint a handle; got %A" handle
            | Error e ->
                Expect.isTrue
                    e.Retriable
                    "the request was never answered, so nothing was learned about whether the work is viable"

                Expect.stringContains e.Message "budget" "and the diagnostic names what expired"
        }

        testCaseAsync "an unreachable service is a retriable transport failure, not an exception"
        <| async {
            // A port nothing is listening on. Deliberately not a
            // hostname: DNS failure and connection refusal are different
            // paths and this pins the one a real outage takes.
            let config =
                HttpComputeConfig.create
                    "unreachable"
                    "http://127.0.0.1:1/jobs"
                    ("http://127.0.0.1:1/jobs/" + HttpComputeConfig.JobIdPlaceholder)
                    (JsonPath.ofString "id")
                    (JsonPath.ofString "status")
                |> HttpComputeConfig.withRequestTimeout (TimeSpan.FromSeconds 2.0)

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match! dispatcher.Submit("team-unreachable", ExternalWorkSpec.create "train" "{}") with
            | Ok handle -> failtestf "nothing was reached; got %A" handle
            | Error e -> Expect.isTrue e.Retriable "an unreachable service may be reachable in a minute"

            // And Poll, which has no error channel, must express the same
            // thing as an outcome rather than throwing.
            let! outcome =
                dispatcher.Poll {
                    HandleId = Guid.NewGuid()
                    Backend = "unreachable"
                    ScopeId = "team-unreachable"
                    NativeRef = "job-1"
                    SubmittedAt = DateTime.UtcNow
                }

            match outcome with
            | ExternalOutcome.Failed e ->
                Expect.isTrue e.Retriable "carried as data, so the scheduler decides"

                Expect.isTrue
                    (ExternalOutcome.isTerminal outcome)
                    "terminal in SHAPE so the poller stops — answering Running would keep a dead handle alive forever"
            | other ->
                failtestf
                    "a transport failure must never be reported as a fabricated non-terminal or Cancelled state; got %A"
                    other
        }
    ]

// ── 322.C — Poll ───────────────────────────────────────────────────────

let private pollTests =
    let submitted (stub: StubComputeService) = async {
        let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher

        match! dispatcher.Submit("team-poll", ExternalWorkSpec.create "train" "{}") with
        | Ok handle -> return dispatcher, handle
        | Error e -> return failtestf "submission must be accepted; got %s" e.Message
    }

    testList "322.C — Poll maps the status response onto ExternalOutcome" [

        testCaseAsync "a fractional progress value surfaces as Running, scaled off the configured percentage"
        <| async {
            use stub = startStub ()
            let! dispatcher, handle = submitted stub

            do! stub.Drive handle.NativeRef (ExternalOutcome.Running(Some 0.4))

            match! dispatcher.Poll handle with
            | ExternalOutcome.Running(Some fraction) ->
                Expect.floatClose
                    Accuracy.high
                    fraction
                    0.4
                    "the service reports 40 (a percentage) and ProgressScale = 100 turns it into 0.4 — inferring from magnitude would read a genuine 0.4% as 40%"
            | other -> failtestf "expected Running (Some 0.4); got %A" other
        }

        testCaseAsync "a service that reports no progress yields Running None rather than a fabricated figure"
        <| async {
            use stub = startStub ()
            let! dispatcher, handle = submitted stub

            do! stub.Drive handle.NativeRef (ExternalOutcome.Running None)

            match! dispatcher.Poll handle with
            | ExternalOutcome.Running None -> ()
            | other -> failtestf "expected Running None (GP 12 rule 6); got %A" other
        }

        testCaseAsync "a nonsensical progress value is dropped to None, not clamped into a lie"
        <| async {
            use stub = startStub ()
            let! dispatcher, handle = submitted stub

            do! stub.Drive handle.NativeRef (ExternalOutcome.Running(Some -0.5))

            match! dispatcher.Poll handle with
            | ExternalOutcome.Running None -> ()
            | other -> failtestf "a negative fraction is not progress; expected Running None, got %A" other

            // Above 1.0 IS clamped rather than dropped: a service saying
            // "101%" is reporting completion badly, not reporting
            // nothing.
            do! stub.Drive handle.NativeRef (ExternalOutcome.Running(Some 1.5))

            match! dispatcher.Poll handle with
            | ExternalOutcome.Running(Some fraction) -> Expect.equal fraction 1.0 "clamped to the top of the range"
            | other -> failtestf "expected Running (Some 1.0); got %A" other
        }

        testCaseAsync "a status label the config does not declare is a TERMINAL failure naming the label"
        <| async {
            use stub = startStub ()
            let! dispatcher, handle = submitted stub

            stub.SetRawStatus handle.NativeRef "WORKING" None

            match! dispatcher.Poll handle with
            | ExternalOutcome.Failed e ->
                Expect.isFalse e.Retriable "an undeclared label is configuration, not a transient fault"
                Expect.stringContains e.Message "WORKING" "and the diagnostic names the label to declare"
                Expect.stringContains e.Message "StatusValues" "and where to declare it"
            | other ->
                failtestf
                    "an unclassifiable status must never be assumed to mean success, failure, or still-running; got %A"
                    other
        }

        testCaseAsync "a success with no readable result ref is a TERMINAL failure, not a resolved hand-off"
        <| async {
            use stub = startStub ()
            let! dispatcher, handle = submitted stub

            stub.SetRawStatus handle.NativeRef "succeeded" None

            match! dispatcher.Poll handle with
            | ExternalOutcome.Failed e ->
                Expect.isFalse e.Retriable "terminal"

                Expect.stringContains
                    e.Message
                    "result ref"
                    "the caller's whole reason for polling is to learn where the result is"
            | other -> failtestf "expected Failed; got %A" other

            stub.SetRawStatus handle.NativeRef "succeeded" (Some "blob://out")

            match! dispatcher.Poll handle with
            | ExternalOutcome.Succeeded reference ->
                Expect.equal
                    reference
                    "blob://out"
                    "CONTROL — the identical status label DOES succeed once a result ref is readable, so the refusal above is about the missing ref"
            | other -> failtestf "expected Succeeded; got %A" other
        }

        testCaseAsync "a unit the service has forgotten is Failed TERMINAL, never a fabricated Cancelled"
        <| async {
            use stub = startStub ()
            let dispatcher = dispatcherFor stub :> IExternalComputeDispatcher

            let unknown = {
                HandleId = Guid.NewGuid()
                Backend = "http-stub"
                ScopeId = "team-unknown"
                NativeRef = "job-never-existed"
                SubmittedAt = DateTime.UtcNow.AddHours -3.0
            }

            match! dispatcher.Poll unknown with
            | ExternalOutcome.Failed e ->
                Expect.isFalse e.Retriable "the service has forgotten or expired it; asking again cannot recover it"
                Expect.stringContains e.Message "404" "and the diagnostic says what the service answered"
            | other -> failtestf "Phase 318 forbids inventing a terminal state here; got %A" other
        }

        testCaseAsync "a status body that is not JSON is a terminal, named failure — never a throw"
        <| async {
            // A 200 carrying HTML: a proxy error page, a login redirect, an
            // ingress default backend. The shape a deployment actually
            // hits, and the one a naive parser turns into an unhandled
            // exception on the scheduler's poll tick.
            use stub = startStub ()

            let config = {
                configFor stub with
                    StatusUrlTemplate = stub.BaseUrl + "/notjson/" + HttpComputeConfig.JobIdPlaceholder
            }

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match! dispatcher.Submit("team-html", ExternalWorkSpec.create "train" "{}") with
            | Error e -> failtestf "submission must be accepted; got %s" e.Message
            | Ok handle ->
                match! dispatcher.Poll handle with
                | ExternalOutcome.Failed e ->
                    Expect.isFalse e.Retriable "an answer that is not the protocol is configuration, not congestion"
                    Expect.stringContains e.Message "not JSON" "and the diagnostic says so plainly"
                | other -> failtestf "expected a typed Failed rather than a throw; got %A" other
        }
    ]

// ── 322.D — Cancel ─────────────────────────────────────────────────────

let private cancelTests =
    testList "322.D — Cancel" [

        testCaseAsync "a service with no cancel endpoint is a logged no-op, not a throw"
        <| async {
            use stub = startStub ()
            let logger = RecordingLogger()

            let config = { configFor stub with Cancel = None }

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (logger :> ILogger)

            match! dispatcher.Submit("team-nocancel", ExternalWorkSpec.create "train" "{}") with
            | Error e -> failtestf "submission must be accepted; got %s" e.Message
            | Ok handle ->
                // Must not throw, and must stay idempotent.
                do! dispatcher.Cancel handle
                do! dispatcher.Cancel handle

                Expect.isTrue
                    (logger.Contains "cancel_unsupported")
                    "a silent no-op would leave an operator wondering why a cancel did nothing"

                match! dispatcher.Poll handle with
                | ExternalOutcome.Cancelled ->
                    failtest "no cancel request was issued, so the unit must NOT have become Cancelled"
                | _ -> ()
        }

        testCaseAsync "cancelling a unit the service has forgotten is a no-op, not an error"
        <| async {
            use stub = startStub ()
            let logger = RecordingLogger()

            let dispatcher =
                HttpComputeDispatcher.create
                    (configFor stub)
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (logger :> ILogger)

            do!
                dispatcher.Cancel {
                    HandleId = Guid.NewGuid()
                    Backend = "http-stub"
                    ScopeId = "team-gone"
                    NativeRef = "job-never-existed"
                    SubmittedAt = DateTime.UtcNow
                }

            Expect.isTrue
                (logger.Contains "cancel_unknown_unit")
                "a `finally` that cancels a unit the service has already forgotten must not fault"
        }

        testCaseAsync "a cancel the service rejects is logged, and does not throw across the boundary"
        <| async {
            use stub = startStub ()
            let logger = RecordingLogger()

            // A cancel URL pointing at a route the stub does not serve
            // with POST — the service answers, and answers no.
            let config = {
                configFor stub with
                    Cancel =
                        Some {
                            UrlTemplate = stub.BaseUrl + "/healthz?job=" + HttpComputeConfig.JobIdPlaceholder
                            Method = "POST"
                        }
            }

            let dispatcher =
                HttpComputeDispatcher.create
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (logger :> ILogger)

            do!
                dispatcher.Cancel {
                    HandleId = Guid.NewGuid()
                    Backend = "http-stub"
                    ScopeId = "team-reject"
                    NativeRef = "job-1"
                    SubmittedAt = DateTime.UtcNow
                }

            Expect.isTrue
                (logger.Lines |> List.exists (fun line -> line.Contains "cancel_"))
                "Cancel returns unit by contract, so a rejection is logged and the caller confirms through Poll"
        }
    ]

// ── 322.B + 320 — push completion, end to end ──────────────────────────

let private callbackTests =
    testList "322.B / Phase 320 — push completion over a real socket" [

        testCaseAsync "the submit request carries the callback URL, and the credential arrives separately"
        <| async {
            use stub = startStub ()

            use ingress =
                startIngress (InMemoryExternalHandleStore() :> IExternalHandleStore) (RecordingCompletionSink())

            let config =
                configFor stub
                |> HttpComputeConfig.withCallback {
                    PublicBaseUrl = ingress.BaseUrl
                    RegistrationUrlTemplate = stub.BaseUrl + "/jobs/" + HttpComputeConfig.JobIdPlaceholder + "/webhook"
                    RegistrationMethod = "POST"
                    UrlField = "callbackUrl"
                    SecretField = "callbackSecret"
                    HandleIdField = Some "handleId"
                }

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match!
                (dispatcher :> IExternalComputeDispatcher).Submit("team-push", ExternalWorkSpec.create "train" "{}")
            with
            | Error e -> failtestf "submission must be accepted; got %s" e.Message
            | Ok handle ->
                match stub.Unit handle.NativeRef with
                | None -> failtest "the service must hold the accepted unit"
                | Some unit ->
                    Expect.equal
                        unit.SubmitCallbackUrl
                        (Some(ingress.BaseUrl.TrimEnd '/' + ExternalCallback.Route))
                        "the callback URL is deployment-static, so it rides the submit request"

                Expect.isNone
                    (stub.Webhook handle.NativeRef)
                    "and the SECRET has not been delivered yet — it does not exist until the platform has registered the handle this request just returned"

                // The platform's own step, exactly as the scheduler does
                // it: register, then hand the credential over.
                let secret, _hash = ExternalCallbackSecret.mint ()

                do!
                    (dispatcher :> IExternalCallbackCapableBackend)
                        .AcceptCallbackCredential(
                            handle,
                            {
                                HandleId = handle.HandleId
                                Secret = secret
                                CallbackPath = ExternalCallback.Route
                            }
                        )

                match stub.Webhook handle.NativeRef with
                | None -> failtest "the credential must reach the service, or its webhook cannot authenticate itself"
                | Some hook ->
                    Expect.equal
                        hook.Url
                        (ingress.BaseUrl.TrimEnd '/' + ExternalCallback.Route)
                        "the URL is built from the credential's own CallbackPath, so a deployment mounted under a prefix stays correct"

                    Expect.equal hook.Secret secret "the cleartext secret, delivered once"
                    Expect.equal hook.HandleId (Some(string handle.HandleId)) "and the handle id the ingress routes on"
        }

        testCaseAsync "a callback from the service resolves the run push-style"
        <| async {
            use stub = startStub ()
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let sink = RecordingCompletionSink()
            use ingress = startIngress store sink

            let config =
                configFor stub
                |> HttpComputeConfig.withCallback {
                    PublicBaseUrl = ingress.BaseUrl
                    RegistrationUrlTemplate = stub.BaseUrl + "/jobs/" + HttpComputeConfig.JobIdPlaceholder + "/webhook"
                    RegistrationMethod = "POST"
                    UrlField = "callbackUrl"
                    SecretField = "callbackSecret"
                    HandleIdField = Some "handleId"
                }

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match!
                (dispatcher :> IExternalComputeDispatcher).Submit("team-push", ExternalWorkSpec.create "train" "{}")
            with
            | Error e -> failtestf "submission must be accepted; got %s" e.Message
            | Ok handle ->
                let runId = Guid.NewGuid()
                let secret, hash = ExternalCallbackSecret.mint ()
                do! store.Register(handle, runId, hash)

                do!
                    (dispatcher :> IExternalCallbackCapableBackend)
                        .AcceptCallbackCredential(
                            handle,
                            {
                                HandleId = handle.HandleId
                                Secret = secret
                                CallbackPath = ExternalCallback.Route
                            }
                        )

                match stub.Webhook handle.NativeRef with
                | None -> failtest "the credential must have reached the service"
                | Some hook ->
                    // The service finishes and calls back — a real POST
                    // over a real socket to the real ingress.
                    let! status = stub.SendCallback hook "succeeded" (Some "blob://results/pushed.parquet")
                    Expect.equal status 200 "the ingress accepted the authenticated callback"

                    match sink.Calls with
                    | [ (resolvedHandleId, resolvedRunId, outcome) ] ->
                        Expect.equal resolvedHandleId handle.HandleId "the run this handle belongs to"
                        Expect.equal resolvedRunId runId "routed via the platform's own stored record"

                        Expect.equal
                            outcome
                            (ExternalOutcome.Succeeded "blob://results/pushed.parquet")
                            "with the result ref the service reported — resolved with no poll latency"
                    | other -> failtestf "expected exactly one resolution; got %A" other
        }

        testCaseAsync "CONTROL — a forged secret does not resolve the run"
        <| async {
            // The push path above must not be passing because the ingress
            // accepts anything.
            use stub = startStub ()
            let store = InMemoryExternalHandleStore() :> IExternalHandleStore
            let sink = RecordingCompletionSink()
            use ingress = startIngress store sink

            let handle = {
                HandleId = Guid.NewGuid()
                Backend = "http-stub"
                ScopeId = "team-forge"
                NativeRef = "job-forged"
                SubmittedAt = DateTime.UtcNow
            }

            let _, hash = ExternalCallbackSecret.mint ()
            do! store.Register(handle, Guid.NewGuid(), hash)

            let! status =
                stub.SendCallback
                    {
                        Url = ingress.BaseUrl.TrimEnd '/' + ExternalCallback.Route
                        Secret = "not-the-secret"
                        HandleId = Some(string handle.HandleId)
                    }
                    "succeeded"
                    (Some "blob://forged")

            Expect.notEqual status 200 "a callback presenting the wrong secret is refused"
            Expect.isEmpty sink.Calls "and nothing was resolved"
        }

        testCaseAsync "a service the credential cannot be delivered to still runs — the cost is latency, never a job"
        <| async {
            use stub = startStub ()
            let logger = RecordingLogger()

            let config =
                configFor stub
                |> HttpComputeConfig.withCallback {
                    // A registration endpoint nothing is listening on.
                    PublicBaseUrl = "http://127.0.0.1:1"
                    RegistrationUrlTemplate = "http://127.0.0.1:1/jobs/" + HttpComputeConfig.JobIdPlaceholder + "/hook"
                    RegistrationMethod = "POST"
                    UrlField = "callbackUrl"
                    SecretField = "callbackSecret"
                    HandleIdField = Some "handleId"
                }
                |> HttpComputeConfig.withRequestTimeout (TimeSpan.FromSeconds 2.0)

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    config
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (logger :> ILogger)

            match!
                (dispatcher :> IExternalComputeDispatcher).Submit("team-nohook", ExternalWorkSpec.create "train" "{}")
            with
            | Error e -> failtestf "submission must be accepted; got %s" e.Message
            | Ok handle ->
                // MUST NOT throw: the work is accepted and the run is
                // durably AwaitingExternal, so failing here would trade a
                // latency regression for a lost job.
                do!
                    (dispatcher :> IExternalCallbackCapableBackend)
                        .AcceptCallbackCredential(
                            handle,
                            {
                                HandleId = handle.HandleId
                                Secret = "s3cret"
                                CallbackPath = ExternalCallback.Route
                            }
                        )

                Expect.isTrue
                    (logger.Contains "callback_registration_failed")
                    "the failure is logged and named, and says the run resolves by poll"

                Expect.isFalse
                    (logger.Lines |> List.exists (fun line -> line.Contains "s3cret"))
                    "and NO log line carries the secret — AcceptCallbackCredential's one hard prohibition"

                match! (dispatcher :> IExternalComputeDispatcher).Poll handle with
                | ExternalOutcome.Pending
                | ExternalOutcome.Running _ -> ()
                | other -> failtestf "the unit is unaffected by an undelivered credential; got %A" other
        }
    ]

// ── the probes ─────────────────────────────────────────────────────────

let private probeTests =
    testList "322.F — the readiness probe and the startup preflight" [

        testCaseAsync "the probe is Healthy against a reachable service and Degraded against an unreachable one"
        <| async {
            use stub = startStub ()

            match HttpComputeHealth.tryHealthCheck (dispatcherFor stub) with
            | None -> failtest "a config naming a health URL must produce a probe"
            | Some probe ->
                Expect.equal probe.Name "external_compute:http-stub" "named per backend, so two are distinguishable"
                Expect.equal probe.Kind Readiness "restarting this replica does not fix an unreachable compute service"

                match! probe.Check() with
                | Healthy -> ()
                | other -> failtestf "expected Healthy against a live service; got %A" other

            let unreachable =
                HttpComputeDispatcher.createTyped
                    ({
                        configFor stub with
                            HealthUrl = Some "http://127.0.0.1:1/healthz"
                     }
                     |> HttpComputeConfig.withRequestTimeout (TimeSpan.FromSeconds 2.0))
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match HttpComputeHealth.tryHealthCheck unreachable with
            | None -> failtest "a probe was expected"
            | Some probe ->
                match! probe.Check() with
                | Degraded message ->
                    Expect.stringContains
                        message
                        "in-process request handling"
                        "Degraded, not Unhealthy: external compute is a hand-off destination, so draining the whole rotation would turn a partial outage into a total one"
                | other -> failtestf "expected Degraded; got %A" other
        }

        testCaseAsync "the preflight fails on a credential that is not in the store — the common deployment miss"
        <| async {
            use stub = startStub ()
            let secrets = RotatingSecretStore()

            let config =
                configFor stub
                |> HttpComputeConfig.withAuth (HttpComputeAuth.bearer "compute-token")

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    config
                    (secrets :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            let validator =
                HttpComputeHealth.configValidator dispatcher (secrets :> ISecretStore)

            match! validator.Validate() with
            | ConfigValidation.ValidationResult.Error message ->
                Expect.stringContains message "compute-token" "the diagnostic names the key that is missing"
            | other ->
                failtestf
                    "an absent credential must fail preflight — otherwise it surfaces as a 401 on the first submission hours later; got %A"
                    other

            // CONTROL — the same validator passes once the secret exists,
            // so the failure above is about the secret and not about the
            // validator always failing.
            secrets.Set "_platform" "compute-token" "token-v1"

            match! validator.Validate() with
            | ConfigValidation.ValidationResult.Ok -> ()
            | other -> failtestf "CONTROL: expected Ok once the credential is present; got %A" other
        }

        testCaseAsync "the preflight WARNS rather than aborting startup when the service is merely unreachable"
        <| async {
            use stub = startStub ()

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    ({
                        configFor stub with
                            HealthUrl = Some "http://127.0.0.1:1/healthz"
                     }
                     |> HttpComputeConfig.withRequestTimeout (TimeSpan.FromSeconds 2.0))
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match!
                (HttpComputeHealth.configValidator dispatcher (RotatingSecretStore() :> ISecretStore)).Validate()
            with
            | ConfigValidation.ValidationResult.Warning _ -> ()
            | other ->
                failtestf
                    "aborting startup here would let a briefly-down compute service take the whole deployment with it; got %A"
                    other
        }

        testCaseAsync "no health URL is a Warning: nothing would notice the service going away"
        <| async {
            use stub = startStub ()

            let dispatcher =
                HttpComputeDispatcher.createTyped
                    { configFor stub with HealthUrl = None }
                    (RotatingSecretStore() :> ISecretStore)
                    sharedHttpClient.Value
                    (RecordingLogger() :> ILogger)

            match!
                (HttpComputeHealth.configValidator dispatcher (RotatingSecretStore() :> ISecretStore)).Validate()
            with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "HealthUrl" "and says what to configure"
            | other -> failtestf "expected a Warning; got %A" other
        }
    ]

/// Every Phase 322 list, in reading order.
let tests =
    testList "ToolUp.ExternalCompute.Http — the generic HTTP/REST dispatcher (Phase 322)" [
        selectorTests
        statusMapTests
        configTests
        composeTests
        authTests
        submitTests
        classificationTests
        pollTests
        cancelTests
        callbackTests
        probeTests
        conformanceTests
    ]
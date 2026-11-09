namespace ToolUp.Remoting.Giraffe

open Microsoft.AspNetCore.Http

open Giraffe
open System.IO
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Proxy
open System.Threading.Tasks

/// The batch route's wire constants: the `error` code of a refusal and the
/// `HttpContext.Items` key that marks a request as a batch element.
module internal BatchElement =
    [<Literal>]
    let ItemsKey = "ToolUp.Remoting.BatchElement"

    [<Literal>]
    let RefusalCode = "remoting_batch_refused"

/// The routes of every contract this process has mounted that a batch
/// envelope may never name (`Contract.refusedBatchRoutes`). Recorded when a
/// dispatcher is MOUNTED, never while a contract or table is built: the batch
/// middleware sits in front of every API the process mounts, and a route is
/// non-batchable by its shape, which the mounted contract states.
module internal MountedContracts =
    let private refused =
        System.Collections.Concurrent.ConcurrentDictionary<string, unit>(System.StringComparer.OrdinalIgnoreCase)

    let mount (contract: ApiContract) =
        for route in Contract.refusedBatchRoutes contract do
            refused.TryAdd(route, ()) |> ignore

    let isRefused (route: string) = refused.ContainsKey route

// Assembly-private: the adapter's own build/dispatch plumbing, reached by consumers
// only through `Remoting.buildHttpHandler`. Tests see it through InternalsVisibleTo.
module internal GiraffeUtil =
    let setJsonBody (backend: JsonSerializerBackend) (response: obj) (logger: Option<string -> unit>) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            // TIDY-UP "ToolUp.Remoting per-request cleanups" (F6) — direct-
            // write JSON into the response stream instead of the legacy
            // MemoryStream → byte[] → string → write triple-allocation. STJ
            // serialises straight into the stream; the byte-pin tests in the
            // Remoting suite confirm the wire shape is byte-identical to
            // pre-TIDY-UP. Diagnostics breadcrumb still emits the response
            // body when a logger is composed — only difference is the path:
            // serialise to a small temporary stream for the breadcrumb when
            // a logger is present (cold debug path); otherwise the response
            // stream IS the only materialisation.
            match logger with
            | Some _ ->
                use ms = new MemoryStream()
                jsonSerializeWithBackend backend response ms
                let responseBody = System.Text.Encoding.UTF8.GetString(ms.ToArray())
                Diagnostics.outputPhase logger responseBody
                ctx.Response.ContentType <- "application/json; charset=utf-8"
                return! setBodyFromString responseBody next ctx
            | None ->
                ctx.Response.ContentType <- "application/json; charset=utf-8"
                // Use async overload. Kestrel runs with
                // `AllowSynchronousIO=false` by default, which rejects the
                // sync `JsonSerializer.Serialize(stream,...)` overload
                // when `stream` is `HttpResponse.Body` with
                // `InvalidOperationException: Synchronous operations are
                // disallowed.` Bug introduced by F6 direct-write; this
                // fix preserves the direct-write perf win.
                do! jsonSerializeWithBackendAsync backend response ctx.Response.Body
                return! next ctx
        }

    /// Handles thrown exceptions
    let fail (ex: exn) (routeInfo: RouteInfo<HttpContext>) (options: RemotingOptions<HttpContext, 't>) : HttpHandler =
        let logger = options.DiagnosticsLogger
        let backend = options.JsonSerializer

        fun (next: HttpFunc) (ctx: HttpContext) ->
            match options.ErrorHandler with
            | None -> setJsonBody backend (Errors.unhandled routeInfo.methodName) logger next ctx
            | Some errorHandler ->
                match errorHandler ex routeInfo with
                | Ignore -> setJsonBody backend (Errors.ignored routeInfo.methodName) logger next ctx
                | Propagate error -> setJsonBody backend (Errors.propagated error) logger next ctx
                | PropagateCategorised(category, error) ->
                    // Emit the additive categorised envelope.
                    // Wire shape grows a `category` field; clients reading
                    // only `error` see the original body unchanged.
                    setJsonBody
                        backend
                        (Errors.categorisedWithSchema options.SchemaVersion category error)
                        logger
                        next
                        ctx

    /// Used to halt the forwarding of the Http context
    let halt: HttpContext option = None

    let notFound (options: RemotingOptions<HttpContext, 'impl>) next (ctx: HttpContext) =
        match ctx.Request.Method.ToUpper(), options.Docs with
        | "GET", (Some docsUrl, Some docs) when docsUrl = ctx.Request.Path.Value ->
            let (Documentation(docsName, docsRoutes)) = docs

            let schema =
                Docs.makeDocsSchemaWithSchemaVersions typeof<'impl> docs options.RouteBuilder options.SchemaVersion

            let docsApp = DocsApp.embedded docsName docsUrl schema
            htmlString docsApp next ctx
        | "OPTIONS", (Some docsUrl, Some docs) when
            sprintf "/%s/$schema" docsUrl = ctx.Request.Path.Value
            || sprintf "%s/$schema" docsUrl = ctx.Request.Path.Value
            ->
            let schema =
                Docs.makeDocsSchemaWithSchemaVersions typeof<'impl> docs options.RouteBuilder options.SchemaVersion

            let serializedSchema = schema.ToJsonString()
            text serializedSchema next ctx
        | _ -> Task.FromResult halt

    /// Body normalisation built-in.
    ///
    /// The Fable.SimpleJson client may emit an empty / `null` / `""` body
    /// for `unit -> Async<'T>` methods depending on which network primitive
    /// it uses. Rewrite those to `[]` before the dispatcher reads the body,
    /// and promote GET to POST so the POST-shaped invocation path applies.
    /// Triggered by the `x-remoting-proxy` header so non-Remoting clients
    /// hitting the same route aren't perturbed.
    ///
    /// Idempotent: a request that's already `[]` flows through unchanged
    /// (the body-content predicate doesn't match `[]`).
    let private emptyArrayJsonBytes = System.Text.Encoding.UTF8.GetBytes "[]"

    let private normaliseRemotingBody (ctx: HttpContext) : System.Threading.Tasks.Task =
        task {
            // TIDY-UP "ToolUp.Remoting per-request cleanups" (F7) — the
            // outer non-streaming branch (line ~443 below) already calls
            // `ctx.Request.EnableBuffering()` unconditionally before
            // invoking this normaliser. Calling it a second time here is
            // idempotent (one bool check on `HttpRequest.Body.CanSeek`),
            // but the prior double-call read as if buffering happened
            // independently. Single call now; the outer call is the
            // single source of truth.
            use reader =
                new System.IO.StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8, leaveOpen = true)

            let! body = reader.ReadToEndAsync()
            ctx.Request.Body.Position <- 0L
            let trimmed = body.Trim()

            if trimmed = "" || trimmed = "\"\"" || trimmed = "null" then
                ctx.Request.Body <- new System.IO.MemoryStream(emptyArrayJsonBytes)
                ctx.Request.ContentLength <- System.Nullable(int64 emptyArrayJsonBytes.Length)

                if ctx.Request.Method = "GET" then
                    ctx.Request.Method <- "POST"
        }
        :> System.Threading.Tasks.Task

    /// The contract a composition dispatches over — the registered generated
    /// table's, or the record read once — bound to the composition's flags.
    /// A composition that makes a refusal of the record refuses to start,
    /// naming every refusal.
    let contractFor<'impl> (options: RemotingOptions<HttpContext, 'impl>) : ApiContract =
        match Contract.bind (ContractFlags.ofOptions options) (contractOf<'impl> ()) with
        | Error refusals -> invalidOp (ContractRefusal.render refusals)
        | Ok contract -> contract

    /// Build the dispatcher table ONCE for a contract. The proxy and every
    /// per-method lookup the request path reads are projections of the
    /// contract, built here, not per request. The returned function accepts
    /// a per-request `implBuilder` and yields the `HttpHandler` that
    /// dispatches a single request against that builder. Pure: building a
    /// table writes nothing process-wide.
    let buildDispatcherTableFor<'impl> (contract: ApiContract) (options: RemotingOptions<HttpContext, 'impl>) =
        // The proxy with its argument parse exposed, so the validation
        // pre-flight decodes the first argument through the dispatch path's
        // own decode and hands the result on: one parse, one decode, and
        // validation sees the value the handler receives. A record whose
        // generated table is registered is served through its generated
        // invocations INSIDE this proxy, so every stage below runs around a
        // generated method exactly as around a reflective one.
        let apiProxy = makeApiProxyWithParse options
        let rmsManager = getRecyclableMemoryStreamManager options

        let byMethod (slice: MethodContract -> 'a option) : Map<string, 'a> =
            contract.Methods
            |> List.choose (fun m -> slice m |> Option.map (fun value -> m.Name, value))
            |> Map.ofList

        // A method with no classification is absent: under a resolver the
        // contract has refused it, and without one nothing is evaluated.
        let classifications =
            byMethod (fun m -> m.Classification |> Option.map AuthClassifier.ofClassified)

        let rateLimits = byMethod (fun m -> Some(m.Budgets |> List.map RateLimit.ofBudget))

        let audits = byMethod (fun m -> m.Audit |> Option.map _.Kind)

        // The audited method's input record, so the payload is built even when
        // the input carries no validation attribute.
        let auditInputTypes = byMethod (fun m -> m.Audit |> Option.bind _.Input)

        let idempotentMethods =
            contract.Methods |> List.filter _.Idempotent |> List.map _.Name |> Set.ofList

        let validationInputTypes = byMethod _.Validated

        let streamingShapes =
            byMethod (fun m ->
                match m.Shape with
                | MethodShape.Streaming element -> Some(List.head m.Arguments, element)
                | _ -> None)

        // Consulted ONLY when an `IJobDispatcher` is composed — without one
        // the methods dispatch exactly as before (GP 11).
        let longRunningShapes =
            byMethod (fun m ->
                match m.Shape with
                | MethodShape.LongRunning calls -> Some calls
                | _ -> None)

        let schemaVersions = contract.Schemas

        // This record's own addressable names: a request for ANOTHER API's
        // method reaches this handler first in a multi-API `choose` and must
        // fall through untouched, whatever `X-Remoting-Schema` says.
        let schemaAddressable = Contract.addressableMethods contract

        // Returned closure-of-closures: `dispatch implBuilder`
        // produces an `HttpHandler` that dispatches one request. The outer
        // `buildDispatcherTable` evaluation above (proxy + classifiers +
        // guards) is one-time per `options` value; only the innermost
        // `task { ... }` body executes per request.
        fun (implBuilder: HttpContext -> 'impl) ->
            fun (next: HttpFunc) (ctx: HttpContext) -> task {
                let isProxyHeaderPresent = ctx.Request.Headers.ContainsKey "x-remoting-proxy"

                // Correlation-id + telemetry shared between the
                // streaming and non-streaming branches. Previously the
                // streaming short-circuit ran BEFORE `CallContext.beginRequest`
                // / response-header stamping / `IRemotingTelemetry` emission,
                // making streaming methods invisible to every observability
                // seam. Hoisted up here so both branches benefit.
                let correlationId =
                    let mutable values: Microsoft.Extensions.Primitives.StringValues =
                        Microsoft.Extensions.Primitives.StringValues.Empty

                    if ctx.Request.Headers.TryGetValue("x-correlation-id", &values) then
                        let v = values.ToString()

                        if System.String.IsNullOrWhiteSpace v then
                            System.Guid.NewGuid().ToString("N")
                        else
                            v
                    else
                        System.Guid.NewGuid().ToString("N")

                use _correlationScope = CallContext.beginRequest correlationId

                ctx.Response.Headers["x-correlation-id"] <- Microsoft.Extensions.Primitives.StringValues correlationId

                // Streaming-method short-circuit.
                //
                // Methods returning IAsyncEnumerable<'T> bypass the proxy entirely
                // and emit SSE frames. The pre-flight chain (auth / validation /
                // idempotency / rate-limit / audit) doesn't apply to
                // streaming methods in v0 — those seams compose with non-streaming
                // dispatch only. Future enhancements compose them per-frame.
                //
                // Telemetry IS emitted on streaming methods (start ⇒
                // completion); audit + auth remain non-streaming-only.
                let streamingPath = Route.methodSegment ctx.Request.Path.Value

                let streamingShape = streamingShapes |> Map.tryFind streamingPath

                // Wire-schema negotiation.
                //
                // The trailing path segment IS the addressed method name;
                // `streamingPath` above computes exactly that, so it is
                // aliased here rather than recomputed — under the name the
                // negotiation reads, so neither use reads as the other's
                // concern.
                let addressedMethod = streamingPath

                let requestedSchema =
                    let mutable values: Microsoft.Extensions.Primitives.StringValues =
                        Microsoft.Extensions.Primitives.StringValues.Empty

                    if ctx.Request.Headers.TryGetValue(SchemaVersion.Header, &values) then
                        Some(values.ToString())
                    else
                        None

                let schemaRouting =
                    SchemaVersion.negotiate
                        schemaVersions
                        schemaAddressable
                        options.SchemaVersion
                        requestedSchema
                        addressedMethod

                let schemaRefusal =
                    match schemaRouting with
                    | SchemaRouting.Refused(requested, supported) -> Some(requested, supported)
                    | _ -> None

                // Companion-route resolution. `<method>/status`,
                // `<method>/progress` and `<method>/cancel` for a classified
                // long-running method, served off the composed dispatcher.
                // Resolved from the last TWO path segments, so an ordinary
                // method route (one segment after the record name) never
                // matches; `None` when no dispatcher is composed.
                let companion =
                    match options.JobDispatcher with
                    | Some dispatcher when not (Map.isEmpty longRunningShapes) ->
                        LongRunning.tryResolveCompanion longRunningShapes ctx.Request.Path.Value
                        |> Option.map (fun resolved -> dispatcher, resolved)
                    | _ -> None

                // The companion caller. Resolved through
                // the composed `AuthContextResolver` (when there is one) so
                // ownership is evaluated for THIS caller: the submitting
                // subject sees its job, a caller holding `JobAdminRole` sees
                // every job, anyone else sees not-found. Companions are never
                // `[<PublicEndpoint>]`, so the resolver always runs for them.
                let! companionCaller = task {
                    match companion, options.AuthContextResolver with
                    | Some _, Some resolver ->
                        let! authCtx = resolver ctx |> Async.StartAsTask
                        return Some authCtx
                    | _ -> return None
                }

                let companionSubject = LongRunning.subjectOf companionCaller

                let companionIsAdmin =
                    companionCaller
                    |> Option.map (fun authCtx -> authCtx.HasRole options.JobAdminRole)
                    |> Option.defaultValue false

                let stjOptions =
                    match options.JsonSerializer with
                    | SystemTextJson opts -> opts

                // The SSE branch below serves two producers through one loop:
                // a streaming method's `IAsyncEnumerable<'T>` and
                // the progress companion's `IAsyncEnumerable<JobStatus<'T>>`
                // . Each is (label, element type, body -> enumerable);
                // the label names the call in telemetry and diagnostics.
                let sseSource: (string * System.Type * (string -> obj)) option =
                    match streamingShape with
                    | Some(argType, elementType) ->
                        Some(
                            streamingPath,
                            elementType,
                            fun bodyText ->
                                let argValue =
                                    Streaming.parseFirstArg bodyText argType stjOptions
                                    |> Option.defaultWith (fun () -> System.Activator.CreateInstance argType)
                                // Resolve impl + invoke streaming method.
                                let impl = implBuilder ctx
                                Streaming.invokeMethod impl streamingPath argValue
                        )
                    | None ->
                        match companion with
                        | Some(dispatcher, (methodName, JobCompanion.Progress, calls)) ->
                            Some(
                                methodName + "/progress",
                                calls.StatusType,
                                fun bodyText ->
                                    let handle =
                                        Streaming.parseFirstArg bodyText calls.HandleType stjOptions
                                        |> Option.defaultValue calls.EmptyHandle

                                    calls.Subscribe dispatcher companionSubject companionIsAdmin handle
                            )
                        | _ -> None

                if
                    ctx.Items.ContainsKey BatchElement.ItemsKey
                    && (sseSource.IsSome
                        || companion.IsSome
                        || longRunningShapes.ContainsKey addressedMethod)
                then
                    // A streaming or long-running call reached
                    // through a batch envelope. The batch route refuses such
                    // an envelope before running anything; this is the
                    // backstop for a route it could not see, and it refuses
                    // the element (400) rather than serving a shape the
                    // envelope's `{ status, body }` answer cannot carry.
                    ctx.Response.StatusCode <- 400

                    let payload =
                        box {|
                            error = BatchElement.RefusalCode
                            methodName = addressedMethod
                            reason = "a streaming or long-running method is never batched"
                        |}

                    let envelope =
                        Errors.categorisedWithSchema options.SchemaVersion ErrorCategory.User payload

                    return! setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                elif schemaRefusal.IsSome then
                    // The caller pinned a wire schema this
                    // method does not serve. Refused BEFORE the streaming
                    // split and before the pre-flight chain: serving the
                    // wrong shape to a client that said which shape it
                    // wanted is the single failure this header exists to
                    // prevent, and it applies to streaming methods too
                    // (whose supported vector is the composed default).
                    //
                    // 400 + `ErrorCategory.User` — a caller-side mistake,
                    // carrying the discriminating detail in the payload
                    // rather than in a new closed-DU case no consumer has
                    // recompiled against (see SchemaVersion.fs).
                    let requested, supported = schemaRefusal.Value
                    ctx.Response.StatusCode <- 400

                    let payload = SchemaVersion.refusalPayload addressedMethod requested supported

                    let envelope =
                        Errors.categorisedWithSchema options.SchemaVersion ErrorCategory.User payload

                    return! setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                elif sseSource.IsSome then
                    let sseLabel, sseElementType, produce = sseSource.Value
                    // Streaming-method telemetry stopwatch.
                    // Started at SSE entry; stopped at terminal frame
                    // (complete / error). Bridges the streaming branch
                    // back into the per-method-completion contract of
                    // `IRemotingTelemetry`.
                    //
                    // Gated symmetrically with the non-streaming
                    // branch: `Api.make` + `FeatureMode.Disabled` deployments
                    // skip the Stopwatch + MethodTelemetry alloc entirely.
                    let streamingTelemetryActive =
                        options.Telemetry.IsSome
                        && (match options.TelemetryGate with
                            | Some gate -> gate ()
                            | None -> true)

                    let streamingStopwatch =
                        if streamingTelemetryActive then
                            System.Diagnostics.Stopwatch.StartNew()
                        else
                            Unchecked.defaultof<_>

                    // The chunk-count dimension. Counted at
                    // the single write site below whether or not telemetry
                    // is active (an `int` increment is cheaper than a second
                    // gate), and reported as `ChunkCount = Some n` on the
                    // one-per-call telemetry event.
                    let mutable chunkCount = 0

                    let emitStreamingTelemetry (outcome: MethodOutcome) =
                        if streamingTelemetryActive then
                            match options.Telemetry with
                            | Some sink ->
                                streamingStopwatch.Stop()

                                sink.OnMethodCompleted {
                                    MethodName = sseLabel
                                    ElapsedMs = int streamingStopwatch.ElapsedMilliseconds
                                    Outcome = outcome
                                    CorrelationId = Some correlationId
                                    ChunkCount = Some chunkCount
                                }
                            | None -> ()

                    // Wrap the streaming setup in a try/with so
                    // setup-time exceptions (null funcObj from invokeMethod,
                    // missing serializer, etc.) emit a single error frame
                    // before failing the response, rather than NRE-ing
                    // outside any handler.
                    try
                        let elementType = sseElementType
                        // Read body, parse first arg. EnableBuffering is required
                        // before any Position-rewinding read on the request body —
                        // the streaming branch runs BEFORE body-normalisation so we
                        // can't rely on its EnableBuffering call.
                        ctx.Request.EnableBuffering()

                        use reader =
                            new System.IO.StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8, leaveOpen = true)

                        let! bodyText = reader.ReadToEndAsync()

                        // The producer parses the body and yields the
                        // enumerable (a streaming method's, or the progress
                        // companion's subscription).
                        let asyncEnumerable = produce bodyText

                        // Guard against a streaming method that
                        // returns null (a legitimate-looking but broken
                        // impl). Without this, `getEnumeratorMethod.Invoke`
                        // NREs and the response goes out as a 500 with no
                        // SSE error frame.
                        if isNull asyncEnumerable then
                            invalidOp (
                                sprintf "streaming method '%s' returned null instead of an IAsyncEnumerable<_>" sseLabel
                            )

                        // SSE response setup.
                        ctx.Response.StatusCode <- 200
                        ctx.Response.ContentType <- "text/event-stream"

                        ctx.Response.Headers["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache"

                        ctx.Response.Headers["X-Accel-Buffering"] <- Microsoft.Extensions.Primitives.StringValues "no"
                        // Iterate via the closed-generic IAsyncEnumerable<'T> /
                        // IAsyncEnumerator<'T> interfaces (concrete-class
                        // GetMethod returns null when the methods are interface-
                        // implemented; the interface types name the methods
                        // unambiguously).
                        let enumerableInterface =
                            typedefof<System.Collections.Generic.IAsyncEnumerable<_>>.MakeGenericType([| elementType |])

                        let enumeratorInterface =
                            typedefof<System.Collections.Generic.IAsyncEnumerator<_>>.MakeGenericType([| elementType |])

                        let getEnumeratorMethod = enumerableInterface.GetMethod "GetAsyncEnumerator"

                        let enumerator =
                            getEnumeratorMethod.Invoke(
                                asyncEnumerable,
                                [| box System.Threading.CancellationToken.None |]
                            )

                        let moveNextMethod = enumeratorInterface.GetMethod "MoveNextAsync"
                        let currentProperty = enumeratorInterface.GetProperty "Current"
                        let disposeMethod = typeof<System.IAsyncDisposable>.GetMethod "DisposeAsync"

                        // Track terminal-frame outcome so the
                        // dispose-task path can `do!` await without losing
                        // the exception (you can't bind inside the
                        // catch arm of an enclosing try-finally).
                        let mutable streamingFailure: exn option = None

                        try
                            try
                                let mutable continueIter = true

                                while continueIter do
                                    let valueTaskBool = moveNextMethod.Invoke(enumerator, [||])
                                    // ValueTask<bool>: use AsTask().Result idiom via reflection.
                                    let asTaskMethod = valueTaskBool.GetType().GetMethod "AsTask"

                                    let taskBool =
                                        asTaskMethod.Invoke(valueTaskBool, [||]) :?> System.Threading.Tasks.Task<bool>

                                    let! moved = taskBool

                                    if moved then
                                        let element = currentProperty.GetValue enumerator
                                        // Use the captured `elementType` from the
                                        // streaming-shape classifier instead of
                                        // `element.GetType()` — the latter NREs
                                        // on null elements (a legitimate yield
                                        // for `IAsyncEnumerable<MyRecord option>`
                                        // patterns), terminating the stream on
                                        // the first None. Also avoids a
                                        // per-iteration reflection lookup.
                                        let json =
                                            System.Text.Json.JsonSerializer.Serialize(element, elementType, stjOptions)

                                        // Every chunk frame carries
                                        // `id: <correlation-id>-<chunk-index>`, so a
                                        // chunk is attributable to its call from the
                                        // wire alone (the same token the telemetry
                                        // event and the response header carry).
                                        let frame =
                                            Streaming.formatChunkWithId (sprintf "%s-%d" correlationId chunkCount) json

                                        chunkCount <- chunkCount + 1
                                        do! ctx.Response.Body.WriteAsync(frame, 0, frame.Length)
                                        do! ctx.Response.Body.FlushAsync()
                                    else
                                        continueIter <- false

                                let completeFrame = Streaming.formatComplete ()
                                do! ctx.Response.Body.WriteAsync(completeFrame, 0, completeFrame.Length)
                                // Flush the terminal frame so EventSource clients
                                // observe the `complete` event even on
                                // connection-reuse paths where .NET may otherwise
                                // hold the frame in the response buffer until
                                // close.
                                do! ctx.Response.Body.FlushAsync()
                            with ex ->
                                streamingFailure <- Some ex
                                let errFrame = Streaming.formatError ex.Message
                                do! ctx.Response.Body.WriteAsync(errFrame, 0, errFrame.Length)
                                // Flush the terminal error frame for the same
                                // reason as the complete frame above.
                                do! ctx.Response.Body.FlushAsync()
                        finally
                            // The dispose-await CANNOT use `do!` here
                            // because F# `task` doesn't allow binds inside
                            // `finally`. Block-wait is restricted to the
                            // dispose path (no I/O latency expected) — every
                            // other sync-over-async in this branch was
                            // promoted to `let!`.
                            try
                                let disposeValueTask = disposeMethod.Invoke(enumerator, [||])
                                let disposeAsTask = disposeValueTask.GetType().GetMethod "AsTask"

                                let disposeTask =
                                    disposeAsTask.Invoke(disposeValueTask, [||]) :?> System.Threading.Tasks.Task

                                disposeTask.GetAwaiter().GetResult()
                            with _ ->
                                ()

                        // Emit per-method-completion telemetry now
                        // that the stream has terminated (success or error).
                        match streamingFailure with
                        | Some ex -> emitStreamingTelemetry (MethodOutcome.Failed ex)
                        | None -> emitStreamingTelemetry MethodOutcome.Succeeded
                    with setupEx ->
                        // Setup-time exception (null funcObj, missing
                        // STJ, etc.) BEFORE we entered the iteration loop.
                        // The response may already have status set; emit a
                        // single error frame as a courtesy and let the
                        // adapter's outer pipeline surface the rest.
                        if not (ctx.Response.HasStarted) then
                            ctx.Response.StatusCode <- 500
                            ctx.Response.ContentType <- "text/event-stream"

                        let errFrame = Streaming.formatError setupEx.Message

                        try
                            do! ctx.Response.Body.WriteAsync(errFrame, 0, errFrame.Length)
                            do! ctx.Response.Body.FlushAsync()
                        with _ ->
                            ()

                        emitStreamingTelemetry (MethodOutcome.Failed setupEx)

                    return! next ctx
                elif companion.IsSome then
                    // The poll and cancel companions.
                    //
                    // JSON request / JSON response, exactly the shape a
                    // hand-wired `JobHandle<'T> -> Async<JobStatus<'T>>` (or
                    // `-> Async<unit>`) method produces: the body is the
                    // Remoting argument array `[handle]`, the reply is the
                    // STJ serialisation of the typed status (or unit). Served
                    // by the dispatcher off the composed `IJobDispatcher`, so
                    // the pre-flight chain does not run here — ownership is
                    // the guard (the companion caller resolved above), and
                    // the SUBMIT method already ran the full chain.
                    let dispatcher, (methodName, kind, calls) = companion.Value

                    let companionLabel =
                        match kind with
                        | JobCompanion.Cancel -> methodName + "/cancel"
                        | JobCompanion.Status
                        | JobCompanion.Progress -> methodName + "/status"

                    let companionTelemetryActive =
                        options.Telemetry.IsSome
                        && (match options.TelemetryGate with
                            | Some gate -> gate ()
                            | None -> true)

                    let companionStopwatch =
                        if companionTelemetryActive then
                            System.Diagnostics.Stopwatch.StartNew()
                        else
                            Unchecked.defaultof<_>

                    let emitCompanionTelemetry (outcome: MethodOutcome) =
                        if companionTelemetryActive then
                            match options.Telemetry with
                            | Some sink ->
                                companionStopwatch.Stop()

                                sink.OnMethodCompleted {
                                    MethodName = companionLabel
                                    ElapsedMs = int companionStopwatch.ElapsedMilliseconds
                                    Outcome = outcome
                                    CorrelationId = Some correlationId
                                    ChunkCount = None
                                }
                            | None -> ()

                    let! companionOutcome = task {
                        try
                            ctx.Request.EnableBuffering()

                            use reader =
                                new System.IO.StreamReader(
                                    ctx.Request.Body,
                                    System.Text.Encoding.UTF8,
                                    leaveOpen = true
                                )

                            let! bodyText = reader.ReadToEndAsync()

                            let handle =
                                Streaming.parseFirstArg bodyText calls.HandleType stjOptions
                                |> Option.defaultValue calls.EmptyHandle

                            use _caller = CallContext.beginSubject companionSubject companionIsAdmin

                            let call, payloadType =
                                match kind with
                                | JobCompanion.Cancel -> calls.Cancel, typeof<unit>
                                | _ -> calls.Status, calls.StatusType

                            let! result = call dispatcher handle |> Async.StartAsTask

                            ctx.Response.StatusCode <- 200
                            ctx.Response.ContentType <- "application/json; charset=utf-8"

                            do!
                                System.Text.Json.JsonSerializer.SerializeAsync(
                                    ctx.Response.Body,
                                    result,
                                    payloadType,
                                    stjOptions
                                )

                            return Ok()
                        with ex ->
                            return Error ex
                    }

                    match companionOutcome with
                    | Ok() ->
                        emitCompanionTelemetry MethodOutcome.Succeeded
                        return! next ctx
                    | Error ex ->
                        emitCompanionTelemetry (MethodOutcome.Failed ex)

                        if ctx.Response.HasStarted then
                            return! next ctx
                        else
                            ctx.Response.StatusCode <- 500

                            let envelope =
                                Errors.categorisedWithSchema options.SchemaVersion ErrorCategory.System (box ex.Message)

                            return! setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                else
                    // Correlation-id is now established above
                    // (covers both streaming and non-streaming branches).
                    // The non-streaming branch continues with body buffering
                    // + the pre-flight chain.

                    // THE single pre-dispatch buffering stage.
                    // This one call is what makes the request body
                    // rewindable for everything that follows: the
                    // pre-flight reads, the proxy's argument read, and any
                    // stage that reads the body AFTER dispatch — the
                    // dispatcher's own (audit payload, idempotency hash) or
                    // one a consumer composes around the handler. The
                    // `FileBufferingReadStream` it installs is owned by
                    // ASP.NET Core (registered for disposal at
                    // end-of-request); nothing in the dispatcher disposes
                    // it — the proxy reads it with `leaveOpen` and rewinds
                    // (Proxy.fs, the `InputBytes = None` arm). Until 461
                    // the proxy DID dispose it, and the rule "seed the body
                    // cache before dispatch" lived in a comment plus one
                    // guard per known consumer; now a post-dispatch reader
                    // needs no guard and no comment.
                    // `PostDispatchBodyReadTests` is the contract.
                    //
                    // Unconditional on the non-streaming branch.
                    // ASP.NET only allocates the buffer when the body is
                    // read more than once, so the cost is zero for the
                    // common single-read path AND every downstream stage
                    // is freed from the "did EnableBuffering get called
                    // upstream?" hazard that caused audit emission to
                    // silently break when `BodyNormalisation = Disabled`
                    // in 0.1.14.
                    //
                    // TIDY-UP "ToolUp.Remoting per-request cleanups" (F7) —
                    // this is the ONLY EnableBuffering call on the non-streaming
                    // branch. `normaliseRemotingBody` (the conditional follow-on
                    // below) previously called it again; that duplicate was
                    // dropped because buffering at the request level is global
                    // and the second call was misleading rather than necessary.
                    ctx.Request.EnableBuffering()

                    if isProxyHeaderPresent && options.BodyNormalisation = Enabled then
                        do! normaliseRemotingBody ctx

                    // The method name is the trailing path segment.
                    // Computed once, up here, because the body
                    // cache below names it on a fault and
                    // every pre-flight stage after it keys on it.
                    // The negotiated version's handler, when it
                    // differs from the addressed name. `applyRouting`
                    // rewrites ONLY the trailing segment, so everything
                    // downstream — the proxy's endpoint map, the auth /
                    // validation / rate-limit / audit / idempotency
                    // classification keys, the telemetry method name —
                    // keys on the handler that actually runs. That is the
                    // point: `GetThing_V2` carries its own attributes, and
                    // a version that dispatched to one handler while being
                    // authorised as another would be a hole rather than a
                    // feature. Returns the route unchanged for every
                    // unversioned method, which is all of them today.
                    let endpointName =
                        SubRouting.getNextPartOfPath ctx |> SchemaVersion.applyRouting schemaRouting

                    // One line per call when the served version
                    // is scheduled for retirement. After the route rewrite
                    // so the message names the version, not the handler.
                    match schemaRouting with
                    | SchemaRouting.Serve(_, version, Some retireAfter) ->
                        SchemaVersion.logDeprecation options.DiagnosticsLogger addressedMethod version retireAfter
                    | _ -> ()

                    let methodNameForAuth = Route.methodSegment endpointName

                    // Lazy body cache built on a shared bytes
                    // cell. Materialised once on first downstream demand,
                    // then the hash derives from the cached bytes
                    // without re-reading ctx.Request.Body. The 0.1.15 fix
                    // read the body unconditionally for every non-streaming
                    // request even when no downstream stage needed it,
                    // costing ~32 MiB peak on the recommended multipart
                    // upload composition — which is why this does NOT
                    // make this eager: the body stays lazy, and the stream
                    // it reads stays alive (see the buffering stage above).
                    let cachedBodyBytesCell: byte[] option ref = ref None
                    let cachedBodyHashCell: string option ref = ref None

                    // A timestamp for the cold-path telemetry
                    // event below; taken only when a sink is composed so the
                    // hot path pays nothing (GP 13).
                    let bodyCacheClock =
                        if options.Telemetry.IsSome then
                            System.Diagnostics.Stopwatch.GetTimestamp()
                        else
                            0L

                    // Safe to call from ANY stage, before or after dispatch:
                    // the stream it reads is the buffered body the stage
                    // above installed, which nothing in the dispatcher
                    // disposes. A post-dispatch first read is a
                    // seek-to-0 copy of the buffer, not a fault.
                    let readCachedBodyBytes () = task {
                        match cachedBodyBytesCell.Value with
                        | Some bytes -> return bytes
                        | None ->
                            try
                                ctx.Request.Body.Position <- 0L

                                use ms = new System.IO.MemoryStream()
                                do! ctx.Request.Body.CopyToAsync ms
                                ctx.Request.Body.Position <- 0L
                                let bytes = ms.ToArray()
                                cachedBodyBytesCell.Value <- Some bytes
                                return bytes
                            with :? System.ObjectDisposedException as disposed ->
                                // The body was disposed by
                                // something composed AROUND the handler (the
                                // dispatcher never disposes it). Translate the
                                // bare "Cannot access a disposed object" into a
                                // fault that names the method and whether the
                                // response had started, log it, record it on
                                // the telemetry seam as a `Failed` outcome, and
                                // rethrow. Before the first byte that is an
                                // ordinary 500 through the host's error
                                // handling; after it the connection resets
                                // regardless — and the log line + telemetry
                                // record are then the only evidence the
                                // "handler succeeded, client saw 502" shape
                                // leaves behind.
                                let fault =
                                    RequestBodyDisposedException(methodNameForAuth, ctx.Response.HasStarted, disposed)

                                options.DiagnosticsLogger
                                |> Option.iter (fun log -> log (sprintf "ToolUp.Remoting: %s" fault.Message))

                                let telemetryActive =
                                    options.Telemetry.IsSome
                                    && (match options.TelemetryGate with
                                        | Some gate -> gate ()
                                        | None -> true)

                                if telemetryActive then
                                    options.Telemetry.Value.OnMethodCompleted {
                                        MethodName = methodNameForAuth
                                        ElapsedMs =
                                            int
                                                (System.Diagnostics.Stopwatch.GetElapsedTime bodyCacheClock)
                                                    .TotalMilliseconds
                                        Outcome = MethodOutcome.Failed fault
                                        CorrelationId = Some correlationId
                                        ChunkCount = None
                                    }

                                return raise fault
                    }

                    // Body-hash is raw-bytes SHA-256, lazy, and
                    // memoised against the same bytes cell as the text
                    // cache. Correct for multipart bodies (the 0.1.15
                    // string-shape produced false matches when invalid
                    // UTF-8 bytes were lossy-decoded to U+FFFD).
                    let readCachedBodyHash () = task {
                        match cachedBodyHashCell.Value with
                        | Some hash -> return hash
                        | None ->
                            let! bytes = readCachedBodyBytes ()
                            let hash = Idempotency.hashRequestBodyBytes bytes
                            cachedBodyHashCell.Value <- Some hash
                            return hash
                    }

                    use output = rmsManager.GetStream "remoting-output-stream"

                    let props = {
                        ImplementationBuilder = fun _ -> implBuilder ctx
                        EndpointName = endpointName
                        Input = ctx.Request.Body
                        // Populated just before `proxy props` is
                        // invoked (below, after the pre-flight chain). When an
                        // upstream pre-flight stage forced the body cache,
                        // the proxy parses from the cached bytes — no second
                        // stream read on `ctx.Request.Body`, no second string
                        // materialisation.
                        InputBytes = None
                        IsProxyHeaderPresent = isProxyHeaderPresent
                        HttpVerb = ctx.Request.Method
                        InputContentType =
                            if isNull ctx.Request.ContentType then
                                ""
                            else
                                ctx.Request.ContentType
                        Output = output
                    }

                    // Auth pre-flight. Lookup the method's classification,
                    // optionally resolve the auth context, evaluate, deny if needed.
                    //
                    // `methodNameForAuth` is the trailing path segment (the
                    // path), computed above the body cache. For the deny path we emit
                    // a categorised envelope; no telemetry is
                    // emitted on auth deny (the contract is per-method-completion —
                    // auth denial precedes method invocation).
                    //
                    // Auth pre-flight runs only when an AuthContextResolver is
                    // composed. Without one, every method is allowed (matches
                    // the unguarded behaviour for consumers who haven't opted in).
                    // The resolved auth context (when computed) is reused by
                    // the rate-limit pre-flight below — avoids a double
                    // resolver invocation per request.
                    //
                    // Awaited via `let!` instead of sync-over-async
                    // `GetAwaiter().GetResult()`. The previous shape blocked a
                    // thread-pool worker for the full duration of the resolver
                    // (auth / idempotency / rate-limit ALL did this), starving
                    // the pool under any auth resolver that does network I/O
                    // (JWKS fetch, OIDC introspection, tenant lookup). Same
                    // promotion applied below to the idempotency lookup + the
                    // rate-limit evaluation.
                    // WATCH-LIST (perf, no action) — Map lookups per request.
                    // This is the first of the pre-flight chain's per-request
                    // `Map.tryFind` lookups (classification here, then
                    // validation-input type, rate limits, audit metadata) plus a
                    // `Set` membership test. TRIGGER for revisiting — swapping
                    // these `Map`s for `Dictionary<string, _>` — is any SDK or
                    // consumer API record reaching roughly 30 methods. Nothing
                    // below that is worth trading an immutable lookup for a
                    // mutable one and its allocation.
                    //
                    // Re-measured 2026-07-30: the trigger is NOT met. Counting
                    // every `type *Api = {` record across the SDK and the
                    // consuming repos — 53 records — the true maximum is
                    // `IFormApi` at 16 methods, then `ISchedulingApi` 15,
                    // `TeamApi` 15, `KnowledgeApi` 13, `IPlatformTenantApi` 13.
                    // `Map` at n = 16 is a balanced tree of depth ~4. Still about
                    // half the trigger.
                    //
                    // RE-MEASURE WITH THE SAME WHOLE-ESTATE COUNT; never re-derive
                    // the ceiling by eye. The figure this note replaced cited a
                    // 7-field record as the largest, having sampled one repo's
                    // module directory — less than half the real maximum, and it
                    // went stale precisely because it was never re-checked
                    // against the full set.

                    // A classification-map MISS means this method does not belong
                    // to THIS handler's API. In a multi-API `choose` the dispatch
                    // handlers are tried in order and a non-matching request must
                    // fall through to the next handler (the proxy's
                    // `EndpointNotFound -> next` path does this). The
                    // auth pre-flight must therefore NOT fail-closed on a miss —
                    // doing so lets whichever API is composed first deny every
                    // other API's methods (and even raw non-remoting routes like
                    // `/health`), 401-ing the whole app once an auth resolver is
                    // armed. Fail-closed is still guaranteed for genuinely-
                    // unclassified methods: the startup classifier refuses to
                    // start unless every method that DOES belong to this API is
                    // classified, so a `Some` lookup is exhaustive for this API.
                    let methodClassification = classifications |> Map.tryFind methodNameForAuth

                    let! resolvedAuthContextForRequest = task {
                        match options.AuthContextResolver, methodClassification with
                        | None, _
                        | Some _, None
                        | Some _, Some Public -> return None
                        | Some resolver, Some _ ->
                            let! authCtx = resolver ctx |> Async.StartAsTask
                            return Some authCtx
                    }

                    // The resolved subject rides the call context
                    // for the rest of this request, so a handler's
                    // `IJobDispatcher.Enqueue` stamps the job with its
                    // submitting caller (and the job body sees that caller
                    // again). `None` for a public / unresolved / anonymous
                    // request — an ownerless job. Never job-admin here: that
                    // standing is granted only to the companion routes.
                    use _subjectScope =
                        CallContext.beginSubject (LongRunning.subjectOf resolvedAuthContextForRequest) false

                    let denyReason =
                        match options.AuthContextResolver, methodClassification with
                        | None, _
                        | Some _, None -> None
                        | Some _, Some methodCls ->
                            match AuthClassifier.evaluate methodCls resolvedAuthContextForRequest with
                            | Deny r -> Some r
                            | Allow -> None

                    if denyReason.IsSome then
                        // Deny path. Emit categorised Auth envelope
                        // *directly* without consulting the consumer's error
                        // handler — the auth contract is unconditional: classified
                        // methods that fail evaluation always categorise as `Auth`,
                        // regardless of what the consumer's exception-handler
                        // policy is. Status 401 + categorised envelope per
                        // Phase 69b.E. No telemetry — auth denial precedes the
                        // per-method-completion contract.
                        ctx.Response.StatusCode <- 401

                        // The per-request deny reason (`missing-role: Admin`,
                        // `missing-tenant`, …) is server-side-only data — it
                        // names *which* authorization rule failed and so must
                        // not reach the client (it would leak the auth policy;
                        // see the `AuthDecision.Deny` doc in Auth.fs). Route it
                        // to the diagnostics logger and redact the wire `error`
                        // to a constant `"auth-denied"`. The structured channel
                        // is already opaque (generic `category = "auth"`, no
                        // typed reason field — pinned by the adversarial pack),
                        // so this closes the last message-text disclosure.
                        options.DiagnosticsLogger
                        |> Option.iter (fun log ->
                            log (sprintf "%s: auth-denied: %s" methodNameForAuth denyReason.Value))

                        let envelope =
                            Errors.categorisedWithSchema options.SchemaVersion ErrorCategory.Auth (box "auth-denied")

                        return! setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                    else
                        // Typed validation pre-flight.
                        //
                        // Only runs when (a) the method's input type carries
                        // ValidationAttributes (cached at startup) AND (b) the
                        // serializer backend is STJ (the Validation engine uses
                        // STJ to parse the first argument; the Newtonsoft path
                        // is a follow-up for the legacy opt-in branch).
                        //
                        // Body-read pattern: EnableBuffering once,
                        // read, restore position so the proxy reads fresh. The
                        // pre-read is non-destructive — the proxy sees the same
                        // bytes.
                        let validationInputType = validationInputTypes |> Map.tryFind methodNameForAuth

                        // Cache the first-arg value validation
                        // parses so the audit emission below can reuse it
                        // (when audit AND validation are armed for the same
                        // method). Saves a second `parseFirstArgFromBody`
                        // call per request on dual-armed methods.
                        let validationParsedFirstArg: obj option ref = ref None

                        // The arguments validation parsed, handed
                        // to the proxy at dispatch so the body is parsed and
                        // the first argument decoded ONCE per request.
                        let validationParsedArguments: ParsedArguments voption ref = ref ValueNone

                        // Lazy body read: validation only forces
                        // the cache when its method actually has validation
                        // attributes AND the STJ backend is composed.
                        let! validationViolations = task {
                            match validationInputType, options.JsonSerializer with
                            | Some inputT, SystemTextJson _ ->
                                let! bodyBytes = readCachedBodyBytes ()

                                // The dispatch path's own parse and
                                // first-argument decode, run here. `ValueNone`
                                // (unparseable, or refused by the decode) skips
                                // validation and leaves the refusal to the
                                // proxy, exactly as `parseFirstArgFromBody`'s
                                // `None` did.
                                match apiProxy.ParseFirst endpointName bodyBytes with
                                | ValueSome parsed ->
                                    let inputValue = parsed.First
                                    validationParsedArguments.Value <- ValueSome parsed
                                    validationParsedFirstArg.Value <- Some inputValue

                                    // Hand the per-request context to
                                    // any `[<Custom>]` IFieldValidator (subject id +
                                    // correlation id from the request context).
                                    let validationContext =
                                        { new IValidationContext with
                                            member _.SubjectId =
                                                match resolvedAuthContextForRequest with
                                                | Some c -> c.SubjectId
                                                | None -> "anonymous"

                                            member _.CorrelationId = Some correlationId
                                        }

                                    return
                                        Validation.evaluate
                                            options.ValidationMessages
                                            validationContext
                                            inputT
                                            inputValue
                                | ValueNone -> return []
                            | _ -> return []
                        }

                        if not (List.isEmpty validationViolations) then
                            ctx.Response.StatusCode <- 400

                            let payload =
                                box {|
                                    methodName = methodNameForAuth
                                    violations = validationViolations
                                |}

                            let envelope =
                                Errors.categorisedWithSchema options.SchemaVersion ErrorCategory.Validation payload

                            return! setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                        else

                            // Idempotency pre-flight.
                            //
                            // Runs BEFORE rate-limit so cache hits don't decrement
                            // the budget (the original call already paid). When the
                            // method carries [<Idempotent>] AND a store is composed:
                            //   * Missing `X-Idempotency-Key` → ErrorCategory.User envelope.
                            //   * Cache hit → replay the cached response, skip handler + rate-limit.
                            //   * Cache miss → continue down the chain; capture+store
                            //                  the response on success after the handler.
                            let isMethodIdempotent = idempotentMethods.Contains methodNameForAuth

                            // RemoteIpResolver hook. When a consumer
                            // wires a resolver (typically reading
                            // X-Forwarded-For after ASP.NET's
                            // ForwardedHeadersMiddleware has rewritten
                            // ctx.Connection.RemoteIpAddress), use it. Else
                            // fall back to the socket-level address (correct
                            // for hosts NOT behind a CDN / proxy; defeated
                            // the per-IP partition in 0.1.14 for hosts that
                            // were).
                            let remoteIp =
                                match options.RemoteIpResolver with
                                | Some resolver ->
                                    match resolver ctx with
                                    | Some ip -> Some ip
                                    | None ->
                                        let ip = ctx.Connection.RemoteIpAddress
                                        if isNull ip then None else Some(ip.ToString())
                                | None ->
                                    let ip = ctx.Connection.RemoteIpAddress
                                    if isNull ip then None else Some(ip.ToString())

                            let subjectKey = RateLimit.deriveSubjectKey resolvedAuthContextForRequest remoteIp

                            let idempotencyKey =
                                if isMethodIdempotent && options.IdempotencyStore.IsSome then
                                    let mutable values: Microsoft.Extensions.Primitives.StringValues =
                                        Microsoft.Extensions.Primitives.StringValues.Empty

                                    if
                                        ctx.Request.Headers.TryGetValue("X-Idempotency-Key", &values)
                                        && not (System.String.IsNullOrWhiteSpace(values.ToString()))
                                    then
                                        Some(values.ToString())
                                    else
                                        None
                                else
                                    None

                            // Nested early-returns via if/elif/else so every
                            // task arm produces a `Task<HttpContext option>`. The two
                            // early-return shapes (missing header → 400; cache hit → replay)
                            // both fully handle the response before yielding to `next`.
                            let idempotencyArmed = isMethodIdempotent && options.IdempotencyStore.IsSome

                            // ONE prefetch, and it is an
                            // OPTIMISATION, not a correctness guard. When a
                            // post-dispatch consumer is known to need the
                            // body bytes — the idempotency store path (the
                            // response's `RequestBodyHash`) or the audit
                            // payload on an audited method that validation
                            // will not already have read — materialise them
                            // once HERE so the proxy parses from the same
                            // bytes (the `InputBytes` fastpath)
                            // instead of the body crossing the stream
                            // boundary twice. Deleting this line changes
                            // cost, never correctness: the buffered stream
                            // stays alive past dispatch (see the buffering
                            // stage above), so a post-dispatch read that
                            // was NOT prefetched is a seek-to-0 copy, not a
                            // disposed-stream fault. Until 461 the two
                            // halves of this condition were two guards
                            // (idempotency; audit), each
                            // load-bearing for correctness because the
                            // proxy disposed the stream, and each a rule the
                            // next consumer's author had to know about.
                            let postDispatchBodyConsumerArmed =
                                (idempotencyArmed && idempotencyKey.IsSome)
                                || (options.AuditEmitter.IsSome
                                    && auditInputTypes.ContainsKey methodNameForAuth
                                    && not (validationInputTypes.ContainsKey methodNameForAuth))

                            if postDispatchBodyConsumerArmed then
                                let! _ = readCachedBodyBytes ()
                                ()

                            // Async lookup via `let!` instead of
                            // sync-over-async GetAwaiter().GetResult().
                            let! cachedResponse = task {
                                if idempotencyArmed && idempotencyKey.IsSome then
                                    let scope = Idempotency.deriveScope subjectKey methodNameForAuth

                                    let! r =
                                        options.IdempotencyStore.Value.TryGet(idempotencyKey.Value, scope)
                                        |> Async.StartAsTask

                                    return r
                                else
                                    return None
                            }

                            // Body-hash mismatch detection is now
                            // lazy: only computed when (a) idempotency is
                            // armed AND (b) there's a stored response with
                            // a non-empty hash to compare against. The
                            // 0.1.15 shape hashed every idempotent call
                            // regardless — observable CPU on high-RPS
                            // idempotent endpoints with large bodies.
                            let! idempotencyBodyMismatch = task {
                                match cachedResponse with
                                | Some r when
                                    idempotencyArmed
                                    && idempotencyKey.IsSome
                                    && not (System.String.IsNullOrEmpty r.RequestBodyHash)
                                    ->
                                    let! h = readCachedBodyHash ()
                                    return r.RequestBodyHash <> h
                                | _ -> return false
                            }

                            if idempotencyArmed && idempotencyKey.IsNone then
                                // Missing header on an idempotent method → 400 + User envelope.
                                ctx.Response.StatusCode <- 400

                                let envelope =
                                    Errors.categorisedWithSchema
                                        options.SchemaVersion
                                        ErrorCategory.User
                                        (box (
                                            sprintf
                                                "%s: missing-idempotency-key: methods marked [<Idempotent>] require the X-Idempotency-Key header"
                                                methodNameForAuth
                                        ))

                                return! setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                            elif idempotencyBodyMismatch then
                                // Same idempotency key reused with a
                                // different body → 409 Conflict + User
                                // envelope. The cached response is NOT
                                // replayed (it belonged to a different
                                // logical request).
                                ctx.Response.StatusCode <- 409

                                let envelope =
                                    Errors.categorisedWithSchema
                                        options.SchemaVersion
                                        ErrorCategory.User
                                        (box (
                                            sprintf
                                                "%s: idempotency-key-body-mismatch: the X-Idempotency-Key was previously used against a different request body; reuse with a fresh key or repeat the original body exactly"
                                                methodNameForAuth
                                        ))

                                return! setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                            elif cachedResponse.IsSome then
                                // Cache hit — replay verbatim, skip handler + the
                                // downstream rate-limit / telemetry / store steps AND the
                                // method's OWN audit (the first call's audit row is the
                                // system of record; the replay must not double-emit it).
                                let response = cachedResponse.Value
                                ctx.Response.StatusCode <- response.StatusCode
                                ctx.Response.ContentType <- response.ContentType

                                ctx.Response.Headers["x-idempotency-replay"] <-
                                    Microsoft.Extensions.Primitives.StringValues "true"

                                // Positive replay audit. Distinct from the
                                // method's own `[<Audit>]` event (suppressed above): this
                                // marks that a replay occurred and cites the original via
                                // the shared idempotency key. Dispatcher-emitted like
                                // `RateLimitExceeded`, so a replay is auditable even when
                                // the method carries no `[<Audit>]`. Fire only when an
                                // emitter is composed (GP 13 — zero cost otherwise).
                                match options.AuditEmitter with
                                | Some emitter ->
                                    let replaySubjectId =
                                        match resolvedAuthContextForRequest with
                                        | Some authCtx -> authCtx.SubjectId
                                        | None -> "anonymous"

                                    do!
                                        emitter.Emit {
                                            Kind = AuditKind.IdempotencyReplay
                                            MethodName = methodNameForAuth
                                            SubjectId = replaySubjectId
                                            CorrelationId = Some correlationId
                                            Timestamp = System.DateTimeOffset.UtcNow
                                            Payload = Map [ "idempotencyKey", idempotencyKey.Value ]
                                        }
                                        |> Async.StartAsTask
                                | None -> ()

                                do! ctx.Response.Body.WriteAsync(response.Body, 0, response.Body.Length)
                                return! next ctx
                            else
                                // Rate-limit pre-flight.
                                //
                                // Only runs when (a) an `IRateLimitStore` is composed AND
                                // (b) the method carries at least one [<RateLimit>] attribute.
                                // The subject key for the bucket is derived from the resolved
                                // auth context (when present) or "anonymous" otherwise.
                                let methodBudgets =
                                    rateLimits |> Map.tryFind methodNameForAuth |> Option.defaultValue []

                                // Async evaluation via `let!` instead of
                                // sync-over-async GetAwaiter().GetResult().
                                // Reuses the subjectKey computed once above.
                                let! rateLimitDecision = task {
                                    match options.RateLimitStore, methodBudgets with
                                    | None, _ -> return RateLimitAllowed
                                    | _, [] -> return RateLimitAllowed
                                    | Some store, budgets ->
                                        let! d =
                                            RateLimit.evaluate store subjectKey methodNameForAuth budgets
                                            |> Async.StartAsTask

                                        return d
                                }

                                match rateLimitDecision with
                                | RateLimitDenied retryAfter ->
                                    // Status 429 + Retry-After header + categorised envelope.
                                    ctx.Response.StatusCode <- 429
                                    let secondsRounded = max 1 (int (System.Math.Ceiling retryAfter.TotalSeconds))

                                    ctx.Response.Headers["Retry-After"] <-
                                        Microsoft.Extensions.Primitives.StringValues(string secondsRounded)

                                    // Denial telemetry. A bridged
                                    // `IMetricsSink` records `RateLimited` distinctly from
                                    // handler errors so saturation is separable from 5xx on
                                    // dashboards. `ElapsedMs = 0` — the handler never ran;
                                    // denial is a pre-flight short-circuit.
                                    match options.Telemetry with
                                    | Some sink when
                                        (match options.TelemetryGate with
                                         | Some gate -> gate ()
                                         | None -> true)
                                        ->
                                        sink.OnMethodCompleted {
                                            MethodName = methodNameForAuth
                                            ElapsedMs = 0
                                            Outcome = MethodOutcome.RateLimited retryAfter
                                            CorrelationId = Some correlationId
                                            ChunkCount = None
                                        }
                                    | _ -> ()

                                    // Denial audit. Emitted regardless of
                                    // whether the method also carries `[<Audit>]`, so
                                    // rate-limit denial patterns surface in the audit trail.
                                    // `SubjectId` is the rate-limit bucket identity (the
                                    // resolved subject id, or the per-IP fallback for
                                    // anonymous callers).
                                    match options.AuditEmitter with
                                    | Some emitter ->
                                        do!
                                            emitter.Emit {
                                                Kind = AuditKind.RateLimitExceeded
                                                MethodName = methodNameForAuth
                                                SubjectId = subjectKey
                                                CorrelationId = Some correlationId
                                                Timestamp = System.DateTimeOffset.UtcNow
                                                Payload = Map.ofList [ "retryAfterSeconds", string secondsRounded ]
                                            }
                                            |> Async.StartAsTask
                                    | None -> ()

                                    let envelope =
                                        Errors.categorisedWithSchema
                                            options.SchemaVersion
                                            ErrorCategory.RateLimit
                                            (box (
                                                sprintf
                                                    "%s: rate-limit-exceeded: retry after %ds"
                                                    methodNameForAuth
                                                    secondsRounded
                                            ))

                                    return!
                                        setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                                | RateLimitAllowed ->



                                    // Wall-clock measurement for the telemetry hook.
                                    // Started just before proxy dispatch; stopped at every terminal
                                    // branch (success / handler exception). Endpoint-not-found and
                                    // invalid-http-verb don't emit because they aren't valid method
                                    // invocations — the telemetry contract is per-method-completion.
                                    //
                                    // `props.EndpointName` is the full request path (e.g.
                                    // `/api/IHarnessApi/Echo`); the proxy does its own segment
                                    // matching against it. For telemetry we surface just the
                                    // trailing segment — the method-name conventions the
                                    // routeBuilder produced.
                                    let methodName = Route.methodSegment props.EndpointName

                                    // Telemetry-allocation gate. Skips the
                                    // Stopwatch + MethodTelemetry alloc + Map.ofList tags
                                    // entirely when the gate returns false (the canonical
                                    // `Api.make` + `FeatureMode.Disabled` shape — `IMetricsSink`
                                    // resolves to `NoOpMetricsSink`, so emitting would
                                    // do nothing anyway). Consumer-supplied sinks never
                                    // compose a gate, so they always emit.
                                    let telemetryActive =
                                        options.Telemetry.IsSome
                                        && (match options.TelemetryGate with
                                            | Some gate -> gate ()
                                            | None -> true)

                                    let stopwatch =
                                        if telemetryActive then
                                            System.Diagnostics.Stopwatch.StartNew()
                                        else
                                            Unchecked.defaultof<_>

                                    let emitTelemetry (outcome: MethodOutcome) =
                                        if telemetryActive then
                                            match options.Telemetry with
                                            | Some sink ->
                                                stopwatch.Stop()

                                                sink.OnMethodCompleted {
                                                    MethodName = methodName
                                                    ElapsedMs = int stopwatch.ElapsedMilliseconds
                                                    Outcome = outcome
                                                    CorrelationId = Some correlationId
                                                    ChunkCount = None
                                                }
                                            | None -> ()

                                    // Hand cached bytes to the proxy
                                    // when an upstream pre-flight stage forced
                                    // the cache. The proxy then parses from
                                    // bytes directly; no second stream read +
                                    // no second string materialisation.
                                    let propsWithCache = {
                                        props with
                                            InputBytes = cachedBodyBytesCell.Value
                                    }

                                    // An AUDITED method's argument is
                                    // decoded once, by the dispatch path, and the
                                    // audit payload below reads that value. Until
                                    // 905 a method audited without validators was
                                    // parsed here by dispatch and then parsed and
                                    // decoded AGAIN after it, with plain STJ, to
                                    // build the payload. Validated methods already
                                    // arrive with `validationParsedArguments` set
                                    // , so this runs only when the
                                    // validation stage did not parse. The bytes
                                    // were prefetched by `postDispatchBodyConsumerArmed`.
                                    // A multipart body is not an argument array;
                                    // `ValueNone` (or a parse that throws) leaves the
                                    // parse and its refusal to dispatch, as before.
                                    if
                                        validationParsedArguments.Value.IsNone
                                        && options.AuditEmitter.IsSome
                                        && auditInputTypes.ContainsKey methodName
                                        && not (validationInputTypes.ContainsKey methodName)
                                        && not (
                                            props.InputContentType.StartsWith(
                                                "multipart/form-data",
                                                System.StringComparison.Ordinal
                                            )
                                        )
                                    then
                                        match cachedBodyBytesCell.Value with
                                        | Some bytes ->
                                            let parsed =
                                                try
                                                    apiProxy.ParseFirst endpointName bytes
                                                with _ ->
                                                    ValueNone

                                            match parsed with
                                            | ValueSome p ->
                                                validationParsedArguments.Value <- parsed
                                                validationParsedFirstArg.Value <- Some p.First
                                            | ValueNone -> ()
                                        | None -> ()

                                    match! apiProxy.Invoke propsWithCache validationParsedArguments.Value with
                                    | Success isBinaryOutput ->
                                        ctx.Response.StatusCode <- 200

                                        if isBinaryOutput && isProxyHeaderPresent then
                                            ctx.Response.ContentType <- "application/octet-stream"
                                        elif options.ResponseSerialization.IsJson then
                                            ctx.Response.ContentType <- "application/json; charset=utf-8"
                                        else
                                            ctx.Response.ContentType <- "application/vnd.msgpack"

                                        // Capture response bytes before flushing
                                        // when the method is idempotent and a store is composed.
                                        // RecyclableMemoryStream.ToArray() is position-independent
                                        // so the subsequent CopyToAsync still drains correctly.
                                        let capturedBody: byte[] =
                                            if
                                                isMethodIdempotent
                                                && options.IdempotencyStore.IsSome
                                                && idempotencyKey.IsSome
                                            then
                                                output.ToArray()
                                            else
                                                null

                                        do! output.CopyToAsync ctx.Response.Body
                                        emitTelemetry MethodOutcome.Succeeded

                                        // Store the captured response so the
                                        // next call with the same key replays it. Fire-and-
                                        // wait so the test gate observes deterministic state.
                                        if not (isNull capturedBody) then
                                            let scope = Idempotency.deriveScope subjectKey methodNameForAuth

                                            // Body-hash is only
                                            // materialised on the store path
                                            // (a previously-missed cache
                                            // store). For mismatch-check
                                            // paths the lazy helper already
                                            // forced it. This branch only
                                            // runs when `idempotencyArmed`
                                            // is true (the outer `if` is
                                            // gated on `capturedBody`, which
                                            // is non-null only when the
                                            // method is idempotent AND a
                                            // store is composed), so
                                            // computing the hash here is
                                            // genuinely needed.
                                            let! bodyHash = readCachedBodyHash ()

                                            let response = {
                                                Body = capturedBody
                                                StatusCode = ctx.Response.StatusCode
                                                ContentType = ctx.Response.ContentType
                                                RequestBodyHash = bodyHash
                                            }

                                            let! stored =
                                                options.IdempotencyStore.Value.Store(
                                                    idempotencyKey.Value,
                                                    scope,
                                                    response,
                                                    options.IdempotencyTtl
                                                )
                                                |> Async.Catch
                                                |> Async.StartAsTask

                                            // A store that could not
                                            // write RAISES, and the call is then NOT
                                            // memoised: a retry with this key runs the
                                            // handler again. The answer has already
                                            // been written to the caller and the
                                            // handler's effect has happened, so the
                                            // call is not failed — it is reported, and
                                            // the audit emission below still runs.
                                            match stored with
                                            | Choice1Of2() -> ()
                                            | Choice2Of2 storeFailure ->
                                                let line =
                                                    sprintf
                                                        "[remoting] idempotency store write FAILED in %s — the call was answered but is NOT memoised; a retry with the same X-Idempotency-Key re-executes the handler: %s: %s"
                                                        methodName
                                                        (storeFailure.GetType().FullName)
                                                        storeFailure.Message

                                                eprintfn "%s" line
                                                options.DiagnosticsLogger |> Option.iter (fun log -> log line)

                                        // Audit emission after successful invocation.
                                        // Looks up the method's audit kind (if any) and emits an
                                        // AuditEvent. Payload is the PII-redacted input-record
                                        // snapshot (fields without [<PiiSafe>] are redacted to
                                        // <redacted:TypeName>); when no input record is available
                                        // (unit-input methods or non-record args) the payload
                                        // is empty.
                                        match options.AuditEmitter, audits |> Map.tryFind methodName with
                                        | Some emitter, Some kind ->
                                            let subjectId =
                                                match resolvedAuthContextForRequest with
                                                | Some authCtx -> authCtx.SubjectId
                                                | None -> "anonymous"

                                            // When validation
                                            // parsed the first-arg value
                                            // earlier in the same request,
                                            // reuse the cached value instead
                                            // of re-running `parseFirstArgFromBody`
                                            // a second time.
                                            // Input type comes from the
                                            // validation classifier when the method is
                                            // validated (reusing its parsed-arg cache), or
                                            // from the audit classifier's own input-type map
                                            // otherwise — audited methods without validators
                                            // used to emit empty payloads.
                                            //
                                            // And when validation did
                                            // not parse it, the dispatch stage did,
                                            // just before `Invoke` (see there). No
                                            // second STJ parse remains on this path:
                                            // an empty cache means the argument was
                                            // not an array this path could decode
                                            // (a multipart body), and the payload is
                                            // empty, which is what the re-parse
                                            // answered for one.
                                            let payload =
                                                match
                                                    validationInputTypes
                                                    |> Map.tryFind methodName
                                                    |> Option.orElse (auditInputTypes |> Map.tryFind methodName)
                                                with
                                                | Some inputT ->
                                                    match validationParsedFirstArg.Value with
                                                    | Some v -> Audit.payloadFromInputRecord inputT v
                                                    | None -> Map.empty
                                                | None -> Map.empty

                                            let evt = {
                                                Kind = kind
                                                MethodName = methodName
                                                SubjectId = subjectId
                                                CorrelationId = Some correlationId
                                                Timestamp = System.DateTimeOffset.UtcNow
                                                Payload = payload
                                            }

                                            do! emitter.Emit evt |> Async.StartAsTask
                                        | _ -> ()

                                        return! next ctx
                                    | Exception(e, functionName, requestBodyText) ->
                                        // Categorise multipart-cap
                                        // breaches as `User` + 413 Payload
                                        // Too Large rather than the generic
                                        // `System` / unhandled categorisation
                                        // any other exception would receive.
                                        // Surfacing the right category lets
                                        // operators distinguish caller-side
                                        // misuse from server-side faults in
                                        // metrics / audit / log dashboards.
                                        match e with
                                        | :? MultipartCapExceededException as capEx ->
                                            ctx.Response.StatusCode <- 413
                                            emitTelemetry (MethodOutcome.Failed e)

                                            let envelope =
                                                Errors.categorisedWithSchema
                                                    options.SchemaVersion
                                                    ErrorCategory.User
                                                    (box (
                                                        sprintf
                                                            "%s: multipart-cap-exceeded: %s"
                                                            functionName
                                                            capEx.Message
                                                    ))

                                            return!
                                                setJsonBody
                                                    options.JsonSerializer
                                                    envelope
                                                    options.DiagnosticsLogger
                                                    next
                                                    ctx
                                        | _ ->
                                            ctx.Response.StatusCode <- 500

                                            let routeInfo = {
                                                methodName = functionName
                                                path = ctx.Request.Path.ToString()
                                                httpContext = ctx
                                                requestBodyText = requestBodyText
                                            }

                                            // Unconditional stderr breadcrumb.
                                            // The Remoting transport otherwise swallows
                                            // the exception into the `Errors.unhandled`
                                            // envelope with no detail, leaving operators
                                            // blind to the actual failure. eprintfn lands
                                            // in stderr → captured by Docker into the
                                            // App Service default_docker.log
                                            // unconditionally; no DI, no logger plumbing
                                            // needed. Cheap (one printf per 500); the
                                            // genuine cold-path cost is the exception
                                            // itself, not the log line.
                                            eprintfn
                                                "[remoting] EXCEPTION in %s: %s: %s"
                                                functionName
                                                (e.GetType().FullName)
                                                e.Message

                                            if not (isNull e.StackTrace) then
                                                eprintfn "[remoting] stack:\n%s" e.StackTrace

                                            match e.InnerException with
                                            | null -> ()
                                            | inner ->
                                                eprintfn
                                                    "[remoting] inner: %s: %s"
                                                    (inner.GetType().FullName)
                                                    inner.Message

                                            emitTelemetry (MethodOutcome.Failed e)
                                            return! fail e routeInfo options next ctx
                                    | DecodeRefused(error, functionName) ->
                                        // The request did not
                                        // decode. A named refusal, not a
                                        // fault: 400 + the
                                        // `validation` envelope, so a
                                        // client that already branches on
                                        // `category` needs no new code to
                                        // tell "you sent the wrong shape"
                                        // from "the server fell over".
                                        // The handler never ran.
                                        //
                                        // The payload mirrors the
                                        // violation envelope's shape
                                        // (`methodName` + structured
                                        // detail) so both arrive under one
                                        // category with one parse. `path`
                                        // is the segment list, not the
                                        // rendered string, because a
                                        // client branching on the field
                                        // must not have to re-split prose;
                                        // `message` carries the rendered
                                        // sentence for logs and for the
                                        // clients that only display it.
                                        ctx.Response.StatusCode <- 400
                                        emitTelemetry (MethodOutcome.Failed(ToolUp.Remoting.DecodeException error))

                                        let payload =
                                            box {|
                                                methodName = functionName
                                                decodeError = {|
                                                    path = error.Path
                                                    expected = error.Expected
                                                    found = error.Found
                                                |}
                                                message = ToolUp.Remoting.DecodeError.render error
                                            |}

                                        let envelope =
                                            Errors.categorisedWithSchema
                                                options.SchemaVersion
                                                ErrorCategory.Validation
                                                payload

                                        return!
                                            setJsonBody
                                                options.JsonSerializer
                                                envelope
                                                options.DiagnosticsLogger
                                                next
                                                ctx
                                    | InvalidHttpVerb -> return halt
                                    | EndpointNotFound -> return! notFound options next ctx
            }


    /// Mount a composition: its contract (refused at compose time when the
    /// composition makes a refusal of the record), its non-batchable routes
    /// recorded for the batch route, and its dispatcher table, built once.
    let buildDispatcherTable<'impl> (options: RemotingOptions<HttpContext, 'impl>) =
        let contract = contractFor options
        MountedContracts.mount contract
        buildDispatcherTableFor contract options

module Remoting =

    // Once-per-type registry so the Empty-impl warning
    // doesn't spam the log when a test harness composes the same
    // shape repeatedly. Keyed on `typeof<'t>.FullName` so two
    // unrelated API records each emit their own one-shot warning.
    let private emptyWarningOnce =
        System.Collections.Concurrent.ConcurrentDictionary<string, unit>()

    let private warnEmptyOnce (typeKey: string) (logger: Option<string -> unit>) (message: string) =
        if emptyWarningOnce.TryAdd(typeKey, ()) then
            Diagnostics.outputPhase logger message

    /// Builds a HttpHandler from the given implementation and options
    let buildHttpHandler (options: RemotingOptions<HttpContext, 't>) =
        match options.Implementation with
        | Empty ->
            // Once-per-process warning. The Giraffe adapter
            // supports the full from* surface, so the warning lists
            // every option.
            warnEmptyOnce
                typeof<'t>.FullName
                options.DiagnosticsLogger
                "ToolUp.Remoting (Giraffe): composed with Empty implementation; no routes will dispatch. This is almost always a misconfiguration — pipe through Remoting.fromValue / .fromContext / .fromContextAsync to register a real impl."

            fun _ _ -> skipPipeline
        | StaticValue impl -> GiraffeUtil.buildDispatcherTable options (fun _ -> impl)
        | FromContext createImplementationFrom -> GiraffeUtil.buildDispatcherTable options createImplementationFrom
        | FromContextAsync createImplementationFromAsync ->
            // Await the async resolver per request, then dispatch
            // through the sync path with the resolved impl. The resolver is
            // invoked on every request, not snapshotted at composition time.
            //
            // The dispatcher table is built ONCE at compose time (proxy +
            // classifier maps + rmsManager + guards); per request just await
            // the async resolver and hand the result to it.
            let dispatch = GiraffeUtil.buildDispatcherTable options

            fun (next: HttpFunc) (ctx: HttpContext) -> task {
                let! impl = createImplementationFromAsync ctx |> Async.StartAsTask
                return! dispatch (fun _ -> impl) next ctx
            }

/// The options of the remoting batch route (`RemotingBatch`).
type RemotingBatchOptions = {
    /// The path the envelope is POSTed to. Default `/api/_batch`.
    Route: string
    /// The most elements one envelope may carry; a larger one is refused
    /// whole, before any element runs. Default 32.
    MaxElements: int
    /// Wraps the downstream pipeline for EACH element: the middleware a
    /// lone call would have passed through BEFORE the batch point. The
    /// batch middleware sits at the composition's pre-middleware seam, so
    /// CSRF has already run — for the envelope, not for its elements;
    /// `ServerApp.withRemotingBatching` supplies the CSRF middleware here.
    /// The identity function adds nothing.
    ElementPipeline: RequestDelegate -> RequestDelegate
}

/// Same-tick calls travel as one request: the server half.
///
/// An ASP.NET Core middleware serving `POST <Route>`. The body is a JSON
/// array of `{ "route": "/api/Api/Method", "body": "<the call's JSON body>"
/// | null }`; the answer is a JSON array of `{ "status": n, "body": "..." }`,
/// one per element, in order. Each element runs through the REST OF THE
/// PIPELINE — everything composed after this middleware, down to the
/// remoting dispatcher — on a request of its own: its own path, method and
/// body; the envelope's headers, cookies, connection and session; a fresh
/// `Items` (seeded from the envelope's) and its own DI scope. So scope
/// resolution, surface enforcement, the inbound rate limiter and every
/// dispatcher seam (authorisation, rate limit, audit, idempotency,
/// validation) run per element exactly as for a call that arrived alone,
/// and a refusal of one element is that element's `status`, never the
/// envelope's. Elements run one after another, in order: two calls that
/// arrived alone would share the session, and sequential execution keeps
/// that sharing as safe as it is for them.
///
/// The envelope is refused whole — `400` with `remoting_batch_refused`,
/// nothing run — when it is not an array of elements, exceeds
/// `MaxElements`, names a route that is not a plain absolute path, or
/// names a streaming or long-running route (or its companions). The
/// client re-sends a refused envelope's calls one request each.
///
/// GP 11 / GP 13: nothing is served until composed.
module RemotingBatch =

    open System
    open System.Collections.Generic
    open System.Text
    open System.Text.Json
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Http.Features
    open Microsoft.AspNetCore.Http.Features.Authentication
    open Microsoft.Extensions.DependencyInjection

    [<Literal>]
    let DefaultRoute = "/api/_batch"

    [<Literal>]
    let DefaultMaxElements = 32

    /// The `error` code of a refused envelope (and of a refused element).
    [<Literal>]
    let RefusalCode = BatchElement.RefusalCode

    /// The `HttpContext.Items` key present on every element's request.
    [<Literal>]
    let ElementItemsKey = BatchElement.ItemsKey

    /// `DefaultRoute`, `DefaultMaxElements`, no element pipeline.
    let defaults: RemotingBatchOptions = {
        Route = DefaultRoute
        MaxElements = DefaultMaxElements
        ElementPipeline = id
    }

    /// Whether an envelope may name `route` — false for a streaming or
    /// long-running method's route (or its companions) of any API this
    /// process has mounted.
    let isBatchable (route: string) : bool = not (MountedContracts.isRefused route)

    type private Element = { Route: string; Body: string option }

    /// A route an element may name: an absolute path with nothing a real
    /// request line would have had normalised away first — no query, no
    /// fragment, no percent-escape, no backslash, no empty / `.` / `..`
    /// segment, no control character.
    let private isPlainPath (route: string) =
        let refusedChars = set [ '?'; '#'; '%'; char 92 ]

        not (String.IsNullOrEmpty route)
        && route.[0] = '/'
        && route.Length > 1
        && route
           |> Seq.forall (fun c -> not (Char.IsControl c) && not (refusedChars.Contains c))
        && (route.Substring 1).Split('/')
           |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> "..")

    let private readElement (options: RemotingBatchOptions) (item: JsonElement) : Result<Element, string> =
        let mutable route = Unchecked.defaultof<JsonElement>
        let mutable body = Unchecked.defaultof<JsonElement>

        if item.ValueKind <> JsonValueKind.Object then
            Error "an element is not an object"
        elif
            not (item.TryGetProperty("route", &route))
            || route.ValueKind <> JsonValueKind.String
        then
            Error "an element has no string `route`"
        else
            let routeText = route.GetString()

            let bodyText =
                if item.TryGetProperty("body", &body) then
                    match body.ValueKind with
                    | JsonValueKind.String -> Ok(Some(body.GetString()))
                    | JsonValueKind.Null -> Ok None
                    | _ -> Error "an element's `body` is neither a string nor null"
                else
                    Ok None

            if not (isPlainPath routeText) then
                Error(sprintf "the element route '%s' is not a plain absolute path" routeText)
            elif String.Equals(routeText, options.Route, StringComparison.OrdinalIgnoreCase) then
                Error "an envelope may not name the batch route"
            elif MountedContracts.isRefused routeText then
                Error(sprintf "'%s' is a streaming or long-running route, which is never batched" routeText)
            else
                bodyText |> Result.map (fun bodyText -> { Route = routeText; Body = bodyText })

    let private parse (options: RemotingBatchOptions) (text: string) : Result<Element list, string> =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Array then
                Error "the envelope is not a JSON array"
            elif root.GetArrayLength() > options.MaxElements then
                Error(
                    sprintf
                        "the envelope carries %d elements; the bound is %d"
                        (root.GetArrayLength())
                        options.MaxElements
                )
            else
                let rec collect (acc: Element list) (items: JsonElement list) =
                    match items with
                    | [] -> Ok(List.rev acc)
                    | item :: rest ->
                        match readElement options item with
                        | Ok element -> collect (element :: acc) rest
                        | Error reason -> Error reason

                collect [] (List.ofSeq (root.EnumerateArray()))
        with :? JsonException ->
            Error "the envelope is not valid JSON"

    let private writeJson (ctx: HttpContext) (status: int) (write: Utf8JsonWriter -> unit) : Task = task {
        use buffer = new IO.MemoryStream()

        do
            use writer = new Utf8JsonWriter(buffer)
            write writer
            writer.Flush()

        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.ContentLength <- Nullable(buffer.Length)
        do! ctx.Response.Body.WriteAsync(buffer.GetBuffer(), 0, int buffer.Length)
    }

    let private refuseEnvelope (ctx: HttpContext) (reason: string) : Task =
        writeJson ctx 400 (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("error", RefusalCode)
            writer.WriteString("reason", reason)
            writer.WriteEndObject())

    /// The response of one element. Records the `OnStarting` / `OnCompleted`
    /// callbacks the pipeline registers and runs them as a server would, so
    /// middleware that stamps headers or defers work at response time
    /// behaves as it does for a call that arrived alone. Internal rather
    /// than private because the hardening-baseline probe drives a
    /// request through the composed pipeline the same way and reads the
    /// same callbacks (`HostingBaseline.probe`).
    type internal ElementResponseFeature() =
        inherit HttpResponseFeature()
        let starting = Stack<Func<obj, Task> * obj>()
        let completed = Stack<Func<obj, Task> * obj>()
        let mutable started = false
        override _.HasStarted = started
        override _.OnStarting(callback, state) = starting.Push(callback, state)
        override _.OnCompleted(callback, state) = completed.Push(callback, state)

        member _.Start() : Task = task {
            if not started then
                started <- true

                while starting.Count > 0 do
                    let callback, state = starting.Pop()
                    do! callback.Invoke state
        }

        member _.Complete() : Task = task {
            while completed.Count > 0 do
                let callback, state = completed.Pop()

                try
                    do! callback.Invoke state
                with _ ->
                    ()
        }

    let private notCopied =
        HashSet<string>([ "Content-Length"; "Transfer-Encoding"; "Content-Encoding" ], StringComparer.OrdinalIgnoreCase)

    /// Run one element through `pipeline` on a request of its own: its
    /// status, its body text, and the cookies it set.
    let private runElement (pipeline: RequestDelegate) (parent: HttpContext) (element: Element) = task {
        let features = FeatureCollection(parent.Features)

        let request = HttpRequestFeature()
        request.Protocol <- parent.Request.Protocol
        request.Scheme <- parent.Request.Scheme

        request.Method <-
            if element.Body.IsSome then
                HttpMethods.Post
            else
                HttpMethods.Get

        request.PathBase <- parent.Request.PathBase.Value
        request.Path <- element.Route
        request.QueryString <- ""
        request.RawTarget <- parent.Request.PathBase.Value + element.Route

        let headers = HeaderDictionary()

        for KeyValue(name, value) in parent.Request.Headers do
            if not (notCopied.Contains name) then
                headers.[name] <- value

        let bodyBytes =
            match element.Body with
            | Some text -> Encoding.UTF8.GetBytes text
            | None -> Array.empty

        if element.Body.IsSome then
            headers.ContentLength <- Nullable(int64 bodyBytes.Length)

        request.Headers <- headers
        request.Body <- new IO.MemoryStream(bodyBytes, false)
        features.Set<IHttpRequestFeature> request

        let response = ElementResponseFeature()
        features.Set<IHttpResponseFeature> response
        use responseBody = new IO.MemoryStream()
        let responseBodyFeature = StreamResponseBodyFeature(responseBody)
        features.Set<IHttpResponseBodyFeature> responseBodyFeature

        // Upstream stamps (request id, resolved share token, …) carry over;
        // what the element's own pipeline stamps stays with the element.
        let items = Dictionary<obj, obj>()

        for KeyValue(key, value) in parent.Items do
            items.[key] <- value

        items.[box ElementItemsKey] <- box true
        features.Set<IItemsFeature>(ItemsFeature(Items = items))
        features.Set<IHttpAuthenticationFeature>(HttpAuthenticationFeature(User = parent.User))
        features.Set<IQueryFeature>(QueryFeature(features))
        features.Set<IRequestCookiesFeature>(RequestCookiesFeature(features))
        features.Set<IResponseCookiesFeature>(ResponseCookiesFeature(features))
        features.Set<IRouteValuesFeature>(RouteValuesFeature())

        use scope =
            parent.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope()

        features.Set<IServiceProvidersFeature>(ServiceProvidersFeature(RequestServices = scope.ServiceProvider))

        let context = DefaultHttpContext(features)
        context.Features.Set<IFormFeature>(FormFeature(context.Request))
        context.Features.Set<IRequestBodyPipeFeature>(RequestBodyPipeFeature(context))

        try
            do! pipeline.Invoke context
        with ex ->
            if not response.HasStarted then
                response.StatusCode <- 500
                responseBody.SetLength 0L

            eprintfn "[remoting] batch element %s threw %s: %s" element.Route (ex.GetType().FullName) ex.Message

        do! response.Start()
        do! responseBodyFeature.CompleteAsync()
        do! response.Complete()

        let cookies = response.Headers.SetCookie.ToArray()
        return response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()), cookies
    }

    /// The middleware. Compose it at the pre-middleware seam
    /// (`ServerApp.withRemotingBatching` does), or on a bare
    /// `IApplicationBuilder` ahead of the APIs it batches.
    let middleware (options: RemotingBatchOptions) (next: RequestDelegate) : RequestDelegate =
        let elementPipeline = options.ElementPipeline next

        RequestDelegate(fun ctx ->
            if
                HttpMethods.IsPost ctx.Request.Method
                && String.Equals(ctx.Request.Path.Value, options.Route, StringComparison.OrdinalIgnoreCase)
            then
                task {
                    use reader = new IO.StreamReader(ctx.Request.Body, Encoding.UTF8)
                    let! text = reader.ReadToEndAsync()

                    match parse options text with
                    | Error reason -> do! refuseEnvelope ctx reason
                    | Ok elements ->
                        let answers = ResizeArray<int * string>()

                        for element in elements do
                            let! status, body, cookies = runElement elementPipeline ctx element
                            answers.Add((status, body))

                            for cookie in cookies do
                                ctx.Response.Headers.Append("Set-Cookie", cookie)

                        do!
                            writeJson ctx 200 (fun writer ->
                                writer.WriteStartArray()

                                for status, body in answers do
                                    writer.WriteStartObject()
                                    writer.WriteNumber("status", status)
                                    writer.WriteString("body", body)
                                    writer.WriteEndObject()

                                writer.WriteEndArray())
                }
                :> Task
            else
                next.Invoke ctx)

    /// Compose the batch route on `app` (`app.Use` of `middleware`).
    let useBatching (options: RemotingBatchOptions) (app: IApplicationBuilder) : IApplicationBuilder =
        app.Use(Func<RequestDelegate, RequestDelegate>(middleware options))
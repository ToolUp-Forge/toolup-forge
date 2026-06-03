namespace ToolUp.Remoting.Giraffe

open Microsoft.AspNetCore.Http

open Giraffe
open System.IO
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Proxy
open System.Threading.Tasks

module GiraffeUtil =
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
                // 0.5.9 — use async overload. Kestrel runs with
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
                    // Phase 69b.E — emit the additive categorised envelope.
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
            let schema = Docs.makeDocsSchema typeof<'impl> docs options.RouteBuilder
            let docsApp = DocsApp.embedded docsName docsUrl schema
            htmlString docsApp next ctx
        | "OPTIONS", (Some docsUrl, Some docs) when
            sprintf "/%s/$schema" docsUrl = ctx.Request.Path.Value
            || sprintf "%s/$schema" docsUrl = ctx.Request.Path.Value
            ->
            let schema = Docs.makeDocsSchema typeof<'impl> docs options.RouteBuilder
            let serializedSchema = schema.ToJsonString()
            text serializedSchema next ctx
        | _ -> Task.FromResult halt

    /// Phase 69b.A — body normalisation built-in.
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

    /// Phase 69n — build the dispatcher table ONCE for a given `options`
    /// record. All substrate construction (proxy + classifier maps +
    /// `rmsManager`) and compose-time guards (record-shape check +
    /// unclassified-method refusal + streaming-attribute refusal) happen
    /// here, not per request. The returned function accepts a per-request
    /// `implBuilder` and yields the `HttpHandler` that dispatches a single
    /// request against that builder.
    ///
    /// The pre-69n `buildFromImplementation` baked both phases into a
    /// single function: callers that wanted to vary the impl per-request
    /// (the `fromContextAsync` shape) had to call the whole thing per
    /// request, paying the substrate cost on every call. The carve here
    /// preserves the `fromContext` / `fromValue` semantics (build once,
    /// dispatch many) AND restores the `fromContextAsync` contract to
    /// build-once / read-per-call.
    let buildDispatcherTable<'impl> (options: RemotingOptions<HttpContext, 'impl>) =
        let proxy = makeApiProxy options
        let rmsManager = getRecyclableMemoryStreamManager options

        // Fail-fast: every attribute-driven seam reflects over F# record
        // fields. A non-record API type silently bypasses all enforcement
        // (every classifier returns empty), which makes the "refuses to
        // start on unclassified methods" guarantee fall through unnoticed.
        // Refuse at compose time whenever a seam expecting record reflection
        // is composed against a non-record `'impl`.
        let isRecordImpl = Microsoft.FSharp.Reflection.FSharpType.IsRecord typeof<'impl>

        let seamsRequiringRecord =
            options.AuthContextResolver.IsSome
            || options.RateLimitStore.IsSome
            || options.AuditEmitter.IsSome
            || options.IdempotencyStore.IsSome

        if seamsRequiringRecord && not isRecordImpl then
            invalidOp (
                sprintf
                    "ToolUp.Remoting refused to start: API type '%s' is not an F# record, but at least one attribute-driven seam (authorisation / rate-limit / audit / idempotency / validation) was composed. These seams require record-field reflection. Convert '%s' to an F# record, or remove the seam composition."
                    typeof<'impl>.Name
                    typeof<'impl>.Name
            )

        // Phase 69d — startup classification.
        //
        // Opt-in semantics: when an `AuthContextResolver` is composed,
        // every API record field must carry one of the authorisation
        // attributes (refuse to start on unclassified). When NO resolver
        // is composed, the auth system is dormant — unclassified methods
        // are treated as `Public`, matching pre-69d behaviour for
        // consumers who haven't opted in.
        let classifications = AuthClassifier.classify typeof<'impl>

        if options.AuthContextResolver.IsSome then
            let unclassified = AuthClassifier.unclassified classifications

            if not (List.isEmpty unclassified) then
                raise (AuthClassifier.unclassifiedException typeof<'impl>.Name unclassified)

        // Phase 69g — cache rate-limit attributes per method at startup.
        // The map is empty for API records that carry no [<RateLimit>]
        // attributes; per-call evaluation skips quickly in that case.
        let rateLimits = RateLimit.classify typeof<'impl>

        // Phase 69h — cache audit attributes per method at startup.
        // Empty for non-audited methods; emission on each successful
        // invocation looks up the kind here.
        let audits = Audit.classify typeof<'impl>

        // Phase 69f — cache idempotency classifications per method at
        // startup. Empty set means no methods are idempotent; per-call
        // evaluation skips quickly when the lookup misses.
        let idempotentMethods = Idempotency.classify typeof<'impl>

        // Phase 69e — cache per-method input types when their record
        // shape carries at least one ValidationAttribute. Empty map
        // means no methods need pre-flight validation; per-call lookup
        // is a fast miss.
        let validationInputTypes = Validation.classify typeof<'impl>

        // Phase 69c — cache per-method (argType, elementType) for
        // streaming methods returning IAsyncEnumerable<'T>. Empty map
        // means no streaming methods; per-request lookup is a fast miss.
        let streamingShapes = Streaming.classify typeof<'impl>

        // Refuse to start when a streaming method carries pre-flight
        // attributes the SSE short-circuit doesn't honour (auth /
        // rate-limit / audit / idempotency). The v0 streaming path
        // bypasses the pre-flight chain; silently dropping the
        // enforcement would route around RBAC. Force the composer to
        // either drop the attribute or move the method off streaming
        // until per-frame composition lands.
        let streamingViolations =
            Streaming.streamingMethodsCarryingUnenforceableAttributes typeof<'impl>

        if not (List.isEmpty streamingViolations) then
            let pretty =
                streamingViolations
                |> List.map (fun (methodName, attrs) ->
                    sprintf "  '%s' carries: [%s]" methodName (String.concat "; " attrs))
                |> String.concat System.Environment.NewLine

            invalidOp (
                sprintf
                    "ToolUp.Remoting refused to start: API record '%s' has streaming method(s) carrying pre-flight attributes the SSE dispatch path does not enforce today. Silently honouring them would route around the declared guard.%s%s%sFix: either remove the attribute, or convert the method off the IAsyncEnumerable<'T> return shape until per-frame seam composition lands."
                    typeof<'impl>.Name
                    System.Environment.NewLine
                    pretty
                    System.Environment.NewLine
            )

        // Phase 69n — returned closure-of-closures: `dispatch implBuilder`
        // produces an `HttpHandler` that dispatches one request. The outer
        // `buildDispatcherTable` evaluation above (proxy + classifiers +
        // guards) is one-time per `options` value; only the innermost
        // `task { ... }` body executes per request.
        fun (implBuilder: HttpContext -> 'impl) ->
            fun (next: HttpFunc) (ctx: HttpContext) -> task {
                let isProxyHeaderPresent = ctx.Request.Headers.ContainsKey "x-remoting-proxy"

                // 0.1.16 — correlation-id + telemetry shared between the
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

                // Phase 69c — streaming-method short-circuit.
                //
                // Methods returning IAsyncEnumerable<'T> bypass the proxy entirely
                // and emit SSE frames. The pre-flight chain (auth / validation /
                // idempotency / rate-limit / audit) doesn't apply to
                // streaming methods in v0 — those seams compose with non-streaming
                // dispatch only. Future enhancements compose them per-frame.
                //
                // 0.1.16 — telemetry IS emitted on streaming methods (start ⇒
                // completion); audit + auth remain non-streaming-only.
                let streamingPath =
                    let path = ctx.Request.Path.Value
                    let lastSlash = path.LastIndexOf '/'

                    if lastSlash >= 0 then
                        path.Substring(lastSlash + 1)
                    else
                        path

                let streamingShape = streamingShapes |> Map.tryFind streamingPath

                if streamingShape.IsSome then
                    // 0.1.16 — streaming-method telemetry stopwatch.
                    // Started at SSE entry; stopped at terminal frame
                    // (complete / error). Bridges the streaming branch
                    // back into the per-method-completion contract of
                    // `IRemotingTelemetry`.
                    //
                    // Phase 69l — gated symmetrically with the non-streaming
                    // branch: `Api.make` + `NoMetricsEndpoint` deployments
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

                    let emitStreamingTelemetry (outcome: MethodOutcome) =
                        if streamingTelemetryActive then
                            match options.Telemetry with
                            | Some sink ->
                                streamingStopwatch.Stop()

                                sink.OnMethodCompleted {
                                    MethodName = streamingPath
                                    ElapsedMs = int streamingStopwatch.ElapsedMilliseconds
                                    Outcome = outcome
                                    CorrelationId = Some correlationId
                                }
                            | None -> ()

                    // 0.1.16 — wrap the streaming setup in a try/with so
                    // setup-time exceptions (null funcObj from invokeMethod,
                    // missing serializer, etc.) emit a single error frame
                    // before failing the response, rather than NRE-ing
                    // outside any handler.
                    try
                        let argType, elementType = streamingShape.Value
                        // Read body, parse first arg. EnableBuffering is required
                        // before any Position-rewinding read on the request body —
                        // the streaming branch runs BEFORE body-normalisation so we
                        // can't rely on its EnableBuffering call.
                        ctx.Request.EnableBuffering()

                        use reader =
                            new System.IO.StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8, leaveOpen = true)

                        let! bodyText = reader.ReadToEndAsync()

                        let stjOptions =
                            match options.JsonSerializer with
                            | SystemTextJson opts -> opts

                        let argValue =
                            Streaming.parseFirstArg bodyText argType stjOptions
                            |> Option.defaultWith (fun () -> System.Activator.CreateInstance argType)
                        // Resolve impl + invoke streaming method.
                        let impl = implBuilder ctx
                        let asyncEnumerable = Streaming.invokeMethod impl streamingPath argValue

                        // 0.1.16 — guard against a streaming method that
                        // returns null (a legitimate-looking but broken
                        // impl). Without this, `getEnumeratorMethod.Invoke`
                        // NREs and the response goes out as a 500 with no
                        // SSE error frame.
                        if isNull asyncEnumerable then
                            invalidOp (
                                sprintf
                                    "streaming method '%s' returned null instead of an IAsyncEnumerable<_>"
                                    streamingPath
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

                        // 0.1.16 — track terminal-frame outcome so the
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

                                        let frame = Streaming.formatChunk json
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
                            // 0.1.16 — the dispose-await CANNOT use `do!` here
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

                        // 0.1.16 — emit per-method-completion telemetry now
                        // that the stream has terminated (success or error).
                        match streamingFailure with
                        | Some ex -> emitStreamingTelemetry (MethodOutcome.Failed ex)
                        | None -> emitStreamingTelemetry MethodOutcome.Succeeded
                    with setupEx ->
                        // 0.1.16 — setup-time exception (null funcObj, missing
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
                else
                    // 0.1.16 — correlation-id is now established above
                    // (covers both streaming and non-streaming branches).
                    // The non-streaming branch continues with body buffering
                    // + the pre-flight chain.

                    // 0.1.15 — unconditional buffering on the non-streaming
                    // branch. ASP.NET only allocates the buffer when the body
                    // is read more than once, so the cost is zero for the
                    // common single-read path AND every downstream stage
                    // (validation, audit emission, body-hash for idempotency,
                    // the proxy's own read) is freed from the
                    // "did EnableBuffering get called upstream?" hazard that
                    // caused audit emission to silently break when
                    // `BodyNormalisation = Disabled` in 0.1.14.
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

                    // 0.1.16 — lazy body cache built on a shared bytes
                    // cell. Materialised once on first downstream demand,
                    // then text + hash derive from the cached bytes
                    // without re-reading ctx.Request.Body (which the
                    // proxy's StreamReader will dispose later in the
                    // pipeline). The 0.1.15 fix read the body
                    // unconditionally for every non-streaming request
                    // even when no downstream stage needed it, costing
                    // ~32 MiB peak on the recommended multipart upload
                    // composition. Now: lazy AND survives the proxy's
                    // body disposal.
                    let cachedBodyBytesCell: byte[] option ref = ref None
                    let cachedBodyTextCell: string option ref = ref None
                    let cachedBodyHashCell: string option ref = ref None

                    let readCachedBodyBytes () = task {
                        match cachedBodyBytesCell.Value with
                        | Some bytes -> return bytes
                        | None ->
                            ctx.Request.Body.Position <- 0L

                            use ms = new System.IO.MemoryStream()
                            do! ctx.Request.Body.CopyToAsync ms
                            ctx.Request.Body.Position <- 0L
                            let bytes = ms.ToArray()
                            cachedBodyBytesCell.Value <- Some bytes
                            return bytes
                    }

                    let readCachedBodyText () = task {
                        match cachedBodyTextCell.Value with
                        | Some txt -> return txt
                        | None ->
                            let! bytes = readCachedBodyBytes ()
                            let txt = System.Text.Encoding.UTF8.GetString bytes
                            cachedBodyTextCell.Value <- Some txt
                            return txt
                    }

                    // 0.1.16 — body-hash is raw-bytes SHA-256, lazy, and
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
                        EndpointName = SubRouting.getNextPartOfPath ctx
                        Input = ctx.Request.Body
                        // Phase 69m — populated just before `proxy props` is
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

                    // Phase 69d — auth pre-flight. Lookup the method's classification,
                    // optionally resolve the auth context, evaluate, deny if needed.
                    //
                    // The methodName below is the trailing path segment (Phase 69b.C
                    // path). For the deny path we emit a categorised envelope via the
                    // 69b.E path; no telemetry is emitted on auth deny (the contract
                    // is per-method-completion — auth denial precedes method invocation).
                    let methodNameForAuth =
                        let p = props.EndpointName
                        let lastSlash = p.LastIndexOf '/'
                        if lastSlash >= 0 then p.Substring(lastSlash + 1) else p
                    // Auth pre-flight runs only when an AuthContextResolver is
                    // composed. Without one, every method is allowed (matches
                    // pre-69d behaviour for consumers who haven't opted in).
                    // The resolved auth context (when computed) is reused by
                    // Phase 69g's rate-limit pre-flight below — avoids a double
                    // resolver invocation per request.
                    //
                    // 0.1.15 — awaited via `let!` instead of sync-over-async
                    // `GetAwaiter().GetResult()`. The previous shape blocked a
                    // thread-pool worker for the full duration of the resolver
                    // (auth / idempotency / rate-limit ALL did this), starving
                    // the pool under any auth resolver that does network I/O
                    // (JWKS fetch, OIDC introspection, tenant lookup). Same
                    // promotion applied below to the idempotency lookup + the
                    // rate-limit evaluation.
                    let! resolvedAuthContextForRequest = task {
                        match options.AuthContextResolver with
                        | None -> return None
                        | Some resolver ->
                            let methodCls =
                                classifications |> Map.tryFind methodNameForAuth |> Option.defaultValue Public

                            match methodCls with
                            | Public -> return None
                            | _ ->
                                let! authCtx = resolver ctx |> Async.StartAsTask
                                return Some authCtx
                    }

                    let denyReason =
                        match options.AuthContextResolver with
                        | None -> None
                        | Some _ ->
                            let methodCls =
                                classifications |> Map.tryFind methodNameForAuth |> Option.defaultValue Public

                            let authDecision = AuthClassifier.evaluate methodCls resolvedAuthContextForRequest

                            match authDecision with
                            | Deny r -> Some r
                            | Allow -> None

                    if denyReason.IsSome then
                        // Phase 69d — deny path. Emit categorised Auth envelope
                        // *directly* without consulting the consumer's error
                        // handler — the auth contract is unconditional: classified
                        // methods that fail evaluation always categorise as `Auth`,
                        // regardless of what the consumer's exception-handler
                        // policy is. Status 401 + categorised envelope per
                        // Phase 69b.E. No telemetry — auth denial precedes the
                        // per-method-completion contract.
                        ctx.Response.StatusCode <- 401

                        let envelope =
                            Errors.categorisedWithSchema
                                options.SchemaVersion
                                ErrorCategory.Auth
                                (box (sprintf "%s: auth-denied: %s" methodNameForAuth denyReason.Value))

                        return! setJsonBody options.JsonSerializer envelope options.DiagnosticsLogger next ctx
                    else
                        // Phase 69e — typed validation pre-flight.
                        //
                        // Only runs when (a) the method's input type carries
                        // ValidationAttributes (cached at startup) AND (b) the
                        // serializer backend is STJ (the Validation engine uses
                        // STJ to parse the first argument; the Newtonsoft path
                        // is a follow-up for the legacy opt-in branch).
                        //
                        // Body-read pattern mirrors 69b.A: EnableBuffering once,
                        // read, restore position so the proxy reads fresh. The
                        // pre-read is non-destructive — the proxy sees the same
                        // bytes.
                        let validationInputType = validationInputTypes |> Map.tryFind methodNameForAuth

                        // Phase 69m — cache the first-arg value validation
                        // parses so the audit emission below can reuse it
                        // (when audit AND validation are armed for the same
                        // method). Saves a second `parseFirstArgFromBody`
                        // call per request on dual-armed methods.
                        let validationParsedFirstArg: obj option ref = ref None

                        // 0.1.16 — lazy body read: validation only forces
                        // the cache when its method actually has validation
                        // attributes AND the STJ backend is composed.
                        let! validationViolations = task {
                            match validationInputType, options.JsonSerializer with
                            | Some inputT, SystemTextJson stjOptions ->
                                let! bodyText = readCachedBodyText ()

                                match Validation.parseFirstArgFromBody bodyText inputT stjOptions with
                                | Some inputValue ->
                                    validationParsedFirstArg.Value <- Some inputValue
                                    return Validation.evaluate inputT inputValue
                                | None -> return []
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

                            // Phase 69f — idempotency pre-flight.
                            //
                            // Runs BEFORE rate-limit so cache hits don't decrement
                            // the budget (the original call already paid). When the
                            // method carries [<Idempotent>] AND a store is composed:
                            //   * Missing `X-Idempotency-Key` → ErrorCategory.User envelope.
                            //   * Cache hit → replay the cached response, skip handler + rate-limit.
                            //   * Cache miss → continue down the chain; capture+store
                            //                  the response on success after the handler.
                            let isMethodIdempotent = idempotentMethods.Contains methodNameForAuth

                            // 0.1.15 — RemoteIpResolver hook. When a consumer
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

                            // Phase 69f — nested early-returns via if/elif/else so every
                            // task arm produces a `Task<HttpContext option>`. The two
                            // early-return shapes (missing header → 400; cache hit → replay)
                            // both fully handle the response before yielding to `next`.
                            let idempotencyArmed = isMethodIdempotent && options.IdempotencyStore.IsSome

                            // 0.1.16 — force-materialise the body bytes
                            // eagerly when idempotency is armed for this
                            // method. The proxy's StreamReader disposes
                            // ctx.Request.Body after dispatch, so the
                            // store-path's body-hash lookup would fail
                            // with `ObjectDisposedException` if we
                            // waited until then. The lazy cache is still
                            // useful for non-idempotent methods (where
                            // validation / audit may not run at all) and
                            // for idempotent methods this just pulls the
                            // materialisation forward by a few lines.
                            if idempotencyArmed && idempotencyKey.IsSome then
                                let! _ = readCachedBodyBytes ()
                                ()

                            // 0.1.15 — async lookup via `let!` instead of
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

                            // 0.1.16 — body-hash mismatch detection is now
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
                                // 0.1.15 — same idempotency key reused with a
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
                                // Cache hit — replay verbatim, skip handler + every
                                // downstream step (rate-limit, telemetry, audit, store).
                                let response = cachedResponse.Value
                                ctx.Response.StatusCode <- response.StatusCode
                                ctx.Response.ContentType <- response.ContentType

                                ctx.Response.Headers["x-idempotency-replay"] <-
                                    Microsoft.Extensions.Primitives.StringValues "true"

                                do! ctx.Response.Body.WriteAsync(response.Body, 0, response.Body.Length)
                                return! next ctx
                            else
                                // Phase 69g — rate-limit pre-flight.
                                //
                                // Only runs when (a) an `IRateLimitStore` is composed AND
                                // (b) the method carries at least one [<RateLimit>] attribute.
                                // The subject key for the bucket is derived from the resolved
                                // auth context (when present) or "anonymous" otherwise.
                                let methodBudgets =
                                    rateLimits |> Map.tryFind methodNameForAuth |> Option.defaultValue []

                                // 0.1.15 — async evaluation via `let!` instead of
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



                                    // Phase 69b.C — wall-clock measurement for the telemetry hook.
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
                                    let methodName =
                                        let p = props.EndpointName
                                        let lastSlash = p.LastIndexOf '/'
                                        if lastSlash >= 0 then p.Substring(lastSlash + 1) else p

                                    // Phase 69l — telemetry-allocation gate. Skips the
                                    // Stopwatch + MethodTelemetry alloc + Map.ofList tags
                                    // entirely when the gate returns false (the canonical
                                    // `Api.make` + `NoMetricsEndpoint` shape — `IMetricsSink`
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
                                                }
                                            | None -> ()

                                    // Phase 69m — hand cached bytes to the proxy
                                    // when an upstream pre-flight stage forced
                                    // the cache. The proxy then parses from
                                    // bytes directly; no second stream read +
                                    // no second string materialisation.
                                    let propsWithCache = {
                                        props with
                                            InputBytes = cachedBodyBytesCell.Value
                                    }

                                    match! proxy propsWithCache with
                                    | Success isBinaryOutput ->
                                        ctx.Response.StatusCode <- 200

                                        if isBinaryOutput && isProxyHeaderPresent then
                                            ctx.Response.ContentType <- "application/octet-stream"
                                        elif options.ResponseSerialization.IsJson then
                                            ctx.Response.ContentType <- "application/json; charset=utf-8"
                                        else
                                            ctx.Response.ContentType <- "application/vnd.msgpack"

                                        // Phase 69f — capture response bytes before flushing
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

                                        // Phase 69f — store the captured response so the
                                        // next call with the same key replays it. Fire-and-
                                        // wait so the test gate observes deterministic state.
                                        if not (isNull capturedBody) then
                                            let scope = Idempotency.deriveScope subjectKey methodNameForAuth

                                            // 0.1.16 — body-hash is only
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

                                            do!
                                                options.IdempotencyStore.Value.Store(
                                                    idempotencyKey.Value,
                                                    scope,
                                                    response,
                                                    options.IdempotencyTtl
                                                )
                                                |> Async.StartAsTask

                                        // Phase 69h — audit emission after successful invocation.
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

                                            // 0.1.16 — audit payload via the
                                            // lazy body reader. First call
                                            // materialises the body string;
                                            // subsequent reads (validation,
                                            // idempotency mismatch) return
                                            // the cached value.
                                            //
                                            // Phase 69m — when validation
                                            // parsed the first-arg value
                                            // earlier in the same request,
                                            // reuse the cached value instead
                                            // of re-running `parseFirstArgFromBody`
                                            // a second time.
                                            let! payload = task {
                                                match validationInputTypes |> Map.tryFind methodName with
                                                | Some inputT ->
                                                    match validationParsedFirstArg.Value with
                                                    | Some v -> return Audit.payloadFromInputRecord inputT v
                                                    | None ->
                                                        let (SystemTextJson stjOpts) = options.JsonSerializer
                                                        let! bodyText = readCachedBodyText ()

                                                        match
                                                            Validation.parseFirstArgFromBody bodyText inputT stjOpts
                                                        with
                                                        | Some v -> return Audit.payloadFromInputRecord inputT v
                                                        | None -> return Map.empty
                                                | None -> return Map.empty
                                            }

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
                                        // 0.1.16 — categorise multipart-cap
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

                                            // 0.5.13 — unconditional stderr breadcrumb.
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
                                    | InvalidHttpVerb -> return halt
                                    | EndpointNotFound -> return! notFound options next ctx
            }

    /// Phase 69n — backwards-compatible shim. Preserves the pre-69n
    /// signature `buildFromImplementation implBuilder options : HttpHandler`
    /// for the `StaticValue` / `FromContext` paths in `buildHttpHandler`.
    /// Behaviour-equivalent to `buildDispatcherTable options implBuilder`.
    let buildFromImplementation<'impl>
        (implBuilder: HttpContext -> 'impl)
        (options: RemotingOptions<HttpContext, 'impl>)
        : HttpHandler =
        buildDispatcherTable options implBuilder

module Remoting =

    // 0.1.16 — once-per-type registry so the Empty-impl warning
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
            // 0.1.16 — once-per-process warning. The Giraffe adapter
            // supports the full from* surface, so the warning lists
            // every option.
            warnEmptyOnce
                typeof<'t>.FullName
                options.DiagnosticsLogger
                "ToolUp.Remoting (Giraffe): composed with Empty implementation; no routes will dispatch. This is almost always a misconfiguration — pipe through Remoting.fromValue / .fromContext / .fromContextAsync to register a real impl."

            fun _ _ -> skipPipeline
        | StaticValue impl -> GiraffeUtil.buildFromImplementation (fun _ -> impl) options
        | FromContext createImplementationFrom -> GiraffeUtil.buildFromImplementation createImplementationFrom options
        | FromContextAsync createImplementationFromAsync ->
            // Phase 69b.B — await the async resolver per request, then dispatch
            // through the sync path with the resolved impl. The resolver is
            // invoked on every request, not snapshotted at composition time.
            //
            // Phase 69n — build the dispatcher table ONCE at compose time
            // (proxy + classifier maps + rmsManager + guards), then per
            // request just await the async resolver and hand the result to
            // the pre-built dispatcher. Pre-69n this branch called
            // `buildFromImplementation` per request, rebuilding the entire
            // dispatcher substrate every time.
            let dispatch = GiraffeUtil.buildDispatcherTable options

            fun (next: HttpFunc) (ctx: HttpContext) -> task {
                let! impl = createImplementationFromAsync ctx |> Async.StartAsTask
                return! dispatch (fun _ -> impl) next ctx
            }
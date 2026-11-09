namespace ToolUp.Remoting.Server.Stages

open System.Threading.Tasks
open ToolUp.Remoting.Server

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

/// Stage 3: a streaming or long-running call (or a companion) reached through
/// a batch envelope. The batch route refuses such an envelope before running
/// anything; this is the backstop for a route it could not see, and it
/// refuses the element (400) rather than serve a shape the envelope's
/// `{ status, body }` answer cannot carry.
[<RequireQualifiedAccess>]
module internal BatchBackstop =

    let private unbatchable (target: Target) : bool =
        match target with
        | Target.Companion _ -> true
        | Target.Method m ->
            match m.Shape with
            | MethodShape.Unary -> false
            | MethodShape.Streaming _
            | MethodShape.LongRunning _ -> true
        | Target.Foreign -> false

    let stage: Stage =
        fun mount ctx ->
            if ctx.Http.Items.ContainsKey BatchElement.ItemsKey && unbatchable ctx.Target then
                let payload =
                    box {|
                        error = BatchElement.RefusalCode
                        methodName = ctx.Addressed
                        reason = "a streaming or long-running method is never batched"
                    |}

                let envelope =
                    Errors.categorisedWithSchema mount.SchemaVersion ErrorCategory.User payload

                Task.FromResult(StageOutcome.Answer(ctx, Response.Json(400, box envelope)))
            else
                Task.FromResult(StageOutcome.Continue ctx)

namespace ToolUp.Remoting.Giraffe

open Microsoft.AspNetCore.Http
open System.Threading.Tasks
open ToolUp.Remoting.Server.Stages

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
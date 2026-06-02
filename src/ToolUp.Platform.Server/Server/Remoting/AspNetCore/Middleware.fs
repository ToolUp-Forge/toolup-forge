namespace ToolUp.Remoting.AspNetCore

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

open System.IO
open System.Threading.Tasks
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Proxy

type HttpFuncResult = Task<HttpContext option>
type HttpFunc = HttpContext -> HttpFuncResult
type HttpHandler = HttpFunc -> HttpFunc

[<AutoOpen>]
module Extensions =
    type HttpContext with
        member self.GetService<'t>() =
            self.RequestServices.GetService(typeof<'t>) :?> 't


/// The parts from Giraffe needed to simplify the middleware implementation
module internal Middleware =

    let writeStringAsync (input: string) (ctx: HttpContext) (logger: Option<string -> unit>) = task {
        Diagnostics.outputPhase logger input
        let bytes = System.Text.Encoding.UTF8.GetBytes(input)
        do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
        return Some ctx
    }

    let text (input: string) =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let bytes = System.Text.Encoding.UTF8.GetBytes(input)
            do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
            return Some ctx
        }

    let compose (handler1: HttpHandler) (handler2: HttpHandler) : HttpHandler =
        fun (final: HttpFunc) ->
            let func = final |> handler2 |> handler1

            fun (ctx: HttpContext) ->
                match ctx.Response.HasStarted with
                | true -> final ctx
                | false -> func ctx

    let (>=>) = compose

    let setResponseBody (backend: JsonSerializerBackend) (response: obj) logger : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            use ms = new MemoryStream()
            jsonSerializeWithBackend backend response ms
            let responseBody = System.Text.Encoding.UTF8.GetString(ms.ToArray())
            return! writeStringAsync responseBody ctx logger
        }

    /// Sets the content type of the Http response
    let setContentType (contentType: string) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            ctx.Response.ContentType <- contentType
            return Some ctx
        }

    /// Sets the body of the response to type of JSON
    let setBody backend value logger : HttpHandler =
        setResponseBody backend value logger
        >=> setContentType "application/json; charset=utf-8"

    /// Used to forward of the Http context
    let halt: HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> Task.FromResult None

    let fail (ex: exn) (routeInfo: RouteInfo<HttpContext>) (options: RemotingOptions<HttpContext, 't>) : HttpHandler =
        let logger = options.DiagnosticsLogger
        let backend = options.JsonSerializer

        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            match options.ErrorHandler with
            | None -> return! setBody backend (Errors.unhandled routeInfo.methodName) logger next ctx
            | Some errorHandler ->
                match errorHandler ex routeInfo with
                | Ignore -> return! setBody backend (Errors.ignored routeInfo.methodName) logger next ctx
                | Propagate error -> return! setBody backend (Errors.propagated error) logger next ctx
                | PropagateCategorised(category, error) ->
                    return! setBody backend (Errors.categorised category error) logger next ctx
        }

    let notFound (options: RemotingOptions<HttpContext, 'impl>) next (ctx: HttpContext) =
        match ctx.Request.Method.ToUpper(), options.Docs with
        | "GET", (Some docsUrl, Some docs) when docsUrl = ctx.Request.Path.Value ->
            let (Documentation(docsName, docsRoutes)) = docs
            let schema = Docs.makeDocsSchema typeof<'impl> docs options.RouteBuilder
            let docsApp = DocsApp.embedded docsName docsUrl schema
            (text docsApp >=> setContentType "text/html") next ctx
        | "OPTIONS", (Some docsUrl, Some docs) when
            sprintf "/%s/$schema" docsUrl = ctx.Request.Path.Value
            || sprintf "%s/$schema" docsUrl = ctx.Request.Path.Value
            ->
            let schema = Docs.makeDocsSchema typeof<'impl> docs options.RouteBuilder
            let serializedSchema = schema.ToJsonString()
            text serializedSchema next ctx
        | _ -> halt next ctx

    /// Fail-fast: the AspNetCore middleware adapter is parity-incomplete
    /// with Giraffe — the Phase 69b–69k seams (telemetry, auth-classifier
    /// enforcement, rate-limit, idempotency, audit, schema-version
    /// stamping, streaming dispatch) ship as Giraffe-only today. Composing
    /// them against the AspNetCore middleware would silently no-op, routing
    /// around the declared guards. Refuse at compose time and direct the
    /// consumer at Giraffe until per-adapter parity ships.
    let private refuseUnsupportedSeams (options: RemotingOptions<'ctx, 'impl>) : unit =
        let composed = [
            if options.Telemetry.IsSome then
                yield "withTelemetry"
            if options.AuthContextResolver.IsSome then
                yield "withAuthContext"
            if options.RateLimitStore.IsSome then
                yield "withRateLimitStore"
            if options.IdempotencyStore.IsSome then
                yield "withIdempotencyStore"
            if options.AuditEmitter.IsSome then
                yield "withAudit"
            if options.SchemaVersion <> 1 then
                yield (sprintf "withSchemaVersion(%d)" options.SchemaVersion)
        ]

        if not (List.isEmpty composed) then
            invalidOp (
                sprintf
                    "ToolUp.Remoting.AspNetCore refused to start: the seams [%s] are composed but the AspNetCore middleware adapter doesn't enforce them today (Phase 69b–69k seams ship Giraffe-only in v0). Silently honouring them would route around the declared guards. Switch the host to the Giraffe adapter, or drop these seams from the composition until per-adapter parity ships."
                    (String.concat "; " composed)
            )

    let buildFromImplementation<'impl>
        (implBuilder: HttpContext -> 'impl)
        (options: RemotingOptions<HttpContext, 'impl>)
        =
        refuseUnsupportedSeams options
        let proxy = makeApiProxy options
        let rmsManager = getRecyclableMemoryStreamManager options

        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let isProxyHeaderPresent = ctx.Request.Headers.ContainsKey "x-remoting-proxy"
            use output = rmsManager.GetStream "remoting-output-stream"

            let props = {
                ImplementationBuilder = (fun () -> implBuilder ctx)
                EndpointName = ctx.Request.Path.Value
                Input = ctx.Request.Body
                IsProxyHeaderPresent = isProxyHeaderPresent
                HttpVerb = ctx.Request.Method
                InputContentType = ctx.Request.ContentType
                Output = output
            }

            match! proxy props with
            | Success isBinaryOutput ->
                ctx.Response.StatusCode <- 200

                if isBinaryOutput && isProxyHeaderPresent then
                    ctx.Response.ContentType <- "application/octet-stream"
                elif options.ResponseSerialization.IsJson then
                    ctx.Response.ContentType <- "application/json; charset=utf-8"
                else
                    ctx.Response.ContentType <- "application/vnd.msgpack"

                do! output.CopyToAsync ctx.Response.Body
                return! next ctx
            | Exception(e, functionName, requestBodyText) ->
                ctx.Response.StatusCode <- 500

                let routeInfo = {
                    methodName = functionName
                    path = ctx.Request.Path.ToString()
                    httpContext = ctx
                    requestBodyText = requestBodyText
                }

                return! fail e routeInfo options next ctx
            | InvalidHttpVerb -> return! halt next ctx
            | EndpointNotFound -> return! notFound options next ctx
        }

type RemotingMiddleware<'t>(next: RequestDelegate, handler: HttpFunc) =

    do
        if isNull next then
            nullArg "next"

    member __.Invoke(ctx: HttpContext) = task {
        let! result = handler ctx

        if (result.IsNone) then
            return! next.Invoke ctx
    }

// 0.1.16 — once-per-type warning registry for the Empty-impl
// diagnostic. Keyed on `typeof<'t>.FullName` so two unrelated API
// records each emit their own one-shot warning.
module internal AspNetCoreWarnings =
    let private emptyWarningOnce =
        System.Collections.Concurrent.ConcurrentDictionary<string, unit>()

    let warnEmptyOnce (typeKey: string) (logger: Option<string -> unit>) (message: string) =
        if emptyWarningOnce.TryAdd(typeKey, ()) then
            Diagnostics.outputPhase logger message

[<AutoOpen>]
module AppBuilderExtensions =
    type IApplicationBuilder with
        member this.UseRemoting(options: RemotingOptions<HttpContext, 't>) =
            let handler =
                match options.Implementation with
                | Empty ->
                    // 0.1.16 — once-per-process Empty-impl warning,
                    // omitting `.fromContextAsync` since AspNetCore
                    // doesn't support it yet.
                    AspNetCoreWarnings.warnEmptyOnce
                        typeof<'t>.FullName
                        options.DiagnosticsLogger
                        "ToolUp.Remoting (AspNetCore): composed with Empty implementation; no routes will dispatch. This is almost always a misconfiguration — pipe through Remoting.fromValue / .fromContext to register a real impl."

                    Middleware.halt
                | StaticValue impl -> Middleware.buildFromImplementation (fun _ -> impl) options
                | FromContext createImplementationFrom ->
                    Middleware.buildFromImplementation createImplementationFrom options
                | FromContextAsync _ ->
                    // Phase 69b.B ships the FromContextAsync shape on the
                    // Giraffe adapter (forge's host). A clean AspNetCore port
                    // — threading the async resolver through the middleware
                    // shape — is a follow-up. Use the Giraffe adapter or
                    // fall back to Remoting.fromContext for sync resolution.
                    raise (
                        System.NotSupportedException(
                            "Remoting.fromContextAsync is not yet supported by the AspNetCore middleware — use the Giraffe adapter, or fall back to Remoting.fromContext for sync resolution. Follow up: https://github.com/ToolUp-Forge/toolup-remoting/issues"
                        )
                    )

            this.UseMiddleware<RemotingMiddleware<'t>>(handler (Some >> Task.FromResult))
            |> ignore
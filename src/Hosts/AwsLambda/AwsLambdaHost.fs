// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Hosts.AwsLambda

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Amazon.Lambda.APIGatewayEvents
open Amazon.Lambda.ApplicationLoadBalancerEvents
open ToolUp.Platform

// ─── ToolUp.Hosts.AwsLambda — AWS Lambda adapter ─────────────────────
//
// Phase 16 host-adapter companion. Bridges AWS Lambda invocations
// through `IServerHost.Invoke` so a compose-built ToolUp deployment
// runs on Lambda (API Gateway REST v1, HTTP API v2, ALB, or Lambda
// Function URLs) without touching its handler code. Same code-path as
// Kestrel; only the host driver differs.
//
// **Three trigger shapes, three bridges.**
//
// API Gateway HTTP API v2 (and Lambda Function URLs, which use the
// same payload shape) is the most common path; bridges named *V2.
// API Gateway REST API v1 and Application Load Balancer use distinct
// event types with multi-value-header semantics; bridges named *V1
// and *Alb respectively. Each bridge is typed against its event
// shape so the consumer's Lambda handler doesn't have to box.
//
// **Usage in a consumer Lambda project.** A `Function.fs` with one
// handler per event shape the deployment fronts:
//
//     module Function
//
//     open Amazon.Lambda.Core
//     open Amazon.Lambda.APIGatewayEvents
//     open Amazon.Lambda.RuntimeSupport
//     open Amazon.Lambda.Serialization.SystemTextJson
//     open ToolUp.Hosts.AwsLambda
//     open ToolUp.Platform
//
//     // Compose the SDK once at cold-start; reused for every invocation.
//     let serverHost: IServerHost =
//         ServerApp.empty
//         |> ServerApp.withConfig {
//             ServerConfig.defaults with
//                 Mode = Anonymous
//                 ServerlessHost = ServerlessHost
//                 JobScheduler = NoJobScheduler
//                 Webhooks = NoWebhooks
//                 Notifications = NoNotificationsExplicit
//                 AuditLog = NoAuditLog
//                 UsageMetering = NoUsageMetering
//                 HealthStateTracking = false
//         }
//         |> ServerApp.addModule (myModule.register ())
//         |> ServerApp.composeOnly   // hypothetical helper returning IServerHost
//
//     // Start the host once at cold-start (no-op when every
//     // IHostedService is gated off, but ASP.NET Core internals need
//     // a started host to resolve logger factory / config root).
//     do serverHost.Host.StartAsync(System.Threading.CancellationToken.None).Wait()
//
//     let handlerV2 (req: APIGatewayHttpApiV2ProxyRequest) (_ctx: ILambdaContext) =
//         AwsLambdaHost.bridgeV2 (serverHost, req)
//
//     [<EntryPoint>]
//     let main _ =
//         LambdaBootstrapBuilder
//             .Create(handlerV2, DefaultLambdaJsonSerializer())
//             .Build()
//             .RunAsync()
//             .Wait()
//         0
//
// **Cold-start contract.** `IServerHost.Host.StartAsync()` is called
// once at cold-start (above), not on every invocation — that would
// be O(n) work per request and would re-run middleware-graph
// construction. `ServerlessHost = ServerlessHost` deployments gate
// every `IHostedService` off so `StartAsync` is effectively free;
// the call still has to happen for ASP.NET Core's internal services
// (logger factory, config root, options) to initialise.
//
// **Body encoding.** Lambda event types carry `Body : string` plus
// `IsBase64Encoded : bool`. Request bodies are decoded per the flag
// (base64 → bytes for binary uploads, UTF-8 → bytes otherwise).
// Response bodies are encoded as text when the `Content-Type` is
// JSON / XML / text / *+json / *+xml, base64 otherwise — matches
// the Amazon.Lambda.AspNetCoreServer text-detection heuristic.

[<AbstractClass; Sealed>]
type AwsLambdaHost =

    // ── Shared helpers ────────────────────────────────────────────

    /// Decide whether a Content-Type header value should be returned
    /// to Lambda as text (UTF-8 body, `IsBase64Encoded = false`) or
    /// as base64 (binary body, `IsBase64Encoded = true`).
    static member private isTextContentType(contentType: string) : bool =
        if String.IsNullOrEmpty contentType then
            false
        else
            let mediaType = contentType.Split(';').[0].Trim().ToLowerInvariant()

            mediaType.StartsWith "text/"
            || mediaType = "application/json"
            || mediaType = "application/xml"
            || mediaType = "application/javascript"
            || mediaType = "application/x-www-form-urlencoded"
            || mediaType.EndsWith "+json"
            || mediaType.EndsWith "+xml"

    /// Decode a Lambda event body (possibly base64-encoded) to a
    /// `MemoryStream` suitable for mounting on `HttpRequest.Body`.
    static member private decodeBody(body: string, isBase64Encoded: bool) : MemoryStream =
        if String.IsNullOrEmpty body then
            new MemoryStream()
        elif isBase64Encoded then
            new MemoryStream(Convert.FromBase64String body)
        else
            new MemoryStream(Encoding.UTF8.GetBytes body)

    /// Build a `DefaultHttpContext` skeleton populated with the
    /// `IServerHost`'s `RequestServices` and a `MemoryStream`-backed
    /// response body. Per-trigger callers fill in method / path /
    /// headers / body on top.
    static member private buildContext(host: IServerHost) : HttpContext =
        let ctx = DefaultHttpContext()

        // Wire the configured WebApplication's service provider into
        // the HttpContext so middleware that resolves services via
        // `HttpContext.RequestServices` (SDK per-request logger /
        // metrics sink lookup) gets the same singletons DI hands out.
        match host.App with
        | Some app -> ctx.RequestServices <- app.Services
        | None -> ()

        // Response body buffer — MemoryStream so post-invoke we can
        // extract bytes and encode to text/base64 per Content-Type.
        ctx.Response.Body <- new MemoryStream()

        ctx

    /// Read the populated `HttpContext.Response.Body` MemoryStream
    /// and encode as `(bodyString, isBase64Encoded)` per the
    /// Content-Type heuristic.
    static member private encodeResponseBody(ctx: HttpContext) : string * bool =
        let stream = ctx.Response.Body :?> MemoryStream
        let bytes = stream.ToArray()

        if bytes.Length = 0 then
            "", false
        elif AwsLambdaHost.isTextContentType ctx.Response.ContentType then
            Encoding.UTF8.GetString bytes, false
        else
            Convert.ToBase64String bytes, true

    // ── API Gateway HTTP API v2 / Lambda Function URLs ────────────

    /// Translate an HTTP API v2 (or Function URL) event into an
    /// `HttpContext` suitable for driving through `IServerHost.Invoke`.
    /// Copies method / path / query / headers / cookies / body
    /// verbatim. The returned `HttpContext` is owned by the caller;
    /// its `Response` is read by `bridgeV2` after `Invoke` returns.
    static member toHttpContextV2(req: APIGatewayHttpApiV2ProxyRequest, host: IServerHost) : HttpContext =
        let ctx = AwsLambdaHost.buildContext host
        let request = ctx.Request

        request.Method <- req.RequestContext.Http.Method
        request.Scheme <- "https"
        request.Host <- HostString(req.RequestContext.DomainName)
        request.PathBase <- PathString ""

        request.Path <-
            if String.IsNullOrEmpty req.RawPath then
                PathString "/"
            else
                PathString req.RawPath

        request.QueryString <-
            if String.IsNullOrEmpty req.RawQueryString then
                QueryString.Empty
            else
                QueryString("?" + req.RawQueryString)

        // v2 Headers are already comma-joined per RFC; copy as
        // single StringValues per key. Case-insensitive in API
        // Gateway; HttpRequest.Headers is case-insensitive too.
        if not (isNull req.Headers) then
            for KeyValue(name, value) in req.Headers do
                request.Headers[name] <- StringValues value

        // v2 carries cookies in a dedicated array (one entry per
        // cookie). ASP.NET Core's cookie middleware reads from the
        // standard `Cookie` request header, so synthesise it when
        // the event populates `Cookies` but no `Cookie` header.
        if not (isNull req.Cookies) && req.Cookies.Length > 0 then
            let existing = request.Headers["Cookie"]

            if StringValues.IsNullOrEmpty existing then
                request.Headers["Cookie"] <- StringValues(String.Join("; ", req.Cookies))

        request.Body <- AwsLambdaHost.decodeBody (req.Body, req.IsBase64Encoded)

        ctx

    /// Write the post-invoke `HttpContext.Response` back to an
    /// `APIGatewayHttpApiV2ProxyResponse`. Status code, headers
    /// (comma-joined per v2 convention), cookies (separated into
    /// the dedicated `Cookies` array), and body (text/base64 per
    /// Content-Type) all round-trip.
    static member toResponseV2(ctx: HttpContext) : APIGatewayHttpApiV2ProxyResponse =
        let body, isBase64 = AwsLambdaHost.encodeResponseBody ctx

        let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let cookies = ResizeArray<string>()

        for KeyValue(name, values) in ctx.Response.Headers do
            if String.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase) then
                for v in values do
                    if not (isNull v) then
                        cookies.Add v
            else
                // v2 expects multi-value headers joined with commas
                // (RFC 7230 §3.2.2 — except Set-Cookie, handled above).
                headers[name] <- String.Join(",", values.ToArray())

        APIGatewayHttpApiV2ProxyResponse(
            StatusCode = ctx.Response.StatusCode,
            Headers = headers,
            Cookies = cookies.ToArray(),
            Body = body,
            IsBase64Encoded = isBase64
        )

    /// Bridge a Lambda HTTP API v2 / Function URL invocation through
    /// the SDK's configured request pipeline. Called per cloud
    /// invocation from the consumer's Lambda handler.
    static member bridgeV2
        (host: IServerHost, req: APIGatewayHttpApiV2ProxyRequest)
        : Task<APIGatewayHttpApiV2ProxyResponse> =
        task {
            let ctx = AwsLambdaHost.toHttpContextV2 (req, host)
            do! host.Invoke ctx
            return AwsLambdaHost.toResponseV2 ctx
        }

    // ── API Gateway REST API v1 ───────────────────────────────────

    /// Translate an API Gateway REST API v1 event into an
    /// `HttpContext` suitable for driving through `IServerHost.Invoke`.
    /// v1 carries headers as both single-value `Headers` and
    /// multi-value `MultiValueHeaders` — we read the multi-value
    /// form preferentially and fall back to single-value.
    static member toHttpContextV1(req: APIGatewayProxyRequest, host: IServerHost) : HttpContext =
        let ctx = AwsLambdaHost.buildContext host
        let request = ctx.Request

        request.Method <- req.HttpMethod
        request.Scheme <- "https"
        request.PathBase <- PathString ""

        // Host header carries the API Gateway custom domain or
        // execute-api.<region>.amazonaws.com; populate from headers.
        let hostHeader =
            if not (isNull req.MultiValueHeaders) && req.MultiValueHeaders.ContainsKey "Host" then
                req.MultiValueHeaders["Host"] |> Seq.tryHead |> Option.defaultValue ""
            elif not (isNull req.Headers) && req.Headers.ContainsKey "Host" then
                req.Headers["Host"]
            else
                ""

        if not (String.IsNullOrEmpty hostHeader) then
            request.Host <- HostString hostHeader

        request.Path <-
            if String.IsNullOrEmpty req.Path then
                PathString "/"
            else
                PathString req.Path

        // Build query string from multi-value parameters when
        // available; fall back to single-value.
        let queryParts = ResizeArray<string>()

        if
            not (isNull req.MultiValueQueryStringParameters)
            && req.MultiValueQueryStringParameters.Count > 0
        then
            for KeyValue(name, values) in req.MultiValueQueryStringParameters do
                for v in values do
                    queryParts.Add(Uri.EscapeDataString name + "=" + Uri.EscapeDataString v)
        elif not (isNull req.QueryStringParameters) && req.QueryStringParameters.Count > 0 then
            for KeyValue(name, value) in req.QueryStringParameters do
                queryParts.Add(Uri.EscapeDataString name + "=" + Uri.EscapeDataString value)

        request.QueryString <-
            if queryParts.Count = 0 then
                QueryString.Empty
            else
                QueryString("?" + String.Join("&", queryParts))

        // Headers — prefer MultiValueHeaders when present, fall back
        // to single-value Headers. API Gateway populates one or the
        // other depending on the integration's payload-format flag.
        if not (isNull req.MultiValueHeaders) then
            for KeyValue(name, values) in req.MultiValueHeaders do
                request.Headers[name] <- StringValues(values |> Seq.toArray)

        if not (isNull req.Headers) then
            for KeyValue(name, value) in req.Headers do
                if not (request.Headers.ContainsKey name) then
                    request.Headers[name] <- StringValues value

        request.Body <- AwsLambdaHost.decodeBody (req.Body, req.IsBase64Encoded)

        ctx

    /// Write the post-invoke `HttpContext.Response` back to an
    /// `APIGatewayProxyResponse`. v1 expects multi-value headers
    /// in the dedicated `MultiValueHeaders` dictionary (single-value
    /// `Headers` is populated as well for clients that don't read
    /// the multi-value form).
    static member toResponseV1(ctx: HttpContext) : APIGatewayProxyResponse =
        let body, isBase64 = AwsLambdaHost.encodeResponseBody ctx

        let singleValueHeaders =
            Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        let multiValueHeaders =
            Dictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase)

        for KeyValue(name, values) in ctx.Response.Headers do
            let arr = values.ToArray()

            if arr.Length > 0 then
                singleValueHeaders[name] <- arr[0]
                multiValueHeaders[name] <- ResizeArray<string>(arr) :> IList<string>

        APIGatewayProxyResponse(
            StatusCode = ctx.Response.StatusCode,
            Headers = singleValueHeaders,
            MultiValueHeaders = multiValueHeaders,
            Body = body,
            IsBase64Encoded = isBase64
        )

    /// Bridge a Lambda API Gateway REST API v1 invocation through
    /// the SDK's configured request pipeline.
    static member bridgeV1(host: IServerHost, req: APIGatewayProxyRequest) : Task<APIGatewayProxyResponse> = task {
        let ctx = AwsLambdaHost.toHttpContextV1 (req, host)
        do! host.Invoke ctx
        return AwsLambdaHost.toResponseV1 ctx
    }

    // ── Application Load Balancer ─────────────────────────────────

    /// Translate an ALB target-group event into an `HttpContext`.
    /// ALB events match the v1 REST shape closely but with their
    /// own type. The same multi-value-vs-single-value duality
    /// applies — populated per the target group's
    /// `lambda.multi_value_headers.enabled` flag.
    static member toHttpContextAlb(req: ApplicationLoadBalancerRequest, host: IServerHost) : HttpContext =
        let ctx = AwsLambdaHost.buildContext host
        let request = ctx.Request

        request.Method <- req.HttpMethod
        request.Scheme <- "https"
        request.PathBase <- PathString ""

        let hostHeader =
            if not (isNull req.MultiValueHeaders) && req.MultiValueHeaders.ContainsKey "host" then
                req.MultiValueHeaders["host"] |> Seq.tryHead |> Option.defaultValue ""
            elif not (isNull req.Headers) && req.Headers.ContainsKey "host" then
                req.Headers["host"]
            else
                ""

        if not (String.IsNullOrEmpty hostHeader) then
            request.Host <- HostString hostHeader

        request.Path <-
            if String.IsNullOrEmpty req.Path then
                PathString "/"
            else
                PathString req.Path

        let queryParts = ResizeArray<string>()

        if
            not (isNull req.MultiValueQueryStringParameters)
            && req.MultiValueQueryStringParameters.Count > 0
        then
            for KeyValue(name, values) in req.MultiValueQueryStringParameters do
                for v in values do
                    queryParts.Add(Uri.EscapeDataString name + "=" + Uri.EscapeDataString v)
        elif not (isNull req.QueryStringParameters) && req.QueryStringParameters.Count > 0 then
            for KeyValue(name, value) in req.QueryStringParameters do
                queryParts.Add(Uri.EscapeDataString name + "=" + Uri.EscapeDataString value)

        request.QueryString <-
            if queryParts.Count = 0 then
                QueryString.Empty
            else
                QueryString("?" + String.Join("&", queryParts))

        if not (isNull req.MultiValueHeaders) then
            for KeyValue(name, values) in req.MultiValueHeaders do
                request.Headers[name] <- StringValues(values |> Seq.toArray)

        if not (isNull req.Headers) then
            for KeyValue(name, value) in req.Headers do
                if not (request.Headers.ContainsKey name) then
                    request.Headers[name] <- StringValues value

        request.Body <- AwsLambdaHost.decodeBody (req.Body, req.IsBase64Encoded)

        ctx

    /// Write the post-invoke `HttpContext.Response` back to an
    /// `ApplicationLoadBalancerResponse`. ALB also accepts the
    /// `StatusDescription` field; we populate it from the status
    /// code's reason phrase (ASP.NET Core does not surface a
    /// reason phrase, so we fall back to the numeric code).
    static member toResponseAlb(ctx: HttpContext) : ApplicationLoadBalancerResponse =
        let body, isBase64 = AwsLambdaHost.encodeResponseBody ctx

        let singleValueHeaders =
            Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        let multiValueHeaders =
            Dictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase)

        for KeyValue(name, values) in ctx.Response.Headers do
            let arr = values.ToArray()

            if arr.Length > 0 then
                singleValueHeaders[name] <- arr[0]
                multiValueHeaders[name] <- ResizeArray<string>(arr) :> IList<string>

        ApplicationLoadBalancerResponse(
            StatusCode = ctx.Response.StatusCode,
            StatusDescription = string ctx.Response.StatusCode,
            Headers = singleValueHeaders,
            MultiValueHeaders = multiValueHeaders,
            Body = body,
            IsBase64Encoded = isBase64
        )

    /// Bridge a Lambda ALB target-group invocation through the
    /// SDK's configured request pipeline.
    static member bridgeAlb
        (host: IServerHost, req: ApplicationLoadBalancerRequest)
        : Task<ApplicationLoadBalancerResponse> =
        task {
            let ctx = AwsLambdaHost.toHttpContextAlb (req, host)
            do! host.Invoke ctx
            return AwsLambdaHost.toResponseAlb ctx
        }
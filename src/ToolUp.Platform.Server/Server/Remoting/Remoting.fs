namespace ToolUp.Remoting.Server

module Remoting =

    /// Cached default System.Text.Json options for the new default serializer
    /// path. Built once at module init — registering the converter set against
    /// a fresh JsonSerializerOptions does ~24 reflection-driven Add calls, so
    /// allocating per createApi() would be wasteful for app patterns that
    /// build multiple APIs in a loop (test harnesses, per-tenant proxies, etc).
    /// Once any serializer call has used these options STJ freezes them, which
    /// is also fine for shared use — every caller gets the same byte-compat
    /// converter registration.
    let private defaultStjOptions =
        ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

    let documentation (name: string) (routes: RouteDocs list) : Documentation = Documentation(name, routes)

    /// Starts with the default configuration for building an API.
    ///
    /// **JSON serializer: System.Text.Json + ToolUp.Remoting.Json.SystemTextJson
    /// FableConverters**. Wire format is the Fable.SimpleJson-compatible shape
    /// (PascalCase fields, DU as `{"Case": ...}` / `{"CaseName": [fields]}`,
    /// Option as inner-value-or-null). Verified by byte-pin tests in the
    /// Remoting suite.
    let createApi () = {
        Implementation = Empty
        RouteBuilder = sprintf "/%s/%s"
        ErrorHandler = None
        DiagnosticsLogger = None
        Docs = None, None
        ResponseSerialization = Json
        JsonSerializer = SystemTextJson defaultStjOptions
        RmsManager = None
        BodyNormalisation = Enabled
        Telemetry = None
        TelemetryGate = None
        AuthContextResolver = None
        RateLimitStore = None
        AuditEmitter = None
        RequireAuditOnRoleGated = false
        IdempotencyStore = None
        IdempotencyTtl = System.TimeSpan.FromHours 1.0
        ValidationMessages = None
        SchemaVersion = 1
        MaxMultipartSectionBytes = 16L * 1024L * 1024L // 16 MiB
        MaxMultipartSections = 64
        RemoteIpResolver = None
    }

    /// Defines how routes are built using the type name and method name. By default, the generated routes are of the form `/typeName/methodName`.
    let withRouteBuilder builder options = { options with RouteBuilder = builder }

    /// Enables the diagnostics logger that will log what steps the library is taking when a request comes in. This could help troubleshoot serialization issues but it could be also be interesting to see what is going on under the hood.
    let withDiagnosticsLogger logger options = {
        options with
            DiagnosticsLogger = Some logger
    }

    /// Enables the automatic generation of API documentation based on type-metadata
    let withDocs (url: string) (docs: Documentation) options = {
        options with
            Docs = Some url, Some docs
    }

    /// Enables you to define a custom error handler for unhandled exceptions thrown by your remote functions. It can also be used for logging purposes or if you wanted to propagate errors back to client.
    let withErrorHandler handler options = {
        options with
            ErrorHandler = Some handler
    }

    /// Specifies that the API only uses binary serialization
    let withBinarySerialization (options: RemotingOptions<'t, 'implementation>) = {
        options with
            ResponseSerialization = MessagePack
    }

    /// Override the System.Text.Json options used by this API.
    ///
    /// `Remoting.createApi()` already defaults to a cached
    /// `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create()` instance
    /// — use this helper only when you need a customised `JsonSerializerOptions`
    /// (e.g. additional converters, different `WriteIndented`, a stricter
    /// encoder). The byte-compatible converter set is registered automatically
    /// by `FableConverters.addTo` / `FableConverters.create()`; if you pass an
    /// `options` instance you constructed yourself, call `addTo` first to
    /// ensure the Fable wire shape is preserved.
    ///
    /// ```fsharp
    /// open ToolUp.Remoting.Server
    /// open ToolUp.Remoting.Json.SystemTextJson
    ///
    /// let myOptions = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
    /// FableConverters.addTo myOptions
    ///
    /// let api =
    ///     Remoting.createApi()
    ///     |> Remoting.fromValue myImpl
    ///     |> Remoting.withSerializerOptions myOptions
    /// ```
    let withSerializerOptions
        (jsonOptions: System.Text.Json.JsonSerializerOptions)
        (options: RemotingOptions<'t, 'implementation>)
        =
        {
            options with
                JsonSerializer = SystemTextJson jsonOptions
        }

    /// Enables you to provide your own instance of a recyclable memory stream manager
    let withRecyclableMemoryStreamManager rmsManager options = {
        options with
            RmsManager = Some rmsManager
    }

    /// Phase 69b.C — compose an `IRemotingTelemetry` sink that receives
    /// a `MethodTelemetry` record per method invocation (success or
    /// error, with wall-clock `ElapsedMs`). The sink is invoked AFTER
    /// the handler completes and BEFORE the response is written, so the
    /// elapsed time reflects everything the dispatcher does for the
    /// request including body normalisation, deserialisation, handler
    /// invocation, and the categorised outcome.
    ///
    /// Compose forge's `IMetricsSink` over this via a small adapter at
    /// composition root time. Without composition, no telemetry is
    /// emitted and the dispatcher pays zero per-call cost (GP 13).
    ///
    /// ```fsharp
    /// let telemetry =
    ///     { new IRemotingTelemetry with
    ///         member _.OnMethodCompleted t =
    ///             metricsSink.Record(t.MethodName, t.ElapsedMs, t.Outcome) }
    ///
    /// Remoting.createApi()
    /// |> Remoting.fromValue api
    /// |> Remoting.withTelemetry telemetry
    /// |> Remoting.buildHttpHandler
    /// ```
    let withTelemetry (sink: IRemotingTelemetry) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            Telemetry = Some sink
    }

    /// Phase 69l — compose a telemetry-allocation gate paired with a
    /// `Telemetry` sink. The dispatcher invokes the gate before every
    /// per-request `Stopwatch` + `MethodTelemetry` allocation; a `false`
    /// return skips the entire path. Use to honour GP 13 ("zero cost
    /// when off") with a default-bridge sink that's wired-but-dormant
    /// — the canonical case is `Api.make`'s default
    /// `IRemotingTelemetry` bridging to forge's `IMetricsSink`, which
    /// resolves to `NoOpMetricsSink` on `NoMetricsEndpoint` deployments.
    ///
    /// The gate is invoked once per request (cheap); the gate function
    /// itself is expected to memoise its decision since the underlying
    /// `IMetricsSink` is a process-lifetime singleton. Without composing
    /// a gate, the dispatcher allocates whenever `Telemetry = Some _`.
    let withTelemetryGate (gate: unit -> bool) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            TelemetryGate = Some gate
    }

    /// Phase 69d — compose an `IAuthContext` resolver. The resolver is
    /// invoked per request (between body normalisation and dispatch).
    /// The resolved `IAuthContext` is evaluated against each method's
    /// authorisation attributes (`[<RequiresRole>]`, `[<RequiresClaim>]`,
    /// `[<TenantScoped>]`). On deny, the dispatcher emits a categorised
    /// `ErrorCategory.Auth` envelope without invoking the handler.
    ///
    /// Methods marked `[<PublicEndpoint>]` skip resolver invocation
    /// entirely; methods marked `[<AllowAnonymous>]` resolve the
    /// context but don't enforce; unclassified methods cause startup
    /// to refuse — the consumer must annotate every API record field.
    let withAuthContext (resolver: 'ctx -> Async<IAuthContext>) (options: RemotingOptions<'ctx, 't>) = {
        options with
            AuthContextResolver = Some resolver
    }

    /// Phase 69g — compose an `IRateLimitStore` against which per-method
    /// `[<RateLimit(count, windowSeconds)>]` attributes are enforced. The
    /// `InMemoryRateLimitStore` is the in-process default; distributed
    /// deployments wire a Redis / DynamoDB / etc. impl against the same
    /// contract.
    ///
    /// Default: not composed (zero per-call cost). Methods with rate-
    /// limit attributes silently pass when no store is registered;
    /// deployments that want enforcement compose a store explicitly.
    ///
    /// Multi-attribute semantics on a method are AND: every budget must
    /// pass for the call to proceed. The first denial returns
    /// `ErrorCategory.RateLimit` envelope + status 429 + `Retry-After`
    /// header.
    let withRateLimitStore (store: IRateLimitStore) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            RateLimitStore = Some store
    }

    /// Phase 69h — compose an `IAuditEmitter` that receives an
    /// `AuditEvent` after every successful invocation of an
    /// `[<Audit(...)>]`-attributed method. PII redaction applies
    /// by default — input-record fields without `[<PiiSafe>]` appear
    /// as `<redacted>` in the event's payload.
    ///
    /// Default: not composed (zero per-call cost). Methods with
    /// `[<Audit>]` attributes silently pass when no emitter is
    /// registered.
    let withAudit (emitter: IAuditEmitter) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            AuditEmitter = Some emitter
    }

    /// Phase 69h.tail — admin-must-be-audited startup gate. With this
    /// composed, the dispatcher refuses to start when any method carrying
    /// a `[<RequiresRole>]` requirement lacks an `[<Audit>]` annotation.
    /// Role-gated methods are admin-shaped; compliance-edition deployments
    /// use this to make "admin action without an audit row" structurally
    /// impossible. Off by default (GP 11) — forge's `Api.make` composes it
    /// when `TOOLUP_AUDIT_ADMIN_REQUIRED=true` is set in the environment.
    let withAuditRequiredOnRoleGated (options: RemotingOptions<'t, 'implementation>) = {
        options with
            RequireAuditOnRoleGated = true
    }

    /// Phase 69e.C — compose a validation-message resolver. The dispatcher
    /// hands every typed-validation violation through `messages.Resolve`,
    /// which returns `Some localised` to override the built-in English
    /// message or `None` to keep it. Build one from a `ViolationCode ->
    /// template` map with `ValidationMessages.fromTemplates`, or implement
    /// `IValidationMessages` directly for fully programmatic resolution.
    ///
    /// Default: not composed; the built-in English messages ship on the
    /// wire and the seam costs nothing per request (GP 13).
    let withValidationMessages (messages: IValidationMessages) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            ValidationMessages = Some messages
    }

    /// Phase 69f — compose an `IIdempotencyStore` against which
    /// `[<Idempotent>]`-attributed methods are enforced. Calls against
    /// idempotent methods require an `X-Idempotency-Key` request header;
    /// the first call's response is captured + cached against
    /// `(subjectId, methodName, key)`. Subsequent calls with the same
    /// triple within the TTL replay the cached response without running
    /// the handler.
    ///
    /// Default: not composed; `[<Idempotent>]` attributes are dormant.
    /// With a store registered, the dispatcher enforces the contract.
    ///
    /// Default TTL is 1 hour; override via `withIdempotencyTtl`.
    let withIdempotencyStore (store: IIdempotencyStore) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            IdempotencyStore = Some store
    }

    /// Phase 69f — override the default idempotency-cache TTL (1h).
    /// The TTL applies uniformly to every idempotent method; per-method
    /// TTL is a future seam.
    let withIdempotencyTtl (ttl: System.TimeSpan) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            IdempotencyTtl = ttl
    }

    /// Phase 69j — set the `__schema_version` integer stamped on every
    /// dispatcher-emitted envelope (errors today; success-shape wrapping
    /// is a future evolution). Defaults to `1` (post-69b.E baseline).
    /// Bump when shipping a wire-format evolution so clients
    /// negotiating via `X-Remoting-Schema` can branch on the version.
    let withSchemaVersion (version: int) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            SchemaVersion = version
    }

    /// Opt out of the built-in body normalisation for `unit -> Async<'T>`
    /// methods. By default, requests carrying the `x-remoting-proxy`
    /// header AND an empty / `null` / `""` body are auto-rewritten to
    /// `[]` before dispatch (and GET is promoted to POST). Pipe through
    /// this helper if your deployment serves non-Remoting clients on the
    /// same routes and needs to receive literal empty bodies. Most
    /// consumers should leave it on.
    ///
    /// Phase 69b.A — folds the behaviour that previously lived in a
    /// separate consumer-side middleware into the dispatcher itself.
    /// Idempotent on the wire: a request that's already `[]` flows
    /// through unchanged.
    let withoutBodyNormalisation (options: RemotingOptions<'t, 'implementation>) = {
        options with
            BodyNormalisation = Disabled
    }

    /// Builds the API using a function that takes the incoming Http context and returns a protocol implementation. You can use the Http context to read information about the incoming request and also use the Http context to resolve dependencies using the underlying dependency injection mechanism.
    let fromContext (f: 'ctx -> 't) (options: RemotingOptions<'ctx, 't>) = {
        options with
            Implementation = FromContext f
    }

    /// Phase 69b.B — builds the API using an **async** function that takes
    /// the incoming Http context and returns an `Async<'impl>`. The resolver
    /// is invoked per call (per HTTP request), not snapshotted at proxy /
    /// `Api.make` time, so any value that's populated lazily after composition
    /// root construction (auth principal post-middleware, tenant config from
    /// `ITenantStore`, CSRF token re-fetched on demand, feature flag snapshot)
    /// is resolved freshly on every call.
    ///
    /// This closes the build-once / read-per-call class of defect that the
    /// closure-captured sync `fromContext` pattern was prone to when a
    /// composition-root closure captured a still-uninitialised value at boot
    /// time. The recommended pattern for any platform integration that needs
    /// async-resolved per-request context.
    ///
    /// Phase 69n: the Giraffe adapter builds the dispatcher table (TypeShape
    /// proxy + the six attribute-driven classifier maps) ONCE at compose
    /// time. Only the async resolver runs per request; the substrate cost
    /// is amortised across every dispatch. (Pre-69n the Giraffe adapter
    /// rebuilt the substrate per request; the caveat that recommended
    /// avoiding `fromContextAsync` at high RPS is retired.)
    ///
    /// ```fsharp
    /// open ToolUp.Remoting.Server
    ///
    /// let api =
    ///     Remoting.createApi()
    ///     |> Remoting.fromContextAsync (fun ctx -> async {
    ///         let! subject = subjectResolver.Resolve ctx |> Async.AwaitTask
    ///         let! tenant = tenantStore.LoadForSubject subject |> Async.AwaitTask
    ///         return { GetReport = fun id -> async { return! reports.LoadFor (tenant, id) } }
    ///     })
    ///     |> Remoting.buildHttpHandler
    /// ```
    let fromContextAsync (f: 'ctx -> Async<'t>) (options: RemotingOptions<'ctx, 't>) = {
        options with
            Implementation = FromContextAsync f
    }

    /// Builds the API using the provided static protocol implementation
    let fromValue (serverImpl: 'implementation) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            Implementation = StaticValue serverImpl
    }

    /// 0.1.15 — override the per-section byte cap inside
    /// `multipart/form-data` request bodies. Default is 16 MiB; raise
    /// for genuine large-file upload paths, lower for tighter DoS
    /// posture. A section that exceeds the cap returns
    /// `ErrorCategory.User` / 400 without materialising the bytes.
    let withMaxMultipartSectionBytes (bytes: int64) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            MaxMultipartSectionBytes = bytes
    }

    /// 0.1.15 — override the per-request multipart section count cap.
    /// Default is 64. A request carrying more parts returns
    /// `ErrorCategory.User` / 400 without iterating further.
    let withMaxMultipartSections (count: int) (options: RemotingOptions<'t, 'implementation>) = {
        options with
            MaxMultipartSections = count
    }

    /// 0.1.15 — compose a custom remote-IP resolver for the rate-limit
    /// per-IP partition. Use when the host runs behind a reverse proxy
    /// / CDN (Cloudflare, CloudFront, ALB, nginx) so anonymous traffic
    /// partitions by client IP, not by proxy IP. The supplied function
    /// should return `Some "<ip>"` for the real client when a trusted
    /// `X-Forwarded-For` chain is present, `None` to fall through to
    /// the socket-level address. Combine with ASP.NET's
    /// `ForwardedHeadersMiddleware` (configured with `KnownProxies` /
    /// `KnownNetworks`) for hardened production setups; this resolver
    /// reads the resulting `ctx.Connection.RemoteIpAddress` after
    /// rewriting.
    let withRemoteIpResolver (resolver: 'ctx -> string option) (options: RemotingOptions<'ctx, 'implementation>) = {
        options with
            RemoteIpResolver = Some resolver
    }
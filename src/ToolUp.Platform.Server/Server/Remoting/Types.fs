namespace ToolUp.Remoting.Server

open System
open FSharp.Reflection
open TypeShape
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module TypeInfo =
    let rec flattenFuncTypes (typeDef: Type) = [|
        if FSharpType.IsFunction typeDef then
            let (domain, range) = FSharpType.GetFunctionElements typeDef
            yield! flattenFuncTypes domain
            yield! flattenFuncTypes range
        else
            yield typeDef
    |]

type ParsingArgumentsError = { ParsingArgumentsError: string }

/// Route information that is propagated to error handler when exceptions are thrown
type RouteInfo<'ctx> = {
    /// The full path of the request
    path: string
    /// The last part of the path of the request
    methodName: string
    /// The HttpContext of the request
    httpContext: 'ctx
    /// The text content of the request, if any
    requestBodyText: string option
}

type CustomErrorResult<'a> = {
    error: 'a
    ignored: bool
    handled: bool
}

/// Phase 69b.E — error categorisation.
///
/// Classifies a propagated error so the client can branch on it without
/// parsing message text. Wire format adds a `category` field to the
/// error envelope alongside the existing `error` body; clients that read
/// only `error` continue to function (additive).
///
/// `[<RequireQualifiedAccess>]` to avoid clashing with common-name DU
/// cases (e.g. `Validation` might shadow other identifiers downstream).
[<RequireQualifiedAccess>]
type ErrorCategory =
    /// Caller-side mistake — bad input, missing field, unsupported state.
    | User
    /// Server-side fault — unexpected exception, infrastructure down.
    | System
    /// Caller exceeded a rate limit; retry after the indicated delay.
    | RateLimit
    /// Authentication / authorization failure.
    | Auth
    /// Resource not found.
    | NotFound
    /// Input failed declared validation.
    | Validation

/// Phase 69b.E — categorised error envelope shipped to the client.
/// Additive on top of `CustomErrorResult<'a>`; the dispatcher emits
/// this when the error handler returns `PropagateCategorised`.
///
/// Phase 69j extension: gains a `__schema_version` field reflecting
/// `RemotingOptions.SchemaVersion`. Default is `1` — the fork-point
/// shape with categorised errors. Future versions branch the dispatcher
/// to emit additional / different fields; clients negotiate by sending
/// `X-Remoting-Schema: <n>` and reading the value back from the
/// envelope. Older clients ignore the additive field (forwards-
/// compatible by JSON convention).
type CategorisedErrorResult<'a> = {
    error: 'a
    ignored: bool
    handled: bool
    category: string
    __schema_version: int
}

/// The ErrorResult lets you choose whether you want to propagate a custom error back to the client or to ignore it. Either case, an exception is thrown on the call-site from the client.
/// Phase 69b.E adds `PropagateCategorised` so a handler can attach an `ErrorCategory` for client-side branching.
type ErrorResult =
    | Ignore
    | Propagate of obj
    | PropagateCategorised of category: ErrorCategory * error: obj

type ErrorHandler<'context> = System.Exception -> RouteInfo<'context> -> ErrorResult

/// A protocol implementation can be a static value provided, generated
/// synchronously from the Http context on every request, or generated
/// asynchronously per request (Phase 69b.B).
///
/// Async resolution is the recommended pattern for any value that's
/// populated lazily after composition root construction — auth principal
/// after the auth middleware has resolved the subject, tenant config
/// looked up from `ITenantStore`, CSRF token re-fetched on demand,
/// feature flag snapshot for the current subject. The resolver is
/// invoked **per call** (per HTTP request), not snapshotted at proxy /
/// `Api.make` time. Closes the build-once / read-per-call class of
/// defect that the closure-captured sync pattern was prone to when a
/// composition-root closure captured a still-uninitialised value.
type ProtocolImplementation<'context, 'serverImpl> =
    | Empty
    | StaticValue of 'serverImpl
    | FromContext of ('context -> 'serverImpl)
    | FromContextAsync of ('context -> Async<'serverImpl>)

type SerializationType =
    | Json
    | MessagePack

/// Which JSON serializer to use for the API's wire format.
///
/// Single backend: `SystemTextJson opts` — System.Text.Json with a
/// `JsonSerializerOptions` instance. Typically constructed via
/// `FableConverters.create()` from `ToolUp.Remoting.Json.SystemTextJson`,
/// which registers the F# converter set (Option / DU / tuple / record /
/// list / Map / Set / number / string / date / byte[] etc.) for the
/// Fable wire shape. Consumers wanting strict-STJ defaults can pass
/// `JsonSerializerOptions()` instead.
///
/// The single-case DU shape is retained for forward compatibility with
/// alternative backends (e.g. a future protobuf or MsgPack-mirror JSON
/// path); right now it is structurally equivalent to a JsonSerializerOptions
/// wrapper.
type JsonSerializerBackend = SystemTextJson of System.Text.Json.JsonSerializerOptions

type internal IShapeFSharpAsyncOrTask =
    abstract Element: TypeShape

type internal ShapeFSharpAsyncOrTask<'T>() =
    interface IShapeFSharpAsyncOrTask with
        member _.Element = shapeof<'T> :> _

/// One per-argument value passed from the HTTP layer into the proxy:
///   * `Choice1Of2 bytes`  — binary input (multipart byte[] section)
///   * `Choice2Of2 jsonText` — the raw JSON text of one element from the
///     outer arguments array. Backend-agnostic by construction: any JSON
///     parser the deserialise path picks can re-parse this text. Previously
///     this carried a `Newtonsoft.Json.Linq.JToken`, which prevented STJ-only
///     consumers from dropping the Newtonsoft transitive dep.
type internal InvocationPropsInt = {
    /// Phase 69m — second arm is now `JsonElement` (was `string`). The
    /// outer arguments array parses once into a JsonDocument; each
    /// element is `Clone`d so it survives the document's disposal and
    /// can be deserialised without re-parsing. Previously the second
    /// arm was raw JSON text and the per-argument deserialise re-parsed
    /// each slice into its own JsonDocument — N+1 parses per call.
    Arguments: Choice<byte[], JsonElement> list
    IsProxyHeaderPresent: bool
    Output: Stream
}

type InvocationProps<'impl> = {
    Input: Stream
    /// Phase 69m — when the adapter's body cache has already
    /// materialised the request bytes (via validation / audit /
    /// idempotency-hash pre-flight stages forcing the lazy cache),
    /// the adapter populates this so the proxy parses directly from
    /// the cached bytes instead of re-reading `Input`. When `None`
    /// (no cache materialised), the proxy falls back to the stream
    /// read — unchanged behaviour for non-Giraffe adapters that don't
    /// implement a body cache.
    InputBytes: byte[] option
    Output: Stream
    ImplementationBuilder: unit -> 'impl
    EndpointName: string
    HttpVerb: string
    InputContentType: string
    IsProxyHeaderPresent: bool
}

type MakeEndpointProps = {
    FieldName: string
    RecordName: string
    ResponseSerialization: SerializationType
    JsonSerializer: JsonSerializerBackend
    FlattenedTypes: Type[]
}

type InvocationResult =
    | Success of isBinaryOutput: bool
    | EndpointNotFound
    | InvalidHttpVerb
    | Exception of exn * functionName: string * requestBodyText: string option

// an example is a list of arguments and the description of the example
type Example = obj list * string

type RouteDocs = {
    Route: string option
    /// An alias for the method name
    Alias: string option
    /// The description of the method
    Description: string option
    /// Examples are objects and optionally, their description
    Examples: Example list
}

/// Contains documented routes for an API
type Documentation = Documentation of string * RouteDocs list

/// Phase 69d — per-request auth context provided to the dispatcher
/// for evaluation against per-method authorisation attributes
/// (`[<RequiresRole>]`, `[<RequiresClaim>]`, `[<TenantScoped>]`, etc.).
///
/// Consumers implement `IAuthContext` over their auth model (forge wires
/// Phase 66's `Subject` over it) and provide an async resolver via
/// `Remoting.withAuthContext`. Without a resolver registered, methods
/// classified `[<PublicEndpoint>]` / `[<AllowAnonymous>]` still work;
/// methods requiring auth deny on every call (fail-closed posture).
type IAuthContext =
    /// True if the caller has the given role.
    abstract HasRole: role: string -> bool
    /// True if the caller has the given claim (value-aware when `value`
    /// is `Some`; presence-only when `None`).
    abstract HasClaim: claim: string * value: string option -> bool
    /// True if a tenant context is present. Used by `[<TenantScoped>]`
    /// to require an authenticated tenant-bound subject.
    abstract HasTenant: unit -> bool
    /// True if the caller is anonymous (no subject resolved). Used to
    /// distinguish a deliberate anonymous request from a missing
    /// auth-context resolver — they have different security postures.
    abstract IsAnonymous: unit -> bool
    /// Phase 69g — stable identifier for this caller, used as the
    /// per-subject rate-limit key. Consumer-defined shape; conventions
    /// include `"anonymous"`, `"user:alice"`, `"tenant:T1:user:alice"`,
    /// `"share-token:abc"`. The dispatcher composes per-method rate-limit
    /// budgets against this id so a single subject's traffic doesn't
    /// starve other subjects' budgets.
    abstract SubjectId: string

/// Phase 69g — outcome of a rate-limit acquisition attempt against an
/// `IRateLimitStore`. `Denied` carries the wall-clock time the caller
/// should wait before retrying (mapped to the HTTP `Retry-After` header
/// + the categorised envelope's `retryAfterSeconds` payload).
type RateLimitDecision =
    | RateLimitAllowed
    | RateLimitDenied of retryAfter: System.TimeSpan

/// Phase 69g — rate-limit substrate. Per-method `[<RateLimit>]`
/// attributes resolve to budget triples (key, count, window); the store
/// tracks consumption across calls and returns a decision per
/// acquisition.
///
/// The interface is async-shaped to honour the [six portability rules](../../../toolup-forge/CLAUDE.md):
/// stateless between calls, identity-by-value (string keys), retry as
/// data (Denied carries TimeSpan, not a callback). The default
/// `InMemoryRateLimitStore` ships in-process; forge / other consumers
/// can wire Redis / DynamoDB / etc. against the same contract.
///
/// Default: not composed; methods with `[<RateLimit>]` attributes
/// silently pass (zero-cost) until a store is registered. With a
/// store registered, the dispatcher enforces budgets against it.
type IRateLimitStore =
    abstract TryAcquire: key: string * count: int * window: System.TimeSpan -> Async<RateLimitDecision>

/// Phase 69h — well-known audit-event kinds. Open via `Custom` for
/// consumer-domain kinds the standard set doesn't anticipate.
[<RequireQualifiedAccess>]
type AuditKind =
    | MoneyMoved
    | PolicyChanged
    | PiiAccessed
    | DataExported
    | PermissionGranted
    | PermissionRevoked
    | TenantCreated
    | TenantDeleted
    | Custom of string

/// Phase 69h — audit event emitted on every successful invocation of
/// an `[<Audit>]`-attributed method. PII fields are redacted unless
/// the field carries `[<PiiSafe>]`.
type AuditEvent = {
    /// The audit category from the `[<Audit kind>]` attribute.
    Kind: AuditKind
    /// The invoked method's bare name (e.g. `PromoteToAdmin`).
    MethodName: string
    /// Subject id from the resolved auth context, or `"anonymous"`.
    SubjectId: string
    /// Request correlation id (Phase 69b.D) for end-to-end joining.
    CorrelationId: string option
    /// Wall-clock timestamp of emission.
    Timestamp: System.DateTimeOffset
    /// PII-redacted snapshot of the input argument shape. Fields without
    /// a `[<PiiSafe>]` attribute are redacted to `<redacted>`; fields
    /// with one carry their string-shape value. Empty when the method
    /// took no arguments or the audit attribute opted into SubjectOnly
    /// payload mode.
    Payload: Map<string, string>
}

/// Phase 69h — audit-event emission hook. Async-shaped: emission can
/// route to a remote audit log without blocking the dispatcher's
/// response path (the dispatcher does `do!` on this so backpressure is
/// honoured if the sink genuinely needs to wait).
///
/// Default: not composed. Methods with `[<Audit>]` attributes silently
/// pass (zero per-call cost). Consumers wire an emitter (forge wires
/// its `IAuditLog` over this) to start receiving events.
type IAuditEmitter =
    abstract Emit: event: AuditEvent -> Async<unit>

/// 0.1.16 — typed exception raised when a multipart request exceeds the
/// configured caps (per-section byte cap OR per-request section count
/// cap). The Giraffe adapter catches this specifically and emits an
/// `ErrorCategory.User` + 413 Payload Too Large envelope, rather than
/// the generic `System` / unhandled categorisation that any plain
/// `Exception` would receive. Surfacing the right category lets
/// operators distinguish "client submitted too much" from "server
/// blew up" in metrics + audit + log dashboards.
type MultipartCapExceededException(message: string) =
    inherit System.Exception(message)

/// Phase 69f — captured response shape stored against an idempotency
/// key + replayed on subsequent calls with the same key. Body is the
/// already-serialised bytes the original call produced; status + content
/// type are replayed verbatim so the client sees a byte-identical
/// response.
///
/// `RequestBodyHash` (0.1.15) is a SHA-256 (lowercase hex) of the
/// original request body — the dispatcher compares the current call's
/// body hash against this on TryGet replays and returns
/// `ErrorCategory.User` / 409 Conflict when they differ. Closes the
/// Stripe-pattern footgun where a client reuses an idempotency key
/// against a mutated body and silently gets back the prior response.
/// Empty string for legacy entries materialised by 0.1.14 stores.
type MemoisedResponse = {
    Body: byte[]
    StatusCode: int
    ContentType: string
    RequestBodyHash: string
}

/// Phase 69f — idempotency substrate. Captures the first call's
/// response under a `(subjectScope, methodName, idempotencyKey)` triple
/// and replays it on subsequent calls within the configured TTL.
/// Keys are subject-scoped so the same UUID submitted by two different
/// callers is two distinct cache slots (security boundary).
///
/// Async-shaped + identity-by-value per the six portability rules.
/// `InMemoryIdempotencyStore` is the in-process default; distributed
/// deployments wire a Redis / DynamoDB / etc. impl against the same
/// contract.
///
/// Default: not composed. Methods with `[<Idempotent>]` silently
/// require the header (deny if missing) only when a store is wired;
/// without a store, the attribute is dormant.
type IIdempotencyStore =
    abstract TryGet: key: string * scope: string -> Async<MemoisedResponse option>
    abstract Store: key: string * scope: string * response: MemoisedResponse * ttl: System.TimeSpan -> Async<unit>

/// Phase 69i — typed handle to a long-running operation. The phantom
/// type parameter `'T` carries the eventual result type through to the
/// `JobStatus<'T>.Succeeded` arm at the call site, so a client polling
/// `GetStatus` against a `JobHandle<ReportFile>` is guaranteed a
/// `JobStatus<ReportFile>` back.
///
/// On the wire the type-erased shape is a single `jobId` string; the
/// phantom `'T` is server-side typing only.
type JobHandle<'T> = JobHandle of jobId: string

/// Phase 69i — status of a long-running operation. Returned by
/// `IJobDispatcher.GetStatus<'T>`. `Succeeded` carries the eventual
/// typed result; `Failed` carries a server-side message (the
/// categorised `ErrorCategory.Validation` / `.System` envelope is a
/// future extension when the dispatcher knows the failure category).
[<RequireQualifiedAccess>]
type JobStatus<'T> =
    | Queued
    | Running of progress: float
    | Succeeded of result: 'T
    | Failed of message: string
    | Cancelled

/// Phase 69i — substrate for long-running operations. `Enqueue` starts
/// the supplied async work in the background and returns a typed
/// handle the caller can use to poll for status. `GetStatus`
/// resolves the handle to the latest status; the v0 default impl is
/// in-process + lifetime-bound to the host (Phase 9b's IJobScheduler
/// wires the distributed shape).
///
/// Default: not composed. Methods returning `Async<JobHandle<'T>>`
/// are recognised by the dispatcher's reflection at startup; without
/// an `IJobDispatcher` registered, the methods still work (the
/// handler manages its own handle generation) but no platform-level
/// retry / persistence / observability flows.
type IJobDispatcher =
    abstract Enqueue<'T> : work: Async<'T> -> Async<JobHandle<'T>>
    abstract GetStatus<'T> : handle: JobHandle<'T> -> Async<JobStatus<'T>>

/// Phase 69b.C — telemetry hook.
///
/// Per-method outcome emitted by the dispatcher after each invocation.
/// `Failed` carries the exception that was thrown; in a later phase the
/// error envelope grows a categorisation DU and that category will ride
/// alongside the exception here.
///
/// `[<RequireQualifiedAccess>]` because the case names would otherwise
/// shadow existing `InvocationResult.Success` (the internal proxy
/// dispatch result) — qualified-access forces `MethodOutcome.Succeeded`
/// / `MethodOutcome.Failed` at every call site, eliminating the clash.
[<RequireQualifiedAccess>]
type MethodOutcome =
    | Succeeded
    | Failed of exn

/// Phase 69b.C — telemetry record emitted on each method completion.
///
/// `ElapsedMs` is the wall-clock time between the dispatcher receiving
/// the request and the handler returning (success) or throwing (error).
/// Body normalisation, deserialisation, handler invocation, and
/// serialisation are all included; per-stage breakdown is a future
/// enhancement.
///
/// Phase 69b.D — `CorrelationId` rides on every telemetry event so
/// the sink can correlate across calls + propagate to logs / metrics
/// / audit rows. Value is what the dispatcher established for the
/// request (header value if present, generated GUID otherwise).
type MethodTelemetry = {
    MethodName: string
    ElapsedMs: int
    Outcome: MethodOutcome
    CorrelationId: string option
}

/// Phase 69b.C — telemetry hook contract.
///
/// Implementations are invoked **per call** (per HTTP request) after the
/// handler completes. The implementation must be fast and non-throwing;
/// the dispatcher does not catch exceptions from telemetry emission, so
/// a throwing telemetry sink will surface as a 500 to the client.
///
/// Wire forge's `IMetricsSink` over this via a small adapter at compose
/// time. Distributed-ready implementations honour the workspace [six
/// portability rules](../../../toolup-forge/CLAUDE.md): stateless
/// between calls, async-friendly (the method is sync because hot-path
/// metrics are write-only — same exception that `IMetricsSink` carries).
///
/// Default: not composed; no telemetry emission, no per-call cost.
/// Compose via `Remoting.withTelemetry`.
type IRemotingTelemetry =
    abstract OnMethodCompleted: MethodTelemetry -> unit

/// Body normalisation behaviour for `unit -> Async<'T>` API methods.
///
/// The Fable.SimpleJson client serializes a `unit` argument as a missing
/// body, `null`, or `""` depending on which network primitive it uses
/// (fetch in the browser may strip the body for "no content" requests).
/// Without normalisation, the dispatcher tries to deserialise the empty
/// body as a JSON array and fails. With normalisation (the default),
/// requests carrying the `x-remoting-proxy` header AND an empty / `null`
/// / `""` body are auto-rewritten to `[]` before dispatch, and a GET is
/// promoted to POST so the dispatcher's POST-shaped invocation path
/// applies.
///
/// Default: `Enabled`. Pre Phase 69b consumers ran a separate
/// `RemotingBodyNormalizationMiddleware` upstream of the Remoting
/// HttpHandler to apply this fix; that middleware retires as the
/// dispatcher takes ownership. Idempotent: a request that's already
/// `[]` flows through unchanged. To opt out (e.g. you want to receive
/// literal empty bodies for non-Remoting clients hitting the same
/// route), pipe through `Remoting.withoutBodyNormalisation`.
type BodyNormalisation =
    | Enabled
    | Disabled

type RemotingOptions<'context, 'serverImpl> = {
    Implementation: ProtocolImplementation<'context, 'serverImpl>
    RouteBuilder: string -> string -> string
    ErrorHandler: ErrorHandler<'context> option
    DiagnosticsLogger: (string -> unit) option
    Docs: string option * Option<Documentation>
    ResponseSerialization: SerializationType
    JsonSerializer: JsonSerializerBackend
    RmsManager: Microsoft.IO.RecyclableMemoryStreamManager option
    BodyNormalisation: BodyNormalisation
    Telemetry: IRemotingTelemetry option
    /// Phase 69l — telemetry seam zero-cost gate. When `Some`, the
    /// dispatcher invokes this function before allocating the
    /// per-request `Stopwatch` + `MethodTelemetry` record. A `false`
    /// return short-circuits the entire telemetry-allocation path
    /// (substring, stopwatch, record, tag map, sink dispatch). Used by
    /// `Api.make` to honour GP 13 when the default `IMetricsSink`
    /// resolves to `NoOpMetricsSink` — the seam pays nothing per
    /// request on the canonical `NoMetricsEndpoint` deployment shape.
    ///
    /// Default `None` — the dispatcher emits whenever `Telemetry =
    /// Some _` (the pre-69l behaviour). Consumer-supplied sinks pass
    /// through the gate by never composing one.
    ///
    /// Compose via `Remoting.withTelemetryGate`.
    TelemetryGate: (unit -> bool) option
    AuthContextResolver: ('context -> Async<IAuthContext>) option
    RateLimitStore: IRateLimitStore option
    AuditEmitter: IAuditEmitter option
    IdempotencyStore: IIdempotencyStore option
    IdempotencyTtl: System.TimeSpan
    /// Phase 69j — `__schema_version` field stamped on every
    /// dispatcher-emitted envelope. Default `1` matches the post-69b.E
    /// shape (categorised errors). Future wire-format evolutions
    /// increment this and consult an `X-Remoting-Schema` request header
    /// to optionally route to a per-version handler shape.
    SchemaVersion: int
    /// 0.1.15 — per-multipart-section byte cap. Applied per part inside
    /// `multipart/form-data` bodies so a single hostile section can't
    /// materialise an unbounded `byte[]` and OOM the host. Default 16
    /// MiB matches a generous mobile-upload ceiling; raise via
    /// composition for genuine large-file paths.
    MaxMultipartSectionBytes: int64
    /// 0.1.15 — per-request multipart section count cap. Caps how many
    /// parts a single multipart request may carry. Default 64; raise via
    /// composition only when the application genuinely needs more.
    MaxMultipartSections: int
    /// 0.1.15 — per-request remote IP resolver. Used by the rate-limit
    /// fallback so anonymous callers partition by client IP (not
    /// reverse-proxy IP). Default `None` → the dispatcher reads
    /// `ctx.Connection.RemoteIpAddress` (socket-level) — correct for
    /// hosts NOT behind a CDN / reverse proxy. Production deployments
    /// behind Cloudflare / CloudFront / ALB / nginx should compose a
    /// resolver that reads `X-Forwarded-For` (or the cloud-specific
    /// `CF-Connecting-IP` / `X-Real-IP`) after configuring ASP.NET's
    /// `ForwardedHeadersMiddleware` with `KnownProxies` /
    /// `KnownNetworks` to whitelist trusted hops. Without this, every
    /// anonymous request from the Internet collapses into a single
    /// rate-limit bucket per proxy IP — defeating the 0.1.14
    /// per-IP partition.
    RemoteIpResolver: ('context -> string option) option
}
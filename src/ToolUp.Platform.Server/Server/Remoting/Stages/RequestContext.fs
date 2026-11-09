namespace ToolUp.Remoting.Server.Stages

open System
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Proxy

/// The identity a caller is metered and memoised under: the resolved
/// subject, or the client's address for a caller without one.
[<Struct>]
type internal SubjectKey = SubjectKey of string

[<RequireQualifiedAccess>]
module internal SubjectKey =

    let value (SubjectKey key) = key

    /// The resolved caller's subject, else the remote address. The spelling
    /// is `RateLimit.deriveSubjectKey`'s: it names persisted rate buckets and
    /// memoised responses, so it is never re-spelled here.
    let ofCaller (caller: IAuthContext option) (remoteIp: string option) : SubjectKey =
        SubjectKey(RateLimit.deriveSubjectKey caller remoteIp)

/// The (subject, method) identity a memoised response is stored under.
[<Struct>]
type internal IdempotencyScope = IdempotencyScope of string

[<RequireQualifiedAccess>]
module internal IdempotencyScope =

    let value (IdempotencyScope scope) = scope

    /// The spelling is `Idempotency.deriveScope`'s, which names stored
    /// entries.
    let ofCall (subject: SubjectKey) (methodName: string) : IdempotencyScope =
        IdempotencyScope(Idempotency.deriveScope (SubjectKey.value subject) methodName)

/// The request body, read once, on first demand, and kept: every stage and
/// the proxy read the same bytes. Safe before or after dispatch: the stream
/// it reads is the buffered body ASP.NET Core owns, which nothing in the
/// dispatcher disposes. A body disposed by something composed AROUND the
/// handler is reported as a `RequestBodyDisposedException` naming the method
/// and whether the response had started, never a bare "disposed object".
[<Sealed>]
type internal LazyBody(http: HttpContext, methodName: string) =
    let mutable bytes: byte[] voption = ValueNone
    let mutable hash: string voption = ValueNone

    /// The bytes, when a stage has read them.
    member _.Cached: byte[] option =
        match bytes with
        | ValueSome read -> Some read
        | ValueNone -> None

    member _.Read() : Task<byte[]> =
        match bytes with
        | ValueSome read -> Task.FromResult read
        | ValueNone -> task {
            try
                http.Request.Body.Position <- 0L
                use buffer = new MemoryStream()
                do! http.Request.Body.CopyToAsync buffer
                http.Request.Body.Position <- 0L
                let read = buffer.ToArray()
                bytes <- ValueSome read
                return read
            with :? ObjectDisposedException as disposed ->
                return raise (RequestBodyDisposedException(methodName, http.Response.HasStarted, disposed))
          }

    /// SHA-256 of the raw bytes (a multipart body included).
    member this.Hash() : Task<string> =
        match hash with
        | ValueSome known -> Task.FromResult known
        | ValueNone -> task {
            let! read = this.Read()
            let computed = Idempotency.hashRequestBodyBytes read
            hash <- ValueSome computed
            return computed
          }

/// What a request addresses on the mounted record.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type internal Target =
    /// A method of the record.
    | Method of MethodContract
    /// A long-running method's companion, served off the composed dispatcher.
    | Companion of CompanionContract * JobCompanionCalls * IJobDispatcher
    /// No method of this record: every stage passes it through, and the
    /// proxy finds no route (another API's handler may own it).
    | Foreign

/// One call through the composition's proxy.
[<NoComparison; NoEquality>]
type internal Dispatch = {
    /// Routed endpoint, the arguments parsed ahead, the body bytes read, the
    /// output stream.
    Invoke: string -> ParsedArguments voption -> byte[] option -> Stream -> Task<Invoked>
    /// The proxy's own parse and first-argument decode, run early.
    ParseFirst: string -> byte[] -> ParsedArguments voption
}

/// A composition, read once when it is mounted: the contract and every
/// option a stage reads.
[<NoComparison; NoEquality>]
type internal Mount = {
    Contract: ApiContract
    Methods: Map<string, MethodContract>
    /// `<method>/<companion>` → its contract, when a job dispatcher is composed.
    Companions: Map<string, CompanionContract * JobCompanionCalls * IJobDispatcher>
    Addressable: Set<string>
    SchemaVersion: int
    Json: JsonSerializerBackend
    Logger: (string -> unit) option
    /// The error envelope the composed `ErrorHandler` makes of a fault.
    Fail: exn -> RouteInfo<HttpContext> -> obj
    Telemetry: IRemotingTelemetry option
    TelemetryGate: (unit -> bool) option
    Resolver: (HttpContext -> Async<IAuthContext>) option
    RemoteIp: (HttpContext -> string option) option
    RateLimitStore: IRateLimitStore option
    AuditEmitter: IAuditEmitter option
    IdempotencyStore: IIdempotencyStore option
    IdempotencyTtl: TimeSpan
    ValidationMessages: IValidationMessages option
    JobAdminRole: string
    NormaliseBody: bool
    Responses: SerializationType
    Output: Microsoft.IO.RecyclableMemoryStreamManager
    /// The documentation page or schema for a route no method owns.
    NotFound: HttpContext -> Response
}

/// One request, as the stages see it. Immutable: a stage that learns
/// something answers a new context.
[<NoComparison; NoEquality>]
type internal RequestContext = {
    Http: HttpContext
    Correlation: string
    /// The trailing path segment as the caller addressed it.
    Addressed: string
    /// The routed path the proxy dispatches (the negotiated version's).
    Endpoint: string
    /// The routed method name: every slice and the telemetry key on it.
    Method: string
    Target: Target
    Routing: SchemaRouting
    Body: LazyBody
    Caller: IAuthContext option
    Subject: SubjectKey
    /// The subject and job-admin standing riding the call context.
    Ambient: (string option * bool) option
    /// The arguments a stage parsed ahead of dispatch.
    Parsed: ParsedArguments voption
    /// The idempotency key and scope a successful answer is memoised under.
    Memo: (string * IdempotencyScope) option
    /// What the call answered, once `invoke` has run.
    Answer: Response option
    Settled: Settlement option
    Telemetry: TelemetryScope option
    Dispatch: Dispatch
}

/// What a stage decides: run the next stage, or answer now.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type internal StageOutcome =
    | Continue of RequestContext
    | Answer of RequestContext * Response

/// One stage of the request pipeline.
type internal Stage = Mount -> RequestContext -> Task<StageOutcome>

[<RequireQualifiedAccess>]
module internal Mount =

    let private notFound (options: RemotingOptions<HttpContext, 'impl>) (http: HttpContext) : Response =
        match http.Request.Method.ToUpperInvariant(), options.Docs with
        | "GET", (Some docsUrl, Some docs) when docsUrl = http.Request.Path.Value ->
            let (Documentation(docsName, _)) = docs

            let schema =
                Docs.makeDocsSchemaWithSchemaVersions typeof<'impl> docs options.RouteBuilder options.SchemaVersion

            Response.Text("text/html; charset=utf-8", DocsApp.embedded docsName docsUrl schema)
        | "OPTIONS", (Some docsUrl, Some docs) when
            sprintf "/%s/$schema" docsUrl = http.Request.Path.Value
            || sprintf "%s/$schema" docsUrl = http.Request.Path.Value
            ->
            let schema =
                Docs.makeDocsSchemaWithSchemaVersions typeof<'impl> docs options.RouteBuilder options.SchemaVersion

            Response.Text("text/plain; charset=utf-8", schema.ToJsonString())
        | _ -> Response.Halt

    let private fail (options: RemotingOptions<HttpContext, 'impl>) (ex: exn) (route: RouteInfo<HttpContext>) : obj =
        match options.ErrorHandler with
        | None -> box (Errors.unhandled route.methodName)
        | Some handler ->
            match handler ex route with
            | ErrorResult.Ignore -> box (Errors.ignored route.methodName)
            | ErrorResult.Propagate error -> box (Errors.propagated error)
            | ErrorResult.PropagateCategorised(category, error) ->
                box (Errors.categorisedWithSchema options.SchemaVersion category error)

    /// Mount a composition over its bound contract.
    let ofOptions (contract: ApiContract) (options: RemotingOptions<HttpContext, 'impl>) : Mount = {
        Contract = contract
        Methods = contract.Methods |> List.map (fun m -> m.Name, m) |> Map.ofList
        Companions =
            match options.JobDispatcher with
            | None -> Map.empty
            | Some dispatcher ->
                let shapes =
                    contract.Methods
                    |> List.choose (fun m ->
                        match m.Shape with
                        | MethodShape.LongRunning calls -> Some(m.Name, calls)
                        | _ -> None)
                    |> Map.ofList

                contract.Companions
                |> List.choose (fun c ->
                    shapes
                    |> Map.tryFind c.Of
                    |> Option.map (fun calls -> c.Name, (c, calls, dispatcher)))
                |> Map.ofList
        Addressable = Contract.addressableMethods contract
        SchemaVersion = options.SchemaVersion
        Json = options.JsonSerializer
        Logger = options.DiagnosticsLogger
        Fail = fail options
        Telemetry = options.Telemetry
        TelemetryGate = options.TelemetryGate
        Resolver = options.AuthContextResolver
        RemoteIp = options.RemoteIpResolver
        RateLimitStore = options.RateLimitStore
        AuditEmitter = options.AuditEmitter
        IdempotencyStore = options.IdempotencyStore
        IdempotencyTtl = options.IdempotencyTtl
        ValidationMessages = options.ValidationMessages
        JobAdminRole = options.JobAdminRole
        NormaliseBody = options.BodyNormalisation = BodyNormalisation.Enabled
        Responses = options.ResponseSerialization
        Output = getRecyclableMemoryStreamManager options
        NotFound = notFound options
    }

[<RequireQualifiedAccess>]
module internal RequestContext =

    let private emptyArray = Text.Encoding.UTF8.GetBytes "[]"

    /// A ToolUp client may send an empty, `null` or `""` body for a method
    /// taking `unit`: rewritten to `[]`, and a GET promoted to POST so the
    /// POST-shaped call applies. Only for requests carrying the
    /// `x-remoting-proxy` header, so other clients of the route are
    /// untouched; a body already `[]` flows through unchanged.
    let private normalise (http: HttpContext) : Task = task {
        use reader =
            new StreamReader(http.Request.Body, Text.Encoding.UTF8, leaveOpen = true)

        let! body = reader.ReadToEndAsync()
        http.Request.Body.Position <- 0L
        let trimmed = body.Trim()

        if trimmed = "" || trimmed = "\"\"" || trimmed = "null" then
            http.Request.Body <- new MemoryStream(emptyArray)
            http.Request.ContentLength <- Nullable(int64 emptyArray.Length)

            if http.Request.Method = "GET" then
                http.Request.Method <- "POST"
    }

    /// The method or companion a routed path addresses.
    let target (mount: Mount) (path: string) : Target =
        let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)

        let companion =
            if segments.Length < 2 || Map.isEmpty mount.Companions then
                None
            else
                Map.tryFind (segments[segments.Length - 2] + "/" + segments[segments.Length - 1]) mount.Companions

        match companion, Map.tryFind (Route.methodSegment path) mount.Methods with
        | Some(c, calls, dispatcher), _ -> Target.Companion(c, calls, dispatcher)
        | None, Some m -> Target.Method m
        | None, None -> Target.Foreign

    /// The context of one request. The body becomes rewindable here — the
    /// one buffering point, so every stage, the proxy and anything composed
    /// after dispatch read the same bytes — and is normalised.
    let ofHttp (mount: Mount) (http: HttpContext) (path: string) (dispatch: Dispatch) : Task<RequestContext> = task {
        http.Request.EnableBuffering()

        if mount.NormaliseBody && http.Request.Headers.ContainsKey "x-remoting-proxy" then
            do! normalise http

        let addressed = Route.methodSegment http.Request.Path.Value

        return {
            Http = http
            Correlation = ""
            Addressed = addressed
            Endpoint = path
            Method = Route.methodSegment path
            Target = target mount path
            Routing = SchemaRouting.AsAddressed
            Body = LazyBody(http, Route.methodSegment path)
            Caller = None
            Subject = SubjectKey.ofCaller None None
            Ambient = None
            Parsed = ValueNone
            Memo = None
            Answer = None
            Settled = None
            Telemetry = TelemetryScope.start mount.Telemetry mount.TelemetryGate
            Dispatch = dispatch
        }
    }

    /// The caller's subject id for an audit row or a validator.
    let subjectId (ctx: RequestContext) : string =
        match ctx.Caller with
        | Some caller -> caller.SubjectId
        | None -> "anonymous"

    /// The JSON method the context addresses, if it is one.
    let methodOf (ctx: RequestContext) : MethodContract option =
        match ctx.Target with
        | Target.Method m -> Some m
        | _ -> None
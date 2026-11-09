namespace ToolUp.Remoting.Server.Stages

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Proxy

/// The request pipeline: one list of stages over one `RequestContext`,
/// shared by both adapters.
[<RequireQualifiedAccess>]
module internal Pipeline =

    /// The order is the doctrine. Authorisation before argument decoding, so
    /// an unauthenticated caller learns no field names; idempotency before
    /// the rate limit, so a replay is not charged; the call before its audit
    /// row. A streaming call runs through all of it, as a unary one does.
    let stages: Stage list = [
        Correlate.stage
        Negotiate.stage
        BatchBackstop.stage
        Authorize.stage
        Validate.stage
        Idempotency.stage
        RateLimit.stage
        Invoke.stage
        Audit.stage
    ]

    /// Keep the call context in step with what a stage learned: the
    /// correlation id, then the caller's subject. Opened here, in the
    /// pipeline's own frame, so they ride the rest of the request — the
    /// handler, and a stream's producer, which starts under the writer.
    let private enter (scopes: Stack<IDisposable>) (before: RequestContext) (after: RequestContext) : unit =
        if after.Correlation <> before.Correlation then
            scopes.Push(CallContext.beginRequest after.Correlation)

        match before.Ambient, after.Ambient with
        | None, Some(subject, jobAdmin) -> scopes.Push(CallContext.beginSubject subject jobAdmin)
        | _ -> ()

    let private emit (ctx: RequestContext) (settlement: Settlement option) : unit =
        match ctx.Telemetry, settlement with
        | Some scope, Some settled -> TelemetryScope.emit ctx.Correlation settled scope
        | _ -> ()

    /// Run `stages` over `start`, write what they answered, and emit the
    /// call's telemetry once. False when no method of this record owns the
    /// route, so the adapter passes the request on.
    let run (stages: Stage list) (mount: Mount) (start: RequestContext) : Task<bool> = task {
        let scopes = Stack<IDisposable>()
        let mutable current = start

        try
            try
                let mutable pending = stages
                let mutable answered: Response voption = ValueNone

                while (match answered, pending with
                       | ValueNone, _ :: _ -> true
                       | _ -> false) do
                    let! outcome = (List.head pending) mount current
                    pending <- List.tail pending

                    match outcome with
                    | StageOutcome.Continue next ->
                        enter scopes current next
                        current <- next
                    | StageOutcome.Answer(next, response) ->
                        enter scopes current next
                        current <- next
                        answered <- ValueSome response

                let response =
                    match answered, current.Answer with
                    | ValueSome response, _
                    | ValueNone, Some response -> response
                    | ValueNone, None -> Response.Halt

                let! streamed = Response.write mount.Json mount.Logger current.Correlation current.Http response

                match streamed, response with
                | Some ended, Response.Stream(label, _) ->
                    emit
                        current
                        (Some {
                            Method = label
                            Outcome = ended.Outcome
                            Chunks = Some ended.Chunks
                        })
                | _ -> emit current current.Settled

                return
                    match response with
                    | Response.Halt -> false
                    | _ -> true
            with :? RequestBodyDisposedException as fault ->
                // The one evidence a "handler succeeded, client saw 502" shape
                // leaves: a log line and a `Failed` outcome naming the method.
                mount.Logger
                |> Option.iter (fun log -> log (sprintf "ToolUp.Remoting: %s" fault.Message))

                emit
                    current
                    (Some {
                        Method = current.Method
                        Outcome = MethodOutcome.Failed fault
                        Chunks = None
                    })

                return raise fault
        finally
            while scopes.Count > 0 do
                scopes.Pop().Dispose()
    }

    /// The composition's proxy as the `invoke` stage calls it, for one request.
    let dispatchOf (proxy: ApiProxy<'impl>) (impl: HttpContext -> 'impl) (http: HttpContext) : Dispatch = {
        Invoke =
            fun endpoint parsed bytes output ->
                proxy.Invoke
                    {
                        ImplementationBuilder = fun () -> impl http
                        EndpointName = endpoint
                        Input = http.Request.Body
                        InputBytes = bytes
                        IsProxyHeaderPresent = http.Request.Headers.ContainsKey "x-remoting-proxy"
                        HttpVerb = http.Request.Method
                        InputContentType =
                            match http.Request.ContentType with
                            | null -> ""
                            | contentType -> contentType
                        Output = output
                    }
                    parsed
        ParseFirst = proxy.ParseFirst
    }

    /// The contract a composition dispatches over — the registered generated
    /// table's, or the record read once — bound to the composition's flags. A
    /// composition that makes a refusal of the record refuses to start,
    /// naming every refusal.
    let contractFor (options: RemotingOptions<HttpContext, 'impl>) : ApiContract =
        match Contract.bind (ContractFlags.ofOptions options) (contractOf<'impl> ()) with
        | Error refusals -> invalidOp (ContractRefusal.render refusals)
        | Ok contract -> contract

    /// Serve a bound contract: the mount and the proxy are built ONCE, here;
    /// the returned function serves one request against an implementation.
    /// `path` is the request path the adapter routes on (after any
    /// sub-route). Pure: serving a contract writes nothing process-wide.
    let serve
        (contract: ApiContract)
        (options: RemotingOptions<HttpContext, 'impl>)
        : (HttpContext -> 'impl) -> HttpContext -> string -> Task<bool> =
        let mounted = Mount.ofOptions contract options
        let proxy = makeApiProxyWithParse options

        fun impl http path -> task {
            let! ctx = RequestContext.ofHttp mounted http path (dispatchOf proxy impl http)
            return! run stages mounted ctx
        }

    /// Mount a composition: its contract, its non-batchable routes recorded
    /// for the batch route, and `serve` over it.
    let mount
        (options: RemotingOptions<HttpContext, 'impl>)
        : (HttpContext -> 'impl) -> HttpContext -> string -> Task<bool> =
        let contract = contractFor options
        MountedContracts.mount contract
        serve contract options

    let private warnedEmpty = ConcurrentDictionary<string, unit>()

    /// A composition with no implementation dispatches nothing — almost
    /// always a misconfiguration, so it is said once per API record.
    let warnEmptyOnce (typeKey: string) (logger: (string -> unit) option) (adapter: string) : unit =
        if warnedEmpty.TryAdd(typeKey, ()) then
            Diagnostics.outputPhase
                logger
                (sprintf
                    "ToolUp.Remoting (%s): composed with Empty implementation; no routes will dispatch. This is almost always a misconfiguration — pipe through Remoting.fromValue / .fromContext / .fromContextAsync to register a real impl."
                    adapter)
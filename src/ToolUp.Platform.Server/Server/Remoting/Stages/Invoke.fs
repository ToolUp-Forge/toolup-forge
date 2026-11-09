namespace ToolUp.Remoting.Server.Stages

open System
open System.Threading.Tasks
open ToolUp.Remoting
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Proxy

/// Stage 8: the call. A method runs through the composition's proxy, which
/// decodes its arguments (a refusal is 400 with the `Validation` envelope —
/// the handler never ran) and answers with the serialised result, or, for a
/// streaming method, with its stream source: a streaming call is a call, and
/// every stage before this one has already run for it. A companion runs its
/// typed call off the composed job dispatcher. The answer is left on the
/// context for `audit`; a refusal or a fault answers now.
[<RequireQualifiedAccess>]
module internal Invoke =

    let private settle (methodName: string) (outcome: MethodOutcome) (ctx: RequestContext) = {
        ctx with
            Settled =
                Some {
                    Method = methodName
                    Outcome = outcome
                    Chunks = None
                }
    }

    let private decodeRefusal (mount: Mount) (methodName: string) (error: DecodeError) : Response =
        // The payload mirrors the violation envelope (`methodName` + detail)
        // so both arrive under one category with one parse; `path` is the
        // segment list, so a client branching on it need not re-split prose.
        let payload =
            box {|
                methodName = methodName
                decodeError = {|
                    path = error.Path
                    expected = error.Expected
                    found = error.Found
                |}
                message = DecodeError.render error
            |}

        Response.Json(400, box (Errors.categorisedWithSchema mount.SchemaVersion ErrorCategory.Validation payload))

    let private isMultipart (ctx: RequestContext) =
        not (isNull ctx.Http.Request.ContentType)
        && ctx.Http.Request.ContentType.StartsWith("multipart/form-data", StringComparison.Ordinal)

    /// An audited method's argument, decoded by the dispatch path's own parse
    /// so the audit payload reads the value the handler received. A
    /// validated method already arrives parsed; a multipart body is not an
    /// argument array, and a parse that fails leaves the refusal to dispatch.
    let private parseForAudit (mount: Mount) (ctx: RequestContext) : Task<RequestContext> = task {
        match ctx.Parsed, mount.AuditEmitter, RequestContext.methodOf ctx with
        | ValueNone, Some _, Some m when m.Audit |> Option.exists _.Input.IsSome && not (isMultipart ctx) ->
            let! bytes = ctx.Body.Read()

            let parsed =
                try
                    ctx.Dispatch.ParseFirst ctx.Endpoint bytes
                with _ ->
                    ValueNone

            return { ctx with Parsed = parsed }
        | _ -> return ctx
    }

    let private companion
        (mount: Mount)
        (ctx: RequestContext)
        (contract: CompanionContract)
        (calls: JobCompanionCalls)
        (dispatcher: IJobDispatcher)
        : Task<StageOutcome> =
        task {
            let! bytes = ctx.Body.Read()

            let handle =
                match mount.Json with
                | SystemTextJson options ->
                    parseArguments mount.Json contract.Name 1 bytes
                    |> Result.bind (function
                        | first :: _ -> calls.DecodeHandle options first
                        | [] ->
                            Error(
                                DecodeError.at
                                    [ sprintf "%s(args)" contract.Name ]
                                    "1 argument(s) of the types JobHandle"
                                    "0 argument(s)"
                            ))

            let subject, admin =
                match ctx.Ambient with
                | Some(subject, admin) -> subject, admin
                | None -> None, false

            match handle, mount.Json with
            | Error error, _ ->
                return
                    StageOutcome.Answer(
                        settle contract.Name (MethodOutcome.Failed(DecodeException error)) ctx,
                        decodeRefusal mount contract.Name error
                    )
            | Ok handle, SystemTextJson options ->
                match contract.Kind with
                | JobCompanion.Progress ->
                    let source = calls.Progress dispatcher subject admin handle options

                    return
                        StageOutcome.Continue {
                            ctx with
                                Answer = Some(Response.Stream(contract.Name, source))
                        }
                | JobCompanion.Status
                | JobCompanion.Cancel ->
                    let call, declared =
                        match contract.Kind with
                        | JobCompanion.Cancel -> calls.Cancel, typeof<unit>
                        | _ -> calls.Status, calls.StatusType

                    let! result = call dispatcher handle |> Async.Catch |> Async.StartAsTask

                    match result with
                    | Choice1Of2 value ->
                        let answered = {
                            settle contract.Name MethodOutcome.Succeeded ctx with
                                Answer = Some(Response.Typed(200, value, declared))
                        }

                        return StageOutcome.Continue answered
                    | Choice2Of2 fault ->
                        let envelope =
                            Errors.categorisedWithSchema mount.SchemaVersion ErrorCategory.System (box fault.Message)

                        return
                            StageOutcome.Answer(
                                settle contract.Name (MethodOutcome.Failed fault) ctx,
                                Response.Json(500, box envelope)
                            )
        }

    let private method (mount: Mount) (ctx: RequestContext) : Task<StageOutcome> = task {
        let! ctx = parseForAudit mount ctx
        let output = mount.Output.GetStream "remoting-output-stream"
        ctx.Http.Response.RegisterForDispose output
        let! invoked = ctx.Dispatch.Invoke ctx.Endpoint ctx.Parsed ctx.Body.Cached output

        match invoked with
        | Invoked.Written binary ->
            let isProxy = ctx.Http.Request.Headers.ContainsKey "x-remoting-proxy"

            let contentType =
                if binary && isProxy then
                    "application/octet-stream"
                elif mount.Responses.IsJson then
                    "application/json; charset=utf-8"
                else
                    "application/vnd.msgpack"

            match ctx.Memo with
            | Some _ -> do! Idempotency.remember mount ctx 200 contentType (output.ToArray())
            | None -> ()

            let answered = {
                settle ctx.Method MethodOutcome.Succeeded ctx with
                    Answer = Some(Response.Bytes(200, contentType, output))
            }

            return StageOutcome.Continue answered
        | Invoked.Streamed source ->
            return
                StageOutcome.Continue {
                    ctx with
                        Answer = Some(Response.Stream(ctx.Method, source))
                }
        | Invoked.Refused(error, methodName) ->
            return
                StageOutcome.Answer(
                    settle ctx.Method (MethodOutcome.Failed(DecodeException error)) ctx,
                    decodeRefusal mount methodName error
                )
        | Invoked.Faulted(:? MultipartCapExceededException as cap, methodName, _) ->
            // A multipart cap breach is the caller's: 413 + `User`, so a
            // dashboard separates misuse from a server fault.
            let envelope =
                Errors.categorisedWithSchema
                    mount.SchemaVersion
                    ErrorCategory.User
                    (box (sprintf "%s: multipart-cap-exceeded: %s" methodName cap.Message))

            return
                StageOutcome.Answer(settle ctx.Method (MethodOutcome.Failed cap) ctx, Response.Json(413, box envelope))
        | Invoked.Faulted(fault, methodName, bodyText) ->
            // An unconditional stderr breadcrumb: the error envelope carries
            // no detail, and this is what reaches the host's log without any
            // logger plumbing.
            eprintfn "[remoting] EXCEPTION in %s: %s: %s" methodName (fault.GetType().FullName) fault.Message

            if not (isNull fault.StackTrace) then
                eprintfn "[remoting] stack:\n%s" fault.StackTrace

            match fault.InnerException with
            | null -> ()
            | inner -> eprintfn "[remoting] inner: %s: %s" (inner.GetType().FullName) inner.Message

            let route = {
                methodName = methodName
                path = ctx.Http.Request.Path.ToString()
                httpContext = ctx.Http
                requestBodyText = bodyText
            }

            return
                StageOutcome.Answer(
                    settle ctx.Method (MethodOutcome.Failed fault) ctx,
                    Response.Json(500, mount.Fail fault route)
                )
        | Invoked.NotRouted -> return StageOutcome.Answer(ctx, mount.NotFound ctx.Http)
        | Invoked.VerbRefused -> return StageOutcome.Answer(ctx, Response.Halt)
    }

    let stage: Stage =
        fun mount ctx ->
            match ctx.Target with
            | Target.Companion(contract, calls, dispatcher) -> companion mount ctx contract calls dispatcher
            | Target.Method _
            | Target.Foreign -> method mount ctx
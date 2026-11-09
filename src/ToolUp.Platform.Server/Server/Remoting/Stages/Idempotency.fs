namespace ToolUp.Remoting.Server.Stages

open System
open System.Threading.Tasks
open ToolUp.Remoting.Server

/// Stage 6: `[<Idempotent>]` methods, when an `IIdempotencyStore` is
/// composed. Runs BEFORE the rate limit, so a replay is not charged (the
/// original call already paid). A missing `X-Idempotency-Key` is 400; a key
/// reused with a different body is 409 (the memoised response belonged to a
/// different request, so it is not replayed); a memoised response is
/// replayed verbatim, with its own audit row (`IdempotencyReplay`) in place
/// of the method's, which the first call already wrote. Otherwise the call
/// proceeds, and `invoke` memoises its answer (`remember`).
[<RequireQualifiedAccess>]
module internal Idempotency =

    [<Literal>]
    let KeyHeader = "X-Idempotency-Key"

    let private userRefusal (mount: Mount) (status: int) (message: string) : Response =
        Response.Json(status, box (Errors.categorisedWithSchema mount.SchemaVersion ErrorCategory.User (box message)))

    let stage: Stage =
        fun mount ctx -> task {
            match RequestContext.methodOf ctx, mount.IdempotencyStore with
            | Some m, Some store when m.Idempotent ->
                match ctx.Http with
                | Header KeyHeader key ->
                    let scope = IdempotencyScope.ofCall ctx.Subject ctx.Method
                    let! memoised = store.TryGet(key, IdempotencyScope.value scope) |> Async.StartAsTask

                    match memoised with
                    | None -> return StageOutcome.Continue { ctx with Memo = Some(key, scope) }
                    | Some response ->
                        // The body hash is computed only to compare with a
                        // stored one.
                        let! mismatch = task {
                            if String.IsNullOrEmpty response.RequestBodyHash then
                                return false
                            else
                                let! hash = ctx.Body.Hash()
                                return response.RequestBodyHash <> hash
                        }

                        if mismatch then
                            return
                                StageOutcome.Answer(
                                    ctx,
                                    userRefusal
                                        mount
                                        409
                                        (sprintf
                                            "%s: idempotency-key-body-mismatch: the X-Idempotency-Key was previously used against a different request body; reuse with a fresh key or repeat the original body exactly"
                                            ctx.Method)
                                )
                        else
                            match mount.AuditEmitter with
                            | Some emitter ->
                                do!
                                    emitter.Emit {
                                        Kind = AuditKind.IdempotencyReplay
                                        MethodName = ctx.Method
                                        SubjectId = RequestContext.subjectId ctx
                                        CorrelationId = Some ctx.Correlation
                                        Timestamp = DateTimeOffset.UtcNow
                                        Payload = Map [ "idempotencyKey", key ]
                                    }
                                    |> Async.StartAsTask
                            | None -> ()

                            return StageOutcome.Answer(ctx, Response.Replay response)
                | _ ->
                    return
                        StageOutcome.Answer(
                            ctx,
                            userRefusal
                                mount
                                400
                                (sprintf
                                    "%s: missing-idempotency-key: methods marked [<Idempotent>] require the X-Idempotency-Key header"
                                    ctx.Method)
                        )
            | _ -> return StageOutcome.Continue ctx
        }

    /// Memoise a successful answer under the call's key, so the next call
    /// with it replays. A store that cannot write RAISES, and the call is then
    /// NOT memoised: a retry runs the handler again. The answer has already
    /// been made and the handler's effect has happened, so the call is not
    /// failed — the failure is reported.
    let remember (mount: Mount) (ctx: RequestContext) (status: int) (contentType: string) (body: byte[]) : Task = task {
        match ctx.Memo, mount.IdempotencyStore with
        | Some(key, scope), Some store ->
            let! hash = ctx.Body.Hash()

            let response = {
                Body = body
                StatusCode = status
                ContentType = contentType
                RequestBodyHash = hash
            }

            let! stored =
                store.Store(key, IdempotencyScope.value scope, response, mount.IdempotencyTtl)
                |> Async.Catch
                |> Async.StartAsTask

            match stored with
            | Choice1Of2() -> ()
            | Choice2Of2 failure ->
                let line =
                    sprintf
                        "[remoting] idempotency store write FAILED in %s — the call was answered but is NOT memoised; a retry with the same X-Idempotency-Key re-executes the handler: %s: %s"
                        ctx.Method
                        (failure.GetType().FullName)
                        failure.Message

                eprintfn "%s" line
                mount.Logger |> Option.iter (fun log -> log line)
        | _ -> ()
    }
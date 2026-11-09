namespace ToolUp.Remoting.Server.Stages

open System
open Microsoft.Extensions.Primitives
open ToolUp.Remoting.Server

/// Stage 7: `[<RateLimit>]` budgets, when an `IRemotingRateLimitStore` is composed.
/// Every budget must pass (AND), charged against the caller's subject key.
/// An exhausted budget is 429 with `Retry-After`, the categorised
/// `RateLimit` envelope, a `RateLimited` telemetry outcome (separable from
/// handler errors on a dashboard) and a `RateLimitExceeded` audit row,
/// whether or not the method is itself audited.
[<RequireQualifiedAccess>]
module internal RateLimit =

    let stage: Stage =
        fun mount ctx -> task {
            let budgets =
                RequestContext.methodOf ctx
                |> Option.map (fun m -> m.Budgets |> List.map ToolUp.Remoting.Server.RateLimit.ofBudget)
                |> Option.defaultValue []

            match mount.RateLimitStore, budgets with
            | Some store, (_ :: _) ->
                let subject = SubjectKey.value ctx.Subject

                let! decision =
                    ToolUp.Remoting.Server.RateLimit.evaluate store subject ctx.Method budgets
                    |> Async.StartAsTask

                match decision with
                | ToolUp.Platform.Admission.Admit _ -> return StageOutcome.Continue ctx
                | ToolUp.Platform.Admission.Deny retryAfter ->
                    let seconds = ToolUp.Platform.RateLimitedError.retryAfterSeconds retryAfter
                    ctx.Http.Response.Headers["Retry-After"] <- StringValues(string seconds)

                    match mount.AuditEmitter with
                    | Some emitter ->
                        do!
                            emitter.Emit {
                                Kind = AuditKind.RateLimitExceeded
                                MethodName = ctx.Method
                                SubjectId = subject
                                CorrelationId = Some ctx.Correlation
                                Timestamp = DateTimeOffset.UtcNow
                                Payload = Map.ofList [ "retryAfterSeconds", string seconds ]
                            }
                            |> Async.StartAsTask
                    | None -> ()

                    let envelope =
                        Errors.categorisedWithSchema
                            mount.SchemaVersion
                            ErrorCategory.RateLimit
                            (box (sprintf "%s: rate-limit-exceeded: retry after %ds" ctx.Method seconds))

                    let settled = {
                        ctx with
                            Settled =
                                Some {
                                    Method = ctx.Method
                                    Outcome = MethodOutcome.RateLimited retryAfter
                                    Chunks = None
                                }
                    }

                    return StageOutcome.Answer(settled, Response.Json(429, box envelope))
            | _ -> return StageOutcome.Continue ctx
        }
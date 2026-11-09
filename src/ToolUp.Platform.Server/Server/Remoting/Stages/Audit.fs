namespace ToolUp.Remoting.Server.Stages

open System
open System.Threading.Tasks
open ToolUp.Remoting.Server

/// Stage 9: the `[<Audit>]` row of a call that succeeded — a written result,
/// or a stream that opened. Its payload is the PII-redacted snapshot of the
/// input record (`[<PiiSafe>]` fields only), read from the argument the
/// dispatch decoded; a method with no input record, or a body that was not an
/// argument array, audits an empty payload. A replay never reaches this
/// stage: its own `IdempotencyReplay` row was written instead.
[<RequireQualifiedAccess>]
module internal Audit =

    let stage: Stage =
        fun mount ctx -> task {
            match mount.AuditEmitter, RequestContext.methodOf ctx, ctx.Answer with
            | Some emitter, Some m, Some(Response.Bytes _ | Response.Stream _) ->
                match m.Audit with
                | Some audit ->
                    let payload =
                        match m.Validated |> Option.orElse audit.Input, ctx.Parsed with
                        | Some input, ValueSome parsed ->
                            ToolUp.Remoting.Server.Audit.payloadFromInputRecord input parsed.First
                        | _ -> Map.empty

                    do!
                        emitter.Emit {
                            Kind = audit.Kind
                            MethodName = ctx.Method
                            SubjectId = RequestContext.subjectId ctx
                            CorrelationId = Some ctx.Correlation
                            Timestamp = DateTimeOffset.UtcNow
                            Payload = payload
                        }
                        |> Async.StartAsTask
                | None -> ()
            | _ -> ()

            return StageOutcome.Continue ctx
        }
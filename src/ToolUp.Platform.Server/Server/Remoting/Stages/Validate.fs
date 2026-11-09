namespace ToolUp.Remoting.Server.Stages

open System.Threading.Tasks
open ToolUp.Remoting.Server

/// Stage 5: typed validation of a method whose first argument's record
/// carries validation attributes. The argument is parsed and decoded by the
/// proxy's own path (`ParseFirst`) and handed on, so the body is parsed once
/// and validation sees exactly the value the handler receives. A violation
/// is 400 with the `Validation` envelope; a body that does not parse or
/// decode skips validation and leaves the refusal to the dispatch.
[<RequireQualifiedAccess>]
module internal Validate =

    let stage: Stage =
        fun mount ctx -> task {
            match RequestContext.methodOf ctx |> Option.bind _.Validated, mount.Json with
            | Some input, SystemTextJson _ ->
                let! bytes = ctx.Body.Read()

                match ctx.Dispatch.ParseFirst ctx.Endpoint bytes with
                | ValueNone -> return StageOutcome.Continue ctx
                | ValueSome parsed ->
                    let parsedCtx = { ctx with Parsed = ValueSome parsed }

                    // A `[<Custom>]` field validator sees the caller and the
                    // call's correlation id.
                    let validationContext =
                        { new IValidationContext with
                            member _.SubjectId = RequestContext.subjectId ctx
                            member _.CorrelationId = Some ctx.Correlation
                        }

                    match Validation.evaluate mount.ValidationMessages validationContext input parsed.First with
                    | [] -> return StageOutcome.Continue parsedCtx
                    | violations ->
                        let payload =
                            box {|
                                methodName = ctx.Method
                                violations = violations
                            |}

                        let envelope =
                            Errors.categorisedWithSchema mount.SchemaVersion ErrorCategory.Validation payload

                        return StageOutcome.Answer(parsedCtx, Response.Json(400, box envelope))
            | _ -> return StageOutcome.Continue ctx
        }
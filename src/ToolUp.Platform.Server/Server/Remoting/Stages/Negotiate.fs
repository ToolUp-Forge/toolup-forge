namespace ToolUp.Remoting.Server.Stages

open System.Threading.Tasks
open ToolUp.Remoting.Server

/// Stage 2: the wire schema. The caller's `X-Remoting-Schema` is negotiated
/// against the method's served versions; a version the method does not serve
/// is refused (400, `ErrorCategory.User`, the supported vector in the
/// payload) — serving the wrong shape to a client that said which shape it
/// wanted is the one failure the header exists to prevent. Otherwise the
/// route's trailing segment is rewritten to the negotiated version's
/// handler, so every later stage — authorisation included — keys on the
/// handler that actually runs: `GetThing_V2` carries its own markers.
[<RequireQualifiedAccess>]
module internal Negotiate =

    let stage: Stage =
        fun mount ctx ->
            let requested =
                match ctx.Http.Request.Headers.TryGetValue SchemaVersion.Header with
                | true, values -> Some(values.ToString())
                | false, _ -> None

            let routing =
                SchemaVersion.negotiate
                    mount.Contract.Schemas
                    mount.Addressable
                    mount.SchemaVersion
                    requested
                    ctx.Addressed

            match routing with
            | SchemaRouting.Refused(requested, supported) ->
                let envelope =
                    Errors.categorisedWithSchema
                        mount.SchemaVersion
                        ErrorCategory.User
                        (SchemaVersion.refusalPayload ctx.Addressed requested supported)

                Task.FromResult(StageOutcome.Answer(ctx, Response.Json(400, box envelope)))
            | _ ->
                // One line per call to a version scheduled for retirement,
                // naming the version rather than the handler.
                match routing with
                | SchemaRouting.Serve(_, version, Some retireAfter) ->
                    SchemaVersion.logDeprecation mount.Logger ctx.Addressed version retireAfter
                | _ -> ()

                let endpoint = SchemaVersion.applyRouting routing ctx.Endpoint
                let methodName = Route.methodSegment endpoint

                Task.FromResult(
                    StageOutcome.Continue {
                        ctx with
                            Routing = routing
                            Endpoint = endpoint
                            Method = methodName
                            Target = RequestContext.target mount endpoint
                            Body = LazyBody(ctx.Http, methodName)
                    }
                )
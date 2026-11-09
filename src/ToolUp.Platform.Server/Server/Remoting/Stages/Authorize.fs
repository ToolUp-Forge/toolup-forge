namespace ToolUp.Remoting.Server.Stages

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server

/// Stage 4: who is calling, and may they. Runs only when an
/// `AuthContextResolver` is composed; without one every method is allowed.
///
/// A method's classification is evaluated against the resolved caller
/// (`AuthClassifier.evaluate`); a `[<PublicEndpoint>]` method never consults
/// the resolver. A denial is 401 with the categorised `Auth` envelope,
/// whatever the composed error handler says: the per-request reason names
/// which rule failed, so it goes to the diagnostics logger and the wire
/// carries the constant `"auth-denied"`. A companion carries no marker:
/// its caller is resolved so the dispatcher can bind the job to its owner
/// (the submitting subject, or a holder of `JobAdminRole`), which is the
/// companion's guard. A route no method of this record owns is passed
/// through untouched: in a multi-API composition it is another API's.
///
/// This stage runs before argument decoding, so an unauthenticated caller
/// learns nothing of a method's argument shape.
[<RequireQualifiedAccess>]
module internal Authorize =

    let private remoteIp (mount: Mount) (http: HttpContext) : string option =
        let socket () =
            match http.Connection.RemoteIpAddress with
            | null -> None
            | address -> Some(address.ToString())

        match mount.RemoteIp with
        | Some resolve -> resolve http |> Option.orElseWith socket
        | None -> socket ()

    let private resolve (mount: Mount) (http: HttpContext) : Task<IAuthContext option> = task {
        match mount.Resolver with
        | Some resolver ->
            let! caller = resolver http |> Async.StartAsTask
            return Some caller
        | None -> return None
    }

    let stage: Stage =
        fun mount ctx -> task {
            let! caller, ambient, decision = task {
                match mount.Resolver, ctx.Target with
                | Some _, Target.Companion _ ->
                    let! caller = resolve mount ctx.Http

                    let admin =
                        caller |> Option.exists (fun authCtx -> authCtx.HasRole mount.JobAdminRole)

                    return caller, (LongRunning.subjectOf caller, admin), AuthDecision.Allow
                | Some _, Target.Method m ->
                    match m.Classification |> Option.map AuthClassifier.ofClassified with
                    | Some MethodClassification.Public
                    | None -> return None, (None, false), AuthDecision.Allow
                    | Some classification ->
                        let! caller = resolve mount ctx.Http

                        return
                            caller, (LongRunning.subjectOf caller, false), AuthClassifier.evaluate classification caller
                | _ -> return None, (None, false), AuthDecision.Allow
            }

            let next = {
                ctx with
                    Caller = caller
                    Subject = SubjectKey.ofCaller caller (remoteIp mount ctx.Http)
                    Ambient = Some ambient
            }

            match decision with
            | AuthDecision.Allow -> return StageOutcome.Continue next
            | AuthDecision.Deny reason ->
                mount.Logger
                |> Option.iter (fun log -> log (sprintf "%s: auth-denied: %s" ctx.Method reason))

                let envelope =
                    Errors.categorisedWithSchema mount.SchemaVersion ErrorCategory.Auth (box "auth-denied")

                return StageOutcome.Answer(next, Response.Json(401, box envelope))
        }
namespace ToolUp.Remoting.Server.Stages

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives

/// A header's value, when the request carries it non-blank.
[<AutoOpen>]
module internal Headers =

    let (|Header|_|) (name: string) (http: HttpContext) : string option =
        match http.Request.Headers.TryGetValue name with
        | true, values when not (String.IsNullOrWhiteSpace(values.ToString())) -> Some(values.ToString())
        | _ -> None

/// Stage 1: the call's correlation id — the caller's `x-correlation-id`, or a
/// fresh one — stamped on the response. The pipeline opens the call context
/// with it, so the handler, the audit rows and every stream chunk's id carry
/// the same token.
[<RequireQualifiedAccess>]
module internal Correlate =

    [<Literal>]
    let HeaderName = "x-correlation-id"

    let stage: Stage =
        fun _ ctx ->
            let correlation =
                match ctx.Http with
                | Header HeaderName supplied -> supplied
                | _ -> Guid.NewGuid().ToString("N")

            ctx.Http.Response.Headers[HeaderName] <- StringValues correlation
            Task.FromResult(StageOutcome.Continue { ctx with Correlation = correlation })
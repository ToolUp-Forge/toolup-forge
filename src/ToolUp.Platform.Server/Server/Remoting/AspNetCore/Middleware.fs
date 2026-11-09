namespace ToolUp.Remoting.AspNetCore

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http

open System.Threading.Tasks
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Stages

type HttpFuncResult = Task<HttpContext option>
type HttpFunc = HttpContext -> HttpFuncResult
type HttpHandler = HttpFunc -> HttpFunc

[<AutoOpen>]
module Extensions =
    type HttpContext with
        member self.GetService<'t>() =
            self.RequestServices.GetService(typeof<'t>) :?> 't

/// The ASP.NET Core mounting of the request pipeline: the same stages the
/// Giraffe adapter runs, so every seam composed on the options is enforced
/// here too.
module internal Middleware =

    /// Used to forward the Http context
    let halt: HttpHandler = fun (_: HttpFunc) (_: HttpContext) -> Task.FromResult None

    let buildFromImplementation<'impl>
        (options: RemotingOptions<HttpContext, 'impl>)
        : (HttpContext -> 'impl) -> HttpHandler =
        let serve = Pipeline.mount options

        fun (implBuilder: HttpContext -> 'impl) (next: HttpFunc) (ctx: HttpContext) -> task {
            match! serve implBuilder ctx ctx.Request.Path.Value with
            | true -> return! next ctx
            | false -> return None
        }

type RemotingMiddleware<'t>(next: RequestDelegate, handler: HttpFunc) =

    do
        if isNull next then
            nullArg "next"

    member __.Invoke(ctx: HttpContext) = task {
        match! handler ctx with
        | Some _ -> ()
        | None -> return! next.Invoke ctx
    }

[<AutoOpen>]
module AppBuilderExtensions =
    type IApplicationBuilder with
        member this.UseRemoting(options: RemotingOptions<HttpContext, 't>) =
            let handler: HttpHandler =
                match options.Implementation with
                | ProtocolImplementation.Empty ->
                    Pipeline.warnEmptyOnce typeof<'t>.FullName options.DiagnosticsLogger "AspNetCore"
                    Middleware.halt
                | ProtocolImplementation.StaticValue impl -> Middleware.buildFromImplementation options (fun _ -> impl)
                | ProtocolImplementation.FromContext createImplementationFrom ->
                    Middleware.buildFromImplementation options createImplementationFrom
                | ProtocolImplementation.FromContextAsync createImplementationFromAsync ->
                    // The pipeline is mounted ONCE, here; per request only the
                    // async resolver is awaited.
                    let dispatch = Middleware.buildFromImplementation options

                    fun (next: HttpFunc) (ctx: HttpContext) -> task {
                        let! impl = createImplementationFromAsync ctx |> Async.StartAsTask
                        return! dispatch (fun _ -> impl) next ctx
                    }

            this.UseMiddleware<RemotingMiddleware<'t>>(handler (Some >> Task.FromResult))
            |> ignore
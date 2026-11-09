namespace ToolUp.Remoting.Giraffe

open Microsoft.AspNetCore.Http

open Giraffe
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Stages

// Assembly-private: the adapter's mounting, reached by consumers only through
// `Remoting.buildHttpHandler`. Tests see it through InternalsVisibleTo. The
// request itself is the pipeline's (`Stages/Pipeline.fs`), which the ASP.NET
// Core middleware runs too.
module internal GiraffeUtil =

    /// The contract a composition dispatches over, bound to its flags; a
    /// composition that makes a refusal of the record refuses to start.
    let contractFor<'impl> (options: RemotingOptions<HttpContext, 'impl>) : ApiContract = Pipeline.contractFor options

    /// Build the dispatcher table ONCE for a contract. The returned function
    /// accepts a per-request `implBuilder` and yields the `HttpHandler` that
    /// runs one request through the pipeline. A route no method of the record
    /// owns is passed on (`None`), so several APIs compose under `choose`.
    let buildDispatcherTableFor<'impl> (contract: ApiContract) (options: RemotingOptions<HttpContext, 'impl>) =
        let serve = Pipeline.serve contract options

        fun (implBuilder: HttpContext -> 'impl) ->
            fun (next: HttpFunc) (ctx: HttpContext) -> task {
                match! serve implBuilder ctx (SubRouting.getNextPartOfPath ctx) with
                | true -> return! next ctx
                | false -> return None
            }

    /// Mount a composition: its contract (refused at compose time when the
    /// composition makes a refusal of the record), its non-batchable routes
    /// recorded for the batch route, and its dispatcher table, built once.
    let buildDispatcherTable<'impl> (options: RemotingOptions<HttpContext, 'impl>) =
        let contract = contractFor options
        MountedContracts.mount contract
        buildDispatcherTableFor contract options

module Remoting =

    /// Builds a HttpHandler from the given implementation and options
    let buildHttpHandler (options: RemotingOptions<HttpContext, 't>) =
        match options.Implementation with
        | ProtocolImplementation.Empty ->
            Pipeline.warnEmptyOnce typeof<'t>.FullName options.DiagnosticsLogger "Giraffe"
            fun _ _ -> skipPipeline
        | ProtocolImplementation.StaticValue impl -> GiraffeUtil.buildDispatcherTable options (fun _ -> impl)
        | ProtocolImplementation.FromContext createImplementationFrom ->
            GiraffeUtil.buildDispatcherTable options createImplementationFrom
        | ProtocolImplementation.FromContextAsync createImplementationFromAsync ->
            // The table is built ONCE, here; per request only the async
            // resolver is awaited and its implementation handed to it.
            let dispatch = GiraffeUtil.buildDispatcherTable options

            fun (next: HttpFunc) (ctx: HttpContext) -> task {
                let! impl = createImplementationFromAsync ctx |> Async.StartAsTask
                return! dispatch (fun _ -> impl) next ctx
            }
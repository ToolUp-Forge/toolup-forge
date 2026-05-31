namespace ToolUp.Platform

// Server-side `Api.make` helper — replicates the surface of
// `SAFE.Api.make` from SAFE.Server.Utils (MIT, Copyright 2024
// Compositional IT) so server call sites can continue using
// `Api.make (builder, errorHandler = eh)` unchanged after the
// ToolUp.Platform SAFE removal. See Shared/Api.fs for the DU half
// (`ApiCall`, `RemoteData`) and Client/Api.fs for the Fable-side
// `Api.makeProxy` helper.
//
// This file is injected via ToolUp.Platform.Server.props into every
// server-hosting project; ToolUp.Platform.dll itself intentionally
// does not depend on ToolUp.Remoting.Giraffe.

open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open Giraffe

/// Server-side Fable Remoting helper. Mirrors SAFE.Api.make so server
/// call sites keep using `Api.make (builder, errorHandler = eh)`.
type Api =
    /// Build a Fable Remoting HttpHandler from an `HttpContext -> 'T`
    /// api builder. Matches SAFE.Api.make's signature: optional route
    /// builder, error handler, and remoting-options customiser.
    static member make<'T>
        (
            api: HttpContext -> 'T,
            ?routeBuilder: string -> string -> string,
            ?errorHandler: exn -> RouteInfo<HttpContext> -> ErrorResult,
            ?customOptions: RemotingOptions<HttpContext, 'T> -> RemotingOptions<HttpContext, 'T>
        ) : HttpHandler =
        let routeBuilder = defaultArg routeBuilder (sprintf "/api/%s/%s")
        let customOptions = defaultArg customOptions id

        Remoting.createApi ()
        |> Remoting.withRouteBuilder routeBuilder
        |> Remoting.fromContext api
        |> (match errorHandler with
            | Some eh -> Remoting.withErrorHandler eh
            | None -> id)
        |> customOptions
        |> Remoting.buildHttpHandler
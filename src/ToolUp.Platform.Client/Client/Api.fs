// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// Client-side `Api.makeProxy` helper — replicates the surface of
// `SAFE.Api.makeProxy` from SAFE.Client.Utils (MIT, Copyright 2017
// SAFE-Stack) so client call sites can continue using
// `Api.makeProxy<T> (customOptions = ...)` unchanged after the
// ToolUp.Platform SAFE removal. See Shared/Api.fs for the DU half
// (`ApiCall`, `RemoteData`) and Server/Api.fs for the server-side
// `Api.make` helper.
//
// This file is injected via ToolUp.Platform.Client.props into every
// Fable-compiled client project; ToolUp.Platform.dll itself
// intentionally does not depend on ToolUp.Remoting.Client.

open ToolUp.Remoting.Client

/// Client-side Fable Remoting helper. Mirrors SAFE.Api.makeProxy so
/// client call sites keep using `Api.makeProxy<T> (customOptions = ...)`.
type Api =
    /// Build a Fable Remoting proxy bound to the caller's type. Matches
    /// SAFE.Api.makeProxy's signature: optional route builder and
    /// optional remoting-options customiser (used for request headers,
    /// multipart optimisation, binary serialisation, etc.).
    static member inline makeProxy<'TApi>
        (?routeBuilder: string -> string -> string, ?customOptions: RemoteBuilderOptions -> RemoteBuilderOptions)
        : 'TApi =
        let routeBuilder = defaultArg routeBuilder (sprintf "/api/%s/%s")
        let customOptions = defaultArg customOptions id

        Remoting.createApi ()
        |> Remoting.withRouteBuilder routeBuilder
        |> customOptions
        |> Remoting.buildProxy<'TApi>
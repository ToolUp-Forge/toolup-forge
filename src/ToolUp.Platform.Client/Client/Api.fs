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

open System
open ToolUp.Remoting.Client

/// Client-side Fable Remoting helper. Mirrors SAFE.Api.makeProxy so
/// client call sites keep using `Api.makeProxy<T> (customOptions = ...)`.
type Api =
    /// Phase 853 — the generated proxy for the record `apiKey` names, when
    /// one exists and admits `options`: a builder a composition registered
    /// (`GeneratedProxies`), then the platform's own generated records
    /// (`PlatformClientProxies`).
    static member tryGenerated(apiKey: string, options: RemoteBuilderOptions) : obj option =
        match GeneratedProxies.tryBuild apiKey options with
        | Some proxy -> Some proxy
        | None when GeneratedProxies.admits options -> PlatformClientProxies.tryBuild apiKey options
        | None -> None

    /// Phase 853 — resolve `apiType`'s proxy: GENERATED first — a record
    /// value of closures over generated encoders and decoders, built with no
    /// reflection — and otherwise a proxy whose reflective build
    /// (`Remoting.buildProxy`) waits for its first call, so boot pays for no
    /// proxy it does not call. That first call looks for a generated builder
    /// again, so one registered at composition, after this proxy was made,
    /// is still the one used.
    static member resolveProxy(options: RemoteBuilderOptions, apiType: Type) : obj =
        let key = GeneratedProxies.keyOf apiType

        match Api.tryGenerated (key, options) with
        | Some proxy -> proxy
        | None ->
            Remoting.buildLazyProxy (
                apiType,
                fun () ->
                    match Api.tryGenerated (key, options) with
                    | Some proxy -> proxy
                    | None -> box (Remoting.buildProxy (options, apiType))
            )

    /// Build a Fable Remoting proxy bound to the caller's type. Matches
    /// SAFE.Api.makeProxy's signature: optional route builder and
    /// optional remoting-options customiser (used for request headers,
    /// multipart optimisation, binary serialisation, etc.).
    ///
    /// Phase 853 — generated-first, reflective-on-first-call otherwise
    /// (`resolveProxy`). A call site is unchanged.
    static member inline makeProxy<'TApi>
        (?routeBuilder: string -> string -> string, ?customOptions: RemoteBuilderOptions -> RemoteBuilderOptions)
        : 'TApi =
        let routeBuilder = defaultArg routeBuilder (sprintf "/api/%s/%s")
        let customOptions = defaultArg customOptions id

        let options =
            Remoting.createApi () |> Remoting.withRouteBuilder routeBuilder |> customOptions

        unbox<'TApi> (Api.resolveProxy (options, typeof<'TApi>))
// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Client

open Fable.Core
open Fable.SimpleJson
open System
open Microsoft.FSharp.Reflection
open ToolUp.Remoting

module Remoting =
    /// Starts with default configuration for building a proxy
    let createApi () = {
        CustomHeaders = []
        BaseUrl = None
        Authorization = None
        WithCredentials = false
        RouteBuilder = sprintf ("/%s/%s")
        CustomResponseSerialization = None
        IsMultipartEnabled = false
    }

    /// Defines how routes are built using the type name and method name. By default, the generated routes are of the form `/typeName/methodName`.
    let withRouteBuilder builder (options: RemoteBuilderOptions) = { options with RouteBuilder = builder }

    /// Sets the base url for the request. Useful if you are making cross-domain requests
    let withBaseUrl url (options: RemoteBuilderOptions) = { options with BaseUrl = Some url }

    /// Adds custom headers to each request of the proxy.
    ///
    /// **ToolUp deployments: do NOT use this for the guard-owned keys**
    /// (`Authorization`, `X-User-Id`, `X-CSRF-Token`, `x-correlation-id`).
    /// The SDK's request guard (`CsrfClient.installRequestGuard`, wired
    /// by `SDK.Client.program`) attaches those at *send* time from the
    /// live identity / token caches — that is the sanctioned seam, and
    /// it is correct no matter when the proxy was built. A header set
    /// here is frozen at proxy-build time: it goes stale on sign-in /
    /// token refresh, and the guard (which tracks every header write)
    /// will defer to it rather than overwrite — so you keep sending the
    /// stale value. Use this helper only for app-specific keys the
    /// guard doesn't own.
    let withCustomHeader headers (options: RemoteBuilderOptions) = { options with CustomHeaders = headers }

    /// Phase 69j — pin the wire-schema version this proxy expects, sending
    /// it as `X-Remoting-Schema: <version>` on every request.
    ///
    /// **Opt-in, and deliberately not a default.** Sending the header on
    /// every proxy would change the request bytes of every existing
    /// deployment on upgrade, which GP 11 forbids — and it would do so to
    /// assert a version the caller never chose. A proxy that sends nothing
    /// is served the server's configured default exactly as before; a
    /// proxy that sends a version the server does not serve is REFUSED with
    /// the supported vector rather than served a shape it cannot read,
    /// which is the whole reason to opt in.
    ///
    /// APPENDS rather than replacing, so it composes in any order with
    /// `withCustomHeader`. `withSchemaVersion` twice is the caller
    /// contradicting themselves; the last one applied wins, matching how
    /// the send path reads duplicate header pairs.
    let withSchemaVersion (version: int) (options: RemoteBuilderOptions) = {
        options with
            CustomHeaders =
                (options.CustomHeaders
                 |> List.filter (fun (name, _) -> name <> "X-Remoting-Schema"))
                @ [ "X-Remoting-Schema", string version ]
    }

    /// Sets the authorization header of every request from the proxy.
    ///
    /// **ToolUp deployments: avoid this.** The SDK's request guard
    /// (`CsrfClient.installRequestGuard`) already attaches the live
    /// `Authorization` identity header at *send* time on every eligible
    /// `/api/*` request. A token passed here is frozen at proxy-build
    /// time — it goes stale on sign-in / refresh, and because the guard
    /// defers to headers the proxy set itself, the stale value is what
    /// the server keeps seeing. Only reach for this on a proxy that
    /// deliberately bypasses the guard (e.g. a cross-origin API the
    /// guard excludes).
    let withAuthorizationHeader token (options: RemoteBuilderOptions) = {
        options with
            Authorization = Some token
    }

    /// Sets the withCredentials option on the XHR request, which is useful for CORS scenarios
    let withCredentials withCredentials (options: RemoteBuilderOptions) = {
        options with
            WithCredentials = withCredentials
    }

    /// Specifies that the API uses binary serialization for responses
    let withBinarySerialization (options: RemoteBuilderOptions) =
        // Phase 785 — the algebra path's opt-in registrations. Made here
        // because this function is the ONE production consumer of the
        // MsgPack reader in the tree, so "the binary wire is in use" and
        // "the platform's decoders are available" become the same event.
        // Idempotent, and a proxy that never opts into binary
        // serialization registers nothing and pays nothing (GP 13).
        PlatformDecoders.registerAll ()

        let serializer response returnType =
            // Phase 783 — read through the refusing entry. A malformed
            // reply raises `DecodeException` carrying a named
            // `DecodeError`; `Proxy.proxyFetch`'s 200 arm converts it into
            // a `ProxyRequestException` whose `DecodeError` is `Some`, so
            // the caller distinguishes "the server's reply did not decode"
            // from "the server returned an error" without reading prose.
            //
            // `CustomResponseSerializer` is `byte[] -> Type -> obj` and
            // stays that way: making it return a `Result` would retype a
            // public seam every consumer implements, to say something the
            // exception already says at the one call site that can act on
            // it.
            // Phase 785 — the opt-in branch. A return type with a
            // registered algebra decoder is read ONCE into the closed
            // value model and then decoded by total combinators; a type
            // without one takes the reflection path byte-for-byte as
            // before (GP 11). The two produce the same value on every
            // Phase 784 corpus shape — `DecoderAlgebraTests` runs both
            // and compares, accept path and refuse path.
            //
            // A registry MISS is the reflection path, never an error:
            // the lookup is by `Type.FullName`, and a generic
            // instantiation this host renders differently costs the
            // algebra rather than the decode.
            let decoded =
                match RemotingDecoders.tryGet returnType with
                | Some decoder -> MsgPack.Read.Reader(response).TryReadValue() |> Result.bind decoder
                | None -> MsgPack.Read.Reader(response).TryRead returnType

            match decoded with
            | Ok value -> value
            | Error error -> raise (DecodeException error)

        {
            options with
                CustomResponseSerialization = Some serializer
        }

    /// Enables top level byte array parameters (such as in `upload: Metadata -> byte[] -> Async<UploadResult>`) to be sent with minimal overhead using multipart/form-data.
    ///
    /// !!! ToolUp.Remoting.Suave servers do not support this option.
    let withMultipartOptimization options = {
        options with
            IsMultipartEnabled = true
    }

type Remoting() =
    /// For internal library use only.
    static member buildProxy(options: RemoteBuilderOptions, resolvedType: Type) =
        let schemaType = createTypeInfo resolvedType

        match schemaType with
        | TypeInfo.Record getFields ->
            let (fields, recordType) = getFields ()

            let fieldTypes =
                Reflection.FSharpType.GetRecordFields recordType
                |> Array.map (fun prop -> prop.Name, prop.PropertyType)

            let recordFields = [|
                for field in fields do
                    let normalize n =
                        let fieldType =
                            fieldTypes
                            |> Array.pick (fun (name, typ) -> if name = field.FieldName then Some typ else None)

                        let fn = Proxy.proxyFetch options recordType.Name field fieldType

                        match n with
                        | 0 -> box (fn null null null null null null null null)
                        | 1 -> box (fun a -> fn a null null null null null null null)
                        | 2 ->
                            let proxyF a b = fn a b null null null null null null
                            unbox (System.Func<_, _, _> proxyF)
                        | 3 ->
                            let proxyF a b c = fn a b c null null null null null
                            unbox (System.Func<_, _, _, _> proxyF)
                        | 4 ->
                            let proxyF a b c d = fn a b c d null null null null
                            unbox (System.Func<_, _, _, _, _> proxyF)
                        | 5 ->
                            let proxyF a b c d e = fn a b c d e null null null
                            unbox (System.Func<_, _, _, _, _, _> proxyF)
                        | 6 ->
                            let proxyF a b c d e f = fn a b c d e f null null
                            unbox (System.Func<_, _, _, _, _, _, _> proxyF)
                        | 7 ->
                            let proxyF a b c d e f g = fn a b c d e f g null
                            unbox (System.Func<_, _, _, _, _, _, _, _> proxyF)
                        | 8 ->
                            let proxyF a b c d e f g h = fn a b c d e f g h
                            unbox (System.Func<_, _, _, _, _, _, _, _, _> proxyF)
                        | _ ->
                            failwithf
                                "Cannot generate proxy function for %s. Only up to 8 arguments are supported. Consider using a record type as input"
                                field.FieldName

                    let argumentCount =
                        match field.FieldType with
                        | TypeInfo.Async _ -> 0
                        | TypeInfo.Promise _ -> 0
                        | TypeInfo.Func getArgs -> Array.length (getArgs ()) - 1
                        | _ -> 0

                    // Phase 69c.D — a streaming field gets the cold-stream
                    // proxy instead of the request/response one.
                    match Proxy.tryStreamingElementType field.FieldType with
                    | Some elementType -> box (Proxy.proxyStream options recordType.Name field elementType)
                    | None -> normalize argumentCount
            |]

            let proxy = FSharpValue.MakeRecord(recordType, recordFields)
            unbox proxy
        | _ ->
            failwithf
                "Cannot build proxy. Exepected type %s to be a valid protocol definition which is a record of functions"
                resolvedType.FullName

    static member inline buildProxy<'t>(options: RemoteBuilderOptions) : 't =
        Remoting.buildProxy (options, typeof<'t>)
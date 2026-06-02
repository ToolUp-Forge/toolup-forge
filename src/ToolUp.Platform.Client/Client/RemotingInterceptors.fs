// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Elmish
open Fable.SimpleJson
open ToolUp.Remoting.Client

/// 0.4.1 — categorised error envelope surfaced from `ToolUp.Remoting`'s
/// Phase 69b.E `CategorisedErrorResult` shape. When the server returns
/// `Errors.PropagateCategorised(category, error)`, the response body is
/// `{ error, ignored, handled, category, __schema_version }`. The forge
/// SDK's `RemotingInterceptors.bridge` parses this on the client side
/// and raises this typed exception so downstream `ofError` handlers can
/// pattern-match on the category without parsing message text.
[<RequireQualifiedAccess>]
type ErrorCategory =
    | User
    | System
    | RateLimit
    | Auth
    | NotFound
    | Validation
    | Unknown of raw: string

module ErrorCategory =

    /// Parse the wire string form (case-insensitive) into the typed DU.
    let ofWire (raw: string) : ErrorCategory =
        match raw with
        | null -> ErrorCategory.Unknown ""
        | s ->
            match s.ToLowerInvariant() with
            | "user" -> ErrorCategory.User
            | "system" -> ErrorCategory.System
            | "ratelimit"
            | "rate-limit" -> ErrorCategory.RateLimit
            | "auth" -> ErrorCategory.Auth
            | "notfound"
            | "not-found" -> ErrorCategory.NotFound
            | "validation" -> ErrorCategory.Validation
            | other -> ErrorCategory.Unknown other

/// Typed exception raised by the categorised-error bridge interceptor.
/// Carries the parsed `ErrorCategory`, the raw error body (still a
/// JSON-text string — callers can re-parse for their domain shape),
/// and the wrapped `ProxyRequestException` as the inner exception for
/// stack-trace continuity.
type RemotingCategorisedException(category: ErrorCategory, errorBody: string, inner: exn) =
    inherit Exception(sprintf "Remoting error (%A): %s" category errorBody, inner)
    member _.Category = category
    member _.ErrorBody = errorBody

/// 0.4.1 — forge-side bridge that registers
/// `Cmd.OfRemoting.IRemotingInterceptor` instances at boot time. The
/// `categorisedErrorBridge` parses Phase 69b.E `CategorisedErrorResult`
/// envelopes out of `ProxyRequestException.ResponseBody` and substitutes
/// a typed `RemotingCategorisedException`; downstream `ofError` handlers
/// can `match ex with :? RemotingCategorisedException as cex -> ...`.
[<RequireQualifiedAccess>]
module RemotingInterceptors =

    let private log = Logger.forCategory "client.remoting.interceptors"

    /// Per-spec body shape — matches `ToolUp.Remoting.Server.Types.CategorisedErrorResult`.
    /// `error` is the raw caller-supplied error obj (JSON-encoded by the
    /// server); we keep it as a string so the parser doesn't need to know
    /// the domain shape.
    type private CategorisedBody = {
        error: obj
        ignored: bool
        handled: bool
        category: string
        __schema_version: int
    }

    /// Try to parse a `ProxyRequestException`'s `ResponseBody` as a Phase
    /// 69b.E categorised envelope. Returns `None` when the body is empty,
    /// non-JSON, or lacks the `category` field — those cases fall back
    /// to the original exception unchanged.
    let private tryParseCategorised (responseBody: string) : (ErrorCategory * string) option =
        if String.IsNullOrWhiteSpace responseBody then
            None
        else
            try
                let parsed = SimpleJson.parse responseBody

                match parsed with
                | JObject fields ->
                    // Fable.SimpleJson's JObject carries a Map<string, Json>.
                    let categoryValue = fields |> Map.tryFind "category"
                    let errorValue = fields |> Map.tryFind "error"

                    match categoryValue with
                    | Some(JString categoryStr) ->
                        let category = ErrorCategory.ofWire categoryStr

                        let errorAsString =
                            match errorValue with
                            | Some(JString s) -> s
                            | Some other -> SimpleJson.toString other
                            | None -> ""

                        Some(category, errorAsString)
                    | _ -> None
                | _ -> None
            with ex ->
                log.Debug $"categorised-envelope parse failed: {ex.Message}"
                None

    /// Bridge interceptor — installed at boot. On a `ProxyRequestException`
    /// whose body parses as a 69b.E envelope, substitutes a
    /// `RemotingCategorisedException`. On any other exception (transport,
    /// timeout, 500-without-categorised-body) the original exception
    /// propagates unchanged.
    let private categorisedErrorBridge: Cmd.OfRemoting.IRemotingInterceptor =
        { new Cmd.OfRemoting.IRemotingInterceptor with
            member _.OnCalling _ = ()
            member _.OnSuccess(_, _) = ()

            member _.OnError(_, ex) =
                // Only `ProxyRequestException` carries the response body
                // we need to inspect. Transport / timeout / JSON-decode
                // exceptions don't have a categorised envelope and
                // propagate unchanged.
                match ex with
                | :? ProxyRequestException as pex ->
                    let body = pex.ResponseText

                    if isNull body then
                        None
                    else
                        match tryParseCategorised body with
                        | Some(category, errorBody) ->
                            Some(RemotingCategorisedException(category, errorBody, ex) :> exn)
                        | None -> None
                | _ -> None
        }

    /// 0.4.1 — observability interceptor. Stashes the correlation id
    /// (set by the per-request CsrfClient guard) into `CallInfo.Bag`
    /// under the key `"x-correlation-id"`, and emits per-method
    /// telemetry (start / success / error) through
    /// `Logger.forCategory "client.remoting.telemetry"`. The
    /// correlation id is reachable on the client because the response
    /// stamps `x-correlation-id` back per Phase 69b.D — but the request
    /// guard generates it client-side first, so a successful call
    /// records the id without needing to read the response header.
    ///
    /// 0.4.3 — the interceptor now reads the **same**
    /// `correlationGetter` thunk that `CsrfClient.installRequestGuard`
    /// uses, rather than minting its own GUID. Before 0.4.3 the
    /// interceptor logged one GUID and the wire carried a different
    /// one (the CsrfClient JS guard's call to `getCorrId()`), so
    /// client logs and server-stamped response logs never stitched on
    /// the same id. Unifying through the configured provider guarantees
    /// client log id == outbound wire id == server-stamped response id
    /// for the common "no proxy mangling" case.
    let private telemetryLog = Logger.forCategory "client.remoting.telemetry"

    let private fallbackCorrelationGetter: unit -> string =
        fun () -> System.Guid.NewGuid().ToString("N")

    let private correlationGetterRef: (unit -> string) ref =
        ref fallbackCorrelationGetter

    let private telemetry: Cmd.OfRemoting.IRemotingInterceptor =
        { new Cmd.OfRemoting.IRemotingInterceptor with
            member _.OnCalling info =
                // Stash the current correlation id at call-start using
                // the same provider the CsrfClient JS guard reads —
                // ensures the logged id equals the wire id. If the
                // provider throws, fall back to a fresh GUID so a
                // misbehaving provider can't blind observability.
                let corrId =
                    try
                        correlationGetterRef.Value()
                    with ex ->
                        telemetryLog.Warn $"correlationGetter raised: {ex.Message}; falling back to fresh GUID"
                        fallbackCorrelationGetter ()

                info.Bag.["x-correlation-id"] <- corrId
                telemetryLog.Debug $"start {info.MethodName} corr={corrId}"

            member _.OnSuccess(info, _) =
                let elapsed = (System.DateTime.UtcNow - info.StartedAt).TotalMilliseconds
                telemetryLog.Debug $"ok    {info.MethodName} ms={int elapsed}"

            member _.OnError(info, ex) =
                let elapsed = (System.DateTime.UtcNow - info.StartedAt).TotalMilliseconds
                // Fable doesn't support `ex.GetType().Name`; hand-discriminate
                // the only kinds the client surfaces at this layer.
                let kind =
                    match ex with
                    | :? ProxyRequestException -> "ProxyRequestException"
                    | :? RemotingCategorisedException -> "RemotingCategorisedException"
                    | _ -> "Exception"

                telemetryLog.Warn $"err   {info.MethodName} ms={int elapsed} kind={kind}"
                None
        }

    /// Install the SDK's standard interceptor chain. Called once from
    /// `SDK.Client.installRequestSeam` so every `Cmd.OfRemoting.call`
    /// site benefits without per-call wiring.
    ///
    /// `correlationGetter` is the same thunk passed to
    /// `CsrfClient.installRequestGuard`; the telemetry interceptor
    /// reads from it so the client log id, the outbound wire header,
    /// and the server-stamped response header all carry the same
    /// value (modulo proxy mangling — see SECURITY note in
    /// `docs/migrations/0.4.3-correlation-id-stitch.md` when added).
    ///
    /// Idempotent — `Interceptors.register` no-ops on a re-registration
    /// of the same instance reference. Re-calling `install` updates the
    /// stored correlationGetter without re-registering the interceptors.
    let install (correlationGetter: unit -> string) : unit =
        correlationGetterRef.Value <- correlationGetter
        Cmd.OfRemoting.Interceptors.register telemetry
        Cmd.OfRemoting.Interceptors.register categorisedErrorBridge
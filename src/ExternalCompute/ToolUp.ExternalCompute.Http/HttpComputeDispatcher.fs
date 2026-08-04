// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.ExternalCompute.Http

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 322 — the generic HTTP/REST IExternalComputeDispatcher ─────
//
// The zero-paid-dependency reference implementation of the Phase 318
// seam (GP 2): `Submit` POSTs a work spec to a configured URL, `Poll`
// GETs a status URL and maps the answer onto `ExternalOutcome`, `Cancel`
// issues the configured cancel request. Everything service-specific is
// `HttpComputeConfig`; this file is the protocol.
//
// **Authentication is resolved PER CALL, never snapshotted.** Every
// request re-reads the credential from `ISecretStore`, so rotating it
// mid-run is picked up on the next request with no restart and no cache
// invalidation. This is not a micro-optimisation deliberately forgone —
// it is the estate's named build-once / read-per-call seam-mismatch
// defect class, where a module-level client or an options record
// snapshots an async-resolved credential at construction and is then
// assumed to re-read it. A dispatcher lives for the process lifetime and
// a compute credential outlives no deployment, so caching here would
// mean a rotation silently breaks every submission until a restart.
//
// **Error classification is the retry contract, so it is explicit.**
//   * transport failure / timeout      → RETRIABLE. An unanswered
//     request says nothing about whether the work is viable.
//   * 5xx                              → RETRIABLE. The service's own
//     admission that this is its problem.
//   * 408 Request Timeout, 429 Too Many Requests → RETRIABLE. These are
//     the two 4xx codes that literally mean "ask again"; treating them
//     as terminal abandons perfectly good work the instant a service
//     rate-limits, which is exactly when a queue is deepest. (Phase 322
//     words the rule as "5xx/timeout retriable, 4xx not" — this is that
//     rule with its two well-known exceptions named rather than an
//     argument against it.)
//   * any other non-2xx                → TERMINAL. A 400 / 404 / 422 is
//     a statement about the request, and re-sending an identical request
//     cannot change the answer.
//
// **`Poll` never throws and never fabricates.** It returns
// `ExternalOutcome`, which has no error channel, so a transport failure
// has to be expressed as an outcome. It is reported as
// `Failed (retriable …)` — terminal in shape, because the poller must
// stop and hand the decision up, with retriability as data so the
// scheduler can re-submit. What it must NOT do is answer `Running` (a
// lie that keeps a dead handle alive forever) or `Cancelled` (a
// fabricated terminal state Phase 318 explicitly forbids).
//
// **What this dispatcher does NOT claim.**
//   * It does not honour `ExternalWorkSpec.Idempotency` itself. The key
//     is forwarded to the service when the config names a field for it,
//     but the handle id is platform-minted per `Submit`, so a service
//     that dedupes returns the same `NativeRef` under a NEW handle id.
//     Phase 318 words idempotent resubmit as a SHOULD for exactly this
//     reason, and Phase 485's memoization decorator is the portable
//     answer — a claim made here would be a claim about the service, not
//     about this code. Declared honestly as
//     `HonoursIdempotency = false` where the Phase 324 pack asks.
//   * It does not validate the presented handle's `ScopeId`. `Poll` and
//     `Cancel` address the service by the opaque `NativeRef` alone,
//     which is all the service gave us; a re-scoped handle is
//     indistinguishable from here. GP 4 is enforced a layer up, where it
//     is structural: the handle store is scope-partitioned and the
//     callback ingress takes the scope from the platform's stored record
//     and never from the request. Declared as
//     `ValidatesHandleScope = false`.
//   * It declares no `IIsolatedComputeBackend` posture, so an
//     `ExecutionProfile.Isolated` spec is refused by the Phase 478 gate
//     rather than handed to a service that has made no isolation
//     guarantee. A generic HTTP endpoint cannot honestly assert
//     no-egress.

/// Where a request failed, for the diagnostic. Kept out of the public
/// surface — it exists so one classifier can serve three call sites
/// without the messages losing which one they came from.
type private Stage =
    | SubmitStage
    | PollStage
    | CancelStage
    | CallbackStage

module private Stage =
    let label =
        function
        | SubmitStage -> "submit"
        | PollStage -> "poll"
        | CancelStage -> "cancel"
        | CallbackStage -> "callback-registration"

/// The generic HTTP/REST external-compute dispatcher.
///
/// Construct via `HttpComputeDispatcher.create`, which validates the
/// config and refuses a malformed one at compose time rather than on the
/// first submission. `secretStore` is required by the companion-authoring
/// convention (substrate arrives through `create`, never read from the
/// environment here) and is only consulted when `config.Auth` is set.
///
/// Distributed-ready: stateless between calls (GP 12 rule 4). Every
/// method receives its whole key — the scope + spec, or the handle — and
/// this type holds no per-unit state at all. Two replicas answer
/// identically, and a recycled one answers identically to a warm one.
type HttpComputeDispatcher
    (config: HttpComputeConfig, secretStore: ISecretStore, httpClient: HttpClient, logger: ILogger) =

    /// A response body, truncated for the diagnostic. A compute service
    /// erroring with a megabyte of HTML must not put a megabyte of HTML
    /// into an audit payload.
    let truncate (body: string) =
        if isNull body then ""
        elif body.Length <= 512 then body
        else body.Substring(0, 512) + "… (truncated)"

    /// `true` when this status code says "ask again" rather than "no".
    /// See the file header for why 408 / 429 join the 5xx family.
    let isRetriableStatus (code: int) =
        code >= 500
        || code = int HttpStatusCode.RequestTimeout
        || code = int HttpStatusCode.TooManyRequests

    let statusError (stage: Stage) (code: int) (body: string) =
        let message =
            sprintf "backend '%s' %s returned HTTP %d: %s" config.Backend (Stage.label stage) code (truncate body)

        if isRetriableStatus code then
            ExternalComputeError.retriable message
        else
            ExternalComputeError.terminal message

    let transportError (stage: Stage) (reason: string) =
        // Always retriable: the request was never answered, so nothing
        // was learned about whether the work itself is viable.
        ExternalComputeError.retriable (
            sprintf "backend '%s' %s could not reach the service: %s" config.Backend (Stage.label stage) reason
        )

    /// Resolve the auth header for THIS request. `Ok None` when the
    /// service is unauthenticated; `Error` (terminal) when a credential
    /// is configured but absent, because no amount of retrying puts a
    /// secret in the store.
    let resolveAuthHeader () = async {
        match config.Auth with
        | None -> return Ok None
        | Some auth ->
            let! secret = secretStore.GetSecret(auth.SecretScope, auth.SecretKey)

            match secret with
            | Some value when not (String.IsNullOrWhiteSpace value) ->
                return Ok(Some(auth.HeaderName, auth.ValueFormat.Replace(HttpComputeAuth.SecretPlaceholder, value)))
            | _ ->
                return
                    Error(
                        ExternalComputeError.terminal (
                            sprintf
                                "backend '%s' is configured to authenticate with ISecretStore secret '%s/%s', which is not present. Set the secret; no retry can compose one."
                                config.Backend
                                auth.SecretScope
                                auth.SecretKey
                        )
                    )
    }

    /// Send `request` under the per-request timeout, returning the status
    /// code + body, or a transport diagnostic.
    let send (stage: Stage) (request: HttpRequestMessage) = async {
        use cts = new CancellationTokenSource(config.RequestTimeout)

        try
            let! response = httpClient.SendAsync(request, cts.Token) |> Async.AwaitTask
            use response = response
            let! body = response.Content.ReadAsStringAsync cts.Token |> Async.AwaitTask
            return Ok(int response.StatusCode, body)
        with
        | :? OperationCanceledException ->
            return
                Error(
                    transportError
                        stage
                        (sprintf "the request exceeded the %O per-request budget" config.RequestTimeout)
                )
        | :? HttpRequestException as ex -> return Error(transportError stage ex.Message)
        | ex -> return Error(transportError stage (sprintf "%s: %s" (ex.GetType().Name) ex.Message))
    }

    /// Build one request with the resolved auth header applied.
    let buildRequest (method: HttpMethod) (url: string) (body: string option) (auth: (string * string) option) =
        let request = new HttpRequestMessage(method, url)

        match auth with
        | Some(headerName, headerValue) ->
            // TryAddWithoutValidation, because a vendor header
            // (`X-Api-Key`) is not a known request header and the
            // validating overload rejects it.
            request.Headers.TryAddWithoutValidation(headerName, headerValue) |> ignore
        | None -> ()

        match body with
        | Some json -> request.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        | None -> ()

        request

    /// The submit request body, per `config.Submit`. `Error` when the
    /// payload was declared raw JSON and is not parseable — a terminal
    /// caller error, named rather than shipped to the service as
    /// something it will reject less clearly.
    let submitBody (scopeId: string) (spec: ExternalWorkSpec) : Result<string, ExternalComputeError> =
        let fields = config.Submit
        let body = JsonObject()
        body[fields.KindField] <- JsonValue.Create spec.Kind

        let payloadResult =
            if fields.PayloadAsRawJson then
                try
                    // A caller's payload is already-serialised JSON
                    // (Phase 318), so the common case is to embed it as a
                    // value rather than double-encode it into a string.
                    body[fields.PayloadField] <- JsonNode.Parse spec.Payload
                    Ok()
                with ex ->
                    Error(
                        ExternalComputeError.terminal (
                            sprintf
                                "backend '%s' is configured with PayloadAsRawJson, but this spec's Payload is not valid JSON (%s). Serialise the payload, or set PayloadAsRawJson = false to send it as an opaque string."
                                config.Backend
                                ex.Message
                        )
                    )
            else
                body[fields.PayloadField] <- JsonValue.Create spec.Payload
                Ok()

        match payloadResult with
        | Error e -> Error e
        | Ok() ->
            match fields.ScopeField with
            | Some field -> body[field] <- JsonValue.Create scopeId
            | None -> ()

            match fields.ResourceHintsField with
            | Some field when not spec.ResourceHints.IsEmpty ->
                let hints = JsonObject()

                for KeyValue(key, value) in spec.ResourceHints do
                    hints[key] <- JsonValue.Create value

                body[field] <- hints
            | _ -> ()

            match fields.TimeoutSecondsField, spec.Timeout with
            | Some field, Some timeout -> body[field] <- JsonValue.Create(int timeout.TotalSeconds)
            | _ -> ()

            match fields.IdempotencyField, spec.Idempotency with
            | Some field, Some key -> body[field] <- JsonValue.Create key
            | _ -> ()

            // The callback URL — deployment-static, so it can ride the
            // submit request. The per-handle SECRET cannot: it does not
            // exist until the platform has registered the handle this
            // request is about to return. See `HttpComputeCallback`.
            match fields.CallbackUrlField, config.Callback with
            | Some field, Some callback ->
                body[field] <- JsonValue.Create(callback.PublicBaseUrl.TrimEnd '/' + ExternalCallback.Route)
            | _ -> ()

            Ok(body.ToJsonString())

    let parseJson (stage: Stage) (body: string) : Result<JsonDocument, ExternalComputeError> =
        try
            Ok(JsonDocument.Parse body)
        with ex ->
            Error(
                ExternalComputeError.terminal (
                    sprintf
                        "backend '%s' %s answered with a body that is not JSON (%s): %s"
                        config.Backend
                        (Stage.label stage)
                        ex.Message
                        (truncate body)
                )
            )

    /// Read the progress fraction, scaled and clamped. `None` for an
    /// absent, unreadable, or nonsensical value — a backend that cannot
    /// report progress says `None` rather than fabricating a figure
    /// (GP 12 rule 6), and so does this reader when the figure it found
    /// is not one.
    let readProgress (root: JsonElement) =
        config.Selectors.Progress
        |> Option.bind (fun path -> JsonPath.selectFloat path root)
        |> Option.map (fun raw -> raw / config.ProgressScale)
        |> Option.bind (fun fraction ->
            if Double.IsNaN fraction || Double.IsInfinity fraction || fraction < 0.0 then
                None
            else
                Some(min 1.0 fraction))

    /// Map one status document onto an outcome.
    let toOutcome (root: JsonElement) : ExternalOutcome =
        match JsonPath.selectString config.Selectors.Status root with
        | None ->
            ExternalOutcome.Failed(
                ExternalComputeError.terminal (
                    sprintf
                        "backend '%s' answered a status request with no readable value at selector '%s'. Either the service's response shape changed or the selector is wrong; both are configuration, not a transient fault."
                        config.Backend
                        config.Selectors.Status.Text
                )
            )
        | Some label ->
            match HttpComputeStatusMap.classify config.StatusValues label with
            | None ->
                // Deliberately NOT guessed. Every available guess is a
                // claim about whether the work finished.
                ExternalOutcome.Failed(
                    ExternalComputeError.terminal (
                        sprintf
                            "backend '%s' reported status '%s', which is not declared in StatusValues. Add it to the class it belongs to; a status this dispatcher cannot classify is never assumed to mean success, failure, or still-running."
                            config.Backend
                            label
                    )
                )
            | Some HttpStatusClass.Pending -> ExternalOutcome.Pending
            | Some HttpStatusClass.Running -> ExternalOutcome.Running(readProgress root)
            | Some HttpStatusClass.Cancelled -> ExternalOutcome.Cancelled
            | Some HttpStatusClass.Succeeded ->
                let resultRef =
                    config.Selectors.ResultRef
                    |> Option.bind (fun path -> JsonPath.selectString path root)
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)

                match resultRef with
                | Some reference -> ExternalOutcome.Succeeded reference
                | None ->
                    // A success with no result reference is not a
                    // success this platform can hand on: the caller's
                    // whole reason for polling is to learn where the
                    // result is.
                    ExternalOutcome.Failed(
                        ExternalComputeError.terminal (
                            sprintf
                                "backend '%s' reported status '%s' (success) but no result reference was readable%s. A success with no result ref cannot resolve a hand-off."
                                config.Backend
                                label
                                (match config.Selectors.ResultRef with
                                 | Some path -> sprintf " at selector '%s'" path.Text
                                 | None -> " (no ResultRef selector is configured)")
                        )
                    )
            | Some HttpStatusClass.Failed ->
                let message =
                    config.Selectors.Error
                    |> Option.bind (fun path -> JsonPath.selectString path root)
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.defaultValue (
                        sprintf "backend '%s' reported status '%s' with no diagnostic" config.Backend label
                    )

                let retriable =
                    config.Selectors.Retriable
                    |> Option.bind (fun path -> JsonPath.selectBool path root)
                    |> Option.defaultValue false

                ExternalOutcome.Failed {
                    Message = message
                    Retriable = retriable
                }

    /// The config this dispatcher was composed with. Read by the health
    /// probe and the startup validator so they cannot drift from it.
    member _.Config = config

    /// Submit under `scopeId`, returning the handle immediately.
    member _.SubmitWork(scopeId: string, spec: ExternalWorkSpec) = async {
        match submitBody scopeId spec with
        | Error e -> return Error e
        | Ok body ->
            match! resolveAuthHeader () with
            | Error e -> return Error e
            | Ok auth ->
                use request = buildRequest HttpMethod.Post config.SubmitUrl (Some body) auth

                match! send SubmitStage request with
                | Error e -> return Error e
                | Ok(code, responseBody) ->
                    if code < 200 || code > 299 then
                        return Error(statusError SubmitStage code responseBody)
                    else
                        match parseJson SubmitStage responseBody with
                        | Error e -> return Error e
                        | Ok document ->
                            use document = document

                            match JsonPath.selectString config.Selectors.JobId document.RootElement with
                            | Some jobId when not (String.IsNullOrWhiteSpace jobId) ->
                                return
                                    Ok {
                                        HandleId = Guid.NewGuid()
                                        Backend = config.Backend
                                        ScopeId = scopeId
                                        NativeRef = jobId
                                        SubmittedAt = DateTime.UtcNow
                                    }
                            | _ ->
                                // Accepted, but we cannot address it
                                // again. Terminal: the work may well be
                                // running, and saying so is the honest
                                // answer — re-submitting on a retry flag
                                // would start a SECOND unit while the
                                // first ran on unobserved.
                                return
                                    Error(
                                        ExternalComputeError.terminal (
                                            sprintf
                                                "backend '%s' accepted the submission (HTTP %d) but no job id was readable at selector '%s': %s. The work may be running and is now unaddressable; fix the selector before re-submitting."
                                                config.Backend
                                                code
                                                config.Selectors.JobId.Text
                                                (truncate responseBody)
                                        )
                                    )
    }

    /// Read the current outcome of `handle`.
    member _.PollHandle(handle: ExternalHandle) = async {
        let url = HttpComputeConfig.expandJobId handle.NativeRef config.StatusUrlTemplate

        match! resolveAuthHeader () with
        | Error e -> return ExternalOutcome.Failed e
        | Ok auth ->
            use request = buildRequest HttpMethod.Get url None auth

            match! send PollStage request with
            | Error e -> return ExternalOutcome.Failed e
            | Ok(code, body) ->
                if code = int HttpStatusCode.NotFound then
                    // Phase 318: a handle the backend no longer
                    // recognises is a TERMINAL failure, never an
                    // invented Cancelled.
                    return
                        ExternalOutcome.Failed(
                            ExternalComputeError.terminal (
                                sprintf
                                    "backend '%s' no longer holds unit '%s' (HTTP 404). It was accepted at %O; the service has forgotten or expired it."
                                    config.Backend
                                    handle.NativeRef
                                    handle.SubmittedAt
                            )
                        )
                elif code < 200 || code > 299 then
                    return ExternalOutcome.Failed(statusError PollStage code body)
                else
                    match parseJson PollStage body with
                    | Error e -> return ExternalOutcome.Failed e
                    | Ok document ->
                        use document = document
                        return toOutcome document.RootElement
    }

    /// Request teardown of `handle`. Best-effort and idempotent.
    member _.CancelHandle(handle: ExternalHandle) = async {
        match config.Cancel with
        | None ->
            // 322.D — a service without a cancel endpoint. Logged at
            // Info because it is a deployment fact rather than a fault,
            // and the caller confirms through Poll either way.
            logger.Info
                $"[external-compute-http] event=cancel_unsupported backend=%s{config.Backend} handle=%O{handle.HandleId} — no cancel endpoint is configured for this service, so the request is a no-op; the unit runs to its own completion"
        | Some cancel ->
            let url = HttpComputeConfig.expandJobId handle.NativeRef cancel.UrlTemplate

            match! resolveAuthHeader () with
            | Error e ->
                logger.Warn
                    $"[external-compute-http] event=cancel_unauthenticated backend=%s{config.Backend} handle=%O{handle.HandleId}: %s{e.Message}"
            | Ok auth ->
                use request = buildRequest (HttpMethod cancel.Method) url None auth

                match! send CancelStage request with
                | Error e ->
                    // Cancel returns unit by contract, so a failure is
                    // logged rather than raised: `Cancel` reports that
                    // the request was lodged, and the caller confirms
                    // the outcome through `Poll`.
                    logger.Warn
                        $"[external-compute-http] event=cancel_failed backend=%s{config.Backend} handle=%O{handle.HandleId}: %s{e.Message}"
                | Ok(code, body) ->
                    if code = int HttpStatusCode.NotFound then
                        // Already gone. Idempotent by contract —
                        // cancelling a unit the service has forgotten is
                        // not an error.
                        logger.Debug
                            $"[external-compute-http] event=cancel_unknown_unit backend=%s{config.Backend} handle=%O{handle.HandleId} — the service does not hold this unit (HTTP 404); treating the cancel as already satisfied"
                    elif code < 200 || code > 299 then
                        logger.Warn
                            $"[external-compute-http] event=cancel_rejected backend=%s{config.Backend} handle=%O{handle.HandleId} status=%d{code}: %s{truncate body}"
                    else
                        logger.Debug
                            $"[external-compute-http] event=cancel_lodged backend=%s{config.Backend} handle=%O{handle.HandleId} status=%d{code}"
    }

    /// Deliver the Phase 320 per-handle callback credential to the
    /// service, so its webhook can authenticate itself.
    member _.DeliverCallbackCredential(handle: ExternalHandle, credential: ExternalCallbackCredential) = async {
        match config.Callback with
        | None ->
            logger.Debug
                $"[external-compute-http] event=callback_not_configured backend=%s{config.Backend} handle=%O{handle.HandleId} — no callback registration is configured for this service; this run resolves by poll"
        | Some callback ->
            let url =
                HttpComputeConfig.expandJobId handle.NativeRef callback.RegistrationUrlTemplate

            let body = JsonObject()
            body[callback.UrlField] <- JsonValue.Create(callback.PublicBaseUrl.TrimEnd '/' + credential.CallbackPath)
            body[callback.SecretField] <- JsonValue.Create credential.Secret

            match callback.HandleIdField with
            | Some field -> body[field] <- JsonValue.Create(string credential.HandleId)
            | None -> ()

            match! resolveAuthHeader () with
            | Error e ->
                logger.Warn
                    $"[external-compute-http] event=callback_unauthenticated backend=%s{config.Backend} handle=%O{handle.HandleId}: %s{e.Message} — this run resolves by poll"
            | Ok auth ->
                use request =
                    buildRequest (HttpMethod callback.RegistrationMethod) url (Some(body.ToJsonString())) auth

                match! send CallbackStage request with
                | Error e ->
                    // Best-effort by contract (Phase 320): the work is
                    // accepted and the run is durably AwaitingExternal,
                    // so a failure here costs latency, never a job. No
                    // secret in any of these log lines.
                    logger.Warn
                        $"[external-compute-http] event=callback_registration_failed backend=%s{config.Backend} handle=%O{handle.HandleId}: %s{e.Message} — this run resolves by poll"
                | Ok(code, responseBody) ->
                    if code < 200 || code > 299 then
                        logger.Warn
                            $"[external-compute-http] event=callback_registration_rejected backend=%s{config.Backend} handle=%O{handle.HandleId} status=%d{code}: %s{truncate responseBody} — this run resolves by poll"
                    else
                        logger.Debug
                            $"[external-compute-http] event=callback_registered backend=%s{config.Backend} handle=%O{handle.HandleId} status=%d{code}"
    }

    /// Probe the configured health endpoint. `None` when no health URL is
    /// configured — read by the health check and the startup validator.
    member _.ProbeHealth() = async {
        match config.HealthUrl with
        | None -> return None
        | Some url ->
            match! resolveAuthHeader () with
            | Error e -> return Some(Error e.Message)
            | Ok auth ->
                use request = buildRequest HttpMethod.Get url None auth

                match! send PollStage request with
                | Error e -> return Some(Error e.Message)
                | Ok(code, body) ->
                    if code >= 200 && code <= 299 then
                        return Some(Ok())
                    else
                        return
                            Some(
                                Error(
                                    sprintf
                                        "backend '%s' health endpoint %s returned HTTP %d: %s"
                                        config.Backend
                                        url
                                        code
                                        (truncate body)
                                )
                            )
    }

    interface IExternalComputeDispatcher with
        member this.Backend = config.Backend
        member this.Submit(scopeId, spec) = this.SubmitWork(scopeId, spec)
        member this.Poll handle = this.PollHandle handle
        member this.Cancel handle = this.CancelHandle handle

    interface IExternalCallbackCapableBackend with
        member this.AcceptCallbackCredential(handle, credential) =
            this.DeliverCallbackCredential(handle, credential)

[<RequireQualifiedAccess>]
module HttpComputeDispatcher =

    /// Construct the dispatcher, refusing a malformed config **at
    /// compose time**.
    ///
    /// The raise is the point: every problem `HttpComputeConfig.problems`
    /// reports would otherwise present as a runtime symptom that looks
    /// like something else — a status template with no `{jobId}` makes
    /// every unit report the first one's status, an auth format with no
    /// `{secret}` presents as a service-side 401. Failing in front of the
    /// operator who wrote the config is strictly better than failing in
    /// front of a caller who cannot fix it.
    /// `create`, returning the concrete type — for a composition helper
    /// that also needs the health probe and the preflight validator,
    /// both of which read `Config` off the instance so they cannot drift
    /// from the dispatcher they describe.
    let createTyped
        (config: HttpComputeConfig)
        (secretStore: ISecretStore)
        (httpClient: HttpClient)
        (logger: ILogger)
        : HttpComputeDispatcher =
        match HttpComputeConfig.problems config with
        | [] -> HttpComputeDispatcher(config, secretStore, httpClient, logger)
        | problems ->
            raise (
                ArgumentException(
                    sprintf
                        "the HTTP external-compute config is not usable:%s"
                        (problems |> List.map (sprintf "\n  - %s") |> String.concat ""),
                    nameof config
                )
            )

    /// Construct the dispatcher as the seam the platform composes.
    let create
        (config: HttpComputeConfig)
        (secretStore: ISecretStore)
        (httpClient: HttpClient)
        (logger: ILogger)
        : IExternalComputeDispatcher =
        createTyped config secretStore httpClient logger :> IExternalComputeDispatcher
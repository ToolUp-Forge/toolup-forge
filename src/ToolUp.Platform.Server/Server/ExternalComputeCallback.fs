// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ExternalComputeCallback

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 320 — the completion-callback ingress ─────────────────────
//
//   POST /_platform/external-compute/callback
//   X-ToolUp-External-Callback-Secret: <per-handle secret>
//   { "handleId": "…", "status": "succeeded", "resultRef": "…" }
//
// A backend that finished pushes its outcome here, and the run resolves
// immediately instead of on the next scheduler tick. Polling (Phase 319)
// stays the universal fallback, and nothing here is load-bearing for
// correctness — a callback that never arrives, arrives twice, or arrives
// forged all end with the run resolved exactly once.
//
// **Everything the platform trusts comes from the platform.** The request
// supplies a handle id, a secret and an outcome. The scope, the job run,
// the backend and the retry policy all come from the platform's own
// stored record; there is no field a caller can set that redirects the
// resolution at another tenant's work (GP 4). That is what makes the
// pre-authentication lookup safe: it is keyed by an unguessable id and
// discloses nothing.
//
// **Order of operations, and why it is this order.**
//   1. throttle check — cheapest, and the brute-force friction
//   2. parse the body — a shape error is a misconfigured integration far
//      more often than an attack, but is still refused uniformly, because
//      a handler that explained its schema to an unauthenticated caller
//      would be an oracle
//   3. resolve the handle → the stored record
//   4. verify the secret against THAT record's hash, constant-time
//   5. claim + drive, behind `IExternalCompletionSink`
//
// Verification is step 4 and not step 1 because there is nothing to
// verify against until the record is loaded — the secret is per handle.
// So an unknown handle is refused without a crypto comparison, which
// costs nothing: the id is a v4 GUID, so "does this handle exist" is not
// a distinguisher an attacker can walk.
//
// **Every refusal is a uniform 403 plus a rate-limited warning, never a
// silent 401.** The Phase 232 encryption-admin posture: the response
// discloses neither which step failed nor whether the handle exists,
// while the log and the audit trail record exactly which. A forged
// callback is something an operator wants to alert on, and a bare status
// code in an access log is not an alert. The WARNING is rate-limited per
// source address so a scripted probe cannot turn the log into the
// denial-of-service; the AUDIT event is not, because the trail has to
// stay complete precisely when the log has gone quiet.
//
// **A duplicate resolves 200, not 409.** Idempotency is the phase's
// point, and a backend that retries on non-2xx would retry a correct
// duplicate forever. The body names which case it was, so a backend that
// cares can tell them apart.
//
// **The route is `/_platform/...` and NOT `/api/...`, deliberately — do
// not move it.** `SurfaceEnforcementMiddleware` and `CsrfMiddleware` are
// both scoped to `/api/*`, so this path sits outside the session-auth and
// double-submit-token envelope, which is what lets a GPU service POST to
// it carrying nothing but its per-handle secret. Relocating it under
// `/api/` would demand an XSRF token from a caller with no browser, no
// cookie jar and no way to obtain one, and every backend would begin
// failing with `csrf_validation_failed` — a break that no unit test in
// this pack would catch, because the middleware is not in the harness.
// The exemption is paid for here: every request is authenticated against
// the handle's own secret hash, refused uniformly, throttled per source
// address, and audited either way. Same posture as the anonymous
// `/_platform/signing-key/` route.

/// At most one forged-callback warning per source address per this many
/// minutes, however many refusals arrive.
[<Literal>]
let private warningWindowMinutes = 5.0

/// Refusals from one source address inside `throttleWindowMinutes`
/// before the ingress answers 429 without doing any work.
///
/// In-process (single-instance) friction layered over a 256-bit secret,
/// **not** the primary control — the secret is. Same shape and same
/// honest framing as the Phase 232 admin-token throttle. Two separate
/// knobs (this and `warningWindowMinutes`) because they bound different
/// things: one bounds guessing, the other bounds log volume.
[<Literal>]
let private maxRefusals = 20

[<Literal>]
let private throttleWindowMinutes = 5.0

[<Literal>]
let private platformScope = "_platform"

// Documented mutable-state exception to GP 5, and the same one
// `EncryptionAdminHandler` takes: a refusal counter IS state, and these
// two dictionaries are the whole of it. Concurrent by construction.
let private refusals = ConcurrentDictionary<string, int * DateTime>()
let private lastWarned = ConcurrentDictionary<string, DateTime>()

let private jsonOptions = FableConverters.create ()

/// What the ingress decided, computed before anything is written to the
/// response.
///
/// Separating decision from response is what keeps this handler readable
/// at five sequential gates: each gate produces a `Decision` and returns,
/// and there is exactly one place that turns a `Decision` into a status
/// code and an audit row — so a new gate cannot accidentally ship a
/// different response shape or forget the audit.
type private Decision =
    /// Too many refusals from this address; refused without work.
    | Throttled
    /// Refused. `reason` is internal-only — the response is uniform.
    | Refused of reason: string * handleId: Guid option
    /// External compute is composed but no usable handle store is, so
    /// callbacks cannot be authenticated or routed at all.
    | StoreMissing
    /// The handle authenticated, but no completion sink is registered.
    | SinkMissing of ExternalHandleRecord * ExternalOutcome
    /// The handle authenticated and the sink answered.
    | Applied of ExternalHandleRecord * ExternalOutcome * ExternalResolution

let private clientIp (ctx: HttpContext) : string =
    match ctx.Connection.RemoteIpAddress with
    | null -> "unknown"
    | ip -> string ip

let private resolveLogger (ctx: HttpContext) : ILogger option =
    match ctx.RequestServices.GetService(typeof<ILogger>) with
    | :? ILogger as l -> Some l
    | _ -> None

let private resolveAudit (ctx: HttpContext) : IAuditLog option =
    match ctx.RequestServices.GetService(typeof<IAuditLog>) with
    | :? IAuditLog as a -> Some a
    | _ -> None

let private isThrottled (ip: string) : bool =
    match refusals.TryGetValue ip with
    | true, (count, windowStart) when (DateTime.UtcNow - windowStart).TotalMinutes < throttleWindowMinutes ->
        count >= maxRefusals
    | _ -> false

let private recordRefusal (ip: string) : unit =
    refusals.AddOrUpdate(
        ip,
        (fun _ -> (1, DateTime.UtcNow)),
        (fun _ (count, windowStart) ->
            if (DateTime.UtcNow - windowStart).TotalMinutes < throttleWindowMinutes then
                (count + 1, windowStart)
            else
                (1, DateTime.UtcNow))
    )
    |> ignore

/// Should the forged-callback warning be logged for `ip` right now?
/// First refusal in a window: yes. Subsequent ones: suppressed.
let private shouldWarn (ip: string) : bool =
    let now = DateTime.UtcNow

    match lastWarned.TryGetValue ip with
    | true, at when (now - at).TotalMinutes < warningWindowMinutes -> false
    | _ ->
        lastWarned[ip] <- now
        true

/// Test seam: clear the in-process throttle + warning state so packs do
/// not inherit each other's counters. Never called from the request path.
let resetThrottleState () =
    refusals.Clear()
    lastWarned.Clear()

/// Live refusal count for `ip`. Exposed so a test can assert the
/// throttle actually counted rather than inferring it from a status code
/// that has several causes.
let refusalCount (ip: string) : int =
    match refusals.TryGetValue ip with
    | true, (count, _) -> count
    | _ -> 0

let private readSecretHeader (ctx: HttpContext) : string option =
    match ctx.Request.Headers.TryGetValue ExternalCallback.SecretHeader with
    | true, values when values.Count > 0 ->
        match values[0] with
        | null
        | "" -> None
        | v -> Some v
    | _ -> None

/// The five gates. Reads the request, resolves + authenticates the
/// handle, and drives the run — writing nothing to the response.
let private decide (ctx: HttpContext) (ip: string) : Async<Decision> = async {
    if isThrottled ip then
        return Throttled
    else
        let! body = (new StreamReader(ctx.Request.Body)).ReadToEndAsync() |> Async.AwaitTask

        let parsed =
            try
                match JsonSerializer.Deserialize<ExternalCallbackPayload>(body, jsonOptions) with
                | payload when payload.HandleId <> Guid.Empty -> Some payload
                | _ -> None
            with _ ->
                None

        match parsed with
        | None -> return Refused("malformed-body", None)
        | Some payload ->
            match readSecretHeader ctx with
            | None -> return Refused("missing-secret", Some payload.HandleId)
            | Some presented ->
                match ExternalCallback.toOutcome payload with
                | Error _ -> return Refused("non-terminal-status", Some payload.HandleId)
                | Ok outcome ->
                    match ctx.RequestServices.GetService(typeof<IExternalHandleStore>) with
                    | :? IExternalHandleStore as store ->
                        let! resolved = store.Resolve payload.HandleId

                        match resolved with
                        | None -> return Refused("unknown-handle", Some payload.HandleId)
                        | Some record ->
                            if not (ExternalCallbackSecret.verify record.CallbackSecretHash presented) then
                                return Refused("secret-mismatch", Some payload.HandleId)
                            else
                                match ctx.RequestServices.GetService(typeof<IExternalCompletionSink>) with
                                | :? IExternalCompletionSink as sink ->
                                    let! resolution = sink.ResolveExternal(record.Handle, record.JobRunId, outcome)

                                    return Applied(record, outcome, resolution)
                                | _ -> return SinkMissing(record, outcome)
                    | _ -> return StoreMissing
}

/// Emit the rejection audit + the rate-limited warning. Both, always —
/// the audit is the complete trail, the warning is the alertable signal.
let private auditRefusal (ctx: HttpContext) (ip: string) (reason: string) (handleId: Guid option) = async {
    recordRefusal ip

    if shouldWarn ip then
        resolveLogger ctx
        |> Option.iter (fun l ->
            l.Warn(
                sprintf
                    "[external-compute-callback] event=callback_refused reason=%s handle=%s from=%s — a completion callback was refused. If this repeats from an address that is not your compute backend, treat it as a forged-callback attempt. Further warnings from this address are suppressed for %g minutes; the audit trail (ExternalCallbackRejected) records every one."
                    reason
                    (handleId |> Option.map string |> Option.defaultValue "<none>")
                    ip
                    warningWindowMinutes
            ))

    match resolveAudit ctx with
    | Some audit ->
        do!
            audit.Record(
                platformScope,
                ExternalCallbackRejected {
                    HandleId = handleId |> Option.map string
                    Reason = reason
                    ClientIp = ip
                    OccurredAt = DateTimeOffset.UtcNow
                }
            )
    | None -> ()
}

/// Audit a resolution. Emitted on EVERY resolution the ingress reaches,
/// including the idempotent duplicate (GP 6) — see the payload doc for
/// why the no-op is audited too.
let private auditResolved
    (ctx: HttpContext)
    (record: ExternalHandleRecord)
    (outcome: ExternalOutcome)
    (resolution: string)
    (runStatus: string option)
    =
    async {
        match resolveAudit ctx with
        | Some audit ->
            do!
                audit.Record(
                    record.Handle.ScopeId,
                    ExternalCallbackResolved {
                        HandleId = string record.Handle.HandleId
                        Backend = record.Handle.Backend
                        ScopeId = record.Handle.ScopeId
                        JobRunId = string record.JobRunId
                        Outcome = ExternalOutcome.label outcome
                        Resolution = resolution
                        RunStatus = runStatus
                        OccurredAt = DateTimeOffset.UtcNow
                    }
                )
        | None -> ()
    }

let private callbackHandler: HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        let ip = clientIp ctx
        let! decision = decide ctx ip |> Async.StartImmediateAsTask

        match decision with
        | Throttled ->
            resolveLogger ctx
            |> Option.iter (fun l ->
                l.Warn(
                    sprintf
                        "[external-compute-callback] event=callback_throttled from=%s — more than %d refused callbacks in %g minutes; refusing without work"
                        ip
                        maxRefusals
                        throttleWindowMinutes
                ))

            ctx.Response.StatusCode <- 429
            return! ctx.WriteTextAsync "external-compute callback: too many refused attempts; try again later"

        | Refused(reason, handleId) ->
            do! auditRefusal ctx ip reason handleId |> Async.StartImmediateAsTask
            // Uniform refusal — discloses neither which gate failed nor
            // whether the handle exists.
            ctx.Response.StatusCode <- 403
            return! ctx.WriteTextAsync "external-compute callback refused"

        | StoreMissing ->
            // The route mounts only when external compute is composed, so
            // this is a deployment that opted in without a usable handle
            // store (typically a blob backend with no conditional
            // writes). Not the caller's fault and not a forgery signal —
            // answered as unavailable, and every run still resolves by
            // poll.
            resolveLogger ctx
            |> Option.iter (fun l ->
                l.Warn
                    "[external-compute-callback] event=callback_store_missing — no IExternalHandleStore is registered, so completion callbacks cannot be authenticated or routed and runs resolve by poll only. Compose a blob backend implementing IConditionalBlobStorage, or register an IExternalHandleStore explicitly.")

            ctx.Response.StatusCode <- 503
            return! ctx.WriteTextAsync "external-compute callback: handle store not configured"

        | SinkMissing(record, outcome) ->
            do!
                auditResolved ctx record outcome "sink-not-configured" None
                |> Async.StartImmediateAsTask

            ctx.Response.StatusCode <- 503
            return! ctx.WriteTextAsync "external-compute callback: no completion sink configured"

        | Applied(record, outcome, resolution) ->
            match resolution with
            | ExternalResolution.Resolved status ->
                do!
                    auditResolved ctx record outcome "resolved" (Some status)
                    |> Async.StartImmediateAsTask

                ctx.Response.StatusCode <- 200

                return!
                    ctx.WriteJsonAsync {|
                        HandleId = record.Handle.HandleId
                        Resolution = "resolved"
                        RunStatus = status
                    |}

            | ExternalResolution.AlreadyResolved ->
                do!
                    auditResolved ctx record outcome "already-resolved" None
                    |> Async.StartImmediateAsTask

                ctx.Response.StatusCode <- 200

                return!
                    ctx.WriteJsonAsync {|
                        HandleId = record.Handle.HandleId
                        Resolution = "already-resolved"
                    |}

            | ExternalResolution.NoAwaitingRun ->
                // Also 200, and for the same reason as the duplicate:
                // the run is not awaiting, so there is nothing the
                // backend could usefully retry.
                do!
                    auditResolved ctx record outcome "no-awaiting-run" None
                    |> Async.StartImmediateAsTask

                ctx.Response.StatusCode <- 200

                return!
                    ctx.WriteJsonAsync {|
                        HandleId = record.Handle.HandleId
                        Resolution = "no-awaiting-run"
                    |}

            | ExternalResolution.ScopeMismatch(handleScope, runScope) ->
                // GP 4 refusal. Audited as a resolution (it names a real
                // handle the caller authenticated for) AND refused as a
                // forgery signal, because two independent stores
                // disagreeing about a handle's scope is never routine.
                do!
                    auditResolved ctx record outcome "scope-mismatch" None
                    |> Async.StartImmediateAsTask

                resolveLogger ctx
                |> Option.iter (fun l ->
                    l.Error(
                        sprintf
                            "[external-compute-callback] event=callback_scope_mismatch handle=%O handleScope=%s runScope=%s from=%s — the handle store and the job store disagree about this handle's scope; refusing"
                            record.Handle.HandleId
                            handleScope
                            runScope
                            ip,
                        None
                    ))

                do!
                    auditRefusal ctx ip "scope-mismatch" (Some record.Handle.HandleId)
                    |> Async.StartImmediateAsTask

                ctx.Response.StatusCode <- 403
                return! ctx.WriteTextAsync "external-compute callback refused"

            | ExternalResolution.SinkNotConfigured ->
                do!
                    auditResolved ctx record outcome "sink-not-configured" None
                    |> Async.StartImmediateAsTask

                ctx.Response.StatusCode <- 503
                return! ctx.WriteTextAsync "external-compute callback: no completion sink configured"
    }

/// Routes table. Mounted by `buildRouteHandlers` **only** when the
/// deployment composed an external-compute backend
/// (`ServerConfig.ExternalCompute = CustomExternalCompute`), so a
/// deployment that composed none has no such path and the Giraffe
/// terminal middleware answers a clean 404 (GP 13). No hosted service, no
/// middleware, no allocation for anyone else.
let routes: HttpHandler list = [ POST >=> route ExternalCallback.Route >=> callbackHandler ]

/// The handler alone, for a test (or a consumer) that mounts the ingress
/// itself rather than through `compose`.
let handler: HttpHandler = callbackHandler
module ToolUp.Platform.PeerBearerAuthMiddleware

open System
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.Auth

// ─── Phase 37 — peer-bearer-auth middleware ──────────────────────────
//
// Substrate that lets one ToolUp instance accept authenticated HTTP
// calls from another using a shared per-peer bearer token. Sits ahead
// of `AuthEnforcementMiddleware` in the pipeline; for paths matching
// `ServerConfig.PeerRoutePrefixes` the middleware:
//
//   1. Reads `X-Peer-Name` to identify the calling instance.
//   2. Resolves `ISecretStore.GetSecret("_platform", $"peers/{peerName}/bearer")`.
//   3. Compares it constant-time against the caller's
//      `Authorization: Bearer <token>` header.
//   4. On match: stamps `HttpContext.Items["PeerName"]` so downstream
//      handlers can partition state per caller, and lets the request
//      continue. `AuthEnforcementMiddleware` sees the request is a
//      peer route and skips its user-auth check — the bearer IS the
//      authentication.
//   5. On mismatch (missing header, missing secret, wrong token,
//      missing `X-Peer-Name`): responds 401 before the handler runs.
//
// Non-peer routes pass straight through — the middleware is only
// registered when `PeerRoutePrefixes` is non-empty, so deployments
// without peer routes never see the cost of even the path check.
//
// **Constant-time comparison.** `CryptographicOperations.FixedTimeEquals`
// over UTF-8 byte arrays. A naive `=` comparison leaks the prefix length
// of the expected secret through timing (canonical webhook-verification
// mistake, called out in `TECHNICAL_GUIDE.md`). The pre-check on byte
// lengths is itself a known-length input (the attacker controls both
// sides of that check); `FixedTimeEquals` requires equal-length spans
// so the length check is structural, not security-load-bearing.
//
// **Audit emission.** `PeerCallAccepted` / `PeerCallRejected` events
// land in `IEventStore` under `SourceModule = "_platform.peer.bearer"`.
// `peerName` populates each payload — load-bearing for 1-N concurrency
// partitioning per the FederatedCHAID prototype plan (`peerName`
// becomes the audit-query filter operators use to attribute traffic to
// individual buyers). Emission is best-effort fire-and-forget: a
// failure to write the audit event never affects the request outcome.
//
// **Relationship to Phase 18.** Phase 18's `IPeerAuthProvider` / `Jwt-
// PeerAuthProvider` ship a richer surface (cryptographic signature
// verification, delegated assertions, capability handshake). Both
// flavours coexist on different prefixes — a deployment can register
// `withPeerRoutePrefix "/api/peer/echo"` for the bearer flavour AND
// register the Phase 18 substrate at `/api/peer/federated/` at the
// same time.
//
// ─── Six-rule portability audit (Phase 9c — Guiding Principle 12) ────
//
//   1. Identity by value      — `peerName : string` (request header
//                                 value). No live framework handle on
//                                 the wire; `HttpContext.Items
//                                 ["PeerName"]` is a plain string.
//   2. Async at every boundary — `ISecretStore.GetSecret` is `Async<_>`;
//                                 `IEventStore.Write` is `Async<_>`;
//                                 middleware bridges via `task { }`.
//                                 No sync escape on a dependency call.
//   3. Retry + supervision as data — N/A: auth is synchronous. No
//                                 retry semantics in scope; failed auth
//                                 means 401, end of request.
//   4. Stateless handlers     — the middleware holds no per-caller
//                                 state across requests. Every request
//                                 re-reads the secret via `ISecretStore`
//                                 so rotated tokens flow through
//                                 immediately (no in-process cache).
//   5. No cross-shard ordering promise — N/A: each request is
//                                 independently authenticated; audit
//                                 events use the underlying
//                                 `IEventStore`'s per-`ScopeId` FIFO
//                                 contract and inherit no stronger
//                                 promise.
//   6. Precision at the lower bound — N/A: no scheduling or timing
//                                 primitive on the surface.
//
// No framework-specific serialisation attributes on the payloads;
// `FableConverters` is the universal converter set used elsewhere in
// the SDK (matches `JobScheduler.fs` and `WebhookDispatcher.fs`).

[<Literal>]
let PeerBearerSourceModule = "_platform.peer.bearer"

[<Literal>]
let PeerNameHeader = "X-Peer-Name"

[<Literal>]
let SecretStoreScope = "_platform"

/// Build the `ISecretStore` key for a given peer's bearer token.
/// `peers/{peerName}/bearer` — `peerName` is the value the caller
/// supplies in `X-Peer-Name`.
let secretKeyFor (peerName: string) : string = $"peers/{peerName}/bearer"

// ─── Audit payloads ──────────────────────────────────────────────────

/// Payload for `PeerCallAccepted`. `PeerName` is the load-bearing field
/// — downstream audit queries filter on it to attribute traffic to a
/// specific buyer / cooperating instance.
type PeerCallAcceptedPayload = {
    PeerName: string
    Path: string
    Method: string
    OccurredAt: DateTime
}

/// Payload for `PeerCallRejected`. `PeerName` is `None` when the
/// `X-Peer-Name` header was absent (the caller is not identifiable).
/// `Reason` is a stable wire-format discriminator the contract pack
/// asserts against.
type PeerCallRejectedPayload = {
    PeerName: string option
    Path: string
    Method: string
    Reason: string
    OccurredAt: DateTime
}

/// Stable `Reason` values emitted on `PeerCallRejected`. The contract
/// pack pins them so downstream operator dashboards can group rejections.
module RejectionReason =
    [<Literal>]
    let MissingPeerNameHeader = "missing_peer_name_header"

    [<Literal>]
    let MissingAuthorizationHeader = "missing_authorization_header"

    [<Literal>]
    let MalformedAuthorizationHeader = "malformed_authorization_header"

    [<Literal>]
    let NoSecretConfigured = "no_secret_configured"

    [<Literal>]
    let TokenMismatch = "token_mismatch"

    /// Phase 137 — `X-Peer-Name` failed charset validation before it was
    /// interpolated into the ISecretStore key. Distinct from
    /// `MissingPeerNameHeader` so dashboards can tell a probe (traversal
    /// attempt) from a benign missing header.
    [<Literal>]
    let InvalidPeerName = "invalid_peer_name"

    /// Phase 137 — no `ISecretStore` is registered, so peer auth cannot
    /// be evaluated. Fail-closed as an audited 401 rather than an
    /// NRE→500 that would blind the peer audit trail (a composition
    /// defect, surfaced explicitly).
    [<Literal>]
    let NoSecretStore = "no_secret_store"

// ─── Authorization header parsing ────────────────────────────────────

/// Extract the bearer token from an `Authorization: Bearer <token>`
/// header value. Returns `None` for any malformed shape — case-
/// insensitive on the scheme, trims surrounding whitespace.
let tryParseBearer (authorizationHeader: string) : string option =
    if String.IsNullOrWhiteSpace authorizationHeader then
        None
    else
        let trimmed = authorizationHeader.Trim()
        let prefix = "Bearer "

        if
            trimmed.Length > prefix.Length
            && trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        then
            let token = trimmed.Substring(prefix.Length).Trim()
            if token.Length = 0 then None else Some token
        else
            None

// ─── Constant-time comparison ────────────────────────────────────────

/// UTF-8 byte equality via `CryptographicOperations.FixedTimeEquals`.
/// The length pre-check is structural — `FixedTimeEquals` throws on
/// unequal-length spans in older runtimes; on .NET 6+ it returns false
/// for unequal lengths but still works in constant time across all
/// equal-length inputs. We pre-check length so the function is portable
/// across runtimes.
let constantTimeEquals (expected: string) (actual: string) : bool =
    let expectedBytes = Encoding.UTF8.GetBytes expected
    let actualBytes = Encoding.UTF8.GetBytes actual

    expectedBytes.Length = actualBytes.Length
    && CryptographicOperations.FixedTimeEquals(ReadOnlySpan expectedBytes, ReadOnlySpan actualBytes)

// ─── Audit emission helpers ──────────────────────────────────────────

let private fableJsonOptions = FableConverters.create ()

let private serialize (value: 'T) : string =
    JsonSerializer.Serialize(value, fableJsonOptions)

/// Write a `_platform.peer.bearer` event. Best-effort: failures are
/// swallowed so a flaky event store never affects the request outcome
/// (same idiom as `JobScheduler.emitEvent` /
/// `WebhookDispatcher.emitAudit`).
let private emitEvent
    (eventStore: IEventStore option)
    (logger: ILogger option)
    (eventType: string)
    (payload: 'T)
    : Async<unit> =
    async {
        match eventStore with
        | None -> ()
        | Some store ->
            try
                let evt =
                    Events.create SecretStoreScope PeerBearerSourceModule eventType (serialize payload)

                do! store.Write evt
            with ex ->
                match logger with
                | Some l -> l.Warn($"[PeerBearerAuthMiddleware] event=write_failed eventType={eventType}: {ex.Message}")
                | None -> ()
    }

let emitAccepted
    (eventStore: IEventStore option)
    (logger: ILogger option)
    (payload: PeerCallAcceptedPayload)
    : Async<unit> =
    emitEvent eventStore logger "PeerCallAccepted" payload

let emitRejected
    (eventStore: IEventStore option)
    (logger: ILogger option)
    (payload: PeerCallRejectedPayload)
    : Async<unit> =
    emitEvent eventStore logger "PeerCallRejected" payload

// ─── Auth result ─────────────────────────────────────────────────────

/// Outcome of validating a single peer-route request. Returned by
/// `authenticate` so the contract pack can assert against the verdict
/// independently of the middleware plumbing.
type AuthOutcome =
    | Accepted of peerName: string
    | Rejected of peerName: string option * reason: string

/// Pure validation logic, factored out of `InvokeAsync` so the contract
/// pack can drive it without spinning up the full middleware. Reads the
/// `X-Peer-Name` and `Authorization` headers, resolves the expected
/// token via `ISecretStore`, and returns an `AuthOutcome`. Does NOT
/// emit audit events — emission is the middleware's responsibility so
/// the function stays a pure validator (no `IEventStore` side effect on
/// the contract-test path).
let authenticate (secretStore: ISecretStore) (ctx: HttpContext) : Async<AuthOutcome> = async {
    let peerNameHeader =
        match ctx.Request.Headers.TryGetValue(PeerNameHeader) with
        | true, values when values.Count > 0 ->
            let raw = string values[0]

            if String.IsNullOrWhiteSpace raw then
                None
            else
                Some(raw.Trim())
        | _ -> None

    match peerNameHeader with
    | None -> return Rejected(None, RejectionReason.MissingPeerNameHeader)
    | Some peerName when (IdentitySanitiser.sanitiseScopeId peerName) |> Result.isError ->
        // Phase 137 — `peerName` becomes a path segment of the secret
        // key (`peers/{peerName}/bearer`). Reject `/`, `\`, `..`, NUL,
        // control chars, leading-period, etc. before it is interpolated,
        // so it cannot traverse a path-mapping secret-store companion's
        // key space. Reuse the wave-wide IdentitySanitiser policy.
        return Rejected(Some peerName, RejectionReason.InvalidPeerName)
    | Some peerName ->
        let authorization =
            match ctx.Request.Headers.TryGetValue("Authorization") with
            | true, values when values.Count > 0 -> Some(string values[0])
            | _ -> None

        match authorization with
        | None -> return Rejected(Some peerName, RejectionReason.MissingAuthorizationHeader)
        | Some headerValue ->
            match tryParseBearer headerValue with
            | None -> return Rejected(Some peerName, RejectionReason.MalformedAuthorizationHeader)
            | Some presented ->
                let! expectedOpt = secretStore.GetSecret(SecretStoreScope, secretKeyFor peerName)

                match expectedOpt with
                | None -> return Rejected(Some peerName, RejectionReason.NoSecretConfigured)
                | Some expected ->
                    if constantTimeEquals expected presented then
                        return Accepted peerName
                    else
                        return Rejected(Some peerName, RejectionReason.TokenMismatch)
}

// ─── Middleware ──────────────────────────────────────────────────────

/// Phase 37 — Giraffe-compatible ASP.NET Core middleware that enforces
/// peer-bearer auth on paths registered in
/// `ServerConfig.PeerRoutePrefixes`. Non-peer paths pass through
/// untouched.
type PeerBearerAuthMiddleware(next: RequestDelegate, config: ServerConfig) =

    member _.InvokeAsync(ctx: HttpContext) =
        task {
            let isPeer = PeerRouteRegistry.isPeerRoute config.PeerRoutePrefixes ctx.Request.Path

            if not isPeer then
                do! next.Invoke(ctx)
            else
                let eventStore =
                    match ctx.RequestServices.GetService(typeof<IEventStore>) with
                    | :? IEventStore as s -> Some s
                    | _ -> None

                let logger =
                    match ctx.RequestServices.GetService(typeof<ILogger>) with
                    | :? ILogger as l -> Some l
                    | _ -> None

                let path = ctx.Request.Path.Value
                let methodName = ctx.Request.Method
                let now = DateTime.UtcNow

                let reject401 (peerName: string option) (reason: string) = task {
                    do!
                        emitRejected eventStore logger {
                            PeerName = peerName
                            Path = path
                            Method = methodName
                            Reason = reason
                            OccurredAt = now
                        }
                        |> Async.StartImmediateAsTask

                    ctx.Response.StatusCode <- 401
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync("""{"error":"Peer authentication required","status":401}""")
                }

                // Phase 137 — typed resolution of ISecretStore. When it is
                // unregistered (a composition defect), fail closed as an
                // audited 401 with reason `no_secret_store` rather than a
                // hard downcast that NREs into a 500 and never emits a
                // PeerCallRejected event — matching the graceful pattern
                // ShareTokenAuthMiddleware already uses.
                match ctx.RequestServices.GetService(typeof<ISecretStore>) with
                | :? ISecretStore as secretStore ->
                    let! outcome = authenticate secretStore ctx |> Async.StartImmediateAsTask

                    match outcome with
                    | Accepted peerName ->
                        ctx.Items[PeerRouteRegistry.PeerNameItemsKey] <- box peerName

                        do!
                            emitAccepted eventStore logger {
                                PeerName = peerName
                                Path = path
                                Method = methodName
                                OccurredAt = now
                            }
                            |> Async.StartImmediateAsTask

                        do! next.Invoke(ctx)
                    | Rejected(peerName, reason) -> do! reject401 peerName reason
                | _ ->
                    // No ISecretStore registered — fail closed + audited.
                    do! reject401 None RejectionReason.NoSecretStore
        }
        :> System.Threading.Tasks.Task
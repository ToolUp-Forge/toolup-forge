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
// **Security posture — what this substrate does NOT provide (Phase 317).**
// The two flavours are legitimately different tools, not two attempts at
// one, and the difference is worth stating plainly at the top of the
// weaker one. A static shared bearer has:
//
//   * **no expiry** — the credential is valid until an operator rotates
//     the `ISecretStore` entry. There is no `exp`, so a leaked token is
//     leaked until someone notices.
//   * **no audience** — the token names no receiver, so the same secret
//     presented to any deployment that trusts the same peer name is
//     accepted. The signed-JWT flavour binds `aud` to the receiver's own
//     peer id (Phase 130 / Phase 309).
//   * **no per-call minting** — one long-lived value is replayed
//     verbatim on every call, so there is no freshness window and
//     nothing for a replay guard to key on (contrast Phase 338's
//     `IPeerReplayGuard` + call scoping over per-call 5-minute tokens).
//   * **no delegated-assertion verification** — `X-Peer-Name` is a
//     self-asserted header admitted on the strength of the shared
//     secret; there is no originator chain to verify (contrast Phase
//     330, where the host verifies a `Delegated` originator before
//     dispatch).
//   * **no asymmetric option** — the secret is symmetric and shared, so
//     the receiver can mint anything the caller can. Phase 343's
//     `AsymmetricPeerAuthProvider` (ES256 / RS256) exists precisely for
//     counterparties who will not share one.
//   * **no transport posture of its own** — confidentiality on the wire
//     is the deployment's ingress problem (contrast Phase 339, which
//     refuses cleartext peer transport off loopback by default).
//
// None of that makes it wrong. A static bearer is the right tool for a
// small, operator-controlled set of internal callers on a route the
// deployment already fronts with TLS, and it needs no key ceremony. It
// is the wrong tool for a federation edge with an organisation you do
// not operate. The compose-time advisory at the foot of this file exists
// so the choice between them is visible rather than inferred from which
// config field someone happened to set.
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

// ─── Phase 317 — peer-auth posture advisory ──────────────────────────
//
// The two peer-auth substrates are documented as coexisting on
// different prefixes, and nothing checked that they did. A
// `PeerRoutePrefixes` entry is an ordinary `StartsWith` prefix, so
// `"/peer/"` — the most natural name an operator would reach for —
// covers the `/peer/v1/` namespace `JsonRpcPeerHost.routes` serves.
// When it does, this middleware is registered AHEAD of the Giraffe
// router, so the static-bearer gate runs first and the signed-JWT host
// is only reached by requests that already satisfied the weaker
// substrate. The failure is quiet in both directions:
//
//   * A `JsonRpcPeerClient` presents a signed peer JWT and NO
//     `X-Peer-Name` header, so every federation call is answered `401
//     missing_peer_name_header` before dispatch. The federation surface
//     looks composed and answers nothing.
//   * If the operator does seed static bearers and callers do send
//     `X-Peer-Name`, the federation edge has grown a second, weaker,
//     never-expiring credential that must be distributed out of band —
//     and its refusals are audited under `_platform.peer.bearer` rather
//     than the peer call trail.
//
// So the posture is classified at compose time and the live shadow is
// surfaced as one startup `Warn`. **Advisory only** — there is nothing
// to refuse here: a deployment may genuinely intend to front its own
// federation routes with a shared bearer, and refusing would break an
// existing composition on upgrade (GP 11). Nothing about either auth
// path changes; this adds no per-request work and, when no prefix is
// registered, no work at all (GP 13).
//
// The classification is a value rather than an inference — the same
// shape `PeerAudienceBinding` (Phase 309) and `TemplateApprovalPosture`
// (Phase 480) take — so a deployment asserts its own posture in its own
// preflight instead of scraping a log line, and the advisory cannot
// drift from what was classified.

/// The route namespace the signed-JWT peer host serves
/// (`POST /peer/v1/{contractId}`, `GET /peer/v1/capabilities`, …).
///
/// **Duplicated deliberately** from the peer companion's own route
/// table: that companion depends on this assembly, so the literal
/// cannot be imported from it without inverting the dependency. Same
/// structurally-forced duplication (and the same maintenance hazard) as
/// the `"/api/csrf-token"` literal in `SurfaceEnforcementMiddleware` —
/// if the peer host's namespace ever moves, both definitions move in
/// lockstep.
[<Literal>]
let SignedPeerRouteNamespace = "/peer/v1/"

/// A deployment's peer-auth posture, classified from `ServerConfig` at
/// compose time. Ordered weakest-guarantee-last: the two flagged rungs
/// are the ones where a static-bearer prefix has reached into the
/// namespace the signed-JWT substrate owns.
type PeerAuthPosture =
    /// Neither substrate composed — this deployment exposes no
    /// cross-deployment surface, so there is no posture to compare.
    | NoPeerAuthSurface
    /// Only the signed-JWT substrate (`PeerSubstrate =
    /// EnabledPeerSubstrate`, no static-bearer prefixes). The strongest
    /// rung: per-call minting, `exp` / `aud`, host-verified delegation,
    /// https-by-default transport, and an asymmetric-key option.
    | SignedPeerAuthOnly
    /// Only the static-bearer substrate, on prefixes of its own. A
    /// legitimate posture, and the weaker of the two — see the file
    /// header for exactly which guarantees are absent.
    | StaticBearerOnly of prefixes: string list
    /// Only the static-bearer substrate, but on a prefix that covers the
    /// namespace the signed-JWT host would serve. Nothing is shadowed
    /// today — the peer substrate is off — so this is latent, not a
    /// defect: enabling `PeerSubstrate` later would put the bearer gate
    /// in front of every federation call without a line changing here.
    | StaticBearerOnReservedNamespace of prefixes: string list
    /// Both substrates composed, on disjoint prefixes. The documented
    /// coexistence: the bearer flavour guards its own routes and the
    /// signed-JWT host serves `/peer/v1/*` untouched.
    | BothSubstratesDisjoint of bearerPrefixes: string list
    /// **The composition defect.** The signed-JWT host is serving and a
    /// static-bearer prefix covers its namespace, so the bearer gate
    /// decides who reaches federation. Carries only the shadowing
    /// prefixes, not every registered one.
    | StaticBearerShadowsSignedPeer of prefixes: string list

/// True when a `PeerRoutePrefixes` entry would claim any path under the
/// signed-JWT peer namespace. Both directions matter and each is a real
/// shape: a prefix SHORTER than the namespace (`"/"`, `"/peer/"`)
/// swallows all of it, and one LONGER (`"/peer/v1/ledger"`) claims part
/// of it. An empty prefix matches every path, so it swallows it too —
/// `String.StartsWith ""` is `true`, which is exactly how the runtime
/// registry behaves.
///
/// Case-insensitive, mirroring `PeerRouteRegistry.isPeerRoute`: the
/// classification must agree with the gate that will actually run.
let shadowsSignedPeerNamespace (prefix: string) : bool =
    match prefix with
    | null -> false
    | p ->
        SignedPeerRouteNamespace.StartsWith(p, StringComparison.OrdinalIgnoreCase)
        || p.StartsWith(SignedPeerRouteNamespace, StringComparison.OrdinalIgnoreCase)

/// Classify this deployment's peer-auth posture. Pure and total over
/// `ServerConfig`; exposed so a deployment (or a test) asserts on data
/// rather than on a log line, and so the advisory cannot disagree with
/// the classification it was derived from.
let auditPeerAuthPosture (config: ServerConfig) : PeerAuthPosture =
    let signedHostServing =
        match config.PeerSubstrate with
        | EnabledPeerSubstrate -> true
        | NoPeerSubstrate -> false

    match config.PeerRoutePrefixes with
    | [] ->
        if signedHostServing then
            SignedPeerAuthOnly
        else
            NoPeerAuthSurface
    | bearerPrefixes ->
        match bearerPrefixes |> List.filter shadowsSignedPeerNamespace with
        | [] ->
            if signedHostServing then
                BothSubstratesDisjoint bearerPrefixes
            else
                StaticBearerOnly bearerPrefixes
        | shadowing ->
            if signedHostServing then
                StaticBearerShadowsSignedPeer shadowing
            else
                StaticBearerOnReservedNamespace shadowing

/// The guarantee delta, shared by the advisory and the docs so the two
/// cannot drift. Deliberately enumerated rather than summarised as
/// "weaker": an operator reading a startup line needs to know which
/// properties they gave up, not that a value judgement was made.
let peerAuthSubstrateDelta =
    "The static-bearer substrate has no expiry, no audience, no per-call minting or replay window, no delegated-assertion verification, no asymmetric-key option and no transport posture of its own. The signed-JWT peer substrate has all six."

/// The advisory for a posture, or `None` when there is nothing to say.
///
/// Only the live shadow warns. `StaticBearerOnReservedNamespace` is
/// classified but silent: warning a deployment that has not composed the
/// peer substrate about a collision with a host it does not run is a
/// warning about a composition that does not exist, and an advisory that
/// fires on a correct configuration is one operators learn to ignore.
/// The rung is still data, so a deployment that wants to hold the line
/// early can assert it in its own preflight.
let peerAuthPostureAdvisory (posture: PeerAuthPosture) : string option =
    match posture with
    | NoPeerAuthSurface
    | SignedPeerAuthOnly
    | StaticBearerOnly _
    | StaticBearerOnReservedNamespace _
    | BothSubstratesDisjoint _ -> None
    | StaticBearerShadowsSignedPeer prefixes ->
        let named = String.concat ", " prefixes

        Some
            $"peer-auth-posture: ServerConfig.PeerRoutePrefixes entr(ies) [{named}] cover the '{SignedPeerRouteNamespace}' namespace this deployment's signed-JWT peer host serves, so PeerBearerAuthMiddleware gates every federation call FIRST and the weaker of the two peer-auth substrates decides who reaches the host. A typed peer client presents a signed peer JWT and no '{PeerNameHeader}' header, so those calls are answered 401 ({RejectionReason.MissingPeerNameHeader}) before dispatch and the federation surface answers nothing. {peerAuthSubstrateDelta} If the federation surface is what you meant to expose, move the static-bearer prefix off '{SignedPeerRouteNamespace}' (the two substrates are documented as coexisting on DIFFERENT prefixes — e.g. '/api/peer/echo'); if the static-bearer flavour is what you meant to guard these routes with, say so deliberately and treat this as an accepted posture."

/// Emit the advisory once at startup, best-effort. Called by
/// `configurePipeline` at the point the middleware is registered — so a
/// deployment with no peer prefixes never runs the classifier at all
/// (GP 13) — and exposed so a deployment can run the same check from its
/// own preflight.
let advisePeerAuthPosture (logger: ILogger) (config: ServerConfig) : unit =
    auditPeerAuthPosture config
    |> peerAuthPostureAdvisory
    |> Option.iter (fun advisory -> logger.Warn advisory)
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 528 — session registry + revocation ───────────────────────
//
// Until this phase a credential stayed valid until its natural expiry.
// There was no "active sessions" view, no sign-out-everywhere, and no
// admin force-revoke — which is the gap Phase 463 named (the revocation
// window) without closing it. A leaked bearer token, a lost laptop, or a
// compromised OIDC session could only be answered by rotating whatever
// signed the token, which logs out everybody.
//
// The registry records one `SessionRecord` per (subject, credential)
// pair, lists them for the owning user, and marks them revoked. A
// lightweight middleware refuses a revoked session's next request within
// a documented staleness bound.
//
// ─── How the session ID is derived, and why it is not a credential ───
//
// The session id is a one-way derivation of the credential the caller
// already presents (`SessionIdentity.derive`, server tier): the bearer
// token's `jti` where the token carries one, else a hash of the
// presented credential material; for an anonymous subject, the
// Phase 337 server-sealed session id. It is therefore
//
//   * stable — the same credential derives the same id on every request
//     and on every instance, with no shared state to synchronise;
//   * revocable at exactly the right granularity — revoking it kills
//     that one credential, not the user's other sessions;
//   * NOT itself a credential — it is a hash, so it grants nothing to a
//     holder, and no new client-visible secret is minted anywhere.
//
// That last property is what lets the record be listed to the user and
// echoed in a revoke call without widening the attack surface: naming a
// session id is not the same as holding the session.
//
// ─── Composition with Phase 337 ──────────────────────────────────────
//
// For an anonymous subject the derivation reads the id
// `ScopeResolutionMiddleware` resolved — which since Phase 337 is the
// id carried inside the DataProtection-sealed binding cookie, never the
// self-asserted `X-User-Id` echo. That composition is load-bearing
// rather than incidental: a registry keyed off a client-assertable id
// would let a caller name (and revoke) another session's record simply
// by claiming its id, which is the exact defect Phase 337 closed one
// layer down. Building on the verified id means the isolation the
// registry needs is already established before it runs (GP 4).

/// Lifecycle state of a recorded session. Terminal in one direction:
/// a revoked session is never re-activated — a caller who wants back in
/// authenticates afresh, which derives a new session id.
type SessionStatus =
    /// The session is live. `IsRevoked` returns `false`.
    | ActiveSession
    /// The session was revoked (by its owner, by sign-out-everywhere, or
    /// by a team admin). Every subsequent request bearing the same
    /// credential is refused by `SessionRevocationMiddleware` once its
    /// negative cache expires.
    | RevokedSession

/// One recorded session. Identity is entirely by value (GP 12 rule 1):
/// every field is a string, a `DateTimeOffset`, or a DU over those, so a
/// Redis- or SQL-backed `ISessionRegistry` stores and returns the same
/// record with no live handle anywhere in the shape.
type SessionRecord = {
    /// Derived session id — see the module header. Opaque to clients;
    /// stable across requests and instances for one credential.
    SessionId: string
    /// The owning identity: `AccessContext.UserId` at the time the
    /// session was first recorded. The isolation key for every read —
    /// `ListForUser` never returns another user's record.
    UserId: string
    /// The storage scope the session resolved to. Recorded so a team
    /// admin's revoke can be bounded to their own team's scope, and so a
    /// forensic reader can tell which tenant a session was acting in
    /// (GP 4).
    ScopeId: string
    /// Coarse device descriptor — a truncated, normalised `User-Agent`.
    /// Deliberately NOT the raw header and never an IP address: the
    /// record is listed back to the user, and a session list is a poor
    /// place to accumulate a location history. It exists to answer "is
    /// this the browser I am sitting at?", which a browser+OS pair
    /// answers and a fingerprint would over-answer.
    DeviceDescriptor: string
    /// Name of the `IAuthProvider` that authenticated the session, or
    /// `"anonymous"` for a Phase 337 sealed anonymous session. Lets the
    /// UI distinguish "signed in with SSO" from "signed in with the
    /// header provider" without the user decoding a token.
    AuthProvider: string
    /// When the session was first recorded — i.e. the first request that
    /// presented this credential while the registry was composed.
    CreatedAt: DateTimeOffset
    /// Last request seen on this session. Advanced by `Touch`, which is
    /// deliberately coarse (see `ISessionRegistry.Touch`).
    LastSeenAt: DateTimeOffset
    /// Lifecycle state.
    Status: SessionStatus
    /// When the session was revoked. `None` while `ActiveSession`.
    RevokedAt: DateTimeOffset option
    /// `AccessContext.UserId` of whoever revoked it — the owner for a
    /// self-revoke, the admin for a team force-revoke. `None` while
    /// `ActiveSession`.
    RevokedBy: string option
}

module SessionRecord =
    /// Is this record currently usable? The single place the
    /// active/revoked question is answered, so a store, a middleware and
    /// a UI cannot disagree about it.
    let isActive (record: SessionRecord) : bool =
        match record.Status with
        | ActiveSession -> true
        | RevokedSession -> false

    /// Mark a record revoked at `at`, attributed to `by`. Idempotent in
    /// effect — re-revoking an already-revoked record keeps the ORIGINAL
    /// revocation's timestamp and actor, because the first revocation is
    /// the one that mattered and overwriting it would erase the forensic
    /// answer to "who cut this off, and when".
    let revoke (at: DateTimeOffset) (by: string) (record: SessionRecord) : SessionRecord =
        match record.Status with
        | RevokedSession -> record
        | ActiveSession -> {
            record with
                Status = RevokedSession
                RevokedAt = Some at
                RevokedBy = Some by
          }

/// Why a session operation failed. Returned as data rather than thrown
/// (GP 12 rule 3) so a distributed implementation reports the same
/// shapes a blob-backed one does.
[<RequireQualifiedAccess>]
type SessionError =
    /// No record exists for the named session id in the reachable scope.
    /// Deliberately indistinguishable from "exists but belongs to
    /// someone else" at the API boundary — see `ISessionApi.RevokeSession`.
    | NotFound of sessionId: string
    /// The caller may not act on this session — it belongs to another
    /// user, or to a team the caller does not administer.
    | AccessDenied of reason: string
    /// The backing store could not be reached. Operator-visible; must
    /// not be echoed verbatim to a client.
    | StoreUnavailable of message: string

module SessionError =
    /// Client-safe rendering. `NotFound` and `AccessDenied` collapse to
    /// one message on purpose: telling a caller that a session id they
    /// do not own nevertheless EXISTS turns the list endpoint into an
    /// oracle over other users' session ids (GP 4).
    let toClientMessage (error: SessionError) : string =
        match error with
        | SessionError.NotFound _
        | SessionError.AccessDenied _ -> "That session is not available to you."
        | SessionError.StoreUnavailable _ ->
            "Sessions are temporarily unavailable. Your existing sessions are unaffected."

/// Client-facing session-security API.
///
/// Mounted only when the deployment opts in via
/// `ServerConfig.SessionRegistry`; the default `NoSessionRegistry`
/// mounts nothing and the routes 404 (GP 13).
///
/// **No method takes a user id from the wire except
/// `RevokeSessionForUser`, and that one is admin-gated and
/// team-bounded.** Every other method derives the owning identity from
/// the authenticated `AccessContext`, so a caller cannot address another
/// user's sessions by naming them — the same posture
/// `IModuleVisibilityApi` takes with scope (GP 4).
type ISessionApi = {
    /// The caller's own active and revoked sessions, most recently seen
    /// first. Revoked records are included (rather than filtered) so a
    /// user can see that a sign-out-everywhere actually took effect;
    /// `SessionRecord.Status` distinguishes them.
    [<RequiresClaim "scope">]
    ListMySessions: unit -> Async<Result<SessionRecord list, string>>

    /// Revoke one of the caller's own sessions — including, legitimately,
    /// the one making the call ("sign out this device"). Idempotent.
    ///
    /// A session id the caller does not own returns the same message as
    /// one that does not exist, per `SessionError.toClientMessage`.
    [<RequiresClaim "scope">]
    [<Audit "Custom:SessionRevoked">]
    RevokeSession: string -> Async<Result<unit, string>>

    /// Sign out everywhere: revoke every session belonging to the
    /// caller, the current one included. Returns how many records moved
    /// from active to revoked, so the UI can say something truthful
    /// rather than "done".
    [<RequiresClaim "scope">]
    [<Audit "Custom:AllSessionsRevoked">]
    RevokeAllMySessions: unit -> Async<Result<int, string>>

    /// Team admin force-revoke: revoke every session belonging to
    /// `userId` **within the caller's own team scope**. Requires
    /// `TeamRoles.canWriteTeamConfig` (Owner/Admin) in team mode and is
    /// refused outright in every other mode — a deployment with no team
    /// has no "administrator of someone else" to be.
    [<RequiresClaim "scope">]
    [<Audit "Custom:AllSessionsRevoked">]
    RevokeAllForUser: string -> Async<Result<int, string>>
}

module SessionApi =
    /// ToolUp.Remoting endpoint prefix. Matches `IModuleVisibilityApi` /
    /// `IFeatureFlagApi`.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"

    /// Longest `User-Agent` prefix kept in `SessionRecord.DeviceDescriptor`.
    /// Long enough to carry browser + platform, short enough that the
    /// record cannot become a de-facto fingerprint store.
    [<Literal>]
    let DeviceDescriptorMaxLength = 120

    /// Normalise a raw `User-Agent` into the stored descriptor: collapse
    /// whitespace, truncate, and fall back to a stable placeholder when
    /// the header is absent. Lives here (Core) rather than server-side so
    /// the client renders exactly the string the server stored — a
    /// truncation applied on one side only is how a list ends up showing
    /// two entries for one device.
    let deviceDescriptorOf (userAgent: string option) : string =
        match userAgent with
        | None -> "Unknown device"
        | Some raw ->
            let collapsed =
                String.Join(" ", raw.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries))

            if String.IsNullOrWhiteSpace collapsed then
                "Unknown device"
            elif collapsed.Length <= DeviceDescriptorMaxLength then
                collapsed
            else
                collapsed.Substring(0, DeviceDescriptorMaxLength)
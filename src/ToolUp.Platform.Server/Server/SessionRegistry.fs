// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 528 — the blob-backed default `ISessionRegistry`, plus the
/// session-id derivation every caller shares.
///
/// One JSON blob per session at
/// `_platform/sessions/{scopeId}/{sessionId}.json`, over the composed
/// `IBlobStorage`. BCL-only JSON through `FableConverters` (GP 1) — no
/// vendor dependency, and the same converter set the wire uses, so a
/// `SessionRecord` read back off a blob is byte-equivalent to one that
/// crossed the wire.
///
/// Distributed-readiness: **distributed-ready** for the store itself —
/// every method reads and writes blobs and carries no state between
/// calls (GP 12 rule 4), so any instance answers identically. The
/// caller-side cache in `SessionRevocationMiddleware` is what is
/// per-instance; see the multi-instance caveat on `ISessionRegistry`.
module ToolUp.Platform.SessionRegistry

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

/// The reserved platform container every `_platform/...` artefact lives
/// in. Matches `AuditReplicator` / `ConfigValidator`.
[<Literal>]
let Container = "_platform"

[<Literal>]
let private Prefix = "sessions/"

let private jsonOptions = FableConverters.create ()

// ─── Session-id derivation ───────────────────────────────────────────

/// Derivation of the session id from the credential a request already
/// carries. **No new client-visible credential is minted anywhere in
/// this module** — the id is a one-way hash, so listing it back to a
/// user or accepting it in a revoke call grants nothing.
///
/// Why a derivation rather than a stored, server-generated id: a stored
/// id needs a lookup keyed by *something* on the request, and the only
/// candidates are the credential itself (so: derive) or a second cookie
/// (so: mint a new credential, which the phase explicitly forbids —
/// every additional bearer-shaped value is another thing to steal). A
/// derivation is also stateless across instances and survives a store
/// wipe: the same credential derives the same id on every node, forever,
/// with nothing to synchronise.
[<RequireQualifiedAccess>]
module SessionIdentity =

    /// Domain separation for the hash. Any change here invalidates every
    /// stored session id — an intentional, breaking act.
    [<Literal>]
    let private Purpose = "ToolUp.SessionRegistry.v1"

    let private sha256Hex (material: string) : string =
        use h = SHA256.Create()

        h.ComputeHash(Encoding.UTF8.GetBytes material)
        |> Array.map _.ToString("x2")
        |> String.concat ""

    /// Recover a JWT's `jti` claim without validating the token.
    ///
    /// **Validation is deliberately NOT this function's job** and its
    /// absence is not a gap: by the time a session id is derived,
    /// `IAuthProvider.GetUser` has already validated the credential and
    /// produced the `AuthenticatedUser` whose id is mixed into the hash.
    /// Re-validating here would duplicate the provider's job with a
    /// second, weaker implementation — the classic shape where the two
    /// disagree. What this read does is choose a STABLE input: a `jti` is
    /// the token's own identity, so a re-issued token with the same
    /// subject derives a *different* session (correct — it is a different
    /// sign-in), while a repeated request with the same token derives the
    /// same one.
    ///
    /// A token with no `jti`, or one that is not a JWT at all, falls back
    /// to hashing the raw credential — same stability, no `jti` needed.
    let private jtiOf (token: string) : string option =
        match token.Split '.' with
        | [| _; payload; _ |] ->
            match Base64Url.tryDecode payload with
            | None -> None
            | Some bytes ->
                try
                    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)

                    match doc.RootElement.TryGetProperty "jti" with
                    | true, el when el.ValueKind = JsonValueKind.String ->
                        match el.GetString() with
                        | null -> None
                        | "" -> None
                        | jti -> Some jti
                    | _ -> None
                with _ ->
                    None
        | _ -> None

    /// Derive the session id for an authenticated caller.
    ///
    /// `credential` is the raw presented bearer material (the
    /// `Authorization` header value with its scheme stripped). It is
    /// hashed and immediately discarded — nothing in the returned value,
    /// the stored record, or the audit row can be walked back to it.
    let ofCredential (userId: string) (credential: string) : string =
        let stable =
            match jtiOf credential with
            | Some jti -> "jti:" + jti
            | None -> "raw:" + sha256Hex credential

        sha256Hex (Purpose + "|" + userId + "|" + stable)

    /// Derive the session id for a Phase 337 sealed anonymous session.
    ///
    /// `sessionId` here is the id recovered from the DataProtection-
    /// sealed binding cookie — i.e. server-issued and verified before it
    /// reaches this function. Hashing it anyway is not ceremony: the
    /// sealed id IS the anonymous storage-scope id and rides `X-User-Id`
    /// in plaintext, so using it verbatim as the registry key would make
    /// a session record addressable by a value any observer of the
    /// request has seen. The hash is the same one-way step the
    /// authenticated path takes, which also keeps both kinds of session
    /// id indistinguishable in shape.
    let ofAnonymousSession (sessionId: string) : string =
        sha256Hex (Purpose + "|anonymous|" + sessionId)

    /// Derive the session id for a resolved `Subject` and, where the
    /// subject is authenticated, the credential it presented. `None`
    /// when nothing stable is available to derive from — an
    /// authenticated request that carried no bearer credential (a
    /// header-auth deployment, say) has no per-session identity to
    /// record, and inventing one per request would fill the store with
    /// single-use rows nobody can act on.
    ///
    /// `ClaimBearer` is deliberately `None`: a share-token claim already
    /// has its own revocation path (`IShareTokenStore.Revoke`), and
    /// recording it here would create a second, divergent place to
    /// revoke the same thing.
    let ofSubject (subject: Subject) (credential: string option) : string option =
        match subject, credential with
        | AnonymousSession sid, _ when sid <> "" -> Some(ofAnonymousSession sid)
        | AnonymousSession _, _ -> None
        | Subject.AuthenticatedUser userId, Some cred when cred <> "" -> Some(ofCredential userId cred)
        | TeamMember(userId, _), Some cred when cred <> "" -> Some(ofCredential userId cred)
        | Subject.AuthenticatedUser _, _
        | TeamMember _, _ -> None
        | Subject.ClaimBearer _, _ -> None

// ─── Blob addressing ─────────────────────────────────────────────────

/// Validate a scope id destined to become a blob-key segment, reusing
/// the wave-wide `IdentitySanitiser` policy the team / permission /
/// share-token seams apply. A traversal scope id would otherwise let a
/// read enumerate — or a write land at — a chosen `_platform/...` path.
let private sanitiseScope (scopeId: string) : Result<string, SessionError> =
    match Auth.IdentitySanitiser.sanitiseScopeId scopeId with
    | Ok value -> Ok value
    | Error reason -> Error(SessionError.AccessDenied(sprintf "scopeId failed identity validation: %s" reason))

/// Session ids are derived SHA-256 hex (see `SessionIdentity`), so they
/// are structurally path-safe. This check exists anyway because the id
/// arrives from the WIRE on the revoke path, where a caller can send
/// whatever they like — and a 64-char-hex assertion is cheaper and more
/// exact than re-running the general sanitiser.
let private isDerivedId (sessionId: string) : bool =
    sessionId.Length = 64
    && sessionId
       |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

let private scopePrefix (scopeId: string) = Prefix + scopeId + "/"

let private blobName (scopeId: string) (sessionId: string) =
    scopePrefix scopeId + sessionId + ".json"

// ─── Blob-backed implementation ──────────────────────────────────────

/// The blob-backed default. `retentionDays` bounds how long a record is
/// returned by `ListForUser` after `LastSeenAt` — an expired record is
/// filtered on read rather than swept, so there is no background service
/// to compose and no cost when the registry is unused (GP 13).
///
/// `now` is injected so the retention and revocation timestamps are
/// testable without a wall clock (the estate's frozen-now convention).
type BlobBackedSessionRegistry
    (blobs: IBlobStorage, logger: ILogger option, retentionDays: int, now: unit -> DateTimeOffset) =

    let warn (msg: string) =
        match logger with
        | Some l -> l.Warn msg
        | None -> ()

    let serialise (record: SessionRecord) =
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, jsonOptions))

    let deserialise (bytes: byte[]) : SessionRecord option =
        try
            let value =
                JsonSerializer.Deserialize<SessionRecord>(Encoding.UTF8.GetString bytes, jsonOptions)

            if obj.ReferenceEquals(value, null) then
                None
            else
                Some value
        with ex ->
            // A record that will not deserialise silently stops being
            // listed AND stops being revocable, which is the worse half.
            // Surface it rather than letting a session quietly become
            // unmanageable.
            warn (sprintf "SessionRegistry: a session record could not be deserialised; skipping it. %s" ex.Message)
            None

    let read (scopeId: string) (sessionId: string) : Async<SessionRecord option> = async {
        match! blobs.Download(Container, blobName scopeId sessionId) with
        | Ok bytes -> return deserialise bytes
        | Error _ -> return None
    }

    let write (record: SessionRecord) (scopeId: string) : Async<Result<unit, SessionError>> = async {
        match! blobs.Upload(Container, blobName scopeId record.SessionId, serialise record) with
        | Ok _ -> return Ok()
        | Error e -> return Error(SessionError.StoreUnavailable e)
    }

    /// Every record under one scope's prefix. Bounded-parallel reads —
    /// `ListForUser` runs on an interactive page load, and a session set
    /// is small, but a remote blob store makes N serial round-trips
    /// visible. 16 matches the estate's fan-out convention.
    let readScope (scopeId: string) : Async<SessionRecord list> = async {
        let! names = blobs.List(Container, scopePrefix scopeId)

        let! results =
            names
            |> List.map (fun name -> async {
                match! blobs.Download(Container, name) with
                | Ok bytes -> return deserialise bytes
                | Error _ -> return None
            })
            |> fun xs -> Async.Parallel(xs, 16)

        return results |> Array.toList |> List.choose id
    }

    let isExpired (at: DateTimeOffset) (record: SessionRecord) =
        retentionDays > 0 && record.LastSeenAt.AddDays(float retentionDays) < at

    interface ISessionRegistry with

        member _.Record(record) = async {
            match sanitiseScope record.ScopeId with
            | Error e -> return Error e
            | Ok scopeId ->
                if not (isDerivedId record.SessionId) then
                    return Error(SessionError.AccessDenied "sessionId is not a derived session identifier")
                else
                    match! read scopeId record.SessionId with
                    // Already recorded. Return the STORED record, not the
                    // incoming one: `CreatedAt` and any revocation on the
                    // stored copy are the facts, and letting a returning
                    // credential overwrite them would both reset "signed
                    // in since" and un-revoke a revoked session.
                    | Some existing -> return Ok existing
                    | None ->
                        match! write record scopeId with
                        | Ok() -> return Ok record
                        | Error e -> return Error e
        }

        member _.Touch(scopeId, sessionId, seenAt) = async {
            match sanitiseScope scopeId with
            // A touch is a liveness signal, not an assertion (see the
            // interface doc): every miss is `Ok ()` rather than an error,
            // because there is nothing here worth failing the request
            // that carried it over.
            | Error _ -> return Ok()
            | Ok scope ->
                if not (isDerivedId sessionId) then
                    return Ok()
                else
                    match! read scope sessionId with
                    | None -> return Ok()
                    | Some record when not (SessionRecord.isActive record) -> return Ok()
                    | Some record ->
                        // Minute-grain precision (GP 12 rule 6): a touch
                        // whose delta is under a minute is skipped, so a
                        // burst of requests on one session costs one blob
                        // write rather than one per request. Callers are
                        // told not to read `LastSeenAt` as a
                        // request-accurate clock precisely so this is
                        // conformant rather than a corner cut.
                        if seenAt - record.LastSeenAt < TimeSpan.FromMinutes 1.0 then
                            return Ok()
                        else
                            let! written = write { record with LastSeenAt = seenAt } scope

                            match written with
                            | Ok() -> return Ok()
                            | Error e ->
                                // A failed touch must not fail the request
                                // either — it costs a stale `LastSeenAt`,
                                // nothing more.
                                warn (sprintf "SessionRegistry: touch write failed; LastSeenAt is stale. %A" e)
                                return Ok()
        }

        member _.ListForUser(scopeId, userId) = async {
            match sanitiseScope scopeId with
            | Error e -> return Error e
            | Ok scope ->
                let at = now ()

                let! records = readScope scope

                return
                    records
                    |> List.filter (fun r -> r.UserId = userId && not (isExpired at r))
                    |> Ok
        }

        member _.Revoke(scopeId, sessionId, actorUserId) = async {
            match sanitiseScope scopeId with
            | Error e -> return Error e
            | Ok scope ->
                if not (isDerivedId sessionId) then
                    return Error(SessionError.NotFound sessionId)
                else
                    match! read scope sessionId with
                    | None -> return Error(SessionError.NotFound sessionId)
                    | Some record ->
                        // Idempotent: `SessionRecord.revoke` keeps the
                        // original revocation's timestamp and actor, so a
                        // repeat call is a no-op write rather than a
                        // rewriting of history.
                        let revoked = SessionRecord.revoke (now ()) actorUserId record
                        return! write revoked scope
        }

        member this.RevokeAllForUser(scopeId, userId, actorUserId) = async {
            match sanitiseScope scopeId with
            | Error e -> return Error e
            | Ok scope ->
                let! records = readScope scope

                let active =
                    records |> List.filter (fun r -> r.UserId = userId && SessionRecord.isActive r)

                let at = now ()

                let! results =
                    active
                    |> List.map (fun r -> write (SessionRecord.revoke at actorUserId r) scope)
                    |> fun xs -> Async.Parallel(xs, 16)

                // Count what actually landed, not what was attempted: the
                // caller reports this number to a user who is about to
                // decide whether they are safe, so an optimistic count is
                // worse than a small one.
                let revoked = results |> Array.filter Result.isOk |> Array.length

                match
                    results
                    |> Array.tryPick (function
                        | Error e -> Some e
                        | Ok() -> None)
                with
                | Some e when revoked = 0 -> return Error e
                | Some e ->
                    warn (sprintf "SessionRegistry: partial sign-out-everywhere for a user; some writes failed: %A" e)
                    return Ok revoked
                | None -> return Ok revoked
        }

        member _.IsRevoked(scopeId, sessionId) = async {
            // Fails open by construction — every non-affirmative path
            // returns `false`. See the interface doc for why: this is a
            // revocation list consulted AFTER the credential has been
            // validated, so failing closed would convert a store outage
            // into a fleet-wide sign-out.
            match sanitiseScope scopeId with
            | Error _ -> return false
            | Ok scope ->
                if not (isDerivedId sessionId) then
                    return false
                else
                    match! read scope sessionId with
                    | Some record -> return not (SessionRecord.isActive record)
                    | None -> return false
        }

module BlobBackedSessionRegistry =
    /// Build the blob-backed registry with the system clock. The
    /// four-argument constructor stays available for tests that need a
    /// frozen clock.
    let create (blobs: IBlobStorage) (logger: ILogger option) (retentionDays: int) : ISessionRegistry =
        BlobBackedSessionRegistry(blobs, logger, retentionDays, fun () -> DateTimeOffset.UtcNow) :> ISessionRegistry
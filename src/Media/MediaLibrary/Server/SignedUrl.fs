// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.SignedUrl

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 88 — scope-signed expiring media URLs ──────────────────────
//
// Reuses the HMAC-SHA256 signing pattern from `ShareTokenStore` (GP 4):
// a gated media item is reachable only via a freshly-minted URL whose
// signature binds `(MediaId, ScopeId, Container, ExpiresAt)`. The range
// handler verifies the signature and the expiry before serving a byte,
// so the underlying blob is never world-readable and a leaked URL stops
// working at its TTL.
//
// The signing key is a 32-byte secret in `ISecretStore` under the
// reserved `_platform` scope, auto-generated on first use — same
// lifecycle as `share_token_signing_key`. The `mint` / `verify`
// functions are pure (key + clock in, token / result out) so the
// crypto + expiry logic is unit-testable without an `ISecretStore`.

[<Literal>]
let private platformContainer = "_platform"

[<Literal>]
let private signingKeySecretName = "media_library_signing_key"

// ─── base64url + HMAC helpers (copied from the ShareTokenStore pattern;
// they are small standard primitives, inlined so the companion stays a
// self-contained package with no cross-module private dependency) ──────

let private base64UrlEncode (bytes: byte[]) : string =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private base64UrlDecode (s: string) : byte[] option =
    try
        let pad = (4 - s.Length % 4) % 4
        let padded = s.Replace('-', '+').Replace('_', '/') + String.replicate pad "="
        Some(Convert.FromBase64String padded)
    with _ ->
        None

let private hmacSha256 (key: byte[]) (data: byte[]) : byte[] =
    use h = new HMACSHA256(key)
    h.ComputeHash(data)

let private constantTimeEquals (a: byte[]) (b: byte[]) : bool =
    if a.Length <> b.Length then
        false
    else
        let mutable diff = 0

        for i in 0 .. a.Length - 1 do
            diff <- diff ||| int (a[i] ^^^ b[i])

        diff = 0

/// The signed payload carried inside a media URL token. Public so the
/// STJ `FableConverters` record converter can read the synthesised
/// constructor and round-trip the fields (a `private` record silently
/// serialises to `{}` and breaks verification — the same gotcha
/// `ShareTokenStore.SignedPayload` documents).
type MediaSignedPayload = {
    MediaId: string
    ScopeId: string
    Container: string
    ExpiresAtUnix: int64
}

module private Json =
    let private options = FableConverters.create ()

    let serializeString (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<'T>(json, options))
        with _ ->
            None

// ─── Pure mint / verify ───────────────────────────────────────────────
//
// Wire format: `{mediaId}.{base64url(payloadJson)}.{base64url(hmac)}`.
// The `mediaId` prefix is cross-checked against the signed payload so a
// swapped prefix fails verification; the persisted record + the route
// segment stay the source of truth.

/// Mint a signed token for a media item bound to a scope and expiry.
/// Pure — the caller supplies the resolved signing key and the absolute
/// expiry instant.
let mint (signingKey: byte[]) (id: MediaId) (scope: StorageScope) (expiresAt: DateTimeOffset) : string =
    let payload = {
        MediaId = MediaId.value id
        ScopeId = scope.ScopeId
        Container = scope.Container
        ExpiresAtUnix = expiresAt.ToUnixTimeSeconds()
    }

    let payloadBytes = Json.serializeString payload |> Encoding.UTF8.GetBytes
    let signature = hmacSha256 signingKey payloadBytes
    sprintf "%s.%s.%s" payload.MediaId (base64UrlEncode payloadBytes) (base64UrlEncode signature)

/// Verify a token's SIGNATURE and structure, WITHOUT consulting the
/// expiry. Pure — returns the payload the signature covers, or
/// `Malformed` / `InvalidSignature`. Never returns `Expired`, because it
/// never looks.
///
/// **This is not an access decision and must never back one.** Every
/// serving path calls `verify` below, which layers the expiry check on
/// top; this function exists for Phase 742's delivered-egress
/// reconciliation, where the question is "which scope minted the URL
/// whose bytes an edge has already served" — asked from a CDN access log
/// that arrives HOURS after the fact (an edge of the class above
/// typically delivers within the hour and can lag a day). By then a
/// validly-signed
/// token is expired far more often than not, so refusing it would
/// discard the attribution for precisely the normal case. The bytes left
/// the edge whatever the clock now says; the signature still identifies
/// who minted the grant, and that is the only claim made here.
let verifySignature (signingKey: byte[]) (token: string) : Result<MediaSignedPayload, SignedUrlError> =
    let parts = token.Split('.')

    if parts.Length <> 3 then
        Error Malformed
    else
        let mediaIdFromPrefix = parts[0]

        match base64UrlDecode parts[1], base64UrlDecode parts[2] with
        | Some payloadBytes, Some signatureBytes ->
            let expected = hmacSha256 signingKey payloadBytes

            if not (constantTimeEquals expected signatureBytes) then
                Error InvalidSignature
            else
                match Json.tryDeserialize<MediaSignedPayload> payloadBytes with
                | Some payload when payload.MediaId = mediaIdFromPrefix -> Ok payload
                | Some _ -> Error InvalidSignature
                | None -> Error Malformed
        | _ -> Error Malformed

/// Verify a signed token against the signing key and the current clock.
/// Pure — returns the validated payload, or a `SignedUrlError`
/// (`Malformed` / `InvalidSignature` / `Expired`). Signature comparison
/// is constant-time.
///
/// Layered over `verifySignature` rather than duplicating it, so the two
/// cannot drift into disagreeing about what a well-formed token is —
/// the expiry test is the whole of the difference, and it is visible
/// here as the whole of the difference.
let verify (signingKey: byte[]) (token: string) (now: DateTimeOffset) : Result<MediaSignedPayload, SignedUrlError> =
    match verifySignature signingKey token with
    | Error e -> Error e
    | Ok payload ->
        if payload.ExpiresAtUnix <= now.ToUnixTimeSeconds() then
            Error SignedUrlError.Expired
        else
            Ok payload

// ─── Signing-key resolution (mirrors ShareTokenStore) ─────────────────

let private resolveSigningKey (secretStore: ISecretStore) : Async<Result<byte[], string>> = async {
    let! existing = secretStore.GetSecret(platformContainer, signingKeySecretName)

    match existing with
    | Some s ->
        match base64UrlDecode s with
        | Some bytes when bytes.Length >= 32 -> return Ok bytes
        | _ -> return Error "media_library_signing_key is malformed (expected base64url-encoded 32+ bytes)"
    | None ->
        let key = Array.zeroCreate<byte> 32
        use rng = RandomNumberGenerator.Create()
        rng.GetBytes(key)
        let encoded = base64UrlEncode key
        let! saveResult = secretStore.SetSecret(platformContainer, signingKeySecretName, encoded)

        match saveResult with
        | Ok() -> return Ok key
        | Error e -> return Error(sprintf "couldn't persist media_library_signing_key: %s" e)
}

/// Mints + verifies media URL tokens against a signing key resolved
/// (and memoised) from `ISecretStore`. Stateless except for the cached
/// key, which double-check-locks like `BlobShareTokenStore`.
type MediaUrlSigner(secretStore: ISecretStore) =
    let lockObj = obj ()
    let mutable signingKeyCache: byte[] option = None

    let getSigningKey () = async {
        match signingKeyCache with
        | Some k -> return Ok k
        | None ->
            let! resolved = resolveSigningKey secretStore

            match resolved with
            | Ok k ->
                lock lockObj (fun () ->
                    if signingKeyCache.IsNone then
                        signingKeyCache <- Some k)

                return Ok k
            | Error e -> return Error(KeyResolutionFailed e)
    }

    /// Mint a signed token bound to the scope, expiring at `now + ttl`.
    member _.SignAsync
        (id: MediaId, scope: StorageScope, ttl: TimeSpan, now: DateTimeOffset)
        : Async<Result<string, SignedUrlError>> =
        async {
            match! getSigningKey () with
            | Error e -> return Error e
            | Ok key -> return Ok(mint key id scope (now + ttl))
        }

    /// Verify a signed token against the resolved key and `now`.
    member _.VerifyAsync(token: string, now: DateTimeOffset) : Async<Result<MediaSignedPayload, SignedUrlError>> = async {
        match! getSigningKey () with
        | Error e -> return Error e
        | Ok key -> return verify key token now
    }

    /// Verify a token's signature against the resolved key WITHOUT
    /// consulting expiry — see `verifySignature`. Used by Phase 742's
    /// delivered-egress attribution, never by a serving path.
    member _.VerifySignatureAsync(token: string) : Async<Result<MediaSignedPayload, SignedUrlError>> = async {
        match! getSigningKey () with
        | Error e -> return Error e
        | Ok key -> return verifySignature key token
    }

// ─── Phase 472 — delegated URL signing ────────────────────────────────
//
// The origin HMAC above binds `(MediaId, ScopeId, Container, ExpiresAt)`
// and is verified by this origin's own range handler. That is exactly
// right when the origin serves the bytes — and exactly wrong once a CDN
// does: the viewer never reaches the origin, so nothing ever verifies
// the token, and the object is world-readable at the edge.
//
// Every CDN of the CloudFront / Cloudflare class solves this with its
// OWN signed-URL scheme, verified at the edge. `IDelegatedUrlSigner` is
// the seam that lets a deployment mint those instead.
//
// **The seam takes the deployment's signing callback — no cloud SDK in
// the interface** (the `ICloudTranscodeProvider` pattern). A signing
// scheme needs a private key, and a private key belongs to the
// deployment, not to the SDK: forge would otherwise have to model
// key-pair provisioning, rotation and per-vendor canonicalisation for
// every CDN anyone might use. Instead the interface speaks only in
// value types the SDK already owns, and the reference sub-companion
// (`ToolUp.Hosts.EdgeCache`) turns a plain `sign: string -> string`
// callback into an implementation.
//
// **The origin HMAC remains the default AND the verification
// fallback.** Composing a delegated signer changes what `SignedUrl`
// MINTS; it does not remove `/media/signed/{id}?token=`, and does not
// stop `MediaUrlSigner.VerifyAsync` from verifying an origin token. A
// deployment that composes a signer and later removes it keeps working,
// and a token minted before the switch keeps working until it expires.
//
// ─── The six portability rules (GP 12) ────────────────────────────────
//
// 1. *Identity by value* — `MediaId` (a single-case string DU) and
//    `StorageScope` (a record) in, a `string` URL out.
// 2. *Async at every boundary* — `SignUrl` returns `Async<Result<…>>`;
//    a real signer may need to fetch a key from `ISecretStore`.
// 3. *Failure / policy as data* — failure is the existing
//    `SignedUrlError`, not an exception and not a callback. The TTL is
//    a `TimeSpan` parameter, not a policy object the SDK closes over.
// 4. *Stateless between invocations* — every call carries the item, the
//    scope and the TTL. An implementation may cache a key; it holds no
//    per-call continuity.
// 5. *No cross-shard ordering promises* — each mint is independent.
// 6. *Precision at the lower bound* — `ttl` is a `TimeSpan`, and an
//    implementation declares the granularity its edge actually honours
//    via `TtlPrecision` (most CDNs sign to whole seconds). A caller
//    that needs sub-second expiry is told, not silently rounded.

/// The granularity an edge actually honours for a signed URL's expiry.
/// Rule 6 — declared rather than assumed, because a signer that rounds a
/// 500 ms TTL up to a second has extended the window and nothing said so.
type SignedUrlTtlPrecision =
    | TtlSecond
    | TtlMinute

/// Mint a CDN-native signed URL for a media item, verified at the edge
/// rather than at this origin. Composed via
/// `MediaLibraryServerApp.withDelegatedUrlSigner`; absent it, media URL
/// minting takes the origin HMAC path exactly as before (GP 11).
type IDelegatedUrlSigner =
    /// Stable name for diagnostics and audit lines (e.g.
    /// `"callback-signer"`). Not an identity the SDK dispatches on.
    abstract Name: string

    /// The expiry granularity this signer's edge honours (rule 6).
    abstract TtlPrecision: SignedUrlTtlPrecision

    /// Mint an ABSOLUTE signed URL for `id`, viewable for `ttl`. The
    /// scope is passed so a signer can bind the viewing tenant into the
    /// URL (or into a signed policy document) exactly as the origin HMAC
    /// does — a delegated signer that ignores it has widened the gate,
    /// and the seam is shaped so that is a visible choice rather than an
    /// impossible one.
    ///
    /// Returns an absolute URL because the whole point is that it does
    /// not resolve against this origin. A relative result would resolve
    /// against whatever host served the page, which on a CDN-fronted
    /// deployment is the edge — where there is no origin route to hit.
    abstract SignUrl: id: MediaId * scope: StorageScope * ttl: TimeSpan -> Async<Result<string, SignedUrlError>>
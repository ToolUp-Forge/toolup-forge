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

/// Verify a signed token against the signing key and the current clock.
/// Pure — returns the validated payload, or a `SignedUrlError`
/// (`Malformed` / `InvalidSignature` / `Expired`). Signature comparison
/// is constant-time.
let verify (signingKey: byte[]) (token: string) (now: DateTimeOffset) : Result<MediaSignedPayload, SignedUrlError> =
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
                | Some payload when payload.MediaId = mediaIdFromPrefix ->
                    if payload.ExpiresAtUnix <= now.ToUnixTimeSeconds() then
                        Error Expired
                    else
                        Ok payload
                | Some _ -> Error InvalidSignature
                | None -> Error Malformed
        | _ -> Error Malformed

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
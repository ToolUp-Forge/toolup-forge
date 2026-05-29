module ToolUp.Platform.ShareTokenStore

open System
open System.Security.Cryptography
open System.Text
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets

// ─── Storage layout ──────────────────────────────────────────────────
//
// Container: always `_platform`.
// Token blob:           `share-tokens/{scopeId}/{tokenId}.json`
// Resource index entry: `share-tokens/{scopeId}/_by-resource/{sha256(kind|id)}/{tokenId}.ref`
//
// Mirrors the `_platform/jobs/`, `_platform/webhooks/`, and
// `_platform/audit/` layouts so operators see one consistent shape
// across SDK subsystems. Scope sits in the path prefix so per-scope
// operations are a cheap `List(container, "share-tokens/{scopeId}/...")`
// call and cross-scope leakage is structurally impossible.
//
// Resource-index segment hashes `{kind}|{id}` to bound path length
// and avoid path-incompatible characters in user-supplied
// `resourceKind` / `resourceId` values.

[<Literal>]
let private platformContainer = "_platform"

[<Literal>]
let private signingKeySecretName = "share_token_signing_key"

let private tokenBlob (scopeId: string) (tokenId: string) =
    $"share-tokens/{scopeId}/{tokenId}.json"

let private resourceHash (resourceKind: string) (resourceId: string) =
    use sha = SHA256.Create()
    let key = $"{resourceKind}|{resourceId}"
    let hash = sha.ComputeHash(Encoding.UTF8.GetBytes key)
    Convert.ToHexString(hash).ToLowerInvariant()

let private resourceIndexBlob (scopeId: string) (resourceKind: string) (resourceId: string) (tokenId: string) =
    let h = resourceHash resourceKind resourceId
    $"share-tokens/{scopeId}/_by-resource/{h}/{tokenId}.ref"

let private resourceIndexPrefix (scopeId: string) (resourceKind: string) (resourceId: string) =
    let h = resourceHash resourceKind resourceId
    $"share-tokens/{scopeId}/_by-resource/{h}/"

let private tokensPrefix (scopeId: string) = $"share-tokens/{scopeId}/"

// ─── JSON ────────────────────────────────────────────────────────────

module private Json =
    let private settings =
        let s = JsonSerializerSettings()
        s.Converters.Add(FableJsonConverter())
        s

    let serialize (value: 'T) : byte[] =
        JsonConvert.SerializeObject(value, settings) |> Encoding.UTF8.GetBytes

    let serializeString (value: 'T) : string =
        JsonConvert.SerializeObject(value, settings)

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonConvert.DeserializeObject<'T>(json, settings))
        with _ ->
            None

// ─── Base64url helpers ───────────────────────────────────────────────

let private base64UrlEncode (bytes: byte[]) : string =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private base64UrlDecode (s: string) : byte[] option =
    try
        let pad = (4 - s.Length % 4) % 4
        let padded = s.Replace('-', '+').Replace('_', '/') + String.replicate pad "="
        Some(Convert.FromBase64String padded)
    with _ ->
        None

// ─── HMAC helpers ────────────────────────────────────────────────────

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

// ─── Wire format ─────────────────────────────────────────────────────
//
// `{tokenId}.{base64url(payloadJson)}.{base64url(hmac)}`
//
// `tokenId` is the storage-key for the persisted claim. The signed
// `payload` carries (TokenId, ScopeId, ResourceKind, ResourceId) so
// a swapped tokenId fails verification and a swapped scope or
// resource is detected without trusting the embedded values — the
// persisted record stays the source of truth.

/// Public so Newtonsoft's reflection-based serialisation through
/// `FableJsonConverter` round-trips the fields. A `private` F# record
/// hides its accessors from the converter, which silently produces
/// `{}` and breaks the Validate path. Same gotcha as
/// `JobStore.IdempotencyEntry`.
type SignedPayload = {
    TokenId: string
    ScopeId: string
    ResourceKind: string
    ResourceId: string
}

let private encodeToken (signingKey: byte[]) (claim: ShareTokenClaim) : string =
    let payload = {
        TokenId = claim.TokenId
        ScopeId = claim.ScopeId
        ResourceKind = claim.ResourceKind
        ResourceId = claim.ResourceId
    }

    let payloadJson = Json.serializeString payload
    let payloadBytes = Encoding.UTF8.GetBytes payloadJson
    let signature = hmacSha256 signingKey payloadBytes
    sprintf "%s.%s.%s" claim.TokenId (base64UrlEncode payloadBytes) (base64UrlEncode signature)

/// Returns the verified `tokenId` plus the parsed `SignedPayload` on
/// success, so callers can cross-check the embedded scope/resource
/// against the persisted claim.
let private parseToken (signingKey: byte[]) (token: string) : Result<string * SignedPayload, ShareTokenError> =
    let parts = token.Split('.')

    if parts.Length <> 3 then
        Error ShareTokenError.Malformed
    else
        let tokenIdFromPrefix = parts[0]

        match base64UrlDecode parts[1], base64UrlDecode parts[2] with
        | Some payloadBytes, Some signatureBytes ->
            let expected = hmacSha256 signingKey payloadBytes

            if not (constantTimeEquals expected signatureBytes) then
                Error ShareTokenError.InvalidSignature
            else
                match Json.tryDeserialize<SignedPayload> payloadBytes with
                | Some payload when payload.TokenId = tokenIdFromPrefix -> Ok(tokenIdFromPrefix, payload)
                | Some _ -> Error ShareTokenError.InvalidSignature
                | None -> Error ShareTokenError.Malformed
        | _ -> Error ShareTokenError.Malformed

// ─── Signing-key resolution ──────────────────────────────────────────
//
// 32-byte HMAC key persisted in `ISecretStore` under the reserved
// `_platform` scope. Auto-generated and saved on first use if the
// secret is absent — operators get share-tokens working immediately
// without having to pre-provision the key, but rotation remains
// possible via the secret store's normal Set/Delete flow (operator
// regenerates, restarts the process, all previously-issued tokens
// fail verification — the documented behaviour).

let private resolveSigningKey (secretStore: ISecretStore) : Async<Result<byte[], string>> = async {
    let! existing = secretStore.GetSecret(platformContainer, signingKeySecretName)

    match existing with
    | Some s ->
        match base64UrlDecode s with
        | Some bytes when bytes.Length >= 32 -> return Ok bytes
        | _ -> return Error "share_token_signing_key is malformed (expected base64url-encoded 32+ bytes)"
    | None ->
        let key = Array.zeroCreate<byte> 32
        use rng = RandomNumberGenerator.Create()
        rng.GetBytes(key)
        let encoded = base64UrlEncode key
        let! saveResult = secretStore.SetSecret(platformContainer, signingKeySecretName, encoded)

        match saveResult with
        | Ok() -> return Ok key
        | Error e -> return Error(sprintf "couldn't persist share_token_signing_key: %s" e)
}

// ─── BlobShareTokenStore ─────────────────────────────────────────────

/// Default `IShareTokenStore` impl. Persists claims under
/// `_platform/share-tokens/{scopeId}/...` and HMAC-signs token strings
/// against a key resolved from `ISecretStore`. Audit emission is
/// optional — pass `None` for the `audit` constructor parameter when
/// the deployment runs `AuditLog = NoAuditLog`.
type BlobShareTokenStore(storage: IBlobStorage, secretStore: ISecretStore, audit: IAuditLog option, logger: ILogger) =

    let lockObj = obj ()

    // Cached signing key — resolved lazily on first use, then
    // memoised. ISecretStore reads are cheap but not free; one read
    // per token operation would be wasteful.
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
            | Error e -> return Error(ShareTokenError.StorageFailed e)
    }

    let writeClaim (claim: ShareTokenClaim) = async {
        let bytes = Json.serialize claim
        let! result = storage.Upload(platformContainer, tokenBlob claim.ScopeId claim.TokenId, bytes)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error(ShareTokenError.StorageFailed e)
    }

    let writeResourceIndex (claim: ShareTokenClaim) = async {
        let blob =
            resourceIndexBlob claim.ScopeId claim.ResourceKind claim.ResourceId claim.TokenId

        let! result = storage.Upload(platformContainer, blob, [||])

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error(ShareTokenError.StorageFailed e)
    }

    let readClaim (scopeId: string) (tokenId: string) = async {
        let! result = storage.Download(platformContainer, tokenBlob scopeId tokenId)

        match result with
        | Ok bytes ->
            match Json.tryDeserialize<ShareTokenClaim> bytes with
            | Some claim -> return Ok claim
            | None -> return Error(ShareTokenError.StorageFailed "share-token blob deserialisation failed")
        | Error _ -> return Error ShareTokenError.NotFound
    }

    let recordAudit (scopeId: string) (event: AuditEvent) =
        match audit with
        | Some a ->
            // Fire-and-forget; audit failure must never block the
            // primary operation. `IAuditLog.Record` itself logs at
            // Warn on internal errors.
            Async.Start(a.Record(scopeId, event))
        | None -> ()

    interface IShareTokenStore with
        member _.Issue(request) = async {
            // Phase 21e — reject malformed rate-limit at issue-time
            // rather than first-use so operators see the failure in
            // the issue flow rather than the first respondent's
            // request.
            match ShareTokenTypes.validateRateLimit request.RateLimit with
            | Error msg -> return Error(ShareTokenError.StorageFailed msg)
            | Ok() ->

                let! signingKey = getSigningKey ()

                match signingKey with
                | Error err -> return Error err
                | Ok key ->
                    let now = DateTimeOffset.UtcNow

                    let useLimit =
                        match request.UseLimit with
                        | None -> ShareTokenTypes.DefaultUseLimit
                        | Some explicit -> explicit

                    let expiresAt =
                        match request.ExpiresAt with
                        | Some at -> at
                        | None -> now + ShareTokenTypes.DefaultLifetime

                    let tokenId =
                        let bytes = Array.zeroCreate<byte> 16
                        use rng = RandomNumberGenerator.Create()
                        rng.GetBytes(bytes)
                        base64UrlEncode bytes

                    let claim = {
                        TokenId = tokenId
                        ScopeId = request.ScopeId
                        ResourceKind = request.ResourceKind
                        ResourceId = request.ResourceId
                        AttributedHandle = request.AttributedHandle
                        IssuedBy = request.IssuedBy
                        IssuedAt = now
                        ExpiresAt = expiresAt
                        UseLimit = useLimit
                        UsedCount = 0
                        Revoked = false
                        RateLimit = request.RateLimit
                    }

                    match! writeClaim claim with
                    | Error e -> return Error e
                    | Ok() ->
                        match! writeResourceIndex claim with
                        | Error e -> return Error e
                        | Ok() ->
                            let tokenString = encodeToken key claim

                            recordAudit
                                request.ScopeId
                                (AuditEvent.ShareTokenIssued {
                                    UserId = request.IssuedBy
                                    TokenId = tokenId
                                    ResourceKind = request.ResourceKind
                                    ResourceId = request.ResourceId
                                    AttributedHandle = request.AttributedHandle
                                    ExpiresAt = expiresAt
                                })

                            return Ok { Token = tokenString; Claim = claim }
        }

        member _.Validate(token) = async {
            let! signingKey = getSigningKey ()

            match signingKey with
            | Error err -> return Error err
            | Ok key ->
                match parseToken key token with
                | Error err -> return Error err
                | Ok(_, payload) ->
                    let! claimResult = readClaim payload.ScopeId payload.TokenId

                    match claimResult with
                    | Error err -> return Error err
                    | Ok claim ->
                        // Cross-check the signed payload against the
                        // persisted record. A mismatch means someone
                        // tried to splice a different scope / resource
                        // into a valid signature — reject as if the
                        // signature itself was bad.
                        if
                            claim.ScopeId <> payload.ScopeId
                            || claim.ResourceKind <> payload.ResourceKind
                            || claim.ResourceId <> payload.ResourceId
                        then
                            return Error ShareTokenError.InvalidSignature
                        elif claim.Revoked then
                            return Error ShareTokenError.RevokedToken
                        elif claim.ExpiresAt <= DateTimeOffset.UtcNow then
                            return Error ShareTokenError.Expired
                        else
                            match claim.UseLimit with
                            | Some limit when claim.UsedCount >= limit -> return Error ShareTokenError.UseLimitExceeded
                            | _ -> return Ok claim
        }

        member _.MarkUsed(scopeId, tokenId) = async {
            let! claimResult = readClaim scopeId tokenId

            match claimResult with
            | Error err -> return Error err
            | Ok claim ->
                if claim.Revoked then
                    return Error ShareTokenError.RevokedToken
                else
                    match claim.UseLimit with
                    | Some limit when claim.UsedCount >= limit -> return Error ShareTokenError.UseLimitExceeded
                    | _ ->
                        let updated = {
                            claim with
                                UsedCount = claim.UsedCount + 1
                        }

                        match! writeClaim updated with
                        | Error e -> return Error e
                        | Ok() ->
                            recordAudit
                                scopeId
                                (AuditEvent.ShareTokenUsed {
                                    TokenId = tokenId
                                    ResourceKind = claim.ResourceKind
                                    ResourceId = claim.ResourceId
                                    AttributedHandle = claim.AttributedHandle
                                })

                            return Ok()
        }

        member _.Revoke(scopeId, tokenId, actorUserId) = async {
            let! claimResult = readClaim scopeId tokenId

            match claimResult with
            | Error ShareTokenError.NotFound -> return Error ShareTokenError.NotFound
            | Error err -> return Error err
            | Ok claim when claim.Revoked ->
                // Idempotent — already revoked, no audit re-emission.
                return Ok()
            | Ok claim ->
                let updated = { claim with Revoked = true }

                match! writeClaim updated with
                | Error e -> return Error e
                | Ok() ->
                    recordAudit
                        scopeId
                        (AuditEvent.ShareTokenRevoked {
                            UserId = actorUserId
                            TokenId = tokenId
                            ResourceKind = claim.ResourceKind
                            ResourceId = claim.ResourceId
                        })

                    return Ok()
        }

        member _.ListByResource(scopeId, resourceKind, resourceId) = async {
            let prefix = resourceIndexPrefix scopeId resourceKind resourceId
            let! refs = storage.List(platformContainer, prefix)

            let tokenIds =
                refs
                |> List.choose (fun blobName ->
                    // LocalFileStorage on Windows returns paths with `\`;
                    // cloud impls use `/`. Normalise before prefix-match
                    // (mirrors `DataObjectStore.normalize`).
                    let normalised = blobName.Replace('\\', '/')

                    if normalised.StartsWith prefix && normalised.EndsWith ".ref" then
                        let trimmed = normalised.Substring(prefix.Length)
                        Some(trimmed.Substring(0, trimmed.Length - 4))
                    else
                        None)

            let! claims =
                tokenIds
                |> List.map (fun tid -> async {
                    let! r = readClaim scopeId tid

                    return
                        match r with
                        | Ok claim -> Some claim
                        | Error _ ->
                            // A dangling index entry is recoverable noise — log
                            // and skip rather than fail the entire listing.
                            logger.Warn $"share-token index entry without claim: scope=%s{scopeId} token=%s{tid}"
                            None
                })
                |> Async.Parallel

            return claims |> Array.choose id |> Array.toList |> List.sortByDescending _.IssuedAt
        }

        member _.ListByIssuer(scopeId, issuerUserId) = async {
            // No per-issuer index blob exists (unlike `_by-resource/`).
            // Scan the scope's token blobs directly and filter on
            // `IssuedBy` in memory — the membership-change cadence this
            // serves (a leaver's claims, enumerated once on removal)
            // does not warrant a second index to keep in sync on every
            // issue. The prefix `share-tokens/{scopeId}/` also covers
            // the `_by-resource/` index subtree, so direct token blobs
            // are isolated by requiring a `.json` extension AND no `/`
            // in the path segment after the prefix.
            let prefix = tokensPrefix scopeId
            let! blobs = storage.List(platformContainer, prefix)

            let tokenIds =
                blobs
                |> List.choose (fun blobName ->
                    let normalised = blobName.Replace('\\', '/')

                    if normalised.StartsWith prefix && normalised.EndsWith ".json" then
                        let trimmed = normalised.Substring(prefix.Length)

                        // A `/` here means the blob lives under a
                        // sub-prefix (`_by-resource/`), not a top-level
                        // token blob — skip it.
                        if trimmed.Contains '/' then
                            None
                        else
                            Some(trimmed.Substring(0, trimmed.Length - 5))
                    else
                        None)

            let! claims =
                tokenIds
                |> List.map (fun tid -> async {
                    let! r = readClaim scopeId tid

                    return
                        match r with
                        | Ok claim -> Some claim
                        | Error _ ->
                            logger.Warn
                                $"share-token blob unreadable during ListByIssuer: scope=%s{scopeId} token=%s{tid}"

                            None
                })
                |> Async.Parallel

            return
                claims
                |> Array.choose id
                |> Array.filter (fun c -> c.IssuedBy = issuerUserId)
                |> Array.toList
                |> List.sortByDescending _.IssuedAt
        }

/// Convenience constructor mirroring `JobStore.BlobJobStore` /
/// `EntityStore.BlobEntityStore` — keeps the wiring in `compose`
/// terse.
let create
    (storage: IBlobStorage)
    (secretStore: ISecretStore)
    (audit: IAuditLog option)
    (logger: ILogger)
    : IShareTokenStore =
    BlobShareTokenStore(storage, secretStore, audit, logger) :> IShareTokenStore
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ServiceAccountStore

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Storage layout ──────────────────────────────────────────────────
//
// Container: always `_platform`.
// Account:        `service-accounts/{scopeId}/accounts/{accountId}.json`
// Token record:   `service-accounts/{scopeId}/tokens/{tokenId}.json`
// Scope pointer:  `service-accounts/_by-token/{tokenId}.ref`   (body = scopeId)
//
// Mirrors the `_platform/share-tokens/`, `_platform/jobs/` and
// `_platform/audit/` layouts so operators see one shape across SDK
// subsystems. Scope sits in the path prefix, so every per-scope
// operation is a cheap prefix `List` and cross-scope enumeration is
// structurally impossible rather than filtered-for.
//
// **Why the pointer blob exists.** A presented token is
// `tusa_{tokenId}.{secret}` and carries NO scope — deliberately, because
// putting the owning team's id in a credential publishes it to anyone
// who sees the credential, and a scope the CALLER asserts is not
// evidence of anything. So validation needs one scope-free hop:
// `_by-token/{tokenId}.ref` holds the scope id, and the authoritative
// record still lives under the scope prefix where `ListTokens` can
// enumerate it. The pointer holds no authority of its own — a forged or
// corrupted pointer can only send the lookup to a scope where the token
// record does not exist, which is a `NotFound`, never an escalation.
//
// The alternative — one flat token namespace keyed by id — was rejected
// because it makes `ListTokens` a whole-deployment scan and puts every
// tenant's credentials in one prefix, giving up exactly the structural
// isolation the rest of the layout buys (GP 4).

[<Literal>]
let platformContainer = "_platform"

let private accountBlob (scopeId: string) (accountId: string) =
    $"service-accounts/{scopeId}/accounts/{accountId}.json"

let private accountsPrefix (scopeId: string) = $"service-accounts/{scopeId}/accounts/"

let private tokenBlob (scopeId: string) (tokenId: string) =
    $"service-accounts/{scopeId}/tokens/{tokenId}.json"

let private tokensPrefix (scopeId: string) = $"service-accounts/{scopeId}/tokens/"

let private tokenScopePointer (tokenId: string) =
    $"service-accounts/_by-token/{tokenId}.ref"

// ─── JSON ────────────────────────────────────────────────────────────

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<'T>(json, options))
        with _ ->
            None

// ─── Secret generation + hashing ──────────────────────────────────────
//
// The secret is 32 CSPRNG bytes (256 bits), base64url-encoded. What
// persists is base64url(SHA-256(saltBytes ++ secretBytes)) plus the
// per-token salt.
//
// **On the choice of a single SHA-256 rather than a password KDF.** A
// deliberate decision, not an oversight. Argon2/PBKDF2 exist to make an
// offline brute-force of a LOW-ENTROPY human-chosen secret expensive.
// This secret is 256 bits from `RandomNumberGenerator` and is never
// chosen, reused, or typed by a person, so there is no dictionary to
// run and iterated hashing buys nothing measurable while adding a
// per-request CPU cost to every machine call. The salt is still
// per-token so two tokens never share a hash, and the comparison is
// still constant-time so the stored digest is not an oracle.
//
// The comparison uses `JwtCrypto.fixedTimeEquals` — the shared
// `CryptographicOperations.FixedTimeEquals` wrapper in `Platform.Core`
// that `ShareTokenStore`, `StaticJwtAuthProvider` and the InterPlatform
// peer auth provider were all consolidated onto. It is the most-used of
// the several constant-time helpers in the tree, and it is the one the
// `IPeerBearerAuthContract` pack pins, so it is the right one to reuse
// rather than adding an eleventh local copy.

let private randomBase64Url (byteCount: int) =
    let bytes = Array.zeroCreate<byte> byteCount
    use rng = RandomNumberGenerator.Create()
    rng.GetBytes bytes
    Base64Url.encode bytes

let private hashSecret (salt: string) (secret: string) : string =
    let saltBytes = Encoding.UTF8.GetBytes salt
    let secretBytes = Encoding.UTF8.GetBytes secret
    let combined = Array.append saltBytes secretBytes
    Base64Url.encode (SHA256.HashData combined)

/// Constant-time comparison of a computed digest against the persisted
/// one. Both are base64url ASCII, so a UTF-8 byte comparison is exact.
let private digestMatches (expected: string) (actual: string) =
    JwtCrypto.fixedTimeEquals (Encoding.UTF8.GetBytes expected) (Encoding.UTF8.GetBytes actual)

// ─── Blob read / write helpers ────────────────────────────────────────
//
// Module-level rather than class-level because F# permits explicit type
// parameters only on module or member bindings — a `let readJson<'T>`
// inside the type body is FS0665.

/// Read a blob and decode it, distinguishing "absent" from "present but
/// unreadable" via a one-sided `Exists` probe on the error path — the
/// same discipline `ShareTokenStore.readClaim` documents at length. A
/// failed `Download` is not evidence of absence, and reporting a storage
/// fault as `NotFound` points an operator at the wrong subsystem.
let private readJson<'T>
    (storage: IBlobStorage)
    (blob: string)
    (onAbsent: ServiceAccountError)
    : Async<Result<'T, ServiceAccountError>> =
    async {
        let! result = storage.Download(platformContainer, blob)

        match result with
        | Ok bytes ->
            match Json.tryDeserialize<'T> bytes with
            | Some value -> return Ok value
            | None ->
                return Error(ServiceAccountError.StorageFailed $"service-account blob deserialisation failed: {blob}")
        | Error downloadError ->
            let! present = storage.Exists(platformContainer, blob)

            if present then
                return Error(ServiceAccountError.StorageFailed downloadError)
            else
                return Error onAbsent
    }

let private writeJson<'T>
    (storage: IBlobStorage)
    (blob: string)
    (value: 'T)
    : Async<Result<unit, ServiceAccountError>> =
    async {
        let! result = storage.Upload(platformContainer, blob, Json.serialize value)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error(ServiceAccountError.StorageFailed e)
    }

/// Decode every `.json` blob under `prefix`. An individual unreadable
/// blob is logged and skipped rather than failing the whole listing: an
/// admin screen that shows nine of ten accounts is more useful than one
/// that shows an error, and the skipped blob is named in the log.
let private listUnder<'T> (storage: IBlobStorage) (logger: ILogger) (prefix: string) : Async<'T list> = async {
    let! names = storage.List(platformContainer, prefix)

    let! decoded =
        names
        |> List.filter (fun n -> n.EndsWith(".json", StringComparison.Ordinal))
        |> List.map (fun name -> async {
            let! bytes = storage.Download(platformContainer, name)

            match bytes with
            | Ok b -> return Json.tryDeserialize<'T> b
            | Error e ->
                logger.Warn $"service-account listing skipped an unreadable blob '{name}': {e}"
                return None
        })
        |> Async.Sequential

    return decoded |> Array.choose id |> List.ofArray
}

// ─── BlobServiceAccountStore ─────────────────────────────────────────

/// Default `IServiceAccountStore` impl. Persists accounts and token
/// records under `_platform/service-accounts/...`. Audit emission is
/// optional — pass `None` when the deployment runs `AuditLog = NoAuditLog`.
///
/// **Distributed-ready.** No state is held between calls: every
/// operation reads what it needs from `IBlobStorage` and writes back.
/// There is no signing key to cache and no authority memo, so two
/// replicas cannot disagree about whether a token is live. The one
/// concurrency caveat is shared with every blob-backed store in the SDK
/// — a read-modify-write (`SetStatus`, `SetPermissions`, `RevokeToken`)
/// is last-writer-wins across replicas until `IBlobStorage.UploadWithETag`
/// CAS is threaded through. That is benign here in a way it is not for
/// `ShareTokenStore.MarkUsed`: none of these writes carries a counter
/// whose invariant a lost update would break, and the security-relevant
/// direction is safe by shape — a concurrent revoke and a concurrent
/// permission-widening cannot combine to un-revoke a token, because
/// revocation lives on the token record and permissions live on the
/// account record.
type BlobServiceAccountStore(storage: IBlobStorage, audit: IAuditLog option, logger: ILogger) =

    let recordAudit (scopeId: string) (event: AuditEvent) =
        match audit with
        | Some a ->
            // Fire-and-forget; audit failure must never block the
            // primary operation. `IAuditLog.Record` itself logs at Warn
            // on internal errors.
            Async.Start(a.Record(scopeId, event))
        | None -> ()

    /// Read an account and REFUSE a scope mismatch rather than returning
    /// it. The blob path already carries the scope, so a mismatch here
    /// means a corrupted record rather than a cross-tenant reach — but
    /// it is refused either way, because a record whose declared scope
    /// disagrees with its location is exactly the shape whose authority
    /// must not be honoured.
    let readAccount (scopeId: string) (accountId: string) = async {
        match! readJson<ServiceAccount> storage (accountBlob scopeId accountId) ServiceAccountError.AccountNotFound with
        | Error e -> return Error e
        | Ok account when account.ScopeId <> scopeId -> return Error ServiceAccountError.ScopeMismatch
        | Ok account -> return Ok account
    }

    let moduleNames (permissions: Map<string, ModulePermission list>) =
        permissions |> Map.toList |> List.map fst |> List.sort

    interface IServiceAccountStore with

        member _.Create(request) = async {
            match ServiceAccountTypes.validatePermissions request.Permissions with
            | Error e -> return Error e
            | Ok() ->
                let account = {
                    AccountId = randomBase64Url 16
                    DisplayName = request.DisplayName
                    ScopeId = request.ScopeId
                    Permissions = request.Permissions
                    CreatedBy = request.CreatedBy
                    CreatedAt = DateTimeOffset.UtcNow
                    Status = ServiceAccountStatus.Active
                }

                match! writeJson storage (accountBlob account.ScopeId account.AccountId) account with
                | Error e -> return Error e
                | Ok() ->
                    recordAudit
                        account.ScopeId
                        (AuditEvent.ServiceAccountCreated {
                            UserId = request.CreatedBy
                            AccountId = account.AccountId
                            DisplayName = account.DisplayName
                            Modules = moduleNames account.Permissions
                        })

                    return Ok account
        }

        member _.Get(scopeId, accountId) = readAccount scopeId accountId

        member _.List(scopeId) = async {
            let! accounts = listUnder<ServiceAccount> storage logger (accountsPrefix scopeId)
            // Defence in depth: the prefix already isolates the scope,
            // so a record claiming another scope is a corrupted blob and
            // is dropped rather than returned.
            return accounts |> List.filter (fun a -> a.ScopeId = scopeId)
        }

        member _.SetStatus(scopeId, accountId, status, actorUserId) = async {
            match! readAccount scopeId accountId with
            | Error e -> return Error e
            | Ok account ->
                let updated = { account with Status = status }

                match! writeJson storage (accountBlob scopeId accountId) updated with
                | Error e -> return Error e
                | Ok() ->
                    recordAudit
                        scopeId
                        (AuditEvent.ServiceAccountStatusChanged {
                            UserId = actorUserId
                            AccountId = accountId
                            Disabled = (status = ServiceAccountStatus.Disabled)
                        })

                    return Ok updated
        }

        member _.SetPermissions(scopeId, accountId, permissions, actorUserId) = async {
            match ServiceAccountTypes.validatePermissions permissions with
            | Error e -> return Error e
            | Ok() ->
                match! readAccount scopeId accountId with
                | Error e -> return Error e
                | Ok account ->
                    let updated = {
                        account with
                            Permissions = permissions
                    }

                    match! writeJson storage (accountBlob scopeId accountId) updated with
                    | Error e -> return Error e
                    | Ok() ->
                        recordAudit
                            scopeId
                            (AuditEvent.ServiceAccountPermissionsChanged {
                                UserId = actorUserId
                                AccountId = accountId
                                PreviousModules = moduleNames account.Permissions
                                Modules = moduleNames permissions
                            })

                        return Ok updated
        }

        member _.MintToken(request) = async {
            match! readAccount request.ScopeId request.AccountId with
            | Error e -> return Error e
            | Ok account when account.Status = ServiceAccountStatus.Disabled ->
                // A disabled principal must not be able to acquire fresh
                // credentials — otherwise "disable" is only a pause on
                // the tokens that already exist.
                return Error ServiceAccountError.AccountDisabled
            | Ok account ->
                let now = DateTimeOffset.UtcNow

                let expiresAt =
                    match request.ExpiresAt with
                    | Some at -> at
                    | None -> now + ServiceAccountTypes.DefaultTokenLifetime

                let tokenId = randomBase64Url 16
                let salt = randomBase64Url 16
                let secret = randomBase64Url ServiceAccountTypes.SecretBytes

                let record = {
                    TokenId = tokenId
                    AccountId = account.AccountId
                    ScopeId = account.ScopeId
                    Salt = salt
                    SecretHash = hashSecret salt secret
                    DisplayName = request.DisplayName
                    IssuedBy = request.IssuedBy
                    IssuedAt = now
                    ExpiresAt = expiresAt
                    Revoked = false
                    ScopeSnapshot = account.Permissions
                }

                match! writeJson storage (tokenBlob account.ScopeId tokenId) record with
                | Error e -> return Error e
                | Ok() ->
                    // Pointer written AFTER the record, so a crash
                    // between the two leaves an unreachable token rather
                    // than a pointer to a token that does not exist. The
                    // failure direction that matters is the one where a
                    // credential is handed out and cannot be revoked;
                    // this ordering cannot produce it, because the mint
                    // does not return until both writes land.
                    let! pointer =
                        storage.Upload(
                            platformContainer,
                            tokenScopePointer tokenId,
                            Encoding.UTF8.GetBytes account.ScopeId
                        )

                    match pointer with
                    | Error e -> return Error(ServiceAccountError.StorageFailed e)
                    | Ok _ ->
                        recordAudit
                            account.ScopeId
                            (AuditEvent.ServiceAccountTokenMinted {
                                UserId = request.IssuedBy
                                AccountId = account.AccountId
                                TokenId = tokenId
                                DisplayName = request.DisplayName
                                ExpiresAt = expiresAt
                            })

                        return
                            Ok {
                                Secret = ServiceAccountTypes.formatToken tokenId secret
                                Record = record
                            }
        }

        member _.ListTokens(scopeId, accountId) = async {
            let! tokens = listUnder<ServiceAccountToken> storage logger (tokensPrefix scopeId)

            return tokens |> List.filter (fun t -> t.ScopeId = scopeId && t.AccountId = accountId)
        }

        member _.RevokeToken(scopeId, tokenId, actorUserId) = async {
            match! readJson<ServiceAccountToken> storage (tokenBlob scopeId tokenId) ServiceAccountError.NotFound with
            | Error e -> return Error e
            | Ok token when token.ScopeId <> scopeId -> return Error ServiceAccountError.ScopeMismatch
            | Ok token when token.Revoked ->
                // Idempotent — already revoked is a success, and emits no
                // second audit row.
                return Ok()
            | Ok token ->
                match! writeJson storage (tokenBlob scopeId tokenId) { token with Revoked = true } with
                | Error e -> return Error e
                | Ok() ->
                    recordAudit
                        scopeId
                        (AuditEvent.ServiceAccountTokenRevoked {
                            UserId = actorUserId
                            AccountId = token.AccountId
                            TokenId = tokenId
                        })

                    return Ok()
        }

        member _.ValidateToken(token) = async {
            match ServiceAccountTypes.tryParseToken token with
            | Error e -> return Error e
            | Ok(tokenId, secret) ->
                let! pointer = storage.Download(platformContainer, tokenScopePointer tokenId)

                match pointer with
                | Error _ ->
                    // No pointer ⇒ no such token. The pointer carries no
                    // authority, so there is nothing to distinguish here
                    // beyond absence.
                    return Error ServiceAccountError.NotFound
                | Ok scopeBytes ->
                    let scopeId = Encoding.UTF8.GetString scopeBytes

                    match!
                        readJson<ServiceAccountToken> storage (tokenBlob scopeId tokenId) ServiceAccountError.NotFound
                    with
                    | Error e -> return Error e
                    | Ok record ->
                        // Secret comparison FIRST, before revocation or
                        // expiry: a caller who does not hold the secret
                        // learns nothing about the token's state, so the
                        // endpoint cannot be used to probe which ids are
                        // live.
                        if not (digestMatches record.SecretHash (hashSecret record.Salt secret)) then
                            return Error ServiceAccountError.InvalidSecret
                        else
                            match ServiceAccountTypes.classifyToken DateTimeOffset.UtcNow record with
                            | Error e -> return Error e
                            | Ok() ->
                                // Account re-read on EVERY validation — a
                                // disable or a permission narrowing has to
                                // bite on the next request, on any node
                                // (portability rule 4).
                                match! readAccount record.ScopeId record.AccountId with
                                | Error e -> return Error e
                                | Ok account when account.Status = ServiceAccountStatus.Disabled ->
                                    return Error ServiceAccountError.AccountDisabled
                                | Ok account ->
                                    let effective =
                                        ServiceAccountTypes.effectivePermissions
                                            account.Permissions
                                            record.ScopeSnapshot

                                    if effective.IsEmpty then
                                        // Never admit an empty map: it reads
                                        // as UNRESTRICTED downstream.
                                        return Error ServiceAccountError.NoPermissionsDeclared
                                    else
                                        return
                                            Ok {
                                                AccountId = account.AccountId
                                                DisplayName = account.DisplayName
                                                ScopeId = account.ScopeId
                                                TokenId = record.TokenId
                                                Permissions = effective
                                                ExpiresAt = record.ExpiresAt
                                            }
        }

/// Construct the default blob-backed store. Mirrors
/// `ShareTokenStore.create`'s shape so the two substrates are composed
/// the same way.
let create (storage: IBlobStorage) (audit: IAuditLog option) (logger: ILogger) : IServiceAccountStore =
    BlobServiceAccountStore(storage, audit, logger) :> IServiceAccountStore
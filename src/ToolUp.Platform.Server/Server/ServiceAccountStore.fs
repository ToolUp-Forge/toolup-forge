// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Scope.ServiceAccountStore

open System
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Config

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
let platformContainer = ScopeKey.PlatformContainer

let private accountBlob (scopeId: string) (accountId: string) =
    $"service-accounts/{scopeId}/accounts/{accountId}.json"

let private accountsPrefix (scopeId: string) = $"service-accounts/{scopeId}/accounts/"

let private tokenBlob (scopeId: string) (tokenId: string) =
    $"service-accounts/{scopeId}/tokens/{tokenId}.json"

let private tokensPrefix (scopeId: string) = $"service-accounts/{scopeId}/tokens/"

let private tokenScopePointer (tokenId: string) =
    $"service-accounts/_by-token/{tokenId}.ref"

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

// ─── The documents ────────────────────────────────────────────────────

let private platformScope = ScopeKey.platform

/// The token-to-scope pointer is the scope id as plain text.
let private pointerCodec: BlobCodec<string> = {
    Encode = Encoding.UTF8.GetBytes
    Decode = fun bytes -> Ok(Encoding.UTF8.GetString bytes)
}

let private storageFailed (error: BlobMapStoreError) =
    ServiceAccountError.StorageFailed(BlobMapStoreError.describe error)

/// Every document under `prefix`. An individual unreadable or undecodable
/// blob is logged and skipped rather than failing the whole listing: an
/// admin screen that shows nine of ten accounts is more useful than one that
/// shows an error, and the skipped blob is named in the log. (A listing that
/// decides authority never takes this shape: `ValidateToken` reads one
/// document and refuses on a fault.)
let private listUnder<'T> (docs: BlobDocumentStore<'T>) (logger: ILogger) (prefix: string) : Async<'T list> = async {
    let! rows = docs.List(platformScope, prefix, BlobDocumentStoreLimits.DefaultListDegree)

    return
        rows
        |> List.choose (fun (path, verdict) ->
            match verdict with
            | Ok value when (DocPath.value path).EndsWith(".json", StringComparison.Ordinal) -> Some value
            | Ok _ -> None
            | Error e ->
                logger.Warn
                    $"service-account listing skipped an unreadable blob '{DocPath.value path}': {BlobMapStoreError.describe e}"

                None)
}

// ─── BlobServiceAccountStore ─────────────────────────────────────────

/// Default `IServiceAccountStore` impl. Persists accounts and token
/// records under `_platform/service-accounts/...`. Audit emission is
/// optional — pass `None` when the deployment runs `AuditLog = FeatureMode.Disabled`.
///
/// **Distributed-ready.** No state is held between calls: every
/// operation reads what it needs from `IBlobStorage` and writes back.
/// There is no signing key to cache and no authority memo, so two
/// replicas cannot disagree about whether a token is live. Every document
/// goes through the platform's durable document store
/// (`BlobDocumentStore`): a read-modify-write (`SetStatus`,
/// `SetPermissions`, `RevokeToken`) is one guarded update, conditional where
/// the backend has the ETag seam, so a concurrent writer on another replica
/// is replayed rather than lost; an unreadable record is `StorageFailed`,
/// never `NotFound`. The security-relevant direction is safe by shape as
/// well: a concurrent revoke and a concurrent permission-widening cannot
/// combine to un-revoke a token, because revocation lives on the token
/// record and permissions live on the account record.
type BlobServiceAccountStore(storage: IBlobStorage, audit: IAuditLog option, logger: ILogger) =

    // Phase 1026 — fire-and-forget, and observed: a failing sink loses that
    // one row and logs a Warning naming it, never blocking the primary
    // operation or crashing the host.
    let auditWarn =
        Background.writeFailureLogger "BlobServiceAccountStore" (Some logger) "" "the operation"

    let accountDocs =
        BlobDocumentStore<ServiceAccount>(storage, BlobCodec.json<ServiceAccount>, logger)

    let tokenDocs =
        BlobDocumentStore<ServiceAccountToken>(storage, BlobCodec.json<ServiceAccountToken>, logger)

    let pointerDocs = BlobDocumentStore<string>(storage, pointerCodec, logger)

    let accountPath scopeId accountId =
        DocPath.ofString (accountBlob scopeId accountId)

    let tokenPath scopeId tokenId =
        DocPath.ofString (tokenBlob scopeId tokenId)

    /// Write a fresh document (a new account, a new token record, a pointer).
    let create (docs: BlobDocumentStore<'T>) (path: DocPath) (value: 'T) = async {
        let! written = docs.Update(platformScope, path, fun _ -> BlobUpdate.Write(value, ()))
        return written |> Result.mapError storageFailed
    }

    /// One guarded read-modify-write of an account in its scope. `change`
    /// sees the stored account and returns the replacement, or `Error` to
    /// refuse; nothing is written on a refusal.
    let changeAccount
        (scopeId: string)
        (accountId: string)
        (change: ServiceAccount -> Result<ServiceAccount, ServiceAccountError>)
        =
        async {
            let! outcome =
                accountDocs.Update(
                    platformScope,
                    accountPath scopeId accountId,
                    function
                    | None -> BlobUpdate.Keep(Error ServiceAccountError.AccountNotFound)
                    | Some account when account.ScopeId <> scopeId ->
                        BlobUpdate.Keep(Error ServiceAccountError.ScopeMismatch)
                    | Some account ->
                        match change account with
                        | Error e -> BlobUpdate.Keep(Error e)
                        | Ok updated -> BlobUpdate.Write(updated, Ok(account, updated))
                )

            return outcome |> Result.mapError storageFailed |> Result.bind id
        }

    let recordAudit (scopeId: string) (event: AuditEvent) =
        match audit with
        | Some a ->
            let what = sprintf "%s audit" (AuditEvent.eventTypeName event)

            try
                a.Record(scopeId, event) |> Background.startAudit auditWarn what
            with ex ->
                auditWarn what ex
        | None -> ()

    /// Read an account and REFUSE a scope mismatch rather than returning
    /// it. The blob path already carries the scope, so a mismatch here
    /// means a corrupted record rather than a cross-tenant reach — but
    /// it is refused either way, because a record whose declared scope
    /// disagrees with its location is exactly the shape whose authority
    /// must not be honoured.
    let readAccount (scopeId: string) (accountId: string) = async {
        match! accountDocs.Read(platformScope, accountPath scopeId accountId) with
        | Error e -> return Error(storageFailed e)
        | Ok None -> return Error ServiceAccountError.AccountNotFound
        | Ok(Some account) when account.ScopeId <> scopeId -> return Error ServiceAccountError.ScopeMismatch
        | Ok(Some account) -> return Ok account
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

                match! create accountDocs (accountPath account.ScopeId account.AccountId) account with
                | Error e -> return Error e
                | Ok() ->
                    recordAudit
                        account.ScopeId
                        (AuditEvent.ServiceAccountCreated {
                            UserId = UserId request.CreatedBy
                            AccountId = account.AccountId
                            DisplayName = account.DisplayName
                            Modules = moduleNames account.Permissions
                        })

                    return Ok account
        }

        member _.Get(scopeId, accountId) = readAccount scopeId accountId

        member _.List(scopeId) = async {
            let! accounts = listUnder accountDocs logger (accountsPrefix scopeId)
            // Defence in depth: the prefix already isolates the scope,
            // so a record claiming another scope is a corrupted blob and
            // is dropped rather than returned.
            return accounts |> List.filter (fun a -> a.ScopeId = scopeId)
        }

        member _.SetStatus(scopeId, accountId, status, actorUserId) = async {
            match! changeAccount scopeId accountId (fun account -> Ok { account with Status = status }) with
            | Error e -> return Error e
            | Ok(_, updated) ->
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
                match!
                    changeAccount scopeId accountId (fun account ->
                        Ok {
                            account with
                                Permissions = permissions
                        })
                with
                | Error e -> return Error e
                | Ok(before, updated) ->
                    recordAudit
                        scopeId
                        (AuditEvent.ServiceAccountPermissionsChanged {
                            UserId = actorUserId
                            AccountId = accountId
                            PreviousModules = moduleNames before.Permissions
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

                match! create tokenDocs (tokenPath account.ScopeId tokenId) record with
                | Error e -> return Error e
                | Ok() ->
                    // Pointer written AFTER the record, so a crash
                    // between the two leaves an unreachable token rather
                    // than a pointer to a token that does not exist. The
                    // failure direction that matters is the one where a
                    // credential is handed out and cannot be revoked;
                    // this ordering cannot produce it, because the mint
                    // does not return until both writes land.
                    match! create pointerDocs (DocPath.ofString (tokenScopePointer tokenId)) account.ScopeId with
                    | Error e -> return Error e
                    | Ok() ->
                        recordAudit
                            account.ScopeId
                            (AuditEvent.ServiceAccountTokenMinted {
                                UserId = UserId request.IssuedBy
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
            let! tokens = listUnder tokenDocs logger (tokensPrefix scopeId)

            return tokens |> List.filter (fun t -> t.ScopeId = scopeId && t.AccountId = accountId)
        }

        member _.RevokeToken(scopeId, tokenId, actorUserId) = async {
            let! outcome =
                tokenDocs.Update(
                    platformScope,
                    tokenPath scopeId tokenId,
                    function
                    | None -> BlobUpdate.Keep(Error ServiceAccountError.NotFound)
                    | Some token when token.ScopeId <> scopeId ->
                        BlobUpdate.Keep(Error ServiceAccountError.ScopeMismatch)
                    // Idempotent: already revoked is a success, and emits no
                    // second audit row.
                    | Some token when token.Revoked -> BlobUpdate.Keep(Ok None)
                    | Some token -> BlobUpdate.Write({ token with Revoked = true }, Ok(Some token))
                )

            match outcome |> Result.mapError storageFailed |> Result.bind id with
            | Error e -> return Error e
            | Ok None -> return Ok()
            | Ok(Some token) ->
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
                let! pointer = pointerDocs.Read(platformScope, DocPath.ofString (tokenScopePointer tokenId))

                match pointer with
                // No pointer: no such token. A pointer that cannot be read
                // is a storage fault, reported as one.
                | Ok None -> return Error ServiceAccountError.NotFound
                | Error e -> return Error(storageFailed e)
                | Ok(Some scopeId) ->
                    match! tokenDocs.Read(platformScope, tokenPath scopeId tokenId) with
                    | Error e -> return Error(storageFailed e)
                    | Ok None -> return Error ServiceAccountError.NotFound
                    | Ok(Some record) ->
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
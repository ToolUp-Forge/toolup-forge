// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── IServiceAccountStore (Phase 527) ────────────────────────────────
//
// SDK-level interface for the service-account substrate: named machine
// principals owned by a storage scope, each minting scoped, expiring,
// revocable API tokens. The default impl is `BlobServiceAccountStore`;
// a distributed companion (a directory-backed principal store, a
// hosted-secrets-backed token store) binds to this interface.
//
// The type model, the claim shape a validated token resolves to, and
// the account-∩-snapshot authority rule all live in
// `ToolUp.Platform.Core`'s `ServiceAccountTypes.fs` — read that file's
// preamble first; this one is only the operations.
//
// **The scope parameter is not decoration.** Every management member
// takes `scopeId` and the implementation MUST refuse an account whose
// `ScopeId` differs (`ServiceAccountError.ScopeMismatch`). An admin in
// team A naming team B's account id is a cross-tenant read, and the
// only structural place to stop it is here, at the store — a handler
// that "remembers to filter" is exactly the convention GP 4 exists to
// replace.
//
// **`ValidateToken` re-reads BOTH records, every call.** Not a cache
// with an invalidation story: an actual re-read of the token record and
// the owning account record on every request. Disabling an account or
// revoking a token has to take effect on the next request on every
// node, and portability rule 4 forbids assuming in-memory state
// survives between invocations anyway. The cost is two blob reads per
// machine request; a distributed implementation that wants to do better
// owns that decision and its correctness argument.
//
// **Phase 9c portability rules (all six honoured):**
//
//   1. Identity by value. Every parameter and return is a string, a
//      `Map`, or a domain record. No live handles.
//   2. Async at every boundary. Every member returns `Async<_>`.
//   3. Retry / supervision as data. Failures are `ServiceAccountError`
//      values; no `OnFailure` callbacks, no exceptions on expected
//      paths.
//   4. Stateless handlers between invocations. See the re-read note
//      above — the interface promises nothing is remembered.
//   5. No cross-shard ordering promises. Accounts and tokens are
//      independent; nothing orders operations across scopes, and
//      `ListTokens` returns no ordering guarantee beyond what the
//      caller sorts for itself.
//   6. Precision at the lower bound. Expiry is second-precision
//      (`ServiceAccountTypes.ExpiryPrecision`), the JWT `exp` lower
//      bound. No sub-second promise is made or required.

type IServiceAccountStore =
    /// Create a named machine principal in `request.ScopeId`. The store
    /// assigns `AccountId`, stamps `CreatedAt`, and starts the account
    /// `Active`. Refuses an empty declared permission set
    /// (`NoPermissionsDeclared`) — see the `validatePermissions` note in
    /// `ServiceAccountTypes`: an empty `ModulePermissions` map reads as
    /// *unrestricted* everywhere else in the platform.
    abstract Create: request: ServiceAccountCreateRequest -> Async<Result<ServiceAccount, ServiceAccountError>>

    /// Read one account. `Error ScopeMismatch` when the account exists
    /// but belongs to another scope — never `Ok` and never the other
    /// scope's record.
    abstract Get: scopeId: string * accountId: string -> Async<Result<ServiceAccount, ServiceAccountError>>

    /// Every account owned by `scopeId`, active and disabled alike.
    /// Callers filter. No ordering promise (rule 5).
    abstract List: scopeId: string -> Async<ServiceAccount list>

    /// Flip an account's `Status`. Disabling refuses every one of its
    /// tokens wholesale on the next request without touching the tokens
    /// themselves, so enabling restores the prior credential set.
    /// Idempotent. `actorUserId` is the audit attribution.
    abstract SetStatus:
        scopeId: string * accountId: string * status: ServiceAccountStatus * actorUserId: string ->
            Async<Result<ServiceAccount, ServiceAccountError>>

    /// Replace an account's declared permission set. Narrowing takes
    /// effect on every outstanding token at its next use (the live
    /// account is the ceiling half of the authority meet); widening does
    /// NOT widen already-minted tokens, whose mint-time snapshot is the
    /// other half. Refuses an empty set for the same reason `Create`
    /// does.
    abstract SetPermissions:
        scopeId: string * accountId: string * permissions: Map<string, ModulePermission list> * actorUserId: string ->
            Async<Result<ServiceAccount, ServiceAccountError>>

    /// Mint a token for an account. **The returned
    /// `MintedServiceAccountToken.Secret` is the only exposure of the
    /// secret that will ever exist** — what persists is a per-token salt
    /// plus SHA-256(salt ++ secret). Refuses a disabled account
    /// (`AccountDisabled`): a disabled principal must not be able to
    /// acquire fresh credentials.
    abstract MintToken:
        request: ServiceAccountTokenMintRequest -> Async<Result<MintedServiceAccountToken, ServiceAccountError>>

    /// Every token record for one account — active, expired and revoked
    /// alike, so an admin UI can show a credential's whole history.
    /// Never carries a secret (there is none to carry).
    abstract ListTokens: scopeId: string * accountId: string -> Async<ServiceAccountToken list>

    /// Permanently revoke one token. Idempotent — revoking an
    /// already-revoked token returns `Ok`. `actorUserId` is the audit
    /// attribution.
    abstract RevokeToken:
        scopeId: string * tokenId: string * actorUserId: string -> Async<Result<unit, ServiceAccountError>>

    /// Validate a presented token string and resolve the machine
    /// principal it authenticates.
    ///
    /// Applies, in order: wire-format parse → token record read →
    /// constant-time secret comparison → revocation → expiry → owning
    /// account read → account status → authority meet. A failure at any
    /// gate is a typed `ServiceAccountError`; the handler collapses the
    /// token-path cases to one observable 401 so the endpoint is not an
    /// oracle for which tokens exist.
    ///
    /// The resolved `Permissions` are guaranteed non-empty — an empty
    /// meet returns `NoPermissionsDeclared` rather than a principal,
    /// because an empty map would read as unrestricted downstream.
    abstract ValidateToken: token: string -> Async<Result<ServiceAccountPrincipal, ServiceAccountError>>
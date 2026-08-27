// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Service accounts + scoped API tokens (Phase 527) ─────────────────
//
// Machine principals. A `ServiceAccount` is a named, non-human identity
// OWNED BY A SCOPE (a team, or whatever `ScopeId` the deployment's
// storage-scope model uses), carrying a DECLARED module-permission set.
// It mints `ServiceAccountToken`s — scoped, expiring, revocable bearer
// credentials — and a request presenting one resolves to a `Subject`
// carrying the account's authority, never a human user's.
//
// **The subject shape is `ClaimBearer`, deliberately.** A service
// account is a new claim-bearer SHAPE, not a fifth `Subject` case and
// not a parallel auth path: the token handler synthesises a
// `ShareTokenClaim` whose `ScopeId` is the account's owning scope and
// whose `ResourceKind` is `ServiceAccountTypes.ClaimResourceKind`, and
// the existing `ISubjectResolver` four-step algorithm resolves it to
// `ClaimBearer claim` with no change to the Subject model. Every
// downstream consequence follows for free: `AccessContext.configScope`
// already derives the claim's scope, so cross-scope reach is
// structurally impossible (GP 4), and `SurfaceEnforcementMiddleware`
// already gates `ClaimBearerKind` per route.
//
// **Where authority lives.** The account's declared permissions ride
// `AccessContext.ModulePermissions`, so `canAccessModule` /
// `hasPermission` / `ServerModule.withGuardedApi` gate a machine caller
// through exactly the RBAC path a human caller goes through. Note the
// platform convention that an EMPTY `ModulePermissions` map means
// UNRESTRICTED (opt-in RBAC) — which is the wrong floor for a machine
// principal, so an empty declared set is refused at BOTH ends: the
// store rejects a create/update carrying one
// (`ServiceAccountError.NoPermissionsDeclared`), and token validation
// refuses a token whose effective set came out empty. A service account
// is never unrestricted by omission.
//
// **Tokens are never stored in plaintext.** `Issue` returns the secret
// exactly once; what persists is a per-token random salt plus
// SHA-256(salt ++ secret), compared in constant time on validation.
// There is no recovery path for a lost secret — mint a new token and
// revoke the old one.
//
// **Portability (GP 12) — the six rules, audited on `IServiceAccountStore`:**
//
//   1. Identity by value. `AccountId` / `TokenId` / `ScopeId` are
//      strings; permissions are a `Map`. No live handles cross the
//      interface.
//   2. Async at every boundary. Every member of `IServiceAccountStore`
//      returns `Async<_>`.
//   3. Retry / supervision as data. Failures are `ServiceAccountError`
//      cases; no `OnFailure` callbacks.
//   4. Stateless handlers between invocations. `ValidateToken` re-reads
//      the persisted token AND the persisted account on every call, so
//      a disable or a revoke takes effect on the next request on any
//      node — no in-memory authority cache is assumed or permitted.
//   5. No cross-shard ordering promises. Accounts and tokens are
//      independent records; nothing orders operations across scopes.
//   6. Precision at the lower bound. Expiry is timestamp-bounded to
//      SECOND precision (`ServiceAccountTypes.ExpiryPrecision`) — the
//      same lower bound the JWT `exp` claim declares — so no
//      implementation is asked to honour a sub-second promise.
//
// Lives in the Fable-compatible `Shared/` layer so the admin UI renders
// the same shapes the server persists (GP 10). No crypto here: hashing
// and comparison are server-tier (`ServiceAccountStore`), because
// `System.Security.Cryptography` is not Fable-compilable.

/// Lifecycle state of a service account. Disabling is the reversible
/// kill switch: a disabled account's tokens are refused WHOLESALE,
/// without touching the tokens themselves, so re-enabling restores the
/// prior credential set rather than forcing a re-mint.
///
/// `[<RequireQualifiedAccess>]` because `Active` / `Disabled` are
/// generic enough to collide with sibling lifecycle DUs across the SDK.
[<RequireQualifiedAccess>]
type ServiceAccountStatus =
    /// Tokens are honoured subject to their own expiry / revocation.
    | Active
    /// Every token belonging to this account is refused, regardless of
    /// its own expiry or revocation state. Reversible.
    | Disabled

/// A machine principal owned by a storage scope.
///
/// `Permissions` is the DECLARED authority ceiling — the set a token
/// minted by this account may carry. It is intersected with the token's
/// own mint-time snapshot at validation (see
/// `ServiceAccountTypes.effectivePermissions`), so narrowing the account
/// narrows every outstanding token immediately, and widening the account
/// never widens a token that was minted before the widening.
type ServiceAccount = {
    /// Stable public id, unique within the deployment. Server-assigned
    /// at create time; effectively immutable.
    AccountId: string
    /// Operator-facing name. Free-form; not an identity.
    DisplayName: string
    /// Owning storage scope. Every token this account mints is bound to
    /// this scope and cannot reach another (GP 4).
    ScopeId: string
    /// Declared module-permission set. Keys are module names, values are
    /// permission lists, exactly as `AccessContext.ModulePermissions`.
    /// MUST be non-empty — an empty map reads as *unrestricted* under
    /// the platform's opt-in-RBAC convention, which is precisely the
    /// wrong default for a machine principal.
    Permissions: Map<string, ModulePermission list>
    /// Authenticated user who created the account.
    CreatedBy: string
    CreatedAt: DateTimeOffset
    Status: ServiceAccountStatus
}

/// A minted token's PERSISTED record. Never carries the secret — the
/// secret exists only in the `MintedServiceAccountToken.Secret` field
/// returned by the mint call, and only at that moment.
type ServiceAccountToken = {
    /// Public id, unique per token. Rides the token string in the clear
    /// so the validator can locate the record before doing any
    /// comparison; it is an identifier, not a secret.
    TokenId: string
    /// Owning account.
    AccountId: string
    /// Owning scope, denormalised from the account so the storage path
    /// and the synthesised claim need no second read.
    ScopeId: string
    /// Per-token random salt, base64url. Distinct per token so two
    /// tokens with the same secret do not share a hash.
    Salt: string
    /// base64url(SHA-256(salt-bytes ++ secret-bytes)). The only
    /// representation of the secret that ever reaches storage.
    SecretHash: string
    /// Operator-facing label ("CI deploy key", "nightly export").
    DisplayName: string
    /// Authenticated user who minted the token.
    IssuedBy: string
    IssuedAt: DateTimeOffset
    /// Hard expiry. Second precision (GP 12 rule 6).
    ExpiresAt: DateTimeOffset
    /// `true` once `RevokeToken` has been called. Revocation is
    /// permanent and idempotent.
    Revoked: bool
    /// The account's `Permissions` AS AT MINT TIME. Retained for
    /// forensics ("what was this credential granted when it was
    /// issued?") and as the ceiling half of
    /// `ServiceAccountTypes.effectivePermissions` — a later widening of
    /// the account does not widen an already-minted token.
    ScopeSnapshot: Map<string, ModulePermission list>
}

/// Input to `IServiceAccountStore.Create`. The store fills in
/// `AccountId`, `CreatedAt`, and `Status` so the caller cannot forge
/// them.
type ServiceAccountCreateRequest = {
    DisplayName: string
    ScopeId: string
    Permissions: Map<string, ModulePermission list>
    CreatedBy: string
}

/// Input to `IServiceAccountStore.MintToken`. The store fills in
/// `TokenId`, `Salt`, `SecretHash`, `IssuedAt`, `Revoked`, and
/// `ScopeSnapshot`.
type ServiceAccountTokenMintRequest = {
    AccountId: string
    ScopeId: string
    DisplayName: string
    IssuedBy: string
    /// `None` = use `ServiceAccountTypes.DefaultTokenLifetime`.
    /// `Some at` = explicit expiry. There is no "never expires" option,
    /// deliberately: an unbounded machine credential is the shape this
    /// substrate exists to replace.
    ExpiresAt: DateTimeOffset option
}

/// The mint result — the ONE and ONLY exposure of the secret. Persist
/// `Record`; hand `Secret` to the operator and forget it.
type MintedServiceAccountToken = {
    /// Wire format: `{TokenPrefix}{tokenId}.{secret}`. Presented as
    /// `Authorization: Bearer <this>`. Opaque — never construct or
    /// parse it by hand outside `ServiceAccountTypes`.
    Secret: string
    /// The persisted record, returned so an admin UI can render the new
    /// row without a second read.
    Record: ServiceAccountToken
}

/// The resolved authority of a validated token. Returned by
/// `IServiceAccountStore.ValidateToken`; the token handler turns it into
/// a `ShareTokenClaim` + an `AccessContext.ModulePermissions` overlay.
type ServiceAccountPrincipal = {
    AccountId: string
    DisplayName: string
    ScopeId: string
    TokenId: string
    /// Account permissions ∩ token snapshot. Guaranteed non-empty — a
    /// validation that computes an empty set returns
    /// `ServiceAccountError.NoPermissionsDeclared` rather than a
    /// principal, because an empty map would read as *unrestricted*.
    Permissions: Map<string, ModulePermission list>
    ExpiresAt: DateTimeOffset
}

/// Failure shape for `IServiceAccountStore`. Distinct cases let the
/// handler decide what to surface: the token-path cases collapse to one
/// observable 401 at the wire (so the endpoint is not an oracle for
/// which tokens exist), while the management-path cases surface
/// verbatim to an authorised admin.
///
/// `[<RequireQualifiedAccess>]` because `NotFound` / `StorageFailed`
/// collide with `ShareTokenError`, `DataObjectError` and other sibling
/// DUs in `namespace ToolUp.Platform`.
[<RequireQualifiedAccess>]
type ServiceAccountError =
    /// Token string could not be parsed into `{prefix}{tokenId}.{secret}`.
    | Malformed
    /// No token record for the presented `TokenId`. Observably identical
    /// to `InvalidSecret` at the wire; distinct so server logs can tell
    /// "guessed an id" from "guessed a secret".
    | NotFound
    /// The record exists and the presented secret does not match its
    /// hash. Never distinguished from `NotFound` in a response.
    | InvalidSecret
    /// `ExpiresAt` is in the past.
    | Expired
    /// `RevokeToken` was called on this token.
    | RevokedToken
    /// The owning account is `Disabled` — every one of its tokens is
    /// refused wholesale, regardless of the token's own state.
    | AccountDisabled
    /// No account record for the id (management path), or a token whose
    /// owning account has been deleted (token path).
    | AccountNotFound
    /// A create / update / validation produced an EMPTY effective
    /// permission set. Refused rather than admitted, because the
    /// platform reads an empty `ModulePermissions` map as unrestricted.
    | NoPermissionsDeclared
    /// The caller's scope does not own the named account. A management
    /// call is never allowed to reach across scopes (GP 4).
    | ScopeMismatch
    /// Underlying storage / hashing raised. Operator-visible (logs);
    /// not surfaced raw to callers.
    | StorageFailed of string

module ServiceAccountTypes =
    /// Reserved `SourceModule` for service-account audit events.
    /// Filtering `IEventStore` reads on this constant returns the
    /// service-account audit trail only.
    [<Literal>]
    let AuditSourceModule = "_platform.audit.service_accounts"

    /// `ShareTokenClaim.ResourceKind` for the claim a validated
    /// service-account token synthesises. Handlers that need to tell a
    /// machine caller from a share-link bearer match on this.
    [<Literal>]
    let ClaimResourceKind = "_platform.service_account"

    /// Prefix on every minted token string. Present so the credential is
    /// recognisable on sight and greppable by a secret scanner — the
    /// same reason every major platform prefixes its API keys. Not a
    /// security control.
    [<Literal>]
    let TokenPrefix = "tusa_"

    /// Default token lifetime when `ExpiresAt = None`. 90 days: long
    /// enough that rotation is a scheduled chore rather than a weekly
    /// interruption, short enough that a forgotten credential dies.
    let DefaultTokenLifetime: TimeSpan = TimeSpan.FromDays 90.0

    /// Declared precision of the expiry contract (GP 12 rule 6). Second,
    /// matching the JWT `exp` lower bound — no implementation is asked
    /// for sub-second expiry.
    let ExpiryPrecision: TimeSpan = TimeSpan.FromSeconds 1.0

    /// Number of random bytes in a minted secret. 32 bytes = 256 bits
    /// from a CSPRNG.
    [<Literal>]
    let SecretBytes = 32

    /// Is the declared permission map usable as a machine principal's
    /// authority? An empty map is refused: `AccessContext.canAccessModule`
    /// treats empty as UNRESTRICTED (opt-in RBAC), so admitting one would
    /// silently grant a service account everything. A module key mapping
    /// to an empty list is equally refused — it is the same hole one
    /// level down.
    let validatePermissions (permissions: Map<string, ModulePermission list>) : Result<unit, ServiceAccountError> =
        if permissions.IsEmpty then
            Error ServiceAccountError.NoPermissionsDeclared
        elif permissions |> Map.exists (fun _ perms -> List.isEmpty perms) then
            Error ServiceAccountError.NoPermissionsDeclared
        else
            Ok()

    /// Every `ModulePermission` case, in ascending authority order. Used
    /// by `effectivePermissions` to compute the meet of two grant lists.
    /// Enumerated rather than reflected so the file stays Fable-safe.
    /// A new case added to `ModulePermission` belongs here too.
    let private allPermissions = [
        ModulePermission.SchemaOnly
        ModulePermission.Read
        ModulePermission.Write
        ModulePermission.Admin
    ]

    /// The authority a validated token actually carries: the MEET of the
    /// owning account's live `Permissions` and the token's mint-time
    /// `ScopeSnapshot`.
    ///
    /// Per module present in BOTH maps, a permission `p` is admitted
    /// when both sides grant it — where "grants" honours the
    /// `ModulePermission.implies` hierarchy, so a snapshot of `[Admin]`
    /// against a live account of `[Write]` yields `Write` rather than
    /// the empty list a naive list-intersection would produce. Modules
    /// absent from either side are dropped entirely.
    ///
    /// The admitted set is then reduced to its MAXIMAL elements —
    /// grants not already implied by another grant in the set. Without
    /// that reduction the meet returns the downward CLOSURE, so a plain
    /// `[Read]` declaration comes back as `[SchemaOnly; Read]`: behaviourally
    /// identical (every `hasPermission` check runs through `implies`
    /// anyway) but not the same VALUE, which matters because this map is
    /// what the admin UI renders and what an operator compares against
    /// what they declared. A credential screen that answers "Schema
    /// only, Read" to a grant of "Read" invites someone to conclude the
    /// system did something they did not ask for. The contract pack
    /// caught exactly that.
    ///
    /// This is what makes the two directions behave correctly and
    /// differently:
    ///   * NARROWING the account narrows every outstanding token on the
    ///     next request (the live side shrinks) — which is the whole
    ///     point of holding the account as the ceiling.
    ///   * WIDENING the account does NOT widen a token minted before the
    ///     widening (the snapshot side does not move) — a credential
    ///     cannot silently gain authority its holder was never issued.
    let effectivePermissions
        (accountPermissions: Map<string, ModulePermission list>)
        (tokenSnapshot: Map<string, ModulePermission list>)
        : Map<string, ModulePermission list> =
        accountPermissions
        |> Map.toList
        |> List.choose (fun (moduleName, live) ->
            match Map.tryFind moduleName tokenSnapshot with
            | None -> None
            | Some snapshot ->
                let granted (held: ModulePermission list) (required: ModulePermission) =
                    held |> List.exists (fun h -> ModulePermission.implies h required)

                let admitted =
                    allPermissions |> List.filter (fun p -> granted live p && granted snapshot p)

                // Keep only the maximal grants — those no OTHER admitted
                // grant already implies. Stated generally rather than as
                // "take the strongest" so an incomparable pair (a future
                // `ModulePermission` case off the Read/Write/Admin spine,
                // as `SchemaOnly` itself once was) survives intact
                // instead of being silently dropped.
                let maximal =
                    admitted
                    |> List.filter (fun p ->
                        admitted |> List.forall (fun q -> q = p || not (ModulePermission.implies q p)))

                if List.isEmpty maximal then
                    None
                else
                    Some(moduleName, maximal))
        |> Map.ofList

    /// Render a token string from its id and secret. The single place
    /// the wire format is constructed.
    let formatToken (tokenId: string) (secret: string) : string =
        sprintf "%s%s.%s" TokenPrefix tokenId secret

    /// Parse a presented token string into `(tokenId, secret)`.
    /// `Error Malformed` for anything that is not
    /// `{TokenPrefix}{tokenId}.{secret}` with both halves non-empty.
    ///
    /// The split is on the FIRST `.` only: `tokenId` is base64url (which
    /// never contains `.`), while the secret is likewise base64url, so
    /// one separator is unambiguous — and splitting on the first keeps a
    /// future secret alphabet that does contain `.` from silently
    /// changing how existing tokens parse.
    let tryParseToken (token: string) : Result<string * string, ServiceAccountError> =
        if String.IsNullOrWhiteSpace token then
            Error ServiceAccountError.Malformed
        elif not (token.StartsWith(TokenPrefix, StringComparison.Ordinal)) then
            Error ServiceAccountError.Malformed
        else
            let body = token.Substring TokenPrefix.Length
            let separator = body.IndexOf '.'

            if separator <= 0 || separator >= body.Length - 1 then
                Error ServiceAccountError.Malformed
            else
                let tokenId = body.Substring(0, separator)
                let secret = body.Substring(separator + 1)
                Ok(tokenId, secret)

    /// Is this token live at `now`, ignoring the account's status?
    /// Ordered so the cheapest and most-informative refusal wins:
    /// revocation is a deliberate act and expiry is a lapse, and an
    /// operator reading a log wants to see which happened.
    let classifyToken (now: DateTimeOffset) (token: ServiceAccountToken) : Result<unit, ServiceAccountError> =
        if token.Revoked then
            Error ServiceAccountError.RevokedToken
        elif token.ExpiresAt <= now then
            Error ServiceAccountError.Expired
        else
            Ok()

// ─── Admin-UI wire surface ────────────────────────────────────────────
//
// `ServiceAccountToken` carries `Salt` and `SecretHash`. Neither is a
// secret in the sense the credential is — a salt is public by design and
// a SHA-256 of 256 random bits is not invertible — but neither has any
// business crossing the wire either, and a field that never leaves the
// server cannot leak from a client bundle, a browser devtools panel, or
// a logged API response. So the admin API speaks a projection, and the
// persisted record stays server-side.

/// A token as the admin UI sees it: everything needed to render the row
/// and decide what to do about it, and nothing derived from the secret.
type ServiceAccountTokenView = {
    TokenId: string
    AccountId: string
    DisplayName: string
    IssuedBy: string
    IssuedAt: DateTimeOffset
    ExpiresAt: DateTimeOffset
    Revoked: bool
}

/// The mint response as the admin UI sees it. `Secret` is displayed once
/// behind an explicit "I have copied this" acknowledgement and is not
/// retrievable afterwards — there is no server-side copy to retrieve.
type MintedServiceAccountTokenView = {
    Secret: string
    Token: ServiceAccountTokenView
}

/// Payload for `IServiceAccountApi.CreateAccount`. `ScopeId`, `CreatedBy`,
/// `CreatedAt` and `Status` are resolved server-side from the request
/// context — an admin chooses only the name and the authority.
type CreateServiceAccountRequest = {
    [<PiiSafe>]
    DisplayName: string
    Permissions: Map<string, ModulePermission list>
}

/// Payload for `IServiceAccountApi.MintToken`.
type MintServiceAccountTokenRequest = {
    [<PiiSafe>]
    AccountId: string
    [<PiiSafe>]
    DisplayName: string
    /// `None` = `ServiceAccountTypes.DefaultTokenLifetime`.
    ExpiresAt: DateTimeOffset option
}

module ServiceAccountTokenView =
    /// Project a persisted token record onto the wire shape, dropping
    /// `Salt`, `SecretHash` and `ScopeSnapshot`. The snapshot is dropped
    /// not because it is sensitive but because it is a stale copy of the
    /// account's grant — showing it beside the account's live permissions
    /// invites an operator to read the wrong one.
    let ofRecord (token: ServiceAccountToken) : ServiceAccountTokenView = {
        TokenId = token.TokenId
        AccountId = token.AccountId
        DisplayName = token.DisplayName
        IssuedBy = token.IssuedBy
        IssuedAt = token.IssuedAt
        ExpiresAt = token.ExpiresAt
        Revoked = token.Revoked
    }

/// Admin-facing API for managing service accounts and their tokens.
/// Mounted only when `ServerConfig.ServiceAccounts` opts in.
///
/// **Scope isolation (GP 4).** Every method resolves the caller's scope
/// from `AccessContext` server-side and passes it to
/// `IServiceAccountStore`, which refuses an account belonging to another
/// scope. No method takes a scope parameter, so a caller cannot name one.
///
/// **Owner/Admin gated, and closed to machine callers.** Writes require
/// `TeamRoles.canWriteTeamConfig` in team scope; and EVERY method —
/// reads included — refuses a `ClaimBearer` subject. That second rule is
/// the one that matters: a service-account token resolves to
/// `ClaimBearer`, so without it a machine credential could mint further
/// credentials or widen its own account's permissions, and a scoped
/// credential that can rewrite its own scope is not scoped. The
/// `[<TenantScoped>]` attribute classifies the methods for the Phase 69d
/// dispatcher; the claim-bearer refusal is enforced in the handler,
/// because no shipped attribute expresses "authenticated human only".
type IServiceAccountApi = {
    /// Every service account in the caller's scope, active and disabled.
    [<TenantScoped>]
    ListAccounts: unit -> Async<Result<ServiceAccount list, string>>

    /// Create a machine principal in the caller's scope. Refuses an empty
    /// permission map — see `ServiceAccountTypes.validatePermissions`.
    [<TenantScoped>]
    [<Audit "Custom:ServiceAccountCreated">]
    CreateAccount: CreateServiceAccountRequest -> Async<Result<ServiceAccount, string>>

    /// Disable or re-enable an account. Disabling refuses every one of
    /// its tokens wholesale without revoking them.
    [<TenantScoped>]
    [<Audit "PolicyChanged">]
    SetAccountStatus: string * ServiceAccountStatus -> Async<Result<ServiceAccount, string>>

    /// Replace an account's declared permission set. Narrowing bites on
    /// every outstanding token at its next use.
    [<TenantScoped>]
    [<Audit "PermissionGranted">]
    SetAccountPermissions: string * Map<string, ModulePermission list> -> Async<Result<ServiceAccount, string>>

    /// Every token issued by one account — active, expired and revoked
    /// alike. Never carries a secret.
    [<TenantScoped>]
    ListTokens: string -> Async<Result<ServiceAccountTokenView list, string>>

    /// Mint a token. The response is the ONLY exposure of the secret
    /// that will ever exist.
    [<TenantScoped>]
    [<Audit "Custom:ServiceAccountTokenMinted">]
    MintToken: MintServiceAccountTokenRequest -> Async<Result<MintedServiceAccountTokenView, string>>

    /// Permanently revoke a token. Idempotent.
    [<TenantScoped>]
    [<Audit "Custom:ServiceAccountTokenRevoked">]
    RevokeToken: string -> Async<Result<unit, string>>
}

module ServiceAccountApi =
    /// Remoting endpoint prefix. Matches `WebhookApi.routeBuilder` and
    /// the rest of the platform's admin APIs.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"
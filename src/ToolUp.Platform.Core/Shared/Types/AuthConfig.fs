// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// JWS signature algorithms an OIDC-style auth provider can be configured
/// to accept. Identity-by-value (rule 1): the DU carries no live handles
/// and is safe to share across the framework-portable interfaces and the
/// JWT-parsing path. `tryParse` round-trips the canonical RFC 7518 string
/// representation a JWT header's `alg` field uses; unknown strings return
/// `None` and the validation pipeline maps them to `UnsupportedAlgorithm`.
///
/// `HS256` is deliberately absent — symmetric flows belong to
/// `StaticJwtAuthProvider`, whose secret never leaves the deployment;
/// an OIDC issuer publishing HS256 over JWKS would imply secret-sharing
/// with browser-side parties, which the OIDC trust model forbids. EdDSA /
/// Ed25519 are likewise omitted until customer demand surfaces.
type JwsAlgorithm =
    /// RSA-SHA256 with PKCS#1 v1.5 padding. The OIDC ecosystem's
    /// historical default; Entra / Azure AD / Auth0 / Okta all sign
    /// with RS256 unless explicitly reconfigured.
    | RS256
    /// RSA-SHA384 with PKCS#1 v1.5 padding. Same RSA key shape as
    /// RS256; differs only in hash strength.
    | RS384
    /// RSA-SHA512 with PKCS#1 v1.5 padding. Same RSA key shape as
    /// RS256 / RS384; rarely issued in practice but cheap to support
    /// alongside the others.
    | RS512
    /// ECDSA over the P-256 curve with SHA-256 hashing. AWS Cognito,
    /// Firebase Auth, and some Okta / dynamic-client OIDC flows issue
    /// ES256-signed tokens by default. JWS signature transport is the
    /// IEEE-P1363 fixed-field concatenation (r||s, 64 bytes for P-256),
    /// not DER-encoded.
    | ES256
    /// RSA-SHA256 with PSS (probabilistic) padding. Modern-RSA
    /// alternative to RS256 with the same key shape; some hardened
    /// IdPs default to PS256 to avoid PKCS#1 padding attacks against
    /// historically weak implementations.
    | PS256

module JwsAlgorithm =
    /// Render the canonical RFC 7518 `alg` string. Used in error
    /// messages (`UnsupportedAlgorithm`) and in the per-algorithm
    /// docs / migration tables.
    let toString =
        function
        | RS256 -> "RS256"
        | RS384 -> "RS384"
        | RS512 -> "RS512"
        | ES256 -> "ES256"
        | PS256 -> "PS256"

    /// Parse a JWT header `alg` value. Case-sensitive — RFC 7515
    /// requires the registered name verbatim. Unknown strings return
    /// `None` so the caller can decide whether to fall through to
    /// `UnsupportedAlgorithm` or some other error path.
    let tryParse (raw: string) : JwsAlgorithm option =
        match raw with
        | "RS256" -> Some RS256
        | "RS384" -> Some RS384
        | "RS512" -> Some RS512
        | "ES256" -> Some ES256
        | "PS256" -> Some PS256
        | _ -> None

/// How an `IAuthProvider` obtains the signing key used to verify JWT
/// signatures. Each provider consumes a subset — `StaticJwtAuthProvider`
/// uses `StaticSecret`; OIDC providers use `JwksDiscovery` (SaaS IdPs)
/// or `JwksExplicit` (self-hosted IdPs on non-standard paths).
type KeySource =
    /// HS256 shared secret, used by the token issuer and verifier.
    /// Appropriate for internal services that mint their own tokens;
    /// not suitable for browser-facing flows where the key would leak.
    | StaticSecret of key: string
    /// OIDC metadata discovery — fetch
    /// `{issuerUrl}/.well-known/openid-configuration`, read the
    /// `jwks_uri` field, then fetch the signing keys from there. The
    /// standard path for hosted OIDC providers (Clerk, Auth0,
    /// Azure AD, Okta, Google Identity).
    | JwksDiscovery of issuerUrl: string
    /// Direct JWKS URL. Skips the OIDC metadata lookup — useful when
    /// the JWKS is exposed on a non-standard path or a provider
    /// doesn't publish the metadata document.
    | JwksExplicit of jwksUrl: string

/// Where in the HTTP request a JWT will be found. `BearerHeader` is
/// the standard OIDC transport; `Cookie` and `CustomHeader` support
/// apps that mint tokens outside the OIDC flow or need HttpOnly
/// cookies for browser-side protection.
type TokenLocation =
    /// `Authorization: Bearer <token>`. Default for OIDC flows.
    | BearerHeader
    /// Token value is the named cookie's value. Used for HttpOnly
    /// browser sessions that never expose the token to JavaScript.
    | Cookie of name: string
    /// Token value is the named header's value. Used for non-standard
    /// protocols that expose tokens outside the bearer convention.
    | CustomHeader of name: string
    /// 0.5.3 — Check `Authorization: Bearer <token>` first, then fall
    /// back to the named cookie when the header is absent. Required for
    /// deployments that serve BOTH the standard REST auth path
    /// (Authorization-header-bearing XHR/fetch) AND SSE (EventSource,
    /// which the browser will only ever auth via cookie — the
    /// EventSource spec forbids custom request headers). The single-
    /// location cases above force a deployment to pick one transport
    /// or the other and silently break the unpicked path; this case
    /// admits both. Pairs with `ServerConfig.SseAuthMode = CookieRequired`
    /// and `UserSession.setAuthToken` on the client side, which already
    /// writes the JWT into both `localStorage` (for the Authorization
    /// header) and the matching cookie (for SSE).
    | BearerOrCookie of cookieName: string

/// Opt-in override of which JWT claims an OIDC-style provider projects
/// onto `AuthenticatedUser.UserId` / `TenantId`.
///
/// The generic OIDC provider's built-in mapping is `sub` -> `UserId`
/// with no `TenantId` projection. That is correct for most IdPs and
/// wrong for a family of them: some issuers mint a `sub` that is
/// PAIRWISE PSEUDONYMOUS (a different value per relying party / app
/// registration), so downstream artefacts keyed on it — admin lists,
/// RBAC entries, audit records — lose identity continuity the moment
/// the application is re-registered. Those IdPs publish a stable
/// identifier under a different claim name (`oid` on Microsoft Entra,
/// and various vendor-specific spellings elsewhere), and a tenant
/// identifier under another (`tid`). Naming them here covers the whole
/// family generically instead of one provider-specific decorator per
/// IdP.
///
/// **Every field defaults to "names nothing" (`ClaimMapping.none`).** A
/// mapping that names no claim and no admission rule is a no-op, and
/// `AuthConfig.ClaimMapping = None` skips the projection entirely — an
/// existing deployment is byte-for-byte unchanged until it opts in (GP 11).
///
/// Phase 987 adds the directory half: `RolesClaim` / `GroupsClaim` project
/// the IdP's role and group claims onto `AuthenticatedUser.DirectoryRoles`
/// (`GroupAliases` renames group ids), which gate pages only; Phase 993's
/// `ApiRoleGrants` is the explicit allow-list that also makes a mapped
/// role an API role. `AllowedTenants` / `RequiredRoles` form an optional
/// admission gate. Build from
/// `ClaimMapping.none` or `ClaimMapping.directoryRoles` with a copy-and-
/// update expression so a later field addition does not break the call
/// site.
///
/// **A named claim is REQUIRED, not preferred (fail-closed).** When a
/// claim is named here and the validated token does not carry it as a
/// non-empty string that survives `IdentitySanitiser.sanitiseScopeId`,
/// the request is REJECTED rather than falling back to `sub`. An
/// operator who names a claim has asserted that their IdP mints it; a
/// silent fallback would hand the deployment a *different* identity for
/// the same human — precisely the pairwise-`sub` continuity break the
/// mapping exists to avoid — and would do it invisibly, at the moment
/// the IdP's configuration drifted. Fail-closed turns that into a
/// diagnosable authentication failure naming the claim.
///
/// The projection is applied strictly AFTER the token's signature,
/// issuer, audience and expiry have been verified. It reads already-
/// trusted bytes; it is not a second validation path and it can never
/// admit a token the validator refused.
type ClaimMapping = {
    /// Claim name to project onto `AuthenticatedUser.UserId` in place of
    /// `sub`. `None` keeps the provider's built-in `sub` behaviour.
    /// Example: `Some "oid"` for a Microsoft Entra tenant.
    UserIdClaim: string option
    /// Claim name to project onto `AuthenticatedUser.TenantId`. `None`
    /// leaves `TenantId` exactly as the provider resolved it (which is
    /// `None` for the generic OIDC provider). Example: `Some "tid"`.
    TenantIdClaim: string option
    /// Phase 987 — claim whose values become directory roles
    /// (`AuthenticatedUser.DirectoryRoles`, and so `AccessContext.TokenRoles`,
    /// which `ScopeGated` pages read). Since Phase 993 a mapped role is an
    /// API role (`AuthenticatedUser.Roles`, read by `[<RequiresRole>]`) only
    /// when `ApiRoleGrants` names it. `None` maps no role claim. The conventional name is `roles` (Entra
    /// app roles, and most IdPs' role claim): `ClaimMapping.directoryRoles`
    /// names it. The claim may be a JSON string (one role) or an array of
    /// strings; an absent claim grants no role. Any other shape — a number,
    /// an object, an array holding a non-string or a blank entry — rejects
    /// the token (fail-closed), naming the claim.
    RolesClaim: string option
    /// Phase 987 — claim whose values (group ids or names) also become
    /// directory roles, after `GroupAliases` (and API roles only through
    /// `ApiRoleGrants`, as for `RolesClaim`). `None` maps no
    /// group claim; the conventional name is `groups`. Same shape rules as
    /// `RolesClaim`. When the IdP reports a group OVERAGE instead of the
    /// claim — `_claim_names` names this claim, which Microsoft Entra does
    /// when a user is in more groups than fit in a token — the token is
    /// rejected naming the claim: the SDK does not call the directory to
    /// resolve the groups, and treating the user as group-less would
    /// silently deny (or, under a negative rule, silently admit) them.
    GroupsClaim: string option
    /// Phase 987 — group id → role name. A value of `GroupsClaim` found
    /// here becomes the mapped name (so a page can say `scope:finance`
    /// rather than an opaque object id); a value not found is kept as it
    /// is. Empty by default.
    GroupAliases: Map<string, string>
    /// Phase 987 — admission gate: when non-empty, a token is admitted
    /// only if its mapped `TenantId` is one of these values. Requires
    /// `TenantIdClaim` (a token with no mapped tenant is refused). Empty
    /// admits every tenant the issuer and audience checks admit.
    AllowedTenants: string list
    /// Phase 987 — admission gate: when non-empty, a token is admitted
    /// only if it carries at least one of these roles after role and group
    /// mapping (so an aliased group name may be named here). Empty admits
    /// every principal. A refused token is an authentication failure
    /// (401), not a 403: the deployment does not accept the principal at
    /// all.
    RequiredRoles: string list
    /// Phase 993 — the ONLY route from a directory role to an API role.
    /// A mapped role or group (after `GroupAliases`, so a group that has an
    /// alias is matched by its alias, never by its raw id) that is named
    /// here is ALSO added to `AuthenticatedUser.Roles`, where
    /// `[<RequiresRole>]` gates read it. Empty by default: role mapping
    /// then grants page audiences only, and whoever administers the
    /// directory cannot grant API privilege by naming a role or group.
    /// Each entry is an exact role name; `ClaimMapping.validateApiRoleGrants`
    /// refuses a blank or padded entry and `PlatformAdmin` (the platform-
    /// admin grant is resolved server-side from `IPlatformAdminStore`, never
    /// from a token), and the OIDC provider refuses to build over a refused
    /// set.
    ApiRoleGrants: Set<string>
}

module ClaimMapping =
    /// A mapping that names no claim — behaviourally identical to
    /// `AuthConfig.ClaimMapping = None`. Useful as a builder start point.
    let none: ClaimMapping = {
        UserIdClaim = None
        TenantIdClaim = None
        RolesClaim = None
        GroupsClaim = None
        GroupAliases = Map.empty
        AllowedTenants = []
        RequiredRoles = []
        ApiRoleGrants = Set.empty
    }

    /// Phase 987 — the conventional directory-role mapping: `roles` and
    /// `groups` become directory roles (`AuthenticatedUser.DirectoryRoles`,
    /// which gate pages); none is an API role until `ApiRoleGrants` names
    /// it (Phase 993). Identity claims are left
    /// as the provider resolves them; combine with `UserIdClaim` /
    /// `TenantIdClaim` as needed (`{ ClaimMapping.directoryRoles with
    /// UserIdClaim = Some "oid" }`).
    let directoryRoles: ClaimMapping = {
        none with
            RolesClaim = Some "roles"
            GroupsClaim = Some "groups"
    }

    /// `true` when the mapping names no claim at all, so applying it is a
    /// no-op. Providers short-circuit on this so an explicitly-supplied
    /// empty mapping costs nothing (GP 13).
    let isEmpty (mapping: ClaimMapping) : bool =
        mapping.UserIdClaim.IsNone
        && mapping.TenantIdClaim.IsNone
        && mapping.RolesClaim.IsNone
        && mapping.GroupsClaim.IsNone
        && mapping.AllowedTenants.IsEmpty
        && mapping.RequiredRoles.IsEmpty

    /// Phase 993 — the role names an `ApiRoleGrants` set may not carry, and
    /// why: `PlatformAdmin` is resolved server-side (`IPlatformAdminStore`),
    /// so naming it here could never take effect and would read as a grant.
    let reservedApiRoleGrants: Map<string, string> =
        Map["PlatformAdmin",
            "the platform-admin grant is resolved server-side from IPlatformAdminStore and is never taken from a token"]

    /// Phase 993 — `Ok ()` when every `ApiRoleGrants` entry is an exact,
    /// non-blank role name with no whitespace or control character and is
    /// not reserved; `Error reason` naming the first offending entry
    /// otherwise. The OIDC provider calls this when it is BUILT, so a
    /// malformed allow-list fails the deployment at startup rather than
    /// silently granting nothing (or something else).
    let validateApiRoleGrants (grants: Set<string>) : Result<unit, string> =
        let malformed (role: string) =
            System.String.IsNullOrWhiteSpace role
            || role
               |> Seq.exists (fun c -> System.Char.IsWhiteSpace c || System.Char.IsControl c)

        let refusal (role: string) =
            if malformed role then
                Some
                    $"ApiRoleGrants entry '{role}' is not a role name (blank, or carries whitespace or a control character)"
            else
                reservedApiRoleGrants
                |> Map.tryFind role
                |> Option.map (fun why -> $"ApiRoleGrants may not name '{role}': {why}")

        match grants |> Seq.tryPick refusal with
        | None -> Ok()
        | Some reason -> Error reason

/// Declarative configuration for an `IAuthProvider`. Providers read
/// the fields they care about and ignore the rest — e.g.
/// `StaticJwtAuthProvider` expects `KeySource = StaticSecret _`;
/// OIDC providers expect `JwksDiscovery` or `JwksExplicit`.
///
/// `Issuer` and `Audience` are claim-validation inputs when `Some`.
/// Leaving either `None` skips the corresponding check — acceptable
/// during development but SHOULD NOT be left unset in production OIDC
/// deployments where issuer and audience restrict the token's
/// intended recipient.
type AuthConfig = {
    /// Expected `iss` claim on inbound tokens; `None` disables the
    /// check. SaaS IdPs always populate `iss` with the issuer URL.
    Issuer: string option
    /// Expected `aud` claim on inbound tokens; `None` disables the
    /// check. Set per-tenant when the IdP issues audience-restricted
    /// tokens (i.e. almost always, in production).
    Audience: string option
    /// Where the verifier finds the signing key for inbound tokens.
    KeySource: KeySource
    /// Where the verifier extracts the JWT from the HTTP request.
    TokenLocation: TokenLocation
    /// Clock-skew tolerance (seconds) applied to `exp` and `nbf`
    /// claim validation. `None` -> SDK default (60s — standard
    /// practice, covers small NTP drift between deployment host and
    /// IdP). Cross-region setups with tighter / looser drift can
    /// override; raise cautiously since wider tolerance widens the
    /// replay window for stolen tokens.
    ClockSkewSeconds: int64 option
    /// JWS signature algorithms the deployment is willing to trust.
    /// `None` -> SDK default `[RS256]`, preserving today's behaviour
    /// for every existing consumer. Operators wanting to interoperate
    /// with IdPs that issue ES256 / RS384 / RS512 / PS256 (AWS
    /// Cognito, Firebase Auth, some Okta / dynamic-client OIDC flows,
    /// hardened RSA deployments) opt in explicitly via
    /// `Some [ RS256; ES256 ]` or similar. An inbound token whose
    /// header `alg` is not in this list is rejected with
    /// `UnsupportedAlgorithm` even if its signature would otherwise
    /// verify — the trust set is operator-owned, not auto-widened by
    /// the SDK. `HS256` is intentionally not a member: symmetric
    /// flows belong to `StaticJwtAuthProvider`.
    AcceptedAlgorithms: JwsAlgorithm list option
    /// 0.5.4 — When `Some true`, the OIDC provider's `userFromPayload`
    /// prefers the JWT `oid` claim over `sub` when constructing
    /// `AuthenticatedUser.UserId`. `oid` is Microsoft Entra's tenant-
    /// wide stable user identifier; `sub` is a pairwise pseudonymous
    /// identifier specific to the relying party (different per app
    /// registration). Bootstrapping platform admins, RBAC entries,
    /// and audit identity by the tenant-stable `oid` is operationally
    /// correct for Entra deployments — the operator looks up the
    /// admin's `oid` in the Entra portal once and the value stays
    /// stable across app registrations / token refreshes.
    ///
    /// `None` or `Some false` (default) preserves the provider's pre-
    /// 0.5.4 behaviour (`sub` only) — the SDK never silently switches
    /// identity sources for an existing deployment (GP 11).
    ///
    /// `AuthProvider.fromEnv` auto-enables this flag when the
    /// configured `Issuer` matches `https://login.microsoftonline.com/`
    /// — any other issuer leaves the flag `None`. Consumers wiring
    /// `AuthConfig` directly can set it explicitly.
    ///
    /// When the flag is on but the inbound token has no `oid` claim,
    /// the provider falls back to `sub` — never breaks the request
    /// path on a missing optional claim.
    PreferOidWhenPresent: bool option
    /// Opt-in override of which claims become `AuthenticatedUser.UserId`
    /// / `TenantId`, applied post-validation. `None` (the default) keeps
    /// the provider's built-in `sub` -> `UserId` behaviour with no
    /// `TenantId` projection, byte-for-byte (GP 11).
    ///
    /// Distinct from `PreferOidWhenPresent`, which is a single-IdP
    /// convenience with fallback semantics: it PREFERS `oid` and falls
    /// back to `sub` when the claim is absent. `ClaimMapping` is the
    /// generic form and is fail-closed — a named claim the token does
    /// not carry rejects the request. Set one or the other; when both
    /// are set `ClaimMapping.UserIdClaim` wins, because it is the
    /// explicit operator instruction and the stricter of the two.
    ClaimMapping: ClaimMapping option
}

/// Phase 987 — where an audience-gated SSR page sends a browser that
/// arrived with no credential, instead of answering a bare `401`.
///
/// Registered in DI (a singleton) by whatever mounts the sign-in routes —
/// `OidcSsrSignIn.register` for the OIDC provider. When it is registered,
/// a page whose audience requires a principal answers a credential-less
/// browser navigation (`GET` / `HEAD` accepting `text/html`) with a `302`
/// to `SignInPath?returnUrl=<the page's path and query>`; every other
/// request, and every deployment that registers nothing, keeps the `401`
/// (GP 11). A principal that is signed in but refused is always `403` and
/// never redirected, so a sign-in that lands an unauthorised principal
/// cannot loop.
type InteractiveSignIn = {
    /// Local path of the route that starts the sign-in, e.g.
    /// `/auth/sign-in`. It receives the return URL as the `returnUrl`
    /// query parameter (`InteractiveSignIn.ReturnUrlParameter`).
    SignInPath: string
}

module InteractiveSignIn =
    /// Query parameter carrying the local URL to return to after sign-in.
    [<Literal>]
    let ReturnUrlParameter = "returnUrl"

    /// Whether `url` is safe to redirect to after sign-in: a local,
    /// rooted path (`/x`), never scheme- or authority-relative (`//host`,
    /// `/\host`) and free of control characters — the open-redirect
    /// guard every consumer of a return URL applies.
    let isLocalReturnUrl (url: string) : bool =
        not (System.String.IsNullOrEmpty url)
        && url.StartsWith "/"
        && not (url.StartsWith "//")
        && not (url.StartsWith "/\\")
        && not (url |> Seq.exists (fun c -> c < ' ' || c = char 127))

    /// The redirect target for a page at `returnUrl`: `SignInPath` with
    /// the return URL appended as `returnUrl`.
    let redirectFor (signIn: InteractiveSignIn) (returnUrl: string) : string =
        let sep = if signIn.SignInPath.Contains "?" then "&" else "?"
        $"{signIn.SignInPath}{sep}{ReturnUrlParameter}={System.Uri.EscapeDataString returnUrl}"
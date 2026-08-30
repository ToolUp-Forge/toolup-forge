# Migration — OIDC bearer-token strategy (Phase 746)

**What changes.** A deployment can now declare **which token the OIDC session sends as its HTTP bearer** — the `access_token` (the OAuth-conventional choice, and the default) or the `id_token`. The declaration is a new `BearerToken: BearerTokenKind option` field on both `OidcAppConfig` (the one-declaration consumer surface) and `OidcUIConfig` (the client-tier projection).

This exists for identity providers whose access tokens are **opaque** — random strings carrying no claims. Such a provider signs a user in successfully and then 401s every subsequent API call, because `OidcAuthProvider`'s bearer path validates a JWT against the issuer's JWKS and an opaque string has nothing to validate. Google is the canonical case: its access tokens are always opaque, and unlike Auth0 there is no dashboard audience knob that makes them decodable.

**The server side is unchanged.** An `id_token` is an ordinary RS256 JWT, signed by the same key set, with `iss` = the issuer and `aud` = the client id — which is what `AuthConfig.Audience` already holds for every preset. The provider was always able to validate one; what was missing was any way for the client to say "send *that* one".

**Default behaviour is unchanged (GP 11).** `BearerToken = None` resolves to `AccessTokenBearer` for every hand-built config and every preset except `google`. An existing deployment that upgrades and changes nothing is byte-for-byte identical.

## Public surface

| Surface | Change |
|---|---|
| `ToolUp.Platform.BearerTokenKind` | **New** DU: `AccessTokenBearer` \| `IdTokenBearer`, with `BearerTokenKind.label`. |
| `ToolUp.Platform.OidcUIConfig` | **New field** `BearerToken: BearerTokenKind option`. |
| `ToolUp.Platform.OidcUIConfig.resolveBearerToken` | **New** — `None` resolves to `AccessTokenBearer`. |
| `OidcAppConfig` | **New field** `BearerToken: BearerTokenKind option`. |
| `OidcAppConfig.resolveBearerToken` | **New** — explicit consumer choice > preset default > `AccessTokenBearer`. |
| `OidcAppConfig.toClientConfig` | Now projects a **resolved** value into `OidcUIConfig.BearerToken`. |
| `PresetKind.defaultBearerToken` | **New** — `IdTokenBearer` for `Google`, `AccessTokenBearer` for every other preset. |
| `PresetKind.opaqueAccessTokenIsUnfixable` | **New** — `true` for `Google` alone. |
| `OidcStateMachine.BearerInputs` / `decideBearerToken` | **New** pure decision, shared by the callback and refresh paths. |
| `OidcCoherenceValidator` | **Two new rules** — 14 (Error) and 15 (Warning); rule 11's provenance line gained a `bearer:` field. |

## Consumer action required

**Record-literal constructions of `OidcUIConfig` or `OidcAppConfig` must add the new field** — the compiler flags the missing field as a record-completeness error (`FS0764`). Set it to `None` for byte-for-byte backward compatibility.

```fsharp skip=fragment
let cfg: OidcUIConfig = {
    Issuer = "https://auth.example.com"
    ClientId = "<client id>"
    RedirectUri = "https://app.example.com/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = None
    ValidateIdToken = Some true
    BearerToken = None          // ← new; None = today's behaviour
}
```

Consumers building via `OidcUIConfig.defaults`, `OidcAppConfig.create`, an `OidcPresets.*` smart constructor, or a companion projection (`EntraExternalIdClientConfig.toOidcUIConfig`, `GoogleIdentityConfig.toOidcUIConfig`) need **no change** — every one of those already supplies the field.

## Adopting the id_token strategy

Google-preset consumers get it automatically:

```fsharp skip=fragment
let googleCfg = OidcPresets.google clientId redirectUri
// googleCfg.BearerToken = None, and OidcAppConfig.resolveBearerToken
// googleCfg = IdTokenBearer, because PresetKind.defaultBearerToken Google
// says so. Nothing else to wire.
```

For an IdP with no preset whose access tokens are opaque, state it:

```fsharp skip=fragment
let cfg = {
    OidcAppConfig.create issuer clientId redirectUri with
        BearerToken = Some IdTokenBearer
}
```

**Two preconditions**, both checked at preflight by `OidcCoherenceValidator`:

1. `Audience` must be `ClientId`, because an id_token's `aud` is always the client id. A mismatch is a **rule-14 Error** that refuses startup rather than surfacing at the first API call.
2. The issuer must reissue an `id_token` on the `refresh_token` grant. Issuers do when the `openid` scope is in play. If yours does not, the refresh fails with a typed `TokenExchangeFailed` and the session drops to sign-in — the SDK deliberately does not fall back to the access token, which would silently swap the bearer to a token class the server cannot validate.

## Verification

1. Compile: `dotnet build` — any missing-field error names the literal to update.
2. Preflight: start the app and read the `oidc-coherence` validator line. It now reports the resolved bearer (`bearer: id-token` / `bearer: access-token`) alongside the preset's other applied quirks.
3. Operationally: complete a sign-in and make one authenticated API call. Under the previous behaviour against an opaque-access-token provider, step 2 returned 401; it now succeeds. Then wait out (or shorten) the token lifetime and confirm the pre-expiry refresh renews the session without a re-sign-in.

## Rollback

Set `BearerToken = Some AccessTokenBearer` (or, on a non-`google` preset, back to `None`). The callback and refresh paths take the historical branch; no on-disk state is migrated. Any bearer already in `localStorage` is replaced at the next sign-in — a session holding an id_token when the strategy flips back will 401 once and re-authenticate, which the shell's existing stale-token handling covers.

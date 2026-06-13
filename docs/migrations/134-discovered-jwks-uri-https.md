# Migration — Phase 134: https-check the OIDC discovered `jwks_uri`

**Status:** behavioural hardening, no API change, no source-level break. A deployment whose IdP advertises an `https://` (or loopback `http://`) `jwks_uri` — i.e. every correctly-configured OIDC provider — is byte-for-byte unchanged. Only a provider that advertises a non-loopback `http://` `jwks_uri` (a misconfiguration or a downgrade attempt) is now refused instead of silently trusted.

## What changes

`OidcAuthProvider` already refused a non-https *configured* issuer / JWKS URL at construction (loopback-exempt). For `JwksDiscovery`, the `jwks_uri` is read from the issuer's `.well-known/openid-configuration` metadata document at runtime and was previously fetched verbatim with no scheme check — so a metadata document specifying an `http://` `jwks_uri` downgraded the signing-key fetch to cleartext, where a network attacker could substitute the key set and have forged tokens validate.

The same loopback-exempt `requireHttps` predicate is now applied to:

1. the **discovered `jwks_uri`** (the `JwksDiscovery` arm of `resolveJwksUrl`), before the key fetch; and
2. the **`JwksExplicit` URL at fetch time** (it was construction-checked already — the fetch-time check makes the guarantee local to the fetch and immune to any future construction-bypass).

A non-https, non-loopback `jwks_uri` yields `Error (JwksUnavailable …)` naming the downgrade risk; the keys are never fetched over cleartext. Loopback `http://` is still permitted for a local mock IdP.

## Consumer action

None for any deployment whose IdP serves an `https://` `jwks_uri` (all hosted IdPs — Clerk, Auth0, Azure AD, Okta, Google Identity — do). If a deployment relied on an `http://` non-loopback `jwks_uri`, switch the IdP to https (the only safe configuration).

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — the discovered-`jwks_uri` cases in `AuthProviderTests.fs` pass: an `http://…/jwks` discovery document is rejected before any key fetch; an `https://` `jwks_uri` proceeds; a loopback `http://127.0.0.1/jwks` is allowed.

## Rollback

Remove the scheme guard from the `JwksDiscovery` arm of `resolveJwksUrl` and the `JwksExplicit` fetch-time path. The discovered `jwks_uri` returns to being fetched verbatim regardless of scheme. No persisted state involved.

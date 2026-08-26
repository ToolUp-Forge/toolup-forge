# ToolUp.AuthProviders.Oidc

Generic OIDC server-side `IAuthProvider` for `ToolUp.Platform`. Discovers JWKS via `.well-known/openid-configuration`, validates RS256 JWT bearer tokens against the discovered keys, and projects the resolved identity into `AuthenticatedUser`. Provider-agnostic — works against any OIDC-compliant issuer (Auth0, Cognito, Keycloak, etc.).

## Key revocation window — read before deploying

By default this provider caches JWKS keys for **10 minutes** and OIDC discovery
metadata for 24 hours. So:

> **A signing key the issuer has revoked keeps validating tokens on a given
> instance for up to 10 minutes while the issuer is reachable, and for as long
> as JWKS fetches keep failing.** Once a refresh is failing, the provider
> prefers serving the last-known-good key set over failing every sign-in — the
> window is then bounded by provider availability, not by the TTL.

That is a deliberate availability-over-revocation default, matching mainstream
OIDC libraries: an issuer blip should not take your application down. If your
threat model includes signing-key compromise, three opt-in knobs on
`OidcHardening` change it, and every one defaults to the behaviour above:

- **`JwksCacheTtl`** — shorten the ordinary window. `Some TimeSpan.Zero`
  disables the JWKS cache entirely: every validation re-fetches and nothing is
  served from cache, stale fallback included. That is the tightest window this
  provider offers, at one round-trip per validated request.
- **`FailClosedOnStaleJwks`** — bound the *outage* window: a failing refresh
  surfaces the error rather than serving possibly-revoked keys. Sign-in fails
  while the IdP is unreachable; that is the trade.
- **`JwksEvictionSignal`** — bound the window *across instances*. A fetch
  failure publishes a `CustomNotification` on the platform-reserved scope;
  siblings subscribed via `OidcJwksCache.subscribeToEvictions` evict their own
  entry for that URL. Without it, a fleet's window is each instance's TTL
  measured independently. Requires a distributed notification channel — the
  in-process default reaches only the publishing process.

**Not covered by any of them: per-token revocation.** This provider does not
perform token introspection (RFC 7662); revocation is observed only through the
key set, so a revoked *grant* whose signing key is still published keeps
validating until the token's `exp`. Keep access-token lifetimes short.

Full guidance, including the wiring snippet for the cross-instance signal, is in
the SDK's auth-providers companion documentation.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

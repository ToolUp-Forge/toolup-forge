# ToolUp.AuthProviders.Oidc

Generic OIDC server-side `IAuthProvider` for `ToolUp.Platform`. Discovers JWKS via `.well-known/openid-configuration`, validates RS256 JWT bearer tokens against the discovered keys, and projects the resolved identity into `AuthenticatedUser`. Provider-agnostic — works against any OIDC-compliant issuer (Auth0, Cognito, Keycloak, etc.).

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

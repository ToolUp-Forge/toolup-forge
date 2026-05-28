// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Phase 6k Workstream A. Provider-agnostic bridge between a
/// client-side identity SDK (Clerk, Microsoft Entra via MSAL, Auth0,
/// WorkOS, etc.) and `UserSession`. The deployment composes one
/// implementation in `Client.fs`; the SDK reads from it on every
/// request via `withRequestHeaders` and on the SSE handshake via
/// the cookie write inside `setAuthToken`.
///
/// Why this exists: each provider's client SDK has a different shape
/// for "give me a fresh JWT" — Clerk's `clerk.session.getToken()`,
/// MSAL's `acquireTokenSilent`, Auth0's `getAccessTokenSilently`. The
/// SDK needs to call one of them on every request without knowing
/// which provider is wired up. `IAuthBridge` is the abstraction.
///
/// ## Server-side counterpart
///
/// On the server, all of these providers reduce to the same shape:
/// a JWT validated against a JWKS endpoint. `OidcAuthProvider` already
/// handles this — the deployment configures the issuer URL per-provider
/// (e.g., Clerk's `clerk.accounts.dev/<instance>`, Microsoft's
/// `login.microsoftonline.com/<tenant>/v2.0`). No provider-specific
/// server code needed.
///
/// ## Threading
///
/// `GetJwt` is called from the periodic refresh loop installed by
/// `UserSession.installBridge`. Implementations should cache the JWT
/// briefly (typical client SDKs do this internally) and return it
/// without a network round-trip when possible. A `None` return means
/// the user is signed out — the SDK clears the local cache + cookie
/// to match.
///
/// ## Example deployment composition
///
/// ```fsharp
/// // src/ToolUpApp-Client/Client.fs
/// let clerkBridge = ClerkAuthBridge.create publishableKey
/// let microsoftBridge = MicrosoftAuthBridge.create { TenantId = "..."; ClientId = "..." }
///
/// // Pick one per deployment:
/// { ClientConfig.defaults with AuthBridge = Some clerkBridge }
/// ```
type IAuthBridge =
    /// Return a fresh access token, refreshing transparently if the
    /// provider SDK supports silent refresh. `None` when the user is
    /// not signed in.
    abstract GetJwt: unit -> Async<string option>

    /// Stable user identifier from the provider — surfaced for
    /// diagnostic purposes (e.g. logging in the chat side panel). The
    /// canonical server-side identity comes from JWT validation; this
    /// is a best-effort label for "who does the client think it is?"
    abstract GetUserId: unit -> string option

    /// Provider name (e.g. `"clerk"`, `"microsoft"`, `"auth0"`).
    /// Surfaced in the audit trail's `AuthProvider` field, the
    /// `/dev/inspect` panel, and trace logs under category `"auth"`.
    abstract ProviderName: string
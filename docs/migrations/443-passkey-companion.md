# Phase 443 — WebAuthn / passkey auth companion

**What changes:** a new opt-in auth companion pair — `ToolUp.AuthProviders.Passkey`
(server) and `ToolUp.AuthProviders.Passkey.Client` (Fable client) — gives a self-hosted
deployment with **no external IdP** phishing-resistant, passwordless sign-in over WebAuthn.
The platform never stores a password hash. Session issuance mints the **same short-lived
HS256 platform JWT** `StaticJwtAuthProvider` validates — no parallel session model.

**Dependency:** [Fido2NetLib](https://github.com/passwordless-lib/fido2-net-lib) (`Fido2`
4.0.1, MIT — GP 2), isolated to the server companion (GP 1). Core carries no Fido2NetLib
reference; a deployment that never composes the companion pays nothing (GP 13).

---

## Server wiring

Compose over a base `ServerApp` with `PasskeyCompose`:

```fsharp
open ToolUp.AuthProviders.Passkey

let config =
    { PasskeyConfig.create "example.com" "Example App" [ "https://app.example.com" ] with
        // Invite-gated by default. For a fresh deployment's FIRST credential,
        // supply a one-time bootstrap token (typically from an env var):
        BootstrapToken = Some (System.Environment.GetEnvironmentVariable "PASSKEY_BOOTSTRAP_TOKEN" |> Option.ofObj |> Option.defaultValue "")
        // Open registration is an EXPLICIT opt-in (secure by default):
        // AllowOpenRegistration = true
    }

// PasskeyCompose sets the app IAuthProvider (validates the minted session JWTs),
// registers the preflight validator + the PasskeyRuntime DI singleton, and mounts
// the ceremony routes onto the SDK route chain.
serverApp
|> PasskeyCompose.create config
|> PasskeyCompose.run
```

`PasskeyCompose.run` folds four things into the base app in one pass:

1. `ServerApp.withAuth (PasskeyAuthProvider config)` — the request-time validator. It IS a
   `StaticJwtAuthProvider` bound to the auto-generated session signing secret (resolved
   lazily from `ISecretStore` under `_platform/passkey_session_signing_key`). Reports
   `IsCryptographicallyVerified = true`, so it satisfies the startup auth-mode gate.
2. `ServerApp.withConfigValidator (PasskeyConfigValidator config)` — a **security-class**
   preflight (runs even under `SkipPreflight`): relying-party id must be a registrable
   suffix of every origin, and every origin must be `https://` (loopback exempt).
   `AllowOpenRegistration = true` surfaces as a startup **warning**.
3. The ceremony routes: `POST /api/passkey/{register,assert}/{begin,complete}`.
4. The `PasskeyRuntime` DI singleton (Fido2 verifier + blob-backed credential store +
   in-memory challenge store), built over the resolved `IBlobStorage`.

**Required substrate:** `IBlobStorage` (credential records under
`_platform/auth/passkeys/`), `ISecretStore` (session signing secret). Optional:
`IPendingInviteStore` (invite-gated registration by email), `IAuditLog` (ceremony audit).

**Registration policy (443.B).** Secure by default — a registration ceremony is permitted
only when one of: an existing authenticated session drives the identity; a valid one-time
bootstrap token is presented (fresh-deployment bootstrap); or a pending team invite exists
for the supplied email. `AllowOpenRegistration = true` bypasses the gate (explicit opt-in,
preflight-surfaced).

**Sign-in mints a session.** A successful assertion (and a successful registration, which
implies authentication of that ceremony) mints the short-lived platform JWT and returns it
as `{ Token; ExpiresInSeconds; UserId }`. A `UserLoggedIn` audit event (AuthProvider
`"Passkey"`) is emitted on each; registration also emits `PasskeyCredentialRegistered`.

## Client wiring

Add the companion's exported handler and select the mode:

```fsharp
open ToolUp.AuthProviders   // PasskeyRegister

{ ClientConfig.defaults with
    AuthUI = PasskeyAuthUI PasskeyUIConfig.defaults
    Handlers =
        { ClientHandlerRegistry.empty with
            AuthUIHandlers = [ PasskeyRegister.handler ] } }
```

The client companion drives `navigator.credentials.create/get` via Fable bindings — **zero
npm dependencies** (browser-native WebAuthn + `fetch`, mirroring the OidcClient precedent).
It posts to `{ApiBase}/register/*` and `{ApiBase}/assert/*` (default `ApiBase = /api/passkey`).

## The `AuthUI` DU addition (consumer-visible)

`AuthUIMode` (`ToolUp.Platform.SDK.ClientTypes`, the type behind `ClientConfig.AuthUI`)
gains one **additive** case:

```fsharp
| PasskeyAuthUI of PasskeyUIConfig
```

WebAuthn is a **protocol, not a vendor**, so — exactly like `OidcAuthUI` — this is a
first-class named case rather than a `ProviderAuthUI (tag, obj)` form. This is the single
consumer-visible surface change (GP 11 — additive within the pre-1.0 window).

**Exhaustive-match compile prompt.** A consumer that pattern-matches `AuthUIMode`
exhaustively (no wildcard) will get an FS0025 incomplete-match warning after upgrading —
add a `| PasskeyAuthUI _ -> …` arm (or a `| _ ->` wildcard). No runtime behaviour changes
for a deployment that does not select the new case; `AuthUI` defaults to `NoAuthUI`.

## Bootstrap flow (fresh deployment, no IdP, no users)

1. Set an env var, e.g. `PASSKEY_BOOTSTRAP_TOKEN`, and wire it into
   `PasskeyConfig.BootstrapToken`.
2. Open the app; the sign-in screen offers **Register a passkey** with a bootstrap-token
   field. Enter a username + the bootstrap token, complete the browser's passkey prompt.
3. The first credential is enrolled and the browser is signed in. Rotate/clear the env var
   afterwards — the bootstrap path is a one-time convenience, not a standing credential.
4. Subsequent users register via an existing admin session or a pending team invite (or,
   if `AllowOpenRegistration = true`, openly).

## Notes / follow-ups

- **Clone detection.** The signature counter is persisted per credential and a
  non-advancing counter on assertion is rejected (Fido2NetLib enforces it; the companion
  re-checks explicitly).
- **Challenge store.** The default `InMemoryPasskeyChallengeStore` is single-instance
  (dev / sticky-session); the `IPasskeyChallengeStore` seam accepts a shared (e.g. Redis)
  implementation for multi-instance deployments.
- **Live verification.** The server ceremony + orchestration are unit-tested through a stub
  `IFido2` (round-trip, counter regression, gating, expiry) and the session-token round-trip
  is validated against `StaticJwtAuthProvider`; Fido2NetLib's own suite covers the WebAuthn
  crypto vectors. A full browser end-to-end (real authenticator) remains an operator
  acceptance step.

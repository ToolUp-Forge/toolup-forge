# ToolUp.AuthProviders.Passkey

WebAuthn / passkey server-side `IAuthProvider` companion for `ToolUp.Platform`. Phishing-resistant, passwordless sign-in for self-hosted deployments that have **no external identity provider** — the platform never stores a password hash. Built over [Fido2NetLib](https://github.com/passwordless-lib/fido2-net-lib) (MIT), isolated to this companion so `ToolUp.Platform.*` carries no `Fido2` dependency (GP 1).

Every other shipped auth companion (OIDC, Clerk, Entra) delegates identity to a hosted IdP; a standalone deployment otherwise has only `StaticJwtAuthProvider`. This companion fills that gap: it runs the WebAuthn registration (attestation) and sign-in (assertion) ceremonies, and on success mints the **same** short-lived HS256 platform session JWT that `StaticJwtAuthProvider` validates — no parallel session model, no second token shape.

## What it implements

- **`IAuthProvider`** — `PasskeyAuthProvider` validates the minted session JWT on every request (issuer / audience / `exp`), resolving the request principal exactly as the static-JWT provider does.
- **`IConfigValidator`** (security-class) — `PasskeyConfigValidator` runs a startup preflight that fails loudly on a relying-party-id / origin mismatch or a cleartext origin, rather than letting the first sign-up silently fail at the browser. Being security-class, it runs even under `ServerConfig.SkipPreflight`.
- **Four ceremony routes** mounted onto the SDK route chain (see [Routes](#routes)).

Substrate dependencies arrive through composition, never read directly (companion rule): credential records persist through the resolved `IBlobStorage` (container `_platform`, blob prefix `auth/passkeys/`); the HS256 session signing secret is resolved per use from `ISecretStore` (key `passkey_session_signing_key`, auto-generated on first use, mirroring `ShareTokenStore`); ceremony outcomes emit through `IAuditLog` when one is composed; pending team invites are honoured through `IPendingInviteStore` when present.

## How a consumer composes it

Wrap a base `ServerApp` with `PasskeyServerApp` (the `PasskeyCompose` companion root, mirroring `PeerServerApp`) and run it. At `run`, the companion folds itself into the base app in one pass: it sets the app `IAuthProvider`, registers the preflight validator, mounts the ceremony routes, and registers the `PasskeyRuntime` DI singleton (the Fido2 verifier plus the credential / challenge stores) over the resolved `IBlobStorage`.

```fsharp skip=fragment
open ToolUp.Platform.Server
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyCompose

let config =
    // Minimal secure defaults: invite-gated registration, https enforced,
    // 120s challenge TTL, 15-minute session. Set RP id / name / origins.
    PasskeyConfig.create "example.com" "Example App" [ "https://app.example.com" ]

[<EntryPoint>]
let main _ =
    ServerApp.empty
    |> ServerApp.withConfig { ServerConfig.defaults with Port = 5000 }
    |> ServerApp.withStorage (LocalFileStorage "data")   // any IBlobStorage
    |> PasskeyCompose.create config
    |> PasskeyCompose.run
```

The relying-party id (`RelyingPartyId`) is the WebAuthn scope of the credential — a **registrable domain suffix** of every browser origin the app is served from, with no scheme or port (`example.com`, not `https://example.com:5000`). It covers subdomains: RP id `example.com` scopes credentials for both `https://example.com` and `https://app.example.com`. `Origins` is the exact allow-list the verifier checks the browser's `clientDataJSON.origin` against.

Core takes no `Fido2` dependency and a deployment that never composes `PasskeyServerApp` pays nothing (GP 13).

## Configuration contract

`PasskeyConfig` is a declarative record (`PasskeyConfig.create` sets the secure defaults; override fields as needed):

| Field | Meaning | Default |
|---|---|---|
| `RelyingPartyId` | Registrable domain suffix of every origin — the credential scope. | *(required)* |
| `RelyingPartyName` | Human-readable name shown by the authenticator UI. | *(required)* |
| `Origins` | Exact browser origins permitted to run the ceremony. | *(required)* |
| `ChallengeTtlSeconds` | Begin-ceremony challenge lifetime; single-use + expiring (replay defence). | `120` |
| `SessionTokenTtlSeconds` | Lifetime of the minted platform session JWT. | `900` |
| `Issuer` / `Audience` | Optional `iss` / `aud` bound onto the session JWT and required by the paired validator. `None` skips the binding (matches `StaticJwtConfig`). | `None` |
| `AllowOpenRegistration` | `false` = invite-gated (an existing session, a pending invite, or the bootstrap token is required to enrol). `true` = anyone may enrol — an explicit opt-in the preflight surfaces as a warning. | `false` |
| `BootstrapToken` | One-time token (typically from an env var) that lets a fresh deployment enrol its **first** credential before any session or invite exists. Compared in constant time; `None` disables the path. | `None` |
| `EnforceHttps` | Require every origin to be `https://` (loopback exempt for local dev). Relax only behind a terminating TLS proxy. | `true` |

**Registration is secure by default.** With `AllowOpenRegistration = false`, `register/begin` admits a caller only when an authenticated session already drives the identity, a pending invite matches the supplied email, or the one-time `BootstrapToken` is presented — closing the "anyone can enrol" hole a naïve passkey flow leaves open. The bootstrap token is the intended way to enrol the first administrator on a brand-new deployment; source it from an environment variable your host reads at startup and pass it into `BootstrapToken`.

## Session signing key

The HS256 secret that signs (and validates) the platform session JWT lives in `ISecretStore` at scope `_platform`, key `passkey_session_signing_key`. It is auto-generated on first use, so no manual seeding is required for the default in-process store. Because it is read from the store rather than baked into config, rotating the secret at the store invalidates outstanding sessions on the next validation — the operator's revocation lever. Point `ISecretStore` at a durable / shared backing store (e.g. the Azure Key Vault companion) for a multi-instance deployment so every instance signs and validates against the same key.

## Routes

Four Giraffe routes drive the two ceremonies. Each *begin* returns the Fido2 options JSON plus a `challengeId`; the matching *complete* echoes `?challenge=<id>` on the query string with the browser's raw serialised `PublicKeyCredential` in the request body. A successful *complete* on either leg mints the session JWT and emits the ceremony audit rows.

| Route | Purpose |
|---|---|
| `POST /api/passkey/register/begin` | Resolve the enrolment identity (policy-gated), return attestation options. |
| `POST /api/passkey/register/complete` | Verify attestation, persist the credential, and sign the user in. |
| `POST /api/passkey/assert/begin` | Return assertion options (username-scoped, or usernameless for discoverable credentials). |
| `POST /api/passkey/assert/complete` | Verify the assertion, clone-detect via the signature counter, and sign the user in. |

Assertion enforces a monotonic per-credential signature counter — a non-increasing counter signals a cloned authenticator and the sign-in is rejected.

## See also

- [`../../InterPlatform/README.md`](../../InterPlatform/README.md) — the opt-in companion-composition shape (`PeerServerApp`) this mirrors.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyTypes

open System
open ToolUp.Platform

// ─── Phase 443 — WebAuthn / passkey server companion types ───────────
//
// A passkey-first auth companion for self-hosted deployments with no
// external IdP. Every other shipped companion (OIDC, Clerk, Entra)
// delegates identity; a standalone deployment has only
// `StaticJwtAuthProvider`. WebAuthn gives phishing-resistant,
// passwordless sign-in without the platform ever storing a password
// hash. Session issuance mints the SAME short-lived HS256 platform JWT
// shape `StaticJwtAuthProvider` validates — no parallel session model.

/// Declarative companion configuration. The relying-party (RP) id is
/// the WebAuthn scope of the credential — a registrable domain suffix
/// of every browser origin the app is served from (e.g. RP id
/// `example.com` covers origins `https://example.com` and
/// `https://app.example.com`). `Origins` is the exact allow-list the
/// ceremony verifier checks the browser's `clientDataJSON.origin`
/// against.
type PasskeyConfig = {
    /// WebAuthn relying-party id — a registrable domain suffix of every
    /// entry in `Origins`. Never include scheme or port (`example.com`,
    /// not `https://example.com:5000`).
    RelyingPartyId: string
    /// Human-readable RP name shown by the authenticator UI.
    RelyingPartyName: string
    /// Exact browser origins permitted to run the ceremony
    /// (`https://app.example.com`). The verifier rejects any
    /// `clientDataJSON.origin` outside this list.
    Origins: string list
    /// Challenge time-to-live. A begin-ceremony challenge that is not
    /// completed within this window is rejected (single-use + expiring —
    /// replay defence). Default 120s.
    ChallengeTtlSeconds: int
    /// Lifetime of the minted platform session JWT, in seconds. Matches
    /// the short-lived-bearer convention (`exp` mandatory). Default 900s
    /// (15 minutes).
    SessionTokenTtlSeconds: int
    /// Optional `iss` claim stamped on the minted session JWT and
    /// required by the paired `PasskeyAuthProvider` validator. `None`
    /// skips issuer binding (matches `StaticJwtConfig.Issuer = None`).
    Issuer: string option
    /// Optional `aud` claim stamped on the minted session JWT and
    /// required by the paired validator. `None` skips audience binding.
    Audience: string option
    /// Registration policy (secure by default). `false` (default) —
    /// invite-gated: registration requires an existing authenticated
    /// session, an invitation link's token, or the one-time bootstrap
    /// token. `true` — open registration: anyone may enrol a passkey for a
    /// NEW user id (an id the deployment already knows is refused). An
    /// explicit opt-in, surfaced by preflight.
    AllowOpenRegistration: bool
    /// One-time bootstrap token (typically sourced from an env var) that
    /// lets a fresh deployment enrol its FIRST credential before any
    /// session or invite exists. `None` disables the bootstrap path.
    /// Compared in constant time, and spent on its first successful use:
    /// the consumed marker is `BootstrapMarkerBlob` in the credential
    /// store's `_platform` container, so every replica sharing that store
    /// sees it.
    BootstrapToken: string option
    /// Why the deployment accepts an authenticator that does not verify
    /// the user (a PIN or a biometric), only that someone touched it.
    /// `None` (default) — both ceremonies REQUIRE user verification: the
    /// passkey is the only factor. `Some reason` lowers the requirement to
    /// preferred; a blank reason is read as `None`.
    UserVerificationWaiver: string option
    /// Whether `PasskeyConfigValidator` enforces that every origin is
    /// `https://` (loopback hosts exempt for local dev). Default `true`
    /// — WebAuthn requires a secure context in the browser anyway, so a
    /// cleartext origin is a misconfiguration. Overridable only for
    /// documented behind-TLS-proxy topologies.
    EnforceHttps: bool
}

module PasskeyConfig =
    /// The `_platform` blob container credential records live in
    /// (scope-derived; see `IBlobStorage` scope discipline). Blob names
    /// are prefixed `auth/passkeys/`.
    [<Literal>]
    let PlatformContainer = ScopeKey.PlatformContainer

    [<Literal>]
    let CredentialBlobPrefix = "auth/passkeys/"

    /// `ISecretStore` key (under `_platform`) holding the HS256 session
    /// signing secret. Auto-generated on first use, mirroring
    /// `ShareTokenStore`'s `share_token_signing_key`.
    [<Literal>]
    let SigningKeySecretName = "passkey_session_signing_key"

    /// Blob (under `PlatformContainer`) marking the bootstrap token spent.
    /// Outside `CredentialBlobPrefix`, so a credential listing never reads it.
    [<Literal>]
    let BootstrapMarkerBlob = "auth/passkey-bootstrap/consumed.json"

    /// Blob prefix (under `PlatformContainer`) of the create-only claims a
    /// registration takes on a NEW user id, keyed by its user handle.
    [<Literal>]
    let IdentityClaimPrefix = "auth/passkey-identities/"

    /// Minimal, secure defaults — invite-gated, https-enforced, no
    /// bootstrap token, user verification required, 120s challenge /
    /// 15-min session. A consumer sets
    /// `RelyingPartyId` / `RelyingPartyName` / `Origins`.
    let create (rpId: string) (rpName: string) (origins: string list) : PasskeyConfig = {
        RelyingPartyId = rpId
        RelyingPartyName = rpName
        Origins = origins
        ChallengeTtlSeconds = 120
        SessionTokenTtlSeconds = 900
        Issuer = None
        Audience = None
        AllowOpenRegistration = false
        BootstrapToken = None
        UserVerificationWaiver = None
        EnforceHttps = true
    }

/// A persisted passkey credential. Byte fields are stored as base64url
/// strings so the record round-trips through plain `System.Text.Json`
/// (no custom `byte[]` converter needed) and the credential id is a
/// filesystem/blob-safe name segment. PII-free apart from the optional
/// `Email` the deployment already holds for the user.
type PasskeyCredentialRecord = {
    /// base64url of the raw credential id (authenticator handle).
    CredentialId: string
    /// base64url of the COSE public key returned by attestation.
    PublicKey: string
    /// Authenticator signature counter. Monotonic per credential;
    /// a non-increasing value on assertion signals a cloned
    /// authenticator (rejected).
    SignCount: uint32
    /// base64url of the stable per-user handle (SHA-256 of the platform
    /// UserId). Ties every credential a user enrols to one identity.
    UserHandle: string
    /// The platform identity (`sub`) this credential authenticates as.
    UserId: UserId
    /// Display name captured at enrolment.
    DisplayName: string
    /// Optional email — drives the pending-invite team-membership path.
    Email: string option
    /// Declared authenticator transports (usb / nfc / ble / internal /
    /// hybrid), best-effort hints for the next assertion.
    Transports: string list
    /// Enrolment timestamp (UTC).
    CreatedAt: DateTime
}

// ─── Wire envelopes ──────────────────────────────────────────────────
//
// The ceremony option blobs themselves are Fido2NetLib's own JSON
// (`CredentialCreateOptions.ToJson()` / `AssertionOptions.ToJson()`),
// carried verbatim in `OptionsJson`. The raw authenticator responses
// ride in the request BODY (the browser's serialised PublicKeyCredential)
// with the `challengeId` correlator on the query string — so nothing is
// double-JSON-encoded.

/// `POST /api/passkey/register/begin` request.
type RegisterBeginRequest = {
    /// A NEW username / handle to enrol under. Required on the
    /// open-registration and bootstrap paths, where an id the deployment
    /// already knows is refused; ignored when an authenticated session or
    /// an invitation drives the identity.
    Username: string option
    /// Display name for the authenticator UI. Defaults to the username.
    DisplayName: string option
    /// The token an invitation link carries (`/invite/<token>`). Redeemed
    /// once on a successful enrolment, which joins the invited team with
    /// the invited role under an identity the server mints.
    InviteToken: string option
    /// One-time bootstrap token for a fresh deployment's first credential.
    BootstrapToken: string option
}

/// `POST /api/passkey/assert/begin` request.
type AssertionBeginRequest = {
    /// Optional username to scope `allowCredentials` to that user's
    /// enrolled credentials. Omit for a discoverable-credential
    /// (usernameless) flow.
    Username: string option
}

/// Begin-ceremony response — the Fido2 options JSON plus the correlating
/// challenge id the matching complete call must echo.
type CeremonyOptionsResponse = {
    ChallengeId: string
    OptionsJson: string
}

/// Successful ceremony result — the minted platform session JWT.
type SessionTokenResponse = {
    Token: string
    ExpiresInSeconds: int
    UserId: UserId
}

/// The two ceremony kinds a pending challenge belongs to.
type ChallengeKind =
    | Registration
    | Assertion
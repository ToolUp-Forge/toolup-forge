# Migration — Phase 332: reject empty / weak HMAC secrets (billing + peer)

**Status:** additive (GP 11). A deployment with well-formed secrets is byte-for-byte unchanged.
**Packages touched:** `ToolUp.Stripe.Webhook`, `ToolUp.Stripe.Server`, `ToolUp.Stripe.TierToken`,
`ToolUp.InterPlatform`.

## What changed and why

An HMAC-SHA256 computed with an **empty key is publicly computable** — anyone can produce a valid
MAC. `WebhookSigner.verifyWith` previously computed `HMAC-SHA256(UTF8(secret))` with **no non-empty
guard**, so a deployment with `WebhookSecret = ""` (the unset-env-var case) would accept a forged
`checkout.session.completed` / `invoice.paid` and drive fake payment state / free tier upgrades. The
sibling `TierToken.Token` already guarded its key length; this phase closes the matching omission and
raises the bar from *non-empty* to *≥ 32 bytes* across the three signing packages, and adds a boot-time
`StripeConfig` validator so the misconfiguration is a **loud startup refusal**, not a silent runtime
forgery-enabler.

The strength floor is **32 bytes** of the UTF-8 encoding (the SHA-256 block/output size). Every real
Stripe `whsec_…` secret and every provisioned peer signing key clears it, so no well-formed deployment
changes behaviour (GP 11).

### 1. `ToolUp.Stripe.Webhook` — `WebhookSigner` fails closed on a blank/weak secret

- New **additive** `WebhookError` case `SecretMissing`. `verifyWith` returns `Error SecretMissing`
  **before** computing any HMAC when the secret is empty or below 32 bytes.
- `ToolUp.Stripe.Server.Routes` maps `SecretMissing → HTTP 500` (server misconfiguration, not a bad
  request) with a non-secret-leaking log line.

No consumer action required — the guard is internal. If you pattern-match `WebhookError` exhaustively,
add a `| SecretMissing ->` arm (the compiler will flag the incomplete match).

### 2. `ToolUp.Stripe.Server` — new `StripeConfigValidator`

A billing-active deployment wires the validator into its composition root:

```fsharp
open ToolUp.Stripe.Server

// config : StripeConfig, read from ISecretStore / env at compose time
serverApp
|> ServerApp.withConfigValidator (StripeConfigValidator config)
|> ServerApp.run
```

It refuses startup (`ValidationResult.Error`, actionable message naming the secret) when `WebhookSecret`
is empty, not `whsec_…`-shaped, or below 32 bytes. It is **security-class**
(`ISecurityClassValidator`), so it runs even under `ServerConfig.SkipPreflight = true` — a single
boolean can never silently re-open the forged-payment surface. A deployment that never bills never
composes it and pays nothing (GP 13). Registering it **is** the "billing-active" signal.

### 3. `ToolUp.Stripe.TierToken` — `Token.mint` / `Token.validate`

The existing empty-key guard now rejects any key **below 32 bytes** (still `MintError.SecretMissing` /
`ValidateError.SecretMissing` — no new error case). A too-short signing secret fails closed instead of
minting/verifying a valid-but-weak MAC. No API change.

### 4. `ToolUp.InterPlatform` — `JwtPeerAuthProvider`

The per-call `ISecretStore` signing-key read (issue / validate / delegation) now fails closed with
`Error (PeerUnauthorized …)` when the stored key is blank or below 32 bytes, so a peer whose key is
`""` (or a short placeholder) can neither mint nor accept a publicly-forgeable token. Internal change —
no public-surface change.

## Note on the shared strength helper

The min-entropy guard is a ~3-line pure function *mirrored* (not shared) across the three signing
packages — `WebhookSigner.secretIsStrong`, `Token.secretIsStrong`, `PeerJwt.signingKeyIsStrong`. These
packages are deliberately decoupled (`ToolUp.Stripe.Webhook` carries zero dependencies by design; the
three share no common referenced library), so a single shared module would force new coupling. This
follows the existing local precedent — `constantTimeEquals` is already duplicated across
`WebhookSigner.fs` and `Token.fs` for the same reason. The `32`-byte floor is a `[<Literal>]` in each.

## API baselines

`WebhookError.SecretMissing` and `StripeConfigValidator` are **additive** public surface. The Phase 175
`PublicApiApproval` gate tolerates additive growth (it fails only on *removed* tokens), so the build
stays green without a baseline edit; `api-baselines/ToolUp.Stripe.Webhook.approved.txt` and
`…Stripe.Server.approved.txt` fold the new tokens in on the next `TOOLUP_APPROVE_API=1` regeneration.

## Verification

```pwsh
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Stripe.Tests/ToolUp.Stripe.Tests.fsproj   # incl. SecretStrengthGateTests
dotnet run --project Build.fsproj -- VerifyAll
```

## Rollback

Revert the commit. The change is additive and self-contained; no data migration, no persisted-state
change. A deployment already running with well-formed secrets is unaffected either way.

# Phase 235 — Webhook signing-secret rotation

**Status:** additive, transparent. No consumer action required for the wire; the admin UI
gains one button.

## What changed

A webhook subscription's HMAC signing secret can now be rotated server-side, without delete +
recreate and without a changed subscription id.

- **Contract (`ToolUp.Platform.Core`).** `IWebhookApi` gains
  `RotateSecret: Guid -> Async<Result<WebhookSubscription, string>>`, carrying the same
  `[<RequiresClaim "scope">]` gate as the other mutators plus
  `[<Audit "Custom:WebhookSubscriptionSecretRotated">]`. Like `CreateSubscription`, the response
  returns the **new secret unmasked, once** — every other path stays masked.
- **Model (`ToolUp.Platform.Core`).** `WebhookSubscription` gains `PreviousSecret: string option`
  and `PreviousSecretExpiresAt: DateTime option` — the grace-window state. `maskSecret` now masks
  both secrets. New helpers on the `WebhookSubscription` module: `secretRotationGracePeriod`
  (the documented constant, **24h**), `withRotatedSecret`, and `acceptedSecrets`.
- **Handler (`ToolUp.Platform.Server`).** `RotateSecret` generates a fresh high-entropy secret
  (32 cryptographically-random bytes, base64 — same shape as the create-time client generator),
  persists new + previous + grace expiry via the new `IWebhookRegistry.RotateSecret`, and audits
  the rotation (actor + subscription id + grace expiry; **never** the secret value).
- **Dispatcher (`ToolUp.Platform.Server`).** Deliveries are signed with the **current** secret;
  during the grace window the `X-ToolUp-Signature` header carries **both** signatures
  (`sha256=<new>,sha256=<old>`, comma-joined) so a receiver still configured with the old secret
  keeps verifying. After the window closes only the current signature is emitted and the old
  secret stops verifying. New public `WebhookSignature` module (`header` / `headerFor` /
  `verifies`) holds the signing + receiver-side verification logic.
- **Admin UI (`ToolUp.Platform.Client`).** A **Rotate secret** button per subscription (behind a
  confirm dialog) calls `RotateSecret` and reveals the new secret once via the same
  one-time-reveal banner the create flow uses.

## Grace window

The previous secret remains valid for **24h** (`WebhookSubscription.secretRotationGracePeriod`) —
a full business day for receivers to update their stored secret with zero missed deliveries.
This is the only knob; rotation is otherwise immediate (the new secret becomes primary at once).
The window is a deliberate availability-over-secrecy trade-off for an operational-convenience
feature — a leaked secret keeps signing for the bounded window. Operators who need an immediate
hard cutover should rotate, then re-rotate after the window (or delete + recreate as before).

## Consumer action

- **Wire / receivers:** none. Existing receivers that verify a single `sha256=<hex>` value should
  accept that **any** comma-separated signature in `X-ToolUp-Signature` matches their secret (the
  Stripe-style multi-signature convention). A receiver that exact-matches the whole header value
  must be updated to split on `,` before comparing — but this only matters during a rotation
  grace window, and a receiver that already split (most do) needs no change.
- **Admin UI:** none — the Rotate secret affordance appears automatically.
- **Storage:** the two new `WebhookSubscription` fields are `option`-typed and serialise as
  `null` when absent; a pre-235 persisted subscription blob (lacking the keys) reads back with
  both as `None`. Backward-compatible on the read path.

## Rollback

Revert the phase commit. Persisted subscriptions written after the upgrade carry the two new
fields; on rollback the old code ignores them (extra JSON keys are tolerated by the
`FableConverters` deserialiser), so no data migration is needed. Any subscription mid-grace-window
at rollback simply reverts to single-secret signing with its current secret.

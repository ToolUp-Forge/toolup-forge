# Migration — Phase 232: Encryption admin-token hardening

**What changed** (the `POST /api/_platform/encryption/destroy-scope-key/{scopeId}` endpoint):

1. **Uniform 403 on every non-admin failure.** Previously a missing
   `X-Admin-Token` header (when a token was configured) returned **401**,
   disclosing that a token is set; an invalid token returned 403; no token
   configured returned 401. All three now return a uniform **403** with a
   generic message — the response no longer reveals whether a token is
   configured or which part failed.
2. **Per-IP throttle.** After 5 failed token attempts from the same source
   IP within 5 minutes, the endpoint returns **429** (in-process friction
   over the high-entropy token; not the primary control).
3. **Logged attempts.** Failed attempts log `Warn` (source IP + reason);
   token-gated success logs `Info`. The canonical `EncryptionKeyDestroyed`
   audit still fires on a successful destroy.

**Who must act**

- **No consumer code change.** This is an internal deployment-admin
  endpoint. The Platform-Admin role path is unchanged.
- **Scripts using `X-Admin-Token`:** if a script distinguished 401 (missing
  header) from 403 (wrong token), it should now treat **403** as the single
  "not authorized" response, and back off on **429**. The constant-time
  token comparison and successful-destroy behaviour are unchanged.

**Rollback**

Revert the forge commit. No data or config migration.

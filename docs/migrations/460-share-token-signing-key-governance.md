# Share-token signing-key governance (Phase 460) — consumer migration

**What changes.** The share-token HMAC signing key
(`share_token_signing_key`, in the `_platform` scope of your composed
`ISecretStore`) is now treated as an **operator-managed secret**. A
production-shaped deployment with a live share-token surface and no
provisioned key **refuses to start**, where it previously started and
minted one for itself.

"Production-shaped" means `ServerConfig.PublicBaseUrl` is set **or**
`ReplicaCount > 1`. A single-instance, non-public deployment is
unaffected: it still auto-generates, and the preflight is still silent.

## Do you have to do anything?

**No, if you already provision the key.** The validator returns `Ok`
with no message, exactly as before. Record `n-a`.

**No, if you are not production-shaped** (no `PublicBaseUrl`, one
replica) — dev boxes, CI, single-instance internal deployments. The
auto-generate convenience survives untouched. Record `n-a`.

**No, if you do not compose a share-token surface at all** —
`ShareTokenStore = NoShareTokenStore` and no claim-bearer surface. The
validator self-gates. Record `n-a`.

**YES, if you are public or multi-replica and have never provisioned the
key.** Your next deploy will fail preflight with
`share-token-signing-key-provenance`. This is the case the phase exists
to catch, and it is not bypassable with `SkipPreflight` — the validator
is security-class.

## The fix (choose one)

**1 — Provision the key. This is the correct fix.** Write a
base64url-encoded 32+ byte random value to `share_token_signing_key` in
the `_platform` container of your `ISecretStore`, before the next boot.
Generate one with:

```bash
# 32 random bytes, base64url, no padding
openssl rand 32 | basenc --base64url | tr -d '='
```

Then **back it up alongside your database credentials**. If the secret
store is ever wiped or restored blank, every outstanding public share
link stops verifying, permanently — the tokens carry no recoverable
secret.

**2 — Acknowledge an ephemeral key.** This is the **non-breaking
route**: set it, deploy, and provision on your own schedule.

```text
TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1
```

or, in the composition root, `AcceptEphemeralShareTokenKey = true` on
`ServerConfig`. The refusal becomes a `Warning` that **names the flag**
as the reason nothing was refused — deliberately, so an opt-out nobody
remembers making cannot look like a correct configuration in a preflight
artefact.

**3 — Correct the shape.** If the deployment is not actually
internet-facing, clearing `PublicBaseUrl` / setting `ReplicaCount = 1`
makes the validator silent. Only do this if it is true.

## A second, quieter finding you may now see

If you are production-shaped and the key **is** present but this SDK
minted it, you will get a new `Warning`. That is not the refusal above —
it fires on a deployment that was previously reporting clean.

The store now writes a marker, `share_token_signing_key_origin =
auto-generated`, beside any key it mints, which is the only thing
distinguishing a minted key from a provisioned one. To clear the
warning **without invalidating a single share link**:

1. Read the current `share_token_signing_key` value.
2. Record it in your secret-management system as a managed secret, with
   the same backup scope as your other credentials.
3. **Delete the `share_token_signing_key_origin` marker.** That deletion
   is how you tell preflight you have adopted the key.

A key minted before this phase carries no marker and reads as
operator-provisioned, so no already-green deployment newly reddens here.

## Rotation, now that you own it

Overwrite `share_token_signing_key`. Every process picks the new value
up within the 10-minute signing-key cache TTL — **no restart needed** —
and all outstanding tokens then fail verification. That is intended, and
it doubles as a "revoke every live share link at once" lever. There is
no key-id field and no overlap window.

## Multiple replicas

Replicas booting together against an empty secret store used to race:
each minted a key, the last write won, and a token signed by one replica
could fail on another. Generation is now serialised within a process,
the store is re-read inside that gate, and after persisting the replica
**adopts whatever the store holds** rather than the value it minted — so
a replica never signs with a key the secret store does not have.

This narrows the window to one store round-trip; it is not a
compare-and-set, because `ISecretStore` exposes no conditional write.
Pre-provisioning removes the race entirely, which is why option 1 above
is the correct fix rather than merely the tidy one.

## Verification

- Boot with the key absent and `PublicBaseUrl` set: startup fails naming
  `share-token-signing-key-provenance`.
- Set `TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1`: startup succeeds, and
  the finding appears as a `Warning` naming the flag — check the
  HealthMonitorUI Preflight tab (production-safe) or the `/dev/inspect`
  Validators panel (debug builds only).
- Provision the key and clear the flag: the finding disappears.

## Rollback

Set `TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1`. There is no other
rollback and none is needed — the flag restores the previous startup
behaviour exactly, differing only in that the posture is now reported
rather than silent.

## See also

- `DEPLOYMENT.md` — "Share-token signing key — an operator-managed
  secret" (the full operator procedure, including the refusal table)
- `docs/platform/security.md` — the security-posture summary
- `docs/security/PLATFORM-SECURITY-RULES.md` — rule **AN-11**

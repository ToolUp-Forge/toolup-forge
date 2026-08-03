# Migration — Phase 334: federated-identity sanitisation parity

**Status:** security hardening + one additive public function. **Can reject identities that previously authenticated** — see [Rollout order](#rollout-order) before upgrading a deployment that uses Entra External ID or the InterPlatform peer substrate. A well-formed identity is byte-for-byte unchanged (GP 11), and a deployment composing neither surface is unaffected.

## What changes

Phase 6l.H shipped `IdentitySanitiser.sanitiseScopeId` — the single charset policy for any identifier that becomes a storage-scope or key-path segment (alphanumerics, `-`, `_`, `.`; no leading period; 1–256 chars; no Windows reserved device name). Phase 137 applied it to `X-Peer-Name` before the `peers/{name}/bearer` key. Three federated boundaries were still raw, and this phase closes them with the **same** sanitiser — not a second implementation of it.

| Boundary | Before | After |
|---|---|---|
| `EntraExternalIdAuthProvider.applyEntraMapping` | Raw `oid` / `sub` / `tid` claims **overwrote** the inner OIDC provider's already-sanitised `UserId` / `TenantId`; the sanitised value was only the fallback, so 6l.H's guard was undone one line after it ran. | Each claim is sanitised. A rejected claim is treated as **absent**, so the mapping walks the same `oid → sub → inner` candidate chain and lands on the inner provider's sanitised value. |
| `JwtPeerAuthProvider.ValidatePeerToken` | The token's own, wholly unverified `iss` was interpolated straight into `peers/{iss}/signing-key` for the `ISecretStore` lookup. | `iss` is shape-checked **before** the key is built. A malformed one is `PeerUnauthorized`, and never reaches `GetSecret` — not even to miss, because a path-mapping secret-store companion resolves the traversal on the way to deciding that. |
| `JwtPeerAuthProvider.VerifyDelegation` | The last hop of the wire-supplied `DelegationChain` addressed a signing key through the same interpolation. | Same guard, same rule set. |
| `BlobPeerRegistry` | `PeerId` became a blob name (`peers/{peerId}.json`) unchecked, on `Resolve` / `Register` / `Remove`. | Sanitised first. `Resolve` → `None`, `Register` → `Error (PeerTransport …)`, `Remove` → no-op (it is already documented idempotent). |

`DisplayName` and `Email` are deliberately **not** sanitised — they never become a scope or key-path segment, and constraining them would reject legitimate human names for no security gain.

**The issue path is deliberately unguarded.** `IssuePeerToken` builds its key from the caller id supplied by the deployment's own composition (`withLocalPeer`), not from the wire. Refusing to mint there would break a deployment whose local id is unusual but whose peers never actually see it, and would convert this hardening into an outage on upgrade of the *calling* side.

## New public surface (additive)

One function, added so the parity test pack drives the shipped mapping rather than a re-implementation of it:

```fsharp
ToolUp.AuthProviders.EntraExternalIdAuthProvider.applyValidatedClaims
    : rawToken: string -> user: AuthenticatedUser -> AuthenticatedUser
```

It performs **no validation of its own** — the caller must have verified the token first, exactly as the decorator has by the time it calls this. No existing signature changed; `api-baselines/ToolUp.AuthProviders.EntraExternalId.approved.txt` grew by one line and lost none.

## Consumer action

**None for a well-formed identity.** The sanitiser returns valid input unchanged, so an existing peer id, `oid`, `sub` or `tid` that already matched the policy resolves the same key, the same scope and the same blob as before.

**Action is needed if any of these is true:**

1. **A peer id contains a character outside `[A-Za-z0-9-_.]`** — most plausibly a `:`, a space, a `/`, or a URL-shaped id. That peer's inbound tokens will now be refused with `PeerUnauthorized`, and its directory entry will no longer resolve.
2. **A peer id starts with a period**, or is a Windows reserved device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`), bare or with an extension.
3. **Your IdP issues a non-conforming `oid` / `sub` / `tid`.** Entra's own claims are GUIDs and conform; a federated or self-issued upstream may not. The effect is *not* a rejected login — it is a **fall-back to the inner OIDC provider's sanitised `UserId`**, which for such a deployment means users land on a different storage scope than before and appear to have lost their data.

Audit before upgrading:

```powershell
# Peer ids currently registered in the directory (adjust for your IBlobStorage backend).
# Anything not matching this pattern will be refused after the upgrade.
Get-ChildItem <blob-root>/_platform/peers -Filter *.json |
    Where-Object { $_.BaseName -notmatch '^[A-Za-z0-9][A-Za-z0-9\-_.]{0,255}$' }
```

For the Entra side, check a sample id token's `oid` / `tid` against the same pattern. If either fails, **rename the identity before upgrading** rather than after — see below.

## Rollout order

The peer change is asymmetric: the *receiving* side enforces, the *issuing* side does not. That is what makes a safe order possible.

1. **Audit both sides first** (above). If every peer id and every federated claim conforms, upgrade in any order — there is nothing to sequence.
2. **If a peer id does not conform:** rename it on **both** deployments *before* either upgrades — re-`Register` the directory entry under the conforming id and move the `ISecretStore` secret from `peers/{old}/signing-key` to `peers/{new}/signing-key`. Both sides must agree on the id, because it is what the token's `iss` carries and what selects the key.
3. **If a federated claim does not conform:** decide the target `UserId` deliberately and migrate the storage scope to it, rather than letting the fall-back choose. The fall-back is safe (it is the inner provider's already-sanitised value) but it is not necessarily the id you want, and it changes which scope those users read.
4. **Upgrade receivers before issuers** if you must interleave. A receiver on this version refuses a malformed `iss` from an old issuer — a clean, audited `PeerUnauthorized`. The reverse order fails identically but leaves the malformed-id window open for longer.

There is no persisted-state migration and no wire-format change: the token shape, the key layout and the directory document are all unchanged.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — **5,488 tests, 0 failed**, including 25 new cases in `InProcess/FederatedIdentitySanitisationTests.fs`.
- The pack drives **one shared corpus of hostile identifiers through all three boundaries** and asserts each verdict equals the canonical `IdentitySanitiser` verdict, so a future divergence on any single boundary fails rather than hiding behind the other two.
- **Negative control:** with the two guards reverted to pass-through, 13 of the 25 cases fail. The traversal case additionally registers a real, strength-clearing signing key *at the traversal key path*, so the refusal is provably the shape check and not a missing or weak key.

## Rollback

Revert the three source files. `IdentitySanitiser` itself is untouched, no persisted state is involved, and the additive `applyValidatedClaims` can be dropped with its baseline line. A deployment that renamed a peer id or a federated identity as part of step 2/3 should **not** rename it back — the conforming id is valid on both versions.

## See also

- [`docs/migrations/131-identity-sanitisation-store-seam.md`](131-identity-sanitisation-store-seam.md)
- [`docs/migrations/137-peer-bearer-middleware-robustness.md`](137-peer-bearer-middleware-robustness.md) — the same treatment for `X-Peer-Name`
- [`docs/security/PLATFORM-SECURITY-RULES.md`](../security/PLATFORM-SECURITY-RULES.md)

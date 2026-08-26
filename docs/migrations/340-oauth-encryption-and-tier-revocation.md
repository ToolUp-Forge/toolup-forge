# Migration — Phase 340: OAuth-credential encryption scope + tier-token revocation

**Status:** additive. No API is removed or retyped, and no previously-green deployment is refused
startup by this change. Two things a deployment could not previously observe now become visible, and
one opt-in capability (tier-token revocation) is new.

**Consumer action:** read §1 if you run OAuth connectors; read §3 if you issue tier cookies. If you
already compose an encrypting `ISecretStore` and never mint a revocable token, you have nothing to do.

## Why

Two entitlement / credential-at-rest gaps, both of them silent:

1. Connector OAuth flows persist a long-lived third-party **refresh token** through `ISecretStore`.
   Whether that lands encrypted depends entirely on which store is composed, and the default is a raw
   `FileSecretStore`. Phase 138's `oauth-secret-encryption-mode` validator refused that — but only in
   auth-requiring modes, and the `AcceptPlaintextSecretsWhenAuthRequired` escape hatch suppressed it
   *without a trace*. So an `Anonymous`-surface deployment running connectors, and any deployment
   holding the flag, both reported clean while writing plaintext credentials to blob storage.
2. The tier cookie is a stateless signed claim whose only bound is `exp`. A cancelled or
   charged-back subscriber kept the paid tier until the token expired on its own, and a leaked cookie
   could not be withdrawn at all. Blast radius scaled with the configured cookie lifetime, and nothing
   in the surface said so.

## 1. `oauth-secret-encryption-mode` — the scope is no longer auth-gated

`OAuthSecretEncryptionModeValidator` previously ran its whole predicate inside
`ConfigValidator.gatedAuthValidation`, so it evaluated to `Ok` unless `DeploymentConfig.requiresAnyAuth`
held. That gate is right for its siblings (`csrf-default-mode`, `header-auth-mode`, `sse-auth-mode`)
— they reason about how a *request* is authenticated, which is meaningless without auth — and wrong
for this one: the credentials at risk are the deployment's own third-party grants, persisted by the
ingestion substrate whether or not callers authenticate.

New severity ladder (the validator is still registered only inside the DataIngestion-gated OAuth
block, so a deployment with no connectors never sees any of it):

| Store encrypts at rest? | `AcceptPlaintextSecretsWhenAuthRequired` | Auth-requiring surface? | Before | After |
|---|---|---|---|---|
| yes | either | either | `Ok` | **`Ok`** (unchanged) |
| no | `false` | yes | `Error` (refusal) | **`Error`** (unchanged) |
| no | `false` | no (Anonymous) | `Ok`, silent | **`Warning`** |
| no | `true` | either | `Ok`, silent | **`Warning`** naming the flag |

**Nothing that booted before stops booting.** `Warning` is non-blocking in
`ConfigValidatorAggregator` — it is printed in the preflight summary and logged at Warn. The two new
rows are the ones that used to say nothing at all.

**What to do about a new Warning.** Same three fixes the refusal always printed, and the Warning
carries the identical text:

1. Compose the encryption decorator with a real key —
   `TOOLUP_SECRETS_MASTER_KEY` set to a base64-encoded 32-byte key
   (`EncryptedSecretStore.generateMasterKey ()` once, during setup). Note an `EncryptedSecretStore`
   with **no** key is a plaintext passthrough and is detected as such.
2. Switch to a cloud-KMS-backed store — `TOOLUP_SECRET_STORE=azure-key-vault` (or
   `aws-secrets-manager` / `vault` / `gcp-secret-manager`).
3. Keep `ServerConfig.AcceptPlaintextSecretsWhenAuthRequired = true` **if you have confirmed** the
   storage backend provides at-rest encryption itself (disk FDE, an encrypting volume). The Warning
   is the point here: the flag is now visible in every preflight artefact instead of being
   indistinguishable from a correctly-encrypting store.

If you set the flag long ago and cannot now say why, treat the Warning as the prompt to check the
backend rather than as noise to filter.

## 2. Tier-token revocation — a per-subject epoch (opt-in, `ToolUp.Stripe.TierToken`)

The token gains a second shape. Both are minted with the same secret and verified with the same
constant-time compare; the part count distinguishes them.

```
legacy    (v1, 3 parts)  {tier}.{exp}.{sig}
revocable (v2, 5 parts)  {tier}.{exp}.{subjectB64Url}.{epoch}.{sig}
```

Revocation is a **per-subject epoch**: your deployment stores one monotonic counter per subject and
bumps it whenever every outstanding grant for that subject must stop working — cancellation,
chargeback, a leaked cookie, a "sign out everywhere". A token stamped *below* the current epoch is
refused on the next request, whatever its `exp` says. The epoch, not the token, is the authority.

Both new fields sit inside the signed payload, so neither can be edited without invalidating the
signature; the subject is base64-url encoded so a subject containing a `.` (an email address, a
namespaced id) cannot shift the field boundaries.

New surface:

```fsharp
type TokenClaims = { Tier: Tier; Subject: string option; Epoch: int64 option; ExpiresAt: DateTimeOffset }
type EpochLookup = string -> Async<int64 option>          // None ⇒ SubjectUnknown (fails closed)

Token.mintFor           : Tier -> string -> int64 -> int -> DateTimeOffset -> byte[] -> Result<string, MintError>
Token.inspect           : DateTimeOffset -> string -> byte[] -> Result<TokenClaims, ValidateError>
Token.validateWithEpoch : EpochLookup -> DateTimeOffset -> string -> byte[] -> Async<Result<Tier, ValidateError>>
Token.RecommendedMaxLifetimeSeconds : int   // 86400

Cookie.issueFor                    : CookieConfig -> HttpContext -> Tier -> string -> int64 -> int -> byte[] -> Result<unit, MintError>
Cookie.resolveFromRequestWithEpoch : CookieConfig -> HttpContext -> DateTimeOffset -> byte[] -> EpochLookup -> Async<Tier option>
```

New error cases (appended at the end of each DU, so no existing tag moves):
`MintError.InvalidSubject` / `.InvalidEpoch`; `ValidateError.RevocationCheckRequired` / `.Revoked` /
`.SubjectUnknown`.

### Adopting it — two steps, in this order, with no flag day

**Step 1 — move the resolve path first.** A legacy three-part token carries no subject, so
`validateWithEpoch` passes it straight through with its tier and never calls the lookup. Switching
your resolve path is therefore a no-op for every cookie currently in the wild:

```fsharp
// before
let tier = Cookie.resolveFromRequest config ctx DateTimeOffset.UtcNow secret
           |> Option.defaultValue Tier.Anonymous

// after — same answer for every existing cookie
let currentEpochFor : EpochLookup =
    fun subject -> async { return! myStore.TryGetTokenEpoch subject }   // None ⇒ unknown subject

let! tier =
    Cookie.resolveFromRequestWithEpoch config ctx DateTimeOffset.UtcNow secret currentEpochFor
    |> Async.map (Option.defaultValue Tier.Anonymous)
```

**Step 2 — start minting revocable tokens**, once step 1 is deployed everywhere that reads the cookie:

```fsharp
let! epoch = myStore.GetOrCreateTokenEpoch userId          // starts at 0L
Cookie.issueFor config ctx tier userId epoch lifetimeSeconds secret |> ignore
```

Then revoke by bumping:

```fsharp
// on cancellation / chargeback / a reported leak
do! myStore.BumpTokenEpoch userId
```

**Do not reverse the two steps.** `Token.validate` and `Cookie.resolveFromRequest` **refuse** a
revocable token — `RevocationCheckRequired`, resolving to `None` at the cookie surface — rather than
degrading to the tier. That is deliberate: a deployment that minted revocable tokens and kept reading
them through the epoch-unaware path would have a revocation feature that revokes nothing, and would
look entirely healthy doing it. Minting before the readers are updated logs every user out; reading
first costs nothing.

**Storing the epoch.** It is one non-negative `int64` per subject, monotonic, never reset. Any store
you already own will do (a user row, a blob, a cache with a durable backing). `None` from the lookup
means "no such subject" and fails closed — a deleted account must not keep a paid tier — so return
`Some 0L`, not `None`, for a live subject that has never been revoked.

**Recommended maximum cookie lifetime.** `Token.RecommendedMaxLifetimeSeconds` is **86400 (24 hours)**.
It is guidance, not a clamp: enforcing it would silently change an existing deployment's session
behaviour on upgrade. The reasoning is the blast-radius argument made concrete — a *legacy* token
cannot be withdrawn at all, so its lifetime **is** the window in which a cancelled, charged-back or
leaked grant keeps working, and beyond about a day that window stops being a session and starts being
a licence. A *revocable* token is bounded by the epoch bump instead and can safely run longer; the
ceiling then governs only how long a subject keeps a tier you downgraded without bumping anything.

## 3. Two tier-cookie edges hardened

Both are behavioural changes to existing functions. Neither breaks a signature.

**`Cookie.clear` now mirrors every attribute `Cookie.issue` sets** — `HttpOnly`, `Secure`,
`SameSite=Lax`, `Path=/`. It previously set only `Expires`. This is not tidiness: a browser matches a
replacement cookie on name + domain + path and can reject or partition one whose security attributes
disagree, so an unmirrored clear could leave the cookie the signout was supposed to remove **live**.
Both paths now build from one shared options factory, so they cannot drift again.

**The `InsecureCookiesEnvVar` downgrade now additionally requires a non-production request host.**
The env var alone used to be sufficient, so a variable set once in a shared compose file — or baked
into an image later promoted — shipped a non-`Secure` tier cookie over a public network, silently,
because the cookie still works without `Secure`.

Non-production means: loopback (`localhost`, `127.*`, `[::1]`, `0.0.0.0`), a `.localhost` / `.local` /
`.test` / `.internal` suffix, or a single-label host (a compose / Kubernetes service name, which
cannot be a public FQDN). Both halves are exposed for your own tests:
`Cookie.isNonProductionHost : (string | null) -> bool` and
`Cookie.insecureDowngradeApplies : CookieConfig -> HttpContext -> bool`.

The input is the `Host` **header**, which a client controls. That is acceptable and worth stating
plainly: forging it can only weaken the attacker's own cookie, on a deployment that already opted
into the env var, and cannot re-enable the downgrade for anyone else. The gate is defence against a
misplaced env var, not against a hostile client.

**Who this affects:** a deployment serving plain HTTP from a *public* hostname with the insecure flag
set. Its cookies become `Secure` and will therefore stop being sent over HTTP. That is the intended
correction — but if you run a preview environment on a routable domain over HTTP, terminate TLS there
or move it to one of the recognised development suffixes.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — every pack green.
- New coverage in `src/ToolUp.Stripe.Tests/TierTokenRevocationTests.fs`: epoch bump revokes before
  `exp`; revocation is per-subject; a token stamped above the current epoch stays live (monotonic, not
  equality); unknown subject fails closed; an expired token never costs a lookup; `Token.validate`
  refuses a revocable token; forged epoch and swapped subject are both `SignatureMismatch`; a
  truncated revocable token does not degrade to a legacy one; legacy tokens round-trip unchanged and
  never reach the lookup; dotted subjects survive the round-trip; `clear` mirrors the issue-path
  attributes; the insecure downgrade is inert on a production host and honoured on localhost.
- Updated coverage in `src/ToolUp.Platform.Tests/InProcess/OAuthSecretEncryptionModeValidatorTests.fs`:
  the two new `Warning` rows, plus a guard that the escape hatch does **not** manufacture a finding on
  an encrypting store.

## Rollback

§1 — restore the `ConfigValidator.gatedAuthValidation` wrapper in
`OAuthSecretEncryptionModeValidator.Validate()`; the two `Warning` rows revert to silent `Ok`.
§2 — entirely additive; existing `mint` / `validate` call sites are unaffected, so removing the new
functions is sufficient (do this only if no revocable token is outstanding, since none would then
validate). §3 — restore the previous `clear` body and drop the host predicate from
`insecureDowngradeApplies`; both revert to the prior behaviour with no data migration.

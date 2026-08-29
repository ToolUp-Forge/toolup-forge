# Security — deployment-time threat surfaces and hardening

This page is the platform-side security reference for operators composing a ToolUp deployment. The repo-root [`SECURITY.md`](../../SECURITY.md) covers vulnerability disclosure (where to send reports, embargo policy); this page covers the threat surfaces a deployment exposes and the SDK substrate available to harden them.

> **Answering a vendor-risk questionnaire rather than tuning a deployment?** Read [`../security/PLATFORM-SECURITY-RULES.md`](../security/PLATFORM-SECURITY-RULES.md) instead — the versioned, evidence-cited statement of what the SDK enforces, with an explicit out-of-scope list. This page assumes you have already chosen to deploy and are hardening a specific composition.

Most of the page is the **Mixed-mode threat surface** section below — the model documented in [`surfaces.md`](surfaces.md) lets a single deployment serve anonymous + authenticated subjects concurrently, and the threat surfaces that follow are not the union of the single-shape threats but a distinct shape worth reasoning about explicitly.

## Mixed-mode threat surface

A mixed-mode deployment hosts subjects of different trust levels in the same process. A `SurfaceProfile` list of `[anonymous; team; claimBearer]` means the same middleware pipeline resolves three concrete subject kinds per request and routes each to a handler whose `SurfaceRequirement` admits it. The model's hardening contracts are declarative and structural — `SurfaceEnforcementMiddleware` gates every `/api/*` route against its declared `SurfaceRequirement`, `StorageScope` is derived from the resolved `Subject` and cannot be synthesised by handler code, and `SurfaceCoherenceValidator` refuses startup on the ten declared rule violations rather than letting a misconfiguration ship.

What that leaves to the operator is the **shape-asymmetric risks**: per-resource budgets that one surface can consume on behalf of itself but starve another, attacker behaviours that exploit the cheapest subject kind (anonymous), and trust boundaries the surface model surfaces but does not enforce (share-token leakage, cross-archetype admin exposure). This section is the operator's reference for those.

### Per-shape rate-limiting guidance

`RateLimitConfig` resolves a `RateLimitPolicy` per request via the resolved `Subject`'s kind, with a partition key implied by that kind. The shape:

```fsharp
type RateLimitConfig = {
    /// Applied to every subject kind without a PerShape entry. `None`
    /// disables rate-limiting deployment-wide unless PerShape lists
    /// per-kind overrides.
    Default: RateLimitPolicy option
    /// Sparse per-subject-kind overrides; absent keys use Default.
    PerShape: Map<SubjectKind, RateLimitPolicy>
}
```

Authoring forms in `RateLimitConfig`:

| Helper | Use for |
|---|---|
| `RateLimitConfig.none` | No limiter wired. The pre-66 default; rejected by `RateLimitModeValidator` when any `SurfaceProfile` other than `Anonymous` is in `Surfaces`, unless `AcceptNoRateLimitWhenAuthRequired = true`. |
| `RateLimitConfig.uniform policy` | One policy applied to every subject kind. Equivalent to the pre-66 single-policy shape; correct for any single-shape deployment. |
| `RateLimitConfig.perShape map` | Different policies per kind, no fallback. Unmatched kinds resolve to no limit. Use when subject-kind-specific behaviour is the point and a silent fallback would hide misconfiguration. |
| `RateLimitConfig.withOverrides default overrides` | Default policy plus per-kind overrides. The mixed-mode common shape. |

The partition keys per kind are not authored — they are implied by `RateLimitPolicy.partitionFor`:

| `SubjectKind` | Partition key | Carrying value |
|---|---|---|
| `AnonymousKind` | `ip:{clientIp}` | Remote IP from the request (X-Forwarded-For when `ForwardedHeadersTrust` admits the proxy). |
| `UserKind` | `user:{userId}` | The authenticated subject's id. |
| `TeamMemberKind` | `team:{teamId}` | The active team id. **Shared across all members of the same team.** |
| `ClaimBearerKind` | `token:{tokenId}` | The presented share-token id. |

The team-partition is the load-bearing operational subtlety: a team of N members shares one budget. Sizing a `TeamMemberKind` policy by per-user expectations and then watching one busy member exhaust the budget for the rest is the predictable failure mode.

#### Why per-shape budgets matter

A single `Default` policy applied uniformly to a mixed-mode deployment is operationally lossy in both directions:

- **Anonymous traffic starving authenticated traffic.** A `Default` set to admit an authenticated user's expected request rate (say 100 req/min) lets each anonymous IP do the same. A scraper hitting public endpoints under N IPs claims N × budget — the deployment burns CPU and downstream costs on traffic that produces no value, while authenticated subjects share the leftover capacity.
- **Authenticated traffic starving anonymous traffic.** The inverse: a `Default` set to a tight anonymous-shape ceiling rejects authenticated subjects who legitimately need more headroom.

The fix is `RateLimitConfig.withOverrides`:

```fsharp
{ ServerConfig.defaults with
    Surfaces = [ SurfaceProfile.anonymous; SurfaceProfile.individual ]
    RateLimit = RateLimitConfig.withOverrides
                    { PermitLimit = 100; WindowSeconds = 60; QueueLimit = 0 }   // default
                    (Map.ofList [
                        AnonymousKind, { PermitLimit = 10; WindowSeconds = 60; QueueLimit = 0 }
                        UserKind,      { PermitLimit = 200; WindowSeconds = 60; QueueLimit = 0 }
                    ]) }
```

The per-shape budgets are independent — an anonymous burst cannot consume an authenticated subject's headroom because they partition on different keys.

#### Anonymous session-id rotation

Anonymous subjects partition on `ip:` rather than `session:` deliberately. The session id is a client-controlled X-User-Id header value; an attacker can rotate it freely. Partitioning on the session would let one IP claim an arbitrary multiple of the anonymous budget by rotating headers. The IP-keyed partition holds the budget down to the real network identity.

Operators behind a proxy must set `ForwardedHeadersTrust` correctly so the partition key reflects the real client IP, not the proxy IP. Untrusted-proxy deployments collapse every anonymous caller to one partition and produce a global anonymous budget — the failure mode is denial-of-service against legitimate anonymous users, not bypass.

#### Validator coverage

`RateLimitModeValidator` enforces two compose-time rules:

- **No-policy-anywhere.** `Surfaces` contains any non-`Anonymous` profile AND `RateLimitConfig.isEnabled config.RateLimit = false` → Error (or Warning if `AcceptNoRateLimitWhenAuthRequired = true`).
- **Dead per-shape entry.** `PerShape` contains a key for a `SubjectKind` no `SurfaceProfile` in `Surfaces` admits → Warning. Most often hit when a deployment trims `Surfaces` without trimming `RateLimit.PerShape`; the warning surfaces the silent rate-limit no-op.

Both rules fire at startup; a startup with rule violations does not bind to the request port.

#### Excluded routes

`/health`, `/ready`, `/api/notifications`, `/api/ai/events` bypass rate-limiting entirely (pipeline-level, not policy-level). The exclusion is intentional — health probes must reach the deployment under load, and SSE streams are long-lived single-request shapes that the rate window does not model. Custom routes do not opt into the bypass; only the four listed paths are excluded.

### AI-cost-ceiling considerations

A deployment whose `Surfaces` contains `Anonymous` AND whose AI module admits `AnonymousKind` exposes provider tokens to unbounded anonymous traffic. The threat is **cost asymmetry** — the attacker pays nothing; the deployment pays per token. Per-shape rate-limiting (above) caps the request count; the per-request cost remains operator-owned and is the actual lever.

The hardening surface, in order of increasing operator effort:

1. **Refuse `Anonymous` on AI-using routes.** The default `ServerModule.DefaultSurfaceRequirement` is `userOrTeam`, which already excludes `AnonymousKind`. A deployment that wants to expose AI to anonymous subjects must declare the looser requirement explicitly — this is the design intent, not an accident.
2. **Per-shape rate-limit ceilings.** A `RateLimitConfig.withOverrides` entry for `AnonymousKind` keyed on IP caps the burst rate one anonymous IP can issue. The ceiling does not stop a distributed attacker; it does stop single-source abuse and forces the attacker to pay infrastructure cost proportional to the request count.
3. **Per-deployment AI provider keys, not per-user.** The default `IAIProviderFactory` resolves a single provider key for the deployment; every request bills to that key. This is the right shape for an internal-tools deployment serving authenticated subjects with already-bounded counts.
4. **Platform-Admin-issued keys.** The shipped `IPlatformAIKeyStore` substrate lets a Platform-Admin role issue deployment-bound keys via the `PlatformAIKeysAdmin` module rather than the keys living in `appsettings.json` / environment variables. The capability rotates without redeploy; revoking a leaked key is a single admin action, not a config change.
5. **Bring-your-own-key (BYOK) per authenticated subject.** A deployment can wire `IAIProviderFactory` to resolve a user-issued key from `IConfigStore` per request, shifting the cost basis from "deployment owns the key" to "calling subject owns the key". The model is only meaningful for `UserKind` / `TeamMemberKind` subjects — anonymous subjects have no persistent identity to attach a key to. BYOK and `Anonymous` AI surfaces are mutually exclusive.

The architecture's position is that anonymous AI access is operator-owned cost control, not a default the SDK provides. The [Pure-Anonymous public portal](surfaces.md#pure-anonymous-public-portal) archetype in `surfaces.md` cross-links here for that reason. A deployment that turns AI on for anonymous subjects without picking from this list is shipping an unbounded cost surface.

### Threat model — per-archetype dominant threats

The five mixed-mode archetypes from [`surfaces.md`](surfaces.md#mixed-mode-deployment-archetypes) and [the consumer migration guide](../migrations/0.X.0-platform-mode-to-surfaces.md#five-worked-examples) face different dominant threat shapes. The model is descriptive, not exhaustive — handler-level vulnerabilities (SQL injection, path traversal, deserialisation gadgets, secret-handling defects) cut across every archetype and are addressed by the same defences as any non-mixed-mode deployment.

#### Pure-Individual internal-tools deployment

`Surfaces = Surfaces.individual`. Every route requires authentication; the external attack surface is the auth provider and the deployment's static assets.

**Dominant threat shape.** Insider misuse and credential compromise — an authenticated subject acting outside the bounds the deployment expects, or an attacker who has compromised a subject's auth-provider credentials. `Subject`-derived `StorageScope` prevents cross-user reads, but does not prevent an authenticated subject from doing legitimate-shape work that the deployment did not intend.

**Defences worth verifying.** RBAC declarations on every privileged module (the `IPermissionStore` is the substrate); `AcceptHeaderAuthWhenAuthRequired = false` in production (header auth is a dev path only); audit retention long enough to investigate post-compromise; secret rotation cadence aligned with credential-compromise blast radius.

#### Federation deployment pair (two-app)

Two cooperating deployments, each `Surfaces = Surfaces.individual`, addressing each other via `PeerRoutePrefixes` carrying peer-bearer authentication. The Subject model is unchanged on each side; peer requests carry delegated authority from the other deployment and flow through `PeerBearerAuthMiddleware` rather than `SurfaceEnforcementMiddleware`.

**Dominant threat shape.** Peer trust-boundary compromise — a peer-bearer credential is signing-key-based and authorises requests as if they originated from a peer deployment. A leaked peer key is equivalent to compromise of the trusting deployment for whichever surfaces the peer routes reach.

**Defences worth verifying.** Peer credentials in `ISecretStore`, not config files; rotation cadence on the peer key matches the cross-deployment trust window; `PeerRoutePrefixes` lists only the routes the peer is expected to call — every additional prefix expands the peer's authority; both sides log peer-authenticated requests with the peer identity, not just the resolved `Subject`.

#### Pure-Anonymous public portal

`Surfaces = Surfaces.anonymous`. Every route admits anonymous subjects; no sign-in flow exists.

**Dominant threat shape.** Scraping, spam, and cost-asymmetric resource consumption (the AI-cost surface above is the most acute example). The deployment has no authority to refuse a request based on subject identity — every request is from the same trust tier.

**Defences worth verifying.** `RateLimitConfig` with an `AnonymousKind` policy keyed on IP; `ForwardedHeadersTrust` correctly configured for the deployment's proxy chain (otherwise rate-limit partitions collapse); request-body size limits (`MaxRequestBodyBytes`) sized for legitimate anonymous payloads only; AI surfaces handled per the section above; CAPTCHA / proof-of-work at the route boundary for high-cost endpoints (not SDK-provided — this is a deployment-owned middleware that runs ahead of the handler).

The CSRF carve-out (`CsrfMiddleware` skips when `AcceptedSubjects` admits `AnonymousKind` or `ClaimBearerKind`) is the right behaviour for this archetype — no session exists to bind a nonce against — but it does mean state-changing anonymous endpoints must be designed assuming any caller can submit. Idempotency keys and server-side rate-limit are the substitutes for CSRF.

#### Public-utility-with-admin

`Surfaces = Surfaces.anonymousAndIndividual`. Public calculator / lookup tool plus a small private admin in the same process.

**Dominant threat shape.** Admin-route exposure — the same process serves public traffic and admin handlers, so a routing defect, a misdeclared `SurfaceRequirement`, or a handler that reads identity from the wrong source can expose admin functionality to anonymous callers. The deployment is structurally fine; the operator's discipline carries the risk.

**Defences worth verifying.** Every admin module's `DefaultSurfaceRequirement` is at minimum `userOrTeam` (the fail-closed default catches an undeclared module, but an *explicitly* mis-declared `public_` does not trigger the validator). Client-side `Visibility = visibleToAuthenticated` on every admin module (hides the surface from the anonymous sidebar; does not gate the API — the server `SurfaceRequirement` is the gate, but `Visibility` removes the discovery surface). `SurfaceCoherenceValidator` Rule 3 (module requirement unreachable under declared `Surfaces`) catches the inverse defect — a teamScoped module under `anonymousAndIndividual` would fail startup, surfacing the misconfiguration before it ships.

The optional `IAnonymousSessionMigrator` is the hardening surface for the migration moment: an anonymous visitor who signs in lifts to `AuthenticatedUser`, and per-`userId` `SemaphoreSlim` locking in the middleware prevents the double-migration race. A deployment that wires no migrator and accepts the data-discard shape ships an acceptable state; a deployment that writes a custom migrator must honour the idempotency contract documented on `IAnonymousSessionMigrator`.

#### Public landing + team SaaS + share links

`Surfaces = [SurfaceProfile.anonymous; SurfaceProfile.multiTeam; SurfaceProfile.claimBearer]`. The full mixed-mode case — three concurrent shapes in one process.

**Dominant threat shape.** Share-token leakage. A `ShareTokenClaim` is the credential — possession authorises access to the bound resource for the lifetime + use-limit declared at issue. Tokens leak through email forwards, screenshots, accidental commits, browser history, and bookmarking; the deployment has no signal that the bearer is the intended recipient.

**Defences worth verifying.** Token lifetimes scoped to use (`DefaultLifetimeDays`, `DefaultUseLimit` on `ClaimBearerConfig`) — a token that admits one submission and then revokes by use-limit cannot be replayed. The `RevokeOnIssuerRemoved` companion (`src/ShareTokenStoreDecorators/RevokeOnIssuerRemoved/`) revokes a leaver's outstanding tokens on `MembershipChanged.Removed`, closing the "ex-employee's tokens stay valid" gap. `IShareTokenStore.ListByIssuer` lets an audit surface enumerate live tokens per issuer for forensic review. `SurfaceRequirement.claimBearerOnly` on the gated routes ensures the rest of the deployment is unreachable even with a valid token — the claim authorises the bound resource, not the deployment as a whole.

Secondary threat: cross-team team-switcher abuse. A user in N teams uses `MultiTeam` `HeaderSwitcher`; `TeamScopeResolver`'s 5-minute sliding cache on the active-team probe is short enough that revoked memberships propagate fast, but the team-membership probe itself is uncached on every request to defend against concurrent removal (design §1.3 step 2). Trust the `MembershipChanged` event flow — handler code that re-reads team membership locally races the probe and is the failure shape.

### Cross-cutting concerns

#### CSRF carve-out is per-route, not per-prefix

`CsrfMiddleware` derives its carve-out from the `SurfaceRequirementRegistry` per request: a route whose `AcceptedSubjects` admits `AnonymousKind` or `ClaimBearerKind` skips CSRF (no session exists to bind a nonce against). The pre-66 prefix-list (`AnonymousRoutePrefixes`) is retired and the carve-out cannot widen accidentally by adding a route under a "loose" prefix.

The CSRF gate is the right shape for `userOrTeam` / `teamScoped` routes. Anonymous and claim-bearer routes are intentionally outside it — anonymous-route hardening is rate-limit + body-size + idempotency-key based, not nonce-based.

#### Audit visibility per subject kind

`IAuditSink.Deliver` takes `AuditEnvelope list`. The envelope carries `Subject: AuditSubject` (`AnonymousAudit` / `UserAudit` / `TeamAudit` / `ClaimAudit`), `ScopeId`, `OccurredAt`, and the original event. Sinks declare `SchemaVersion: int` (current = `2`); the `subject_kind` tag flows through every downstream observability path.

A mixed-mode deployment serving high anonymous traffic can swamp downstream sinks. `ServerConfig.AuditSamplingPolicy` (default `AuditSamplingPolicy.none` = keep every event) lets the operator opt into per-kind sampling — typically a low rate on `AnonymousKind` events that exist only for forensic visibility, full coverage on the authenticated subject kinds where each event ties to an accountable identity. The sampling decision is deterministic per event-id (hashed to a [0,1) value, compared to the per-kind rate), so re-runs and replays produce identical sampled sets.

The point: anonymous-shape audit volume should not constrain the authenticated-shape audit fidelity. The substrate makes this declarable; the deployment owns the rate choice.

#### Validator coverage as a hardening lever

`SurfaceCoherenceValidator` refuses startup on ten rule violations. The rules are listed in `src/ToolUp.Platform.Server/Server/SurfaceCoherenceValidator.fs`; the operator-facing summary:

| Rule | Fires when | Severity |
|---|---|---|
| 1 | `Surfaces` empty | Error |
| 2 | Duplicate `SurfaceProfile` constructors | Error |
| 3 | Module `DefaultSurfaceRequirement` unreachable under declared `Surfaces` | Error |
| 4 | Per-route override unreachable | Error |
| 5 | `ClaimBearer` declared + `ShareTokenStore = NoShareTokenStore` (auto-promotion fires, but flag the dependency) | Warning |
| 6 | `EnabledShareTokenStore` + no `ClaimBearer` surface | Warning |
| 7 | `Surfaces` anonymous-only + non-`HeaderAuthProvider` auth provider registered | Warning |
| 8 | Any non-`Anonymous` surface + `ServerApp.withAuth` never called | Error |
| 9 | `withShareTokenStoreDecorator` wired + no `ClaimBearer` in `Surfaces` | Warning |
| 10 | `withShareTokenStoreDecorator` wired + no `Team` in `Surfaces` (the decorator only acts on team `MembershipChanged`) | Warning |

The validator is a deploy-time gate, not a runtime check. A deployment whose composition root produces a coherent state at startup stays coherent across the process lifetime — `Surfaces`, `SurfaceRequirementRegistry`, and the decorator chain are all immutable post-compose.

#### Share-token signing key is an operator-managed secret

`BlobShareTokenStore` signs every share token with an HMAC-SHA256 key resolved from `ISecretStore` under `_platform / share_token_signing_key`. If the key is **absent** at first use the store generates a 32-byte CSPRNG key and persists it — a convenience for dev / single-instance, **not** a production posture. Treat the key as an operator-managed secret:

- **Pre-provision it** before first boot in any production / multi-instance deployment. Set `share_token_signing_key` (in the `_platform` container of whatever `ISecretStore` is composed) to a base64url-encoded 32+ byte random value. A key the operator never set is invisible to backup and rotation governance; and across N replicas booting against an empty store, the authoritative key is whichever replica wins the first-write race.
- **Back it up** with the rest of the secret store. If it is lost / the store is re-provisioned, every previously-issued share token fails verification.
- **Rotation procedure.** Overwrite `share_token_signing_key` with a new value. Every process picks up the new key within the signing-key cache TTL (no restart needed); **all outstanding tokens then fail verification** — this is the expected, intended effect of rotation, so rotate on a cadence aligned to your token lifetimes (or deliberately, to revoke all live tokens at once).

The `share-token-signing-key-provenance` preflight validator **refuses startup** when the share-token surface is live, the deployment is production / multi-instance shaped (`ReplicaCount > 1` or `PublicBaseUrl` set), and the key is still absent. It is security-class, so the refusal is not bypassable via `SkipPreflight`. Two downgrades to `Warning`, both of which say so rather than going quiet: `ServerConfig.AcceptEphemeralShareTokenKey = true` (`TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1`) acknowledges a throwaway key — the non-breaking route for a deployment already running — and a key that is present but was **auto-generated** by the SDK is reported for as long as that holds. Provenance is recorded by the `share_token_signing_key_origin` marker the store writes beside a key it mints; deleting the marker is how an operator acknowledges adopting the key. A single-instance, non-public deployment, or one where the key is operator-provisioned, is silent. Full operator procedure: [`DEPLOYMENT.md`](../../DEPLOYMENT.md) — "Share-token signing key".

#### Secrets at rest — the posture of the store that is composed

Everything held on a user's behalf that is not data lives behind `ISecretStore`: BYOK provider keys, OAuth refresh and cached access tokens, webhook signing secrets, per-tenant credentials. Whether any of it is encrypted where it lands is a property of the **composed store**, not of a config flag — and the SDK's default store (`FileSecretStore`) writes flat JSON. `EncryptedSecretStore` is a decorator that encrypts only when `TOOLUP_SECRETS_MASTER_KEY` is set; without the key it passes plaintext through, which is the shape most deployments trip over.

Three validators cover this surface, and they ask different questions:

| Validator | Asks | Scope |
|---|---|---|
| `encrypted-secret-store-mode` | is the **master-key env var** set? | auth-requiring deployments |
| `oauth-secret-encryption-mode` | does the **composed store** encrypt? | deployments running connector OAuth flows |
| `secret-store-at-rest-posture` | does the **composed store** encrypt? | every auth-requiring deployment, whatever the store and whatever else is composed |

The third is the general one, and it **refuses startup**. It is security-class, so `SkipPreflight = true` does not bypass it. The posture it reads is what the store *declares* — the optional `ISecretStoreAtRestPosture` interface, which a companion or a consumer's own store implements to say `EncryptsAtRest` / `PlaintextAtRest` / `UnknownAtRest`. A store that declares nothing is treated as not encrypting and named as **undeclared**, not as plaintext: the refusal states what was established and no more. The `TOOLUP_SECRET_STORE` name is consulted only for an undeclaring store, because that switch records what was *asked for* — a companion that fell back to the local default still matches it.

`TOOLUP_ACCEPT_PLAINTEXT_SECRETS=1` (equivalently `ServerConfig.AcceptPlaintextSecretsWhenAuthRequired`, which the older `TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE=1` spelling also sets) lowers the refusal to a `Warning` that names the acknowledgement as the reason nothing was refused. That is the right lever when the storage medium supplies the encryption the store does not — disk FDE, an encrypting volume, a KMS-managed bucket — and the wrong one as a way past a failing boot. Full operator procedure, including master-key provisioning and rotation: [`DEPLOYMENT.md`](../../DEPLOYMENT.md) — "Secrets at rest".

## Vulnerability disclosure

Security defects in the SDK itself are reported via the process documented in [`SECURITY.md`](../../SECURITY.md) at the repo root. Deployment-time hardening defects (a `SurfaceCoherenceValidator` rule that should fire and does not, a per-shape rate-limit partition that the documented partitioning model does not match, an audit envelope shape that breaks a downstream sink) are reported the same way — they are SDK defects, not operator-tuning concerns.

## Related references

- [`../security/PLATFORM-SECURITY-RULES.md`](../security/PLATFORM-SECURITY-RULES.md) — the procurement-facing, version-stamped rule set: every structurally-enforced guarantee with an evidence pointer, plus the explicit out-of-scope list. Cite this in a questionnaire; cite this page when tuning.
- [`surfaces.md`](surfaces.md) — the Subject / SurfaceProfile / SurfaceRequirement mental model and the five deployment archetypes referenced above.
- [`auth.md`](auth.md) — `IAuthProvider` authoring; the request-resolution flow per subject kind.
- [`composition-roots.md`](composition-roots.md) — `ServerApp` composition, the validator registry, env-var contracts.
- [`portability-rules.md`](portability-rules.md) — the six portability rules that constrain `ISubjectResolver`, `IShareTokenStore.ListByIssuer`, and the per-shape `RateLimitConfig` substrate.
- [`../migrations/0.X.0-platform-mode-to-surfaces.md`](../migrations/0.X.0-platform-mode-to-surfaces.md) — the consumer migration guide; carries the five worked examples cited above by archetype.
- [`../design/mixed-mode-platform.md`](../design/mixed-mode-platform.md) — the full design pass: per-shape rate-limit substrate (§3.10), `RevokeOnIssuerRemoved` companion (§3.11), risk analysis (§4.2 — R4 mixed-mode threat surface, R6 audit volume).

# ToolUp Platform — security rules

**RulesetVersion:** `0.11.0`
**Applies to:** the `ToolUp.*` NuGet packages published at version `0.11.0`
**Status:** current
**Last reviewed:** 2026-07-28 (every evidence path verified to resolve on this date)
**Canonical path:** `/security/rules/0.11/`
**License:** Apache-2.0 — this document and the source it cites are public

---

## What this document is

ToolUp Platform is a modular F# full-stack SDK for building multi-tenant
analytical applications. This document states, rule by rule, **what the SDK
enforces** — and cites, for every rule, the file in the public source tree where
that enforcement lives.

It exists so that a compliance officer, security reviewer, or vendor-risk
assessor can answer a security questionnaire about a product built on this SDK
**without contacting the maintainers and without reading the source tree end to
end**. Each rule is written to be quotable in a questionnaire response and
verifiable by a reviewer with nothing more than a clone of the repository at the
matching version tag.

**There are no aspirational rules in this document.** A control that is planned,
partial, or unenforced is either absent or listed in
[§9 Out of scope and known limitations](#9-out-of-scope-and-known-limitations).
If you find a rule whose evidence does not support it, that is a defect — report
it via [`SECURITY.md`](../../SECURITY.md).

### What this document is *not*

- **Not a certification.** Nothing here asserts SOC 2, ISO 27001, HIPAA, or any
  other attestation. Certification is a property of a deployment and the
  organisation operating it, never of an SDK.
- **Not legal advice.** Where the document names a regulation, it describes a
  mechanism, not an obligation.
- **Not a statement about any particular deployment.** A product built on this
  SDK composes companions, adds its own application modules, and chooses its own
  configuration. This document is the floor beneath that product, not the whole
  of it. See [§How to apply this to a deployment](#how-to-apply-this-to-a-deployment).

---

## How to read a rule

Every rule has three parts and one label.

> **RULE-ID — Statement of the rule.**
> *Enablement:* one of the three labels below.
> What it means in practice.
> **Evidence:** repository-relative paths.

**Enablement labels** — a reviewer should read these first, because they decide
whether a rule is a property of the SDK or a configuration item to verify in the
deployment under review:

| Label | Meaning | What a reviewer should do |
|---|---|---|
| **Always on** | Structural. Cannot be turned off by configuration, and in several cases cannot be turned off even by the preflight-skip escape hatch. | Accept as a property of the SDK version. |
| **Default on** | Active in a stock composition; an operator can weaken it, and doing so is an explicit, named configuration act. | Ask the deployment to evidence that it has not been weakened. |
| **Opt-in** | Substrate that exists and is contract-tested, but is inert until the deployment composes it. | Ask the deployment whether it is composed. The rule says what you get if it is. |

The **Opt-in** label is not a hedge — it is a deliberate design commitment. The
SDK's Guiding Principles 11 and 13 require that a new capability defaults to its
prior behaviour and that a deployment which does not use a subsystem pays nothing
for it. The consequence for a reviewer is that an opt-in control is a question
for the *vendor of the product*, not for the SDK.

### Evidence conventions

- Every path is **repository-relative** and resolvable in the public
  Apache-2.0 repository at the tag matching this RulesetVersion.
- A `Phase NN` label denotes a unit of shipped work; the label appears in the
  repository's own commit messages and migration documents, so
  `git log --oneline --grep "Phase NN"` resolves it. The **file path** is the
  primary evidence; the phase label is a convenience for locating the change
  that introduced it.
- `docs/migrations/*.md` entries are the consumer-facing change notes. Where a
  rule changed a default, the migration entry states exactly what changed and
  what a deployment must do — these are useful to a reviewer assessing whether a
  deployment on an older version has the control at all.

---

## How these rules are verified

The SDK's security posture is machine-checkable in four independent ways. A
reviewer who wants to go beyond reading this document should use these rather
than auditing source by hand.

### Startup preflight — misconfiguration refuses to boot

`IConfigValidator` implementations run once at the end of composition, **before
the server binds its port**. An `Error`-severity finding raises
`ConfigPreflightFailedException` and the process never serves a request. There
are approximately fifty shipped validators; the security-relevant ones are cited
individually throughout this document.

The escape hatch matters and is deliberately narrow: `SkipPreflight` suppresses
only the **external-probe** class of validators (those that reach out to a
network dependency). Validators marked `ISecurityClassValidator` or
`IStructuralClassValidator` run regardless — a deployment cannot skip its way
past a security rule.

**Evidence:** `src/ToolUp.Platform.Core/Shared/IConfigValidator.fs` ·
`src/ToolUp.Platform.Server/Server/ConfigValidatorAggregator.fs` ·
`src/ToolUp.Platform.Server/Server/Compose/ComposeConfigValidators.fs` ·
`docs/platform/composition-roots.md` (§"What `SkipPreflight` skips — and what it
never skips") · `docs/migrations/281-composition-preflight.md` ·
`docs/migrations/327-security-class-validator-property.md`

### Contract test packs — one conformance bar for every implementation

Every extension-point interface ships an executable contract pack. Any
implementation — first-party, third-party, or one written by the deployment —
can be run against the same bar. Eighty-nine packs ship at this version,
including `IPermissionStoreContract`, `ISubjectResolverContract`,
`IShareTokenStoreContract`, `ISecretStoreContract`, `IAuditSinkContract`,
`IDataSubjectRequestContract`, `IClientToolAuthorizerContract`,
`StoreIdSanitisingContract`, and `FailClosedContract`.

**Evidence:** `src/ToolUp.Platform.Tests/Contracts/` (89 packs) ·
`docs/platform/portability-rules.md` (§"Conformance bar")

### Machine-readable manifests, gated by golden baselines in CI

Four aspects of a composition are emitted as machine-readable JSON and diffed
against a committed baseline in the test suite. The one that matters most to a
security reviewer is the **authorization-surface manifest**: it enumerates every
externally reachable entry point with its normalised authorization requirement,
classified as `ExplicitRequirement`, `InheritedDefaultDeny`, or
`AnonymousReachable`. **A change that makes a new endpoint anonymously reachable
fails the build**, so an accidental exposure cannot ship quietly.

**Evidence:** `src/ToolUp.Platform.Server/Server/AuthorizationSurface.fs` ·
`composition-baselines/authorization-surface-baseline.json` ·
`composition-baselines/composition-baseline.json` ·
`composition-baselines/data-footprint-baseline.json` ·
`composition-baselines/event-topology-baseline.json` ·
`src/ToolUp.Platform.Tests/Composition/CompositionBaselineTests.fs` ·
`docs/migrations/438-authorization-surface.md`

### The composition rule set is itself published as versioned data

The invariants the composition validator enforces are not buried in imperative
code — they are declared as data (rule code, severity, class, description, pure
evaluator) and exported as a manifest. `classifiedRuleManifest` additionally
publishes each rule's **class**, which is precisely what an external checker
needs in order to know which invariants `SkipPreflight` can never disable.

Each rule carries its own semantic version, and corrections to a published rule
are distributed through a committed **errata channel** rather than by silently
editing history. The errata file currently ships empty — no rule has yet needed
a correction — which is itself the useful signal.

**Evidence:** `src/ToolUp.Platform.Server/Server/CompositionValidator.fs`
(`ruleManifest`, `classifiedRuleManifest`) ·
`src/ToolUp.Platform.Server/Server/CompositionRuleVersioning.fs` (per-rule
`RuleVersion` / `RuleVersionBump` + errata loader) ·
`rule-errata/README.md` · `rule-errata/composition-rule-errata.json` ·
`composition-baselines/rule-manifest-baseline.json` ·
`src/ToolUp.Platform.Server/Server/DataFootprint.fs` and
`src/ToolUp.Platform.Server/Server/EventTopology.fs` (two further preflight
families exporting their own classified manifests) ·
`docs/migrations/294-invariant-rule-manifest.md` ·
`docs/platform/composition-roots.md`

### Optional shift-left analyzer

A Roslyn-equivalent F# analyzer reproduces the runtime authorization classifier's
matching at compile time, so an unclassified API method is a build error in CI
rather than a startup refusal in staging.

**Evidence:** `src/ToolUp.Remoting.Analyzers/Analyzer.fs` ·
`src/ToolUp.Remoting.Analyzers/Recognition.fs` (diagnostic `TUR0001`) ·
`docs/migrations/195-compile-time-analyzer-auth-audit-attributes.md`

---

## 1. Tenant isolation

The SDK's central structural claim: a handler cannot read another tenant's data
by forgetting to filter, because it never had the opportunity to filter in the
first place. Isolation is carried by the type system and the scope resolver, not
by a "remember to add the WHERE clause" convention. This is Guiding Principle 4.

> **TI-1 — The storage scope is derived from the resolved subject; handler code
> cannot synthesise it.**
> *Enablement:* Always on.
> Each request resolves to a typed `Subject` (one of four kinds: anonymous,
> user, team member, claim bearer). A `StorageScope` is derived from that
> `Subject` by the resolver and flows to the stores. A handler receives the
> scope; it cannot construct one for a tenant other than the one the request
> resolved to, and no shipped store method accepts an unresolved scope.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/Types/Subject.fs` (the four
> subject kinds) ·
> `src/ToolUp.Platform.Core/Shared/Types/StorageScope.fs` ·
> `src/ToolUp.Platform.Core/Shared/Interfaces/ISubjectResolver.fs` ·
> `src/ToolUp.Platform.Server/Server/Scope/DefaultSubjectResolver.fs` ·
> `src/ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs` (the four
> shipped resolvers and the container-prefix construction) ·
> `src/ToolUp.Platform.Server/Server/Middleware.fs` (`Middleware.fromSubject`) ·
> `src/ToolUp.Platform.Tests/Contracts/ISubjectResolverContract.fs` ·
> `docs/platform/surfaces.md`

> **TI-1b — Membership caches are invalidated by event, and a deprovisioned
> tenant's cache entries are evicted, so a warm cache cannot resolve a revoked
> membership back into a live scope.**
> *Enablement:* Always on.
> The active-team probe is cached with a short sliding TTL for latency, but
> invalidation is event-driven on membership change rather than left to TTL
> expiry, and a tenant-lifecycle hook evicts the affected entries on
> deprovisioning. Handler code that re-reads team membership locally races the
> probe and is the documented failure shape — the substrate's own path is the
> correct one.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs`
> (`InvalidateUser`, `MembershipChanged` handling) ·
> `src/ToolUp.Platform.Server/Server/Lifecycle/MembershipCacheLifecycle.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/ITenantLifecycleContract.fs` ·
> `docs/platform/security.md` (§"Public landing + team SaaS + share links",
> cross-team switcher note)

> **TI-2 — Caller-supplied identifiers that become storage keys are validated at
> the store seam, not at the caller.**
> *Enablement:* Always on.
> Every caller-supplied id that would become a segment of a blob key —
> `teamId`, `userId`, `scopeId`, `tokenId` — is validated before any read or
> write. Traversal-shaped ids (`../`, backslashes, NUL and control characters,
> whitespace), the reserved `_platform` scope, and Windows reserved device names
> are **rejected**, not sanitised-and-continued. The guarantee is enforced by
> decorators at the `ITeamStore`, `IPermissionStore`, and `IShareTokenStore`
> parameter seams, so it holds regardless of which caller reached the store.
> **Evidence:** `src/ToolUp.Platform.Server/Server/Scope/StoreIdSanitising.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/StoreIdSanitisingContract.fs` ·
> `docs/migrations/131-identity-sanitisation-store-seam.md` (Phase 131)

> **TI-3 — Rate-limit partitions are derived from the resolved subject kind, and
> the anonymous partition is not client-controllable.**
> *Enablement:* Default on for any deployment admitting a non-anonymous surface
> (see AN-1); the specific budgets are operator-chosen.
> Partition keys are implied by subject kind, not authored: `ip:{clientIp}` for
> anonymous, `user:{userId}`, `team:{teamId}`, `token:{tokenId}`. Anonymous
> traffic deliberately partitions on IP rather than on the client-supplied
> session header, because a client can rotate a header freely and would
> otherwise claim an arbitrary multiple of the anonymous budget. Per-shape
> budgets are independent, so an anonymous burst cannot consume authenticated
> headroom.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/SDK.Shared.fs`
> (`module RateLimitPolicy`, `partitionFor`) ·
> `src/ToolUp.Platform.Server/Server/RateLimiting.fs` (builds the partitioned
> limiter from the resolved subject) ·
> `src/ToolUp.Platform.Server/Server/RateLimitMiddleware.fs` ·
> `src/ToolUp.Platform.Core/Shared/IRateLimiter.fs` (`RateLimitKey` — identity
> by value, for the outbound provider partition) ·
> `src/ToolUp.Platform.Server/Server/InProcessRateLimiter.fs` ·
> `src/ToolUp.Platform.Server/Server/IRateLimitStore.fs` and
> `src/RateLimit/Redis/RedisRateLimitStore.fs` (the distributed-counter seam and
> a shipped implementation) ·
> `src/ToolUp.Platform.Tests/Contracts/IRateLimiterContract.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IRateLimitStoreContract.fs` ·
> `docs/platform/security.md` (§"Per-shape rate-limiting guidance")

> **TI-4 — A deployment with no rate limiter and an authenticated surface is
> refused at startup unless the operator explicitly accepts the gap.**
> *Enablement:* Always on (structural-class validator).
> `RateLimitModeValidator` raises an `Error` when `Surfaces` contains any
> non-anonymous profile and no rate-limit policy is configured. The operator can
> proceed only by setting `AcceptNoRateLimitWhenAuthRequired = true`, which
> downgrades the finding to a warning — an explicit, greppable acceptance rather
> than a silent default.
> **Evidence:** `src/ToolUp.Platform.Server/Server/RateLimitModeValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/RateLimitConfigValidator.fs` ·
> `docs/platform/security.md` (§"Validator coverage")

> **TI-5 — An in-memory rate limiter paired with a scale-out deployment is
> refused or warned at startup.**
> *Enablement:* Always on.
> Distribution capability is declared as data (`IsDistributed: bool`) rather
> than inferred, and a validator refuses the combination of a single-process
> limiter with a multi-replica shape — the failure mode where a documented
> ceiling is silently multiplied by the replica count.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/IShareTokenRateLimiter.fs` ·
> `src/ToolUp.Platform.Server/Server/RateLimiterInstanceValidator.fs` ·
> `docs/migrations/136-share-link-token-leak-hardening.md` (Phase 136)

> **TI-6 — Per-tenant cryptographic separation is available, and destroying a
> tenant's key renders that tenant's data unreadable — on the replica serving the
> request when the call returns, and across the remaining replicas at minute
> grain.**
> *Enablement:* Opt-in.
> See [EN-3](#4-encryption) for the propagation mechanism and the two startup
> preflight checks that cover a scale-out deployment. Listed here because
> crypto-shred is the isolation control most often asked about in vendor
> questionnaires — and because the honest timing answer to that question is a
> bounded window, not "instantly", whenever more than one replica is serving.

---

## 2. Authentication

The SDK does not implement an identity provider. It consumes one, and enforces
the properties of the *edge* — which subject a request resolves to, how the
credential is carried, and what the deployment is refused permission to do
carelessly.

> **AN-1 — A deployment declaring any authenticated surface cannot start without
> an authentication provider.**
> *Enablement:* Always on.
> `SurfaceCoherenceValidator` refuses startup on ten declared coherence
> violations. Rule 8 is the load-bearing one here: any non-anonymous surface
> combined with a composition root that never wired an auth provider is an
> `Error`, and the process does not bind its port.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/SurfaceCoherenceValidator.fs` ·
> `docs/platform/security.md` (§"Validator coverage as a hardening lever" —
> the full ten-rule table)

> **AN-2 — The development header-auth provider cannot be the production posture
> silently.**
> *Enablement:* Always on.
> `HeaderAuthProvider` trusts a request header for identity and exists for local
> development only. A validator surfaces its use against an authenticated
> surface; the operator must set `AcceptHeaderAuthWhenAuthRequired` to proceed.
> A related validator refuses silent auto-bootstrap of a development
> administrator in a deployment shaped for production.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/HeaderAuthProviderModeValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/AutoBootstrapDevAdminModeValidator.fs` ·
> `src/ToolUp.Platform.Core/Shared/Interfaces/IAuthProvider.fs` (the
> vendor-neutral contract every provider satisfies) ·
> `docs/migrations/129-secure-by-default-request-edge.md` (Phase 129a) ·
> `docs/platform/auth.md` · `docs/companions/auth-providers.md`

> **AN-3 — An OIDC provider advertising a non-loopback plaintext JWKS endpoint is
> refused.**
> *Enablement:* Always on (when the OIDC provider is composed).
> The discovery document's `jwks_uri` must be `https://`, or `http://` on
> loopback. A provider advertising plaintext over the network — a
> misconfiguration or an active downgrade attempt — is refused rather than
> silently trusted for signing-key retrieval.
> **Evidence:** `src/AuthProviders/Oidc/OidcAuthProvider.Jwks.fs` ·
> `src/AuthProviders/OidcClient/OidcDiscovery.fs` ·
> `docs/migrations/134-discovered-jwks-uri-https.md` (Phase 134)

> **AN-4 — OIDC configuration completeness and audience binding are checked at
> startup.**
> *Enablement:* Always on (when the OIDC provider is composed).
> A token accepted without audience binding is a token issued for another
> relying party. The audience-binding validator and the configuration
> completeness validator both run in preflight.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/OidcAudienceBindingValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/OidcConfigCompletenessValidator.fs` ·
> `src/AuthProviders/Oidc/OidcAuthProvider.Jwt.fs` (signature, issuer,
> audience, and expiry validation) ·
> `src/AuthProviders/Oidc/OidcAuthValidator.fs` ·
> `src/AuthProviders/OidcClient/OidcIdTokenValidator.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IAuthProviderContract.fs`

> **AN-5 — The session credential is held in a server-set `HttpOnly` cookie, not
> in JavaScript-readable storage.**
> *Enablement:* Default on.
> Prior to this control the client mirrored the bearer token into
> `localStorage` and a script-readable cookie, which makes any cross-site
> scripting defect a credential-theft defect. The token is now set server-side
> as `HttpOnly`.
> **Evidence:** `src/ToolUp.Platform.Server/Server/AuthSessionHandler.fs` ·
> `docs/migrations/133-httponly-auth-cookie.md` (Phase 133)

> **AN-6 — State-changing API requests from session-bearing subjects carry a
> synchroniser CSRF token, and an internet-facing authenticated deployment
> cannot leave the check off by accident.**
> *Enablement:* Default on; refusal at startup for the internet-facing shape.
> `CsrfMiddleware` mints a cryptographically sealed, session-bound token,
> requires it on every state-changing `/api/*` request, and fixed-time-compares
> it; a `SameSite=Strict` cookie is set alongside as independent defence in
> depth. The
> carve-out for anonymous and claim-bearer routes is derived **per route** from
> the surface requirement registry, not from a prefix list, so it cannot widen
> accidentally when a route is added under a "loose" prefix. The preflight
> finding for a missing CSRF configuration on an authenticated internet-facing
> deployment was escalated from warning to error, with a typed opt-out for the
> genuinely-internal case.
> **Evidence:** `src/ToolUp.Platform.Server/Server/CsrfMiddleware.fs` ·
> `src/ToolUp.Platform.Server/Server/CsrfHardeningValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/CsrfDefaultModeValidator.fs` ·
> `docs/migrations/129-secure-by-default-request-edge.md` (Phase 129c/129d) ·
> `../../SECURITY.md` (§"CSRF synchroniser token — client request-guard", which
> states the threat model and the guard's deliberate narrowness)

> **AN-7 — A response-header security floor is stamped on every response.**
> *Enablement:* Always on (the floor); strict policy is default-on-configurable.
> A baseline of security headers is emitted unconditionally. The stricter
> posture (`StrictSecurityHeaders`) adds Content-Security-Policy, HSTS,
> `X-Frame-Options`, `X-Content-Type-Options`, and `Referrer-Policy`, and a
> validator reports the gap when the deployment shape warrants it. Per-request
> nonce and per-content hash CSP sources are available for deployments running a
> strict CSP over their own server-rendered inline content.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/SecurityHeadersMiddleware.fs` ·
> `src/ToolUp.Platform.Server/Server/SecurityHeaders.fs` ·
> `src/ToolUp.Platform.Server/Server/SecurityHeadersValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/CspMiddleware.fs` ·
> `src/ToolUp.Platform.Server/Server/ICspContributor.fs` (the contributor
> extension point) ·
> `src/ToolUp.Platform.Server/Server/SecurityHardening.fs` (folds every
> contributor plus the `'self'` baseline into one policy at compose time, so a
> component cannot widen the policy at request time) ·
> `docs/migrations/129-secure-by-default-request-edge.md` (Phase 129d) ·
> `docs/migrations/156-csp-nonce-hash.md` (Phase 156)

> **AN-8 — A CORS policy combining credentials with a wildcard origin refuses to
> boot, and the refusal survives the preflight-skip escape hatch.**
> *Enablement:* Always on. No opt-out.
> `AllowCredentials = true` together with `"*"` in the origin list is refused
> before the CORS policy is registered. The validator implements
> `ISecurityClassValidator`, so `SkipPreflight` does not suppress it.
> **Evidence:** `src/ToolUp.Platform.Server/Server/CorsConfigValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/Compose/ComposeRuntimeServices.fs`
> (`assertCorsCredentialsCompatible`) ·
> `src/ToolUp.Platform.Tests/InProcess/CorsCredentialsWildcardBootTests.fs` ·
> Phase 462

> **AN-9 — PKCE enforcement and peer-JWT audience binding are load-bearing, not
> inert.**
> *Enablement:* Default on (both were present-but-unwired before this change).
> The OAuth credential flow enforces PKCE, and cross-deployment peer
> authentication binds the JWT audience to the local peer identity, so a token
> minted for one peer cannot be replayed against another.
> **Evidence:** `src/ToolUp.Platform.Server/Server/PeerBearerAuthMiddleware.fs` ·
> `src/ToolUp.Platform.Server/Server/PeerBearerConfigValidator.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IPeerBearerAuthContract.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IOAuthCredentialFlowContract.fs` ·
> `docs/migrations/130-pkce-and-peer-audience.md` (Phase 130)

> **AN-10 — Responses reached via a share-link token suppress referrer and
> caching.**
> *Enablement:* Always on (when the share-token surface is composed).
> A share token is a bearer secret that frequently travels in a URL. Every
> response reached via one is stamped `Referrer-Policy: no-referrer` and
> `Cache-Control: no-store`, blunting leakage through the `Referer` header,
> browser history, and intermediary caches. Per-route overrides are preserved.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/ShareTokenAuthMiddleware.fs` ·
> `docs/migrations/136-share-link-token-leak-hardening.md` (Phase 136 part 1)

> **AN-11 — The share-token signing key is treated as an operator-managed
> secret, and an unmanaged key in a production-shaped deployment is surfaced.**
> *Enablement:* Always on (the warning); key provisioning is operator work.
> The store signs tokens with an HMAC-SHA256 key from `ISecretStore`. If absent
> it generates one — a development convenience, not a production posture,
> because a key the operator never set is invisible to backup and rotation
> governance and, across replicas, is decided by a first-write race. A preflight
> validator emits a warning when the share-token surface is live, the deployment
> is production- or multi-instance-shaped, and the key is still absent.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/ShareTokenSigningKeyProvenanceValidator.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IShareTokenStoreContract.fs` ·
> `docs/platform/security.md` (§"Share-token signing key is an operator-managed
> secret" — includes the rotation procedure and its intended blast radius)

> **AN-12 — A departing team member's outstanding share tokens can be revoked
> automatically on membership removal.**
> *Enablement:* Opt-in (a store decorator).
> The decorator revokes a leaver's outstanding tokens on a membership-removed
> event, closing the "ex-employee's links stay live" gap. `ListByIssuer` on the
> store lets an audit surface enumerate live tokens per issuer for forensic
> review.
> **Evidence:**
> `src/ShareTokenStoreDecorators/RevokeOnIssuerRemoved/RevokeOnIssuerRemovedStore.fs` ·
> `src/ShareTokenStoreDecorators/RevokeOnIssuerRemoved/README.md` ·
> `docs/platform/security.md` (§"Public landing + team SaaS + share links")

> **AN-13 — Anonymous-session migration binds the session to its owner.**
> *Enablement:* Opt-in (active only where a real session migrator is composed).
> When an anonymous visitor signs in, their prior anonymous work can be lifted
> into the authenticated account. Ownership binding closes the horizontal
> data-theft path in that lift, and per-user locking prevents a
> double-migration race. A deployment on the default no-op migrator is
> unaffected and emits no binding cookie.
> **Evidence:**
> `src/ToolUp.Platform.Core/Shared/Interfaces/IAnonymousSessionMigrator.fs` ·
> `src/ToolUp.Platform.Server/Server/AnonymousSessionMigrationMiddleware.fs` ·
> `docs/migrations/135-anonymous-session-binding.md` (Phase 135)

---

## 3. Authorisation

The design position is that authorisation is a property of the **dispatcher**,
not of handler bodies. A handler that forgets to check is the defect class this
section eliminates structurally.

> **AZ-1 — Every API method must carry exactly one authorisation classification,
> or the server refuses to start.**
> *Enablement:* Always on, for every record-shaped API type.
> Each method on an API record declares its access requirement by attribute —
> `[<RequiresRole "...">]`, `[<RequiresClaim "...">]`, `[<TenantScoped>]`,
> `[<AllowAnonymous>]`, or `[<PublicEndpoint>]`. A startup classifier walks
> every registered API record; an unclassified method produces a refusal naming
> the record and the offending field, and the process does not start. The
> classifier is armed for every record type including internal and private ones,
> so an unannotated record cannot slip past by being invisible to reflection
> defaults.
> This is the rule that converts "the module author remembered the guard" into a
> property the deployment cannot ship without.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/AuthAttributes.fs` (the
> tier-shared attribute definitions consumers annotate with) ·
> `src/ToolUp.Platform.Server/Server/Remoting/Auth.fs` (`AuthClassifier`, the
> server-tier attribute originals, and `unclassifiedException`) ·
> `src/ToolUp.Platform.Server/Server/Remoting/Giraffe/GiraffeAdapter.fs` (the
> startup refusal site) · `src/ToolUp.Platform.Server/Server/Api.fs` (`Api.make`
> arms the classifier) ·
> `src/ToolUp.Platform.Tests/InProcess/AuthorizationTests.fs` ·
> `docs/migrations/69d-authorization-metadata.md` (Phase 69d)

> **AZ-2 — Authorisation fails closed. An unclassified or unmatched method
> denies; it never falls through to allow.**
> *Enablement:* Always on.
> The runtime resolution of an unclassified method is `Deny`, not a permissive
> default — belt and braces behind AZ-1's startup refusal. A single
> classification map is pinned across API registrations so two registrations
> cannot disagree, and an unresolvable entry is treated as unclassified, i.e.
> denied. The property is covered by a dedicated adversarial test suite and a
> contract pack an external implementation can run against itself.
> **Evidence:** `src/ToolUp.Platform.Server/Server/Remoting/Auth.fs` ·
> `src/ToolUp.Platform.Server/Server/RemotingHelpers.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/FailClosedContract.fs` ·
> `src/ToolUp.Platform.Tests/InProcess/AdversarialFailClosedTests.fs` ·
> `docs/migrations/132-fail-closed-authz-defaults.md` (Phase 132)

> **AZ-3 — Every API route is gated against a declared surface requirement, and
> the default requirement excludes anonymous subjects.**
> *Enablement:* Always on.
> `SurfaceEnforcementMiddleware` gates every `/api/*` route against its declared
> `SurfaceRequirement`. A module that declares nothing inherits the default,
> which admits authenticated users and team members only. Exposing a route to
> anonymous callers is therefore an explicit declaration a reviewer can grep
> for, never an omission.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/SurfaceEnforcementMiddleware.fs` ·
> `src/ToolUp.Platform.Core/Shared/Types/SurfaceRequirement.fs` ·
> `src/ToolUp.Platform.Core/Shared/Types/SurfaceProfile.fs` ·
> `src/ToolUp.Platform.Tests/InProcess/AuthorizationSurfaceTests.fs` ·
> `docs/platform/surfaces.md`

> **AZ-4 — A composition whose declared surfaces and declared route requirements
> are incoherent refuses to start.**
> *Enablement:* Always on.
> Ten coherence rules are checked at startup: empty or duplicated surface
> declarations, a module requirement unreachable under the declared surfaces, a
> per-route override that can never match, an authenticated surface with no auth
> provider, and share-token dependencies declared without their substrate. Four
> are errors that stop the boot; six are warnings.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/SurfaceCoherenceValidator.fs` ·
> `docs/platform/security.md` (§"Validator coverage as a hardening lever")

> **AZ-5 — Module authorisation runs through a single choke point; modules do not
> check permissions themselves.**
> *Enablement:* Default on (applied by the module-registration helper).
> The permission model is three-layered: a deployment-wide platform role, a
> per-team role (`Owner` / `Admin` / `Member`), and a per-team-per-module
> permission with an explicit implication hierarchy declared in one place. Module
> API handlers are wrapped by the permission-guarded API helper, which resolves
> the caller's permissions before invoking the function. This is the only
> sanctioned authorisation choke point in module code — modules do not check
> permissions themselves, and the wrap is applied by the module-registration
> helper rather than by each module author remembering it.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/Types/PermissionTypes.fs`
> (the permission values and `ModulePermission.implies`) ·
> `src/ToolUp.Platform.Core/Shared/Types/RoleTypes.fs` and
> `src/ToolUp.Platform.Core/Shared/Types/TeamRoles.fs` (the role predicates,
> shared between server and browser tiers so the two cannot disagree) ·
> `src/ToolUp.Platform.Server/Server/Scope/PermissionStore.fs` ·
> `src/ToolUp.Platform.Server/Server/Teams/TeamManagement.fs` ·
> `src/ToolUp.Platform.Server/Server/RemotingHelpers.fs`
> (`makePermissionGuardedApi`) ·
> `src/ToolUp.Platform.Tests/Contracts/IPermissionStoreContract.fs` ·
> `docs/platform/auth.md` (§"Permissions + roles")

> **AZ-6 — Platform-administrator paths are gated by role, and emergency token
> access is constant-time compared.**
> *Enablement:* Always on.
> Administrative operations — assigning administrators, destroying encryption
> keys, writing to platform-owned stores — gate on the platform-administrator
> role. The emergency administrative token path uses constant-time comparison. A
> multi-instance coherence validator checks the administrative configuration is
> consistent across replicas.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/PlatformAdminAuthorizationMiddleware.fs` ·
> `src/ToolUp.Platform.Server/Server/AdminTokenValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/MultiInstanceAdminCoherenceValidator.fs` ·
> `docs/migrations/567-admin-area-separate-surface.md`

> **AZ-7 — The authorisation surface is emitted as a machine-readable manifest and
> diffed against a committed baseline in CI.**
> *Enablement:* Always on in the SDK's own CI; opt-in emission for a deployment.
> The manifest enumerates every externally reachable entry point across four
> seams — route prefixes, exact route overrides, API record fields, and AI tool
> plus event-handler registrations — each with its normalised requirement and a
> three-valued classification. It reads the same classifier the runtime uses, so
> the manifest and the enforcement cannot drift apart. A delta report separates
> `Weakened` from `Strengthened` changes and produces a severity verdict.
> For a reviewer this is the single most useful artefact in the repository: it is
> the answer to "enumerate every anonymously reachable endpoint", computed rather
> than asserted.
> **Evidence:** `src/ToolUp.Platform.Server/Server/AuthorizationSurface.fs` ·
> `composition-baselines/authorization-surface-baseline.json` ·
> `src/ToolUp.Platform.Tests/Composition/CompositionBaselineTests.fs` ·
> `docs/migrations/438-authorization-surface.md` (Phase 438)

> **AZ-8 — Action authorisation is default-deny by contract, and an
> implementation is forbidden from failing open.**
> *Enablement:* Opt-in (the seam); default-deny is a property of the contract
> wherever it is composed.
> Host-mediated and AI-originated actions can be gated by an action authoriser
> keyed on a typed action descriptor and access context. The contract is
> explicit that an implementation must **not** throw — an unavailable permission
> store resolves to `Deny`, never to "allow because the check errored", which is
> the classic fail-open defect. The shipped implementation resolves against the
> permission store and denies any descriptor naming a scope other than the
> caller's. Denials are audited.
> **Evidence:**
> `src/ToolUp.Platform.Core/Shared/Types/ActionAuthorization.fs` (the
> default-deny contract and the must-not-throw rule) ·
> `src/ToolUp.Platform.Server/Server/Scope/PermissionStoreActionAuthorizer.fs` ·
> `src/ToolUp.Platform.Server/Server/Scope/HostActionAuditHook.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IActionAuthorizerContract.fs` ·
> `docs/platform/action-authorizer.md` ·
> `docs/migrations/113-action-authorizer.md`

> **AZ-9 — Field-level classification can redact classified values before they
> leave the server, layered on top of role-based access.**
> *Enablement:* Opt-in.
> Where role-level access is too coarse — a caller may see a record but not one
> of its fields — a classification gate resolves each field to `Allow` or
> `Redact` against the caller's context, using a pluggable field classifier. It
> composes with, rather than replaces, the permission model, and it is the
> mechanism that keeps classified values out of a model prompt
> ([§8](#8-ai-safety)).
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/Classification/ClassificationGate.fs` ·
> `src/ToolUp.Platform.Server/Server/Classification/IFieldClassifier.fs` ·
> `src/ToolUp.Platform.Server/Server/Classification/DefaultFieldClassifier.fs`

> **AZ-10 — A companion's effect, determinism, and distribution posture can be
> declared as typed data and enforced by a default-deny gate.**
> *Enablement:* Opt-in (the gate); the descriptor's identity value is a no-op, so
> an undeclared component changes nothing.
> Each companion may declare a capability descriptor on three orthogonal axes.
> The composition capability gate permits an invocation only when the declared
> capability is within the permitted envelope, and every denial is observable.
> The descriptor makes the file-header prose about "dev-only" versus
> "distributed-ready" machine-checkable rather than advisory.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/CompanionCapability.fs` ·
> `src/ToolUp.Platform.Server/Server/CompositionCapabilityGate.fs` ·
> `src/ToolUp.Platform.Tests/InProcess/CompositionCapabilityGateTests.fs` ·
> `docs/migrations/282-companion-capability.md` ·
> `docs/migrations/300-composition-capability-sandbox.md`

---

## 4. Encryption

> **EN-1 — HTTPS redirection and forwarded-header trust are explicit
> configuration, validated at startup.**
> *Enablement:* Default on (forwarded-header trust); operator-set (HTTPS
> redirection).
> Behind a TLS-terminating proxy, forwarded-header trust is what lets secure
> cookie scoping and absolute-URL construction see the originating scheme. A
> validator checks the setting against the deployment shape, because getting it
> wrong silently degrades both cookie security and the anonymous rate-limit
> partition.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/ForwardedHeadersTrustValidator.fs` ·
> `docs/platform/auth.md` (§"Hardening checklist for production")
>
> *Transport encryption itself is terminated by the deployment's infrastructure —
> see [§9](#9-out-of-scope-and-known-limitations).*

> **EN-2 — Application-tier envelope encryption at rest is available for every
> blob-backed store, transparently.**
> *Enablement:* Opt-in (a storage decorator).
> The decorator wraps any `IBlobStorage` and applies AES-256-GCM envelope
> encryption. The envelope is `[Magic:4][KeyIdLen:1][KeyId:N][Nonce:12][Tag:16]
> [Ciphertext:M]`, using the BCL `AesGcm` primitive: a 256-bit key, a 12-byte
> nonce, and a 16-byte AEAD authentication tag. The envelope is opaque to the
> underlying storage, so the control composes with, rather than replaces,
> bucket-level or provider-managed encryption.
> **Evidence:** `src/ToolUp.Platform.Server/Server/EncryptedBlobStorage.fs` ·
> `src/ToolUp.Platform.Core/Shared/EncryptionTypes.fs` ·
> `src/ToolUp.Platform.Server/Server/Compose/ComposeEncryption.fs` (applies the
> decorator to the resolved storage so every downstream store inherits it —
> a store cannot opt out by construction) ·
> `docs/platform/storage.md` (§"Encryption at rest (application-level)")

> **EN-3 — Per-tenant key resolution supports cryptographic erasure
> (crypto-shred), gated by the platform-administrator role.**
> *Enablement:* Opt-in (select the per-scope key resolver).
> With the per-scope resolver, each tenant's data is encrypted under its own key.
> Destroying that key makes the tenant's data unreadable — the key id in the
> envelope no longer resolves — which is materially faster and more complete than
> walking and deleting every object. **Timing is stated precisely rather than as
> "immediate": the erasure is complete on the replica serving the request when the
> call returns, and completes across the remaining replicas at minute grain.** A
> destroy broadcasts a key-destroyed envelope over the notification channel so
> every other replica drops its cached copy of the key; the propagation window is
> the configured channel companion's fanout latency. **Channel wiring is optional
> on a single replica and required on more than one, and the difference is
> enforced rather than documented:** a deployment that *declares* more than one
> replica — in configuration or through the environment; either counts — is
> **refused at startup** when the broadcast cannot reach a sibling, whether
> because the resolver was never wired to a channel or because the wired channel
> is the in-process default that cannot cross a process boundary. A deployment
> whose *shape* implies several replicas without declaring a count is warned
> rather than refused, because a single-replica multi-tenant deployment is
> legitimate and its fanout is correctly a no-op. The administrative endpoint
> is role-gated, and four audit events fire — key creation, rotation, destruction,
> and one destruction-acknowledgement per replica that drops the key, so the trail
> evidences fleet-wide erasure rather than only the replica that served the
> request. Each carries the acting user, the target scope, and a server-side
> timestamp.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/PerScopeKeyResolver.fs` (one key per
> scope, persisted through `ISecretStore`) ·
> `src/ToolUp.Platform.Server/Server/SingleKeyResolver.fs` (the platform-wide
> alternative, for deployments where per-tenant shred is not a requirement) ·
> `src/ToolUp.Platform.Server/Server/IBlobEncryptionKeyResolver.fs` ·
> `src/ToolUp.Platform.Server/Server/EncryptionAdminHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/Lifecycle/EncryptionKeyProvisionLifecycle.fs`
> and `src/ToolUp.Platform.Server/Server/Lifecycle/EncryptionKeyLifecycle.fs`
> (mint on tenant provision, shred on deprovision) ·
> `src/ToolUp.Platform.Server/Server/PerScopeKeyResolverDistributedValidator.fs`
> (refuses a declared scale-out deployment whose shred cannot reach a sibling —
> either unwired or on an in-process channel — because shred must invalidate
> every replica's key cache; the declaration is read from
> `ServerConfig.ReplicaCount` as well as `TOOLUP_REPLICA_COUNT`, so configuring
> the count in code cannot bypass the refusal) ·
> `src/ToolUp.Platform.Server/Server/KeyDestroyAckCoverageValidator.fs` (warns
> when the same combination appears in a multi-tenant shape that has not declared
> its replica count — the case the refusal above cannot see) ·
> `src/ToolUp.Platform.Core/Shared/EncryptionTypes.fs` (the key-destroyed
> broadcast envelope) ·
> `docs/migrations/232-encryption-admin-token-hardening.md` ·
> `src/ToolUp.Platform/technical-guide/03-authentication-secrets-and-encryption.md`
> (§"Timing contract: minute-grain replica-fanout time, not instant") ·
> `docs/platform/storage.md` (§"Key resolvers")

> **EN-4 — Key custody can be delegated to an external KMS without changing the
> encryption seam.**
> *Enablement:* Opt-in (a companion package).
> Key resolution is an interface; KMS-backed resolvers ship as companions for the
> three major cloud key-management services, so a deployment that must hold keys
> outside the application does so without forking the storage layer.
> **Evidence:** `src/Encryption/AwsKms/AwsKmsKeyResolver.fs` ·
> `src/Encryption/AzureKeyVault/AzureKeyVaultKeyResolver.fs` ·
> `src/Encryption/GoogleCloudKms/GoogleCloudKmsKeyResolver.fs` ·
> `docs/migrations/22a-kms-encryption-resolvers.md`

> **EN-4b — Provider-side encryption-at-rest posture is probed at startup for
> each cloud storage backend.**
> *Enablement:* Always on (when the corresponding storage companion is
> composed).
> Each cloud storage companion ships its own preflight validator checking the
> bucket- or container-level at-rest posture, so a deployment that relies on
> provider-managed encryption is told at boot if the bucket does not actually
> have it.
> **Evidence:**
> `src/Storage/AwsS3Storage/AwsS3EncryptionAtRestValidator.fs` ·
> `src/Storage/AzureBlobStorage/AzureBlobEncryptionAtRestValidator.fs` ·
> `src/Storage/GoogleCloudStorage/GcsEncryptionAtRestValidator.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IBlobStorageContract.fs`

> **EN-5 — Credentials reach a companion through the secret store, are scoped,
> and are read per call so rotation needs no redeploy.**
> *Enablement:* Always on for the interface contract and the scope guarantee;
> the "never read environment variables directly" part is a **documented
> authoring rule verified by review, not by an analyzer** — see
> [§9 known limitations](#known-limitations-of-the-shipped-controls).
> Every credential-bearing companion takes `ISecretStore` through its
> construction function. The interface contract requires that a secret
> registered under one scope is **never** returned for another scope's lookup,
> and that callers pass a scope derived from a resolved storage scope rather
> than an arbitrary string — the same isolation boundary as
> [TI-1](#1-tenant-isolation), applied to secret material. Reads happen **per
> call**, not once at construction, so a rotated credential takes effect without
> a redeploy or a restart. Exactly one component in the SDK reads environment
> variables and turns them into secrets, which is what makes the rule auditable
> by grep rather than by inspection of every companion.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/Interfaces/ISecretStore.fs`
> (the scope-isolation contract, stated normatively in the interface) ·
> `src/ToolUp.Platform.Server/Server/Infra/EnvironmentSecretStore.fs` (the one
> sanctioned environment-variable reader) ·
> `src/ToolUp.Platform.Server/Server/Infra/EncryptedSecretStore.fs` (envelope
> encryption plus master-key rotation) ·
> `src/ToolUp.Platform.Server/Server/Infra/ResilientSecretStore.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/ISecretStoreContract.fs` ·
> `src/Secrets/AwsSecretsManager/` · `src/Secrets/AzureKeyVault/` ·
> `src/Secrets/GcpSecretManager/` · `src/Secrets/HashiCorpVault/` ·
> `docs/operations/credential-rotation.md` (the per-call provider seam, the
> change-detection cache shape, and the checklist for a new secret-bearing
> companion)

> **EN-6 — A deployment that would persist OAuth refresh tokens to a plaintext
> secret store refuses to start.**
> *Enablement:* Always on (a security-class validator).
> Registering an OAuth credential flow against a non-encrypting secret store in
> any non-anonymous deployment is an `Error`. The deployment refuses to start
> rather than silently persisting long-lived third-party credentials in
> cleartext.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/OAuthSecretEncryptionModeValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/EncryptedSecretStoreModeValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/OAuthFlowValidator.fs` ·
> `docs/migrations/138-oauth-credential-at-rest.md` (Phase 138)

> **EN-7 — Webhook signing secrets are held in the secret store, not in the
> subscription record.**
> *Enablement:* Always on for newly-created subscriptions; backward compatible
> on read.
> Earlier versions persisted the signing secret in cleartext alongside the
> subscription. It now lives in `ISecretStore`, i.e. encrypted at rest wherever
> the composed store encrypts. A validator checks the webhook secret and URL
> configuration at startup.
> **Evidence:** `src/ToolUp.Platform.Server/Server/WebhookSecretValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/WebhookUrlValidator.fs` ·
> `docs/migrations/6d-A-webhook-secret-at-rest.md` (Phase 6d.A)

> **EN-8 — Local on-disk secret material has its file permissions validated, and
> unencrypted local storage is surfaced.**
> *Enablement:* Always on.
> The development-shaped local secret file and local blob storage both carry
> preflight checks, so a deployment that reached production on a development
> substrate is told so at boot rather than discovered later.
> **Evidence:**
> `src/ToolUp.Platform.Server/Server/LocalSecretFilePermissionsValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/LocalStorageEncryptionValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/DataProtectionBackendValidator.fs`

---

## 5. Audit

> **AU-1 — Audit events are emitted by the SDK's own bookkeeping, not by
> application code choosing to record something.**
> *Enablement:* Always on.
> The audit log sits on top of the event store and records typed audit events
> under a reserved platform namespace. The shipped event families cover
> authentication, team operations, permission and role changes, file operations,
> encryption key lifecycle, jobs, data ingestion, entity mutation,
> notifications, audit-replication outcomes, and health-state transitions. Each
> event carries the acting user, the affected user where different, the resource
> id, and a server-side timestamp.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/AuditTypes.fs` (`IAuditLog`) ·
> `src/ToolUp.Platform.Server/Server/AuditLog.fs` ·
> `docs/platform/events.md` (§"Audit log" — the full event inventory)

> **AU-2 — An audit event type that is not decodable for external replication
> fails the build.**
> *Enablement:* Always on.
> Every audit case is decoded through a single codec registry shared between the
> on-platform audit trail and the external replication path. A reflection-based
> exhaustiveness test fails the build if a new audit case is added without a
> registry entry. This closes the failure mode where an external SIEM silently
> stops receiving a category of events because someone added a case and forgot
> the decoder. Unrecognised or malformed payloads land in a per-batch decode
> failure summary rather than advancing the cursor with no signal.
> **Evidence:** `src/ToolUp.Platform.Server/Server/AuditLog.fs` (the registry) ·
> `src/ToolUp.Platform.Tests/InProcess/AuditEventRegistryTests.fs` ·
> `docs/platform/events.md` (§"Exhaustive event coverage")

> **AU-3 — Dispatcher-emitted audit redacts by default: a field is excluded from
> the audit row unless it is explicitly marked safe.**
> *Enablement:* Opt-in per method; redaction is always on within it.
> A compliance-sensitive API method opts into audit with an attribute; the
> dispatcher then emits one uniform row after each successful invocation
> carrying the subject, correlation id, kind, and a **redacted** snapshot of the
> input. Fields are whitelisted individually; anything not whitelisted renders as
> a redaction marker. **Forgetting the attribute keeps personal data out of the
> audit row** — the failure mode is a less useful row, never a leaked one.
> Idempotent replays do not double-audit.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/AuthAttributes.fs` ·
> `src/ToolUp.Platform.Server/Server/Api.fs` ·
> `docs/migrations/69h-audit-annotation-sweep.md` (Phase 69h)

> **AU-4 — The behaviour when an audit write fails is a declared policy, not an
> implementation accident.**
> *Enablement:* Default on as `LogAndContinue`; the stricter policies are
> operator-selected.
> Three policies: `LogAndContinue` (record the failure, let the action proceed),
> `RefuseAction` (fail the action — audit is a precondition), and
> `DegradeToFile` (write to a local fallback store and replay it when the
> primary recovers). A regime that requires audit to be a precondition of the
> action can have exactly that, and can evidence which policy is configured.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/SDK.Shared.fs`
> (`AuditFailurePolicy`) ·
> `src/ToolUp.Platform.Server/Server/AuditFallbackStore.fs` ·
> `src/ToolUp.Platform.Server/Server/AuditFallbackReplayService.fs` ·
> `src/ToolUp.Platform.Server/Server/AuditLogModeValidator.fs` ·
> `src/ToolUp.Platform.Server/Server/AuditLogHealthCheck.fs` · Phase 9t

> **AU-5 — Audit can be replicated to external sinks the deploying organisation
> does not control, with a stated delivery guarantee.**
> *Enablement:* Opt-in (compose one or more sinks).
> A live hook feeds each audit write into a per-sink bounded channel
> (sub-second in steady state), and a background catch-up sweep re-reads from a
> persisted per-sink-per-scope cursor to mop up anything the live path dropped
> across a restart or backpressure event. The delivery guarantee is stated
> plainly: **at-most-once in steady state, at-least-once across a restart**, so
> sinks are required by contract to be batch-idempotent. Six sinks ship —
> object-storage archives for three clouds (compatible with bucket-level
> write-once retention), plus Splunk HEC, Datadog Logs, and CEF.
> **Evidence:** `src/ToolUp.Platform.Server/Server/IAuditSink.fs` ·
> `src/ToolUp.Platform.Server/Server/AuditReplicator.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IAuditSinkContract.fs` ·
> `src/AuditSinks/S3Archive/` · `src/AuditSinks/AzureBlobArchive/` ·
> `src/AuditSinks/GcsArchive/` · `src/AuditSinks/SplunkHec/` ·
> `src/AuditSinks/DatadogLogs/` · `src/AuditSinks/Cef/` ·
> `docs/platform/events.md` (§"External audit replication")

> **AU-6 — Audit sampling, where used, is per subject kind and deterministic.**
> *Enablement:* Opt-in; the default keeps every event.
> A deployment serving high anonymous volume can sample the anonymous-subject
> events that exist only for forensic visibility while keeping full fidelity on
> the subject kinds that tie to an accountable identity. The sampling decision is
> deterministic per event id, so a replay produces an identical sampled set —
> a sampled trail is reproducible, not merely smaller.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/Types/AuditSampling.fs` ·
> `src/ToolUp.Platform.Server/Server/AuditReplicator.fs` ·
> `docs/platform/security.md` (§"Audit visibility per subject kind")

> **AU-7 — Audit envelopes carry the subject kind and a schema version.**
> *Enablement:* Always on (where replication is composed).
> The envelope carries a typed audit subject discriminating anonymous, user,
> team, and claim-bearer actors, the scope id, the occurrence timestamp, and the
> original event; sinks declare the schema version they consume. Downstream
> observability can therefore filter and attribute by actor class without
> parsing free text.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/AuditTypes.fs` ·
> `src/ToolUp.Platform.Server/Server/AuditReplicatorTypes.fs` ·
> `docs/platform/events.md`

---

## 6. Data-subject rights

The persistence model is deliberately built for *"what happened"* — versioned
data objects, an append-only event store, lineage links, retained audit. Article
17 erasure is the opposite requirement. This section is the bridge, and it is
explicit about where the SDK's responsibility ends.

> **Responsibility boundary.** The SDK provides the *tools* — export and erasure
> across every store, opt-in, scope-isolated, auditable. The deploying
> organisation chooses the **policy** per its own legal review and accepts
> liability for that choice. The SDK cannot know a deployment's jurisdiction, its
> retention obligations, or whether a given request is legally valid.

> **DS-1 — The data-subject-request substrate is opt-in and costs nothing when
> unused.**
> *Enablement:* Opt-in.
> Disabled by default: nothing registered, no administrative module injected, no
> endpoint mounted.
> **Evidence:**
> `src/ToolUp.Platform.Core/Shared/Types/DataSubjectTypes.fs`
> (`DataSubjectRequestMode`) · `docs/platform/data-subject-requests.md`

> **DS-2 — The erasure policy is a typed, three-valued choice, and the per-store
> consequence of each is documented rather than emergent.**
> *Enablement:* Opt-in; the policy is the deployment's declared choice.
> `HardDelete` removes the record; `Tombstone` (the default) preserves shape,
> version chains, and lineage while redacting identifying content;
> `RetainPerCompliance` preserves the audit and event record where retention law
> overrides the erasure request. A published matrix states what each policy does
> to each of the eight shipped store families, including the two that
> **refuse** `RetainPerCompliance` erasure by design — the event store and the
> lineage projected from it, because those *are* the audit trail.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/Types/DataSubjectTypes.fs`
> (`ErasurePolicy`) · `src/ToolUp.Platform.Server/Server/EventStoreErasureHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/DataObjectStoreErasureHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/LineageStoreErasureHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/ConfigStoreErasureHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/FeatureFlagStoreErasureHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/BlobStorageErasureHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/VectorStoreErasureHandler.fs` ·
> `docs/platform/data-subject-requests.md` (§"Per-store behaviour matrix" and
> the policy choice tree)

> **DS-3 — Erasure is two-phase — preview, then confirm — and every stage is
> audited.**
> *Enablement:* Opt-in.
> The preview runs every handler without mutating anything and returns per-handler
> affected counts for administrative review; the confirm executes. The
> orchestrator emits request-started, preview-completed, erasure-completed, and
> erasure-failed audit events. A handler that refuses records the refusal and
> does **not** abort the run — the other stores still erase, and the refusal is
> on the record. A partial failure marks the run resumable.
> **Evidence:** `src/ToolUp.Platform.Server/Server/ErasurePipeline.fs` ·
> `src/ToolUp.Platform.Server/Server/DataSubjectRequestApiHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/IDataExporter.fs` (`IErasureHandler`, whose
> `Preview` and `Erase` are separate members — the two phases are a property of
> the contract, not a convention of the caller) ·
> `src/ToolUp.Platform.Tests/Contracts/IDataSubjectRequestContract.fs` ·
> `src/ToolUp.Platform.DataSubject.Tests/` ·
> `docs/platform/data-subject-requests.md` (§"Two-phase commit + audit")

> **DS-4 — Erasure and export are scope-bounded, non-negotiably.**
> *Enablement:* Always on within the substrate.
> If a subject belongs to two tenants, erasing within the first never touches the
> second, even though the same user identifier exists in both. The bound is the
> same structural scope derivation as every other store operation
> ([TI-1](#1-tenant-isolation)) — one tenant's erasure request can never reach
> another tenant's data.
> **Evidence:** `src/ToolUp.Platform.Server/Server/ErasurePipeline.fs` ·
> `src/ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs` ·
> `docs/platform/data-subject-requests.md` (§"Scope isolation (non-negotiable)")

> **DS-5 — Article 15 export produces deterministic bytes and is scope-isolated.**
> *Enablement:* Opt-in.
> Export streams a single archive assembled from every registered exporter,
> concatenated in a deterministic order, so two exports of unchanged data are
> byte-identical and can be checksummed. Long-running exports run through the job
> scheduler with a durable store and progress notification, rather than blocking
> a request.
> **Evidence:** `src/ToolUp.Platform.Core/Shared/Types/DataSubjectApiTypes.fs` ·
> `src/ToolUp.Platform.Server/Server/IDataExporter.fs` ·
> `src/ToolUp.Platform.Server/Server/DSRExportJobHandler.fs` and
> `src/ToolUp.Platform.Server/Server/DSRErasureJobHandler.fs` ·
> `src/ToolUp.Platform.Server/Server/IBackgroundExportStore.fs` and
> `src/ToolUp.Platform.Server/Server/BlobBackedBackgroundExportStore.fs` ·
> `src/ToolUp.Platform.Server/Server/ConversationExporter.fs` and
> `src/ToolUp.Platform.Server/Server/ConversationEraseHandler.fs` (AI
> conversation history participates in both export and erasure) ·
> `src/ToolUp.Platform.DataSubject.Tests/Contracts/IBackgroundExportStoreContract.fs` ·
> `docs/migrations/09h-A-background-export-store.md` (Phase 9h.A) ·
> `docs/platform/data-subject-requests.md` (§"Export (Article 15)")

> **DS-6 — The erasure orchestrator names no concrete store; participation is by
> registration.**
> *Enablement:* Always on within the substrate.
> Stores opt into data-subject handling through extension points. A deployment
> registers handlers for the stores it actually runs, and a custom store
> participates without any edit to the orchestrator or the composition root. The
> practical consequence for a reviewer: the store list is not a closed set the
> vendor must have anticipated.
> **Evidence:** `src/ToolUp.Platform.Server/Server/ErasurePipeline.fs` ·
> `src/ToolUp.Platform.Server/Server/IDataExporter.fs` (the two registration
> interfaces) ·
> `src/ToolUp.Platform.Server/Server/Lifecycle/DataSubjectRequestLifecycle.fs` ·
> `docs/platform/data-subject-requests.md` (§"Composition — the extension-point
> pattern")

---

## 7. Portability rules for infrastructure interfaces

These six rules are not obviously "security" rules, and a reviewer may be
tempted to skip them. They belong here for two reasons a compliance assessment
cares about: they are what makes a **second implementation** of any
infrastructure interface possible (so a deployment is not captive to one job
scheduler, one storage backend, one notification transport, one audit sink), and
several of them are directly load-bearing for correctness under failure —
statelessness across invocations, retry expressed as data, and an explicit
ordering contract are what keep an audit or erasure run correct when a process
restarts mid-flight.

Any infrastructure interface that could plausibly be implemented by a distributed
framework must satisfy all six.

| # | Rule | What it forbids |
|---|---|---|
| 1 | **Identity by value** | Returning or accepting live handles (actor references, grain references) instead of values a second implementation could produce |
| 2 | **Async at every boundary** | Synchronous or fire-and-forget signatures that no distributed implementation could honour |
| 3 | **Retry and supervision as data** | Callback-shaped failure parameters that leak one framework's semantics into the contract |
| 4 | **Stateless handlers between invocations** | In-memory state carried between calls — a handler must receive everything it needs as parameters, because a host may deactivate or restart it |
| 5 | **No cross-shard ordering promises** | Guaranteeing an ordering across scopes that a partitioned implementation cannot deliver |
| 6 | **Precision at the lower bound** | Implying a timing precision some implementations cannot honour; the contract declares its floor |

Two corollaries are enforced alongside them: **no framework-specific
serialisation attributes** on any type crossing the client/server boundary, and
**no vendor SDK inside the core packages** — every cloud or vendor integration
lives in a companion behind an SDK interface (Guiding Principle 1), which is
also what keeps the core's supply-chain surface small.

> *Enablement:* Always on. The rules are a review gate on the SDK's own
> interfaces, and each is backed by an executable contract pack that any external
> implementation can run against itself.
> **Evidence:** `docs/platform/portability-rules.md` (the normative statement,
> the two documented exceptions, and the conformance bar) ·
> `src/ToolUp.Platform.Tests/Contracts/` (89 packs, including
> `IJobSchedulerContract`, `IEventStoreContract`, `IBlobStorageContract`,
> `IModuleQueryBusContract`, `INotificationChannelContract`,
> `ITenantLifecycleContract`) ·
> `src/ToolUp.Platform.Tests/Contracts/ArchitectureFitness.fs` ·
> `docs/platform/data-subject-requests.md` (§"Portability" — every erasure method
> passes the same six-rule audit) ·
> `docs/operations/degraded-capabilities.md`

---

## 8. AI safety

This section is deliberately the most conservative in the document, because AI is
where a security artefact is most likely to over-claim. Read the enablement
labels closely, and read [§9](#9-out-of-scope-and-known-limitations) for what the
SDK explicitly does **not** enforce about model providers.

> **AI-1 — AI-driven UI control is not shipped, and tool dispatch has a single
> gate seam that is consulted before any tool call reaches the browser.**
> *Enablement:* Opt-in — **and the unconfigured default is permissive.** Read
> this rule's last paragraph.
> The agent loop consults the registered tool authoriser **before** emitting any
> client tool invocation. A denied call is never dispatched, the model is told
> the action was refused via a typed result, and the refusal is written to the
> event store as an auditable denial event carrying the tool name, reason, active
> module and page, and the task and conversation ids.
> **The SDK ships no implementation of this seam.** If no authoriser is
> registered, the consult resolves to *allow*. A deployment that exposes
> AI-driven UI control in production is expected to register a default-deny
> allowlist; this is stated here, and in the repository's `SECURITY.md`, because
> a reviewer must not read "there is a gate" as "the gate is closed by default".
> **Evidence:** `src/ToolUp.AI.Core/Shared/AITypes.fs`
> (`IClientToolAuthorizer`) ·
> `src/ToolUp.AI.Server/Server/AIAgentEngine.fs` (the pre-dispatch consult and
> the denial audit) ·
> `src/ToolUp.Platform.Tests/Contracts/IClientToolAuthorizerContract.fs` ·
> `src/ToolUp.Platform.Tests/Contracts/IClientToolDispatchContract.fs` ·
> `../../SECURITY.md` (§"AI-tool authorizer seam", which states the threat model
> and the permanently-denied reserved namespace) ·
> `docs/platform/README.md`

> **AI-1b — The model is only ever offered the tools valid for the caller's
> surface, and tool names and schemas are sanitised before reaching a provider.**
> *Enablement:* Always on (where the AI companion is composed).
> The tool registry applies a per-surface visibility gate, so a tool restricted
> to one surface is not merely refused when called — it is never presented to
> the model in the first place, which removes it from the model's option space
> rather than relying on a downstream refusal. Tool names and the JSON schema
> sent to the provider are escaped and sanitised at the registry boundary.
> **Evidence:** `src/ToolUp.AI.Server/Server/AIToolRegistry.fs`

> **AI-1c — Model-facing write paths refuse unscoped requests and bound their
> payload size.**
> *Enablement:* Always on (where the AI companion is composed).
> The fast-path ingestion endpoint refuses a request that carries no resolved
> scope rather than defaulting to a shared one, and bounds the accepted payload
> — explicitly to cap the blast radius of prompt-injection and
> conversation-history forgery attempts, not merely for throughput.
> **Evidence:** `src/ToolUp.AI.Server/Server/FastPathBeaconHandler.fs`

> **AI-2 — The tool gate is defence in depth, and never substitutes for
> authentication or authorisation.**
> *Enablement:* Always on (as a design constraint).
> Authentication, surface enforcement, scope resolution, and permission guards
> run independently downstream and **never** consult the tool authoriser's
> decision. A compromised or misconfigured authoriser therefore cannot widen a
> caller's data access; it can only affect which UI actions the model may
> propose.
> **Evidence:** `../../SECURITY.md` (§"AI-tool authorizer seam — threat model") ·
> `src/ToolUp.Platform.Server/Server/SurfaceEnforcementMiddleware.fs` ·
> `src/ToolUp.Platform.Server/Server/Remoting/Auth.fs`

> **AI-3 — AI provider credentials go through the secret store, and can be issued
> and revoked administratively without a redeploy.**
> *Enablement:* Opt-in for the administrative key store; the secret-store rule is
> always on ([EN-5](#4-encryption)).
> Provider keys resolve through the same per-call secret seam as every other
> credential. A platform-administrator-issued key store lets a key be rotated or
> revoked as a single administrative action rather than a configuration change and
> redeploy. A deployment may also resolve a per-subject key so the cost and the
> data-handling relationship sit with the calling organisation.
> **Evidence:** `src/ToolUp.AI.Core/Shared/IPlatformAIKeyStore.fs` ·
> `src/ToolUp.AI.Server/Server/BlobPlatformAIKeyStore.fs` ·
> `src/ToolUp.AI.Server/Server/PlatformAIKeysHandler.fs` ·
> `src/ToolUp.AI.Core/Shared/IAIProviderFactory.fs` ·
> `src/AIProviders/README.md` (every shipped provider resolves its key per call
> from the injected secret store, never from an environment variable) ·
> `src/ToolUp.Platform.Tests/Contracts/IPlatformAIKeyStoreContract.fs` ·
> `docs/platform/security.md` (§"AI-cost-ceiling considerations", which
> enumerates the five hardening postures in increasing order of operator effort)

> **AI-4 — AI streaming endpoints are subject to the same authentication model as
> every other endpoint, with an explicit validator for the streaming shape.**
> *Enablement:* Always on.
> Server-sent-event endpoints have a distinct identity lifecycle from ordinary
> request/response calls; a dedicated validator checks the streaming
> authentication mode at startup rather than leaving the difference implicit.
> **Evidence:** `src/ToolUp.Platform.Server/Server/SseAuthModeValidator.fs` ·
> `docs/migrations/117-sse-identity-lifecycle.md` ·
> `docs/platform/sse-deployment.md`

> **AI-5 — The AI wire format has a published conformance corpus.**
> *Enablement:* Always on.
> The wire format between the AI tiers is specified and backed by a shared
> conformance corpus executed on both the .NET and the browser-compiled sides, so
> a divergence between the two is a test failure rather than a runtime surprise.
> **Evidence:** `src/ToolUp.AI.Wire/` ·
> `src/ToolUp.AI.Wire.Conformance/Shared/ConformanceSuite.fs` ·
> `src/ToolUp.AI.Wire.Conformance/Shared/Corpus.fs` ·
> `src/ToolUp.AI.Wire.Conformance/README.md` ·
> `src/ToolUp.AI.Wire.Conformance/PORTABILITY.md`

> **AI-6 — The default module surface requirement excludes anonymous subjects, so
> exposing AI to unauthenticated callers is an explicit act.**
> *Enablement:* Always on.
> The default requirement admits authenticated users and team members only. A
> deployment that wants anonymous AI access must declare the looser requirement
> — which is the design intent, because anonymous AI access is a cost-asymmetric
> surface (the caller pays nothing; the deployment pays per token).
> **Evidence:** `src/ToolUp.Platform.Server/Server/SurfaceEnforcementMiddleware.fs` ·
> `docs/platform/security.md` (§"AI-cost-ceiling considerations")

---

## 9. Out of scope and known limitations

Everything in this section is deliberate. A control listed here is **not**
enforced by the SDK, and a reviewer should direct the corresponding question to
the deployment operator, the infrastructure provider, or the identity provider —
not to the SDK.

### Not the SDK's responsibility

| Area | Why, and who owns it |
|---|---|
| **Certification and attestation** (SOC 2, ISO 27001, HIPAA, PCI DSS) | These attest to an organisation's controls over a system, not to a library. Ask the vendor of the deployed product. |
| **Model-provider data handling** — whether a provider trains on submitted content, its retention window, its sub-processors | Governed by the operator's contract with the model provider. The SDK routes requests to a provider the operator chose and credentialed; it has no ability to constrain what that provider does with the payload, and **this document makes no claim about it**. Where a zero-retention or no-training commitment is required, it must come from the provider agreement. See [AI-3](#8-ai-safety) for the bring-your-own-key posture that moves that relationship to the calling organisation. |
| **Transport-layer security** — TLS termination, cipher suites, certificate lifecycle, HSTS preload registration | The deployment's ingress, load balancer, or platform host. The SDK exposes HTTPS redirection and forwarded-header trust ([EN-1](#4-encryption)) and nothing below that. |
| **Network controls** — segmentation, WAF, DDoS mitigation, IP allowlisting, egress filtering | Infrastructure layer. |
| **Identity-provider security** — password policy, MFA enrolment and enforcement, session revocation at the IdP, account recovery, directory lifecycle | The identity provider. The SDK consumes tokens and validates their signature, issuer, and audience ([AN-3](#2-authentication), [AN-4](#2-authentication)); it does not implement authentication itself. |
| **Storage-layer encryption and immutability** — provider-managed keys, bucket-level encryption, write-once retention locks | The storage provider. The SDK's application-tier envelope encryption ([EN-2](#4-encryption)) composes *with* these rather than replacing them, and the object-storage audit archives are designed to sit inside a write-once bucket. |
| **Availability, backup, and disaster recovery** — RPO/RTO, backup schedules, restore testing | The deployment operator. |
| **Bot management** — CAPTCHA, proof of work, device attestation | Not provided. For an anonymous-facing deployment this is operator-owned middleware running ahead of the handler; rate limiting ([TI-3](#1-tenant-isolation)) is a partial substitute, not an equivalent. |
| **Anonymous AI cost control** | Explicitly operator-owned. The documentation enumerates five postures in increasing order of effort; the SDK provides none of them as a default. |
| **Application-code defects in consumer modules** — injection, path traversal, unsafe deserialisation, or business-logic flaws inside a deployment's own module code | The deployment's own code review. The SDK's structural controls constrain *where* a module can read and *whether* it may be called; they cannot make an incorrect handler correct. |
| **Legal determination of a data-subject request** — validity, jurisdiction, lawful basis, retention obligations, identity verification of the requester | The deploying organisation, per its own legal review. The SDK executes a policy; it does not choose one. |

### Known limitations of the shipped controls

Stated plainly so a reviewer does not have to discover them:

- **Erasure matching is declared-precision, not schema-aware.** Except where
  noted, "names the subject" means a substring match of the subject's identifier
  within a record, because the SDK has no schema knowledge of a deployment's own
  module payloads. A deployment storing an identifier in a transformed or
  tokenised form must register its own erasure handler.
  (`docs/platform/data-subject-requests.md`, §"Per-store behaviour matrix")
- **Lineage tombstoning is whole-payload, not field-level.** Under `Tombstone`,
  a lineage link's payload is tombstoned as a whole, which drops the edge from
  traversal. Field-level erasure — where the edge survives with tombstoned
  identifiers — is a tracked follow-up, not shipped.
- **Audit replication is at-least-once across a restart.** Steady state is
  at-most-once, but the catch-up sweep may re-deliver after a restart where the
  cursor had not advanced. Sinks are required by contract to be
  batch-idempotent; a custom sink that ignores this will duplicate rows.
  ([AU-5](#5-audit))
- **Deployments that ran audit replication on a pre-registry SDK version have
  incomplete external trails for that window.** The on-platform audit trail is
  unaffected. A documented backfill procedure exists.
  (`docs/platform/events.md`, backfill note)
- **A team-partitioned rate-limit budget is shared by the whole team.** Sizing a
  team policy against per-user expectations and then watching one busy member
  exhaust it for the others is the predictable failure mode.
  ([TI-3](#1-tenant-isolation))
- **Anonymous rate-limit partitioning depends on correct proxy trust
  configuration.** Behind a proxy with forwarded-header trust misconfigured,
  every anonymous caller collapses into one partition. The failure mode is denial
  of service against legitimate anonymous users, not a bypass — but it is a
  failure mode. ([EN-1](#4-encryption))
- **A share token is a bearer credential.** Possession authorises access to the
  bound resource for the declared lifetime and use limit. The deployment has no
  signal that the bearer is the intended recipient; the mitigations are short
  lifetimes, use limits, leak-suppressing response headers
  ([AN-10](#2-authentication)), and issuer-scoped revocation
  ([AN-12](#2-authentication)).
- **Rotating the share-token signing key invalidates every outstanding token.**
  This is the intended effect, and it is the mechanism for revoking all live
  tokens at once — but it is not a no-impact rotation.
  ([AN-11](#2-authentication))
- **The AI tool gate is permissive when unconfigured.** Restated here because it
  is the single most important caveat in [§8](#8-ai-safety).
- **"Companions never read environment variables directly" is an authoring rule
  verified by review, not by a compiler check.** The interface shape makes the
  correct path the easy one, and exactly one component in the SDK reads
  environment variables as secrets — so a violation is detectable by search —
  but there is no analyzer that fails the build on a companion that reads
  configuration directly. The rule is stated normatively in
  `docs/platform/auth.md`, `docs/ai/extending.md`, `docs/rag/extending.md`, and
  `docs/platform/events.md`. ([EN-5](#4-encryption))
- **The classification gate is a server-side redaction layer, not a data
  classifier.** It enforces a classification decision; deciding *which* fields
  are sensitive is the deployment's own field classifier to implement.
  ([AZ-9](#3-authorisation))
- **The SDK is pre-1.0.** A minor version bump may carry a breaking change and
  may therefore change a rule. See
  [Ruleset versioning](README.md#ruleset-versioning).

---

## How to apply this to a deployment

A reviewer assessing a *product* built on this SDK is assessing the composition,
not the SDK alone. Four questions extract most of what this document cannot tell
you on its own:

1. **Which SDK version?** Then read the ruleset stamped for that version. Rules
   change; a claim quoted from the wrong version is not a claim.
2. **Which opt-in controls are composed?** Every rule labelled *Opt-in* above is
   a direct question: encryption at rest and which key resolver; audit
   replication and to which sinks; the audit failure policy; the data-subject
   substrate and which erasure policy; the AI tool authoriser.
3. **Which preflight findings does the deployment accept?** Several controls can
   be downgraded from error to warning by a named, greppable configuration flag —
   the rate-limit acceptance, the header-auth acceptance, the CSRF opt-out. Ask
   for the list of accepted findings; it is short, explicit, and it is the real
   deviation register.
4. **What does the deployment's authorisation-surface manifest contain?** This is
   the strongest single artefact: a computed enumeration of every reachable
   endpoint and its authorisation requirement, with anonymous-reachable entries
   called out by name. ([AZ-7](#3-authorisation))

---

## Reporting an error in this document

An unsupported rule, an evidence path that does not resolve, or a rule whose
guarantee is weaker than stated is a **defect**, not a documentation nit. Report
it through the process in [`SECURITY.md`](../../SECURITY.md).

Versioning policy, the canonical URL convention, and the maintenance cadence that
keeps the evidence pointers honest are documented in
[`README.md`](README.md) alongside this file.

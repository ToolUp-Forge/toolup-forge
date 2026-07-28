# Security Policy

ToolUp is a multi-tenant analytical platform; security issues can have
real consequences for deployments. This document covers how to report a
security issue and what response timeline you can expect, by the
affected package's [maintenance tier](CONTRIBUTING.md#three-tier-maintenance-model).

> **Evaluating the platform rather than reporting a vulnerability?** The
> versioned, evidence-cited statement of what the SDK enforces —
> tenant isolation, authentication, authorisation, encryption, audit,
> data-subject rights, portability, AI safety, and an explicit
> out-of-scope list — is
> [`docs/security/PLATFORM-SECURITY-RULES.md`](docs/security/PLATFORM-SECURITY-RULES.md).
> It is written to be read cold by a compliance officer or vendor-risk
> assessor. Versioning policy and maintenance cadence:
> [`docs/security/README.md`](docs/security/README.md).

## Reporting a vulnerability

**Do not open a public GitHub issue** for security-affecting reports.
Public reports give attackers a head-start and put deployments at risk.

Instead, send a private report by either:

1. **Email** — `security@toolup.pro`. Encrypt with our PGP key if you can:

   - Key fingerprint: *to be published once a key is generated; until
     then, plain encrypted email or a private GitHub Security Advisory
     are both acceptable channels*.

2. **GitHub Security Advisory** — open a draft advisory under the
   repository's "Security" tab. Only repository maintainers can see
   draft advisories.

Please include, where possible:

- A description of the issue and its expected impact (e.g. cross-tenant
  data exposure, authentication bypass, unsafe deserialisation, denial
  of service).
- Reproduction steps or a proof-of-concept (kept minimal — we don't need
  fully weaponised exploits).
- The affected SDK version, the deployment mode the issue manifests in
  (`Anonymous` / `AuthenticatedEphemeral` / `Individual` / `Team` /
  `MultiTeam`), and any companion packages involved.
- Whether you have already disclosed the issue to anyone else.

## What happens after you report

| Step | Timeline | Owner |
|---|---|---|
| Acknowledgement of receipt | within **48 hours** | maintainer on call |
| Triage and severity assessment | within **5 days** | Tier-relevant maintainer |
| Patch and release plan | depends on tier (below) | Tier-relevant maintainer |
| Coordinated disclosure | once the patch is released, plus an embargo period agreed with the reporter | maintainer + reporter |

We follow a coordinated-disclosure model. Embargoes are negotiated case
by case but typically run **30–90 days** depending on the severity, the
deployment surface, and the reporter's preferences. Reporters who
request public credit will receive it; reporters who request anonymity
will be respected.

## Patch SLA by tier

The maintenance tiers are defined in
[CONTRIBUTING.md §three-tier maintenance model](CONTRIBUTING.md#three-tier-maintenance-model).
Security patch SLAs differ by tier:

| Tier | What it covers | Patch SLA from triage |
|---|---|---|
| **Tier 1** | SDK core (`ToolUp.Platform`) plus canonical companions (Claude / OpenAI providers; Local + OpenAI embedding; Redis NotificationChannel; LocalFileStorage; OIDC AuthProvider) | **7 days** for High / Critical; **30 days** for Medium; Low handled in next regular release |
| **Tier 2** | Other first-party companions (additional storage backends, additional providers, additional notification channels, additional audit sinks) | **14 days** for High / Critical; **60 days** for Medium; Low handled in next regular release |
| **Tier 3** | Community-maintained companions and modules | Best-effort by the community maintainer; project maintainers do not commit a SLA but will assist with coordinated disclosure if asked |

"From triage" means from the point where the issue's severity has been
assessed, not from initial receipt. If triage takes longer than the
quoted 5 days, the SLA clock starts at the 5-day mark to avoid
penalising reporters for slow internal assessment.

### Severity guidance

We use the **CVSS 3.1** scoring framework. Roughly:

- **Critical (CVSS ≥ 9.0)**: cross-tenant data exposure, full
  authentication bypass, remote code execution.
- **High (CVSS 7.0–8.9)**: privilege escalation within a tenant, secrets
  leakage, signed-token forgery, partial authentication bypass.
- **Medium (CVSS 4.0–6.9)**: denial of service against a single tenant,
  audit-trail bypass, side-channel information leakage.
- **Low (CVSS < 4.0)**: hardening recommendations; issues that require
  pre-existing privileged access; informational findings.

The maintainer's assessment is final, but we'll explain our reasoning in
the advisory.

## Scope

In-scope for this policy:

- The published `ToolUp.*` NuGet packages from the OSS Forge
  repository.
- The companion packages listed in this repository's
  [MAINTAINERS.md](MAINTAINERS.md) under Tier 1 and Tier 2.
- The contract test packs published with the SDK
  (`IBlobStorageContract`, `IJobSchedulerContract`, etc.) — flaws in the
  test packs that mask security issues in real implementations.

Out of scope:

- Third-party services that ToolUp integrates with (Anthropic, OpenAI,
  Azure, AWS, GCP, Clerk, Auth0, etc.) — report to the upstream vendor.
- Customer deployments running the SDK — report to the deployment
  operator, not to the project.
- Cosmetic / documentation issues — use a regular issue or PR.
- Best-practice recommendations that don't correspond to a specific
  exploitable flaw — open a regular issue, not a security advisory.

## Security design notes

These document security-relevant mechanisms a reviewer or deployment
operator should understand. They are not vulnerability reports.

The two notes below are kept here because both are cited directly from
the disclosure scope above. The complete, versioned rule set — with an
evidence pointer into the source tree for every rule — is
[`docs/security/PLATFORM-SECURITY-RULES.md`](docs/security/PLATFORM-SECURITY-RULES.md);
deployment-time threat surfaces and hardening knobs are
[`docs/platform/security.md`](docs/platform/security.md).

### CSRF synchroniser token — client request-guard

`ToolUp.Platform.Server`'s `CsrfMiddleware` mints a cryptographically
random, per-session token surfaced by `GET /api/csrf-token`. Every
state-changing (`POST` / `PUT` / `PATCH` / `DELETE`) `/api/*` request
must echo that token in the `X-CSRF-Token` header; the middleware
fixed-time-compares it to the session-bound value and rejects a
mismatch with `403`. A `SameSite=Strict` `XSRF-TOKEN` cookie is set
alongside as independent defence-in-depth; the session-bound token is
the primary check.

On the client the header is attached by a single seam:
`CsrfClient.installRequestGuard` (in `ToolUp.Platform.Client`) wraps
`XMLHttpRequest.prototype.{open,send}` exactly once, installed at
client module-load. Fable.Remoting's proxy transport is XHR-only and
freezes a proxy's custom-header list at proxy-build time, so a
send-time guard is the only mechanism that reliably carries the token
on every proxy call regardless of when the proxy object was
constructed. The guard is deliberately narrow:

- It attaches the header **only** when the request is same-origin to
  the page **and** the path begins with `/api/` **and** the method is
  state-changing. Same-origin scoping ensures the token is never sent
  to a third-party origin (no token leakage); non-API and read-only
  requests are untouched.
- It only **adds** the header from the in-memory token cache at send
  time; it never overrides a header, never blocks or rewrites a
  request, and never throws — the original `send` is always invoked.
- It is idempotent (prototype sentinel) and reads the token lazily, so
  it carries the token correctly even for proxies built before the
  startup token prefetch resolved.

**No-op by default.** Under `NoSecurityHardening` (the default HTTP
posture) the `/api/csrf-token` route is not mounted, the client token
cache stays empty, and the guard adds nothing — a stock deployment is
behaviourally unchanged (Guiding Principle 13: zero footprint on the
default).

**Threat model.** This defends against cross-origin request forgery: a
victim's session cookie rides along automatically on a forged
state-changing request, but a cross-origin attacker cannot read the
unguessable per-session token (the `/api/csrf-token` response is
same-origin-read-protected) and therefore cannot populate
`X-CSRF-Token`. It is *not* a substitute for authentication or
authorization, which are enforced independently downstream
(`AuthEnforcementMiddleware`, scope resolution, permission guards).

### AI-tool authorizer seam — `IClientToolAuthorizer`

forge exposes `IClientToolAuthorizer` in `ToolUp.AI.Core` as the
single seam through which AI tool dispatch can be gated. `runAgentLoop`
consults the registered authorizer **before** emitting any
`ClientToolInvoke` SSE; a denied call is never dispatched to the
browser, the model is told the action was refused (typed `Denied`
tool-result), and the refusal is written to `IEventStore` as a
`_platform.ai.tool_allowlist_denial` event.

**forge ships no implementation of this seam out of the box.** If no
authorizer is registered, the consult resolves to "allow" — full
agent-loop behaviour with zero gating. A forge consumer that wants
allowlist enforcement must register their own implementation, or
license a third-party one.

Consumers wanting AI-driven UI in production are expected to register
a default-deny allowlist (keyed by module / field / button / page) and
emit bounded refusal-event audit on denials. Consumers staying on
forge-only need to authorise tool dispatch themselves or accept the
defaults documented under each AI tool's `_platform.*` namespace.

**Permanently-denied targets remain enforced inside `ToolUp.AI`**: the
reserved `_sdk.*` Id namespace (Platform Admin, Health Monitor, Team
Manager, etc.) is hard-denied regardless of any authorizer's decision.
That guarantee lives in forge and is independent of the authorizer
implementation.

**Threat model.** This seam is defence-in-depth for prompt-injection
risk against AI-driven UI tool dispatch. It is *not* a substitute for
authentication or authorization, which are enforced independently
downstream (`AuthEnforcementMiddleware`, scope resolution, permission
guards) and never gate on an `IClientToolAuthorizer` decision.

## Acknowledgement

We thank security researchers who report responsibly. ToolUp does not
yet operate a paid bug-bounty programme; we credit reporters in
published advisories and on a security acknowledgements page (once
established).

---

This policy may evolve as the project grows. Material changes are
announced in the repository's `CHANGELOG.md` and in the security
advisory feed.

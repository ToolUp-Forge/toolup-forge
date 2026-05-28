# MAINTAINERS

This file lists the active maintainers of the ToolUp SDK and their
responsibilities by tier. The maintenance commitments are defined in
[CONTRIBUTING.md §three-tier maintenance model](CONTRIBUTING.md#three-tier-maintenance-model).

## Active maintainers

| Maintainer | GitHub | Affiliation | Tier 1 | Tier 2 | Trademark |
|---|---|---|---|---|---|
| Andrew J. Willshire | @andrewjwillshire | ToolUp Analytics Ltd | Lead | Lead | Authoriser |

## Tier responsibilities

### Tier 1 — SDK core + canonical companions

The Tier 1 lead reviews every PR that touches:

- `ToolUp.Platform.Core` / `ToolUp.Platform.Server` /
  `ToolUp.Platform.Client` / `ToolUp.Platform.Build`
- `ToolUp.AI` (companion runtime)
- `ToolUp.RAG` (companion runtime)
- `ToolUp.KnowledgeBase`
- `ToolUp.Forms`
- `ToolUp.Scheduling`
- The canonical providers / sinks: `ToolUp.AIProviders.Claude`,
  `ToolUp.AIProviders.OpenAI`, `ToolUp.EmbeddingProviders.Local`,
  `ToolUp.EmbeddingProviders.OpenAI`, `ToolUp.NotificationChannels.Redis`,
  `ToolUp.AuthProviders.Oidc.Client`, default `LocalFileStorage`

The Tier 1 lead is responsible for:

- Reviewing design issues before significant feature work.
- Approving PRs that change public interfaces.
- Triaging and responding to security issues per the SLA in [SECURITY.md](SECURITY.md).
- Cutting major and minor releases.

### Tier 2 — First-party best-effort companions

The Tier 2 lead reviews PRs that touch any first-party companion not
listed under Tier 1 — additional storage backends (Azure / S3 / GCS),
additional notification channels (SMTP / SendGrid / Twilio / WebPush),
additional audit sinks (S3 / Splunk / Datadog), additional embedding /
auth providers, and the AG Grid Enterprise integration shim.

The Tier 2 lead is responsible for:

- Best-effort PR review (target: ~2 weeks for non-trivial features).
- Tracking the 6-month deprecation policy for any breaking change.
- Coordinating with Tier 3 community maintainers for ecosystem packages.

### Trademark authoriser

Trademark requests (logo on marketing, partnership statements, white-label
licences) are authorised by the named individual at ToolUp Analytics Ltd.
Forks and ordinary fair-use compatibility statements ("Built for ToolUp",
etc.) do not require authorisation — see [TRADEMARK.md](TRADEMARK.md).

## How to become a maintainer

ToolUp's maintainer model is currently a benevolent-dictator structure
under ToolUp Analytics Ltd. A formal maintainer council (3–7 members,
mix of the founding maintainer team and external contributors) is
planned for Year 2–3 if the community grows enough to justify the
overhead.

In the meantime, prospective maintainers earn responsibility by:

1. Sustained high-quality contributions over several months.
2. Demonstrated review judgement on others' PRs (helpful comments,
   correct catches, constructive disagreement).
3. Tier 3 community maintainership of a companion or module that proves
   stability.
4. Direct invitation by the existing maintainer team.

## Inactive / former maintainers

(none yet)

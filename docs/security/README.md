# Security documentation

This directory holds the SDK's **procurement-facing** security material — the
documents a compliance officer, security reviewer, or vendor-risk assessor reads
when evaluating a deployment built on ToolUp Platform.

| Document | Audience | What it answers |
|---|---|---|
| [`PLATFORM-SECURITY-RULES.md`](PLATFORM-SECURITY-RULES.md) | Compliance officer / security reviewer / procurement | "What does this platform enforce, structurally, and how do I verify each claim?" |
| [`../../SECURITY.md`](../../SECURITY.md) | Security researcher | "How do I report a vulnerability, and what response will I get?" |
| [`../platform/security.md`](../platform/security.md) | Deployment operator | "What threat surfaces does *my* composition expose, and which knobs harden them?" |

The three are deliberately separate. `PLATFORM-SECURITY-RULES.md` is the
**versioned artefact** — a stable, citable statement of what the substrate
enforces. `SECURITY.md` is the disclosure policy. `docs/platform/security.md` is
the operator's tuning reference and changes far more often.

## Ruleset versioning

`PLATFORM-SECURITY-RULES.md` carries a **RulesetVersion** in its header. The
policy:

- **RulesetVersion tracks `<ToolUpSdkVersion>`** — the single SDK version
  property in [`Directory.Packages.props`](../../Directory.Packages.props)
  (mirrored by `<Version>` in
  [`Directory.Build.props`](../../Directory.Build.props)). A ruleset stamped
  `0.11.0` describes the substrate shipped by the `ToolUp.*` packages at
  `0.11.0`, and nothing else.
- **A rule statement is only ever true of the version it is stamped with.** A
  reviewer holding a deployment on an older SDK reads the ruleset published for
  *that* version, not this one. Do not back-port a claim.
- **The SDK is pre-1.0**, so per the SemVer-on-`0.x` policy in the repo
  [`README.md` §Versioning](../../README.md#versioning), a **minor** bump may
  carry a breaking change
  and therefore may change a rule; a **patch** bump is non-breaking and
  therefore never weakens a rule. Once `1.0.0` is declared, the ruleset series
  becomes `1.x` and a rule can only be weakened across a major bump.
- **Weakening is announced.** If a rule's guarantee narrows between versions,
  the rule's entry says so explicitly and names the version at which it
  changed. Silent narrowing of a published rule is treated as a defect.

## Canonical URL

The artefact's intended canonical path on the published documentation site is:

```
/security/rules/<ruleset-series>/
```

— i.e. `/security/rules/0.11/` for the current ruleset, resolved against
whichever origin serves this repository's `docs/` tree. The path is chosen so a
reviewer can cite a *stable, version-pinned* URL in a vendor-risk questionnaire
and have it keep resolving to the ruleset they actually assessed, rather than to
a moving "latest".

Until the documentation site is published, the authoritative source is the file
in this repository:
`docs/security/PLATFORM-SECURITY-RULES.md` on the tag matching the SDK version
under review. A reviewer can always resolve a claim against the tagged tree,
which is the strongest form of the citation anyway — the evidence paths are
repository paths.

## Maintenance cadence

**A change that touches a documented rule refreshes that rule's cited evidence
in the same commit.** This is the whole discipline, and it is not optional: an
evidence pointer that no longer resolves turns a compliance artefact into a
liability. Concretely, when a change:

- **moves or renames a cited file** — update every `Evidence:` line naming it;
- **changes what a cited mechanism enforces** — update the rule text, and if the
  guarantee narrowed, say so and stamp the version;
- **adds a new structurally-enforced guarantee** — add a rule, with evidence, in
  the same commit that ships the guarantee;
- **removes a guarantee** — move the rule to the out-of-scope section rather
  than deleting it, so a reviewer comparing two ruleset versions can see the
  change;
- **bumps `<ToolUpSdkVersion>`** — restamp the RulesetVersion header.

Reviewers and contributors can check the artefact mechanically: every
`Evidence:` line cites repository-relative paths, so a path that no longer
exists is detectable by walking the artefact and testing each cited path against
the working tree.

## What this material is not

- **Not a certification.** Nothing here asserts SOC 2, ISO 27001, HIPAA, or any
  other attestation. Certification is a property of a *deployment and the
  organisation operating it*, not of an SDK.
- **Not legal advice.** The data-subject material describes mechanisms, not
  obligations. Jurisdiction, lawful basis, and retention duties are the
  deploying organisation's to determine.
- **Not a claim about a deployment.** The SDK enforces what is documented here;
  a deployment additionally composes companions, writes its own modules, and
  chooses its own configuration. A reviewer assessing a product built on the SDK
  is assessing the composition, and the artefact is the floor beneath it, not
  the whole of it.

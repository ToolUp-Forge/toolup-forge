# Contributing to ToolUp

ToolUp welcomes contributions — bug reports, fixes, features,
documentation, translations, new companion packages, and new modules.
This document covers how to contribute, what's covered by what level of
maintenance commitment, and how to sign your work.

## Table of contents

1. [Quick start](#quick-start)
2. [Developer Certificate of Origin (DCO)](#developer-certificate-of-origin-dco)
3. [Three-tier maintenance model](#three-tier-maintenance-model)
4. [Contribution flow by type](#contribution-flow-by-type)
5. [Promotion from community to first-party](#promotion-from-community-to-first-party)
6. [Style and conventions](#style-and-conventions)
7. [Security-affecting changes](#security-affecting-changes)
8. [Maintainer setup](#maintainer-setup)
9. [Where to ask questions](#where-to-ask-questions)

---

## Quick start

1. **Fork the repository** on GitHub.
2. **Clone your fork** and create a branch off `main` for your change.
3. **Make your change.** Follow the [Style and conventions](#style-and-conventions) guidance.
4. **Sign each commit** with `Signed-off-by:` — see [DCO](#developer-certificate-of-origin-dco).
   Unsigned commits will be rejected at review time and by the
   [`checks.yml`](.github/workflows/checks.yml) CI workflow, which scans every PR
   commit for a `Signed-off-by:` trailer matching the commit author.
5. **Run the build** locally:
   ```
   dotnet build
   dotnet test
   dotnet fantomas --check .
   ```
6. **Open a pull request** against `main` with a clear description of the
   change and the rationale. Reference any related issues.
7. A maintainer will review per the [contribution-flow timelines](#contribution-flow-by-type).

## Developer Certificate of Origin (DCO)

ToolUp uses the **Developer Certificate of Origin (DCO)** — not a
contributor licence agreement (CLA). DCO sign-off is the same mechanism
used by the Linux kernel, Docker, and many other large open-source
projects. It's a low-friction way for you to certify that you have the
right to submit your contribution.

By signing off your commit, you certify that you wrote the patch (or
otherwise have the right to submit it) and that you accept the DCO terms,
reproduced here for reference: <https://developercertificate.org>.

### How to sign your commits

Add a `Signed-off-by:` trailer to every commit message. The simplest way
is to use `git commit -s`:

```
git commit -s -m "Fix authentication bug in OidcAuthProvider"
```

This adds a trailer like:

```
Signed-off-by: Your Name <your.email@example.com>
```

The name and email must match your `git config user.name` and
`git config user.email`. Anonymous or pseudonymous sign-offs are not
accepted.

### Configuring `git` to sign automatically

To avoid forgetting `-s`, you can install the project commit template:

```
git config commit.template ./.github/dco-template
```

…or set `format.signoff = true` globally:

```
git config --global format.signoff true
```

### Fixing missed sign-offs

If you forgot to sign a commit, amend with `git commit --amend -s` (most
recent only) or rebase with `git rebase --signoff <base>` to retroactively
sign every commit since `<base>`.

### Why DCO and not CLA?

A CLA would grant the project's maintainer entity broader rights — typically
including the right to relicense future versions. DCO is a statement of
authorship under the existing licence; it's lower-friction for contributors
and sufficient for everything Apache 2.0 allows. The trade-off is that
relicensing is harder later — but pre-emptive CLA isn't worth the
contribution friction.

## Three-tier maintenance model

ToolUp covers different surfaces with different maintenance commitments.
The tier of a package determines how quickly issues are addressed and
whom the contribution flow involves.

| Tier | Surface | Maintainer commitment |
|---|---|---|
| **Tier 1** | SDK core (`ToolUp.Platform`) plus canonical companions: Claude AI provider, OpenAI AI provider, Local + OpenAI embedding providers, Redis NotificationChannel, LocalFileStorage, OIDC AuthProvider | Guaranteed support during the SDK's lifetime; security patches within **7 days**; major release within current SemVer major; design reviewed by maintainers |
| **Tier 2** | Other first-party companions (additional storage backends, additional providers, additional notification channels, additional audit sinks, etc.) | Best-effort with **6-month deprecation policy**; security patches; community-led feature work welcomed |
| **Tier 3** | Community-maintained companions and modules | Listed in the registry; **no commitment** from project maintainers; community ownership; self-merged PRs by community maintainers |

If you're unsure which tier a package belongs to, check
[`MAINTAINERS.md`](MAINTAINERS.md) or open an issue and ask.

## Contribution flow by type

| Contribution | Process | Typical review time |
|---|---|---|
| Bug fix (Tier 1 / 2) | PR with regression test; reviewed by a maintainer | within 1 week |
| Bug fix (Tier 3) | Self-merged by the community maintainer | per the maintainer's cadence |
| New feature (Tier 1) | Open a design issue first; reviewed by maintainers; PR after design approval | design review ~2 weeks; PR review ~1 week post-approval |
| New feature (Tier 2) | PR direct; reviewed if it touches public surface | ~2 weeks |
| New feature (Tier 3) | PR direct; reviewed only if it touches public surface | per maintainer cadence |
| New companion package | Open a pre-PR design issue; the package lives in `community/` (Tier 3) on first merge; promotion path is below | design ~2 weeks; merge ~1 week |
| Documentation | PR direct; lower review bar | ~1 week |
| i18n / translations | Community-led; native-speaker review encouraged | per reviewer availability |
| Security issues | **Do not open a public issue** — see [SECURITY.md](SECURITY.md) | per the SLA in SECURITY.md |

## Promotion from community to first-party

Tier 3 (community-maintained) companion packages can be promoted to
Tier 2 (first-party best-effort) when all of the following are met:

- The companion has been **stable in production** at **≥ 5 deployments**
  for **≥ 6 months**.
- It has **comprehensive tests** — meeting the relevant contract test
  pack where one exists (e.g., `IBlobStorageContract`,
  `IJobSchedulerContract`).
- It has **documentation** — at minimum a `README.md`,
  `getting-started.md`, and `api-reference.md` covering the public
  surface.
- It has an **identified maintainer** willing to commit to the Tier 2
  response SLA on issues and PRs.
- It passes a **maintainer security review** focused on the package's
  threat model (cross-tenant data, secrets handling, network surface,
  etc.).
- Project maintainers commit to **Tier 2 maintenance going forward**.

Promotion is initiated by opening an issue tagged `promotion-request`.
Promotion from Tier 2 to Tier 1 is reserved for packages that become
load-bearing for the SDK's reference implementation; it requires
maintainer-board approval and is rare.

## Style and conventions

The SDK is written in F# with full-stack discipline (Giraffe over
ASP.NET Core on the server; Fable + Elmish + Feliz on the client; FAKE
for build). Detailed conventions live in `CLAUDE.md` at the repo root —
the AI-assistant context file doubles as the contributor style guide.
Highlights:

- **Formatter:** Fantomas. Run `dotnet fantomas <changed-files>` before
  committing. CI rejects unformatted code.
- **F# idioms:** Use `_.Property` shorthand instead of
  `(fun x -> x.Property)`; avoid the indexer-ambiguity pattern
  (`map[k] x y` — extract to a `let`-bound name first).
- **Elmish MVU purity:** `update` functions are pure; side-effects flow
  through `Cmd`; no module-level mutables.
- **Module discipline:** No cross-module `open` statements; modules
  declare what they need via `NeedsData` and what they provide via
  `ProvidesProcessedData`; the shell wires everything.
- **Comments:** Default to no comments. Add a comment only when the
  *why* is non-obvious — a hidden constraint, subtle invariant, or
  workaround for a specific bug.
- **Tests:** Server-side tests run on Expecto. Each public-surface
  interface should have a contract test pack that companion
  implementations can run against themselves.

## Security-affecting changes

The SDK publishes a versioned, evidence-cited statement of what it
enforces:
[`docs/security/PLATFORM-SECURITY-RULES.md`](docs/security/PLATFORM-SECURITY-RULES.md).
Compliance officers and vendor-risk assessors cite it directly, and every
rule in it carries an `Evidence:` line pointing at real paths in this
repository. That only stays true if contributors keep it true.

**If your change touches a rule documented there, refresh that rule's
cited evidence in the same commit.** Concretely:

- **Moving or renaming a cited file** — update every `Evidence:` line
  naming it.
- **Changing what a cited mechanism enforces** — update the rule text.
  If the guarantee got *weaker*, say so explicitly and name the version.
- **Adding a new structurally-enforced guarantee** (a new fail-closed
  default, a new preflight refusal, a new always-on middleware) — add the
  rule, with evidence, in the commit that ships the guarantee.
- **Removing a guarantee** — move the rule into the out-of-scope section
  rather than deleting it, so a reviewer comparing two ruleset versions
  can see what changed.
- **Bumping `<ToolUpSdkVersion>`** — restamp the `RulesetVersion` header.

An evidence pointer that no longer resolves turns a compliance artefact
into a liability, so treat a stale one as a defect rather than a
documentation nit. The full versioning policy and the reasoning behind
this cadence are in
[`docs/security/README.md`](docs/security/README.md).

Reporting a suspected vulnerability is a different path — do **not** open
a public issue or PR. See [`SECURITY.md`](SECURITY.md).

## Maintainer setup

These steps apply only when publishing NuGet packages — most
contributors never need this.

### Publishing NuGet packages

CI publishes via [`publish-nuget.yml`](.github/workflows/publish-nuget.yml)
on every `v*.*.*` tag push, to two feeds: [nuget.org](https://www.nuget.org)
(the default consumer source — anonymous restore) using the `NUGET_API_KEY`
repo secret, and [GitHub Packages](https://github.com/orgs/ToolUp-Forge/packages)
(secondary/failover) using the Actions-provided `GITHUB_TOKEN`. No
contributor-side token setup is needed for that path; the nuget.org step
skips automatically where the secret is absent (e.g. forks).

For a **local manual publish** (e.g. testing the publish path before
tagging), the FAKE `Publish` target reads its key from the environment,
matched to the target source. GitHub Packages (the default source) reads
`GITHUB_PACKAGES_TOKEN` — a [GitHub PAT](https://github.com/settings/tokens)
with the `write:packages` scope:

    $env:GITHUB_PACKAGES_TOKEN = "<your-pat>"
    dotnet run --project Build.fsproj -- Publish

nuget.org reads `NUGET_API_KEY` — an [api key](https://www.nuget.org/account/apikeys)
with push scope on the `ToolUp.*` glob:

    $env:TOOLUP_PUBLISH_SOURCE = "https://api.nuget.org/v3/index.json"
    $env:NUGET_API_KEY = "<your-key>"
    dotnet run --project Build.fsproj -- Publish

The target is idempotent (`--skip-duplicate`) — re-running after a
transient failure cleanly skips versions already published.

## Where to ask questions

- **General questions and discussion:** GitHub Discussions on the
  Forge repository (post-OSS launch).
- **Bug reports:** GitHub Issues. Include the SDK version, your
  deployment mode (`Anonymous` / `Individual` / `Team` / `MultiTeam`),
  reproduction steps, and any relevant logs (sanitised of secrets).
- **Security issues:** Follow [SECURITY.md](SECURITY.md). Do **not**
  open a public issue.
- **Trademark or licensing questions:** trademark@toolup.pro for
  trademark; otherwise the LICENSE / NOTICE / TRADEMARK files cover
  most cases.

Welcome aboard.

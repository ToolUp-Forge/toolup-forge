# Release process — stable releases, release candidates, and the soak gate

Every `ToolUp.*` package in the lockstep line is published to **nuget.org** by
[`publish-nuget.yml`](../../.github/workflows/publish-nuget.yml), and only by it: a tag push triggers
it, Trusted Publishing mints a one-hour key, and nothing else in this repository can publish. This page
is the operator's flow for the two channels that workflow serves.

| Channel | Tag | Packs as | Who resolves it |
|---|---|---|---|
| **Stable** | `vX.Y.Z` | `X.Y.Z` | every consumer |
| **Release candidate** | `vX.Y.Z-rc.N` (N ≥ 1) | `X.Y.Z-rc.N` (SemVer-2 prerelease) | only a consumer that names the prerelease or opts into prereleases |

**The tree always declares the bare version it is heading for** (`<Version>1.0.0</Version>` in
`Directory.Build.props`). The `-rc.N` label lives only on the tag. So there is never a commit in which
the tree claims to be a candidate, and promotion is a new tag — not a version edit.

A consumer that did not ask for a prerelease never resolves one: NuGet excludes prereleases from
floating and latest-stable resolution. That is what keeps the stable channel untouched by a candidate,
and it is standard NuGet behaviour, not something this repository enforces.

## What the workflow checks, in order, before any key exists

1. **`VerifyReleaseChannel`** (Phase 263) — reads the tag and refuses:
   - any tag shape other than `vX.Y.Z` / `vX.Y.Z-rc.N` (`-rc1`, `-rc.0`, `-beta.1`, `+meta` are all
     refused — the `v*.*.*` trigger matches them, and before this check every such tag published the
     tree's `<Version>` as a *stable* release);
   - a tag whose `X.Y.Z` is not the tree's `<Version>`, and a `<Version>` carrying any suffix;
   - a candidate of a version already released stable, or numbered behind an existing candidate;
   - a stable release that must be **promoted** (below) when the candidate has not soaked.

   For a candidate it exports `TOOLUP_RELEASE_CORE` / `TOOLUP_RELEASE_PRERELEASE` to the rest of the
   job, and [`Directory.Build.targets`](../../Directory.Build.targets) stamps `-rc.N` onto every
   lockstep package. Packages on their own version line (the `-alpha` companions, the Stripe line) are
   **skipped** for a candidate run — an RC must never be the run that first publishes a stable version
   of anything.
2. **`GenerateSdkManifest --check`** (Phase 326) and **`VerifySemVerBump`** (Phase 260) — unchanged.
3. Login, pack, push, provenance attestation — unchanged.
4. **`published-package-smoke`** (Phase 184) — restores the just-published version from nuget.org
   **anonymously**, outside the checkout, and compiles against it. On a candidate tag it probes
   `X.Y.Z-rc.N`, so every candidate publish carries its own restore proof.

The trigger block is tag-only plus manual dispatch, and the Build test pack lints that on every push
(`ReleaseChannel.publishWorkflowFindings`): a branch, pull-request, schedule, `workflow_run` or
`release` trigger would turn an ordinary push into a release.

## Cutting a release candidate

All commands are PowerShell, run from the repository root on an up-to-date `main`.

```powershell
git pull --ff-only
dotnet run --project Build.fsproj -- VerifySemVerBump             # the declared <Version> covers the surface diff

# Dry-run the channel check exactly as the workflow will run it:
$env:TOOLUP_RELEASE_REF = 'v1.0.0-rc.1'
dotnet run --project Build.fsproj -- VerifyReleaseChannel
Remove-Item Env:TOOLUP_RELEASE_REF

git tag -a v1.0.0-rc.1 -m "v1.0.0-rc.1"
git push origin v1.0.0-rc.1                                        # this push publishes — it cannot be taken back
gh run watch (gh run list --workflow publish-nuget.yml --limit 1 --json databaseId --jq '.[0].databaseId')
```

The next candidate number is whatever `VerifyRcPromotion` names when it refuses; it never goes
backwards.

## How a consumer validates a candidate

Pin the candidate explicitly. With the `ToolUp.Sdk` meta-manifest that is one property:

```xml
<ToolUpSdkVersion>1.0.0-rc.1</ToolUpSdkVersion>
```

Without it, pin each package: `<PackageVersion Include="ToolUp.Platform.Core" Version="1.0.0-rc.1" />`.
No feed credential is involved — nuget.org serves candidates anonymously, like everything else. A
consumer that wants to track "the newest candidate" can float `1.0.0-rc.*`; one that does nothing
keeps resolving the stable line.

Packages on their own version line are not re-published under a candidate: keep their existing pins
(`VersionOverride` where the meta-manifest is in use).

Report what you find against the candidate before the soak window closes — a defect that needs a
public-surface change re-rolls the candidate (below), and the window restarts.

## The soak gate — when a candidate may be promoted

A candidate is **promotion-eligible** only when all of these hold:

| Rule | Measured by |
|---|---|
| nuget.org has served it for **≥ 14 days** | the registration leaf's `published` time for every package the smoke job probes — the latest of them starts the clock; unlisted or unreadable is *not* soaked |
| **no public-surface movement** since it | the api-baselines at the candidate tag vs. the promoting tree, classified by the same comparer `VerifySemVerBump` uses (the doc-coverage sidecar excluded) — *any* movement, additive included, because what would be promoted must be what soaked |
| the promoting commit **descends** from it | `git merge-base --is-ancestor` |
| the Phase 257 **scorecard is all-green** | `V1Readiness`, every row `pass` |

The window is the `soakWindow` constant in `ReleaseChannel.fs` — a reviewed commit changes it, not a
run.

**Which stable releases the gate applies to.** Every new major at or past 1.0 (`X.0.0`, X ≥ 1) — it
cannot be tagged without a soaked candidate — and any version that has a candidate at all. Every other
stable release (the 0.x line, 1.x minors and patches cut without a candidate) publishes exactly as
before.

**Where each rule is enforced.** The workflow enforces the window, the surface and the lineage on the
stable tag itself, before a key exists. The scorecard is enforced by the operator before tagging,
because its adoption row reads consumer facts the public workflow has no business reaching.

**Behaviour-only changes are not surface.** A fix that moves no public signature does not re-roll the
candidate under the gate, but it does ship in the stable release un-soaked. Promote from the
candidate's own commit where you can; re-roll for any substantive fix.

## Promoting

```powershell
git fetch --tags
git checkout v1.0.0-rc.3                          # or a descendant with no surface movement since it
$env:TOOLUP_ADOPTION_MATRIX = '<path to the generated adoption matrix>'   # the scorecard's adoption row
$env:TOOLUP_ADOPTION_CONSUMERS = '<consumer names, comma-separated>'
dotnet run --project Build.fsproj -- VerifyRcPromotion   # exit 0 and "ELIGIBLE", or the list of what is missing
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
git checkout main
```

The workflow re-checks window, surface and lineage on the `v1.0.0` tag and refuses the publish if any
has changed since you ran `VerifyRcPromotion`. The environment-variable names the scorecard reads are
`V1Readiness.matrixEnvVar` / `consumersEnvVar`; `dotnet run --project Build.fsproj -- V1Readiness`
writes `docs/reference/v1-readiness.md`, which names every row's input.

## What re-rolls a candidate

Tag the next candidate (`vX.Y.Z-rc.N+1`) from the commit you now want to promote, and the soak
restarts, when:

- the public surface moved since the candidate (any package, additive or breaking);
- the commit you want to promote does not descend from the candidate;
- a defect found during the soak needs a fix you are not willing to ship un-soaked.

A candidate is never re-published under the same number — nuget.org versions are immutable — and a
candidate you want withdrawn from consideration should be **unlisted** on nuget.org, which also makes
it ineligible for promotion.

## Healing a failed run

A run that failed after the push (a transient index or network failure) is re-run **on the tag**, so a
candidate is healed as a candidate:

```powershell
gh workflow run publish-nuget.yml --ref v1.0.0-rc.1
```

A dispatch from a branch packs the tree's `<Version>` on the stable channel — never use one to publish
ahead of a tag. `--skip-duplicate` makes a re-run skip what already landed.

## Smoke-testing the channel end to end

The offline rules are proven on every push; the one thing only a real publish shows is that a
candidate lands on nuget.org and restores anonymously while the stable channel stays put. After the
first candidate publish:

```powershell
$probe = Join-Path ([IO.Path]::GetTempPath()) "rc-probe-$(Get-Random)"   # OUTSIDE any repository
dotnet new console -lang F# -o $probe
Push-Location $probe
dotnet add package ToolUp.Platform.Core --version 1.0.0-rc.1    # the candidate restores, no credential
dotnet add package ToolUp.Platform.Core                          # no version: resolves the latest STABLE
Select-String -Path *.fsproj -Pattern 'ToolUp.Platform.Core'    # shows the stable version, not the candidate
Pop-Location; Remove-Item -Recurse -Force $probe
```

**Tagging a candidate commits the version it names to the gate.** A `vX.Y.Z-rc.1` tag makes `X.Y.Z`'s
stable release answer to the soak gate — 14 days, and no surface movement from that point until
promotion. Choose the version for a first smoke test with that in mind.

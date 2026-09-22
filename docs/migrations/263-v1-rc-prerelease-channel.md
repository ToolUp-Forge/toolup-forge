# Phase 263 — the release-candidate channel and its soak gate

**Stability impact:** additive. **Consumer action required:** none on the stable line (⛔ N/A). A
consumer that wants to validate the frozen surface before a stable major opts in by pinning a
candidate — see [the release process](../platform/release-process.md#how-a-consumer-validates-a-candidate).

## What changed

Release tooling only. **No shipped package's code or public surface changed**, and `<Version>` did not
move.

1. **A second channel on nuget.org, for majors only.** A tag `vX.0.0-rc.N` (X ≥ 1) now publishes the
   lockstep packages as the SemVer-2 prerelease `X.0.0-rc.N`; the first is `1.0.0-rc.1`. There are no
   candidates before 1.0 and none for minors or patches (operator decision 2026-09-22). NuGet never resolves a prerelease for a consumer that did not ask
   for one, so the stable channel is unaffected by construction.
2. **`VerifyReleaseChannel`** runs first in `publish-nuget.yml`, before a credential is minted. It
   refuses any tag other than `vX.Y.Z` / `vX.0.0-rc.N` (X ≥ 1) and any tag that does not name the tree's
   `<Version>`. **This also closes a pre-existing defect:** until now every `v*.*.*` tag published the
   tree's `<Version>` whatever the tag said — a `v1.0.0-rc.1` tag on a tree declaring `1.0.0` would
   have published a *stable* `1.0.0`.
3. **The soak gate.** A major from 1.0 on is promoted only from a candidate nuget.org has served for
   the soak window (14 days by default; `release-channel.json` carries the default and per-release
   overrides, each with a mandatory reason), with no BREAKING surface change since it (additive growth
   does not block), from a descendant commit, with the Phase 257 scorecard all-green. The workflow
   enforces the first three on the stable tag; `VerifyRcPromotion` enforces all four before the tag is
   pushed. 0.x releases, minors and patches publish stable with no soak check.
4. **Two prerelease-aware rules in the existing release tooling.** `VerifySemVerBump` measures from a
   stable release in preference to its own candidates (all parse to the same core), and `Changelog`
   does not render a candidate as a release.

## For a consumer validating a candidate

```xml
<!-- Directory.Packages.props -->
<ToolUpSdkVersion>1.0.0-rc.1</ToolUpSdkVersion>
```

or pin individual packages at `1.0.0-rc.1`. Anonymous restore from nuget.org, no feed credential.
Revert the pin (or move it to the stable version) once the candidate is promoted. Packages that version
on their own line are not re-published under a candidate — keep their existing pins.

## Verification

```powershell
dotnet run --project src/ToolUp.Platform.Build.Tests    # the ReleaseChannel list: every rule, both directions
$env:TOOLUP_RELEASE_REF = 'v0.23.0-rc.1'; dotnet run --project Build.fsproj -- VerifyReleaseChannel; Remove-Item Env:TOOLUP_RELEASE_REF   # refused: not a major
```

## Rollback

Delete `Directory.Build.targets`, remove the `VerifyReleaseChannel` step from `publish-nuget.yml`, and
revert `ReleaseChannel.fs` (and `release-channel.json`) with its registrations. Nothing published changes; the workflow returns to
publishing `<Version>` for every tag — including the defect in point 2.

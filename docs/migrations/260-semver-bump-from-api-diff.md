# Phase 260 — the SemVer bump, derived from the API diff

**Stability impact:** additive. **Consumer action required:** none.

## What changed

Release tooling only. **No shipped code changed behaviour** and no package version moved, so a
consumer deployment is byte-for-byte what it was (GP 11 / GP 13).

1. **A new FAKE target, `VerifySemVerBump`**, computes the bump the public surface demands and
   checks the `<Version>` in `Directory.Build.props` against it:

   ```powershell
   dotnet run --project Build.fsproj -- VerifySemVerBump            # check — fails on an under-bump
   dotnet run --project Build.fsproj -- VerifySemVerBump --propose   # report only
   ```

   It **never edits the version.** The proposal is advice, the check is a floor: a version at or
   above the demand passes, and a deliberately larger bump is never wrong.

2. **It runs at release**, from [`publish-nuget.yml`](../../.github/workflows/publish-nuget.yml),
   beside Phase 326's meta-manifest check and before the NuGet login step — a release that cannot be
   versioned correctly should fail without minting a credential first.

3. **The rule it applies is decided on every commit**, by `SemVerBumpTests` in the
   `ToolUp.Platform.Build.Tests` pack (so: `VerifyAll`, `verify.ps1`, and the `verify-all` CI job).
   The module is source-linked into both the FAKE project and the test pack rather than copied —
   the same arrangement `SdkManifest.fs` (326) and `PublishedSmoke.fs` (184) use, and for the same
   reason: a gate and its go-red proofs must not be able to reach different conclusions.

## What it diffs, and why that is not what the phase shard asked for

The shard (2026-06-27) asked for the diff between the **working public surface** and the approved
`api-baselines/`. That was the right measurement when it was written, and Phase 618 has since made
it the wrong one.

618 made the public-API approval gate fail in **both** directions — a removal is breaking, and an
unfolded addition is red too. So on any tree that passes `verify.ps1`, the working surface and its
committed baselines are **identical by construction**. A bump derived from that difference would
classify every green tree as `unchanged`, demand a patch, and — because this check only fails on an
under-bump — never fire at all.

The measurement that survives 618 is the interval between **releases**: the committed baselines at
the last release tag versus the committed baselines now. And 618 is precisely what makes it faithful
rather than approximate — each side of that diff is an exact mirror of the public surface at that
commit, because a commit where it was not would have failed the gate. The dependency runs the
opposite way from how it looks: **this check is trustworthy because the approval gate is strict**,
and relaxing 618 would silently cost this one its meaning.

A practical consequence: the check reads text and `git`, so it needs no build, no
`MetadataLoadContext` and no network. It classifies 53 moved baselines in about two seconds.

## The policy it applies

The 0.x table is **this repo's own**, not a new one. `Directory.Build.props` states it twice —
"minor bumps may include breaking changes during 0.x; patch bumps remain non-breaking", and at the
0.23.0 entry, "MINOR is where this repo's SemVer-on-0.x policy puts a break".

| Surface change | Demanded while the released line is `0.x` | Demanded from `1.0.0` |
|---|---|---|
| a public member removed, renamed or retyped, or a package withdrawn | **minor** | **major** |
| public surface added only (a new member, a new package, an `[<Obsolete>]` marking) | **patch** | **minor** |
| no surface change | **patch** | **patch** |

Both tables are pinned by tests. The live one is the 0.x column; the other becomes live at 1.0.0,
and pinning only today's would let the 1.0 transition quietly change what the gate means on the one
release where it matters most.

Which table applies is selected by the **released** version, not the declared one: the compatibility
promise in force is the one attached to the version a consumer is already holding.

The per-package classes roll up to the single lockstep `<Version>` by taking the highest — one
breaking package makes the release breaking, however many others did not move.

## Two details that are easy to get wrong

**The release point is the newest tag strictly BELOW the declared version**, not simply the newest
tag. `publish-nuget.yml` runs on the `v*` tag push, so by the time the check executes there the tag
being released already exists and points at HEAD. Measuring against it would compare the release
with itself, find no diff, and then fail the version for not having advanced past itself. Defining
the release point this way makes the check give the same answer before and after the tag is cut.

**A withdrawn package is called out separately.** A baseline present at the release and absent now
means a package that stopped being produced — the one break a version number cannot express, since a
consumer that raises its pin without dropping the `PackageReference` fails at *restore* (`NU1101`)
rather than at compile. It classifies as breaking and the report names it as a withdrawal.

## What it said on the release it was written against

Run at `0.23.0`, with `v0.22.0` the newest tag:

```
Public surface since v0.22.0 (0.22.0): breaking — 53 package baseline(s) moved.
  breaking   ToolUp.AuthProviders.EntraExternalId — PACKAGE WITHDRAWN (+0 / -25)
  breaking   ToolUp.AuthProviders.EntraExternalId.Client — PACKAGE WITHDRAWN (+0 / -21)
  breaking   ToolUp.Platform.Client (+1911 / -1175)
  …
  additive   Feliz.AgCharts — new package (+333 / -0)
Demanded bump: minor (SemVer-on-0.x policy) → proposed 0.23.0. Declared: 0.23.0 (a minor over 0.22.0).
```

That is the same conclusion `Directory.Build.props` reaches in prose — six times, in six hand-written
paragraphs added by Phases 739, 741, 743, 737, 752 and 749, each arguing that the slot must be a
minor and that a second bump would publish a version nobody asked for. The check does not replace
those notes; they carry the *reason*, which no diff can. It removes the need for the arithmetic in
them to be right by hand.

## If the check fails

- **`UNDER-BUMP`** — the declared version claims a smaller change than the surface made. The report
  names the smallest version that satisfies the diff and the removed members that make it breaking.
  If a removal is intentional, the bump is the answer; if it is not, the surface change is the bug.
- **no release point visible** — no release tag below the declared version was found. This is a
  failure rather than a pass: a shallow checkout fetches no tags, so "I can see no release" and
  "there has been no release" are the same observation from inside the check, and a release gate
  reporting green on a comparison it did not make is worse than no gate. Remedy:
  `git fetch --tags --force`.
- **a tag that names no commit** — `TOOLUP_SEMVER_BASE` was set to something misspelt or unfetched.

`TOOLUP_SEMVER_BASE` names the tag to measure from explicitly; its name must parse as a version,
because the released version is what selects the policy table.

## Consumer adoption

⛔ **N/A.** This is forge's own release hygiene — a FAKE target, a repo-root build module and a test
pack. There is no consumer-visible surface, no package changed, and nothing to adopt.

# Phase 184 — Fresh-machine published-package CI smoke gate

**Consumer action: none.** This is CI-only and additive. No SDK source, no published surface, no
package layout changed. Read this if you maintain the release, add a package, or wonder why the
release run has a job after `publish`.

## What changed

| Where | What |
|---|---|
| `.github/workflows/publish-nuget.yml` | a `published-package-smoke` job, `needs: publish`, on the `v*.*.*` tag path only |
| `Build.fs` | a `VerifyPublishedPackages` FAKE target — the whole mechanism |
| `PublishedSmoke.fs` *(new, repo root)* | its offline-decidable rules; FAKE-free and BCL-only |
| `src/ToolUp.Platform.Build.Tests/PublishedSmokeTests.fs` *(new)* | 43 cases proving those rules in both directions, plus a lint over the committed workflow |

Nothing runs on a push or a PR except the test pack, which is pure. `checks.yml` is untouched.

## Why

"A fresh machine can `dotnet add package` the SDK and build it" was a Wave-5.5 exit criterion, run
**once, by hand**. Nothing re-ran it after any later tag.

Every other gate in this repo proves something about the **tree**. A nupkg is a different artefact,
and two classes live only in it:

- a transitive `PackageReference` that a same-repo `ProjectReference` silently supplied, so the
  solution builds and a consumer's restore does not;
- a `fable/`-packed source path that stops extracting, so the DLL is fine and a consumer's Fable
  compile is not.

Both surface first in an adopter's `dotnet add package`. This closes that.

## What the gate does

On a release tag, after the publish push:

1. **Reads the version off the tag** — `refs/tags/v0.23.0` → `0.23.0`. No copy of the number lives
   in the workflow, so none can drift from what was just pushed. Off the tag path (a
   `workflow_dispatch` heal) it falls back to `<Version>` in `Directory.Build.props`, which is what
   a dispatch packs.
2. **Reconciles the probe closure against `SdkManifest.expected`** — the computed packable set, not
   a second hand-written list. See the finding below.
3. **Waits for nuget.org to index the push.** A push is *accepted* before it is *served*; probing
   immediately records a false red against a release that is fine (the Phase 255 lesson). Polls the
   flat-container index for up to 20 minutes, then fails with a message that names both readings —
   "the publish did not push this" and "the index is slow" — rather than asserting either.
4. **Scaffolds a probe outside the repository**, under the system temp dir, with a `<clear />`d
   `nuget.config` naming only nuget.org. Anonymous restore, no credential, no local feed: exactly
   the path an external adopter takes.
5. **Builds a DLL-tier probe** against `ToolUp.Platform.Core` / `.Server` / `ToolUp.AI.Core` /
   `.Server`.
6. **Transpiles a Fable-tier probe** against `ToolUp.Platform.Client` — `dotnet fable -o output
   --noCache` — and fails if it emitted no `.js`, because Fable exits 0 having matched nothing.

Measured against the published 0.22.0 from a developer machine: **33 seconds**, 364 `.js` emitted.

## Running it yourself

```powershell
# the current Directory.Build.props <Version> (only useful once it is published)
dotnet run --project Build.fsproj -- VerifyPublishedPackages

# aim it at any past release
$env:TOOLUP_PUBLISHED_REF = "v0.22.0"
dotnet run --project Build.fsproj -- VerifyPublishedPackages
```

`TOOLUP_PUBLISHED_INDEX_TIMEOUT_MINUTES` shortens the index wait (useful for seeing the red).

It is deliberately **not** in `BuildConfig.TestPacks` and not in `verify.ps1`: it needs the network,
and it needs the version under test to be *published*, which on a developer checkout it is not by
construction — `<Version>` is always the next, unreleased number. A gate that is red on a fresh
clone for a reason nobody can fix is one people learn to skip.

## Two things the phase shard asked for that are not here, and why

**There is no `ToolUp.AI` package.** The shard's probe list named `ToolUp.Platform.Core` +
`ToolUp.AI` + `ToolUp.Platform.Server`. `src/ToolUp.AI/` holds a README and two `.props` files and
produces no `.fsproj`; nothing packs that id and nuget.org serves no blob for it. A probe declaring
it would have failed every release with `NU1101` — a red asserting "the release is broken" about a
defect in the probe. The AI tier's real ids are `ToolUp.AI.Core` / `.Server` / `.Client`, and the
probe takes the first two.

That is the stranger's-package class (Phase 307, guarded in-repo by `TemplateGate.unproducedIds`
since Phase 754) one level up, so it is guarded the same way rather than fixed once:
`PublishedSmoke.unpublishedProbeIds` reconciles the declared closure against
`SdkManifest.expected`, and the Build pack fails if the shipped closure ever names an id the release
does not push.

**The gate is not a branch-protection required check, and cannot be.** The shard asked for one.
Branch protection gates pushes and pull requests to a **branch**; this job runs on a `v*.*.*`
**tag** push, so it never reports a status GitHub could require on `main`. Adding it to
CONTRIBUTING's required-check list would block every PR forever on a check that never arrives —
the exact hazard that section already warns about for a renamed job.

What enforcement exists is on the tag: a red probe is a red release run. Note what that means and
what it does not. nuget.org versions are immutable, so the packages are already published when the
probe fails; the remedy is a new patch version. The gate's value is that **CI** says the release is
unusable, minutes after the tag, rather than an adopter saying so days later.

## For a maintainer adding a package

Nothing is required. The probe closure is a deliberately small sample of the tiers, not an
enumeration of the ~166 published ids — its job is to prove that a restore from nuget.org resolves
and compiles, which one package per tier demonstrates. Add an id to
`PublishedSmoke.dllProbeIds` / `fableProbeIds` only when a new tier is genuinely a different
*shape* (a new packaging convention, a new source-in-nupkg layout), and the guard in step 2 will
tell you immediately if the id is not one this repo publishes.

## For a maintainer editing the workflow

`PublishedSmoke.smokeJobFindings` lints the committed `publish-nuget.yml` from the Build test pack
on every push, and will fail if the smoke job loses `needs: publish`, stops calling the target, or
**inlines the probe as workflow shell steps**. That last one is the reason the lint exists rather
than only the target's own guards: steps in the job run in the repository checkout, where
`nuget.config` files merge up the tree and MSBuild walks `Directory.Build.props` upward, so the
repo's own sources — including the workspace-shared local feed — join the resolve path. The probe
would then be green about a release no consumer can restore, and the diff that did it would look
entirely reasonable.

## SDK-ADOPTION.md

No row. The matrix is generated from each consumer's own `sdk-adoption.json` plus the forge phases
carrying `consumer_facing: true` (the derived-registry model, Phase 373) — it is not hand-edited,
and this phase is not consumer-facing: nothing a consumer composes, pins or calls changed.

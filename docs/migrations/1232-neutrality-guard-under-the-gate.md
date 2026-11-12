# Phase 1232 — the neutrality guard finds its list from any worktree, and an absent list is red under the gate

**What changes for a contributor:** nothing to edit. Keep the private token list as one untracked
`neutrality-tokens.local.txt` at the root of your primary checkout; every linked worktree of the clone now finds it
there. Under the local gate an absent list fails the publication-boundary guard instead of skipping it. No package
contract, pin or wire format moves; `ToolUp.Platform.Build` grows additively (below).

## Why

The guard read its list from the checkout root or from `TOOLUP_NEUTRALITY_TOKENS`. A linked worktree has no copy of
an untracked file, so a gate run in a worktree saw no list, enforced only the public canary and reported a loud skip,
and a skip is not a failure: the landing went green over a guard that had not run. A word the boundary bans could
reach `main` that way, and did.

## What changed

- **Where the list is looked for.** `Neutrality.externalTokens` (`ToolUp.Platform.Build`) takes the first source that
  supplies a token, in order: the checkout root; the primary checkout's root, the parent of
  `git rev-parse --git-common-dir`; then the environment variable. `Neutrality.tokenFiles` and `Neutrality.searched`
  name those places, and the absent-source message names all of them.
- **A skip is red under the gate.** `TOOLUP_NEUTRALITY_REQUIRED=1` (or `true`) turns the absent-source case from a
  skip into a failure with the same message (`Neutrality.RequiredVariable`, `Neutrality.isRequired`). The local gate
  (`VerifyAll`, as `verify.ps1` runs it) sets the variable for the packs it launches unless it is already set or `CI`
  is set: a public CI runner holds no private list by construction and keeps the canary-only skip. The `Platform` and
  `Platform.Remoting` rows of the gate manifest now declare both variables as reads, so the scrubbed environment each
  pack runs under names them.
- **The standing population was cleared first**, so the stricter gate lands green: 7 hits in 3 files under `docs/`,
  as measured with the list available when the phase landed. 2 were reworded (a migration note named private
  consumer trees; it now says "first-party consumer trees" and its command takes placeholder roots). 5 are citations
  of a public, Apache-2.0 specification by its repository slug, marked `OSS-BOUNDARY-EXEMPT` with that reason (three
  block markers).

## Additive surface (`ToolUp.Platform.Build`)

`Neutrality.RequiredVariable`, `Neutrality.isRequired`, `Neutrality.primaryRoot`, `Neutrality.tokenFiles`,
`Neutrality.searched`. `Neutrality.externalTokens` keeps its signature.

## Verification

```powershell
# from a linked worktree, with the list only at the primary checkout's root and no variable set:
dotnet run --project src/ToolUp.Platform.Tests.Remoting/ToolUp.Platform.Tests.Remoting.fsproj -- `
    --filter "ToolUp.Platform.Tests.Remoting.OpenCoreVocabularyNeutrality (Phase 477)"
# 14 tests, 0 ignored: the surface scan ran with the full list
```

With `TOOLUP_NEUTRALITY_REQUIRED=1` and no list reachable, the same run fails naming the three places; with the
variable unset it reports the loud skip.

## Rollback

Revert the phase's commits. The list file needs no change: a primary checkout reads it from its own root either way.

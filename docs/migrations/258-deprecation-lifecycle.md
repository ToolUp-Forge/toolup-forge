# Phase 258 — deprecation lifecycle (`[<Obsolete>]` policy + baseline-guard integration)

**Stability impact:** additive. **Consumer action required:** none.

## What changed

Three things, all test-tier and repo-baseline. **No shipped code changed behaviour**, so a consumer
deployment is byte-for-byte what it was (GP 11 / GP 13).

1. **A deprecation policy now exists in writing** —
   [`docs/platform/deprecation-policy.md`](../platform/deprecation-policy.md): the window (a marked
   member stays at least one full minor before the major that may remove it), the message format
   (name a replacement *and* a removal target), and the removal-only-at-a-major rule.

2. **The public-API approval gate now renders deprecations.** A member carrying `[<Obsolete>]` gets
   a marker line beside its unchanged token:

   ```
   ToolUp.Platform.AgGrid.ThemeClass (class)
   ToolUp.Platform.AgGrid.ThemeClass (class)  (obsolete)
   ```

   Phase 618 decided this shape and left the seam (`obsoleteMarker`) with nothing calling it, so
   until now a deprecation was invisible to the gate. Marking a member therefore scores as an
   **addition** — non-breaking, but unfolded, so the baseline must be regenerated in the same PR.
   Removing a member is still **breaking**. No exception was carved into the comparer; the
   distinction falls out of the rendering convention.

3. **The `[<Obsolete>]` message is gated.** A public deprecation whose message is empty, or which
   omits the replacement or the removal target, fails the `ToolUp.Platform.Tests` pack — and so
   fails `pwsh ./verify.ps1` and the `verify-all` CI job. The failure names the member, quotes the
   message, says which half is missing, and states that nothing is broken.

Three baselines gained marker lines — seven added lines in total, no removals: `Feliz.AgGrid` (1),
`ToolUp.Platform.Client` (5), `ToolUp.Platform.Server` (1). And three shipped notices gained a
removal target they were missing: `Feliz.AgGrid.ThemeClass`, its `ToolUp.Platform.AgGrid.ThemeClass`
re-export, and `RemotingHelpers.makePermissionGuardedApi` — whose deletion target was stated in its
doc comment, where a consumer reading only the compiler warning never sees it.

`ToolUp.Platform.Core` did **not** move, which is the policy's scope working as intended: its only
`[<Obsolete>]` members are in the vendored `TypeShape.fs`, which is `module internal` and therefore
not public surface. Vendored internals are out of scope by construction rather than by an exclusion
list anyone has to maintain.

## What a consumer has to do

**Nothing.** No public member was added, removed, retyped, or made to behave differently. The
`[<Obsolete>]` warnings a consumer already saw are unchanged except that three of them now also say
when the member goes away.

## What an SDK author has to do

Read [the policy](../platform/deprecation-policy.md) before marking anything. The short form:

```fsharp skip=fragment
[<System.Obsolete("Use Foo.bar instead. Removed in 1.0.")>]
```

then regenerate the affected baseline, scoped:

```powershell
$env:TOOLUP_APPROVE_API = "ToolUp.Platform.Client"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_API = $null
```

Two failures you may now meet that did not exist before:

- **`N public member(s) ADDED … (obsolete)`** — you marked something and did not regenerate. Expected;
  regenerate and commit the baseline with the change. The marker line *is* the addition.
- **`N public [<Obsolete>] marking(s) do not meet the deprecation-message policy`** — the notice is
  missing a replacement or a removal target. Fix the sentence, not the baseline.

## Rollback

Revert the commit. The gate returns to ignoring attributes entirely, the marker lines fall out of the
baselines at the next regen, and the message policy stops running. Nothing consumer-visible is
involved either way.

## See also

- [`docs/platform/deprecation-policy.md`](../platform/deprecation-policy.md) — the policy itself
- [`src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs`](../../src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs) — the renderer, comparer and message policy
- [`CLAUDE.md` § Public-API approval baselines](../../CLAUDE.md#public-api-approval-baselines-phase-175)

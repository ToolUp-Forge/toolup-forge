# Phase 261 — public XML-doc coverage gate

**Consumer action required: none.** This is a build-and-gate change. Every `ToolUp.*` package
behaves identically at runtime; the only difference a consumer can observe is that they now get
IntelliSense tooltips they did not get before.

## What changes

| | Before | After |
|---|---|---|
| `GenerateDocumentationFile` | unset (so: off) | `true`, repo-wide, in `Directory.Build.props` |
| nupkg contents | `lib/net10.0/<Name>.dll` | `lib/net10.0/<Name>.dll` **+ `<Name>.xml`** |
| Doc coverage of the frozen surface | measured by nobody | measured per assembly, floored, and gated |

Three of the repo's projects opt out in their own `.fsproj` and are unaffected
(`docs-snippets`, `src/Hosts/Docker`, `templates/safer-pack`).

## For a consumer of the packages

Nothing to do. Upgrade as usual.

The `.xml` sidecar beside each `lib/net10.0/*.dll` is what an IDE reads to show a member's
documentation on hover and in completion lists. It was never shipped before, so every `///` comment
this SDK carried was visible in the source and invisible the moment the package crossed a project
boundary. It is now shipped. Doc coverage of the public surface is currently partial — see
[`docs/platform/doc-coverage.md`](../platform/doc-coverage.md) for the census and the plan to raise
it before the 1.0 tag — so expect tooltips on some members and not yet on others.

If your build treats warnings as errors, note that this changes nothing on an F# consumer: F# has no
equivalent of C#'s `CS1591` ("missing XML comment for publicly visible type or member"), so an
undocumented SDK member produces no diagnostic in your build. The coverage question is answered by
the SDK's own gate, not by yours.

Package size grows by the size of the sidecar — for the largest assembly in the repo that is a few
hundred KB, and for most it is a few tens.

## For a contributor to this repo

One rule is new, and it applies whenever you add public surface:

> **A new public member on a packable `ToolUp.*` assembly should carry a `///` doc comment.**
> If it does not, the Public-API approval pack fails with a message naming the assembly, the
> undocumented count, and the two ways forward.

The gate rides the existing `VerifyAll` run — it is part of `ToolUp.Platform.Tests`' Public-API
approval pack, beside the Phase 618 surface-drift comparison and the Phase 258 deprecation-message
policy — so there is no new CI job, no new command, and no new switch. The failure is a coverage
*regression*, never an absolute threshold: improving coverage never fails.

Accepting a new level deliberately (a batch of surface that genuinely cannot carry docs yet, or a
regeneration that locks in an improvement) uses the same switch as the api-baselines:

```powershell
$env:TOOLUP_APPROVE_API = "ToolUp.Platform.Core"   # or a comma-separated list, or "1" for all
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_API = $null
```

then commit `api-baselines/doc-coverage.approved.txt` in the same PR, exactly as you would the
`.approved.txt` beside it.

## Rollback

Set `<GenerateDocumentationFile>false</GenerateDocumentationFile>` in `Directory.Build.props`. The
gate then reports its build-property precondition (`NO XML DOCUMENTATION FILES — …`) rather than a
coverage shortfall, which is the intended shape: it says the measurement cannot be taken, not that
the SDK documents nothing. Deleting `api-baselines/doc-coverage.approved.txt` as well removes the
gate's floor entirely and every assembly then fails as an unrecorded package — so roll back both, or
neither.

## See also

- [`docs/platform/doc-coverage.md`](../platform/doc-coverage.md) — the policy, the census and the
  ratchet plan.
- [`docs/platform/deprecation-policy.md`](../platform/deprecation-policy.md) — the sibling gate in
  the same pack (Phase 258).
- `src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs` — the implementation, and the reasoning
  for each design choice.

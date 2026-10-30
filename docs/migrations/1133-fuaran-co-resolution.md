# The Fuaran floors are gated beside the published Fuaran.UI

**Ships in:** 0.38.0. No public surface moves and no consumer code changes. This note is the release-notes carrier of
the open lead the gate records, and of the guidance for a consumer that composes the ToolUp fact tier with Fuaran-UI
pages.

**Affected:** a consumer that references a ToolUp package depending on Fuaran substrate packages (`Fuaran.Core.*`,
`Fuaran.Compute.*`; today the fact tier and the presentation builders) beside Fuaran.UI and its renderers.

## What changes

1. **A gate on the floors.** `VerifyAll` refuses a commit whose Fuaran substrate floor is ahead of the floor the newest
   published Fuaran.UI release was built against, across a breaking boundary, unless the lead is declared in
   [the open-core boundary](../platform/open-core-boundary.md#declared-leads). Before this, 0.35.0 pinned
   `Fuaran.Core.Column` 0.40.2 while every published Fuaran.UI was built on Core 0.38.1, and a consumer composing both
   failed to build until Fuaran.UI 0.98.1.
2. **A co-build probe.** Every full gate restores, builds, runs and Fable-compiles a scratch consumer of the fact tier
   beside the published Fuaran-UI renderers, from this release's own packages and nuget.org.

## The open lead in this release

| Family | Floor here | Newest published Fuaran.UI | Its floor | Broken until closed |
|---|---|---|---|---|
| `Fuaran.Compute` | `0.42.0` | `0.99.1` | `0.41.0` | None observed: `Fuaran.Compute.DataFrame` 0.42.0 ships the same Fable sources as 0.41.0, and the co-build probe passes beside Fuaran.UI 0.99.1. |

## What to do

Nothing, for a consumer on Fuaran.UI 0.98.1 or later. A consumer pinned to Fuaran.UI 0.97.0 or older that composes the
fact tier must move to Fuaran.UI 0.98.1 or later: the fact tier's Core floor (0.40.2) is ahead of what those releases
were built on.

## Verify

```powershell
dotnet run --project Build.fsproj -- VerifyFuaranCoResolution
dotnet run --project Build.fsproj -- VerifyFuaranCoBuild
```

## Rollback

Nothing to roll back in a consumer. In this repository the gate is two `VerifyAll` legs and one committed record
(`fuaran-ui-floors.json`).

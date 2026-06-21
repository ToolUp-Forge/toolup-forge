# Migration — Phase 175: public-API approval baseline (SemVer guard)

**Status:** additive, test-tier only. **No consumer source change is required, and none is possible to "adopt".** This phase adds a snapshot test over the public surface of the packable `ToolUp.*` assemblies plus a directory of committed baselines. A consumer deployment is byte-for-byte unchanged — nothing ships in any `ToolUp.*` package (GP 13). All consumer columns in the adoption matrix are ⛔ N-A.

## Why

The repo split (Phase 11.B) and the GitHub-Packages publish pipeline (Phase 11.F) gave the SDK a real, versioned published surface across ~90 packages — but nothing mechanically caught an accidental public-surface break between releases. A removed/renamed/retyped public member would only surface weeks later at a consumer's `<PackageReference>` bump. This phase shifts that detection left to PR time, enforcing the SemVer-on-`0.x` policy (GP 11: the public surface must not silently break within a major).

## What changed (forge-internal only)

- **`src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs`** — surface renderer + comparer. Discovers the packable set (mirrors the `Pack` glob: `IsPackable != false`, non-`.Tests`, non-analyzer), renders each assembly's public surface metadata-only via `System.Reflection.MetadataLoadContext`, and diffs against the committed baseline. Type names are rendered assembly-qualifier-free (no `Version=…` suffix) so a routine version bump produces **zero** diff.
- **`src/ToolUp.Platform.Tests/InProcess/PublicApiApprovalTests.fs`** — one Expecto case per packable assembly + four synthetic comparer fixtures (fails-closed on a removal, no false-positive on an addition, retype reads as a removal, comment/blank noise ignored). Wired into `Program.fs` (runs under the Platform pack / `VerifyAll`).
- **`api-baselines/<assembly>.approved.txt`** — committed surface baselines (one per packable assembly).
- **CPM / fsproj** — `System.Reflection.MetadataLoadContext` added to `Directory.Packages.props` and referenced from `ToolUp.Platform.Tests` only.

## Additive-vs-breaking policy

The comparer is direction-aware:

- A token in the **rendered surface but not the baseline** (a new public type/member) is **allowed** — additive growth is non-breaking under `0.x` minor/patch.
- A token in the **baseline but not the rendered surface** (removed / renamed / retyped) is a **breaking diff** — the test fails and names every lost token.

Accepting a deliberate break is a reviewed edit of the `.approved.txt` file in the same PR, regenerated in one step:

```powershell
$env:TOOLUP_APPROVE_API = "1"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_API = $null
```

The regeneration is deterministic (sorted by type then member), so a re-run produces no spurious reordering diffs; the baseline edit is the human checkpoint for any removal.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean (the canonical gate builds every packable DLL before `VerifyAll`, so each assembly's Debug output is present for the renderer).
- The Phase 175 pack is green under `dotnet run --project Build.fsproj -- VerifyAll` (Platform pack); injecting a synthetic removed member into any baseline fails the corresponding assembly's case with the lost signature named.

## Rollback

Delete `api-baselines/`, the two `.fs` files, the `Program.fs` registration line, and the two package entries. No consumer is affected.

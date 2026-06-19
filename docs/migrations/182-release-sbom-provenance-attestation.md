# Migration — Phase 182: release SBOM + build-provenance attestation

**Status:** additive, default-off. **No consumer source change is required to adopt.** Every published package gains a machine-checkable SBOM + signed build-provenance attestation; a consumer that vendors `ToolUp.*` packages into production *verifies* them, it does not change its build. A local `dotnet run -- Pack` / `-- Publish` with `TOOLUP_EMIT_SBOM` unset produces the exact same artefact set as before (GP 11 / GP 13).

## Why

The Distribution wave published ~60 `ToolUp.*` packages to GitHub Packages with **zero** attestation. A downstream consumer had no machine-checkable evidence that a given `ToolUp.Platform.Core.0.x.y.nupkg` was built from the tagged source by CI rather than swapped in transit or at rest. This phase adds two supply-chain affordances to the existing release path:

1. **Per-package SBOM** (CycloneDX 1.5 JSON) — the package's declared NuGet dependency bill of materials, read from the artefact's own embedded `.nuspec` so the SBOM reflects exactly what shipped.
2. **Signed build-provenance attestation** — GitHub's native `actions/attest-build-provenance` binds each artefact's SHA-256 digest to the workflow run (source commit + build environment) via an in-toto provenance statement signed through Sigstore.

## What changed

### 1. `SDK.Build.fs` — gated SBOM emission in the `Publish` target

New module [`Build/SDK.Sbom.fs`](../../src/ToolUp.Platform.Build/Build/SDK.Sbom.fs) (`ToolUp.Platform.Sbom`). After the per-fsproj pack loop, the `Publish` target calls `Sbom.emit`, which — **only when `TOOLUP_EMIT_SBOM` is set to a truthy value** (`1` / `true` / `yes` / `on`) — writes one `<id>.<version>.cdx.json` CycloneDX 1.5 SBOM next to each `.nupkg` in `./artifacts/`.

- **GP 11 / GP 13 — off by default.** Unset, `Sbom.emit` returns `[]` and writes nothing. The `--skip-duplicate` `.nupkg` push is byte-for-byte unchanged; symbol packages stay in `artifacts/` un-pushed exactly as today.
- The push loop filters to `*.nupkg`, so `.cdx.json` SBOMs are **never** pushed to the feed — they travel as separate CI artefacts.
- **GP 1 — no crypto dependency added to the Build package.** The optional provenance-sidecar signer is a structural `Sbom.SignArtefact = byte[] -> Async<Result<string,string>>` function, *not* a reference to `ToolUp.ArtefactSigning`. A deployment that has wired an `IArtefactSigner` (Phase 40) adapts it at its own `Build.fs` call site:

  ```fsharp
  let signer (s: IArtefactSigner) : Sbom.SignArtefact =
      fun bytes -> async {
          let! r = s.Sign bytes
          return r |> Result.map (fun sig' -> sig'.DetachedJws)
                   |> Result.mapError SigningError.describe
      }
  ```

  reusing the Phase 40 primitive rather than introducing a second signing stack. On the canonical CI path the signer is `None` — GitHub's native attestation covers the published artefacts.

### 2. `publish-nuget.yml` — provenance attestation + SBOM upload

- Permissions gain `id-token: write` (OIDC for the Sigstore signing identity) and `attestations: write` (record the attestation against the repo), alongside the existing `packages: write`.
- The pack+push step sets `TOOLUP_EMIT_SBOM: "true"` so SBOMs are emitted on the release path.
- After the push, `actions/attest-build-provenance@v2` attests `artifacts/*.nupkg`.
- `actions/upload-artifact@v4` retains the `artifacts/*.cdx.json` SBOMs as a downloadable `sbom-cyclonedx` run artefact (GitHub Packages NuGet has no SBOM slot). `if-no-files-found: warn` so a manual re-run that skips every duplicate doesn't fail the job.

### 3. Test — `ToolUp.Platform.Build.Tests`

New Expecto pack [`src/ToolUp.Platform.Build.Tests`](../../src/ToolUp.Platform.Build.Tests/SbomTests.fs) (wired into `dotnet run -- VerifyAll` as the `Build` pack). Pins the GP 13 contract: `Sbom.emit` writes nothing and returns `[]` when `TOOLUP_EMIT_SBOM` is unset; emits one CycloneDX SBOM per nupkg when set; the optional signer hook adds a `.sig` sidecar only when supplied.

## Verification path for consumers

A downstream consumer confirms an artefact's provenance + composition with no toolchain beyond the GitHub CLI:

### Verify build provenance

```powershell
# Download the package (e.g. from the GitHub Packages feed), then:
gh attestation verify ToolUp.Platform.Core.0.6.0.nupkg --repo ToolUp-Forge/toolup-forge
```

A success prints the verified provenance: the source commit (`sourceRepositoryDigest`), the workflow that built it (`.github/workflows/publish-nuget.yml`), and the runner. A failure (digest mismatch, wrong repo, or no attestation) exits non-zero — that is the machine-checkable "was this built from tagged source by CI" gate. `gh attestation verify` works offline against a downloaded bundle via `--bundle`, or online against GitHub's attestation API by default.

### Inspect the SBOM

The per-package CycloneDX SBOM is published as the `sbom-cyclonedx` run artefact on the release workflow run. Download it from the run's *Artifacts* section, or via `gh run download <run-id> --name sbom-cyclonedx`. Each `<id>.<version>.cdx.json` is standard CycloneDX 1.5 — feed it to any CycloneDX-aware tool (`cyclonedx validate`, dependency-track, `grype sbom:<file>`) to scan the declared dependency set for known vulnerabilities.

> **Scope note.** The SBOM enumerates the package's **declared NuGet dependencies** (the `.nuspec` dependency set), which is the package's first-order bill of materials. It is not a recursively-resolved transitive closure; for full transitive analysis, combine the per-package SBOMs or resolve against the feed. The repo-wide transitive licence inventory remains `THIRD_PARTY_NOTICES.md` (`dotnet run -- ThirdPartyNotices`).

## Rollback

Each change is additive and gated. To disable on CI without reverting code, drop `TOOLUP_EMIT_SBOM` from `publish-nuget.yml` (SBOMs stop) and remove the attestation/upload steps + the two added permissions. The `Sbom` module and its test are inert when the flag is unset. To remove entirely: delete `Build/SDK.Sbom.fs` + the `Sbom.emit` call in the `Publish` target + the `Build` test pack — no other call site references them.

## Verification (this phase)

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj` — 8 passed, 0 failed.
- `dotnet run --project Build.fsproj -- Pack` packs green; with `TOOLUP_EMIT_SBOM` unset, the artefact set is unchanged (GP 13).

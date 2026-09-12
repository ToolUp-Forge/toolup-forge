# ToolUp.Sdk meta-manifest reconcile (consumer migration)

**What changes.** `ToolUp.Sdk`'s `build/ToolUp.Sdk.props` — the file a consumer imports to resolve
every `ToolUp.*` package from a single `<ToolUpSdkVersion>` — now (a) parses, and (b) lists every
package the SDK publishes. It is generated from the projects the release actually packs, so it
cannot drift again.

**Scope.** Consumer-facing, and a genuine fix rather than an addition: on the previous release the
manifest **did not load at all**. Nothing about your own code changes; what changes is what a
restore does with it.

## The two defects

**1. The file was not well-formed XML, so MSBuild refused the entire import.** Its header comment
contained a nested comment — XML forbids a double hyphen inside a comment, so the outer comment
terminated early and the rest of the file became garbage. MSBuild reported:

```text
error MSB4024: The imported project file "...\ToolUp.Sdk.props" could not be loaded.
An XML comment cannot contain '--', and '-' cannot be the last character. Line 12, position 63.
```

Every consumer importing the manifest failed there. **No** `ToolUp.*` version resolved through it —
not the listed ones either. If you import `ToolUp.Sdk.props` today and see MSB4024, this is why, and
upgrading is the fix.

**2. The entry list had fallen far behind the published set.** It declared 59 ids where the release
publishes 166. The 105 genuinely-missing ones included every package the phase that filed this was
opened for — `ToolUp.Secrets.GcpSecretManager`, `ToolUp.AuditSinks.AzureBlobArchive`,
`ToolUp.AuditSinks.GcsArchive`, `ToolUp.Cloud.Azure`, `ToolUp.Cloud.Aws`, `ToolUp.Cloud.Gcp` — plus
`ToolUp.ArtefactSigning.GoogleCloudKms`, `ToolUp.Encryption.GoogleCloudKms`,
`ToolUp.Hosts.GoogleCloudFunctions`, `ToolUp.Media.CloudTranscode` and ninety-five others. Under
central package management a `<PackageReference>` to any of them resolved no version, which is
`NU1008`. One entry (`ToolUp.X`, from the header's worked example) named a package that has never
existed.

## What you need to do

**Repin, and delete any workaround.** Bump `<ToolUpSdkVersion>` to a release carrying this fix. Then
remove the per-package `<PackageVersion>` overrides you added to work around the omissions — the
manifest now supplies them, and a local override silently pins that one package to whatever version
you wrote when everything else moves.

```xml
<!-- Directory.Packages.props — before -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <ToolUpSdkVersion>0.22.0</ToolUpSdkVersion>
  </PropertyGroup>
  <Import Project="$(NuGetPackageRoot)toolup.sdk\$(ToolUpSdkVersion)\build\ToolUp.Sdk.props" />
  <ItemGroup>
    <PackageVersion Include="ToolUp.Cloud.Azure" Version="0.22.0" />
    <PackageVersion Include="ToolUp.Secrets.GcpSecretManager" Version="0.22.0" />
  </ItemGroup>
</Project>
```

```xml
<!-- Directory.Packages.props — after -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <ToolUpSdkVersion>0.23.0</ToolUpSdkVersion>
  </PropertyGroup>
  <Import Project="$(NuGetPackageRoot)toolup.sdk\$(ToolUpSdkVersion)\build\ToolUp.Sdk.props" />
</Project>
```

Deliberate per-package pins are still supported and still win — declare them **after** the import,
exactly as before. Only the ones that existed solely to paper over a missing entry should go.

## Verification

```powershell
dotnet restore
```

A restore that previously failed with MSB4024, or with NU1008 naming a `ToolUp.*` package, now
succeeds. To see the resolved pin for one package without a full restore:

```powershell
dotnet msbuild <your.fsproj> -getItem:PackageVersion
```

## Three packages the manifest deliberately does not list

Each is published, and each is excluded because a `<PackageVersion>` entry could not help a
consumer:

| Package | Why |
|---|---|
| `ToolUp.Cli` | `<PackAsTool>` — installed with `dotnet tool install` from a tool manifest, never a `<PackageReference>`, so central package management never consults a version for it. |
| `ToolUp.RAG.StaticCorpus.Build` | Same: a `dotnet tool`. |
| `ToolUp.Sdk` | The meta-package that *ships* this file. You must already pin it in your own `Directory.Packages.props` for the import to resolve, so an entry inside it could never be the one that resolves it. |

Both exclusions are computed from the project (a `PackAsTool` flag; the manifest's own owning
directory), not from a hand-kept list — so a new tool package is excluded automatically, and a
package that stops being a tool is re-admitted automatically.

## Rollback

Repin `<ToolUpSdkVersion>` to the previous release and restore the per-package overrides you
removed. Note that the previous manifest does not import at all, so a rollback means going back to
pinning every `ToolUp.*` package by hand.

## For SDK maintainers

The entry list is generated. Adding a packable project without a manifest entry now fails, in two
places:

- `SdkManifestTests` in the `ToolUp.Platform.Build.Tests` Expecto pack, which runs under `VerifyAll`
  and therefore under the `verify-all` CI job on every push and PR;
- a `GenerateSdkManifest --check` step in `publish-nuget.yml`, before the login step, so a release
  cannot publish a package the manifest does not advertise.

Repair either failure with:

```powershell
dotnet run --project Build.fsproj -- GenerateSdkManifest
```

That rewrites only the region below the `BEGIN GENERATED` marker; the hand-authored prose above it is
preserved byte-for-byte. The generator refuses to write a manifest that does not parse as XML, which
is the guard for defect 1 above — a rationale string or an edit to the preamble can reintroduce it,
and the file's own consumers are all outside this repo.

## See also

- `src/ToolUp.Sdk/build/ToolUp.Sdk.props` — the manifest, and the preserved rationale for the
  `ToolUp.Platform.UI` / `Feliz.AgGrid` / `Feliz.AgCharts` / `Feliz.AgGrid.Enterprise` entries.
- `SdkManifest.fs` (repo root) — the reconciler the target and the test share.
- `ToolUp/SDK-ADOPTION.md` — the Phase 344 AG Grid / AG Charts package renames.

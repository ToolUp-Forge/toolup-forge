# Version groups: the core moves in lockstep, each companion family on its own line

**Ships in:** 0.39.0 (`ToolUp.Sdk` meta-manifest, the release tooling). No package's public surface moves.

**Affected:** consumers who resolve `ToolUp.*` versions through the `ToolUp.Sdk` manifest, and contributors who
cut releases.

## What changes

Every packable project is on exactly one version group, declared in [`version-groups.json`](../../version-groups.json).
The **core** group (`ToolUp.Platform.*`, `ToolUp.AI.*`, the feature tiers, transport and wire, `ToolUp.Sdk`) is one
lockstep line at the release's version. Each **companion family** (`audit-sinks`, `storage`, `secrets`,
`data-sources`, `auth-providers`, `notification-channels`, …) has its own version, so a break in one family demands
a bump of that family alone. The model and the release rulings are in
[`docs/platform/release-process.md`](../platform/release-process.md#version-groups--one-lockstep-core-a-line-per-companion-family).

In the manifest, each package now resolves through its group's property: `ToolUpSdkVersion` for the core,
`ToolUp<Family>Version` (for example `ToolUpAuditSinksVersion`) for a family. Each family property defaults to the
version that family shipped at beside the core release the manifest belongs to.

## What a consumer does

**Nothing, if you pin only the core.** A `Directory.Packages.props` that sets `<ToolUpSdkVersion>` and imports the
manifest keeps restoring every package. The five lines that already had their own version (`ToolUp.Stripe.*`,
`ToolUp.Workflow.*`, `ToolUp.Experiments.*`, `ToolUp.Webhooks.*`, `ToolUp.FeatureFlagProviders.OpenFeature`) now
resolve at the version they are published at. Before this change the manifest asked for them at the core's version,
which was never published (`NU1102`), so a per-package `<PackageVersion>` override you added for one of them can go.

**One line, to hold or advance one family.** Set that family's property in the same `PropertyGroup` as
`<ToolUpSdkVersion>`, above the `Import`. The manifest only fills a family property that is still empty, so a value
set after the `Import` is ignored.

```diff
 <Project>
   <PropertyGroup>
     <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
     <ToolUpSdkVersion>0.39.0</ToolUpSdkVersion>
+    <ToolUpAuditSinksVersion>0.39.0</ToolUpAuditSinksVersion>
   </PropertyGroup>
   <Import Project="$(NuGetPackageRoot)toolup.sdk\$(ToolUpSdkVersion)\build\ToolUp.Sdk.props" />
 </Project>
```

The family ids and their properties are listed in `version-groups.json` and in the manifest's generated region.

## Verify

- `dotnet restore` on the consumer resolves every `ToolUp.*` reference with no `NU1008`, `NU1101` or `NU1102`.
- `dotnet list package` shows core packages at `ToolUpSdkVersion` and each family at its own property's version.

## Roll back

Remove the family property line. The family then resolves at the version released beside your core pin.

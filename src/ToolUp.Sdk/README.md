# ToolUp.Sdk

ToolUp SDK meta-manifest — the coordinated-bump escape hatch for consumers who want every `ToolUp.*` package pinned at the same version.

A consumer's `Directory.Packages.props` imports `build/ToolUp.Sdk.props` (NuGet auto-imports it when this package is referenced) and sets a single `<ToolUpSdkVersion>` property; every `ToolUp.*` package then resolves at that version. The package itself carries no runtime DLL — it's a `build/` props artefact.

See `build/ToolUp.Sdk.props` inside the nupkg for the full set of `<PackageVersion>` entries.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

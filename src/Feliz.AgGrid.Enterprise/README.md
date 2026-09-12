# ToolUp.Feliz.AgGrid.Enterprise

Client-side AG Grid Enterprise initialisation companion for `ToolUp.Platform`. Carries the module-level imports + license-key registration so AG Grid Enterprise features activate at module-evaluation time (required for animations + module-registry hooks).

Licensing-isolated from the core SDK so deployments running AG Grid Community don't accidentally ship Enterprise code. The Community-only path is the default; this companion is opt-in via `<Import Project="...\Feliz.AgGrid.Enterprise.Client.props" />` + a runtime `AgGridEnterprise.register "<license-key>"` call in the client composition root.

## Install

```xml
<PackageReference Include="ToolUp.Feliz.AgGrid.Enterprise" Version="0.23.0" />
```

The **package id** is `ToolUp.Feliz.AgGrid.Enterprise`; the assembly, the namespace and the
`Feliz.AgGrid.Enterprise.Client.props` import path are all unchanged. The whole binding family ships
under the `ToolUp.*` prefix because the bare `Feliz.AgGrid` id on nuget.org belongs to an unrelated
package by another author, and an `.Enterprise` id extending someone else's base id would mislead a
reader about who maintains what.

## Licensing — read this before deploying

**This package grants you no AG Grid Enterprise usage rights whatsoever.**

Two licences are in play and they cover different things:

- **This binding is Apache-2.0** (`PackageLicenseExpression`: `Apache-2.0`). That covers the F# shim
  in this package — the imports, the type augmentations, and the `register` call. Nothing more.
- **AG Grid Enterprise is a separate commercial product of AG Grid Ltd, licensed by them, to you.**
  Installing this package does not buy, include, sublicense, bundle or in any way convey that
  licence. The licence key you pass to `AgGridEnterprise.register` must be **your own organisation's**,
  obtained from AG Grid Ltd under their terms, and your use of AG Grid Enterprise is governed by
  those terms rather than by anything here.

Practically: a deployment that installs this package without holding its own AG Grid Enterprise
licence is unlicensed for the Enterprise features it activates. The Community path — `ToolUp.Feliz.AgGrid`
and `ToolUp.Feliz.AgCharts`, which pull no Enterprise distribution — is the default for exactly that
reason.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

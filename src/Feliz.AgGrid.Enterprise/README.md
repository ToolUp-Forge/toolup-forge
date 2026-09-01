# Feliz.AgGrid.Enterprise

Client-side AG Grid Enterprise initialisation companion for `ToolUp.Platform`. Carries the module-level imports + license-key registration so AG Grid Enterprise features activate at module-evaluation time (required for animations + module-registry hooks).

Licensing-isolated from the core SDK so deployments running AG Grid Community don't accidentally ship Enterprise code. The Community-only path is the default; this companion is opt-in via `<Import Project="...\Feliz.AgGrid.Enterprise.Client.props" />` + a runtime `AgGridEnterprise.register "<license-key>"` call in the client composition root.

Subject to the AG Grid Enterprise commercial licence — the deploying organisation supplies its own license key.

This shim itself is licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

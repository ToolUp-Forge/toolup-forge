# ToolUp.Feliz.AgGrid

Feliz bindings for [AG Grid](https://www.ag-grid.com/) **Community** — typed column definitions, grid
props, the grid API surface, the Theming API, CSV export parameters, locale text, and the
`AgGridProvider` module-registration shape AG Grid v35+ requires.

## Install

```xml
<PackageReference Include="ToolUp.Feliz.AgGrid" Version="0.23.0" />
```

The **package id** is `ToolUp.Feliz.AgGrid`; the **namespace you open** is `Feliz.AgGrid`. They differ
deliberately: `Feliz.AgGrid` on nuget.org is an older, unrelated binding by another author, so this
one ships under the `ToolUp.*` prefix. Nothing about the F# surface changes — `open Feliz.AgGrid` is
what your code writes, and always was.

```fsharp
open Feliz
open Feliz.AgGrid

AgGrid.grid [
    AgGrid.rowData rows
    AgGrid.columnDefs [
        ColumnDef.create [ ColumnDef.field _.Name; ColumnDef.headerName "Name" ]
    ]
]
```

Fable-first: the source packs under `fable/`, so a Fable consumer compiles the binding directly
rather than consuming a pre-built assembly.

## npm dependencies

The binding imports from `ag-grid-community` and `ag-grid-react`. Declare both in the consuming app's
`package.json`; nothing here pulls the Enterprise distribution.

## Licensing

This binding is Apache-2.0 (`PackageLicenseExpression`: `Apache-2.0`). AG Grid Community is MIT.
**This package grants no AG Grid Enterprise usage rights** — for the Enterprise feature set add
`ToolUp.Feliz.AgGrid.Enterprise` and supply your own AG Grid Enterprise licence key.

Maintained in [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge).

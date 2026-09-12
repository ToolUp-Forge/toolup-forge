# ToolUp.Feliz.AgCharts

Feliz bindings for [AG Charts](https://www.ag-grid.com/charts/) **Community** — typed series (bar,
line, area, scatter, pie, bubble, error bars), axes and crosslines, legends, tooltips, markers, and a
chart theme builder, plus the deferred module-registration guard the Enterprise companion pre-empts.

## Install

```xml
<PackageReference Include="ToolUp.Feliz.AgCharts" Version="0.23.0" />
```

The **package id** is `ToolUp.Feliz.AgCharts`; the **namespace you open** is `Feliz.AgCharts`. The
whole binding family ships under the `ToolUp.*` prefix (its sibling's bare id belongs to another
author on nuget.org, and a family split across two prefixes would mislead about who maintains what).
The F# surface is unchanged.

```fsharp
open Feliz
open Feliz.AgCharts

AgChart.chart [
    AgChart.options [
        AgChart.data points
        AgChart.series [
            Series.create [ Series.seriesKind Bar; Series.xKey "month"; Series.yKey "revenue" ]
        ]
    ]
]
```

Fable-first: the source packs under `fable/`, so a Fable consumer compiles the binding directly
rather than consuming a pre-built assembly.

## npm dependencies

The binding imports from `ag-charts-community` and `ag-charts-react`. Declare both in the consuming
app's `package.json`; nothing here pulls the Enterprise distribution.

## Licensing

This binding is Apache-2.0 (`PackageLicenseExpression`: `Apache-2.0`). AG Charts Community is MIT.
**This package grants no AG Charts Enterprise usage rights** — for the Enterprise feature set add
`ToolUp.Feliz.AgGrid.Enterprise` and supply your own AG Grid / AG Charts Enterprise licence key.

Maintained in [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge).

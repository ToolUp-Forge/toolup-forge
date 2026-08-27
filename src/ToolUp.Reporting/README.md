# ToolUp.Reporting

Companion package providing typed-template document generation for applications built on [`ToolUp.Platform`](../ToolUp.Platform/). Ships the `IReportRenderer` interface, default `MarkdownRenderer` + `HtmlRenderer` (zero-deps), `IReportTemplateStore` over `IEntityStore`, the `ReportApi` ToolUp.Remoting endpoint, and `ReportingCompose` (registers the renderer registry + API handler against a `ServerApp`).

Each output format that needs a real renderer (PDF / DOCX / XLSX) lives in its own sub-companion under [`src/Reporting/`](../Reporting/) so a deployment that only needs PDF doesn't pay for the OpenXml dep, and vice versa. **`Pptx` is the exception** — deck output is served by a downstream document-emission tier rather than by a renderer here; see [Deck export](#deck-export) below.

Apache-2.0; part of the ToolUp Platform SDK.

## Why a separate companion

Two reasons it doesn't live in `ToolUp.Platform`:

1. **Vendor SDK weight.** PDF / DOCX / XLSX renderers pull in QuestPDF or DocumentFormat.OpenXml; deployments that don't need document generation shouldn't pay for those deps.
2. **Audit + storage integration.** The renderer composes with `IEntityStore` (template persistence), `IDataObjectStore` (rendered-blob storage with versioning), and `IAuditLog` (`ReportRendered` events). Keeping that wiring inside a companion (rather than threading it into the core SDK) preserves the "core ships only opinion-light substrate" principle.

## Activation

```fsharp skip=fragment
open ToolUp.Reporting

ServerApp.empty
|> ServerApp.addModules [ (* your modules *) ]
|> ReportingServerApp.run
```

`ReportingServerApp.run` is a flat superset of `ServerApp.run` — every existing capability passes through, plus the reporting endpoints and renderer registry.

## Built-in renderers

- **`MarkdownRenderer`** — pure F#, zero deps. Templates use `{{key}}` placeholders; `{{#each items}} … {{/each}}` for tables.
- **`HtmlRenderer`** — pure F# + `System.Web.HttpUtility.HtmlEncode`. Same placeholder syntax; values HTML-encoded by default.

## Sub-companions (separate NuGets)

- **`ToolUp.Reporting.Pdf`** — QuestPDF-backed (community-licensed under $1M revenue/yr).
- **`ToolUp.Reporting.Docx`** — DocumentFormat.OpenXml.
- **`ToolUp.Reporting.Xlsx`** — DocumentFormat.OpenXml. Includes the **cell-address-map binding mode** (`Map<CellAddress, ExcelCellValue>` shape that writes values directly into specific cells without template-side `{{key}}` markup) — load-bearing for the Excel-to-ToolUp converter portal's runtime export path.

Each sub-companion ships in its own NuGet so deployments compose only what they need. There is no `ToolUp.Reporting.Pptx`, and there will not be one — see below.

## Deck export

`TemplateFormat` enumerates `Pptx`, and nothing here renders it. A render request for that format refuses with the typed `RenderError.FormatServedByDeckTier`, which is deliberately distinct from `NoRendererForFormat`: there is no package to add. Deck output is produced by a **downstream document-emission tier** that reads the deployment's exported result set (narrative + fact refs + provenance) and emits typed deck parts — a useful deck being a regenerable projection of governed results rather than a rendered template.

This companion's contribution to that tier is the **chart-artifact handoff** (`ChartArtifact`): the deployment's own deterministic chart grammar, exposed as content-hashed, provenance-stamped embeddable artifacts. Full rationale and the composition shape: [`docs/platform/reporting.md`](../../docs/platform/reporting.md).

Licensed under Apache-2.0. Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

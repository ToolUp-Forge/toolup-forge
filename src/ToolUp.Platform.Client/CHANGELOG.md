# Changelog — ToolUp.Platform.Client

All notable changes to the `ToolUp.Platform.Client` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.1.3]

### Fixed

- `AgGrid.ColumnDef.field` bound the wrong column for `int` / `int64`
  fields: Fable coerces a numeric field read to `(r) => (r.Name | 0)`,
  and the accessor parser took everything after the first `.`, yielding
  `"Name | 0)"` so AG Grid bound to a non-existent column (blank
  numeric cells; string columns were unaffected). It now takes only the
  leading identifier run after the first `.`, identical to the previous
  result for string fields.

## [0.1.2]

### Added

- `Components.SvgTree` — generic Feliz SVG tree-renderer component.
- Phase 6h.B: GFM pipe-table rendering in `Toolup.Markdown`.
- Phase 6h.B: resizable AI side panel.
- Data Ingestion UI: implemented the missing "Add data source"
  affordance.

## [0.1.0] - 2026-05-11

- Initial public release.

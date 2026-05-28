# Changelog — ToolUp.AI.SampleClientTool.Core

All notable changes to the `ToolUp.AI.SampleClientTool.Core` package are
recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions track
the coordinated `ToolUp.Sdk` meta-release; per the SemVer-on-0.x policy
(see the repository `CLAUDE.md` "Versioning" section), during `0.x` a
minor bump may carry breaking changes while a patch bump stays
non-breaking.

## [0.2.3]

- Initial public release. Phase 46.B reference-only companion: shared
  types for the sample client-resident-tool calculator
  (`CalcRequest` / `CalcResponse` / `CalcOps.compute`). Pairs with
  `.Server` (tool registration) and `.Client` (Fable browser handler).
  Exists so the `IClientToolAuthorizer` + `ClientToolDispatch`
  substrate has an in-tree consumer exercising the seam end-to-end.
  Not a production companion.

# Changelog — ToolUp.AI.SampleClientTool.Client

All notable changes to the `ToolUp.AI.SampleClientTool.Client` package
are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions track
the coordinated `ToolUp.Sdk` meta-release; per the SemVer-on-0.x policy
(see the repository `CLAUDE.md` "Versioning" section), during `0.x` a
minor bump may carry breaking changes while a patch bump stays
non-breaking.

## [0.2.3]

- Initial public release. Phase 46.B reference-only companion: Fable
  browser-side handler for the sample client-resident-tool calculator.
  Doubles as the ≤10-min worked example for new client-resident-tool
  companion authors — see the package README for the walkthrough.
  Exists so the `IClientToolAuthorizer` + `ClientToolDispatch`
  substrate has an in-tree consumer exercising the seam end-to-end.
  Not a production companion.

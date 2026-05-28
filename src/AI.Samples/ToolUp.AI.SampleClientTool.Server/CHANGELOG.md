# Changelog — ToolUp.AI.SampleClientTool.Server

All notable changes to the `ToolUp.AI.SampleClientTool.Server` package
are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions track
the coordinated `ToolUp.Sdk` meta-release; per the SemVer-on-0.x policy
(see the repository `CLAUDE.md` "Versioning" section), during `0.x` a
minor bump may carry breaking changes while a patch bump stays
non-breaking.

## [0.2.3]

- Initial public release. Phase 46.B reference-only companion:
  server-side compose registering the sample calculator as a
  `ClientResident` AI tool. Bound to the Phase 46.A
  `IClientToolDispatchContract` portability pack via
  `SampleClientToolDispatchTests`. Exists so the
  `IClientToolAuthorizer` + `ClientToolDispatch` substrate has an
  in-tree consumer exercising the seam end-to-end. Not a production
  companion.

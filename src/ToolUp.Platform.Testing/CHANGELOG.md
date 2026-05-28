# Changelog — ToolUp.Platform.Testing

All notable changes to the `ToolUp.Platform.Testing` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.2.3]

- Initial public release. Phase 11a module-testing scaffold:
  in-memory fakes for every contract-tested SDK interface,
  `ModuleHarness` for testing a module's MVU end-to-end without a
  browser, `ServerHarness` for composing a `ServerApp` with fake
  dependencies, `DataTypeTestKit` for testing data-type registration
  and rendering, `ModuleHotReload` for iteration loops. All fakes pass
  the contract test packs in `ToolUp.Platform.Tests`.

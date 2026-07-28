# Changelog — ToolUp.Algorithms.Server

All notable changes to the `ToolUp.Algorithms.Server` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.11.0]

Initial release (Phase 11.E.2) — the analytical-primitive catalog
companion. The shipped interface set was selected by a pre-build
measurement pass (`evals/algorithms-primitives-eval/`) rather than by
intuition; `ICurveFitter` was measured as a control and deliberately
excluded.

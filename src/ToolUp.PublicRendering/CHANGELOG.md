# Changelog — ToolUp.PublicRendering

All notable changes to the `ToolUp.PublicRendering` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.2.3]

- Initial public release. Phase 38 public-rendering surface for
  website-class deployments: `IPublicContentApi` interface,
  Giraffe.ViewEngine SSR pattern, content-as-code loader supporting
  Markdown with YAML front-matter (via Markdig), redirect map driven
  from a CSV. Companion to the `samples/PublicSite/` reference
  deployment that exercises every code path end-to-end with a 10-page
  marketing fixture + 3 news articles + 20-entry redirects.csv.

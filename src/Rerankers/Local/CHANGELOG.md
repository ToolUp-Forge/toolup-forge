# Changelog — ToolUp.Rerankers.Local

All notable changes to the `ToolUp.Rerankers.Local` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.1.2]

- Initial release. Phase 14f follow-up: CPU-bound ONNX cross-encoder
  `IReranker` companion. Model + tokenizer vocab are
  operator-provisioned (no weights bundled); recommended
  `mixedbread-ai/mxbai-rerank-xsmall-v1`, fallback
  `BAAI/bge-reranker-base`. Introduced after the 0.1.0 public release;
  first ships at the coordinated 0.1.2 SDK line.

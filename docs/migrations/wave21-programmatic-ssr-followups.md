# Migration — Wave 21 programmatic-SSR consumer follow-ups

**Status:** four net-new opt-in surfaces. Every default path is byte-for-byte unchanged except where noted (the JSON-LD escaping change below shifts *emitted bytes* — semantically equivalent, no API break). No consumer action required to upgrade.

## Why

The Wave 21 SSR/SEO primitives (Phases 147–157) are designed handler-agnostic so a **programmatic-SSR consumer** — one that renders from domain data through its own Giraffe pipeline rather than composing `PublicPageHandler` / `IPublicContentApi` — can adopt them. A real adoption pass against such a consumer found the conditional-GET core (155/147/149) byte-perfect, but surfaced four gaps where a capability was shipped but not fully reachable / reproducible by that consumer class. These follow-ups close them.

## 1. `StructuredDataHelpers` — minimal, breakout-safe JSON-LD escaping

**Gap:** `serialise` used STJ's default `JavaScriptEncoder`, which escapes every non-ASCII rune and the HTML-significant set (`<`, `>`, `&`, `+`, `'`) to `\uXXXX`. Valid JSON, but it diverges byte-for-byte from a hand-rolled emitter's minimal escaping, so a consumer can't swap its existing FAQPage / BreadcrumbList JSON-LD for the forge emitters without a byte regression on real prose (`vii°`, `IV→V→I`, `&`).

**Change:** `serialise` now uses `UnsafeRelaxedJsonEscaping` (passes UTF-8 + `<`/`>`/`&` through) and re-applies the one escape that matters for `<script>` embedding — `</` → `<\/` — so a literal `</script>` inside a string value cannot terminate the surrounding block. This is **safer** than the prior default for embedding (a minimal, explicit breakout guard) and matches minimal hand-rolled output.

**Byte-shift note:** the emitted JSON-LD bytes change for any payload containing non-ASCII / `<` / `>` / `&` (e.g. `°` → `°`). Output stays valid, rich-result-valid JSON-LD. **If you assert exact JSON-LD bytes in a golden test, update the expectation.** No API signature changed.

**Also added:** `learningResource name description teaches` (SERP `LearningResource` rich result, beginner-reference defaults) + `learningResourceWith …` (explicit `learningResourceType` / `educationalLevel` / `inLanguage`). There was no LearningResource emitter before.

## 2. `SearchIndexEmitter` — keyword serialisation mode

**Gap:** `toJson` always emitted `"keywords":[…]` (a JSON array). A consumer whose client tokenises a single joined keyword string couldn't adopt the projection.

**Change:** `SearchIndexConfig` gains `KeywordFormat : SearchIndexKeywordFormat` (`KeywordsArray` — **the default, byte-for-byte the original output**; or `KeywordsJoined of separator`). New `SearchIndexEmitter.toJsonWith format entries`; `toJson` is now `toJsonWith KeywordsArray`. `handler` honours `config.KeywordFormat`. Opt in with `SearchIndexConfig.withKeywordFormat (KeywordsJoined " ")`.

## 3. `ConditionalGet.cacheableWithMetrics` — 153 metrics from the combinator

**Gap:** the Phase 153 crawler counters (`publicrendering.conditional_get`, `publicrendering.request_by_agent`) were emitted only inside `PublicPageHandler`, never from the handler-agnostic `ConditionalGet.cacheable` combinator — so a 155 consumer got no 304-rate / crawler observability.

**Change:** new `ConditionalGet.cacheableWithMetrics (sink: IMetricsSink) etag lastModified cacheControl`. Wire behaviour is byte-for-byte identical to `cacheable`; it additionally emits `conditional_get{outcome=304|200}` and `request_by_agent{agent=…}` (bounded crawler-class buckets via `RenderMetrics.classifyAgent`; raw UA never a tag). A `NoOpMetricsSink` makes every emit an empty method body (GP 13). The bare `cacheable` is unchanged.

## 4. `Csp.applyNonceCsp` — standalone source-mode CSP

**Gap:** the valuable Phase 156 nonce-mode behaviour (placeholder substitution + the load-bearing 304-skip) lived inside `CspMiddleware`, which needs the `ResolvedCspPolicy` DI singleton from `ServerApp.withCspSourceMode`. A consumer running its own pipeline could reach only the trivial nonce *generator*.

**Change:** new `Csp.applyNonceCsp (ctx) (template) (placeholder) : string` — mints a per-request nonce, stashes it for `Csp.requestNonce`, substitutes `placeholder` in `template`, and registers an `OnStarting` that stamps the `Content-Security-Policy` header with the 304-skip + per-route-override-wins rules. `CspMiddleware` is now a thin wrapper over it, so the composed SDK path is byte-for-byte unchanged. A self-pipeline consumer calls `Csp.applyNonceCsp ctx myTemplate "{MY_PLACEHOLDER}"` in its own middleware and reads `Csp.requestNonce ctx` in its layout.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 0 failures. New coverage: relaxed-escaping + `</` breakout guard + `learningResource`; `KeywordFormat` array-vs-joined (+ handler); `cacheableWithMetrics` 304/200 + agent classification + wire-identity-to-`cacheable`; standalone `applyNonceCsp` substitution + 304-skip + override-wins.

## Rollback

Each change is additive except the §1 escaping default. To revert §1, restore the default `JsonSerializerOptions(WriteIndented = false)` in `StructuredDataHelpers.serialise` and drop the `.Replace("</", "<\\/")`. §2/§3/§4 are new surfaces — remove the additions; existing call sites are unaffected.

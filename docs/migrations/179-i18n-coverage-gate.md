# Phase 179 — i18n translation-coverage gate + pseudo-localisation

**Ships in:** ToolUp.Platform.Core (`ToolUp.Platform.I18nCoverage`,
`ToolUp.Platform.PseudoLocale`, two new `ServerConfig` fields) + ToolUp.Platform.Client
(the `I18n.tr` pseudo-locale branch).

## What changes

Two additive, opt-in surfaces make Phase 12a's "non-English unblocked" claim
*provable*. Both are pure functions over the existing `Translations` / `LocaleCode`
types — no new runtime subsystem, zero cost when unused (GP 13).

### 1. Translation-coverage gate (`I18nCoverage`)

- `I18nCoverage.audit (translations) (requiredLocales) : CoverageReport` — reports,
  per `TranslationKey`, the registered locales with no entry. It honours
  `Translations.tryLookup`'s language-only fallback (an `"en"` entry covers an
  `"en-GB"` requirement) and deliberately ignores the explicit fallback-locale arm
  (a fallback masking a real gap is what the gate catches). `auditKeys` audits an
  explicit key list; `sdkRequiredKeys` is the SDK's own surface — every `sdk.*` key
  plus the stock `ApiError` message key per `ErrorCode`.
- `ServerConfig.RegisteredLocales: LocaleCode list` (default `[ LocaleCode.en ]`) +
  `ServerConfig.I18nCoverageMode` (`NoCoverageCheck | WarnOnMissing | FailOnMissing`,
  default `NoCoverageCheck`). `I18nCoverage.validator translations requiredLocales
  mode : IConfigValidator option` builds the preflight validator: `None` for
  `NoCoverageCheck`; a validator returning `Warning` (log + continue) or `Error`
  (abort startup via the aggregator, naming the missing key + locale) otherwise.

### 2. Pseudo-localisation (`PseudoLocale`)

- `PseudoLocale.code = LocaleCode "qps-ploc"` (the conventional pseudo-locale tag),
  `PseudoLocale.isActive`, and `PseudoLocale.transform : string -> string` — accents
  vowels (`á é í ó ú`, Latin-1-safe), pads length ~30%, wraps in `⟦…⟧` markers, and
  passes `{placeholder}` tokens through untouched so `ApiError.applyPlaceholders`
  still substitutes. A vowel-less string (a symbol / number / code) round-trips
  unchanged.
- The client `I18n.tr` helper applies `transform` to the resolved-or-key string when
  the active locale is the pseudo-locale. An un-externalised hardcoded literal never
  reaches `tr`, so it renders un-accented and stands out.

### Internal reshape (no consumer impact)

`LocaleCode` / `TranslationKey` / `Translations` moved from `Types/I18nTypes.fs` to a
new `Types/LocaleTypes.fs` compiled before `SDK.Shared.fs` (so `ServerConfig` can carry
the two new fields). Same `ToolUp.Platform` namespace, same public API — a pure compile-
order reshape. `ErrorCode` / `ApiError` stay in `I18nTypes.fs`.

## Diff to apply

**Nothing, for every consumer.** Both fields default off (`NoCoverageCheck`, `[ en ]`),
the new functions are additive, and the client `tr` path is byte-for-byte unchanged for
any real locale. A deployment that declares no locales and never resolves `qps-ploc` is
identical to the pre-179 build.

To **opt in** to the coverage gate, declare your locales and pick a mode in the
composition root:

```fsharp
let config =
    { ServerConfig.fromEnv logger overrides with
        RegisteredLocales = [ LocaleCode.en; LocaleCode.fr ]
        I18nCoverageMode = FailOnMissing }
```

Then register the validator alongside the deployment's merged translations (SDK seed +
every module's contribution):

```fsharp
match I18nCoverage.validator mergedTranslations config.RegisteredLocales config.I18nCoverageMode with
| Some v -> app |> ServerApp.withConfigValidator v
| None -> app
```

To **use the pseudo-locale**, have your `ILocaleResolver` return `PseudoLocale.code`
(e.g. behind a `?pseudo=1` query param or a dev-only env flag) — the client `tr` picks
it up automatically.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `I18nCoverageTests` — `audit` gap detection, `en`→`en-GB` language fallback, SDK seed
  coverage for `en`+`fr`, `FailOnMissing`→`Error` / `WarnOnMissing`→`Warning`, and the
  `transform` accent / placeholder-preservation / vowel-less round-trip cases.
- Fable-safe: `PseudoLocale` / `I18nCoverage` use BCL string / `Map` / `List` / `async`
  only (no `#if DEBUG`, no framework handles), so they compile in the client tier.

## Rollback

Revert the SDK version pin. The two `ServerConfig` fields and both modules are additive;
no persisted wire format changes, so rollback is safe. Consumers that opted in remove the
`RegisteredLocales` / `I18nCoverageMode` overrides and the validator registration.

# Migration 275 — hosted-tree i18n resolution seam

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

Phase 264 projects host **data** into the binding namespace, but not localized **strings** +
placeholder interpolation; and Phase 179 is a *source-string* coverage gate that cannot see a tree
emitted at runtime. So a hosted view fell back to literal strings regardless of locale. This phase
ships a neutral resolver a hosted tree resolves i18n key + placeholder bindings against, extending
Phase 179's localization guarantee to the dynamic hosted surface.

New surface in `src/ToolUp.Platform.Client/Client/HostI18nResolver.fs` (namespace `ToolUp.Platform`):

- `IHostI18nResolver` — `Resolve : key:string -> args:Map<string,string> -> locale:LocaleCode ->
  string`. The host decides the active locale (server: request/principal via `ILocaleResolver`;
  client: browser via `I18n.useLocale`) and passes it in; the resolver is a pure function of (key,
  args, locale) over the Phase 179 substrate.
- `HostI18nResolver.create translations fallback onMiss` — the resolver over a `Translations` table +
  fallback locale + a **miss sink** (`key -> locale -> unit`, the Phase 179 coverage signal).
- `HostI18nResolver.ofTranslations translations fallback` — no coverage sink (the miss is still
  observable via the returned key).
- `HostI18nResolver.MissRecordingHostI18nResolver` — a decorator that records every unresolved
  `(key, locale)` (readable via `.Misses`).

Behaviour: a **hit** returns the translated template (pseudo-localised when the locale is `qps-ploc`)
with `{name}` placeholders substituted; a **miss** returns the **key** (placeholders applied) —
observable, never a silent blank (GP 2) — and fires `onMiss`. Pseudolocalisation passthrough means a
hosted tree participates in the existing Phase 179 pseudo-loc audit.

## How to adopt (opt-in)

```fsharp
let resolver =
    HostI18nResolver.create translations LocaleCode.en (fun key locale ->
        // feed the Phase 179 coverage signal
        coverage.RecordMiss key locale)

// In the hosted-render path, beside the Phase 264 projection:
let label = resolver.Resolve "host.greeting" (Map.ofList [ "name", user.Name ]) activeLocale
```

A deployment that never constructs a resolver is byte-for-byte unchanged (GP 11/13).

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostI18nResolver"
cd samples/MinimalClient && dotnet fable -o output   # the client-tier resolver compiles under Fable
```

## Rollback

Delete `HostI18nResolver.fs` + its `<Compile>` entry, `InProcess/HostI18nResolverTests.fs` + its
`<Compile>` and `Program.fs` registration. No runtime impact on any deployment that never resolved
hosted i18n.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in i18n seam for the dynamic hosted
surface. No current matrix consumer hosts a typed-tree UI; a deployment that composes no resolver is
byte-for-byte unchanged (GP 11/13).

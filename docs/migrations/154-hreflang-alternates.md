# Migration — Phase 154: `hreflang` / `rel="alternate"` multi-locale head helpers

**Status:** pure-additive, opt-in, multi-locale-only. A single-locale site (no `hreflang` frontmatter) is byte-for-byte pre-154 (GP 11). No consumer action required.

## What changes

A multi-locale site can now emit `<link rel="alternate" hreflang="…">` clusters so search engines serve the right-language URL and don't treat translations as duplicates. Before Phase 154, `NarrativeLayout.headTags` emitted `og:locale` from `Narrative.Lang` but no `hreflang` alternates.

Phase 154 adds (all in `NarrativeLayout`, resolver-neutral — no i18n routing scheme is imposed, GP 13):

- **`alternates : (lang*url) list -> XmlNode list`** — emits one `<link rel="alternate" hreflang="{lang}" href="{url}">` per entry. An `x-default` cluster member is simply an entry whose lang is `"x-default"`. Empty list → no tags. Attribute values are escaped by Giraffe.ViewEngine.
- **`parseAlternates : string -> (lang*url) list`** — parses the `hreflang` frontmatter convention (comma- / semicolon- / newline-separated `lang=url` pairs); malformed entries dropped, order preserved.
- **`alternatesFromFrontmatter : page -> (lang*url) list`** — reads the page's `hreflang` frontmatter key (`[]` when absent).
- **`headTags`** now emits the hreflang cluster from the frontmatter convention for **any** body kind when the `hreflang` key is present. Absent key → no tags (byte-for-byte pre-154).

## Per-locale URL resolution (resolver-neutral)

The alternate set is supplied by the **`hreflang` frontmatter convention** (a locale→URL map authored in frontmatter, or set programmatically on a synthesised page's `Frontmatter`):

```yaml
hreflang: en=https://example.com/a, fr=https://example.com/fr/a, x-default=https://example.com/a
```

A layout that drives its head off `NarrativeLayout.headTags` then emits the cluster automatically. A layout computing alternates by its own logic can call `NarrativeLayout.alternates entries` directly.

**Reciprocity requirement (guidance, not enforced):** every alternate in a cluster must point back — each locale variant should list the same set of alternates (including itself and `x-default`). The SDK does not validate this.

### MultiSite interaction (Phase 145)

- **Host-per-locale** (`SiteRegistry` satellite per language host): each satellite serves its own pages; the `hreflang` frontmatter on each page lists the absolute URLs across the language hosts.
- **Path-/param-per-locale** (`/fr/...`, `?lang=fr`): a single site; the `hreflang` frontmatter lists the per-path absolute URLs.

There is no behavioural coupling to the registry — `alternates` is purely the URLs the page declares.

### Deferred: compose-registered locale-resolver delegate

A compose-registered locale-resolver delegate (a `with*` that wraps the content API to compute the `hreflang` set per page from a registered i18n routing function, mirroring Phase 148's `withSelfCanonical`) is a planned follow-on. It is **not** required for resolver-neutrality — the frontmatter convention above is the resolver-neutral source and fully covers the acceptance criteria. The delegate is deferred to keep this phase's footprint inside `NarrativeLayout` (the compose root was under heavy concurrent change at ship time); add it when `PublicRenderingCompose` is quiescent, following the `withSelfCanonical` shape.

## Diff to apply

None required. To opt a multi-locale page in, add the `hreflang` frontmatter key (above), or call `NarrativeLayout.alternates` from a custom layout.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `PublicRenderingTests.fs` (`PublicRendering — Phase 154 hreflang alternates`): `alternates` emits one `rel=alternate` per entry incl. `x-default`; empty set → no tags; `parseAlternates` keeps well-formed pairs in order and drops malformed; `headTags` emits the cluster from the frontmatter convention for Markdown / Html / Narrative bodies; absent key → byte-for-byte pre-154; lang/href attribute-escaping.

## Rollback

Revert the Phase 154 commit. The `hreflang` frontmatter key simply stops being read; the new helpers are unreferenced by existing layouts. No data migration.

# Migration — Phase 151: structured-data expansion (`WebSite`/`SearchAction` + rich-result emitters)

**Status:** pure-additive emitters + one back-compatible extension to `organization`. No consumer action required; existing `StructuredDataHelpers` output is unchanged unless a new key/emitter is used (GP 11).

## What changes

`StructuredDataHelpers` gains the schema.org types content / learning / catalog sites actually win rich results with, plus the two highest-value site-level emitters absent before:

- **`webSite name url searchUrlTemplate`** — `WebSite` JSON-LD with an optional `SearchAction` `potentialAction` (the Google sitelinks search box). `searchUrlTemplate` is the search URL carrying the literal `{search_term_string}` token; `None` omits `potentialAction`.
- **`faqPage (question*answer) list`** — `FAQPage` with `Question` / `acceptedAnswer` pairs.
- **`howTo name steps`** — `HowTo` with ordered `HowToStep`s.
- **`course name provider description`** — `Course` with a provider `Organization`.
- **`itemList (name*url) list`** — `ItemList` of positioned `ListItem`s with URLs (listing / index pages).
- **`product name description offers aggregateRating`** — `Product` with an optional `Offer` (`(price, priceCurrency)`) and optional `AggregateRating` (`(ratingValue, reviewCount)`); each optional block omitted when `None`.
- **`videoObject name description thumbnailUrl uploadDate contentUrl`** — `VideoObject`.
- **`organization`** now reads a `sameAs` frontmatter key (comma- / newline-separated profile URLs) → JSON-LD `sameAs` array. **Omitted when the key is absent**, so existing `organization` output is byte-for-byte unchanged (GP 11).

Each new emitter mirrors the existing pure `-> string` shape — callable from a layout exactly like `article` / `breadcrumb` / `siteNavigation` — and degrades missing values to empty strings (or omitted optional blocks) rather than throwing.

## Diff to apply

None required. To use a new emitter, call it from a layout and embed the result:

```fsharp
head [] [
    // site-level, on the home page:
    script [ _type "application/ld+json" ] [
        rawText (StructuredDataHelpers.webSite "Acme" "https://acme.example" (Some "https://acme.example/search?q={search_term_string}"))
    ]
    // an FAQ page:
    script [ _type "application/ld+json" ] [ rawText (StructuredDataHelpers.faqPage faqs) ]
]
```

To add social profiles to an existing `organization` node, add `sameAs:` to the page frontmatter:

```yaml
sameAs: https://twitter.com/acme, https://www.linkedin.com/company/acme
```

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `PublicRenderingTests.fs` (`PublicRendering — Phase 151 structured-data expansion`): each emitter parses as valid JSON; `webSite`+`SearchAction` carries the `{search_term_string}` token + `query-input` and omits `potentialAction` with no template; `product` includes / omits `Offer` + `AggregateRating`; `organization` gains `sameAs` when the key is present and omits it when absent (GP 11 byte-for-byte).

## Rollback

Revert the Phase 151 commit. The new emitters are unreferenced by existing layouts; the `organization` `sameAs` read reverts to the pre-151 fixed shape (a page carrying a `sameAs` key simply stops emitting the array). No data migration.

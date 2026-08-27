# Migration — Phase 212 SEO / structured-data conformance lint (CI)

**Status:** test-only. No shipping source, no package metadata, and no public API changed; every `ToolUp.PublicRendering` output is byte-for-byte what it was (GP 13). **No consumer action is required to upgrade** — the value here is a gate that now catches a class of regression the suite could not see before, plus a reusable rule set a consumer can point at its own rendered pages.

## Why

The SSR SEO surface — the JSON-LD emitters (Phases 151/96), the `<urlset>` / `<sitemapindex>` generator (Phases 149/150), and the canonical / `hreflang` / robots head injections (Phases 148/152/154/111) — shipped before its conformance harness existed. The existing packs assert *behaviour* ("a `robots` key emits a tag", "an excluded slug is dropped"); nothing asserted *conformance* — that the emitted markup is what schema.org, sitemaps.org and Google's rich-result rules actually require. A malformed emitter therefore produced a green build and a broken SERP entry, which is the most expensive place to discover it.

## What was added

One test pack, `src/ToolUp.Platform.Tests/InProcess/StructuredDataConformanceTests.fs`, wired into the `ToolUp.Platform.Tests` runner (and therefore into `VerifyAll`, and therefore into the `verify-all` CI job). 92 cases across six groups:

| Group | What it holds the surface to |
|---|---|
| JSON-LD emitters | Well-formed JSON; `@context` is exactly `https://schema.org`; the declared `@type` is the expected one; every nested entity carries its own `@type` and no nested node re-declares `@context`; no JSON `null`; `itemListElement` / `step` positions form a contiguous `1..n` run; no raw `</` can terminate the embedding `<script>` block. Bound against all sixteen emitters twice — once on a fully-populated page (required properties must carry a **value**) and once on a page with no frontmatter at all (the documented degrade-to-empty must not become a throw). Plus the Open Graph / Twitter meta emitters. |
| Sitemap | The sitemaps.org 0.9 root in its namespace, `<url>` children only, exactly one absolute `http(s)` `<loc>` each, no duplicate `<loc>`, W3C-datetime `<lastmod>`, and the 50,000-URL file cap. Every exclusion class is checked from the emitted XML: `sitemap: exclude`, `Draft`, `Archived`, future-`Scheduled`, and both gated audiences. The `<sitemapindex>` shard path is held to the same contract. |
| Canonical + `hreflang` | Over the **host-aware site registry**: a page served for a satellite site canonicalises to *that* site's origin, exactly one `<link rel="canonical">` reaches the document, an explicit canonical is never doubled, and the self-referencing `hreflang` agrees with the canonical. The locale cluster spanning two hosts must be **reciprocal** — every alternate that names another cluster member is checked for a pointer back, and for agreement about that member's language tag. |
| Robots | The directive vocabulary (known tokens and the four `max-*` / `unavailable_after:` prefixed forms), no repeats, no self-contradicting pair (`index, noindex`), verbatim-and-trimmed emission, and no tag at all for an absent or blank key. |
| Rule self-tests | Every validator is handed a deliberately malformed input and must report. |
| Pinned gap | One shipped behaviour that genuinely fails a correct rule — see below. |

Validation is **pure-string and offline** (GP 12): `System.Text.Json` for the payloads, `System.Xml.Linq` for the sitemap, a small attribute reader over the rendered document. Nothing contacts schema.org or Google — a conformance gate that needs the network is a gate that goes red for reasons unrelated to the code.

`SitemapGenerator.generateUrlSetFrom` is assembly-internal and is exercised through its public delegators (`generateWith` / `generate` / `generateSitemapIndex`), which are one-line wrappers over it. That keeps the shipped package free of an `InternalsVisibleTo` grant it does not otherwise need, and the bytes under test are the same bytes.

## Finding — `robots: noindex` does not exclude a page from `sitemap.xml`

`SitemapGenerator.entriesAt` drops a page for three reasons: a `sitemap: exclude` frontmatter key, a non-`Public` audience, and a not-publicly-visible status. It does **not** consult the Phase 152 `robots` key. A page declaring `robots: noindex` is therefore still advertised in `sitemap.xml` — the combination Search Console reports as the *"Submitted URL marked 'noindex'"* coverage error, because the sitemap asks for indexing and the page refuses it.

The rule is correct and was **not weakened** to make the emitter pass. The current behaviour is pinned as an explicit, commented case (`knownGapTests`) instead, because closing the gap would change the sitemap body — and therefore the Phase 149 ETag — of every deployment already using the key, which is a shipping-surface change this phase is scoped out of (GP 11 / GP 13).

**If you use `robots: noindex`, set `sitemap: exclude` alongside it.** The pack pins that workaround as working. When the gap is closed the pinned case fails by design, which is the prompt to delete it and assert conformance instead.

## Adopting the rules against your own pages

Nothing is exported — the validators live in the test pack. A consumer wanting the same gate over its own rendered output copies the `Conformance` module (about 300 lines, dependency-free beyond the BCL) into its own test project and binds it to its own page set. The three entry points worth binding first:

- `Conformance.jsonLdOfType label expectedType requiredProperties payload`
- `Conformance.sitemapUrlSet label xml`
- `Conformance.hreflangCluster label [ pageUrl, alternates; … ]` — the reciprocity check, which no emitter can perform for itself because a page cannot see its siblings.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — all packs green; the `Platform` pack grows by 92 cases.
- Fail-before / pass-after was proven against the **shipped** emitters, not only against synthetic payloads: five corruptions were applied to real source in turn, each rebuilt and re-run, then reverted. A dropped required property (4 cases red), a removed `</` breakout escape (1), a wrong sitemaps.org namespace (8), a relative canonical (2), and a cluster losing its cross-host pointers (2). All 92 pass before and after.

## Rollback

Delete `src/ToolUp.Platform.Tests/InProcess/StructuredDataConformanceTests.fs`, its `<Compile Include=…>` entry in `ToolUp.Platform.Tests.fsproj`, and the `StructuredDataConformanceTests.tests` line in `Program.fs`. Nothing else references the pack.

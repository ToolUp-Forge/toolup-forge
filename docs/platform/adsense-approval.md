# AdSense approval gotchas

Wiring `ClientConfig.AdPanel = EnabledAdPanel ...` and dropping `<AdSlot>` into a Feliz view is the easy part. Getting Google AdSense to actually serve ads against your site requires passing AdSense's site-approval process, which has a number of non-obvious requirements. This page is the operator-facing checklist — when the substrate compiles, slots render the `<ins class="adsbygoogle">` element, but AdSense returns `data-ad-status="unfilled"` for every impression, work through the items below before raising it as a substrate defect.

This guide is not authoritative — AdSense policies change. Treat it as a "things to verify first" list; the canonical reference is Google's published AdSense Program Policies and Help Center.

## Pre-deployment checklist

### 1. The site must be on a real, HTTPS-served domain

AdSense will not approve a site served from `localhost`, an IP address, or a free hosting subdomain (`*.github.io`, `*.netlify.app`, etc. — historically rejected; some have been approved more recently but the policy is inconsistent). The site must be on a domain you own, served over HTTPS with a valid TLS certificate.

Implications for development:

- **`http://localhost:8080`** — slots render but stay `unfilled`. This is expected; AdSense does not serve to non-public origins.
- **A dev / staging subdomain** (`staging.example.com`) — AdSense approves *the apex domain* `example.com`, and ad serving works across approved subdomains, but `staging.*` typically isn't on the approved list unless explicitly added in the AdSense console under "Sites".
- **HTTP-only origin** — AdSense's `adsbygoogle.js` script will still load over HTTP (mixed-content policy permitting) but slots stay unfilled.

For local development against the real AdSense bundle, use the test-mode parameter (below).

### 2. Test mode for local development

To exercise the slot rendering path without a real fill, append `?google_adtest=on` to the page URL **or** set `data-adtest="on"` on the `<ins>` element. AdSense serves real test ads from its test pool, ignores the site-approval check, and stamps `data-ad-status="filled"` so the `MutationObserver`-driven impression event fires for the analytics seam.

The substrate does not currently surface a typed `Style`-level affordance for `data-adtest`. Two workarounds:

1. **Query-string toggle** — append `?google_adtest=on` to the dev page URL; AdSense's bundle picks it up globally.
2. **Per-slot DOM injection** — for a one-off dev verification, run a small post-mount script that adds `data-adtest="on"` to the `<ins>`. Don't ship this in production.

If a typed `<AdSlot>` test-mode flag is needed for repeatable smoke tests, surface it as a follow-up enhancement rather than working around in consumer code permanently.

### 3. Minimum content depth

AdSense's review favours sites with:

- **A homepage** that describes the site's purpose in non-trivial copy.
- **Multiple content pages** (the unwritten threshold is "more than a landing page" — typical successful applications carry 10+ pages of real content; a single calculator with one landing page is borderline).
- **Original content**, not auto-generated or scraped.

For a public-utility deployment (single calculator, viewer, or converter), the practical fix is supplementary content pages: an About page, a Methodology / How It Works page, a Frequently Asked Questions page, a Privacy Policy, a Terms of Service. This is content the public utility benefits from anyway, and AdSense's reviewer treats them as part of the site's content depth.

### 4. Privacy policy and consent surface

AdSense's review checks for:

- **A linked Privacy Policy** that names Google AdSense (and any other third-party tracking the site uses), explains the cookies / identifiers AdSense uses for personalised ads, and explains how users can opt out.
- **A consent surface** for EU / UK / California users covering the Marketing / Personalisation categories. The AdPanel substrate's consent-gate composition (via `IConsentProvider`) handles the gating; the visible CMP banner is provided by whichever consent provider the deployment wires. `FundingChoicesConsentProvider` is the path of least resistance — Funding Choices is Google's own CMP, AdSense-aware, and free.
- **Targeted-advertising opt-out** mention in the policy, with a working mechanism (typically the CMP banner's "Reject all" or "Manage preferences" button).

The AdPanel substrate enforces the technical gating — `<AdSlot>` returns `Html.none` until the user grants the configured categories — but does not generate the privacy policy text. Author that yourself.

### 5. Traffic threshold and review delay

AdSense does not publish a hard traffic threshold for approval; in practice, sites with effectively zero traffic frequently get approved if every other gate is clean. Reviewer delay varies — typically days to a few weeks; the 2026 baseline is "expect 1–2 weeks, sometimes 4 weeks". The site must remain online and compliant for the full review period.

Don't pre-emptively rip out the AdPanel wiring when the first review submission is "pending" for two weeks; that's the normal cadence. Set `AdPanel = EnabledAdPanel ...` once, push to production, and wait.

### 6. Sticky ads / layout shifts / interstitial limits

Even after approval, AdSense's content-policy bots check:

- **No layout shifts** caused by slow-loading ads — reserve the slot's height before the ad loads (use a fixed-aspect `AdRectangle` / `AdVertical` / `AdHorizontal` `Format`, or wrap the slot in a min-height container).
- **No sticky-bottom ads** that overlap the page's primary content navigation.
- **No more than three** ad units per page (current 2026 guideline; was unlimited pre-2018, now policy-throttled).

The AdPanel substrate does not enforce these limits — that's the consumer's layout responsibility.

## Post-approval verification

When approval lands, the AdSense console flips the site's status to "Ready". At that point:

1. The `data-ad-status` attribute on `<ins class="adsbygoogle">` should flip from `unfilled` to `filled` for slots in active rotation. Some slots stay `unfilled` when AdSense has no relevant ad to serve — this is expected, not a bug.
2. Network traffic in DevTools shows requests to `pagead2.googlesyndication.com` returning ad payload (not just the `adsbygoogle.js` bundle).
3. The `IAdAnalyticsSink.LogImpression` callback fires (when a sink is wired) — verify via the recording-fake test pattern in [`IAdAnalyticsSinkContract`](../../src/ToolUp.Platform.Tests/Contracts/IAdAnalyticsSinkContract.fs).
4. The AdSense console's "Sites" tab shows the domain with "Ready" status; the "Ads" / "By unit" tab shows impressions accumulating.

If approval lands but slots stay `unfilled` indefinitely after the first 48 hours of public traffic, check:

- The site's domain in the AdSense console exactly matches the production domain (no typos, no missing subdomain).
- `AdClientId` in `ClientConfig.AdPanel` matches the AdSense publisher account `ca-pub-XXXXXXXXXXXXXXXX`.
- `SlotId` on each `<AdSlot>` matches a slot the AdSense console minted (not a placeholder like `1234567890`).
- The page is fully indexable (no `noindex` meta, no `robots.txt` disallow on the path).
- No ad blocker is active in the test browser — even on the developer's machine, an extension like uBlock Origin will block AdSense's bundle and prevent fills.

## Common rejection reasons (paraphrased from AdSense's published rejection emails)

| Rejection theme | What it usually means |
|---|---|
| "Insufficient content" | Site has too few content pages OR pages have too little text. Fix by adding About / FAQ / Methodology / Privacy / Terms pages. |
| "Site does not comply with AdSense Program Policies" | Often a missing or weak privacy policy. Re-check the policy is linked from every page (footer is canonical) and names Google AdSense explicitly. |
| "Site under construction" | A staging banner, "coming soon" placeholder, or "Lorem ipsum" content somewhere on the site. Remove all of it before re-submitting. |
| "Navigation issues" | The site's primary nav is broken or some links 404. Run a link checker before re-submitting. |
| "Difficult-to-use site" | Mobile UX is broken, slot density is too high, content is buried behind interstitials. AdSense reviewers test on mobile; the dev's desktop experience is not the review surface. |

Re-submission is free; AdSense's review cadence applies to each re-submission. Fix every flagged issue before re-submitting — partial fixes typically draw the same rejection.

## See also

- [`ads.md`](ads.md) — substrate shape, wiring, consent-gate composition.
- [`migrations/60-adpanel-adsense.md`](../migrations/60-adpanel-adsense.md) — consumer migration walk-through.
- Google's authoritative documentation: AdSense Help Center "Get started" + "Program policies" pages (search the AdSense Help Center directly; URLs change).

# Advertising integration

The Platform ships an opt-in AdSense embedding substrate so consumer apps monetising public-facing traffic can drop an `<AdSlot>` into any Feliz view without hand-rolling the `<ins class="adsbygoogle">` element, the `adsbygoogle.push({})` lifecycle, or the consent gate. The substrate is **narrow by design**: it embeds AdSense ad units. It carries no opinion about page layout (sidebar / banner / in-content all work) and no out-of-the-box opinion about ad-network choice beyond AdSense itself.

Higher-level compositions (e.g. a dedicated side-panel layer that pairs the AI assistant slot with an ads slot, with shared expand / collapse state) sit on top of this substrate — consumers compose `<AdSlot>` inside their own layout shells.

## When to enable

| Deployment shape | Default | When to flip on |
|---|---|---|
| Multi-tenant SaaS with paid users only | `NoAdPanel` | Don't — paid-only apps generally want zero ad surface. |
| Anonymous-mode public utility (calculators, viewers, converters) | `NoAdPanel` | Flip on when the funnel from anonymous → paid premium needs ad revenue to fund the free tier. |
| Mixed free / premium | `NoAdPanel` | Flip on for anonymous + free-tier traffic; pair with the Phase 62 premium-claim substrate to suppress slot render for paid users. |

The default `ClientConfig.AdPanel = NoAdPanel` strips every `<AdSlot>` render path — the component returns `Html.none`, no AdSense script loads, no Funding Choices banner triggers. Bytes on the wire are identical to a build with no ads-related code.

## Substrate shape

Three files in `ToolUp.Platform.Client/Client/AdPanel/`:

- **`AdScriptLoader.fs`** — singleton bootstrap. Loads `https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=...` once per page load and tracks idempotency via a `window.__toolupAdScriptLoaded` marker. Triggered automatically by the first `AdSlot` mount; consumers wanting eager-load call `AdScriptLoader.ensureLoaded` explicitly.
- **`IAdAnalyticsSink.fs`** — optional first-party impression / click logging seam. Default `NoOpAdAnalyticsSink` swallows every event silently so wiring it is a zero-cost choice. The six-rule portability audit lives at the top of the interface file ([`IAdAnalyticsSink.fs`](../../src/ToolUp.Platform.Client/Client/AdPanel/IAdAnalyticsSink.fs)); the matching contract pack is [`IAdAnalyticsSinkContract`](../../src/ToolUp.Platform.Tests/Contracts/IAdAnalyticsSinkContract.fs).
- **`ServerSinkAdAnalytics.fs`** — reference impl. POSTs `AdImpression` / `AdClick` to `/api/_platform/ads/impression` and `/api/_platform/ads/click`. Server-side handler at [`AdAnalyticsApiHandler.fs`](../../src/ToolUp.Platform.Server/Server/AdAnalyticsApiHandler.fs) records via `IAuditLog` under the `_platform` scope. The endpoint mounts only when `ServerConfig.AdAnalytics = EnabledAdAnalytics`.

The Feliz component itself lives in `ToolUp.Platform.Client/Components/AdSlot.fs` alongside `ConsentGate`.

## Minimum wiring

```fsharp
// Client.fs — enable AdPanel:
open ToolUp.Platform

let clientConfig = {
    ClientConfig.defaults with
        AdPanel = EnabledAdPanel {
            DefaultAdClientId = "ca-pub-XXXXXXXXXXXXXXXX"
            ConsentCategoriesRequired = [ Marketing; Personalisation ]
        }
        // Pair with the consent provider:
        ConsentProvider = FundingChoicesConsent "ca-pub-XXXXXXXXXXXXXXXX"
}
```

Then in any Feliz view:

```fsharp
open Components.AdSlot

let view (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        renderHeader ()
        AdSlot config {
            AdClientId = "ca-pub-XXXXXXXXXXXXXXXX"
            SlotId = "1234567890"
            Format = AdAuto
            Style = None
        }
        renderResults model dispatch
    ]
```

The `config` parameter is the deployment's `ClientConfig` — the component branches on `config.AdPanel` so a single view source compiles against both ads-enabled and ads-disabled builds. `SlotId` is the per-unit identifier the AdSense console mints when you create an ad unit; `AdClientId` is the publisher account (`ca-pub-XXXXXXXXXXXXXXXX`).

### Format DU

`AdFormat` mirrors the documented `data-ad-format` values:

| Case | `data-ad-format` | Use for |
|---|---|---|
| `AdAuto` | `auto` | Responsive — AdSense picks the format from slot dimensions. |
| `AdRectangle` | `rectangle` | Fixed-aspect rectangle slot. |
| `AdVertical` | `vertical` | Tall-skyscraper slot. |
| `AdHorizontal` | `horizontal` | Wide-banner slot. |
| `AdFluid layoutKey` | `fluid` + `data-ad-layout-key="..."` | In-content native layouts; the layout key comes from the AdSense console's "Layout key" affordance. |

`AdStyleHint` is the optional `style` attribute override; AdSense's documented default of `display:block` applies when `Style = None`.

## Consent gate composition

The `<AdSlot>` component wraps its render in `ConsentGate` (Phase 59 — see the [`IConsentProvider`](../../src/ToolUp.Platform.Client/Client/Consent/IConsentProvider.fs) interface header for the substrate shape and [migration doc](../migrations/59-iconsentprovider.md) for the consumer migration walk-through). The gate reads the deployment's `IConsentProvider`:

```fsharp
type ClientConfig = {
    // ...
    AdPanel: AdPanelMode             // Phase 60
    ConsentProvider: ConsentProvider // Phase 59
}
```

The composition sequence for a single render:

1. `<AdSlot>` reads `ClientConfig.AdPanel`. If `NoAdPanel`, returns `Html.none` immediately. **No** consent check fires. **No** AdSense script loads.
2. If `EnabledAdPanel panelConfig`, the component calls `Components.ConsentGate.render panelConfig.ConsentCategoriesRequired <child>` where `<child>` is the `<ins class="adsbygoogle">` element.
3. `ConsentGate` queries the wired `IConsentProvider` for the required categories. If every category is `Granted`, the child renders. If any is `Denied` or `NotYetDecided`, the child is replaced with `Html.none`.
4. On render, `AdSlot`'s `useEffectOnce` fires `(window.adsbygoogle = window.adsbygoogle || []).push({})` and (when `MutationObserver` is available) subscribes to `data-ad-status` changes, emitting `AdImpression` to the wired `IAdAnalyticsSink` when AdSense sets the attribute to `filled`.

The consequence: a deployment that wires a real CMP-backed `IConsentProvider` gets a fully GDPR-compliant slot for free — no marketing tracking, no AdSense script load, no impression emission until the user has explicitly granted the categories `AdPanelConfig.ConsentCategoriesRequired` names. A deployment wiring `NoOpConsentProvider` (the default) gets a slot that renders unconditionally — appropriate only in jurisdictions where consent is not required, or for development.

## Analytics seam — wiring a custom sink

`IAdAnalyticsSink` exists for deployments wanting first-party impression / click attribution alongside AdSense's own dashboard. The default is `NoOpAdAnalyticsSink`. Two shipped alternatives:

```fsharp
// Server-backed (records via IAuditLog under the _platform scope):
open ToolUp.Platform.AdPanel
Components.AdSlot.AdAnalytics.setSink (ServerSinkAdAnalytics() :> IAdAnalyticsSink)
```

```fsharp
// Custom — e.g. POSTing to a first-party analytics endpoint (Plausible,
// GA4, custom warehouse). One implementation per sink kind; the contract
// is best-effort, errors swallowed internally.
type PlausibleAdSink() =
    interface IAdAnalyticsSink with
        member _.LogImpression(event: AdImpression) = async {
            try
                let! _ = Http.post "https://plausible.io/api/event" (toPlausibleShape event)
                return ()
            with _ ->
                return ()
        }
        member _.LogClick(event: AdClick) = async {
            try
                let! _ = Http.post "https://plausible.io/api/event" (toPlausibleShape event)
                return ()
            with _ ->
                return ()
        }

Components.AdSlot.AdAnalytics.setSink (PlausibleAdSink() :> IAdAnalyticsSink)
```

The setter is a module-singleton so the sink is a deployment-wide choice, not per-component. Bootstrap order: call `AdAnalytics.setSink` once during client init before any view renders an `<AdSlot>`. The module-level mutable is the documented exception to the no-mutable-globals rule (see [`SDK style — Elmish MVU discipline`](../../CLAUDE.md#elmish-mvu-discipline)).

The contract pack [`IAdAnalyticsSinkContract`](../../src/ToolUp.Platform.Tests/Contracts/IAdAnalyticsSinkContract.fs) is what any custom sink should bind to from its own `InProcess` test file — covers best-effort completion, statelessness across calls, and cross-slot independence.

## Server-side audit endpoint (opt-in)

`ServerConfig.AdAnalytics = EnabledAdAnalytics` mounts two endpoints:

| Route | Method | Records |
|---|---|---|
| `/api/_platform/ads/impression` | POST | `AuditEvent.AdImpressionRecorded` via `IAuditLog` under `_platform` |
| `/api/_platform/ads/click` | POST | `AuditEvent.AdClickRecorded` via `IAuditLog` under `_platform` |

Both accept the `AdImpression` / `AdClick` JSON shapes the `ServerSinkAdAnalytics` client emits. The handler swallows storage failures into `204 No Content` to preserve the best-effort contract on the client; malformed bodies return `400`. Auth gating is whatever the deployment's existing pipeline applies — no special carve-out for these routes.

Default `ServerConfig.AdAnalytics = NoAdAnalytics` strips both endpoints. The corresponding `IAuditLog` cases (`AdImpressionRecorded` / `AdClickRecorded`) remain in the audit event DU regardless — they cost nothing when unused.

## AdSense approval gotchas

See [`adsense-approval.md`](adsense-approval.md) for the operator-facing checklist: HTTPS-only, no localhost test ads (use AdSense's `data-adtest="on"` parameter), site-review delay (~weeks), policy requirements (privacy policy, minimum content depth, traffic thresholds).

## What this substrate does NOT cover

Per the Phase 60 phase body, the substrate is deliberately narrow. The following are out of scope for the substrate itself; consumers compose them separately:

- **Click-tracking redirect handler.** AdSense doesn't expose click events through `adsbygoogle.push`; capturing them requires a `/api/ads/click/{clickId}` redirect-handler pattern that consumers wanting click telemetry implement themselves. The `IAdAnalyticsSink.LogClick` method exists so the same sink interface covers both event types, but the substrate ships no default click-capture mechanism.
- **Alternative ad networks** (header bidding, Google Ad Manager beyond basic AdSense, direct-sold inventory). A future companion package may introduce an `IAdProvider` abstraction once a second concrete provider lands; today's substrate is AdSense-specific.
- **Side-panel layout composition.** Apps wanting an "alternative to AI panel" UX where the AdSlot expands / collapses alongside other side-panel content build the layout themselves; the substrate ships only the slot itself.
- **Ad-blocker mitigation** (neutral route names, unsuspicious DOM class names). The substrate uses AdSense's documented class names (`adsbygoogle`) and AdSense's published asset URLs; standard ad-blocker lists will block both. Mitigation strategies are deployment-specific and out of scope for the SDK.

## See also

- [`consent.md`](consent.md) — Phase 59 `IConsentProvider` substrate that gates `<AdSlot>` rendering.
- [`portability-rules.md`](portability-rules.md) — the six rules audited on `IAdAnalyticsSink`.
- [`migrations/60-adpanel-adsense.md`](../migrations/60-adpanel-adsense.md) — consumer migration doc (drop-in usage + verification + rollback).
- [`adsense-approval.md`](adsense-approval.md) — AdSense site-approval gotchas.

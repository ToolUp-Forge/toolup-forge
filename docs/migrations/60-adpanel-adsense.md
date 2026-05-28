# Phase 60 — AdSense `AdPanel` substrate (consumer migration)

**What changes.** SDK ships an AdSense embedding substrate: `Components.AdSlot` Feliz component + `AdScriptLoader` idempotent bootstrap + `IAdAnalyticsSink` interface + `NoOpAdAnalyticsSink` (default) + `ServerSinkAdAnalytics` (reference impl) + server-side `/api/_platform/ads/{impression,click}` endpoints mounted via `ServerConfig.AdAnalytics = EnabledAdAnalytics`.

**Scope.** Opt-in. Default `ClientConfig.AdPanel = NoAdPanel` + `ServerConfig.AdAnalytics = NoAdAnalytics` preserve today's behaviour byte-for-byte — `<AdSlot>` returns `Html.none` and no AdSense JS loads.

## Diff to apply

Consumers wanting AdSense monetisation:

```fsharp
// Client.fs — enable AdPanel:
let clientConfig = {
    ClientConfig.defaults with
        AdPanel = EnabledAdPanel {
            DefaultAdClientId = "ca-pub-XXXXXXXXXXXXXXXX"
            ConsentCategoriesRequired = [ Marketing; Personalisation ]
        }
        // Pair with Phase 59 consent provider:
        ConsentProvider = FundingChoicesConsent "ca-pub-XXXXXXXXXXXXXXXX"
}

// Drop AdSlot into any Feliz view:
open Components.AdSlot
AdSlot config { AdClientId = "..."; SlotId = "1234567890"; Format = AdAuto; Style = None }
```

For consumers wanting first-party impression / click telemetry:

```fsharp
// Server.fs — enable ad analytics:
let serverConfig = {
    ServerConfig.defaults with
        AdAnalytics = EnabledAdAnalytics
}

// Client.fs bootstrap — swap the analytics sink:
open ToolUp.Platform.AdPanel
Components.AdSlot.AdAnalytics.setSink (ServerSinkAdAnalytics() :> IAdAnalyticsSink)
```

This mounts `POST /api/_platform/ads/impression` + `POST /api/_platform/ads/click` recording `AdImpressionRecorded` / `AdClickRecorded` audit events.

## Verification

1. `dotnet build` clean; Fable transpile clean.
2. With `AdPanel = EnabledAdPanel _` + user grants required consent categories → `<AdSlot>` renders an `<ins class="adsbygoogle">` element + AdSense bundle loads once per page; AdSense fills the slot subject to its own approval gates.
3. With `AdPanel = NoAdPanel` (default) → `<AdSlot>` returns empty fragment; no `https://pagead2.googlesyndication.com/...` request appears in DevTools Network.
4. With `AdAnalytics = EnabledAdAnalytics` + sink swapped → MutationObserver fires `LogImpression` on `data-ad-status=filled`; impressionhandler records `AdImpressionRecorded` audit event.

## Rollback

Revert `ClientConfig.AdPanel = NoAdPanel` + `ServerConfig.AdAnalytics = NoAdAnalytics`. Existing `<AdSlot>` call sites in consumer views silently return empty fragments — no orphaned DOM nodes.

## Consumers

The migration is **N-A** for apps whose deployment posture doesn't surface ads (internal tools, paid-tier products); consumers in the ads-monetised public-utility class adopt by flipping the gate.

## Out of scope

Click-tracking redirect handler stays out of this substrate per the phase body — richer click telemetry composes via a future richer side-panel `AdPanel` companion (out-of-tree).

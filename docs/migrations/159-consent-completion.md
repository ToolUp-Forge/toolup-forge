# Phase 159 — `IConsentProvider` completion (consent store + banner + CMP bridge)

**What changes.** Phase 59 shipped the client-side `IConsentProvider` seam (consent
decisions browser-local). Phase 159 completes it with three additive pieces, all off by
default (GP 13):

1. **`ConsentStateStore`** (server) — durable per-subject consent persistence over
   `IEntityStore`. `IConsentStateStore` interface + `InMemoryConsentStateStore` (dev) +
   `EntityBackedConsentStateStore` (production, durable across restart). Consent changes
   emit a `Custom:ConsentGranted` / `Custom:ConsentWithdrawn` audit row (no new
   `AuditEvent` DU case). Selected via `ServerConfig.ConsentStateStore` /
   `ServerApp.withConsentStateStore`.
2. **`ConsentBanner`** (client) — the SDK's own category-toggle Feliz banner (pure MVU
   core + view). Renders nothing under `NoConsentProvider`.
3. **`IConsentManagementBridge`** (client) — a narrow read-side seam a third-party CMP
   companion implements; `ConsentProvider.resolve` wraps a registered bridge in a
   `BridgedConsentProvider` for `CustomConsentProvider "<name>"`.

Plus an **AdSlot fail-closed fix**: the AdSense script now loads only once the required
ad-consent category is granted (previously the `<ins>` render was gated but the script
loaded on mount regardless).

A deployment that does not opt in is **byte-for-byte unchanged**.

## Server — durable consent store

Default `NoConsentStateStore` (nothing registered). To opt in:

```fsharp
// Entity-backed (durable across restart). Requires EntityStore = EnabledEntityStore;
// the ConsentRecord entity type is registered automatically by the compose path.
let config =
    { ServerConfig.defaults with
        EntityStore = EnabledEntityStore
        ConsentStateStore = EntityBackedConsentStateStore }

// or fluent:
app |> ServerApp.withConsentStateStore EntityBackedConsentStateStore
```

`TOOLUP_CONSENT_STATE_STORE = entity-backed | in-memory | off` lifts the same at runtime
(`ServerConfig.fromEnv`).

Resolve `IConsentStateStore` from DI and use it:

```fsharp
let store = sp.GetRequiredService<IConsentStateStore>()

// Persist a subject's decision (emits Custom:ConsentGranted).
let record = ConsentRecord.create subjectId granted denied DateTimeOffset.UtcNow BannerInteraction
let! _ = store.Upsert(scopeId, record)

// Read it back (durable across restart).
let! current = store.Get(scopeId, subjectId)

// Withdraw categories (emits Custom:ConsentWithdrawn; Necessary is never withdrawable).
let! _ = store.Withdraw(scopeId, subjectId, [ Marketing ])
```

The store is scope-isolated by construction (GP 4) and satisfies the six portability
rules; `IConsentStateStoreContract` validates both impls (round-trip + per-subject /
per-scope isolation + withdraw), and a restart-durability test proves a fresh
entity-backed instance over the same substrate reads back a prior write.

## Client — banner

```fsharp
open ToolUp.Platform.Consent

// Wire the commit sink once at bootstrap: persist the saved state to your durable
// IConsentStateStore (e.g. POST to an endpoint that calls Upsert) and/or update a
// writable provider so ConsentGate surfaces react.
ConsentBanner.onCommit (fun state -> postConsentToServer state)

// Render the banner (no-op under NoConsentProvider; the CMP owns its own UX under
// FundingChoicesConsent / a registered CustomConsentProvider).
ConsentBanner.render ()
```

The MVU core (`init` / `update` / `toConsentState`) is pure and exported — the Fable
NodeTest harness drives it directly.

## Client — third-party CMP bridge

```fsharp
// A CMP companion implements the narrow read-side seam and registers it by name…
type QuantcastBridge() =
    interface IConsentManagementBridge with
        member _.Name = "Quantcast"
        member _.CurrentDecision() = async { return readQuantcastTcfDecision () }
        member _.Subscribe handler = subscribeQuantcast handler

ConsentManagementBridge.register (QuantcastBridge())

// …and the deployment selects it.
// ClientConfig.ConsentProvider = CustomConsentProvider "Quantcast"
```

`ConsentProvider.resolve` wraps the registered bridge in a `BridgedConsentProvider`; an
unregistered name falls back to the no-op (fail-closed — gated surfaces stay hidden).
Google Funding Choices is the reference CMP wiring: the existing
`FundingChoicesConsentProvider` reads the IAB TCF v2.2 decision the same way a bridge's
`CurrentDecision` would.

## API baseline

This phase adds public surface (consent store types, `ConsentStateStoreMode`,
`ServerConfig.ConsentStateStore`, the banner + bridge). The `ServerConfig` record's
constructor signature changes (additive field), which the Phase 175 SemVer guard treats
as churn — the `api-baselines/*.approved.txt` for Core / Server / Client / RAG.Server were
regenerated (`TOOLUP_APPROVE_API=1`) and committed alongside this phase.

## Rollback

Revert the phase commit(s). `ConsentStateStore = NoConsentStateStore` (default) means a
deployment never touched the store; the banner is unrendered under `NoConsentProvider`;
the AdSlot change is a strict tightening (script load is now gated, never looser).

# Phase 59 — `IConsentProvider` substrate (consumer migration)

**What changes.** SDK ships a client-side consent-management seam: `IConsentProvider` interface + `NoOpConsentProvider` (default) + `FundingChoicesConsentProvider` (Google CMP wrapper) + `ConsentGate` Feliz component + optional server-side audit endpoint mounted via `ServerConfig.ConsentAudit = EnabledConsentAudit`.

**Scope.** Opt-in. Default `ClientConfig.ConsentProvider = NoConsentProvider` + `ServerConfig.ConsentAudit = NoConsentAudit` preserve today's behaviour byte-for-byte — existing consumers see no change.

## Diff to apply

Consumers deploying to jurisdictions that require explicit consent (EU + UK + jurisdictions adopting CPRA-shape regulation) wire Funding Choices:

```fsharp
// Client.fs — switch to Funding Choices CMP:
let clientConfig = {
    ClientConfig.defaults with
        ConsentProvider = FundingChoicesConsent "ca-pub-XXXXXXXXXXXXXXXX"
        // ... other settings
}

// Wrap consent-sensitive surfaces in ConsentGate:
open Components.ConsentGate

ConsentGate [ Analytics; Marketing ] None (
    Html.div [
        // ... ad slot, analytics beacon, etc.
    ]
)
```

For consumers wanting server-side audit of consent decisions:

```fsharp
// Server.fs — enable consent audit:
let serverConfig = {
    ServerConfig.defaults with
        ConsentAudit = EnabledConsentAudit
}
```

This mounts `POST /api/_platform/consent` which records `ConsentRecorded` audit events under `_platform` scope. The client-side provider is responsible for actually posting to the endpoint — `FundingChoicesConsentProvider` does not post by default; consumers wanting server-side audit ship a thin override that emits ConsentEvent posts on `OnStateChanged`.

## Verification

1. `dotnet build` clean.
2. With `ConsentProvider = FundingChoicesConsent _`, load any page — Funding Choices banner appears on first visit (assumes the ad-client id is registered with Google).
3. With `NoConsentProvider`, no banner; `ConsentGate`-wrapped surfaces render only when the consumer-declared categories don't include anything beyond `Necessary` (which is granted by default).
4. With `ConsentAudit = EnabledConsentAudit`, `POST /api/_platform/consent` with a `ConsentEvent` body returns 204 and the audit log shows the recorded event.

## Rollback

Revert `ClientConfig.ConsentProvider = NoConsentProvider` + `ServerConfig.ConsentAudit = NoConsentAudit`. All consent-sensitive `ConsentGate` wrappers continue to render their children when the configured categories overlap only with `Necessary`; surfaces conditioned on `Analytics` / `Marketing` / `Personalisation` stay hidden.

## Consumers

The migration is **N-A** for internal-tool deployments with no consent surface. Consumers that surface analytics / marketing / personalisation categories — typically ads-monetised or public-facing deployments — adopt by configuring an `IConsentProvider`.

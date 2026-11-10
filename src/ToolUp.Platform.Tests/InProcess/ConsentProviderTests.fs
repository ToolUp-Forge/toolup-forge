module ToolUp.Platform.Tests.InProcess.ConsentProviderTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Consent
open ToolUp.Platform.Tests.Contracts

// ─── Phase 59 — IConsentProvider in-process contract binding ─────────
//
// Drives the portable `IConsentProviderContract` pack against
// `NoOpConsentProvider` (the SDK's default-on impl).
//
// FundingChoicesConsentProvider's CMP-event handling is Fable-only
// (`JS.setTimeout`, `window.__tcfapi`, `addEventListener`) and not
// reached from the .NET runner. Browser-side parity is exercised
// by the Fable consumer's smoke tests. The OnStateChanged firing path
// is exercised against production `ConsentGated.watch` in
// `ConsentGatedScriptTests`.

let tests =
    let factory () =
        NoOpConsentProvider() :> IConsentProvider

    IConsentProviderContract.tests "NoOpConsentProvider" factory
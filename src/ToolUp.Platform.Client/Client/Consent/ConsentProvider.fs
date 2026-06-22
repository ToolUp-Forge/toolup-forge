// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Consent

open ToolUp.Platform

// ─── Provider resolver ────────────────────────────────────────────
//
// SDK.Client resolves `ClientConfig.ConsentProvider` to a concrete
// `IConsentProvider` instance once per tab. Components access it via
// the `current` accessor below. The instance is a tab-singleton —
// the consent decision is browser-local so a single provider per tab
// is correct.

module ConsentProvider =
    let mutable private instance: IConsentProvider option = None

    /// Resolve the configured provider mode into a live instance.
    /// Idempotent — subsequent calls return the same instance.
    let resolve (mode: ConsentProviderMode) : IConsentProvider =
        match instance with
        | Some provider -> provider
        | None ->
            let provider: IConsentProvider =
                match mode with
                | NoConsentProvider -> NoOpConsentProvider() :> _
                | FundingChoicesConsent adClientId -> FundingChoicesConsentProvider(adClientId) :> _
                | CustomConsentProvider name ->
                    // Phase 159 — a third-party CMP companion registers
                    // an `IConsentManagementBridge` under this name; the
                    // resolver wraps it in a `BridgedConsentProvider`.
                    // Until one is wired, fall back to the no-op to keep
                    // the surface safe (fail-closed — gated surfaces stay
                    // hidden).
                    match ConsentManagementBridge.tryResolve name with
                    | Some bridge -> BridgedConsentProvider(bridge) :> _
                    | None -> NoOpConsentProvider() :> _

            instance <- Some provider
            provider

    /// The currently-resolved provider, or the default no-op if none
    /// has been resolved yet. Components reach for this rather than
    /// holding a per-component reference.
    let current () : IConsentProvider =
        match instance with
        | Some provider -> provider
        | None -> resolve NoConsentProvider
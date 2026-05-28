// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Consent

open System
open ToolUp.Platform

// ─── IConsentProvider ──────────────────────────────────────────────
//
// Phase 59 — pluggable consent management. `IConsentProvider` is the
// client-side seam every consent-sensitive surface composes against —
// ad slots, first-party analytics, third-party embeds, any category
// gated by `ConsentCategory`. The browser owns the consent decision;
// the SDK ships two reference implementations (`NoOpConsentProvider`
// default + `FundingChoicesConsentProvider` adapter) and reserves a
// seam for sub-companion CMPs (Quantcast, Cookiebot, OneTrust) via
// `ConsentProviderMode.CustomConsentProvider`.
//
// **Phase 9c portability rules** (all six honoured):
//
//   1. Identity by value. The user-side identity carried alongside
//      consent decisions is a browser-local UUID (`AnonymousUserId`,
//      `string`) — never a live handle.
//   2. Async at every boundary. `GetCurrentState` / `RequestConsent` /
//      `HasConsented` return `Async<_>`; `OnStateChanged` returns
//      `IDisposable` (subscription handle, not a synchronous result).
//   3. Retry + supervision as data. No callback-typed `OnFailure` /
//      `OnError` parameters. Unknown / errored states fold into
//      `ConsentDecision.NotYetDecided` — surfaces stay hidden until
//      the user has actively granted, matching opt-in semantics.
//   4. Stateless handlers between invocations. Subscribers passed to
//      `OnStateChanged` are pure `ConsentState -> unit` functions;
//      durable state lives in the provider's own store (localStorage,
//      CMP host page, or future sub-companion).
//   5. No cross-shard ordering promises. Providers only commit to
//      per-browser-tab ordering. Cross-device consent sync is a
//      future Phase 62-gated concern, not a contract guarantee.
//   6. Precision documented. State-change propagation runs on the
//      browser's micro-task queue (event-loop ceiling) — sub-tick
//      promises are not a contract requirement of this interface.
//
// Contract-pack binding: `ToolUp.Platform.Tests.Contracts.IConsentProviderContract`
// drives the portable surface assertions; any external implementation
// can validate against the same conformance bar.

type IConsentProvider =
    /// Snapshot the current consent state. Cheap — backed by an
    /// in-memory cache that the provider keeps in sync with its
    /// upstream (localStorage / CMP-host page state).
    abstract GetCurrentState: unit -> Async<ConsentState>

    /// Prompt the user to (re-)decide consent for the supplied
    /// categories. Triggers the CMP's banner / modal. Resolves once
    /// the user has interacted (or rejected the prompt entirely).
    abstract RequestConsent: ConsentCategory list -> Async<ConsentState>

    /// Resolve the current decision for a single category. Convenience
    /// over `GetCurrentState` for components that only care about one
    /// surface (an ad slot, an analytics beacon).
    abstract HasConsented: ConsentCategory -> Async<ConsentDecision>

    /// Subscribe to consent-state changes. The returned `IDisposable`
    /// unsubscribes when disposed. Subscribers fire on every transition
    /// (granted → denied, denied → granted, version bump).
    abstract OnStateChanged: (ConsentState -> unit) -> IDisposable
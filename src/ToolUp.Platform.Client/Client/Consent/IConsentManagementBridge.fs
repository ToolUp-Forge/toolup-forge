// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Consent

open System
open ToolUp.Platform

// ─── IConsentManagementBridge ──────────────────────────────────────
//
// Phase 159 — the seam a third-party Consent-Management-Platform
// companion implements to feed its decision into the SDK without
// re-implementing the banner UI. Where `IConsentProvider` is the full
// surface every consent-sensitive component composes against, the
// bridge is a deliberately narrow read-side seam: a CMP already owns
// the prompt UX and the per-purpose decision; the bridge just exposes
// that decision as a `ConsentState` the SDK understands.
//
// A CMP companion (`ToolUp.Consent.Quantcast`, `Cookiebot`,
// `OneTrust`, …) registers its bridge by name via
// `ConsentManagementBridge.register`, and a deployment selects it with
// `ConsentProviderMode.CustomConsentProvider "<name>"`. The resolver
// (`ConsentProvider.resolve`) wraps the registered bridge in a
// `BridgedConsentProvider` — so the rest of the SDK sees a normal
// `IConsentProvider` and the CMP author writes only the two read-side
// members.
//
// Reference wiring: Google Funding Choices. The existing
// `FundingChoicesConsentProvider` (selected by
// `FundingChoicesConsent adClientId`) reads the IAB TCF v2.2 decision
// directly; a third-party CMP that already surfaces a TCF / per-purpose
// decision implements `CurrentDecision` by translating it to a
// `ConsentState` the same way, then registers under its own name. The
// bridge reads the CMP's decision — it never re-implements the banner.
//
// Six-rule portability (the bridge is the seam, not a distributed
// store, but the same discipline keeps it framework-portable):
//   1. Identity by value — decisions are `ConsentState` records.
//   2. Async at every boundary — `CurrentDecision` returns `Async<_>`;
//      `Subscribe` returns an `IDisposable` handle (not a sync result).
//   3. Retry/supervision as data — no callback-typed failure params;
//      an unreadable CMP decision folds to `ConsentState.initial`
//      (opt-in-safe: only `Necessary` granted).
//   4. Stateless between calls — subscribers are pure
//      `ConsentState -> unit`; durable state lives in the CMP host page.

/// Narrow read-side seam a third-party CMP companion implements. The
/// SDK wraps it in a `BridgedConsentProvider` to satisfy the full
/// `IConsentProvider` surface.
type IConsentManagementBridge =
    /// The CMP author's name for this bridge — matches the
    /// `CustomConsentProvider "<name>"` selector. Used as the registry
    /// key and surfaced in `ConsentSource.CmpDecision`.
    abstract Name: string

    /// Snapshot the CMP's current decision as a `ConsentState`.
    /// Implementations that cannot read a decision yet return
    /// `ConsentState.initial` (only `Necessary` granted) so gated
    /// surfaces stay hidden until the user opts in.
    abstract CurrentDecision: unit -> Async<ConsentState>

    /// Subscribe to CMP decision changes. The returned `IDisposable`
    /// unsubscribes when disposed.
    abstract Subscribe: (ConsentState -> unit) -> IDisposable

/// Wraps an `IConsentManagementBridge` as a full `IConsentProvider`.
/// `RequestConsent` returns the current decision unchanged — the CMP
/// owns the prompt UX and re-prompts on its own policy/version bumps.
type BridgedConsentProvider(bridge: IConsentManagementBridge) =
    interface IConsentProvider with
        member _.GetCurrentState() = bridge.CurrentDecision()

        member _.RequestConsent(_categories: ConsentCategory list) = bridge.CurrentDecision()

        member _.HasConsented(category: ConsentCategory) = async {
            let! state = bridge.CurrentDecision()

            if Set.contains category state.Granted then return Granted
            elif Set.contains category state.Denied then return Denied
            else return NotYetDecided
        }

        member _.OnStateChanged(handler: ConsentState -> unit) = bridge.Subscribe handler

/// Registry of named CMP bridges. A CMP companion registers its bridge
/// at client bootstrap; `ConsentProvider.resolve` looks it up when the
/// deployment selects `CustomConsentProvider "<name>"`. Module-level
/// mutable state is a documented exception (a deployment-wide registry,
/// same shape as `AdAnalytics.setSink` / the resolved provider
/// singleton).
module ConsentManagementBridge =
    let private registry =
        System.Collections.Generic.Dictionary<string, IConsentManagementBridge>()

    /// Register a CMP bridge under its `Name`. Last registration for a
    /// given name wins (re-registration replaces). Call once per CMP
    /// companion at client bootstrap, before any consent surface mounts.
    let register (bridge: IConsentManagementBridge) : unit = registry[bridge.Name] <- bridge

    /// Resolve a registered bridge by name, or `None` when no CMP
    /// companion has registered under that name.
    let tryResolve (name: string) : IConsentManagementBridge option =
        match registry.TryGetValue name with
        | true, bridge -> Some bridge
        | _ -> None
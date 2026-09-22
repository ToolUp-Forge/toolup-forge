// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 191 — the one consent gate every side-effecting script load and
/// emission in this tier runs through: `ConsentGated` (the transport-free
/// decision, fail-closed) and `ConsentGatedScript` (the Feliz wrapper over
/// it). The ad path and the telemetry path are its first two consumers; a
/// consumer's own third-party embed is the third.
module Components.ConsentGatedScript

open System
open Feliz
open ToolUp.Platform
open ToolUp.Platform.Consent

// ─── Phase 191 — the consent gate as ONE seam ──────────────────────
//
// Two consent-categorised side effects ship in this tier — the AdSense
// bundle (`AdSlot`, gated since Phase 159) and product telemetry
// (`Telemetry.trackVia`, gated since Phase 163) — and until this file
// each carried its OWN hand-written gate at its call site. Both were
// correct; neither was reusable, so a consumer's third-party embed (a
// video player, a support widget, a marketing pixel) got nothing for
// free and had to re-derive the same fail-closed logic by hand.
//
// This module is that logic, once. `ConsentGated` is the transport-free
// core: an `Async` one-shot check (`run`) for an effect that happens at
// a point in time, and a subscription (`watch`) for a surface that must
// track the decision for as long as it is mounted.
// `ConsentGatedScript` is the Feliz wrapper over `watch` for the common
// "load a script, then render something" case.
//
// **Fail-closed is the default and the only mode.** Only an explicit
// `ConsentDecision.Granted` opens the gate: `Denied` and `NotYetDecided`
// both suppress (opt-in semantics — the pre-banner state is not
// consent), a provider that throws suppresses, and the shipped
// `NoOpConsentProvider` default grants nothing but `Necessary`, so a
// deployment that has wired no CMP loads nothing at all. Nothing in
// this module can be configured to open the gate on anything else.
//
// **An empty `required` list permits.** It is the declaration "this
// effect needs no consent category", and it matches
// `ConsentState.hasAll []` exactly — the predicate the render-side
// `ConsentGate` has always used. It is not a hole in the gate: the
// consumer that writes `[]` is the one declaring the effect
// uncategorised, and `AdPanelConfig.ConsentCategoriesRequired` behaves
// as it did before this file existed (GP 11).

/// The transport-free half of the consent gate: no React, no DOM, no
/// script loading — just the decision. Exposed as plain functions so
/// the enforcement is assertable off the browser, which is where every
/// test of it runs.
module ConsentGated =

    /// The gate as a pure predicate: only an explicit `Granted` opens
    /// it. The difference between `Denied` and `NotYetDecided` is
    /// invisible here on purpose, since neither is consent.
    let isPermitted (decision: ConsentDecision) : bool = decision = Granted

    /// Ask `provider` whether EVERY required category is granted.
    /// Short-circuits on the first category that is not, so a
    /// single-category gate asks exactly one question and a denied
    /// first category never queries the rest.
    let rec permits (provider: IConsentProvider) (required: ConsentCategory list) : Async<bool> = async {
        match required with
        | [] -> return true
        | category :: rest ->
            let! decision = provider.HasConsented category

            if isPermitted decision then
                return! permits provider rest
            else
                return false
    }

    /// Run `effect` only when every required category is granted.
    /// Never throws at its own call site: a provider that throws is
    /// treated as "no consent" (fail-closed, matching
    /// `IConsentProvider`'s own rule that unknown / errored states fold
    /// into `NotYetDecided`), and an effect that throws is swallowed
    /// too — a gated side effect must not break the surface that
    /// declared it.
    let run (provider: IConsentProvider) (required: ConsentCategory list) (effect: unit -> Async<unit>) : Async<unit> = async {
        try
            let! permitted = permits provider required

            if permitted then
                do! effect ()
        with _ ->
            return ()
    }

    /// Track whether every required category is granted, for as long as
    /// the returned `IDisposable` is undisposed. `onChanged` fires once
    /// with the provider's current state and again on every transition
    /// (granted → denied as well as denied → granted, so a surface can
    /// tear its effect down again when consent is withdrawn). Disposing
    /// unsubscribes AND suppresses the in-flight initial read, so a
    /// surface that unmounts before the provider answers never calls
    /// back into a dead scope.
    let watch (provider: IConsentProvider) (required: ConsentCategory list) (onChanged: bool -> unit) : IDisposable =
        let mutable cancelled = false

        let evaluate (state: ConsentState) =
            if not cancelled then
                onChanged (ConsentState.hasAll required state)

        async {
            let! initial = provider.GetCurrentState()
            evaluate initial
        }
        |> Async.StartImmediate

        let subscription = provider.OnStateChanged evaluate

        { new IDisposable with
            member _.Dispose() =
                cancelled <- true
                subscription.Dispose()
        }

/// Wrap a consent-categorised script load (an ad bundle, an analytics
/// tag, a third-party embed) behind the composed `IConsentProvider`.
/// `load` runs only once every required category is granted — and again
/// if consent is withdrawn and re-granted — and `children` render only
/// then; until then the component is `Html.none` and `load` has never
/// been reached. This is the third call site's entry point: a consumer
/// registers its own loader as `load` and inherits the same gate the
/// SDK's own ad and telemetry paths run under.
[<ReactComponent>]
let ConsentGatedScript (required: ConsentCategory list) (load: unit -> unit) (children: ReactElement) : ReactElement =
    let granted, setGranted = React.useState false

    React.useEffectOnce (fun () ->
        let subscription =
            ConsentGated.watch (ConsentProvider.current ()) required setGranted

        FsReact.createDisposable subscription.Dispose)

    React.useEffect (
        (fun () ->
            if granted then
                load ()),
        [| box granted |]
    )

    if granted then children else Html.none
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Consent.ConsentBanner

open System
open Feliz
open ToolUp.Elmish
open ToolUp.Platform

// ─── Phase 159 — Feliz consent banner ──────────────────────────────
//
// The SDK's own category-toggle consent banner. A deployment running
// `ConsentProviderMode.NoConsentProvider` (the default) gets NO banner
// — `render` short-circuits to `Html.none` when the resolved provider
// is the no-op (GP 13: a deployment that hasn't opted into consent
// management is byte-for-byte unchanged). Under `FundingChoicesConsent`
// / `CustomConsentProvider`, the CMP owns its own banner UX, so this
// SDK banner is for the local / first-party consent path.
//
// The MVU core (`Model` / `Msg` / `init` / `update` / `toConsentState`)
// is PURE and exported so it is unit-testable through the Fable
// NodeTest harness without rendering. The view is a thin Feliz wrapper
// that holds the model in `React.useState` and commits the resulting
// `ConsentState` through the injectable `commitSink` (which a consumer
// wires to its durable `IConsentStateStore` write + audit emission —
// see `docs/migrations/159-consent-completion.md`).

/// The categories the banner lets the user toggle. `Necessary` is
/// never listed — it is strictly-required and always granted.
let defaultManagedCategories: ConsentCategory list = [
    Functional
    Analytics
    Marketing
    Personalisation
    ThirdPartyEmbeds
]

type Model = {
    /// Categories this banner manages (toggle order preserved).
    Managed: ConsentCategory list
    /// Per-category granted toggle. Keys are exactly `Managed`.
    Pending: Map<ConsentCategory, bool>
    /// Consent version carried from the seed state, re-stamped on the
    /// committed `ConsentState` so a CMP policy bump invalidates prior
    /// decisions.
    Version: int
    /// True once the user has saved — the banner dismisses.
    Dismissed: bool
}

type Msg =
    | ToggleCategory of ConsentCategory
    | AcceptAll
    | RejectAll
    | SavePreferences

/// Seed a banner model from the managed categories + the provider's
/// current `ConsentState`. A category already granted starts toggled
/// on; everything else starts off (opt-in default).
let init (managed: ConsentCategory list) (current: ConsentState) : Model = {
    Managed = managed
    Pending = managed |> List.map (fun c -> c, Set.contains c current.Granted) |> Map.ofList
    Version = current.ConsentVersion
    Dismissed = false
}

/// Project the banner's pending toggles into a `ConsentState`.
/// `Necessary` is always granted; every managed category lands in
/// `Granted` (toggle on) or `Denied` (toggle off). Pure — the unit of
/// the NodeTest.
let toConsentState (now: DateTimeOffset) (model: Model) : ConsentState =
    let granted =
        model.Pending
        |> Map.toList
        |> List.choose (fun (c, on) -> if on then Some c else None)
        |> Set.ofList
        |> Set.add Necessary

    let denied =
        model.Pending
        |> Map.toList
        |> List.choose (fun (c, on) -> if on then None else Some c)
        |> Set.ofList

    {
        Granted = granted
        Denied = denied
        LastUpdatedAt = now
        ConsentVersion = model.Version
    }

/// The commit sink — a consumer wires this at client bootstrap to
/// persist the saved `ConsentState` (POST to a durable
/// `IConsentStateStore` write + audit emission) and/or update its
/// writable provider so `ConsentGate` surfaces react. Default no-op
/// (module-level mutable is the documented deployment-wide-sink
/// exception, same shape as `AdAnalytics.setSink`).
let mutable private commitSink: ConsentState -> unit = ignore

/// Register the commit sink. Call once at client bootstrap.
let onCommit (sink: ConsentState -> unit) : unit = commitSink <- sink

/// Invoke the registered commit sink with the saved state. Exposed for
/// the Cmd effect (and reachable by tests that drive the Cmd).
let commit (state: ConsentState) : unit = commitSink state

/// Pure MVU update. `SavePreferences` dismisses the banner and emits a
/// Cmd that fires the commit sink with the projected `ConsentState`.
let update (now: DateTimeOffset) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | ToggleCategory c ->
        match Map.tryFind c model.Pending with
        | Some on ->
            {
                model with
                    Pending = Map.add c (not on) model.Pending
            },
            Cmd.none
        | None -> model, Cmd.none
    | AcceptAll ->
        {
            model with
                Pending = model.Managed |> List.map (fun c -> c, true) |> Map.ofList
        },
        Cmd.none
    | RejectAll ->
        {
            model with
                Pending = model.Managed |> List.map (fun c -> c, false) |> Map.ofList
        },
        Cmd.none
    | SavePreferences ->
        let state = toConsentState now model
        // Raw Cmd: a single fire-and-forget subscription that commits
        // the saved state. Kept out of `update`'s pure body so the
        // transition stays testable.
        { model with Dismissed = true },
        [
            fun _ -> commit state
        ]

// ─── View ──────────────────────────────────────────────────────────

/// Human label for a managed category. The DU case is the persisted,
/// compared value; only this projection is localised.
let private categoryLabel (msgs: ConsentCategoryMessages) =
    function
    | Necessary -> msgs.Necessary
    | Functional -> msgs.Functional
    | Analytics -> msgs.Analytics
    | Marketing -> msgs.Marketing
    | Personalisation -> msgs.Personalisation
    | ThirdPartyEmbeds -> msgs.ThirdPartyEmbeds

[<ReactComponent>]
let ConsentBanner (managed: ConsentCategory list) : ReactElement =
    // Phase 751 — a component, so the ordinary hook applies and no
    // `…With` variant is needed. Read before the `NoOpConsentProvider`
    // short-circuit so the hook order is unconditional.
    let msgs = (MessageCatalogProvider.useMessages ()).Consent
    let provider = ConsentProvider.current ()

    // GP 13 — no consent provider composed ⇒ no banner. The default
    // `NoConsentProvider` resolves to `NoOpConsentProvider`, so the
    // type test keeps a default deployment byte-for-byte unchanged.
    match provider with
    | :? NoOpConsentProvider -> Html.none
    | _ ->
        let model, setModel = React.useState (init managed ConsentState.initial)

        React.useEffectOnce (fun () ->
            let mutable cancelled = false

            async {
                let! current = provider.GetCurrentState()

                if not cancelled then
                    setModel (init managed current)
            }
            |> Async.StartImmediate

            FsReact.createDisposable (fun () -> cancelled <- true))

        let dispatch (msg: Msg) =
            let next, cmd = update DateTimeOffset.UtcNow msg model
            setModel next
            // Run the Cmd's fire-and-forget subscriptions (the commit
            // effect). Dispatch is a no-op for these subs.
            for sub in cmd do
                sub (fun _ -> ())

        if model.Dismissed then
            Html.none
        else
            Html.div [
                prop.className
                    "fixed bottom-0 inset-x-0 z-50 border-t border-border bg-[var(--surface)] text-[var(--text-strong)] px-5 py-4 shadow-lg"
                prop.children [
                    Html.div [
                        prop.className "max-w-4xl mx-auto flex flex-col gap-3"
                        prop.children [
                            Html.p [ prop.className "text-sm text-[var(--muted)]"; prop.text msgs.Body ]
                            Html.div [
                                prop.className "flex flex-wrap gap-4"
                                prop.children [
                                    for category in managed do
                                        let isOn = Map.tryFind category model.Pending |> Option.defaultValue false

                                        Html.label [
                                            prop.className "flex items-center gap-2 text-sm"
                                            prop.children [
                                                Html.input [
                                                    prop.type' "checkbox"
                                                    prop.isChecked isOn
                                                    prop.onChange (fun (_: bool) -> dispatch (ToggleCategory category))
                                                ]
                                                Html.span [ prop.text (categoryLabel msgs.Categories category) ]
                                            ]
                                        ]
                                ]
                            ]
                            Html.div [
                                prop.className "flex flex-wrap gap-2 justify-end"
                                prop.children [
                                    Html.button [
                                        prop.className
                                            "px-3 py-1.5 text-sm rounded-[var(--radius)] border border-border bg-transparent hover:bg-[var(--surface)]"
                                        prop.text msgs.RejectAll
                                        prop.onClick (fun _ -> dispatch RejectAll)
                                    ]
                                    Html.button [
                                        prop.className
                                            "px-3 py-1.5 text-sm rounded-[var(--radius)] border border-border bg-transparent hover:bg-[var(--surface)]"
                                        prop.text msgs.AcceptAll
                                        prop.onClick (fun _ -> dispatch AcceptAll)
                                    ]
                                    Html.button [
                                        prop.className "px-3 py-1.5 text-sm rounded-[var(--radius)] bg-brand text-white"
                                        prop.text msgs.SavePreferences
                                        prop.onClick (fun _ -> dispatch SavePreferences)
                                    ]
                                ]
                            ]
                        ]
                    ]
                ]
            ]

/// Curried helper for the common case (all managed categories).
let render () : ReactElement = ConsentBanner defaultManagedCategories
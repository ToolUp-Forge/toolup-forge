// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.ConsentGate

open System
open Feliz
open ToolUp.Platform
open ToolUp.Platform.Consent

// ─── Feliz consent-gate component ──────────────────────────────────
//
// Wraps any surface whose render must be conditioned on user consent
// (an ad slot, an embedded YouTube iframe, an analytics beacon).
// Children render only when ALL declared categories are granted in
// the current `ConsentState`. Otherwise an empty fragment ships.
//
// Usage:
//   ConsentGate.render
//       [ Analytics; Marketing ]
//       (Some emptyFallback)
//       (Html.div [ ... ad slot ... ])

[<ReactComponent>]
let ConsentGate (required: ConsentCategory list) (fallback: ReactElement option) (children: ReactElement) =
    let provider = ConsentProvider.current ()
    let state, setState = React.useState (ConsentState.initial)

    React.useEffectOnce (fun () ->
        let mutable cancelled = false

        async {
            let! initial = provider.GetCurrentState()

            if not cancelled then
                setState initial
        }
        |> Async.StartImmediate

        let subscription = provider.OnStateChanged setState

        FsReact.createDisposable (fun () ->
            cancelled <- true
            subscription.Dispose()))

    if ConsentState.hasAll required state then
        children
    else
        match fallback with
        | Some f -> f
        | None -> Html.none

/// Curried helper for the common case (no fallback).
let render (required: ConsentCategory list) (children: ReactElement) = ConsentGate required None children
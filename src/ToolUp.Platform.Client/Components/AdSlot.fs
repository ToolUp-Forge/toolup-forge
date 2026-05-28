// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.AdSlot

open System
open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform
open ToolUp.Platform.AdPanel

// ─── Phase 60 — Feliz `AdSlot` component ──────────────────────────
//
// Renders the standard AdSense `<ins class="adsbygoogle" …>` element,
// fires `adsbygoogle.push({})` on mount, and (when the impression
// observer is enabled) attaches a `MutationObserver` to the
// `data-ad-status` attribute AdSense sets to `filled` / `unfilled`.
// Wraps in [Phase 59](../Consent)'s `ConsentGate` against
// `AdPanelConfig.ConsentCategoriesRequired` — slot renders an empty
// fragment until the user grants the required categories.
//
// `AdPanelMode = NoAdPanel` (default `ClientConfig.AdPanel`) skips
// the render path entirely — `AdSlot` returns `Html.none` and the
// AdSense script never loads.

[<Emit("(window.adsbygoogle = window.adsbygoogle || []).push({})")>]
let private pushAd () : unit = jsNative

[<Emit("typeof MutationObserver !== 'undefined'")>]
let private hasMutationObserver () : bool = jsNative

[<Emit("new MutationObserver($0)")>]
let private createObserver (callback: obj -> obj -> unit) : obj = jsNative

[<Emit("$0.observe($1, { attributes: true, attributeFilter: ['data-ad-status'] })")>]
let private observeAttributes (observer: obj) (node: obj) : unit = jsNative

[<Emit("$0.disconnect()")>]
let private disconnect (observer: obj) : unit = jsNative

[<Emit("$0.getAttribute('data-ad-status')")>]
let private getAdStatus (node: obj) : string = jsNative

let private currentPath () =
    Browser.Dom.window.location.pathname + Browser.Dom.window.location.search

/// The client-side analytics sink. Consumers wanting first-party
/// impression / click telemetry replace via `AdAnalytics.setSink`
/// during client bootstrap (default `NoOpAdAnalyticsSink`). Module-
/// level singleton is intentional — the sink is a deployment-wide
/// concern, not per-component.
module AdAnalytics =
    let mutable private sink: IAdAnalyticsSink = NoOpAdAnalyticsSink() :> _

    let setSink (newSink: IAdAnalyticsSink) = sink <- newSink

    let current () = sink

[<ReactComponent>]
let AdSlot (config: ClientConfig) (slot: AdSlotConfig) : ReactElement =
    match config.AdPanel with
    | NoAdPanel -> Html.none
    | EnabledAdPanel panelConfig ->
        let insRef = React.useElementRef ()

        React.useEffectOnce (fun () ->
            AdScriptLoader.ensureLoaded slot.AdClientId
            pushAd ()

            match insRef.current, hasMutationObserver () with
            | Some node, true ->
                let observer =
                    createObserver (fun _mutations _obs ->
                        if getAdStatus (node :> obj) = "filled" then
                            let impression: AdImpression = {
                                SlotId = slot.SlotId
                                AdClientId = slot.AdClientId
                                OccurredAt = DateTimeOffset.UtcNow
                                PathAtImpression = currentPath ()
                            }

                            AdAnalytics.current().LogImpression impression |> Async.StartImmediate)

                observeAttributes observer (node :> obj)

                FsReact.createDisposable (fun () -> disconnect observer)
            | _ -> FsReact.createDisposable id)

        Components.ConsentGate.render
            panelConfig.ConsentCategoriesRequired
            (Html.ins [
                prop.className "adsbygoogle"
                prop.ref insRef
                prop.custom ("data-ad-client", slot.AdClientId)
                prop.custom ("data-ad-slot", slot.SlotId)
                prop.custom (
                    "data-ad-format",
                    match slot.Format with
                    | AdAuto -> "auto"
                    | AdRectangle -> "rectangle"
                    | AdVertical -> "vertical"
                    | AdHorizontal -> "horizontal"
                    | AdFluid _ -> "fluid"
                )
                match slot.Format with
                | AdFluid layoutKey -> prop.custom ("data-ad-layout-key", layoutKey)
                | _ -> ()
                match slot.Style with
                | Some s -> prop.style [ style.custom ("display", "block"); style.custom ("cssText", s.CssStyle) ]
                | None -> prop.style [ style.display.block ]
            ])
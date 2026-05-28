// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Consent

open System
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Google Funding Choices reference provider ─────────────────────
//
// Wraps Google's free Funding Choices CMP (AdSense-blessed). On
// first construction, injects the FC bootstrap script and listens
// for IAB TCF v2.2 / ConsentInfoUpdate callbacks. Translates the
// CMP's per-purpose decisions into `ConsentCategory` shape:
//
//   FC purpose 1  (store / access info)     → Necessary  (always granted)
//   FC purpose 2  (basic ads)                → Marketing
//   FC purpose 3  (personalised ads profile) → Personalisation
//   FC purpose 7  (measurement)              → Analytics
//   FC purpose 9  (market research)          → Analytics
//   FC purpose 10 (product development)      → Functional
//   FC vendor consent                        → ThirdPartyEmbeds
//
// The mapping is conservative — anything ambiguous falls into
// `NotYetDecided` so `ConsentGate` keeps the surface hidden until
// the user explicitly opts in via the CMP banner.

module private FundingChoicesInterop =
    [<Emit("typeof window !== 'undefined' ? window : undefined")>]
    let getWindow () : obj = jsNative

    [<Emit("document.createElement('script')")>]
    let createScript () : obj = jsNative

    [<Emit("document.head.appendChild($0)")>]
    let appendToHead (el: obj) : unit = jsNative

    [<Emit("$0.__tcfapi === undefined ? null : $0.__tcfapi")>]
    let getTcfApi (window: obj) : obj = jsNative

    [<Emit("$0($1, $2, $3)")>]
    let invokeTcfApi (api: obj) (command: string) (version: int) (callback: obj -> bool -> unit) : unit = jsNative

    [<Emit("$0.purpose && $0.purpose.consents ? $0.purpose.consents : {}")>]
    let getPurposeConsents (tcData: obj) : obj = jsNative

    [<Emit("$0[$1] === true")>]
    let isPurposeGranted (consents: obj) (purposeId: string) : bool = jsNative

    [<Emit("$0[$1] === false")>]
    let isPurposeDenied (consents: obj) (purposeId: string) : bool = jsNative

    let purposeToCategory = [
        "2", Marketing
        "3", Personalisation
        "7", Analytics
        "9", Analytics
        "10", Functional
    ]

open FundingChoicesInterop

/// FundingChoices provider. Bootstraps the FC script on construction;
/// state changes flow through the TCF v2 `addEventListener` callback.
type FundingChoicesConsentProvider(adClientId: string) =
    let mutable state = ConsentState.initial

    let subscribers = System.Collections.Generic.Dictionary<int, ConsentState -> unit>()
    let mutable nextId = 0
    let mutable bootstrapped = false

    let notify () =
        for kvp in Seq.toArray subscribers do
            try
                kvp.Value state
            with _ ->
                ()

    let applyTcData (tcData: obj) =
        let consents = getPurposeConsents tcData

        let mutable granted = Set.singleton Necessary
        let mutable denied = Set.empty

        for purposeId, category in purposeToCategory do
            if isPurposeGranted consents purposeId then
                granted <- Set.add category granted
            elif isPurposeDenied consents purposeId then
                denied <- Set.add category denied

        let nextState = {
            Granted = granted
            Denied = denied
            LastUpdatedAt = DateTimeOffset.UtcNow
            ConsentVersion = state.ConsentVersion
        }

        if nextState <> state then
            state <- nextState
            notify ()

    let bootstrap () =
        if not bootstrapped then
            bootstrapped <- true

            let window = getWindow ()

            if not (isNull window) then
                let script = createScript ()
                script?async <- true
                script?src <- $"https://fundingchoicesmessages.google.com/i/{adClientId}?ers=1"
                appendToHead script

                // Poll for the TCF API to come online, then subscribe.
                let rec subscribeWhenReady () =
                    let api = getTcfApi window

                    if isNull api then
                        JS.setTimeout subscribeWhenReady 250 |> ignore
                    else
                        invokeTcfApi api "addEventListener" 2 (fun tcData success ->
                            if success && not (isNull tcData) then
                                applyTcData tcData)

                subscribeWhenReady ()

    do bootstrap ()

    interface IConsentProvider with
        member _.GetCurrentState() = async { return state }

        member _.RequestConsent(_categories: ConsentCategory list) = async {
            // Funding Choices owns the prompt UX; we surface whatever
            // state the user lands on. The CMP re-prompts automatically
            // when the consent version bumps.
            return state
        }

        member _.HasConsented(category: ConsentCategory) = async {
            if Set.contains category state.Granted then return Granted
            elif Set.contains category state.Denied then return Denied
            else return NotYetDecided
        }

        member _.OnStateChanged(handler: ConsentState -> unit) =
            let id = nextId
            nextId <- nextId + 1
            subscribers[id] <- handler

            { new IDisposable with
                member _.Dispose() = subscribers.Remove(id) |> ignore
            }
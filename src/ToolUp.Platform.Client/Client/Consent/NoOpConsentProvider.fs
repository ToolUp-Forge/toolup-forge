// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Consent

open System
open ToolUp.Platform

// ─── Default no-op consent provider ────────────────────────────────
//
// Appropriate for deployments outside jurisdictions that mandate
// explicit consent (internal tools, US-only pre-CPRA scope), and
// the bootstrap state for any consumer wiring a real provider in a
// follow-up commit. `Necessary` is always granted (strictly-necessary
// session cookies + first-party auth); every other category stays
// `NotYetDecided` — meaning `ConsentGate`-wrapped surfaces stay
// hidden until a real provider lands.

type NoOpConsentProvider() =
    let mutable state = ConsentState.initial

    let subscribers = System.Collections.Generic.Dictionary<int, ConsentState -> unit>()
    let mutable nextId = 0

    let notify () =
        for kvp in Seq.toArray subscribers do
            try
                kvp.Value state
            with _ ->
                ()

    interface IConsentProvider with
        member _.GetCurrentState() = async { return state }

        member _.RequestConsent(_categories: ConsentCategory list) = async {
            // No prompt to show — return the current state unchanged.
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
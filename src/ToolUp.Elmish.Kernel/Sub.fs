// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish.Kernel

open System

/// The subscription diff: which subscriptions to stop, keep and start when
/// the model moves, and the step that applies it. A subscription is a key
/// (`string list`) and a start function; the start function is opaque here
/// — it is handed whatever dispatch the host passes and returns the handle
/// that stops it. `proofs/ElmishSub.fst` models `diff`, `NewSubs.calculate`
/// and the active-list half of `Fx.change` clause for clause, with the key
/// and the start function opaque.
module Subscriptions =

    /// Subscription keys.
    module SubId =
        /// The key as the reporter prints it.
        let toString (subId: string list) = String.Join("/", subId)

    /// Applying a diff: starting, stopping and reporting.
    module Fx =

        /// Report a key requested twice.
        let warnDupe onError subId =
            let ex = exn "Duplicate SubId"
            onError ("Duplicate SubId: " + SubId.toString subId, ex)

        /// Stop one subscription, reporting a stop that raises.
        let tryStop onError (subId, sub: IDisposable) =
            try
                sub.Dispose()
            with ex ->
                onError ("Error stopping subscription: " + SubId.toString subId, ex)

        /// Start one subscription, reporting a start that raises (it is then
        /// not held).
        let tryStart onError dispatch (subId, start) : (string list * IDisposable) option =
            try
                Some(subId, start dispatch)
            with ex ->
                onError ("Error starting subscription: " + SubId.toString subId, ex)
                None

        /// Stop every subscription in a list.
        let stop onError subs = subs |> List.iter (tryStop onError)

        /// Apply a diff: warn the duplicates, stop what is no longer
        /// asked for, start what is new, and return the next active
        /// list. `terminated` is the loop's flag, read before EACH
        /// start: a start function may terminate the program, and the
        /// teardown that runs then stops the active list as it stood
        /// BEFORE this diff and never runs again — so a start made after
        /// it would be held by nobody. Once the flag is set, nothing
        /// further is started, what this call had already started is
        /// stopped, and the next active list is empty: a terminated
        /// program holds nothing.
        let change onError dispatch (terminated: unit -> bool) (dupes, toStop, toKeep, toStart) =
            dupes |> List.iter (warnDupe onError)
            toStop |> List.iter (tryStop onError)

            let started =
                toStart
                |> List.choose (fun sub ->
                    if terminated () then
                        None
                    else
                        tryStart onError dispatch sub)

            if terminated () then
                stop onError started
                []
            else
                List.append toKeep started

    /// The requested list, folded into duplicates, keys and first occurrences.
    module NewSubs =

        /// The empty fold: no duplicates, no keys, no subscriptions.
        let (_dupes, _newKeys, _newSubs) as init = List.empty, Set.empty, List.empty

        /// Fold one requested subscription into (duplicates, keys, subscriptions).
        let update (subId, start) (dupes, newKeys, newSubs) =
            if Set.contains subId newKeys then
                subId :: dupes, newKeys, newSubs
            else
                dupes, Set.add subId newKeys, (subId, start) :: newSubs

        /// The requested list as (duplicates, keys, first-occurrence subscriptions).
        let calculate subs = List.foldBack update subs init

    /// No active subscriptions.
    let empty = List.empty<string list * IDisposable>

    /// The diff between the active list and the requested one, as
    /// (duplicates, to stop, to keep, to start). When the key sets agree
    /// the active list is kept whole and nothing starts or stops.
    let diff (activeSubs: (string list * IDisposable) list) (sub: (string list * 'start) list) =
        let keys = activeSubs |> List.map fst |> Set.ofList
        let dupes, newKeys, newSubs = NewSubs.calculate sub

        if keys = newKeys then
            dupes, [], activeSubs, []
        else
            let toKeep, toStop =
                activeSubs |> List.partition (fun (k, _) -> Set.contains k newKeys)

            let toStart = newSubs |> List.filter (fun (k, _) -> not (Set.contains k keys))

            dupes, toStop, toKeep, toStart
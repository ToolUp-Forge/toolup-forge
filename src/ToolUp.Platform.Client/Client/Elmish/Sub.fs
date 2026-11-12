// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

open System
open ToolUp.Elmish.Kernel

/// SubId — Subscription ID, alias for `string list`.
type SubId = string list

/// Subscribe — starts a subscription; returns an `IDisposable` to stop it.
type Subscribe<'msg> = Dispatch<'msg> -> IDisposable

/// Subscription — list of `(SubId, Subscribe)` pairs. Diffed by the runtime
/// across model transitions: subs whose `SubId` disappears are stopped,
/// new ones are started.
///
/// Preserved unchanged from upstream Elmish v5.x for `Program.withSubscription`
/// compatibility. The simpler one-shot pattern that most ToolUp consumers
/// actually use is `Effect<'msg>` + `Program.withEffect` (see `Effect.fs`);
/// `Sub` is here for consumers who need true diff-on-model-change semantics.
type Sub<'msg> = (SubId * Subscribe<'msg>) list

module Sub =

    /// No subscriptions, equivalent to `[]`.
    let none: Sub<'msg> = []

    /// Aggregate multiple subscriptions.
    let batch (subs: #seq<Sub<'msg>>) : Sub<'msg> = subs |> List.concat

    /// Map subscription messages to another type. Prefix the `SubId` to
    /// avoid ID conflicts with other components.
    let map (idPrefix: string) (f: 'a -> 'msg) (sub: Sub<'a>) : Sub<'msg> =
        sub
        |> List.map (fun (subId, subscribe) -> idPrefix :: subId, (fun dispatch -> subscribe (f >> dispatch)))

    /// The subscription diff and the step that applies it, as the runtime
    /// runs them: each name here is the kernel's
    /// (`ToolUp.Elmish.Kernel.Subscriptions`, proved against
    /// `proofs/ElmishSub.fst`), kept under its upstream path.
    module Internal =

        module SubId =
            let toString (subId: SubId) = Subscriptions.SubId.toString subId

        module Fx =

            let warnDupe onError subId = Subscriptions.Fx.warnDupe onError subId

            let tryStop onError (subId, sub: IDisposable) =
                Subscriptions.Fx.tryStop onError (subId, sub)

            let tryStart onError dispatch (subId, start) : (SubId * IDisposable) option =
                Subscriptions.Fx.tryStart onError dispatch (subId, start)

            let stop onError subs = Subscriptions.Fx.stop onError subs

            /// Apply a diff: warn the duplicates, stop what is no longer
            /// asked for, start what is new, and return the next active
            /// list, reading `terminated` before each start; once it is set
            /// nothing further starts and the next active list is empty.
            let change onError dispatch (terminated: unit -> bool) (dupes, toStop, toKeep, toStart) =
                Subscriptions.Fx.change onError dispatch terminated (dupes, toStop, toKeep, toStart)

        module NewSubs =

            let (_dupes, _newKeys, _newSubs) as init = Subscriptions.NewSubs.init

            let update (subId, start) (dupes, newKeys, newSubs) =
                Subscriptions.NewSubs.update (subId, start) (dupes, newKeys, newSubs)

            let calculate subs = Subscriptions.NewSubs.calculate subs

        let empty = Subscriptions.empty

        let diff (activeSubs: (SubId * IDisposable) list) (sub: Sub<'msg>) = Subscriptions.diff activeSubs sub
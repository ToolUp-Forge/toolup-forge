namespace Elmish

open System

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

    module Internal =

        module SubId =
            let toString (subId: SubId) = String.Join("/", subId)

        module Fx =

            let warnDupe onError subId =
                let ex = exn "Duplicate SubId"
                onError ("Duplicate SubId: " + SubId.toString subId, ex)

            let tryStop onError (subId, sub: IDisposable) =
                try
                    sub.Dispose()
                with ex ->
                    onError ("Error stopping subscription: " + SubId.toString subId, ex)

            let tryStart onError dispatch (subId, start) : (SubId * IDisposable) option =
                try
                    Some(subId, start dispatch)
                with ex ->
                    onError ("Error starting subscription: " + SubId.toString subId, ex)
                    None

            let stop onError subs = subs |> List.iter (tryStop onError)

            let change onError dispatch (dupes, toStop, toKeep, toStart) =
                dupes |> List.iter (warnDupe onError)
                toStop |> List.iter (tryStop onError)
                let started = toStart |> List.choose (tryStart onError dispatch)
                List.append toKeep started

        module NewSubs =

            let (_dupes, _newKeys, _newSubs) as init = List.empty, Set.empty, List.empty

            let update (subId, start) (dupes, newKeys, newSubs) =
                if Set.contains subId newKeys then
                    subId :: dupes, newKeys, newSubs
                else
                    dupes, Set.add subId newKeys, (subId, start) :: newSubs

            let calculate subs = List.foldBack update subs init

        let empty = List.empty<SubId * IDisposable>

        let diff (activeSubs: (SubId * IDisposable) list) (sub: Sub<'msg>) =
            let keys = activeSubs |> List.map fst |> Set.ofList
            let dupes, newKeys, newSubs = NewSubs.calculate sub

            if keys = newKeys then
                dupes, [], activeSubs, []
            else
                let toKeep, toStop =
                    activeSubs |> List.partition (fun (k, _) -> Set.contains k newKeys)

                let toStart = newSubs |> List.filter (fun (k, _) -> not (Set.contains k keys))

                dupes, toStop, toKeep, toStart
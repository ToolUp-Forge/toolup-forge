// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Experiments.Server

open System.Collections.Concurrent
open ToolUp.Experiments

/// Dev exposure sink that discards records. The default when a
/// deployment has not wired exposure logging.
type NoOpExposureSink() =
    interface IExposureSink with
        member _.Record(_scopeId, _exposure) = async { return () }

/// Dev / test exposure sink that collects records in memory for
/// inspection. NOT for production (unbounded growth).
type CollectingExposureSink() =
    let recorded = ConcurrentQueue<string * ExposureEvent>()
    member _.Recorded: (string * ExposureEvent) list = recorded |> List.ofSeq

    interface IExposureSink with
        member _.Record(scopeId, exposure) = async { recorded.Enqueue(scopeId, exposure) }

/// Assigns principals to variants and logs an exposure the FIRST time a
/// `(scope, experiment, principal)` triple is observed (de-duped so
/// repeated reads don't re-log). Assignment is deterministic + pure
/// (`Assignment.assign`); only `Running` experiments assign — `Draft` /
/// `Stopped` yield `None` without an exposure. The exposure-dedup set is
/// in-process (single-instance); a distributed deployment supplies a
/// durable sink + dedup.
type ExperimentService(store: IExperimentStore, sink: IExposureSink) =
    let exposed = ConcurrentDictionary<string, byte>()

    /// Resolve and assign the variant for a principal in a scope,
    /// logging a one-time exposure. `None` when the experiment is
    /// missing, not `Running`, or has no assignable variants.
    member _.Assign(scopeId: string, experimentId: string, principalId: string) : Async<Variant option> = async {
        let! experiment = store.Get(scopeId, experimentId)

        match experiment with
        | Some e when e.Status = Running ->
            match Assignment.assign e principalId with
            | None -> return None
            | Some variant ->
                let key = sprintf "%s:%s:%s" scopeId experimentId principalId

                if exposed.TryAdd(key, 0uy) then
                    do!
                        sink.Record(
                            scopeId,
                            {
                                ExperimentId = experimentId
                                PrincipalId = principalId
                                VariantKey = variant.Key
                            }
                        )

                return Some variant
        | _ -> return None
    }
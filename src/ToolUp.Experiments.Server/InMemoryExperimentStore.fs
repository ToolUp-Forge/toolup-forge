// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Experiments.Server

open System.Collections.Concurrent
open ToolUp.Experiments

/// Dev / single-instance `IExperimentStore`. Holds definitions in
/// process memory — correct for a single node, NOT durable across
/// restarts. A multi-instance deployment supplies a durable
/// implementation (e.g. over `IBlobStorage`) with no change to
/// consuming code. Per-scope shard map (GP 4).
type InMemoryExperimentStore() =
    let scopes =
        ConcurrentDictionary<string, ConcurrentDictionary<string, Experiment>>()

    let scopeMap (scopeId: string) =
        scopes.GetOrAdd(scopeId, fun _ -> ConcurrentDictionary<string, Experiment>())

    interface IExperimentStore with
        member _.Get(scopeId, experimentId) = async {
            match scopes.TryGetValue scopeId with
            | true, m ->
                match m.TryGetValue experimentId with
                | true, e -> return Some e
                | _ -> return None
            | _ -> return None
        }

        member _.List(scopeId) = async {
            match scopes.TryGetValue scopeId with
            | true, m -> return m.Values |> List.ofSeq
            | _ -> return []
        }

        member _.Set(scopeId, experiment) = async {
            (scopeMap scopeId)[experiment.Id] <- experiment
            return Ok()
        }

        member _.Remove(scopeId, experimentId) = async {
            match scopes.TryGetValue scopeId with
            | true, m -> m.TryRemove experimentId |> ignore
            | _ -> ()
        }
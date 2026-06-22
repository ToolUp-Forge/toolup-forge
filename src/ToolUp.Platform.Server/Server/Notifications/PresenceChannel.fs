// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent

/// Dev / single-instance `IPresenceChannel`. Holds rosters in process
/// memory — correct for a single node, NOT shared across replicas. A
/// multi-instance deployment supplies a distributed implementation
/// (over the distributed `INotificationChannel` companion) with no
/// change to consuming code.
///
/// Liveness is heartbeat-windowed: an entry is `Online` while its last
/// heartbeat is within `awayThreshold`, `Away` between `awayThreshold`
/// and `expiry`, and excluded from the roster entirely once older than
/// `expiry`. `now` is injectable for deterministic tests.
type InMemoryPresenceChannel(?awayThreshold: TimeSpan, ?expiry: TimeSpan, ?now: unit -> DateTime) =
    let awayThreshold = defaultArg awayThreshold (TimeSpan.FromSeconds 30.0)
    let expiry = defaultArg expiry (TimeSpan.FromSeconds 90.0)
    let clock = defaultArg now (fun () -> DateTime.UtcNow)

    // scopeId -> (principalId -> entry). Per-scope shard; a roster read
    // touches only the requested scope's inner map (GP 4).
    let scopes =
        ConcurrentDictionary<string, ConcurrentDictionary<string, PresenceEntry>>()

    let scopeMap (scopeId: string) =
        scopes.GetOrAdd(scopeId, fun _ -> ConcurrentDictionary<string, PresenceEntry>())

    let upsert (scopeId: string) (principalId: string) (displayName: string option) =
        let m = scopeMap scopeId

        let entry = {
            PrincipalId = principalId
            DisplayName = displayName
            LastSeen = clock ()
            Status = Online
        }

        m.AddOrUpdate(
            principalId,
            entry,
            fun _ existing -> {
                entry with
                    DisplayName = displayName |> Option.orElse existing.DisplayName
            }
        )
        |> ignore

    interface IPresenceChannel with
        member _.Join(scopeId, principalId, displayName) = async { upsert scopeId principalId displayName }

        member _.Heartbeat(scopeId, principalId) = async { upsert scopeId principalId None }

        member _.Leave(scopeId, principalId) = async {
            match scopes.TryGetValue scopeId with
            | true, m -> m.TryRemove principalId |> ignore
            | _ -> ()
        }

        member _.Roster(scopeId) = async {
            match scopes.TryGetValue scopeId with
            | false, _ -> return []
            | true, m ->
                let nowTime = clock ()

                return
                    m.Values
                    |> Seq.choose (fun e ->
                        let age = nowTime - e.LastSeen

                        if age > expiry then None
                        elif age > awayThreshold then Some { e with Status = Away }
                        else Some { e with Status = Online })
                    |> Seq.sortBy _.PrincipalId
                    |> List.ofSeq
        }
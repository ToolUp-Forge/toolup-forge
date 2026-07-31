// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── IPresenceTracker — Phase 442 ────────────────────────────────────
//
// Who's-here presence with a *location* descriptor, one layer above the
// Phase 241 `IPresenceChannel` roster. A tracker answers "who else is in
// this scope, and which module / page are they on" and fans out
// join / move / leave events on the reserved `_platform.presence`
// notification key so a view reacts live rather than polling.
//
// Scope isolation (GP 4): a roster read returns ONLY the calling scope's
// peers, and every event is published on the caller's *own* `scopeId` —
// so the channel's structural scope-gating keeps presence inside one
// team with no post-hoc filter. The `scopeId` is always resolved from
// the caller's authenticated request, never accepted from an untrusted
// source (the same trust boundary as `INotificationChannel`).
//
// Portability (GP 12): identity by value (scopeId / userId strings,
// PresencePeer record), async at every boundary, stateless between calls
// (a distributed impl may serve successive calls from different nodes),
// per-scope sharding with no cross-scope ordering claim, expiry
// precision declared by the implementation's heartbeat window.
//
// Composition: `ServerConfig.Presence = EnabledPresence` registers the
// in-memory default into DI (see `ComposeNotifications`); `NoPresence`
// (the default) registers nothing.
//
// **This is the substrate the platform builds on** — Phase 622.A decided
// that against Phase 241's `IPresenceChannel`, and the reasoning lives at
// the shared seam (`Shared/PresenceTypes.fs`, "THE TWO-SUBSTRATE
// DECISION"). Read it before adding a third presence family.
//
// Phase 622 also mounts a batteries-included `IPresenceApi` over this
// tracker at `/api/IPresenceApi/*` under the same flag, and the shell
// auto-mounts the client half under `ClientConfig.Presence`. That is
// purely additive: the pre-622 contract — SDK registers the substrate, a
// deployment exposes its own module-owned API over the resolved tracker
// and mounts `PresenceContext.provider` itself — remains supported, which
// is why the client hooks stay transport-parameterised.

/// Scope-isolated presence tracker with per-peer location. `Join`
/// announces arrival at a location; `Heartbeat` keeps a peer live
/// without moving it; `Move` changes a peer's location; `Leave` removes
/// them; `Roster` reads the current (non-expired) peers of one scope.
type IPresenceTracker =
    /// Announce a principal present in a scope at `location` (idempotent
    /// — a re-`Join` refreshes liveness and re-announces). Also the
    /// reconnect-re-acquire path (Phase 24): a client whose entry
    /// expired while offline calls `Join` again on reconnect.
    abstract Join:
        scopeId: string * userId: string * displayName: string option * location: PresenceLocation -> Async<unit>

    /// Refresh a principal's liveness at its current location. A no-op
    /// (no event) when the principal is not currently present — the
    /// client re-`Join`s to re-appear.
    abstract Heartbeat: scopeId: string * userId: string -> Async<unit>
    /// Move a present principal to a new location (upserts + announces
    /// `Moved`).
    abstract Move: scopeId: string * userId: string * location: PresenceLocation -> Async<unit>
    /// Remove a principal from a scope's roster (announces `Left`).
    abstract Leave: scopeId: string * userId: string -> Async<unit>
    /// The current non-expired roster for a scope. Scope-isolated:
    /// returns only this scope's peers, sorted by `UserId` for stable
    /// rendering.
    abstract Roster: scopeId: string -> Async<PresencePeer list>

/// Dev / single-instance `IPresenceTracker`. Holds rosters in process
/// memory — correct for a single node, NOT shared across replicas. A
/// multi-instance deployment supplies a distributed implementation (over
/// the distributed `INotificationChannel` companion) with no change to
/// consuming code; the in-memory tracker is flagged single-instance to
/// the Phase 9c distributed-companion family.
///
/// A peer is dropped from the roster once its last heartbeat / move is
/// older than `expiry`. `now` is injectable for deterministic tests.
/// Events are published best-effort on the caller's own `scopeId`; a
/// publish failure never fails the roster mutation (awareness is
/// advisory).
type InMemoryPresenceTracker(channel: INotificationChannel, ?expiry: TimeSpan, ?now: unit -> DateTime) =
    let expiry = defaultArg expiry (TimeSpan.FromSeconds 90.0)
    let clock = defaultArg now (fun () -> DateTime.UtcNow)
    let jsonOptions = FableConverters.create ()

    // scopeId -> (userId -> peer). Per-scope shard; a roster read touches
    // only the requested scope's inner map (GP 4).
    let scopes =
        ConcurrentDictionary<string, ConcurrentDictionary<string, PresencePeer>>()

    let scopeMap (scopeId: string) =
        scopes.GetOrAdd(scopeId, fun _ -> ConcurrentDictionary<string, PresencePeer>())

    let publish (scopeId: string) (change: PresenceChange) (peer: PresencePeer) = async {
        try
            let event: PresenceEvent = { Change = change; Peer = peer }
            let payloadJson = JsonSerializer.Serialize(event, jsonOptions)
            do! channel.Publish(scopeId, CustomNotification(CollaborationTopics.Presence, payloadJson))
        with _ ->
            // Best-effort fan-out — a channel failure must not fail the
            // roster mutation the caller already observed.
            ()
    }

    interface IPresenceTracker with
        member _.Join(scopeId, userId, displayName, location) = async {
            let peer = {
                UserId = userId
                DisplayName = displayName
                Location = location
                LastSeen = clock ()
            }

            (scopeMap scopeId)[userId] <- peer
            do! publish scopeId PresenceChange.Joined peer
        }

        member _.Heartbeat(scopeId, userId) = async {
            match scopes.TryGetValue scopeId with
            | true, m ->
                match m.TryGetValue userId with
                | true, existing -> m[userId] <- { existing with LastSeen = clock () }
                | _ -> () // not present — client re-Joins to re-appear
            | _ -> ()
        }

        member _.Move(scopeId, userId, location) = async {
            let m = scopeMap scopeId

            let displayName =
                match m.TryGetValue userId with
                | true, existing -> existing.DisplayName
                | _ -> None

            let peer = {
                UserId = userId
                DisplayName = displayName
                Location = location
                LastSeen = clock ()
            }

            m[userId] <- peer
            do! publish scopeId PresenceChange.Moved peer
        }

        member _.Leave(scopeId, userId) = async {
            match scopes.TryGetValue scopeId with
            | true, m ->
                match m.TryRemove userId with
                | true, peer -> do! publish scopeId PresenceChange.Left peer
                | _ -> ()
            | _ -> ()
        }

        member _.Roster(scopeId) = async {
            match scopes.TryGetValue scopeId with
            | false, _ -> return []
            | true, m ->
                let nowTime = clock ()

                return
                    m.Values
                    |> Seq.filter (fun p -> nowTime - p.LastSeen <= expiry)
                    |> Seq.sortBy _.UserId
                    |> List.ofSeq
        }
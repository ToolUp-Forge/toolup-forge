// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent

// ─── Phase 112 — scope-isolated server-driven live-session host ───────
//
// A server-driven UI protocol (the server holds the authoritative UI
// tree, computes diffs, pushes patches to a thin generic client shim —
// no per-app Fable compile) needs somewhere to live inside a
// `ServerApp` composition. A raw host gets none of forge's multi-tenant
// isolation, rate limiting, or security middleware for free; this
// substrate supplies the generic, transport-neutral hosting layer any
// such protocol (not only one vendor's) binds to.
//
// **Scope isolation is structural (GP 4).** Sessions are stored
// partitioned by `StorageScope.ScopeId` — every host operation takes
// the caller's scope id first and resolves only within that partition,
// so a frame addressed to scope A can never reach a subscriber whose
// session lives in scope B; there is no "remember to filter" check to
// forget.
//
// **Six portability rules (GP 12):**
//   1. Identity by value — sessions are addressed by `(scopeId,
//      sessionId)` strings and subscriptions by `Guid`; no live
//      handles cross the interface.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry / supervision as data — the open-refusal is a record
//      (`LiveSessionRefusal`); no callback-shaped error channels.
//   4. Stateless handlers — a frame subscriber receives the full
//      payload per invocation; the host holds the registry, the
//      handler holds nothing between frames (an Orleans grain can
//      deactivate / an Akka actor restart between frames).
//   5. Ordering guaranteed only within one `SessionId` — frames pushed
//      to a single session's channel arrive in push order for an
//      in-process subscriber; NO cross-session ordering is promised.
//   6. Precision — N/A (no scheduling primitive on this interface).
//
// **`ILiveSessionStore` deliberately omitted.** The motivating diff
// hosts are connection-lived: the authoritative tree lives with the
// open session and a reconnect re-bootstraps from the server's source
// of truth, so persisting session-tree snapshots across reconnects
// buys nothing today. If a protocol needs resumable sessions, an
// `ILiveSessionStore` (identity-by-value snapshot, async, stateless —
// mirroring the `InMemoryRenderCache` / blob-backed split) slots in
// additively without touching these interfaces.

/// Value descriptor of an open live session.
type LiveSessionDescriptor = {
    SessionId: string
    ScopeId: string
    OpenedAt: DateTimeOffset
}

/// Why `OpenSession` refused — the scope is at its configured
/// concurrent-session cap. Mirrors the SSE per-scope-cap refusal
/// shape (Phase 6l.D): a record, not an exception, so callers
/// surface a clean 429.
type LiveSessionRefusal = {
    ScopeId: string
    Cap: int
    CurrentCount: int
}

/// Push side of one live session: delivers opaque frames (the
/// protocol's own wire format — e.g. a JSON tree-diff patch) to every
/// subscriber of the session. Transport-neutral: the shipped consumer
/// is the SSE endpoint in `LiveSessionsCompose`; a WebSocket transport
/// is an additive companion behind the same interface.
type ILiveChannel =
    /// Push one frame to every current subscriber. Failures writing to
    /// an individual subscriber are contained (the subscriber is the
    /// transport's concern to evict); the push itself never throws.
    abstract PushFrame: payload: string -> Async<unit>

/// Scope-partitioned live-session host. All operations resolve within
/// the caller's scope partition only — `TryGetChannel` / `Subscribe`
/// against another scope's session id return `None` structurally.
type ILiveSessionHost =
    /// Open a session in `scope`. Refuses (as data) when the scope is
    /// at its concurrent-session cap.
    abstract OpenSession: scope: StorageScope -> Async<Result<LiveSessionDescriptor, LiveSessionRefusal>>

    /// Resolve the push channel for a session WITHIN `scopeId`.
    /// `None` when the session does not exist in that scope —
    /// including when the session id exists in a different scope
    /// (the structural cross-scope denial).
    abstract TryGetChannel: scopeId: string * sessionId: string -> Async<ILiveChannel option>

    /// Subscribe a frame handler to a session within `scopeId`.
    /// `None` when the session does not exist in that scope.
    abstract Subscribe: scopeId: string * sessionId: string * handler: (string -> unit) -> Async<Guid option>

    /// Remove a subscription. Idempotent.
    abstract Unsubscribe: scopeId: string * sessionId: string * subscriptionId: Guid -> Async<unit>

    /// Close a session and evict its subscribers. Idempotent.
    abstract CloseSession: scopeId: string * sessionId: string -> Async<unit>

    /// Open sessions in `scopeId` (diagnostics / cap accounting).
    abstract ListSessions: scopeId: string -> Async<LiveSessionDescriptor list>

/// In-process, single-instance default. **Dev / single-node only** in
/// the same sense as `InMemoryNotificationChannel`: state is
/// per-process, so a multi-node deployment needs a distributed
/// implementation behind the same interface (validated by the
/// `ILiveSessionHostContract` pack).
type InMemoryLiveSessionHost(?maxSessionsPerScope: int) =

    let scopes =
        ConcurrentDictionary<
            string,
            ConcurrentDictionary<string, DateTimeOffset * ConcurrentDictionary<Guid, string -> unit>>
         >()

    let channelFor (subscribers: ConcurrentDictionary<Guid, string -> unit>) : ILiveChannel =
        { new ILiveChannel with
            member _.PushFrame payload = async {
                for KeyValue(_, handler) in subscribers do
                    try
                        handler payload
                    with _ ->
                        ()
            }
        }

    interface ILiveSessionHost with
        member _.OpenSession scope = async {
            let sessions = scopes.GetOrAdd(scope.ScopeId, fun _ -> ConcurrentDictionary())

            match maxSessionsPerScope with
            | Some cap when sessions.Count >= cap ->
                return
                    Error {
                        ScopeId = scope.ScopeId
                        Cap = cap
                        CurrentCount = sessions.Count
                    }
            | _ ->
                let descriptor = {
                    SessionId = Guid.NewGuid().ToString("N")
                    ScopeId = scope.ScopeId
                    OpenedAt = DateTimeOffset.UtcNow
                }

                sessions[descriptor.SessionId] <- (descriptor.OpenedAt, ConcurrentDictionary())
                return Ok descriptor
        }

        member _.TryGetChannel(scopeId, sessionId) = async {
            match scopes.TryGetValue scopeId with
            | true, sessions ->
                match sessions.TryGetValue sessionId with
                | true, (_, subscribers) -> return Some(channelFor subscribers)
                | _ -> return None
            | _ -> return None
        }

        member _.Subscribe(scopeId, sessionId, handler) = async {
            match scopes.TryGetValue scopeId with
            | true, sessions ->
                match sessions.TryGetValue sessionId with
                | true, (_, subscribers) ->
                    let id = Guid.NewGuid()
                    subscribers[id] <- handler
                    return Some id
                | _ -> return None
            | _ -> return None
        }

        member _.Unsubscribe(scopeId, sessionId, subscriptionId) = async {
            match scopes.TryGetValue scopeId with
            | true, sessions ->
                match sessions.TryGetValue sessionId with
                | true, (_, subscribers) -> subscribers.TryRemove subscriptionId |> ignore
                | _ -> ()
            | _ -> ()
        }

        member _.CloseSession(scopeId, sessionId) = async {
            match scopes.TryGetValue scopeId with
            | true, sessions -> sessions.TryRemove sessionId |> ignore
            | _ -> ()
        }

        member _.ListSessions scopeId = async {
            match scopes.TryGetValue scopeId with
            | true, sessions ->
                return
                    sessions
                    |> Seq.map (fun (KeyValue(sessionId, (openedAt, _))) -> {
                        SessionId = sessionId
                        ScopeId = scopeId
                        OpenedAt = openedAt
                    })
                    |> List.ofSeq
            | _ -> return []
        }

module LiveSessionHost =
    /// The in-process default. `maxSessionsPerScope = None` leaves the
    /// per-scope concurrent-session count uncapped (rate limiting at
    /// the endpoint still applies via `IRateLimitStore`).
    let createInMemory (maxSessionsPerScope: int option) : ILiveSessionHost =
        match maxSessionsPerScope with
        | Some cap -> InMemoryLiveSessionHost(cap) :> ILiveSessionHost
        | None -> InMemoryLiveSessionHost() :> ILiveSessionHost
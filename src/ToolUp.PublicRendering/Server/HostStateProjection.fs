// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open ToolUp.Platform

// ─── Phase 264 — host-state projection (server / SSR) ──────────────────
//
// The SSR counterpart to the client `IHostStateProjection`. Where the
// client projects an Elmish `Model`, the server projects the
// authoritative live-session state held by the Phase 112
// `ILiveSessionHost` into the SAME neutral `HostBindingSources` namespace
// (`ToolUp.Platform.Core`), so a hosted tree resolves its data bindings
// identically on the server-rendered path and the client path.
//
// **Scope-bound by construction (GP 4).** A `ServerHostStateProjection`
// captures exactly ONE `StorageScope` at build time. Every `Project()`
// resolves only within that scope's partition
// (`ILiveSessionHost.ListSessions scope.ScopeId`) — the projection holds
// no handle to any other scope, so there is nothing to "remember to
// filter": a projection built for scope A is structurally incapable of
// reading scope B's sessions. Isolation is carried by the seam shape, not
// by a runtime check. This mirrors the host's own structural cross-scope
// denial (Phase 112): a session id from another scope resolves to `None`
// because the partition lookup fails, not because a guard rejected it.

/// A scope-bound projection of authoritative server session state into
/// the neutral `HostBindingSources` namespace. Async because the
/// underlying live-session host is async at every boundary (GP 12).
type ServerHostStateProjection =
    /// Project the captured scope's authoritative session state.
    abstract Project: unit -> Async<HostBindingSources>

[<RequireQualifiedAccess>]
module ServerHostStateProjection =

    /// The default neutral projector: the scope's open-session
    /// descriptors, the scope id, and the open-session count, keyed under
    /// `QueryResults`. Pure — a host may supply its own projector to
    /// `forScope` to surface richer authoritative state.
    let defaultProjector (scope: StorageScope) (sessions: LiveSessionDescriptor list) : HostBindingSources =
        HostBindingSources.ofQueryResults (
            Map.ofList [
                "scopeId", box scope.ScopeId
                "openSessions", box sessions
                "openSessionCount", box (List.length sessions)
            ]
        )

    /// Bind a projection to ONE scope against a live-session host. The
    /// returned projection captures `scope` and `host`; every `Project()`
    /// lists ONLY `scope.ScopeId`'s sessions and hands them to
    /// `projector`. There is no API by which it can reach another scope's
    /// partition (GP 4 — structural isolation).
    let forScope
        (host: ILiveSessionHost)
        (projector: StorageScope -> LiveSessionDescriptor list -> HostBindingSources)
        (scope: StorageScope)
        : ServerHostStateProjection =
        { new ServerHostStateProjection with
            member _.Project() = async {
                let! sessions = host.ListSessions scope.ScopeId
                return projector scope sessions
            }
        }

    /// `forScope` with `defaultProjector` — the common case.
    let forScopeDefault (host: ILiveSessionHost) (scope: StorageScope) : ServerHostStateProjection =
        forScope host defaultProjector scope
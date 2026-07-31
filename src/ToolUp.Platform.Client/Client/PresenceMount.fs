// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PresenceMount

open Feliz
open Fable.SimpleJson
open ToolUp.Platform

// ─── Shell auto-mount for presence + soft-locks — Phase 622.C ────────
//
// The batteries-included client half. Phase 442 shipped
// `PresenceContext.provider` and left the DEPLOYMENT to mount it, keep it
// fresh, and run a heartbeat; Phase 241 shipped the heartbeat pump but
// over its own (narrower) roster type. This module joins the two so a
// deployment gets a live "who's here / what's locked" context by setting
// a config flag and nothing else.
//
// **What it reuses rather than reinvents:**
//   * the pump is Phase 241's `PresenceClient.startPump` — the same timer
//     `PresenceClient.start` runs on, generalised over what a beat
//     returns so both callers share one implementation;
//   * the context is Phase 442's `PresenceContext.provider`, unchanged —
//     module views read it through the hooks that already shipped;
//   * the transport is the Phase 622 `IPresenceApi`, over the Phase 442
//     substrate (see the decision record in `Shared/PresenceTypes.fs`).
//
// **Opt-in, and it pays nothing when off (GP 13 / GP 11).** The shell
// mounts this only under `ClientConfig.Presence = EnabledPresence`, whose
// default is `NoPresence`. That is a SEPARATE flag from the server's
// `ServerConfig.Presence` on purpose: an existing deployment that already
// enabled the server substrate and hand-mounts its own client wiring must
// not silently acquire a second heartbeat in every browser tab because it
// upgraded the SDK.
//
// **Failure posture: silent.** Presence is awareness, not function. Every
// call below is wrapped so a failed beat neither blanks the roster nor
// raises — a deployment must not show an error banner because a
// who's-here poll lost a round trip.

/// Proxy over the platform presence API. Built once at module load, like
/// `AdminHome`'s `homeApi` — construction is closures only, no network,
/// so a deployment that never mounts the component pays nothing.
let private presenceApi: IPresenceApi =
    Api.makeProxy<IPresenceApi> (
        routeBuilder = PresenceApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

/// Announce presence at `location` and return the fresh roster. `None`
/// on any failure — the caller leaves the previous roster in place rather
/// than blanking it, so a single dropped request does not make the whole
/// team appear to vanish.
let private beat (location: PresenceLocation) : Async<PresencePeer list option> = async {
    try
        let! roster = presenceApi.Heartbeat location
        return Some roster
    with _ ->
        return None
}

/// Re-read the authoritative roster. Used as the response to a
/// `_platform.presence` event — see the fold note in the component.
let private reread () : Async<PresencePeer list option> = async {
    try
        let! roster = presenceApi.Roster()
        return Some roster
    with _ ->
        return None
}

/// Fold one `_platform.lock` event into the lease map.
let private applyLockEvent (event: LockEvent) (locks: Map<string, LockLease>) : Map<string, LockLease> =
    let key = EntityLockRef.toKey event.Lease.Ref

    match event.Change with
    | LockChange.Taken -> Map.add key event.Lease locks
    | LockChange.Released
    | LockChange.Expired -> Map.remove key locks

/// Owns the heartbeat pump, the SSE subscription and the context value.
/// `moduleId` is the shell's active module — it becomes the peer's
/// location descriptor, so other members see *where* each collaborator is
/// rather than merely that they are online.
[<ReactComponent>]
let private PresenceRoot (moduleId: string) (children: ReactElement) =
    let peers, setPeers = React.useState ([]: PresencePeer list)
    let locks, setLocks = React.useStateWithUpdater (Map.empty: Map<string, LockLease>)

    // The pump, keyed on the active module. A navigation tears down the
    // interval and starts a new one, whose immediate first beat carries
    // the new location — so a move shows up at once instead of at the end
    // of the current heartbeat window.
    //
    // Note the no-op `leave`: departure is owned by the unmount effect
    // below. Folding it in here would announce Left-then-Joined on every
    // single module change, which is fan-out noise for a peer that never
    // actually went anywhere.
    React.useEffect (
        (fun () ->
            let dispose =
                PresenceClient.startPump
                    (fun () -> beat (PresenceLocation.ofModule moduleId))
                    (fun () -> async { return () })
                    PresenceApi.heartbeatIntervalMs
                    (function
                     | Some roster -> setPeers roster
                     | None -> ())

            FsReact.createDisposable dispose),
        [| box moduleId |]
    )

    // Depart once, on real unmount only.
    React.useEffectOnce (fun () ->
        FsReact.createDisposable (fun () ->
            Async.StartImmediate(
                async {
                    try
                        do! presenceApi.Leave()
                    with _ ->
                        ()
                }
            )))

    // Live updates off the reserved `_platform.*` keys the Phase 442
    // substrate already publishes, through the Phase 6a SSE bridge.
    //
    // The two keys are handled deliberately differently:
    //
    //   * **Presence — signal, not payload.** The event is treated as
    //     "the roster changed" and answered with a re-read. The server's
    //     roster is the only thing that knows which peers have aged out
    //     of the heartbeat window, so folding join/move/leave events
    //     client-side would drift: a peer that stopped beating emits no
    //     departure event at all, and a client fold would show them
    //     present indefinitely. Re-reading cannot drift.
    //   * **Locks — payload.** There is no "list the locks in this scope"
    //     operation on `IEntityLockStore`, so the fan-out is the only
    //     source for a lease taken by somebody else, and the payload has
    //     to be parsed. A parse failure skips that event and leaves the
    //     lease map alone, which degrades to exactly the documented
    //     no-provider behaviour ("nothing locked") rather than to a
    //     wrong answer. The holder's own view is unaffected either way —
    //     `useEntityLock` renders from its own acquire outcome, not from
    //     this map.
    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.CustomNotification(key, _) when key = CollaborationTopics.Presence ->
                    Async.StartImmediate(
                        async {
                            match! reread () with
                            | Some roster -> setPeers roster
                            | None -> ()
                        }
                    )
                | Notification.CustomNotification(key, json) when key = CollaborationTopics.Lock ->
                    try
                        let event = Json.parseAs<LockEvent> json
                        setLocks (applyLockEvent event)
                    with _ ->
                        ()
                | _ -> ())

        FsReact.createDisposable dispose)

    // Memoised so a shell re-render that changed neither the roster nor
    // the lease map does not push a new context value through every
    // consumer of it.
    let value =
        React.useMemo (
            (fun () -> { Peers = peers; Locks = locks }: PresenceContext.PresenceContextValue),
            [| box peers; box locks |]
        )

    PresenceContext.provider value children

/// Wrap `children` in a live presence + soft-lock context. Called by the
/// shell under `ClientConfig.Presence = EnabledPresence`; a deployment on
/// the default `NoPresence` never reaches this and renders byte-for-byte
/// as before.
///
/// A deployment that mounts `PresenceContext.provider` itself — the
/// Phase 442 hand-wired path, which stays supported — keeps working with
/// this on: React context resolves to the nearest provider, so the
/// deployment's own nested provider wins for the views beneath it.
let mount (activeModuleId: string) (children: ReactElement) : ReactElement = PresenceRoot activeModuleId children
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Client.SyncCoordinator

open System
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Remoting.Client
open ToolUp.Platform
open ToolUp.Offline
open ToolUp.Offline.OfflineSyncApi
open ToolUp.Offline.Client.OfflineQueue

// ─── Phase 24 — drain-on-reconnect coordinator ───────────────────────
//
// Watches connectivity, drains the queue against `IOfflineSyncApi`, and
// applies each outcome back to the queue. Three design points:
//
// **v1 polls; it does not use the BackgroundSync API.** A poll plus the
// `online` and `visibilitychange` events covers every case a field user
// meets (the tab comes back, the link comes back) without depending on
// an API that two of the three engine families do not implement. The
// phase's out-of-scope list records this deliberately.
//
// **Backoff is per-mutation, not per-drain.** A single poisoned payload
// must not hold the rest of the queue behind it, so a failure parks
// THAT entry with an incremented attempt count and the drain continues.
// The queue's own `Drain` re-offers it once its backoff has elapsed.
//
// **`navigator.onLine` is a hint, never the decision.** It reports
// whether the machine has a link, not whether the server is reachable —
// captive portals and VPN drops both report `true`. So the coordinator
// attempts a drain whenever anything is pending and treats a transport
// failure as the real offline signal; `onLine` only decides how eagerly
// to try.

[<Emit("(typeof navigator !== 'undefined' && navigator !== null && navigator.onLine !== false)")>]
let private navigatorOnline () : bool = jsNative

// Read as a raw string rather than through the typed `VisibilityState`
// enum: the enum's shape has moved between Fable.Browser.Dom majors,
// and this comparison is the one thing that must not break when the
// binding is bumped.
[<Emit("(typeof document !== 'undefined' && document !== null && document.visibilityState === 'visible')")>]
let private documentVisible () : bool = jsNative

/// Map the SDK-side `OfflineConfig` retry fields onto the companion's
/// `RetryPolicy`. They are duplicated shapes because `ClientConfig` may
/// not name a companion type (GP 1); this function is the one place the
/// duplication is reconciled, so a field added to one and not the other
/// fails here rather than diverging silently.
let retryPolicyOf (config: OfflineConfig) : RetryPolicy = {
    InitialDelayMs = config.RetryInitialDelayMs
    Multiplier = config.RetryMultiplier
    MaxDelayMs = config.RetryMaxDelayMs
    MaxAttempts = config.RetryMaxAttempts
}

/// The default proxy over `IOfflineSyncApi`, using the contract's own
/// `routeBuilder` so client and server cannot drift on the URL shape.
let defaultProxy () : IOfflineSyncApi =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder OfflineSyncApi.routeBuilder
    |> Remoting.buildProxy<IOfflineSyncApi>

/// A registered handler for one class of mergeable queued payload —
/// Phase 760's half of the offline ⇄ CRDT recognition seam.
///
/// The coordinator recognises a mergeable entry by the reserved
/// `EntityType` prefix `MergeablePayload.Prefix` (see the design essay
/// on that module) and hands it here instead of replaying it through
/// `IOfflineSyncApi`. It never learns what the payload means: the
/// substrate that owns the channel supplies these two functions, and a
/// deployment registers them at compose time.
///
/// That indirection is the point. `ToolUp.Offline.Client` names no CRDT
/// type and `ToolUp.Platform.CrdtSyncClient` names no queue type, so
/// either companion composes without the other and neither's strip mode
/// (`NoOffline` / `NoCrdtDocuments`) is weakened by the other's presence
/// (GP 10 + GP 13). Both functions are `byte[] -> Async<_>` and
/// `unit -> Async<_>` for the same reason.
type MergeableChannel = {
    /// The channel name that follows `MergeablePayload.Prefix` in the
    /// queued `EntityType`. Registering two handlers for one name is a
    /// deployment error; the first registered wins and the coordinator
    /// does not police it.
    Channel: string
    /// The channel's own catch-up, run at the head of every drain pass
    /// that reaches it — for a CRDT log, the state-vector diff. Total
    /// with respect to the drain: a failure here is warned and the pass
    /// continues, because the entity queue does not travel on this
    /// channel's transport and must not be held behind it.
    CatchUp: unit -> Async<unit>
    /// Replay one buffered payload. Raising means "not now" — the entry
    /// is parked with its attempt counted and the rest of THIS channel's
    /// backlog is left `Pending`, exactly as a transport failure parks
    /// the entity queue.
    ///
    /// There is deliberately no conflict outcome to return. A mergeable
    /// payload cannot conflict; a handler that wants to reject one
    /// permanently is describing a payload that was never mergeable.
    Replay: byte[] -> Async<unit>
}

/// What one drain pass did. Returned so a caller can log or surface it;
/// the coordinator itself needs only `Attempted`.
type DrainReport = {
    Attempted: int
    Applied: int
    Conflicted: int
    Rejected: int
    Failed: int
    /// Mergeable payloads replayed through a registered
    /// `MergeableChannel`. Counted apart from `Applied` because they did
    /// not travel through `IOfflineSyncApi` and were never eligible to
    /// conflict.
    Merged: int
    /// Mergeable payloads whose channel has no registered handler. They
    /// stay `Pending` rather than falling back to the entity path: the
    /// fallback would replay a CRDT update as a last-writer-wins entity
    /// write, which is the exact corruption this seam exists to prevent.
    Unrouted: int
    /// Transport failure — the drain stopped early because the server
    /// is unreachable. The remaining mutations stay `Pending`, NOT
    /// `Failed`: an unreachable server is not the mutation's fault, and
    /// counting it against the mutation's attempt budget would park
    /// perfectly good writes after eight tunnels.
    Disconnected: bool
}

module DrainReport =
    let empty: DrainReport = {
        Attempted = 0
        Applied = 0
        Conflicted = 0
        Rejected = 0
        Failed = 0
        Merged = 0
        Unrouted = 0
        Disconnected = false
    }

/// Apply one server outcome back to the queue.
let private settle (queue: IOfflineQueue) (mutation: QueuedMutation) (outcome: SyncOutcome) (report: DrainReport) = async {
    match outcome with
    | Applied _ ->
        do! queue.MarkApplied mutation.Id

        return {
            report with
                Applied = report.Applied + 1
        }
    | Conflict(_, serverEntity) ->
        do! queue.MarkConflicted(mutation.Id, serverEntity)

        return {
            report with
                Conflicted = report.Conflicted + 1
        }
    | Rejected reason ->
        // Permanent by contract — retrying loops forever, so the entry
        // is dropped rather than parked. The reason is surfaced to the
        // console because a dropped write the user believes they made
        // is the worst silent failure this companion can produce.
        Browser.Dom.console.warn (
            sprintf
                "[ToolUp.Offline] mutation %s on %s was rejected and discarded: %s"
                mutation.Id
                mutation.EntityType
                reason
        )

        do! queue.Discard mutation.Id

        return {
            report with
                Rejected = report.Rejected + 1
        }
}

/// Replay the mergeable half of a due batch through its registered
/// channels. Returns the report updated with `Merged` / `Unrouted` /
/// `Failed`.
///
/// **This runs BEFORE the entity drain, and its failures do not stop
/// it.** The ordering is the substantive half of Phase 760: a CRDT
/// catch-up is a state-vector diff followed by the local backlog, and
/// it must not sit behind a queue of entity writes that may be parked
/// on a conflict the user has not answered. The independence is the
/// other half — these payloads travel on the channel's own transport,
/// so a channel that is down says nothing about whether
/// `IOfflineSyncApi` is reachable, and vice versa.
let private replayMergeable
    (channels: MergeableChannel list)
    (queue: IOfflineQueue)
    (batches: (string * QueuedMutation list) list)
    (initial: DrainReport)
    : Async<DrainReport> =
    async {
        let mutable report = initial

        for channel, entries in batches do
            match channels |> List.tryFind (fun c -> c.Channel = channel) with
            | None ->
                // Nothing registered for this channel. The entries stay
                // Pending — untouched, un-attempted, still durable — so
                // registering the handler later drains them. Warned
                // because a payload nothing can route is a wiring defect
                // that would otherwise present as a queue that never
                // empties.
                Browser.Dom.console.warn (
                    sprintf
                        "[ToolUp.Offline] %d queued payload(s) on mergeable channel '%s' have no registered handler; they stay pending. Register a MergeableChannel for it at compose time."
                        (List.length entries)
                        channel
                )

                report <- {
                    report with
                        Unrouted = report.Unrouted + List.length entries
                }
            | Some handler ->
                // Catch up first, then flush the backlog. A catch-up
                // failure is warned and the flush is skipped for THIS
                // channel only: publishing local updates over a
                // transport that just refused a read would burn the
                // backlog's retry budget for nothing.
                let! caughtUp = async {
                    try
                        do! handler.CatchUp()
                        return true
                    with ex ->
                        Browser.Dom.console.warn (
                            sprintf "[ToolUp.Offline] catch-up failed on mergeable channel '%s': %s" channel ex.Message
                        )

                        return false
                }

                let mutable channelDown = not caughtUp

                for mutation in entries do
                    if not channelDown then
                        let! outcome = async {
                            try
                                do! handler.Replay mutation.Payload
                                return Ok()
                            with ex ->
                                return Error ex.Message
                        }

                        match outcome with
                        | Ok() ->
                            // Settled by definition: a mergeable payload
                            // the channel accepted has converged. There
                            // is no `SyncOutcome` to inspect and no
                            // conflict branch to reach — which is what
                            // the acceptance means by "zero
                            // conflict-resolver involvement".
                            do! queue.MarkApplied mutation.Id

                            report <- {
                                report with
                                    Merged = report.Merged + 1
                            }
                        | Error reason ->
                            do! queue.MarkFailed(mutation.Id, reason)

                            report <- {
                                report with
                                    Failed = report.Failed + 1
                            }

                            channelDown <- true

        return report
    }

/// One drain pass, with mergeable payloads routed to their registered
/// channels. Total: a transport failure ends the entity half of the pass
/// with `Disconnected = true` rather than raising, because this runs
/// from a timer and an escaping exception in a timer callback is
/// invisible.
///
/// `drainOnce` is this with no channels registered, and is what every
/// pre-Phase-760 caller keeps getting.
let drainRouted
    (channels: MergeableChannel list)
    (queue: IOfflineQueue)
    (api: IOfflineSyncApi)
    (policy: RetryPolicy)
    (now: DateTimeOffset)
    : Async<DrainReport> =
    async {
        let! due = queue.Drain(policy, now)
        let mergeable, writes = MergeablePayload.partition due

        let! afterMerge =
            replayMergeable channels queue mergeable {
                DrainReport.empty with
                    Attempted = List.length due
            }

        let mutable report = afterMerge
        let mutable disconnected = false

        for mutation in writes do
            if not disconnected then
                let! outcome = async {
                    try
                        let! result = api.Apply mutation
                        return Ok result
                    with ex ->
                        return Error ex.Message
                }

                match outcome with
                | Ok result ->
                    let! updated = settle queue mutation result report
                    report <- updated
                | Error reason ->
                    // Could be the link, could be this one payload. The
                    // conservative reading is "the link", because marking a
                    // whole queue failed on a dropped connection burns
                    // every entry's retry budget at once. The entry is
                    // parked with its attempt counted, and the pass stops.
                    do! queue.MarkFailed(mutation.Id, reason)

                    report <- {
                        report with
                            Failed = report.Failed + 1
                    }

                    disconnected <- true

        return {
            report with
                Disconnected = disconnected
        }
    }

/// One drain pass with no mergeable channels registered — the
/// pre-Phase-760 behaviour, unchanged.
let drainOnce
    (queue: IOfflineQueue)
    (api: IOfflineSyncApi)
    (policy: RetryPolicy)
    (now: DateTimeOffset)
    : Async<DrainReport> =
    drainRouted [] queue api policy now

/// A running coordinator. `Stop` is the whole reason this is a record
/// of functions rather than a fire-and-forget `start` — a component
/// that mounts one must be able to tear it down, or a re-mount leaves
/// two timers draining the same queue.
type Coordinator = {
    /// Force a drain now (the `online` event, a manual "retry" button).
    SyncNow: unit -> Async<DrainReport>
    /// Current status for the badge.
    Status: unit -> Async<SyncStatus>
    /// Cancel the poll timer and the event listeners.
    Stop: unit -> unit
}

/// Start polling. Returns immediately; the first drain runs on the
/// first tick, not synchronously, so app boot is never blocked on a
/// network call.
///
/// Registers three triggers: the interval, the `online` event, and
/// `visibilitychange` (a backgrounded tab has its timers throttled to
/// ~1/min, so returning to the tab must not wait for the throttled
/// tick).
let startRouted
    (channels: MergeableChannel list)
    (queue: IOfflineQueue)
    (api: IOfflineSyncApi)
    (config: OfflineConfig)
    : Coordinator =
    let policy = retryPolicyOf config
    let mutable draining = false
    let mutable stopped = false

    let syncNow () : Async<DrainReport> = async {
        if stopped || draining then
            return DrainReport.empty
        else
            draining <- true

            try
                let! report = drainRouted channels queue api policy DateTimeOffset.UtcNow
                return report
            finally
                draining <- false
    }

    // A registered channel must catch up on RECONNECT even when the
    // queue is empty: nothing local is outstanding, but the remote log
    // has moved on while the tab was dark and the state-vector diff is
    // the only thing that closes that gap. With no channels registered
    // there is nothing to catch up, so the wake-up stays gated exactly
    // as it was before Phase 760 (GP 11).
    let wakeForcesSync = not (List.isEmpty channels)

    let tick (force: bool) =
        if not stopped then
            async {
                let! entries = queue.List()
                let stats = QueueStats.ofEntries entries

                // Nothing outstanding: no request, no wake-up. A quiet
                // app on this path costs one Map fold per interval.
                if force || stats.Pending > 0 || stats.Failed > 0 then
                    let! _ = syncNow ()
                    return ()
            }
            |> Async.StartImmediate

    let intervalId =
        Browser.Dom.window.setInterval ((fun () -> tick false), max 1000 config.PollIntervalMs)

    let onOnline = fun (_: Browser.Types.Event) -> tick wakeForcesSync

    let onVisible =
        fun (_: Browser.Types.Event) ->
            if documentVisible () then
                tick wakeForcesSync

    Browser.Dom.window.addEventListener ("online", unbox onOnline)
    Browser.Dom.document.addEventListener ("visibilitychange", unbox onVisible)

    {
        SyncNow = syncNow

        Status =
            fun () -> async {
                let! entries = queue.List()
                return SyncStatus.derive (navigatorOnline ()) draining (QueueStats.ofEntries entries)
            }

        Stop =
            fun () ->
                stopped <- true
                Browser.Dom.window.clearInterval intervalId
                Browser.Dom.window.removeEventListener ("online", unbox onOnline)
                Browser.Dom.document.removeEventListener ("visibilitychange", unbox onVisible)
    }

/// Start polling with no mergeable channels registered — the
/// pre-Phase-760 behaviour, unchanged.
let start (queue: IOfflineQueue) (api: IOfflineSyncApi) (config: OfflineConfig) : Coordinator =
    startRouted [] queue api config
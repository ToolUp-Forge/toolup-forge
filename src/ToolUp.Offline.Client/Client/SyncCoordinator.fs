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

/// What one drain pass did. Returned so a caller can log or surface it;
/// the coordinator itself needs only `Attempted`.
type DrainReport = {
    Attempted: int
    Applied: int
    Conflicted: int
    Rejected: int
    Failed: int
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

/// One drain pass. Total: a transport failure ends the pass with
/// `Disconnected = true` rather than raising, because this runs from a
/// timer and an escaping exception in a timer callback is invisible.
let drainOnce
    (queue: IOfflineQueue)
    (api: IOfflineSyncApi)
    (policy: RetryPolicy)
    (now: DateTimeOffset)
    : Async<DrainReport> =
    async {
        let! due = queue.Drain(policy, now)

        let mutable report = {
            DrainReport.empty with
                Attempted = List.length due
        }

        let mutable disconnected = false

        for mutation in due do
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
let start (queue: IOfflineQueue) (api: IOfflineSyncApi) (config: OfflineConfig) : Coordinator =
    let policy = retryPolicyOf config
    let mutable draining = false
    let mutable stopped = false

    let syncNow () : Async<DrainReport> = async {
        if stopped || draining then
            return DrainReport.empty
        else
            draining <- true

            try
                let! report = drainOnce queue api policy DateTimeOffset.UtcNow
                return report
            finally
                draining <- false
    }

    let tick () =
        if not stopped then
            async {
                let! entries = queue.List()
                let stats = QueueStats.ofEntries entries

                // Nothing outstanding: no request, no wake-up. A quiet
                // app on this path costs one Map fold per interval.
                if stats.Pending > 0 || stats.Failed > 0 then
                    let! _ = syncNow ()
                    return ()
            }
            |> Async.StartImmediate

    let intervalId =
        Browser.Dom.window.setInterval (tick, max 1000 config.PollIntervalMs)

    let onOnline = fun (_: Browser.Types.Event) -> tick ()

    let onVisible =
        fun (_: Browser.Types.Event) ->
            if documentVisible () then
                tick ()

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
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Collections.Generic
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform

// ─── The fact-table change signal ───────────────────────────────────
//
// A reader that keeps what it read — a cache of resolved page requests, a
// view that holds a slice — must learn when the facts under it changed. Two
// writes change them: a fact-table run committing (its table), and a data
// object landing that fact invalidation reacts to (any table of the scope,
// since the walk does not say which). This signal carries both, as a scope
// and the tables changed; an empty table list means any table of the scope.
//
// **Raised only when demanded.** A deployment with no reader of the signal
// composes it dormant: `Raise` returns at once, the writer decoration and the
// data-change arming cost nothing (GP 13). A reader that needs it registers
// the demand (`FactTableChangeSignal.demand`) beside its own registration.
//
// **Across replicas through the notification channel.** A raise notifies
// this process's listeners directly, and publishes one notification to the
// change's scope on the composed `INotificationChannel`; a listener's
// subscription to that scope hears the raises of every other process the
// channel reaches, and ignores its own. A channel that delivers nothing
// (the no-op channel, or none composed) leaves each process hearing only its
// own writes, which is exact for a single replica.

/// The signal a fact table's change is raised on.
type IFactTableChangeSignal =
    /// Raise a change: `tableIds` changed in `scope`; empty when any table of
    /// the scope may have changed. Returns once this process's listeners have
    /// been told and the notification is on the channel.
    abstract Raise: scope: ResolvedScope * tableIds: string list -> Async<unit>

    /// Listen for the changes of one scope. `listener` is told the tables
    /// changed (empty: any table of the scope), synchronously, and must not
    /// block. Dispose the result to stop.
    abstract Listen: scopeId: string * listener: (string list -> unit) -> Async<IDisposable>

/// The demand a reader of the signal registers: its presence arms the
/// signal's sources.
type FactTableChangeDemand() = class end

/// The notification a raise publishes to other processes.
type FactTableChangeNotice = {
    /// The raising process's signal, so it ignores its own notice.
    Origin: string
    /// The tables changed; empty for any table of the scope.
    TableIds: string list
}

/// Building and composing the signal.
[<RequireQualifiedAccess>]
module FactTableChangeSignal =

    /// The `CustomNotification` key a raise publishes under.
    [<Literal>]
    let NotificationKey = "Facts.TableChanged"

    let private noticeOptions =
        ToolUp.Remoting.Json.SystemTextJson.FableConverters.shared

    /// The signal over `channel`. `armed` is consulted on every raise; a raise
    /// while it is false does nothing.
    let create (channel: INotificationChannel option) (armed: unit -> bool) : IFactTableChangeSignal =
        let origin = Guid.NewGuid().ToString "N"
        let sync = obj ()
        let listeners = Dictionary<string, Dictionary<Guid, string list -> unit>>()
        let channelSubscriptions = Dictionary<string, NotificationSubscriptionId>()

        let tell (scopeId: string) (tableIds: string list) =
            let current =
                lock sync (fun () ->
                    match listeners.TryGetValue scopeId with
                    | true, byId -> List.ofSeq byId.Values
                    | false, _ -> [])

            for listener in current do
                try
                    listener tableIds
                with _ ->
                    ()

        let onEnvelope (envelope: NotificationEnvelope) =
            match envelope.Notification with
            | CustomNotification(key, payload) when key = NotificationKey ->
                let notice =
                    try
                        Some(JsonSerializer.Deserialize<FactTableChangeNotice>(payload, noticeOptions))
                    with _ ->
                        None

                match notice with
                | Some notice when not (isNull (box notice)) && notice.Origin <> origin ->
                    tell envelope.ScopeId (if isNull (box notice.TableIds) then [] else notice.TableIds)
                | _ -> ()
            | _ -> ()

        { new IFactTableChangeSignal with
            member _.Raise(scope: ResolvedScope, tableIds: string list) = async {
                if armed () then
                    let tableIds = tableIds |> List.distinct |> List.sort
                    tell scope.ScopeId tableIds

                    match channel with
                    | Some channel ->
                        let payload =
                            JsonSerializer.Serialize({ Origin = origin; TableIds = tableIds }, noticeOptions)

                        try
                            do! channel.Publish(scope.ScopeId, CustomNotification(NotificationKey, payload))
                        with _ ->
                            ()
                    | None -> ()
            }

            member _.Listen(scopeId: string, listener: string list -> unit) = async {
                let id = Guid.NewGuid()

                let first =
                    lock sync (fun () ->
                        match listeners.TryGetValue scopeId with
                        | true, byId ->
                            byId.[id] <- listener
                            false
                        | false, _ ->
                            let byId = Dictionary<Guid, string list -> unit>()
                            byId.[id] <- listener
                            listeners.[scopeId] <- byId
                            true)

                match channel with
                | Some channel when first ->
                    let! subscription = channel.Subscribe(scopeId, onEnvelope)

                    let duplicate =
                        lock sync (fun () ->
                            if channelSubscriptions.ContainsKey scopeId then
                                true
                            else
                                channelSubscriptions.[scopeId] <- subscription
                                false)

                    if duplicate then
                        do! channel.Unsubscribe subscription
                | _ -> ()

                return
                    { new IDisposable with
                        member _.Dispose() =
                            lock sync (fun () ->
                                match listeners.TryGetValue scopeId with
                                | true, byId -> byId.Remove id |> ignore
                                | false, _ -> ())
                    }
            }
        }

    /// Decorate a table writer so each successful `Commit` raises a change
    /// of its table. Every other member passes straight through, the orphan
    /// sweep included. A raise failure never fails the commit.
    let raisingWriter (signal: IFactTableChangeSignal) (inner: IFactTableWriter) : IFactTableWriter =
        { new IFactTableWriter with
            member _.OpenRun(scope, tableId, ?provenance, ?options) =
                inner.OpenRun(scope, tableId, ?provenance = provenance, ?options = options)

            member _.WriteRows(scope, runId, rows) = inner.WriteRows(scope, runId, rows)

            member _.Commit(scope: ResolvedScope, runId) = async {
                let! outcome = inner.Commit(scope, runId)

                match outcome with
                | Ok commit ->
                    try
                        do! signal.Raise(scope, [ commit.Watermark.TableId ])
                    with _ ->
                        ()
                | Error _ -> ()

                return outcome
            }

            member _.Abandon(scope, runId, reason) = inner.Abandon(scope, runId, reason)
            member _.Runs(scope, tableId) = inner.Runs(scope, tableId)
            member _.Status(scope, tableId) = inner.Status(scope, tableId)
          interface IFactTableOrphanSweep with
              member _.SweepOrphans(scope) = FactTableOrphanSweep.sweep inner scope
        }

    /// The data-change reaction that raises: a landed data object may change
    /// any table of its scope, so the change names none.
    let reaction (signal: unit -> IFactTableChangeSignal) : FactDataChangeReaction =
        fun scope _ -> signal().Raise(scope, [])

    /// Register the demand that arms the signal. A reader of the signal calls
    /// this beside its own registration; idempotent.
    let demand (services: IServiceCollection) : IServiceCollection =
        services.TryAddSingleton<FactTableChangeDemand>(FactTableChangeDemand())
        services

    /// Whether a reader of the signal is composed.
    let demanded (sp: IServiceProvider) : bool =
        (ServiceComposition.tryService<FactTableChangeDemand> sp).IsSome
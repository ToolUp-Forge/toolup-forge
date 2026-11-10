// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

open System
open System.Threading
open ToolUp.Platform

// ─── Recording<'T> ────────────────────────────────────────────────────
//
// The one list a test double records into. A double is usually written
// by the code under test — from a background job, a timer, a callback
// the code owns — while the test reads it on its own thread, so a bare
// `ResizeArray` there is a race: an `Add` that grows the buffer while a
// reader enumerates it throws "Collection was modified", or the reader
// sees a torn count. `Recording<'T>` takes one lock for every write and
// answers every read with a SNAPSHOT (an immutable list), so a reader
// can never observe a write in flight.
//
// It also turns "wait until the double has seen N things" into a wait
// on the WRITE itself: `WaitUntil` wakes on every `Add`, so a test
// waits exactly as long as the code under test takes, and the timeout
// is a backstop against a hang, never the mechanism the test relies on.

/// A thread-safe, append-only record of values written by a test
/// double. Writes take one lock; reads answer a snapshot.
[<Sealed>]
type Recording<'T>() =
    let items = ResizeArray<'T>()
    let gate = obj ()

    /// Record one value. Safe from any thread.
    member _.Add(item: 'T) =
        lock gate (fun () ->
            items.Add item
            Monitor.PulseAll gate)

    /// Every value recorded so far, oldest first, as an immutable
    /// snapshot: later writes do not appear in a list already read.
    member _.Snapshot() : 'T list = lock gate (fun () -> List.ofSeq items)

    /// How many values have been recorded.
    member _.Count = lock gate (fun () -> items.Count)

    /// Forget everything recorded so far.
    member _.Clear() = lock gate (fun () -> items.Clear())

    /// Block until `predicate` holds over the snapshot, re-checking on
    /// every write, or until `timeout` passes. Answers whether it held.
    /// The timeout is a hang backstop: size it generously, because the
    /// wait returns at the write that satisfies it, not at the deadline.
    member _.WaitUntil(predicate: 'T list -> bool, timeout: TimeSpan) : bool =
        let deadline = Diagnostics.Stopwatch.StartNew()

        lock gate (fun () ->
            let mutable held = predicate (List.ofSeq items)

            while not held && deadline.Elapsed < timeout do
                let remaining = timeout - deadline.Elapsed

                if remaining > TimeSpan.Zero then
                    Monitor.Wait(gate, remaining) |> ignore

                held <- predicate (List.ofSeq items)

            held)

/// Constructors and waits over `Recording<'T>`.
[<RequireQualifiedAccess>]
module Recording =
    /// The backstop a wait on a recording uses when the caller names
    /// none: long enough that a loaded machine never reaches it, short
    /// enough that a genuine hang fails the test rather than the lane.
    let defaultBackstop = TimeSpan.FromSeconds 60.0

    /// A fresh, empty recording.
    let create<'T> () = Recording<'T>()

    /// Block until the recording holds at least `count` values (or the
    /// default backstop passes). Answers whether it did.
    let waitForCount (count: int) (recording: Recording<'T>) : bool =
        recording.WaitUntil((fun items -> List.length items >= count), defaultBackstop)

// ─── The recording audit log ──────────────────────────────────────────
//
// Lives beside `Recording<'T>` rather than with the other fakes because
// it needs nothing but Core: a test pack that may not reference the
// server tier can compile this file on its own.

/// One row an `IAuditLog` was asked to record.
type AuditRecord = {
    /// The stream the row was written under (`AuditScope.streamKey`).
    StreamKey: string
    Scope: AuditScope
    Actor: AuditSubject
    Event: AuditEvent
}

/// Recording `IAuditLog`. Every `Record` succeeds and is kept, in order;
/// `GetAuditTrail` answers the rows of the asked scope, newest first,
/// filtered by event-type name when one is given (the date range is not
/// applied: a recorded `AuditEvent` carries no timestamp of its own).
type TestAuditLog() =
    let rows = Recording<AuditRecord>()

    /// The recording itself, for a wait on a row yet to be written.
    member _.Recording = rows

    /// Every row recorded so far, oldest first.
    member _.Records = rows.Snapshot()

    /// The events recorded so far, oldest first.
    member _.Events = rows.Snapshot() |> List.map _.Event

    interface IAuditLog with
        member _.Record(scope, actor, audit) = async {
            rows.Add {
                StreamKey = AuditScope.streamKey scope
                Scope = scope
                Actor = actor
                Event = audit
            }

            return Ok()
        }

        member _.GetAuditTrail(scope, _dateRange, eventType) = async {
            let key = AuditScope.streamKey scope

            return
                rows.Snapshot()
                |> List.filter (fun row ->
                    row.StreamKey = key
                    && (eventType |> Option.forall ((=) (AuditEvent.eventTypeName row.Event))))
                |> List.rev
                |> List.map _.Event
        }
namespace ToolUp.Remoting.Server

open System.Collections.Generic

/// What one eviction pass left a bounded store with.
[<RequireQualifiedAccess>]
type internal FifoPressure =
    /// The store is below its cap.
    | Relieved
    /// The pass spent its drain budget while still at the cap. The next
    /// admission carries on, so the store is transiently over cap.
    | DrainBudgetSpent
    /// The store is at its cap and no marker is left to evict by.
    | NoMarkers
    /// The store is at its cap and the policy declined every marker.
    | AllHeld

/// How one store uses the index: which entries may go, how much one admission
/// may evict, and what it does about pressure. `OnPressure` runs before the
/// admission inserts, so raising refuses the admission and leaves no trace.
type internal BoundedFifoPolicy = {
    DrainBatch: int
    Evictable: string -> bool
    OnPressure: FifoPressure -> unit
}

module internal BoundedFifoPolicy =
    /// Evict the oldest entry whatever it holds. A single admission adds one
    /// entry, so the steady state removes one victim; the batch bounds the
    /// catch-up after a burst.
    let oldestFirst (onPressure: FifoPressure -> unit) = {
        DrainBatch = 64
        Evictable = fun _ -> true
        OnPressure = onPressure
    }

/// The bounded insertion-order index the in-memory remoting stores share: a
/// store keeps its entries in its own dictionary and this keeps the order they
/// arrived in, one marker per entry. A marker whose entry has gone is stale and
/// is dropped when reached.
///
/// Every structural change to the store goes through this type, under one lock.
/// The order and the dictionary span two structures, and a change that crosses
/// them without the lock can strand an entry with no marker, which nothing can
/// then evict. Reads of the store stay lock-free.
///
/// Nothing here wipes a store. With no marker to evict by, the store goes
/// transiently over its cap and says so through `FifoPressure`.
[<Sealed>]
type internal BoundedFifo
    (cap: int, count: unit -> int, contains: string -> bool, remove: string -> unit, policy: BoundedFifoPolicy) =
    let gate = obj ()
    let order = Queue<string>()

    let compactStaleHeads () =
        while order.Count > 0 && not (contains (order.Peek())) do
            order.Dequeue() |> ignore

    /// Walks forward from the oldest marker. A declined entry rotates to the
    /// tail, so it stays in the sweep without blocking the entries behind it.
    let evictOldest () =
        let mutable drained = 0
        let mutable rotated = 0
        let mutable pressure = ValueNone

        while pressure.IsNone do
            if count () < cap then
                pressure <- ValueSome FifoPressure.Relieved
            elif drained >= policy.DrainBatch then
                pressure <- ValueSome FifoPressure.DrainBudgetSpent
            elif order.Count = 0 then
                pressure <- ValueSome FifoPressure.NoMarkers
            elif rotated >= order.Count then
                pressure <- ValueSome FifoPressure.AllHeld
            else
                let candidate = order.Dequeue()

                if contains candidate then
                    if policy.Evictable candidate then
                        remove candidate
                        drained <- drained + 1
                        rotated <- 0
                    else
                        order.Enqueue candidate
                        rotated <- rotated + 1

        pressure.Value

    /// Runs `insert` for `key`. A key the store does not hold first makes room
    /// and then gains a marker; a key it holds keeps the marker it has, so an
    /// entry's age is its first insertion.
    member _.Admit(key: string, insert: unit -> 'T) : 'T =
        lock gate (fun () ->
            if contains key then
                insert ()
            else
                compactStaleHeads ()
                policy.OnPressure(evictOldest ())
                let inserted = insert ()
                order.Enqueue key
                inserted)

    /// Removes an entry outside eviction. Its marker goes stale and is dropped
    /// when reached.
    member _.Withdraw(key: string) = lock gate (fun () -> remove key)
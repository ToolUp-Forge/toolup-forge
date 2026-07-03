module ToolUp.Platform.Tests.InProcess.CompositionHotSwapTests

open Expecto
open ToolUp.Platform

// ─── Phase 301 — live composition hot-swap ────────────────────────────
//
// Covers the acceptance shape: an enabled companion swap re-points new
// traffic to the replacement while a reference already resolved (an
// in-flight request) finishes on the old; a replacement that fails to
// initialise rolls back cleanly (old stays live); the Phase 291 lifecycle
// order is respected (a cyclic order is refused); only declared
// components are swappable; the gate-off deployment refuses every swap and
// is unchanged (GP 11/13). Every attempt emits a ComponentId-keyed event.

/// A fake companion implementation that records its lifecycle.
type private FakeImpl = {
    Name: string
    mutable Initialised: bool
    mutable Disposed: bool
}

let private mk name = {
    Name = name
    Initialised = false
    Disposed = false
}

let private targetId = ComponentId.forCompanionSlot "IBlobStorage"

/// A registry seeded with an `old` blob-storage impl at `targetId`, plus
/// an acyclic single-component lifecycle order.
let private seed () =
    let old = mk "old"
    let registry = ComponentRegistry<FakeImpl>([ targetId, old ])
    let order = ComponentLifecycle.ofComponents [ targetId ]
    old, registry, order

let private init (i: FakeImpl) = i.Initialised <- true
let private dispose (i: FakeImpl) = i.Disposed <- true

let tests =
    testList "CompositionHotSwap" [

        // ── enabled swap re-points new traffic; in-flight keeps old ───
        testCase "an enabled swap re-points new traffic while an in-flight reference finishes on the old"
        <| fun _ ->
            let old, registry, order = seed ()

            // A request that resolved BEFORE the swap holds the old ref.
            let inFlight = registry.Resolve targetId |> Option.get

            let replacement = mk "new"

            let outcome =
                CompositionHotSwap.swap
                    EnabledHotSwap
                    CompositionHotSwap.noEmit
                    order
                    registry
                    init
                    dispose
                    targetId
                    replacement

            Expect.equal outcome (SwapApplied targetId) "the swap succeeds"
            Expect.isTrue replacement.Initialised "the replacement was initialised"
            Expect.isTrue old.Disposed "the old implementation was disposed"

            // New traffic resolves the replacement...
            Expect.isTrue
                (System.Object.ReferenceEquals(registry.Resolve targetId |> Option.get, replacement))
                "a new resolution sees the replacement"

            // ...but the in-flight reference still points at the old impl.
            Expect.isTrue
                (System.Object.ReferenceEquals(inFlight, old))
                "the in-flight request finishes against the old implementation"

        // ── a failed init rolls back cleanly ──────────────────────────
        testCase "a replacement that fails to initialise rolls back (old stays live)"
        <| fun _ ->
            let old, registry, order = seed ()
            let replacement = mk "new"
            let failingInit _ = failwith "init boom"

            let outcome =
                CompositionHotSwap.swap
                    EnabledHotSwap
                    CompositionHotSwap.noEmit
                    order
                    registry
                    failingInit
                    dispose
                    targetId
                    replacement

            match outcome with
            | SwapRolledBack(id, reason) ->
                Expect.equal id targetId "rollback names the target"
                Expect.stringContains reason "init" "the reason names the init failure"
            | other -> failtestf "expected SwapRolledBack, got %A" other

            Expect.isTrue
                (System.Object.ReferenceEquals(registry.Resolve targetId |> Option.get, old))
                "the registry still serves the old implementation after a failed swap"

            Expect.isFalse old.Disposed "the old implementation was not disposed on a rolled-back swap"

        // ── only declared components are swappable ────────────────────
        testCase "swapping an undeclared ComponentId is rejected (no arbitrary-code injection)"
        <| fun _ ->
            let _, registry, order = seed ()
            let unknown = ComponentId.forCompanionSlot "IAuthProvider"

            let outcome =
                CompositionHotSwap.swap
                    EnabledHotSwap
                    CompositionHotSwap.noEmit
                    order
                    registry
                    init
                    dispose
                    unknown
                    (mk "rogue")

            match outcome with
            | SwapRejected(id, reason) ->
                Expect.equal id unknown "rejection names the target"
                Expect.stringContains reason "declared" "only declared components are swappable"
            | other -> failtestf "expected SwapRejected, got %A" other

        // ── the Phase 291 lifecycle order is respected ────────────────
        testCase "a swap under a cyclic lifecycle order is refused"
        <| fun _ ->
            let _, registry, _ = seed ()
            let other = ComponentId.forCompanionImpl "IAuditSink" "x"

            let cyclic =
                ComponentLifecycle.ofComponents [ targetId; other ]
                |> ComponentLifecycle.before targetId other
                |> ComponentLifecycle.before other targetId

            let outcome =
                CompositionHotSwap.swap
                    EnabledHotSwap
                    CompositionHotSwap.noEmit
                    cyclic
                    registry
                    init
                    dispose
                    targetId
                    (mk "new")

            match outcome with
            | SwapRejected(_, reason) ->
                Expect.stringContains reason "lifecycle order" "the cyclic order is the stated reason"
            | other -> failtestf "expected SwapRejected on a cyclic order, got %A" other

        // ── GP 11/13: gate off refuses every swap, registry unchanged ─
        testCase "the gate-off deployment refuses the swap and is unchanged"
        <| fun _ ->
            let old, registry, order = seed ()
            let replacement = mk "new"

            let outcome =
                CompositionHotSwap.swap
                    NoHotSwap
                    CompositionHotSwap.noEmit
                    order
                    registry
                    init
                    dispose
                    targetId
                    replacement

            match outcome with
            | SwapRejected(_, reason) -> Expect.stringContains reason "disabled" "hot-swap is disabled by default"
            | other -> failtestf "expected SwapRejected when gated off, got %A" other

            Expect.isTrue
                (System.Object.ReferenceEquals(registry.Resolve targetId |> Option.get, old))
                "the registry is untouched when hot-swap is disabled"

            Expect.isFalse replacement.Initialised "the replacement is never initialised when gated off"

        // ── every attempt emits a ComponentId-keyed event ─────────────
        testCase "a swap emits a ComponentId-keyed event"
        <| fun _ ->
            let _, registry, order = seed ()
            let events = System.Collections.Generic.List<HotSwapEvent>()

            let outcome =
                CompositionHotSwap.swap EnabledHotSwap events.Add order registry init dispose targetId (mk "new")

            Expect.equal events.Count 1 "exactly one event emitted"
            Expect.equal events.[0].Component targetId "the event is keyed by the target ComponentId"
            Expect.equal events.[0].Outcome outcome "the event carries the outcome"
    ]
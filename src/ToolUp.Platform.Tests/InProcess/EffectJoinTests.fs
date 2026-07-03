module ToolUp.Platform.Tests.InProcess.EffectJoinTests

open Expecto
open ToolUp.Platform

// ─── Phase 296 — CompanionCapability effect-join surface ──────────────
//
// Covers the acceptance shape: a composition's joined effect class equals
// the componentwise join of its companions' capabilities (associative +
// commutative + idempotent, with `identity` the two-sided identity); an
// effecting companion flips a pure composition to effecting; the composed
// effect is surfaced over a `CapabilitySignature` keyed by the same
// `ComponentId` the Phase 280 manifest enumerates. A deployment that never
// reads it pays nothing (GP 11 / GP 13) — a pure value fold.

// A few representative capabilities across the three axes.
let private effectingClock =
    CompanionCapability.identity
    |> CompanionCapability.withEffect Effecting
    |> CompanionCapability.withDeterminism DeterminismSource.clock

let private effectingRandom =
    CompanionCapability.identity
    |> CompanionCapability.withEffect Effecting
    |> CompanionCapability.withDeterminism DeterminismSource.random

let private devOnlyState = CompanionCapability.devOnlyEffecting

let tests =
    testList "EffectJoin" [

        // ── identity is the two-sided join identity ───────────────────
        testCase "identity is the two-sided join identity"
        <| fun _ ->
            Expect.equal
                (CompanionCapability.join CompanionCapability.identity effectingClock)
                effectingClock
                "identity ⊔ x = x"

            Expect.equal
                (CompanionCapability.join effectingClock CompanionCapability.identity)
                effectingClock
                "x ⊔ identity = x"

        // ── effecting wins over pure ──────────────────────────────────
        testCase "an effecting companion flips a pure composition to effecting"
        <| fun _ ->
            let joined =
                CompanionCapability.joinAll [ CompanionCapability.pure'; CompanionCapability.pure'; effectingClock ]

            Expect.equal joined.Effect Effecting "one effecting part makes the whole effecting"
            Expect.isFalse (CompanionCapability.isPure joined) "the composition is no longer pure"

        // ── an all-pure composition stays pure ────────────────────────
        testCase "a composition of pure (or undeclared) companions joins to identity"
        <| fun _ ->
            let joined =
                CompanionCapability.joinAll [
                    CompanionCapability.identity
                    CompanionCapability.pure'
                    CompanionCapability.defaultCapability
                ]

            Expect.equal joined CompanionCapability.identity "all-pure joins to the identity"

        // ── empty join is the identity ────────────────────────────────
        testCase "the join of no capabilities is the identity"
        <| fun _ -> Expect.equal (CompanionCapability.joinAll []) CompanionCapability.identity "empty ⊔ = identity"

        // ── determinism factors union componentwise ───────────────────
        testCase "determinism factors union under the join"
        <| fun _ ->
            let joined = CompanionCapability.join effectingClock effectingRandom

            Expect.equal
                (DeterminismSource.factors joined.Determinism)
                (Set.ofList [ ClockFactor; RandomFactor ])
                "clock ⊔ random = {clock; random}"

        // ── dev-only wins over distributed-ready ──────────────────────
        testCase "dev-only wins over distributed-ready under the join"
        <| fun _ ->
            let joined =
                CompanionCapability.join CompanionCapability.distributedEffecting devOnlyState

            Expect.equal joined.Readiness DevOnly "one dev-only part makes the whole composition dev-only"
            Expect.isFalse (CompanionCapability.isDistributedReady joined) "the composition is not distributed-ready"

        // ── commutativity ─────────────────────────────────────────────
        testCase "the join is commutative"
        <| fun _ ->
            Expect.equal
                (CompanionCapability.join effectingClock devOnlyState)
                (CompanionCapability.join devOnlyState effectingClock)
                "a ⊔ b = b ⊔ a"

        // ── associativity ─────────────────────────────────────────────
        testCase "the join is associative"
        <| fun _ ->
            let a = effectingClock
            let b = effectingRandom
            let c = devOnlyState

            Expect.equal
                (CompanionCapability.join (CompanionCapability.join a b) c)
                (CompanionCapability.join a (CompanionCapability.join b c))
                "(a ⊔ b) ⊔ c = a ⊔ (b ⊔ c)"

        // ── idempotence ───────────────────────────────────────────────
        testCase "the join is idempotent"
        <| fun _ -> Expect.equal (CompanionCapability.join effectingClock effectingClock) effectingClock "a ⊔ a = a"

        // ── order-independence of joinAll ─────────────────────────────
        testCase "joinAll is order-independent (associative + commutative)"
        <| fun _ ->
            let forward =
                CompanionCapability.joinAll [ effectingClock; effectingRandom; devOnlyState ]

            let reversed =
                CompanionCapability.joinAll [ devOnlyState; effectingRandom; effectingClock ]

            Expect.equal forward reversed "the fold order does not change the joined effect"

        // ── composedEffect over a CapabilitySignature (the manifest surface)
        testCase "composedEffect joins a signature keyed by ComponentId"
        <| fun _ ->
            // Key each declared capability by the same ComponentId the
            // manifest enumerates the companion slot under.
            let signature: CapabilitySignature =
                Map [
                    ComponentId.forCompanionImpl "IAuditSink" "splunk-archive", CompanionCapability.distributedEffecting
                    ComponentId.forCompanionSlot "IJobScheduler", devOnlyState
                    ComponentId.forCompanionSlot "IBlobStorage", CompanionCapability.pure'
                ]

            let composed = CompanionCapability.composedEffect signature

            Expect.equal composed.Effect Effecting "the composition is effecting (audit + jobs are)"
            Expect.equal composed.Readiness DevOnly "one dev-only companion makes the whole dev-only"

            Expect.equal
                composed
                (CompanionCapability.joinAll [
                    CompanionCapability.distributedEffecting
                    devOnlyState
                    CompanionCapability.pure'
                ])
                "composedEffect equals the join of the signature's declared capabilities"

        // ── an undeclared component contributes the identity ──────────
        testCase "resolve falls back to the conservative default for an undeclared component"
        <| fun _ ->
            let signature: CapabilitySignature =
                Map [
                    ComponentId.forCompanionSlot "IBlobStorage", CompanionCapability.distributedEffecting
                ]

            // A component id NOT in the signature resolves to the identity.
            let undeclared =
                CompanionCapability.resolve signature (ComponentId.forCompanionSlot "IAuthProvider")

            Expect.equal undeclared CompanionCapability.identity "an undeclared component resolves to the identity"

            // An empty signature composes to the identity — a fully-undeclared
            // composition is "pure" (byte-for-byte pre-282 posture, GP 11).
            Expect.equal
                (CompanionCapability.composedEffect Map.empty)
                CompanionCapability.identity
                "an all-undeclared composition joins to the identity"
    ]
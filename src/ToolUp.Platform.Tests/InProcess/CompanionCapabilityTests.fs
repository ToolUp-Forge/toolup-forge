module ToolUp.Platform.Tests.InProcess.CompanionCapabilityTests

open Expecto
open ToolUp.Platform

// ─── Phase 282 — typed companion capability descriptors ───────────────
//
// Covers the acceptance shape: a companion can declare a typed
// `CompanionCapability`; it correlates into the Phase 280 manifest by the
// same stable `ComponentId` (no drift-vs-reflection gap); an UNDECLARED
// companion takes the conservative default (the join identity — pure /
// deterministic / distributed-ready) and a pre-282 deployment is
// byte-for-byte unchanged (GP 11). The descriptor is a pure value read on
// demand — nothing is built or enforced here (GP 13; Phase 300 is the
// opt-in runtime gate).

let tests =
    testList "CompanionCapability" [

        // ── the conservative default is the join identity ─────────────
        testCase "the default capability is pure / deterministic / distributed-ready"
        <| fun _ ->
            let d = CompanionCapability.defaultCapability

            Expect.equal d.Effect Pure "default effect is Pure"
            Expect.equal d.Determinism Deterministic "default determinism is Deterministic"
            Expect.equal d.Readiness DistributedReady "default readiness is DistributedReady"
            Expect.equal d CompanionCapability.identity "the default is the join identity"
            Expect.isTrue (CompanionCapability.isPure d) "the default is pure"

        // ── a declared capability round-trips through its axes ────────
        testCase "a declared capability preserves its declared axes"
        <| fun _ ->
            let declared =
                CompanionCapability.identity
                |> CompanionCapability.withEffect Effecting
                |> CompanionCapability.withDeterminism DeterminismSource.clock
                |> CompanionCapability.withReadiness DevOnly

            Expect.equal declared.Effect Effecting "effect declared Effecting"
            Expect.equal declared.Determinism DeterminismSource.clock "determinism declared clock"
            Expect.equal declared.Readiness DevOnly "readiness declared DevOnly"
            Expect.isFalse (CompanionCapability.isPure declared) "a declared effecting capability is not pure"

            Expect.isFalse
                (CompanionCapability.isDistributedReady declared)
                "a dev-only capability is not distributed-ready"

        // ── round-trips into the manifest by shared ComponentId ───────
        //
        // The declaration keys on the SAME ComponentId the Phase 280
        // manifest enumerates the companion slot under — so a capability
        // signature (Map<ComponentId, CompanionCapability>) correlates
        // component-for-component with the manifest, no separate key space.
        testCase "a declared capability correlates to its manifest companion entry by ComponentId"
        <| fun _ ->
            // A composed app with an audit-sink companion — the manifest
            // enumerates it under forCompanionImpl "IAuditSink" <name>.
            let app =
                ServerApp.empty |> ServerApp.withAuditSink (InMemoryAuditSink "splunk-archive")

            let manifest = ServerApp.compositionManifest app

            let auditEntry =
                manifest.CompanionSlots |> List.find (fun e -> e.Label = "IAuditSink")

            // A capability signature declared against the SAME ComponentId.
            let signature: Map<ComponentId, CompanionCapability> =
                Map [ auditEntry.Id, CompanionCapability.distributedEffecting ]

            match Map.tryFind auditEntry.Id signature with
            | Some cap ->
                Expect.equal
                    cap
                    CompanionCapability.distributedEffecting
                    "the declared capability round-trips by the manifest's ComponentId"

                Expect.equal cap.Effect Effecting "the audit sink is declared effecting"
            | None -> failtest "the declared capability did not correlate to the manifest entry's ComponentId"

        // ── an undeclared companion takes the conservative default ────
        testCase "an undeclared companion takes the conservative default"
        <| fun _ ->
            let app =
                ServerApp.empty |> ServerApp.withAuditSink (InMemoryAuditSink "splunk-archive")

            let manifest = ServerApp.compositionManifest app

            let auditEntry =
                manifest.CompanionSlots |> List.find (fun e -> e.Label = "IAuditSink")

            // No capability declared for this id → the lookup default is the
            // conservative identity (byte-for-byte pre-282 posture, GP 11).
            let signature: Map<ComponentId, CompanionCapability> = Map.empty

            let resolved =
                signature
                |> Map.tryFind auditEntry.Id
                |> Option.defaultValue CompanionCapability.defaultCapability

            Expect.equal resolved CompanionCapability.identity "an undeclared companion resolves to the identity"

        // ── reference-companion postures declare the six-rules prose ──
        testCase "the reference-companion posture constants declare the documented prose"
        <| fun _ ->
            // distributed-ready cloud companion — effecting + external-state,
            // distributed-ready.
            Expect.equal
                CompanionCapability.distributedEffecting.Readiness
                DistributedReady
                "distributedEffecting is distributed-ready"

            Expect.equal CompanionCapability.distributedEffecting.Effect Effecting "distributedEffecting is effecting"

            // dev-only in-memory reference impl — the documented exception.
            Expect.equal CompanionCapability.devOnlyEffecting.Readiness DevOnly "devOnlyEffecting is dev-only"

            Expect.isFalse
                (CompanionCapability.isDistributedReady CompanionCapability.devOnlyEffecting)
                "a dev-only reference impl is not distributed-ready"

        // ── DeterminismSource normalises the empty factor set ─────────
        testCase "DeterminismSource folds the empty factor set to Deterministic"
        <| fun _ ->
            Expect.equal
                (DeterminismSource.ofFactors Set.empty)
                Deterministic
                "the empty factor set normalises to Deterministic (equality stays total)"

            Expect.equal (DeterminismSource.factors Deterministic) Set.empty "Deterministic draws on no factors"

            Expect.equal
                (DeterminismSource.factors DeterminismSource.clock)
                (Set.singleton ClockFactor)
                "clock draws on exactly the clock factor"

        // ── factor wire tokens are stable + non-positional ────────────
        testCase "determinism factors render stable wire tokens"
        <| fun _ ->
            Expect.equal (DeterminismFactor.toWireString ClockFactor) "clock" "clock token"
            Expect.equal (DeterminismFactor.toWireString RandomFactor) "random" "random token"
            Expect.equal (DeterminismFactor.toWireString ExternalStateFactor) "external-state" "external-state token"
    ]
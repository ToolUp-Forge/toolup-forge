module ToolUp.Platform.Tests.InProcess.CompositionCapabilityGateTests

open Expecto
open ToolUp.Platform

// ─── Phase 300 — composition capability sandbox (runtime default-deny) ──
//
// Pins the security property: with the gate on, a component may invoke only
// capabilities at or below its declared Phase 282 envelope (resolved from a
// Phase 296 CapabilitySignature keyed by ComponentId); anything beyond fails
// closed with a readable, component-named error AND an observable deny; an
// undeclared component defaults to the identity ("pure") so any effecting /
// hidden-read access it attempts is denied by construction (default-deny);
// the `disabled` gate grants everything (byte-identical off, GP 11/13); and
// `guardInvoke` enforces the envelope IN FRONT of the Phase 266 registry.

// A capability the sandbox permits for a component declared effecting+clock.
let private effectingClock =
    CompanionCapability.identity
    |> CompanionCapability.withEffect Effecting
    |> CompanionCapability.withDeterminism DeterminismSource.clock

let private auditId = ComponentId.forCompanionImpl "IAuditSink" "splunk-archive"
let private jobsId = ComponentId.forCompanionSlot "IJobScheduler"

// A signature declaring the audit component as effecting+clock; the jobs
// component is deliberately UNDECLARED (absent).
let private signature: CapabilitySignature = Map [ auditId, effectingClock ]

let private teamCtx: AccessContext = {
    UserId = "u1"
    TeamId = Some "team-1"
    Subject = TeamMember("u1", "team-1")
    ModulePermissions = Map.empty
    ModuleExposure = Map.empty
    PlatformRole = None
}

let tests =
    testList "CompositionCapabilityGate" [

        // ── within-envelope access is granted ─────────────────────────
        testCase "a component invoking a declared capability is granted"
        <| fun _ ->
            let gate = CompositionCapabilityGate.create ignore signature

            // required == declared → granted; a strictly-lesser (pure)
            // requirement is also granted.
            Expect.equal
                (gate.Check auditId effectingClock)
                CapabilityGateDecision.Granted
                "declared capability granted"

            Expect.equal
                (gate.Check auditId CompanionCapability.pure')
                CapabilityGateDecision.Granted
                "a pure requirement is within any envelope"

        // ── beyond-envelope access is denied, component-named ─────────
        testCase "an undeclared capability use is denied with a component-named error"
        <| fun _ ->
            let gate = CompositionCapabilityGate.create ignore signature

            // The audit component declared clock only; requiring random is
            // beyond its envelope.
            let required =
                CompanionCapability.identity
                |> CompanionCapability.withEffect Effecting
                |> CompanionCapability.withDeterminism DeterminismSource.random

            match gate.Check auditId required with
            | CapabilityGateDecision.Denied denial ->
                Expect.stringContains
                    denial.Reason
                    (ComponentId.value auditId)
                    "the reason names the offending component"

                Expect.stringContains denial.Reason "random" "the reason names the undeclared determinism factor"
                Expect.equal denial.Component auditId "the denial carries the component id"
            | CapabilityGateDecision.Granted -> failtest "expected a beyond-envelope requirement to be denied"

        // ── default-deny: an undeclared component defaults to identity ─
        testCase "an undeclared component is denied any effecting access (default-deny)"
        <| fun _ ->
            let gate = CompositionCapabilityGate.create ignore signature

            // jobsId is absent from the signature → resolves to identity
            // (pure) → any effecting requirement is denied.
            match gate.Check jobsId effectingClock with
            | CapabilityGateDecision.Denied denial ->
                Expect.equal
                    denial.Declared
                    CompanionCapability.identity
                    "an undeclared component's envelope is the identity"

                Expect.stringContains
                    denial.Reason
                    (ComponentId.value jobsId)
                    "the reason names the undeclared component"
            | CapabilityGateDecision.Granted ->
                failtest "an undeclared component must be denied an effecting capability"

        // ── every deny is observable ──────────────────────────────────
        testCase "every deny is handed to the observer (never silent)"
        <| fun _ ->
            let observed = ResizeArray<CapabilityDenial>()
            let gate = CompositionCapabilityGate.create observed.Add signature

            // A granted check does not fire the observer.
            gate.Check auditId effectingClock |> ignore
            Expect.equal observed.Count 0 "a granted check is not observed"

            // A denied check fires it exactly once.
            gate.Check jobsId effectingClock |> ignore
            Expect.equal observed.Count 1 "a deny fires the observer once"
            Expect.equal observed[0].Component jobsId "the observed denial names the component"

        // ── the disabled gate grants everything (off = byte-identical) ─
        testCase "the disabled gate grants every check"
        <| fun _ ->
            let off = CompositionCapabilityGate.disabled

            // Even an undeclared component doing an effecting op is granted
            // when the sandbox is off — the pre-300 posture (GP 11).
            Expect.equal
                (off.Check jobsId effectingClock)
                CapabilityGateDecision.Granted
                "off grants an undeclared effecting op"

            Expect.equal
                (off.Check auditId CompanionCapability.devOnlyEffecting)
                CapabilityGateDecision.Granted
                "off grants regardless of declaration"

        // ── permits follows the Phase 296 lattice order ───────────────
        testCase "permits is the lattice dominance order"
        <| fun _ ->
            Expect.isTrue
                (CompositionCapabilityGate.permits effectingClock CompanionCapability.identity)
                "identity is below every envelope"

            Expect.isTrue
                (CompositionCapabilityGate.permits effectingClock effectingClock)
                "an envelope permits itself (idempotent)"

            Expect.isFalse
                (CompositionCapabilityGate.permits CompanionCapability.pure' effectingClock)
                "an effecting requirement is not permitted by a pure envelope"

        // ── guardInvoke: denies at the sandbox before the registry ────
        testCase "guardInvoke denies at the sandbox before reaching the registry"
        <| fun _ ->
            let gate = CompositionCapabilityGate.create ignore signature
            let reached = ref false

            // An allow-all Phase 266 registry — so the ONLY thing that can
            // deny is the Phase 300 sandbox in front of it.
            let registry = HostCapabilityRegistry.create ActionAuthorizer.allowAll

            registry.Register (CapabilityId "audit.export") (fun _ _ -> async {
                reached.Value <- true
                return Map.empty
            })

            // jobsId is undeclared → sandbox denies → registry never reached.
            let outcome =
                CompositionCapabilityGate.guardInvoke
                    gate
                    jobsId
                    effectingClock
                    registry
                    (CapabilityId "audit.export")
                    Map.empty
                    teamCtx
                |> Async.RunSynchronously

            match outcome with
            | HostCapabilityOutcome.Denied reason ->
                Expect.stringContains reason (ComponentId.value jobsId) "the sandbox reason names the component"
            | HostCapabilityOutcome.Completed _ -> failtest "the sandbox should have denied before the registry ran"

            Expect.isFalse reached.Value "the registry handler must not run when the sandbox denies"

        // ── guardInvoke: delegates to the registry when granted ───────
        testCase "guardInvoke delegates to the registry when the sandbox grants"
        <| fun _ ->
            let gate = CompositionCapabilityGate.create ignore signature
            let reached = ref false
            let registry = HostCapabilityRegistry.create ActionAuthorizer.allowAll

            registry.Register (CapabilityId "audit.export") (fun _ _ -> async {
                reached.Value <- true
                return Map [ "ok", "true" ]
            })

            // auditId declared effecting+clock → sandbox grants → registry runs.
            let outcome =
                CompositionCapabilityGate.guardInvoke
                    gate
                    auditId
                    effectingClock
                    registry
                    (CapabilityId "audit.export")
                    Map.empty
                    teamCtx
                |> Async.RunSynchronously

            match outcome with
            | HostCapabilityOutcome.Completed result ->
                Expect.equal (Map.tryFind "ok" result) (Some "true") "the registry handler ran and returned its result"
            | HostCapabilityOutcome.Denied reason -> failtestf "expected the registry to run; denied: %s" reason

            Expect.isTrue reached.Value "the registry handler runs when the sandbox grants"
    ]
module ToolUp.Platform.Tests.InProcess.SeamAuthorityTests

open System
open Expecto
open ToolUp.Platform

// ─── Phase 688 — seam-granularity module authority grants ─────────────
//
// Pins four properties, in the order the phase argues them:
//
//   1. **The additive floor.** With no `SeamGrantSignature`, a seam gate's
//      decisions are IDENTICAL to the Phase 300 gate's — proved over the
//      whole component × capability × seam cross-product rather than
//      spot-checked, because "additive by construction" is the claim the
//      whole phase rests on and a sampled floor is not a floor.
//   2. **Enforcement.** A declared set refuses everything outside it,
//      fail-closed, component- AND seam-named, observed exactly once, and
//      never before the Phase 300 effect check has had its say.
//   3. **Projection.** The declared sets diff by `ComponentId`, a widening
//      is critical, an incomparable swap counts as a widening, and the
//      wire form round-trips exactly so a golden file can hold it.
//   4. **The verified profile.** Declaration is mandatory there, a
//      half-declared composition is refused by name, and the refusal
//      reaches the audit path (and so the Phase 658 chained ledger).

// ── fixtures ──────────────────────────────────────────────────────────

let private effecting =
    CompanionCapability.identity
    |> CompanionCapability.withEffect Effecting
    |> CompanionCapability.withDeterminism DeterminismSource.externalState

let private reportsId = ComponentId.ofModule "reports"
let private auditId = ComponentId.forCompanionImpl "IAuditSink" "splunk-archive"
let private jobsId = ComponentId.forCompanionSlot "IJobScheduler"

let private entityStore = SeamId.ofInterface "IEntityStore"
let private auditSink = SeamId.ofInterface "IAuditSink"
let private secretStore = SeamId.ofInterface "ISecretStore"

/// `reports` and `audit` are declared effecting; `jobs` is deliberately
/// UNDECLARED, so the Phase 300 default-deny already covers it.
let private signature: CapabilitySignature =
    Map.ofList [ reportsId, effecting; auditId, effecting ]

/// `reports` may reach the entity store and the audit sink, nothing else.
/// `audit` declares an EMPTY set — "reaches no seam at all", which is a
/// real declaration and not a synonym for unrestricted.
let private grants: SeamGrantSignature =
    Map.ofList [
        reportsId, SeamGrant.ofInterfaces [ "IEntityStore"; "IAuditSink" ]
        auditId, SeamGrant.ofSeams []
    ]

let private teamCtx: AccessContext = {
    UserId = "u1"
    TeamId = Some "team-1"
    Subject = TeamMember("u1", "team-1")
    ModulePermissions = Map.empty
    ModuleExposure = Map.empty
    PlatformRole = None
}

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    /// The deny observer is fire-and-forget by contract, so a test
    /// asserting on the row waits for a write it deliberately did not
    /// await. Bounded rather than slept: returns the instant the row
    /// lands, and a genuine regression still fails rather than hangs.
    member _.WaitFor(count: int) =
        let deadline = DateTime.UtcNow.AddSeconds 5.0

        while recorded.Count < count && DateTime.UtcNow < deadline do
            Threading.Thread.Sleep 5

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

let private grant (componentId: ComponentId) (seams: SeamGrant) : ComponentSeamGrant = {
    GrantComponent = componentId
    GrantedSeams = seams
}

// ── 1. the additive floor ─────────────────────────────────────────────

let private additiveFloor =
    testList "additive floor" [

        // The claim the whole phase rests on: a composition that declares
        // no seam grants behaves EXACTLY as it did before Phase 688. Not
        // sampled — every component × capability pair the fixture can
        // produce, asserted equal between the shipped Phase 300 gate and
        // the lifted seam gate. If the lift ever grows an opinion of its
        // own, one of these 12 pairs catches it.
        testCase "an unrestricted lift reproduces the Phase 300 gate decision for every component and capability"
        <| fun _ ->
            let reference = CompositionCapabilityGate.create ignore signature

            let lifted =
                SeamAuthorityGate.unrestricted (CompositionCapabilityGate.create ignore signature)

            let capabilities = [
                CompanionCapability.identity
                effecting
                CompanionCapability.devOnlyEffecting
                CompanionCapability.distributedEffecting
            ]

            for componentId in [ reportsId; auditId; jobsId ] do
                for required in capabilities do
                    Expect.equal
                        (lifted.Check componentId required)
                        (reference.Check componentId required)
                        $"Check must be byte-identical for {ComponentId.value componentId}"

        // And the new member is the same decision when nothing is
        // declared: an undeclared component reaches every seam (GP 11).
        testCase "with no grants declared, CheckSeam equals Check for every seam"
        <| fun _ ->
            let reference = CompositionCapabilityGate.create ignore signature

            let lifted =
                SeamAuthorityGate.unrestricted (CompositionCapabilityGate.create ignore signature)

            for componentId in [ reportsId; auditId; jobsId ] do
                for seam in [ entityStore; auditSink; secretStore ] do
                    Expect.equal
                        (lifted.CheckSeam componentId seam effecting)
                        (reference.Check componentId effecting)
                        "an undeclared seam set refuses nothing"

        // The enabled seam gate is also additive on the components that
        // declared no grant: `jobs` and anything absent keeps the pre-688
        // posture even while its siblings are constrained.
        testCase "a component absent from the grant signature keeps reaching every seam"
        <| fun _ ->
            let gate = SeamAuthorityGate.create ignore signature grants

            // `jobs` is absent from BOTH maps: Phase 300 denies it the
            // effecting capability, and that is the denial we get — not a
            // seam denial invented on top.
            match gate.CheckSeam jobsId secretStore effecting with
            | CapabilityGateDecision.Denied denial ->
                Expect.stringContains
                    denial.Reason
                    "composition capability sandbox"
                    "the pre-688 effect denial is what an undeclared component still gets"
            | CapabilityGateDecision.Granted -> failtest "Phase 300 default-deny still applies"

            // A PURE requirement clears Phase 300 for the same undeclared
            // component, and with no grant declared the seam clears too.
            Expect.equal
                (gate.CheckSeam jobsId secretStore CompanionCapability.pure')
                CapabilityGateDecision.Granted
                "no grant declared means no seam refused"

        testCase "the disabled seam gate grants every check and every seam"
        <| fun _ ->
            let off = SeamAuthorityGate.disabled

            Expect.equal (off.Check jobsId effecting) CapabilityGateDecision.Granted "off grants every check"

            Expect.equal
                (off.CheckSeam jobsId secretStore CompanionCapability.devOnlyEffecting)
                CapabilityGateDecision.Granted
                "off grants every seam"

        testCase "an empty declaration is NOT unrestricted"
        <| fun _ ->
            Expect.isFalse (SeamGrant.isDeclared UnrestrictedSeams) "unrestricted declares nothing"
            Expect.isTrue (SeamGrant.isDeclared (SeamGrant.ofSeams [])) "an empty set is a real declaration"

            Expect.isTrue (SeamGrant.permits UnrestrictedSeams secretStore) "unrestricted permits everything"

            Expect.isFalse
                (SeamGrant.permits (SeamGrant.ofSeams []) secretStore)
                "a declaration of no seams permits nothing — the two ends of the order, never folded together"
    ]

// ── 2. enforcement ────────────────────────────────────────────────────

let private enforcement =
    testList "enforcement" [

        testCase "a declared seam resolves and an undeclared one is refused"
        <| fun _ ->
            let gate = SeamAuthorityGate.create ignore signature grants

            Expect.equal
                (gate.CheckSeam reportsId entityStore effecting)
                CapabilityGateDecision.Granted
                "a seam inside the declared set resolves"

            match gate.CheckSeam reportsId secretStore effecting with
            | CapabilityGateDecision.Denied denial ->
                Expect.stringContains denial.Reason (ComponentId.value reportsId) "the reason names the component"

                Expect.stringContains denial.Reason (SeamId.value secretStore) "and the seam it was refused"

                Expect.stringContains
                    denial.Reason
                    "IEntityStore"
                    "and the set it did declare, so the remedy is in the message"

                Expect.equal denial.Component reportsId "the denial carries the component id"
            | CapabilityGateDecision.Granted -> failtest "a seam outside the declared set must be refused"

        testCase "a component declaring no seams reaches nothing"
        <| fun _ ->
            let gate = SeamAuthorityGate.create ignore signature grants

            for seam in [ entityStore; auditSink; secretStore ] do
                match gate.CheckSeam auditId seam effecting with
                | CapabilityGateDecision.Denied _ -> ()
                | CapabilityGateDecision.Granted -> failtestf "declared{} must refuse %s" (SeamId.value seam)

            // …while its EFFECT envelope is untouched: the two checks are
            // independent axes, which is the point of not folding the
            // grant into `CompanionCapability`.
            Expect.equal
                (gate.Check auditId effecting)
                CapabilityGateDecision.Granted
                "the effect envelope is unchanged by a seam declaration"

        testCase "the Phase 300 effect check runs first and keeps its own reason"
        <| fun _ ->
            // `reports` declared effecting+external-state; requiring
            // dev-only readiness exceeds it. The seam is ALSO outside its
            // set, so a gate that checked seams first would misattribute
            // the refusal.
            let gate = SeamAuthorityGate.create ignore signature grants

            match gate.CheckSeam reportsId secretStore CompanionCapability.devOnlyEffecting with
            | CapabilityGateDecision.Denied denial ->
                Expect.stringContains
                    denial.Reason
                    "composition capability sandbox"
                    "an effect failure is reported against the effect axis it failed"

                Expect.isFalse
                    (denial.Reason.Contains "composition seam authority")
                    "and is not relabelled as a seam refusal"
            | CapabilityGateDecision.Granted -> failtest "a dev-only requirement exceeds a distributed-ready envelope"

        testCase "every seam refusal is observed exactly once (never silent)"
        <| fun _ ->
            let observed = ResizeArray<CapabilityDenial>()
            let gate = SeamAuthorityGate.create observed.Add signature grants

            gate.CheckSeam reportsId entityStore effecting |> ignore
            Expect.equal observed.Count 0 "a granted seam resolution is not observed"

            gate.CheckSeam reportsId secretStore effecting |> ignore
            Expect.equal observed.Count 1 "a seam refusal fires the observer once"
            Expect.equal observed[0].Component reportsId "the observed denial names the component"

            Expect.stringContains
                observed[0].Reason
                (SeamId.value secretStore)
                "and the seam, so an audit row is attributable"

        testCase "resolveSeam never runs the factory on a refusal"
        <| fun _ ->
            let gate = SeamAuthorityGate.create ignore signature grants
            let ran = ref 0

            let factory () =
                ran.Value <- ran.Value + 1
                "the-seam-instance"

            match SeamAuthorityGate.resolveSeam gate reportsId secretStore effecting factory with
            | Error denial -> Expect.stringContains denial.Reason (SeamId.value secretStore) "the refusal is typed"
            | Ok _ -> failtest "an undeclared seam must not resolve"

            Expect.equal ran.Value 0 "the factory must not run — fail-closed by construction, not by convention"

            match SeamAuthorityGate.resolveSeam gate reportsId entityStore effecting factory with
            | Ok value -> Expect.equal value "the-seam-instance" "a declared seam resolves through the factory"
            | Error denial -> failtestf "expected the declared seam to resolve; refused: %s" denial.Reason

            Expect.equal ran.Value 1 "the factory ran exactly once, for the granted resolution"

        testCase "guardSeamInvoke refuses before the Phase 266 registry"
        <| fun _ ->
            let gate = SeamAuthorityGate.create ignore signature grants
            let reached = ref false

            // Allow-all registry, so the ONLY thing that can refuse is the
            // composition-level gate in front of it.
            let registry = HostCapabilityRegistry.create ActionAuthorizer.allowAll

            registry.Register (CapabilityId "secrets.read") (fun _ _ -> async {
                reached.Value <- true
                return Map.empty
            })

            let refused =
                SeamAuthorityGate.guardSeamInvoke
                    gate
                    reportsId
                    secretStore
                    effecting
                    registry
                    (CapabilityId "secrets.read")
                    Map.empty
                    teamCtx
                |> Async.RunSynchronously

            match refused with
            | HostCapabilityOutcome.Denied reason ->
                Expect.stringContains reason (SeamId.value secretStore) "the refusal names the seam"
            | HostCapabilityOutcome.Completed _ -> failtest "the seam gate should have refused before the registry"

            Expect.isFalse reached.Value "the registry handler must not run when the seam is refused"

            // …and delegates when the seam IS declared.
            let allowed =
                SeamAuthorityGate.guardSeamInvoke
                    gate
                    reportsId
                    entityStore
                    effecting
                    registry
                    (CapabilityId "secrets.read")
                    Map.empty
                    teamCtx
                |> Async.RunSynchronously

            match allowed with
            | HostCapabilityOutcome.Completed _ -> Expect.isTrue reached.Value "the registry runs when the seam clears"
            | HostCapabilityOutcome.Denied reason -> failtestf "expected the registry to run; refused: %s" reason
    ]

// ── the grant algebra ─────────────────────────────────────────────────

let private algebra =
    testList "SeamGrant algebra" [

        testCase "resolve falls back to unrestricted for an absent component"
        <| fun _ ->
            Expect.equal
                (SeamGrant.resolve grants jobsId)
                UnrestrictedSeams
                "an absent id never changes behaviour (GP 11)"

            Expect.equal
                (SeamGrant.resolve Map.empty reportsId)
                UnrestrictedSeams
                "and an empty signature restricts nobody"

        testCase "covers is the containment order, with unrestricted on top"
        <| fun _ ->
            let pair = SeamGrant.ofSeams [ entityStore; auditSink ]
            let single = SeamGrant.ofSeams [ entityStore ]

            Expect.isTrue (SeamGrant.covers UnrestrictedSeams pair) "unrestricted covers every declaration"
            Expect.isFalse (SeamGrant.covers pair UnrestrictedSeams) "no declaration covers unrestricted"
            Expect.isTrue (SeamGrant.covers pair single) "a superset covers a subset"
            Expect.isFalse (SeamGrant.covers single pair) "a subset does not cover a superset"
            Expect.isTrue (SeamGrant.covers pair pair) "covering is reflexive"

        testCase "join is a semilattice with unrestricted absorbing"
        <| fun _ ->
            let a = SeamGrant.ofSeams [ entityStore ]
            let b = SeamGrant.ofSeams [ auditSink ]

            Expect.equal (SeamGrant.join a b) (SeamGrant.ofSeams [ entityStore; auditSink ]) "join unions the sets"
            Expect.equal (SeamGrant.join a b) (SeamGrant.join b a) "commutative"
            Expect.equal (SeamGrant.join a a) a "idempotent"
            Expect.equal (SeamGrant.join a UnrestrictedSeams) UnrestrictedSeams "unrestricted absorbs"

            Expect.equal
                (SeamGrant.join a (SeamGrant.ofSeams []))
                a
                "the empty declaration is the identity — the BOTTOM of this order, unlike CompanionCapability.identity"

        testCase "the wire parts round-trip both cases exactly"
        <| fun _ ->
            for original in
                [
                    UnrestrictedSeams
                    SeamGrant.ofSeams []
                    SeamGrant.ofSeams [ auditSink; entityStore ]
                ] do
                let restored =
                    SeamGrant.ofWireParts (SeamGrant.kindLabel original) (SeamGrant.seamLabels original)

                Expect.equal restored original $"round-trip of {SeamGrant.render original}"

        testCase "an unrecognised persisted kind reads as unrestricted, never as a fabricated restriction"
        <| fun _ ->
            Expect.equal
                (SeamGrant.ofWireParts "who-knows" [ "IEntityStore" ])
                UnrestrictedSeams
                "an unreadable grant must not refuse resolutions nobody declared against"

        testCase "seam labels are sorted ordinally, so a golden file is order-independent"
        <| fun _ ->
            Expect.equal
                (SeamGrant.seamLabels (SeamGrant.ofSeams [ secretStore; auditSink; entityStore ]))
                [ "IAuditSink"; "IEntityStore"; "ISecretStore" ]
                "deterministic projection"
    ]

// ── 3. the manifest projection ────────────────────────────────────────

let private projection =
    testList "manifest projection" [

        testCase "the surface is derived from the signature in ComponentId order"
        <| fun _ ->
            let surface = SeamAuthoritySurface.ofSignature grants

            let ids = surface.Granted |> List.map (fun e -> ComponentId.value e.GrantComponent)

            Expect.equal
                ids
                [ ComponentId.value auditId; ComponentId.value reportsId ]
                "every declared component appears, keyed by its stable id"

            Expect.equal
                ids
                (ids |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))
                "ordinally ordered, so declaration order never shows"

            Expect.equal
                (SeamAuthoritySurface.grantOf reportsId surface)
                (SeamGrant.ofInterfaces [ "IEntityStore"; "IAuditSink" ])
                "each component's declared set is recoverable by id"

            Expect.equal
                (SeamAuthoritySurface.grantOf jobsId surface)
                UnrestrictedSeams
                "an undeclared component reads as unrestricted — what the gate resolves"

        testCase "a surface diffed against itself is empty regardless of declaration order"
        <| fun _ ->
            let a = SeamAuthoritySurface.ofSignature grants

            let reversed = { Granted = List.rev a.Granted }

            Expect.isTrue
                (SeamAuthoritySurface.isEmptyDelta (SeamAuthoritySurface.diff a reversed))
                "the diff keys on ComponentId, never on list position"

            Expect.equal
                (SeamAuthoritySurface.severity (SeamAuthoritySurface.diff a a))
                NoAuthorizationDrift
                "an identical pair is no drift"

        testCase "a widened grant set is CRITICAL and named in the rendered delta"
        <| fun _ ->
            let before = {
                Granted = [ grant reportsId (SeamGrant.ofSeams [ entityStore ]) ]
            }

            let after = {
                Granted = [ grant reportsId (SeamGrant.ofSeams [ entityStore; secretStore ]) ]
            }

            let delta = SeamAuthoritySurface.diff before after

            Expect.equal delta.GrantsWidened.Length 1 "the widening is caught"
            Expect.isEmpty delta.GrantsNarrowed "and not double-reported as a narrowing"

            Expect.equal
                (SeamAuthoritySurface.severity delta)
                CriticalAuthorizationDrift
                "reaching more is the outbound twin of a weakened requirement"

            let rendered = SeamAuthoritySurface.renderDelta delta
            Expect.stringContains rendered "CRITICAL" "the severity leads"
            Expect.stringContains rendered "WIDENED" "and the class is named"
            Expect.stringContains rendered "ISecretStore" "and the seam that was added"

        testCase "a narrowed grant set is reviewable, not critical"
        <| fun _ ->
            let before = {
                Granted = [ grant reportsId (SeamGrant.ofSeams [ entityStore; secretStore ]) ]
            }

            let after = {
                Granted = [ grant reportsId (SeamGrant.ofSeams [ entityStore ]) ]
            }

            let delta = SeamAuthoritySurface.diff before after

            Expect.equal delta.GrantsNarrowed.Length 1 "the narrowing is caught"

            Expect.equal
                (SeamAuthoritySurface.severity delta)
                ReviewableAuthorizationDrift
                "reaching less moved, but grew nothing"

        testCase "declared -> unrestricted is a widening, not merely a change"
        <| fun _ ->
            let before = {
                Granted = [ grant reportsId (SeamGrant.ofSeams [ entityStore ]) ]
            }

            let after = {
                Granted = [ grant reportsId UnrestrictedSeams ]
            }

            let delta = SeamAuthoritySurface.diff before after

            Expect.equal delta.GrantsWidened.Length 1 "dropping a declaration is the maximal widening"

            Expect.equal
                (SeamAuthoritySurface.severity delta)
                CriticalAuthorizationDrift
                "a component that stops declaring reaches everything again"

        testCase "an incomparable swap counts as a widening (the conservative reading)"
        <| fun _ ->
            let before = {
                Granted = [ grant reportsId (SeamGrant.ofSeams [ entityStore ]) ]
            }

            let after = {
                Granted = [ grant reportsId (SeamGrant.ofSeams [ secretStore ]) ]
            }

            let delta = SeamAuthoritySurface.diff before after

            Expect.equal
                delta.GrantsWidened.Length
                1
                "a swapped seam is not provably at most what it replaced — the same rule comparePosture takes"

        testCase "a newly-declared component that declares itself unrestricted is CRITICAL"
        <| fun _ ->
            let delta =
                SeamAuthoritySurface.diff SeamAuthoritySurface.empty {
                    Granted = [ grant reportsId UnrestrictedSeams ]
                }

            Expect.equal delta.GrantsAdded.Length 1 ""

            Expect.equal
                (SeamAuthoritySurface.severity delta)
                CriticalAuthorizationDrift
                "the outbound twin of a new anonymous-reachable endpoint"

            Expect.stringContains
                (SeamAuthoritySurface.renderDelta delta)
                "[CRITICAL unrestricted]"
                "marked inline so the reviewer's eye lands on it"

        testCase "an added component with a declared set is reviewable, not critical"
        <| fun _ ->
            let delta =
                SeamAuthoritySurface.diff SeamAuthoritySurface.empty {
                    Granted = [ grant reportsId (SeamGrant.ofSeams [ entityStore ]) ]
                }

            Expect.equal
                (SeamAuthoritySurface.severity delta)
                ReviewableAuthorizationDrift
                "declaring a bounded set is the change this phase wants to encourage"

        testCase "the wire projection round-trips exactly, including the empty declaration"
        <| fun _ ->
            let surface = {
                Granted = [
                    grant reportsId (SeamGrant.ofSeams [ entityStore; auditSink ])
                    grant auditId (SeamGrant.ofSeams [])
                    grant jobsId UnrestrictedSeams
                ]
            }

            let restored = SeamAuthoritySurface.ofWire (SeamAuthoritySurface.toWire surface)

            Expect.isTrue
                (SeamAuthoritySurface.isEmptyDelta (SeamAuthoritySurface.diff surface restored))
                "a golden file compares a committed baseline against a live derivation through diff"

            Expect.equal
                (SeamAuthoritySurface.grantOf auditId restored)
                (SeamGrant.ofSeams [])
                "declared{} survives the round trip as itself, not as unrestricted"

        testCase "the empty delta renders readably rather than blank"
        <| fun _ ->
            Expect.equal
                (SeamAuthoritySurface.renderDelta SeamAuthoritySurface.emptyDelta)
                "(no seam-authority differences)"
                "a CI gate never prints an empty string"
    ]

// ── 4. the verified profile binding ───────────────────────────────────

let private profileBinding =
    testList "verified profile binding" [

        testCase "the standard profile with no grants leaves every seam open (GP 11)"
        <| fun _ ->
            match VerifiedCompositionProfile.resolveSeamGate CompositionProfile.Standard ignore None None with
            | Ok gate ->
                Expect.equal
                    (gate.CheckSeam reportsId secretStore effecting)
                    CapabilityGateDecision.Granted
                    "the pre-688 passthrough, unchanged"
            | Error refusal -> failtestf "expected the disabled seam gate, got %A" refusal

        testCase "the standard profile with an envelope but no grants keeps its effect decisions and refuses no seam"
        <| fun _ ->
            let reference = CompositionCapabilityGate.create ignore signature

            match
                VerifiedCompositionProfile.resolveSeamGate CompositionProfile.Standard ignore (Some signature) None
            with
            | Ok gate ->
                Expect.equal
                    (gate.Check reportsId effecting)
                    CapabilityGateDecision.Granted
                    "the Phase 300 envelope still applies"

                Expect.equal
                    (gate.CheckSeam reportsId secretStore effecting)
                    CapabilityGateDecision.Granted
                    "and no seam is refused until one is declared"

                Expect.equal
                    (gate.Check jobsId effecting)
                    (reference.Check jobsId effecting)
                    "an undeclared component's Phase 300 denial is unchanged"
            | Error refusal -> failtestf "expected a gate, got %A" refusal

        testCase "the verified profile refuses a composition with no seam grants at all"
        <| fun _ ->
            match
                VerifiedCompositionProfile.resolveSeamGate CompositionProfile.Verified ignore (Some signature) None
            with
            | Error(SeamGrantsUndeclared []) ->
                Expect.stringContains
                    (CompositionProfileRefusal.describe (SeamGrantsUndeclared []))
                    "SeamGrantSignature"
                    "the refusal names what to supply"
            | other ->
                failtestf
                    "a mandatory seam check with nothing declared would permit every seam while presenting as enforcement: %A"
                    other

        testCase "the verified profile names every component that declared no seam set"
        <| fun _ ->
            // `reports` declared; `audit` did not — a half-declared
            // composition is the state that reads as enforced and is not.
            let partial': SeamGrantSignature =
                Map.ofList [ reportsId, SeamGrant.ofSeams [ entityStore ] ]

            match
                VerifiedCompositionProfile.resolveSeamGate
                    CompositionProfile.Verified
                    ignore
                    (Some signature)
                    (Some partial')
            with
            | Error(SeamGrantsUndeclared undeclared) ->
                Expect.equal undeclared [ ComponentId.value auditId ] "exactly the component that declared nothing"

                Expect.stringContains
                    (CompositionProfileRefusal.describe (SeamGrantsUndeclared undeclared))
                    (ComponentId.value auditId)
                    "and the message names it, so the fix does not need a search"
            | other -> failtestf "expected a half-declared composition to be refused, got %A" other

        testCase "the verified profile composes a fully-declared composition and enforces it"
        <| fun _ ->
            match
                VerifiedCompositionProfile.resolveSeamGate
                    CompositionProfile.Verified
                    ignore
                    (Some signature)
                    (Some grants)
            with
            | Ok gate ->
                Expect.equal
                    (gate.CheckSeam reportsId entityStore effecting)
                    CapabilityGateDecision.Granted
                    "a declared seam resolves"

                match gate.CheckSeam reportsId secretStore effecting with
                | CapabilityGateDecision.Denied _ -> ()
                | CapabilityGateDecision.Granted -> failtest "an undeclared seam must be refused under the profile"
            | Error refusal -> failtestf "a fully-declared composition must compose; got %A" refusal

        testCase "requiresSeamGrants tracks the profile"
        <| fun _ ->
            Expect.isFalse
                (CompositionProfile.requiresSeamGrants CompositionProfile.Standard)
                "declaration is optional outside the profile"

            Expect.isTrue (CompositionProfile.requiresSeamGrants CompositionProfile.Verified) "and mandatory inside it"

        testCase "a seam refusal reaches the audit path (and so the chained ledger)"
        <| fun _ ->
            let auditLog = RecordingAuditLog()

            let gate =
                match
                    VerifiedCompositionProfile.auditedSeamGate
                        (auditLog :> IAuditLog)
                        "_platform"
                        CompositionProfile.Verified
                        (Some signature)
                        (Some grants)
                with
                | Ok g -> g
                | Error refusal -> failtestf "expected a gate, got %A" refusal

            match gate.CheckSeam reportsId secretStore effecting with
            | CapabilityGateDecision.Denied denial ->
                Expect.equal denial.Component reportsId "fail-closed and component-named"
            | CapabilityGateDecision.Granted -> failtest "an undeclared seam is refused"

            auditLog.WaitFor 1

            match auditLog.Events with
            | [ scopeId, CompositionCapabilityRefused payload ] ->
                Expect.equal scopeId "_platform" "recorded against the platform scope"
                Expect.stringContains payload.Component "reports" "the module is named"

                Expect.stringContains
                    payload.Reason
                    (SeamId.value secretStore)
                    "and the seam — reusing CapabilityDenial is what puts a seam refusal on the existing 657/658 path"

                Expect.equal payload.Profile "verified" ""
            | other -> failtestf "expected exactly one refusal row, got %A" other
    ]

let tests =
    testList "SeamAuthority" [ additiveFloor; enforcement; algebra; projection; profileBinding ]
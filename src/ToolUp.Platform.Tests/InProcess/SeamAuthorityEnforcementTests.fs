module ToolUp.Platform.Tests.InProcess.SeamAuthorityEnforcementTests

open Expecto
open System
open ToolUp.Platform
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.VectorisationTypes

// ─── Phase 691 — the seam gate's first production call site ───────────
//
// Phase 688 shipped `ISeamAuthorityGate` and recorded that nothing in a
// composition called it. This file pins the wiring that closes that, in
// the order the phase argues it:
//
//   1. **The derivation is not a second map.** `reachOf` agrees, entry
//      for entry, with the shipped `ModuleSurface` `Needs` projection it
//      reads — asserted by recomputing the expected set from
//      `ModuleSurface.describe` rather than from a literal, so a change
//      to the declaration→substrate rules cannot leave the two
//      disagreeing.
//   2. **The additive floor, probed over REAL compositions.** Not
//      "`disabled` grants everything" (Phase 688 already proved that of
//      the gate); this is the wired path over a composition of eight
//      genuine module shapes, under every gate a composition with no
//      declared seam grants can produce. The claim the phase rests on is
//      that switching this on changes nothing until something is
//      declared, and a floor proved on a toy module is not a floor.
//   3. **Enforcement, one perturbation per reached seam.** For each seam
//      the reference module genuinely reaches, drop exactly that seam
//      from an otherwise-complete grant and assert exactly that one
//      refusal, named. Ten perturbations, derived from the reach rather
//      than listed — so a new registration field that implies new
//      substrate grows the suite by itself.
//   4. **The profile binds, and refusals reach the ledger.** Under
//      `Verified` an undeclared envelope or a half-declared grant
//      signature is refused before any reach is checked; a real refusal
//      arrives on the Phase 658 audit path as
//      `CompositionCapabilityRefused`.

// ── fixtures ──────────────────────────────────────────────────────────

type private NoopJobHandler() =
    interface IJobHandler with
        member _.Execute _ = async { return JobResult.Success }

let private dataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process =
        fun _ -> async {
            return
                { TypeName = id; Payload = "{}" },
                {
                    FileName = ""
                    DataType = id
                    ProcessedAt = DateTime.UnixEpoch
                    Info = None
                    Error = None
                }
        }
}

let private vectorisation (id: string) : VectorisationHandler = {
    DataTypeId = id
    Vectorise = fun _ -> []
    Summarise = None
}

let private toolDefinition (name: string) : AIToolDefinition = {
    Name = name
    Description = "enforcement fixture tool"
    Parameters = []
    SourceModule = "reference"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
}

let private configSchema: ModuleConfigSchema = {
    Fields = [
        {
            Key = "retention-days"
            DisplayName = "Retention (days)"
            Description = None
            Kind = ConfigFieldKind.Int(Some 1, Some 365)
            Required = false
            DefaultJson = "30"
        }
    ]
}

let private signal: Metrics.MetricDefinition = {
    Name = "reference.processed_total"
    Kind = Metrics.Counter
    Description = "records processed"
    Unit = "1"
    Tags = []
}

let private groundingMetric: Grounding.MetricDefinition = {
    Id = "revenue"
    Name = "Revenue"
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = Grounding.HigherIsBetter
    DisplayFormat = "N0"
    Staleness = Grounding.UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private groundingSubject: Grounding.SubjectDefinition = {
    Id = "product"
    Name = "Product"
    Levels = [ "root" ]
    Calendar = None
}

/// Every `ServerModule` registration field populated, so the reach is
/// measured against a full surface rather than a convenient subset.
let private referenceModule () : ServerModule =
    ServerModule.create "Reference"
    |> ServerModule.withHandlers [ Giraffe.Core.setStatusCode 200 ]
    |> ServerModule.withDataTypes [ dataType "SalesData" ]
    |> ServerModule.withVectorisation [ vectorisation "SalesData" ]
    |> ServerModule.withConfig configSchema
    |> ServerModule.withQueryHandlers [
        {
            QueryKey = "latest-analysis"
            Handle = fun _ -> async { return "" }
        }
    ]
    |> ServerModule.withAITools [ toolDefinition "reference.run", (fun _ _ -> async { return "" }) ]
    |> ServerModule.withMetrics [ signal ]
    |> ServerModule.withRoutePrefix "/api/reference/"
    |> ServerModule.withJobHandler ("reference-scan", NoopJobHandler(), CronTrigger "0 8 * * *")
    |> ServerModule.withBindingStamp (MacStamp("anchor-1", "tag"))
    |> ServerModule.withComponentId "reference-service"
    |> ServerModule.declareMetrics [ groundingMetric ]
    |> ServerModule.declareSubjects [ groundingSubject ]

/// A composition of genuinely different module shapes, so the floor is
/// probed over the range a real deployment spans rather than one module:
/// a module that declares NOTHING (reaches nothing), single-field
/// modules that reach exactly one seam each, one that reaches two from a
/// single field, and the full reference module.
let private realComposition () : ServerModule list = [
    ServerModule.create "Empty"
    ServerModule.create "RoutesOnly"
    |> ServerModule.withRoutePrefix "/api/routes-only/"
    ServerModule.create "JobsOnly"
    |> ServerModule.withJobHandler ("scan", NoopJobHandler(), CronTrigger "0 8 * * *")
    ServerModule.create "QueriesOnly"
    |> ServerModule.withQueryHandlers [
        {
            QueryKey = "q"
            Handle = fun _ -> async { return "" }
        }
    ]
    ServerModule.create "ConfigOnly" |> ServerModule.withConfig configSchema
    ServerModule.create "DataOnly" |> ServerModule.withDataTypes [ dataType "Rows" ]
    // One field, two implied seams — the shape a per-field assumption
    // of "one declaration, one seam" would get wrong.
    ServerModule.create "VectorOnly"
    |> ServerModule.withDataTypes [ dataType "Docs" ]
    |> ServerModule.withVectorisation [ vectorisation "Docs" ]
    referenceModule ()
]

let private componentOf (m: ServerModule) : ComponentId =
    (SeamAuthorityEnforcement.reachOf m).ReachComponent

/// The seams `ModuleSurface` says a module needs, recomputed here from
/// the projection rather than copied from a literal.
let private expectedSeams (m: ServerModule) : Set<SeamId> =
    (ModuleSurface.describe m).Needs
    |> List.filter (fun e -> e.Kind = "substrate")
    |> List.map (fun e -> SeamId.ofInterface e.Key)
    |> Set.ofList

/// An effect envelope declared for every module in the composition, so
/// the Phase 300 half never has an opinion of its own in these tests.
let private effectingSignature (modules: ServerModule list) : CapabilitySignature =
    let effecting =
        CompanionCapability.identity
        |> CompanionCapability.withEffect Effecting
        |> CompanionCapability.withDeterminism DeterminismSource.externalState

    modules |> List.map (fun m -> componentOf m, effecting) |> Map.ofList

/// A grant signature that declares each module's reach EXACTLY — the
/// composition a deployment lands on after reading its own manifest.
let private exactGrants (modules: ServerModule list) : SeamGrantSignature =
    modules
    |> List.map (fun m ->
        let entry = SeamAuthorityEnforcement.reachOf m
        entry.ReachComponent, SeamGrant.ofSeams entry.ReachedSeams)
    |> Map.ofList

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    let gate = obj ()
    member _.Events = lock gate (fun () -> List.ofSeq recorded)

    /// The deny observer is fire-and-forget by contract (`Async.Start`
    /// onto the thread pool), so a test asserting on the rows waits for
    /// writes it deliberately did not await. Event-driven, not polled:
    /// `Record` pulses the monitor, so this returns the instant the
    /// `count`-th row lands, and the cap only bites when the rows are
    /// not coming at all. A timeout fails HERE, naming what did arrive
    /// — a 5s wall-clock poll once expired under machine load and the
    /// failure blamed the Phase 658 ledger claim instead of the
    /// scheduler (2026-08-24, VerifyAll beside a second pack).
    member _.WaitFor(count: int) =
        let cap = TimeSpan.FromSeconds 30.0
        let sw = Diagnostics.Stopwatch.StartNew()

        lock gate (fun () ->
            while recorded.Count < count && sw.Elapsed < cap do
                let remaining = cap - sw.Elapsed

                if remaining > TimeSpan.Zero then
                    Threading.Monitor.Wait(gate, remaining) |> ignore

            if recorded.Count < count then
                failtestf
                    "audit wait: %d of %d expected row(s) arrived within %.0fs — with an event-driven wait this long the deny observer's write never happened (it is not merely late); the ledger assertion after this wait has NOT been evaluated"
                    recorded.Count
                    count
                    cap.TotalSeconds)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            lock gate (fun () ->
                recorded.Add(scopeId, audit)
                Threading.Monitor.PulseAll gate)
        }

        member _.GetAuditTrail(_, _, _) = async { return lock gate (fun () -> recorded |> Seq.map snd |> List.ofSeq) }

// ── 1. the derivation is the shipped projection, not a second map ─────

let private derivation =
    testList "reach derivation" [

        testCase "reachOf reports exactly the substrate ModuleSurface says the module needs"
        <| fun _ ->
            for m in realComposition () do
                let reached = (SeamAuthorityEnforcement.reachOf m).ReachedSeams |> Set.ofList

                Expect.equal
                    reached
                    (expectedSeams m)
                    (sprintf
                        "module '%s' — reachOf must agree with the ModuleSurface Needs projection it reads, or the SDK carries two declaration-to-substrate maps"
                        m.Name)

        // The fixture is a list of pipelines, and a pipeline whose `|>`
        // lands at the element indentation silently FOLDS into its
        // predecessor rather than starting a new element — a reflow can
        // shrink the probe without failing anything. Pin the count.
        testCase "the probe composition really is eight distinct modules"
        <| fun _ ->
            let modules = realComposition ()

            Expect.hasLength
                modules
                8
                "a reflow that folds two list elements together would silently shrink the floor probe"

            Expect.hasLength
                (modules |> List.map _.Name |> List.distinct)
                8
                "each probe module must be distinct, or the floor is probed over fewer shapes than it claims"

        testCase "a module that declares nothing reaches nothing"
        <| fun _ ->
            Expect.isEmpty
                (SeamAuthorityEnforcement.reachOf (ServerModule.create "Empty")).ReachedSeams
                "a module with no registrations implies no substrate, so it reaches no seam"

        testCase "one registration field can imply two seams"
        <| fun _ ->
            let m =
                ServerModule.create "VectorOnly"
                |> ServerModule.withVectorisation [ vectorisation "Docs" ]

            let reached = (SeamAuthorityEnforcement.reachOf m).ReachedSeams |> Set.ofList

            Expect.isTrue
                (reached.Contains(SeamId.ofInterface "IEmbeddingProvider")
                 && reached.Contains(SeamId.ofInterface "IVectorStore"))
                "vectorisation implies BOTH the embedding provider and the vector store"

        testCase "the reach is keyed by the module's declared ComponentId, not its display name"
        <| fun _ ->
            Expect.equal
                (SeamAuthorityEnforcement.reachOf (referenceModule ())).ReachComponent
                (ComponentId.ofModule "reference-service")
                "the reach must key by the Phase 279 id the grant signature is keyed by"

        testCase "the reference module's reach is the full substrate set"
        <| fun _ ->
            // Recomputed rather than literal — but the COUNT is pinned,
            // so a rule silently dropping out of the projection fails
            // here rather than quietly shrinking what is enforced.
            let reached = (SeamAuthorityEnforcement.reachOf (referenceModule ())).ReachedSeams

            Expect.equal
                (List.length reached)
                (Set.count (Set.ofList reached))
                "the reach must be deduplicated — two fields implying IFactStore is one seam"

            Expect.isGreaterThan
                (List.length reached)
                7
                "a module populating every registration field reaches the SDK's substrate broadly; a sudden collapse means the projection lost rules"

        testCase "the reach of a composition preserves module declaration order"
        <| fun _ ->
            let modules = realComposition ()

            Expect.equal
                (SeamAuthorityEnforcement.reach modules |> List.map _.ReachComponent)
                (modules |> List.map componentOf)
                "a refusal report must read in the order the composition root declares its modules"
    ]

// ── 2. the additive floor, over a real composition ────────────────────

let private additiveFloor =
    testList "additive floor over real compositions" [

        // The claim the phase rests on. Every gate a composition with no
        // DECLARED seam grants can produce must admit every reach of
        // every module shape — so switching the call site on changes
        // nothing at all until a grant signature exists.
        testCase "no declared grants admits every reach of every module shape"
        <| fun _ ->
            let modules = realComposition ()
            let signature = effectingSignature modules

            let floorGates = [
                "SeamAuthorityGate.disabled", SeamAuthorityGate.disabled
                "unrestricted over a disabled Phase 300 gate",
                SeamAuthorityGate.unrestricted CompositionCapabilityGate.disabled
                "unrestricted over an ENABLED Phase 300 gate",
                SeamAuthorityGate.unrestricted (CompositionCapabilityGate.create ignore signature)
                "an enabled seam gate over an EMPTY grant signature",
                SeamAuthorityGate.create ignore signature Map.empty
            ]

            for label, gate in floorGates do
                Expect.equal
                    (SeamAuthorityEnforcement.verify gate modules)
                    (Ok())
                    (sprintf "%s must admit every reach — this is the GP 11 floor, not a default" label)

        // The `seamOnly` requirement is load-bearing: `CheckSeam` runs
        // the Phase 300 EFFECT check first, so a call site passing
        // anything above the lattice bottom would turn a seam question
        // into an effect denial. Here NO component is in the capability
        // signature at all, so each resolves to `identity` (pure) — a
        // requirement of `Effecting` would deny every one of them.
        testCase "an undeclared effect envelope does not turn a permitted reach into an effect denial"
        <| fun _ ->
            let modules = realComposition ()
            let gate = SeamAuthorityGate.create ignore Map.empty (exactGrants modules)

            Expect.equal
                (SeamAuthorityEnforcement.verify gate modules)
                (Ok())
                "the seam call site asks only about reach; the effect envelope is Phase 300's own question at its own call sites"

        testCase "a composition whose grants exactly match its reach is admitted"
        <| fun _ ->
            let modules = realComposition ()
            let signature = effectingSignature modules
            let gate = SeamAuthorityGate.create ignore signature (exactGrants modules)

            Expect.equal
                (SeamAuthorityEnforcement.verify gate modules)
                (Ok())
                "declaring exactly what you reach must be admitted, or the declaration is unusable"

        testCase "a composition with no modules is admitted under an enforcing gate"
        <| fun _ ->
            let gate =
                SeamAuthorityGate.create
                    ignore
                    Map.empty
                    (Map.ofList [ ComponentId.ofModule "x", SeamGrant.ofSeams [] ])

            Expect.equal (SeamAuthorityEnforcement.verify gate []) (Ok()) "nothing composed reaches nothing"

        testCase "verifying does not observe a denial when everything is granted"
        <| fun _ ->
            let modules = realComposition ()
            let observed = ResizeArray<CapabilityDenial>()

            let gate =
                SeamAuthorityGate.create observed.Add (effectingSignature modules) (exactGrants modules)

            SeamAuthorityEnforcement.verify gate modules |> ignore

            Expect.isEmpty observed "an admitted composition must put nothing on the audit path"
    ]

// ── 3. enforcement — one perturbation per reached seam ────────────────

let private enforcement =
    testList "enforcement" [

        // Derived, not listed: one perturbation per seam the reference
        // module genuinely reaches. Drop exactly one from an otherwise
        // complete grant; exactly that one must be refused, by name.
        testCase "dropping any single reached seam refuses exactly that seam"
        <| fun _ ->
            let m = referenceModule ()
            let componentId = componentOf m
            let reached = (SeamAuthorityEnforcement.reachOf m).ReachedSeams

            Expect.isGreaterThan
                (List.length reached)
                1
                "the perturbation suite needs a module that reaches more than one seam"

            for dropped in reached do
                let kept = reached |> List.filter (fun s -> s <> dropped)
                let grants = Map.ofList [ componentId, SeamGrant.ofSeams kept ]
                let gate = SeamAuthorityGate.create ignore (effectingSignature [ m ]) grants

                match SeamAuthorityEnforcement.verify gate [ m ] with
                | Ok() ->
                    failtestf
                        "dropping '%s' from the grant must refuse the reach — an admitted undeclared reach is the gap Phase 691 exists to close"
                        (SeamId.value dropped)
                | Error denials ->
                    Expect.hasLength
                        denials
                        1
                        (sprintf "dropping '%s' must refuse exactly that seam, not a cascade" (SeamId.value dropped))

                    let denial = List.head denials

                    Expect.equal denial.Component componentId "the refusal must name the reaching component"

                    Expect.stringContains
                        denial.Reason
                        (SeamId.value dropped)
                        "the refusal reason must name the refused seam, so the remedy is in the message"

        testCase "an empty declared grant refuses every seam the module reaches"
        <| fun _ ->
            let m = referenceModule ()
            let reached = (SeamAuthorityEnforcement.reachOf m).ReachedSeams
            let grants = Map.ofList [ componentOf m, SeamGrant.ofSeams [] ]
            let gate = SeamAuthorityGate.create ignore (effectingSignature [ m ]) grants

            match SeamAuthorityEnforcement.verify gate [ m ] with
            | Ok() -> failtest "an empty declaration is a real declaration — it must refuse every reach"
            | Error denials ->
                Expect.hasLength
                    denials
                    (List.length reached)
                    "every reached seam must be refused, and the report must not short-circuit at the first"

        testCase "refusals are collected across modules, not stopped at the first"
        <| fun _ ->
            let modules =
                realComposition ()
                |> List.filter (fun m -> not (SeamAuthorityEnforcement.reachOf m).ReachedSeams.IsEmpty)

            let grants =
                modules |> List.map (fun m -> componentOf m, SeamGrant.ofSeams []) |> Map.ofList

            let gate = SeamAuthorityGate.create ignore (effectingSignature modules) grants

            let expectedTotal =
                modules
                |> List.sumBy (fun m -> List.length (SeamAuthorityEnforcement.reachOf m).ReachedSeams)

            match SeamAuthorityEnforcement.verify gate modules with
            | Ok() -> failtest "a composition where nothing is declared must be refused"
            | Error denials ->
                Expect.hasLength
                    denials
                    expectedTotal
                    "an operator fixing a composition wants every refusal at once, not the first one repeatedly"

        testCase "a module absent from the grant signature is unrestricted, not refused"
        <| fun _ ->
            // GP 11 at the component granularity: the verified profile,
            // not this constructor, is what makes declaration mandatory.
            let modules = realComposition ()
            let one = List.last modules
            let grants = Map.ofList [ componentOf one, SeamGrant.ofSeams [] ]
            let gate = SeamAuthorityGate.create ignore (effectingSignature modules) grants

            match SeamAuthorityEnforcement.verify gate modules with
            | Ok() -> failtest "the module that DID declare an empty set must still be refused"
            | Error denials ->
                Expect.all
                    denials
                    (fun d -> d.Component = componentOf one)
                    "only the component that declared a set is held to it; the rest resolve to UnrestrictedSeams"

        testCase "every refusal names the component and carries the declared set"
        <| fun _ ->
            let m = referenceModule ()
            let entityStore = SeamId.ofInterface "IJobScheduler"
            let grants = Map.ofList [ componentOf m, SeamGrant.ofSeams [ entityStore ] ]
            let gate = SeamAuthorityGate.create ignore (effectingSignature [ m ]) grants

            match SeamAuthorityEnforcement.verify gate [ m ] with
            | Ok() -> failtest "a partial declaration must refuse everything outside it"
            | Error denials ->
                Expect.all
                    denials
                    (fun d -> d.Reason.Contains "reference-service")
                    "the remedy must be in the message — every refusal names the component"

                Expect.isFalse
                    (denials
                     |> List.exists (fun d -> d.Reason.Contains "'IJobScheduler', which is outside"))
                    "the one seam that WAS declared must not be refused"
    ]

// ── 4. the profile binds, and refusals reach the ledger ───────────────

let private profileBinding =
    testList "verified profile binding" [

        testCase "Standard with nothing declared admits the whole composition"
        <| fun _ ->
            let audit = RecordingAuditLog()
            let modules = realComposition ()

            Expect.equal
                (SeamAuthorityEnforcement.verifyAudited audit "scope-1" CompositionProfile.Standard None None modules)
                (Ok())
                "the pre-657 posture is unchanged: Standard + nothing declared is the additive floor"

            Expect.isEmpty audit.Events "an admitted composition writes nothing to the ledger"

        testCase "Verified with no capability signature is refused before any reach is checked"
        <| fun _ ->
            let audit = RecordingAuditLog()

            Expect.equal
                (SeamAuthorityEnforcement.verifyAudited
                    audit
                    "scope-1"
                    CompositionProfile.Verified
                    None
                    None
                    (realComposition ()))
                (Error(SeamAuthorityRefusal.Profile CapabilityGateUndeclared))
                "a mandatory gate with nothing to check against would admit everything while presenting as enforcement"

        testCase "Verified with an envelope but no grant signature is refused by name"
        <| fun _ ->
            let audit = RecordingAuditLog()
            let modules = realComposition ()

            Expect.equal
                (SeamAuthorityEnforcement.verifyAudited
                    audit
                    "scope-1"
                    CompositionProfile.Verified
                    (Some(effectingSignature modules))
                    None
                    modules)
                (Error(SeamAuthorityRefusal.Profile(SeamGrantsUndeclared [])))
                "declaring an effect envelope and no reachable-seam set is the half-declared state the verified profile must not accept"

        testCase "Verified with complete declarations admits the composition"
        <| fun _ ->
            let audit = RecordingAuditLog()
            let modules = realComposition ()

            Expect.equal
                (SeamAuthorityEnforcement.verifyAudited
                    audit
                    "scope-1"
                    CompositionProfile.Verified
                    (Some(effectingSignature modules))
                    (Some(exactGrants modules))
                    modules)
                (Ok())
                "a fully-declared composition must pass, or the verified profile is unreachable"

        testCase "a reach refusal reaches the audit path as CompositionCapabilityRefused"
        <| fun _ ->
            let audit = RecordingAuditLog()
            let m = referenceModule ()
            let reached = (SeamAuthorityEnforcement.reachOf m).ReachedSeams
            let grants = Map.ofList [ componentOf m, SeamGrant.ofSeams [] ]

            let outcome =
                SeamAuthorityEnforcement.verifyAudited
                    audit
                    "scope-1"
                    CompositionProfile.Verified
                    (Some(effectingSignature [ m ]))
                    (Some grants)
                    [ m ]

            match outcome with
            | Ok() -> failtest "an undeclared reach under the verified profile must be refused"
            | Error(SeamAuthorityRefusal.Profile _) ->
                failtest "the grants WERE declared — this must be a reach refusal, not a profile refusal"
            | Error(SeamAuthorityRefusal.Reaches denials) ->
                Expect.hasLength denials (List.length reached) "every reach must be refused"

                audit.WaitFor(List.length reached)

                let refusals =
                    audit.Events
                    |> List.filter (fun (_, e) ->
                        match e with
                        | AuditEvent.CompositionCapabilityRefused _ -> true
                        | _ -> false)

                Expect.hasLength
                    refusals
                    (List.length reached)
                    "every refusal must land on the Phase 658 ledger — a security control nobody can audit is not one"

        testCase "describeRefusal renders both refusal shapes readably"
        <| fun _ ->
            let profileText =
                SeamAuthorityEnforcement.describeRefusal (SeamAuthorityRefusal.Profile CapabilityGateUndeclared)

            Expect.stringContains profileText "CapabilitySignature" "a profile refusal must name what was missing"

            let m = referenceModule ()
            let grants = Map.ofList [ componentOf m, SeamGrant.ofSeams [] ]
            let gate = SeamAuthorityGate.create ignore (effectingSignature [ m ]) grants

            match SeamAuthorityEnforcement.verify gate [ m ] with
            | Ok() -> failtest "expected refusals"
            | Error denials ->
                let text =
                    SeamAuthorityEnforcement.describeRefusal (SeamAuthorityRefusal.Reaches denials)

                Expect.stringContains text "reference-service" "a reach refusal must name the component"
                Expect.stringContains text "SeamGrant" "the remedy must be in the message"
    ]

let tests =
    testList "SeamAuthorityEnforcement" [ derivation; additiveFloor; enforcement; profileBinding ]
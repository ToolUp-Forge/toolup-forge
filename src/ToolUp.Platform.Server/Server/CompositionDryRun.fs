// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Diagnostics

// ─── Null-composition dry run (CompositionDryRun) ────────────────────
//
// "Does this composition even compose?", answered in milliseconds with
// **zero external services, zero network, zero cloud accounts**.
//
// [Phase 295] proves a `CompositionDescriptor` is *lossless* — lower an
// app to data, rebuild it, get the same component-id set back. That is a
// serialisation law, and it holds equally well for a descriptor that
// names an S3 bucket nobody can reach. This is the runtime counterpart:
// take any descriptor — hand-written, preset / archetype, or tool-emitted
// — rebind every companion slot to the in-process default the slot
// already declares, rebuild it through [Phase 284]'s `ofManifest`, run the
// [Phase 281] / [Phase 294] well-formedness rules over what came out,
// drive the [Phase 291] lifecycle in declared init order, dispose in
// reverse, and report. Every descriptor edit, cook, preset and AI-emitted
// composition gets a wiring smoke test at unit-test speed.
//
// **The null default is `None`, and that is derived, not declared.**
// `ServerApp.empty` sets every companion slot to `None` / `[]`, and an
// empty app composes — the SDK supplies its own in-process default for
// each slot at `run` time (`HeaderAuthProvider`,
// `InMemoryNotificationChannel`, `NoOpAnonymousSessionMigrator`, …). So
// "rebind this slot to its in-memory / `NoX` default" is exactly "do not
// bind it", and the null catalogue's registration for every slot is the
// identity fold. There is no hand-maintained table of per-slot defaults to
// drift: the slot universe is [Phase 293]'s reflected
// `ComposableSurface.slots ()`, so a newly-shipped companion slot is
// null-bindable here the day its `ServerApp` field lands. A selection
// naming a companion interface that is *not* a slot on `ServerApp` (a
// composition-wrapping companion, or a typo) has no such default — and
// that is a **finding**, not a crash.
//
// **A composition defect is never an exception.** Every failure mode —
// an unresolvable id, an unfilled hole, a schema-version gap, a rule
// violation, a cyclic lifecycle order, a throwing init probe — lands in
// the returned `CompositionDryRunReport` as a `CompositionDryRunFinding`, attributed to a
// `ComponentId` wherever the id is derivable. A harness whose failure mode
// is an exception cannot report *several* defects, and reporting all of
// them in one pass is most of the value.
//
// **Where the dry run deliberately stops.** It does not call
// `ServerApp.run`: running binds sockets and starts hosted services, which
// is the opposite of what "zero external dependencies" means. It runs the
// composition well-formedness rules directly (`CompositionValidator
// .checkClassWith`) rather than the full [Phase 9m] `IConfigValidator`
// aggregator, because a companion's registered validator legitimately
// probes a remote dependency. The structural class ([Phase 585]) is
// exactly the set of invariants that are answerable from memory, which is
// exactly the set a null dry run can honestly check; the external-probe
// class is opt-in (`DryRunIncludeExternalProbes`) and empty today.
//
// **Zero cost when unused (GP 11 / GP 13).** Nothing here registers into
// DI, decorates a pipeline, or runs at compose. A consumer that never
// calls `CompositionDryRun.run` composes byte-for-byte what it did before;
// the harness is a function that is not invoked.
//
// **Generic substrate (GP 1).** No vendor type, no domain type — only
// `ComponentId`, the descriptor, and strings.

/// What a dry-run finding is *about*. Every case is a composition defect
/// the harness detected without touching anything outside the process.
type CompositionDryRunFindingKind =
    /// A selection names a companion interface that is not a composable
    /// slot on `ServerApp`, so forge declares no in-memory / `NoX` default
    /// to rebind it to. The selection is dropped from the null-bound
    /// descriptor (it cannot be bound to nothing meaningfully) and
    /// reported here — this is 436.A's "a slot with no such default is a
    /// finding, not a crash".
    | MissingNullDefault
    /// A selected `ComponentId` resolves to no entry in the registration
    /// catalogue (the `DescriptorError.UnknownComponents` case as data).
    | UnresolvedComponent
    /// The descriptor still declared a hole the null binding could not
    /// fill. `bindNulls` fills every unbound hole with the empty filling,
    /// so this surfaces only when a caller dry-runs a descriptor it
    /// null-bound itself and left a hole open.
    | UnfilledDescriptorHole
    /// The descriptor's [Phase 292] schema version could not be migrated
    /// to the current one (too new, or corrupt).
    | SchemaMigrationRejected
    /// A [Phase 281] / [Phase 294] composition well-formedness rule fired.
    /// `FindingCode` carries the rule's stable code, so a caller greps the
    /// same token `CompositionValidator.ruleManifest` publishes.
    | WellFormednessDefect
    /// The [Phase 291] init-before edges contain a cycle, so no
    /// deterministic init order exists. No component is initialised.
    | LifecycleOrderUnsatisfiable
    /// A component's init probe threw. Init stops at the first throw (a
    /// later component may legitimately depend on it); everything already
    /// initialised is still disposed.
    | ComponentInitFailed
    /// A component's dispose probe threw. Dispose continues past it — a
    /// failed dispose must not strand the components behind it.
    | ComponentDisposeFailed

/// One thing the dry run found wrong. Never thrown — always returned.
type CompositionDryRunFinding = {
    FindingKind: CompositionDryRunFindingKind
    /// The stable code a caller greps: a `CompositionValidator` rule code
    /// for a `WellFormednessDefect`, else a `dry-run-*` code owned here.
    FindingCode: string
    /// `DefectError` fails the run; `DefectWarning` is reported and the
    /// verdict stays `Composes`. Reuses the [Phase 281] severity so a
    /// rule's declared severity carries through unchanged.
    FindingSeverity: CompositionDefectSeverity
    /// The component this finding is about, where the id is derivable.
    /// `None` is an honest answer — a rule evaluator returns a message,
    /// not an id, so attribution for a `WellFormednessDefect` is
    /// best-effort (see `CompositionDryRun.attributeDefect`).
    FindingComponent: ComponentId option
    /// The readable, actionable detail — the rule message verbatim where
    /// one exists, so the dry run and the boot-time preflight say the same
    /// words about the same defect.
    FindingDetail: string
}

/// One composed component's trip through the dry-run lifecycle: whether
/// it initialised, whether it disposed, and how long each took. Timings
/// are microseconds — a null composition's whole run is a handful of
/// milliseconds, so milliseconds would round every component to zero.
type CompositionDryRunOutcome = {
    OutcomeComponent: ComponentId
    OutcomeInitialised: bool
    OutcomeDisposed: bool
    OutcomeInitMicros: int64
    OutcomeDisposeMicros: int64
}

/// Did the composition compose?
type CompositionDryRunVerdict =
    /// Built, validated, initialised and disposed with no error-severity
    /// finding. Warnings may still be present.
    | Composes
    /// At least one error-severity finding. `CompositionDryRunReport.ReportFindings`
    /// says what and, where derivable, which component.
    | DoesNotCompose

/// The typed answer to "does it compose?" — the whole outcome of one dry
/// run, including every defect found rather than just the first.
type CompositionDryRunReport = {
    ReportVerdict: CompositionDryRunVerdict
    ReportFindings: CompositionDryRunFinding list
    /// Per-component init / dispose outcome + timings, in init order.
    /// Empty when the composition never built.
    ReportComponents: CompositionDryRunOutcome list
    /// The resolved [Phase 291] init order. Empty when the order was
    /// cyclic or the composition never built.
    ReportInitOrder: ComponentId list
    /// The dispose order — the init order reversed.
    ReportDisposeOrder: ComponentId list
    /// The companion slots that were rebound to their in-process default,
    /// distinct and in first-seen order. This is what the run *changed*
    /// about the descriptor, so a reader can tell which bindings were
    /// never exercised.
    ReportNullBoundSlots: ComponentId list
    /// The descriptor holes filled with the empty filling so a partial /
    /// preset descriptor could be dry-run at all.
    ReportNullFilledHoles: string list
    /// Wall-clock for the whole run, microseconds.
    ReportElapsedMicros: int64
}

/// How to drive one dry run. `CompositionDryRun.options` is the base;
/// every field has a defaulted, zero-dependency value, so the minimal
/// call is `CompositionDryRun.run catalogue descriptor`.
type CompositionDryRunOptions = {
    /// The code side of the composition — the same catalogue the real
    /// composition root would build from. Module registrations must
    /// resolve here; companion slot ids are overlaid with null bindings,
    /// so a catalogue entry for a slot is never reached during a dry run.
    DryRunCatalogue: RegistrationCatalogue
    /// Additional [Phase 291] init-before edges over the composed
    /// component ids. Empty (the default) resolves to registration order.
    DryRunEdges: (ComponentId * ComponentId) list
    /// Per-component init effect. The default is a no-op: the dry run's
    /// claim is that the *order resolves* and the composition holds
    /// together, not that a component did work. A caller with real
    /// per-component init passes it here; a throw becomes a
    /// `ComponentInitFailed` finding, never an escaping exception.
    DryRunInitProbe: ComponentId -> unit
    /// Per-component dispose effect, run in reverse init order.
    DryRunDisposeProbe: ComponentId -> unit
    /// Also evaluate `CompositionValidator.externalProbeRules`. Off by
    /// default: that class exists precisely for rules that reach a
    /// dependency outside the process, which is what a null dry run
    /// excludes. The class is empty today, so this changes nothing yet —
    /// the switch is here so the first such rule does not silently join
    /// the offline path.
    DryRunIncludeExternalProbes: bool
}

/// Null-binding resolution + the dry-run driver.
module CompositionDryRun =

    // ── Finding codes (stable, greppable) ───────────────────────────────

    [<Literal>]
    let MissingNullDefaultCode = "dry-run-missing-null-default"

    [<Literal>]
    let UnresolvedComponentCode = "dry-run-unresolved-component"

    [<Literal>]
    let UnfilledHoleCode = "dry-run-unfilled-hole"

    [<Literal>]
    let SchemaMigrationCode = "dry-run-schema-migration"

    [<Literal>]
    let LifecycleOrderCode = "dry-run-lifecycle-order"

    [<Literal>]
    let InitFailedCode = "dry-run-init-failed"

    [<Literal>]
    let DisposeFailedCode = "dry-run-dispose-failed"

    // ── Timing ──────────────────────────────────────────────────────────

    /// Stopwatch ticks → microseconds. Microseconds because a whole null
    /// composition runs in single-digit milliseconds; a millisecond
    /// resolution would report every component as zero.
    let private micros (ticks: int64) : int64 =
        ticks * 1_000_000L / Stopwatch.Frequency

    let private timed (effect: unit -> 'a) : 'a * int64 =
        let start = Stopwatch.GetTimestamp()
        let result = effect ()
        result, micros (Stopwatch.GetTimestamp() - start)

    // ── 436.A — null-binding resolution ─────────────────────────────────

    [<Literal>]
    let private CompanionPrefix = "companion:"

    /// The companion interface a `ComponentId` addresses, for both slot
    /// (`companion:IBlobStorage`) and multi-impl (`companion:IAuditSink/
    /// s3-archive`) forms. `None` for any other id class (module,
    /// datatype, tool, metric, subject) — those are not slots and are
    /// never null-bound.
    let slotInterface (id: ComponentId) : string option =
        let raw = ComponentId.value id

        if raw.StartsWith(CompanionPrefix, StringComparison.Ordinal) then
            let rest = raw.Substring CompanionPrefix.Length

            let iface =
                match rest.IndexOf '/' with
                | -1 -> rest
                | separator -> rest.Substring(0, separator)

            if String.IsNullOrWhiteSpace iface then None else Some iface
        else
            None

    /// Every companion interface forge declares a composable slot for,
    /// derived from [Phase 293]'s reflection over the `ServerApp`
    /// composition record. Cached — the reflection is over one record type
    /// and its answer cannot change within a process.
    let private composableInterfaces =
        lazy (ComposableSurface.slots () |> List.map _.Interface |> Set.ofList)

    /// The interfaces `bindNulls` can rebind to an in-process default. A
    /// selection naming anything else yields a `MissingNullDefault`
    /// finding.
    let nullBindableInterfaces () : Set<string> = composableInterfaces.Force()

    /// Rebind one selection list: every companion selection collapses to
    /// its bare slot id with its `Inputs` cleared (an input is a binding
    /// parameter — a connection string, a bucket, a region — and the null
    /// binding takes none), deduplicated so N impls of a multi-impl slot
    /// collapse to one null binding. A companion interface forge declares
    /// no slot for is dropped with a finding. Non-companion selections
    /// pass through untouched.
    let private nullBindSelections
        (selections: ComponentSelection list)
        : ComponentSelection list * ComponentId list * CompositionDryRunFinding list =
        let bindable = nullBindableInterfaces ()

        let folder (kept, bound, findings, seen) (selection: ComponentSelection) =
            match slotInterface selection.Id with
            | None -> kept @ [ selection ], bound, findings, seen
            | Some iface when bindable.Contains iface ->
                let slotId = ComponentId.forCompanionSlot iface

                if Set.contains slotId seen then
                    kept, bound, findings, seen
                else
                    kept @ [ { Id = slotId; Inputs = Map.empty } ], bound @ [ slotId ], findings, Set.add slotId seen
            | Some iface ->
                let finding = {
                    FindingKind = MissingNullDefault
                    FindingCode = MissingNullDefaultCode
                    FindingSeverity = DefectError
                    FindingComponent = Some selection.Id
                    FindingDetail =
                        sprintf
                            "Component '%s' selects companion interface '%s', which is not a composable slot on ServerApp — forge declares no in-memory / NoX default to bind it to, so it cannot be dry-run offline. Composable slots: %s. Either the interface name is wrong, or this companion wraps the composition rather than filling a slot (compose it explicitly and dry-run the wrapped descriptor)."
                            (ComponentId.value selection.Id)
                            iface
                            (bindable |> Set.toList |> List.map (sprintf "'%s'") |> String.concat ", ")
                }

                kept, bound, findings @ [ finding ], seen

        let kept, bound, findings, _ =
            selections |> List.fold folder ([], [], [], Set.empty)

        kept, bound, findings

    /// 436.A — the full null-binding pass, with its findings. Every
    /// companion slot (bound externally or otherwise) is rebound to the
    /// in-process default the slot declares, and every unbound descriptor
    /// hole is filled with the empty filling so a partial / preset
    /// descriptor is dry-runnable at all rather than failing on the hole.
    ///
    /// Returns the rebound descriptor alongside the slots rebound, the
    /// holes filled, and any `MissingNullDefault` findings. `bindNulls` is
    /// the 436.A signature; this is the same pass with its report, which
    /// is what `run` consumes.
    ///
    /// Idempotent: null-binding an already-null-bound descriptor is a
    /// no-op.
    let bindNullsWithFindings
        (descriptor: CompositionDescriptor)
        : CompositionDescriptor * ComponentId list * string list * CompositionDryRunFinding list =
        let components, componentSlots, componentFindings =
            nullBindSelections descriptor.Components

        let holeResults =
            descriptor.Holes
            |> List.map (fun hole ->
                match hole.Filling with
                | None -> { hole with Filling = Some [] }, [], [ hole.Name ], []
                | Some filling ->
                    let bound, slots, findings = nullBindSelections filling
                    { hole with Filling = Some bound }, slots, [], findings)

        let holes = holeResults |> List.map (fun (h, _, _, _) -> h)
        let holeSlots = holeResults |> List.collect (fun (_, s, _, _) -> s)
        let filledHoles = holeResults |> List.collect (fun (_, _, n, _) -> n)
        let holeFindings = holeResults |> List.collect (fun (_, _, _, f) -> f)

        let rebound = {
            descriptor with
                Components = components
                Holes = holes
        }

        rebound, (componentSlots @ holeSlots |> List.distinct), filledHoles, (componentFindings @ holeFindings)

    /// 436.A — every unbound or externally-bound companion slot rebound to
    /// the in-memory / `NoX` default the slot already declares, and every
    /// unbound hole filled with the empty filling. A companion interface
    /// with no such default is dropped and reported by
    /// `bindNullsWithFindings` (and by `run`) — never a crash.
    let bindNulls (descriptor: CompositionDescriptor) : CompositionDescriptor =
        let rebound, _, _, _ = bindNullsWithFindings descriptor
        rebound

    /// The dry-run catalogue: `catalogue` overlaid with a null binding for
    /// every composable slot forge declares. The null binding is the
    /// identity fold — leaving a slot unbound *is* selecting its
    /// in-process default, because `run` supplies one for every slot
    /// `ServerApp.empty` leaves `None`.
    ///
    /// Overlaid *after* the caller's entries, so a catalogue that binds a
    /// slot to a real vendor companion cannot be reached by a dry run.
    /// That is the point: the harness answers "does it compose?", not
    /// "does the vendor binding work?".
    let nullCatalogue (catalogue: RegistrationCatalogue) : RegistrationCatalogue =
        nullBindableInterfaces ()
        |> Set.toList
        |> List.fold
            (fun acc iface -> RegistrationCatalogue.add (ComponentId.forCompanionSlot iface) (fun _ app -> app) acc)
            catalogue

    // ── 436.B — dry-run execution ───────────────────────────────────────

    /// Best-effort `ComponentId` attribution for a well-formedness defect.
    /// A [Phase 281] rule evaluator returns a message, not an id, so the
    /// only honest derivation is to look for a *composed* id's literal
    /// value inside the message — a closed universe, so a match is real.
    /// `None` when no composed id appears, which is an honest answer
    /// rather than a guess.
    let private attributeDefect (manifest: CompositionManifest) (defect: CompositionDefect) : ComponentId option =
        CompositionManifest.allComponents manifest
        |> List.map _.Id
        |> List.distinct
        |> List.sortByDescending (fun id -> (ComponentId.value id).Length)
        |> List.tryFind (fun id -> defect.Message.Contains(ComponentId.value id, StringComparison.Ordinal))

    /// The base options: no extra lifecycle edges, no-op init / dispose
    /// probes, structural rules only.
    let options (catalogue: RegistrationCatalogue) : CompositionDryRunOptions = {
        DryRunCatalogue = catalogue
        DryRunEdges = []
        DryRunInitProbe = ignore
        DryRunDisposeProbe = ignore
        DryRunIncludeExternalProbes = false
    }

    let withEdges
        (edges: (ComponentId * ComponentId) list)
        (opts: CompositionDryRunOptions)
        : CompositionDryRunOptions =
        { opts with DryRunEdges = edges }

    let withInitProbe (probe: ComponentId -> unit) (opts: CompositionDryRunOptions) : CompositionDryRunOptions = {
        opts with
            DryRunInitProbe = probe
    }

    let withDisposeProbe (probe: ComponentId -> unit) (opts: CompositionDryRunOptions) : CompositionDryRunOptions = {
        opts with
            DryRunDisposeProbe = probe
    }

    let withExternalProbeRules (enabled: bool) (opts: CompositionDryRunOptions) : CompositionDryRunOptions = {
        opts with
            DryRunIncludeExternalProbes = enabled
    }

    let private verdictOf (findings: CompositionDryRunFinding list) : CompositionDryRunVerdict =
        if findings |> List.exists (fun f -> f.FindingSeverity = DefectError) then
            DoesNotCompose
        else
            Composes

    /// A report for a run that never got past the build — the null-binding
    /// outcome plus whatever stopped it.
    let private stoppedReport
        (nullBound: ComponentId list)
        (filledHoles: string list)
        (findings: CompositionDryRunFinding list)
        (elapsed: int64)
        : CompositionDryRunReport =
        {
            ReportVerdict = verdictOf findings
            ReportFindings = findings
            ReportComponents = []
            ReportInitOrder = []
            ReportDisposeOrder = []
            ReportNullBoundSlots = nullBound
            ReportNullFilledHoles = filledHoles
            ReportElapsedMicros = elapsed
        }

    /// Translate a versioned build failure into findings. Every id / hole
    /// name in the error becomes its own finding, so one build failure
    /// reports every unresolved selection rather than the first.
    let private buildFindings (error: VersionedBuildError) : CompositionDryRunFinding list =
        match error with
        | MigrationFailed migration -> [
            {
                FindingKind = SchemaMigrationRejected
                FindingCode = SchemaMigrationCode
                FindingSeverity = DefectError
                FindingComponent = None
                FindingDetail = CompositionDescriptorVersion.renderMigrationError migration
            }
          ]
        | BuildFailed(UnknownComponents ids) ->
            ids
            |> List.map (fun id -> {
                FindingKind = UnresolvedComponent
                FindingCode = UnresolvedComponentCode
                FindingSeverity = DefectError
                FindingComponent = Some id
                FindingDetail =
                    sprintf
                        "Component '%s' resolves to no entry in the registration catalogue. A dry run supplies null bindings for every companion slot, so an unresolved id is a module (or other non-slot) selection the catalogue does not register — register it via RegistrationCatalogue.addModule / add, or correct the id."
                        (ComponentId.value id)
            })
        | BuildFailed(UnfilledHoles names) ->
            names
            |> List.map (fun name -> {
                FindingKind = UnfilledDescriptorHole
                FindingCode = UnfilledHoleCode
                FindingSeverity = DefectError
                FindingComponent = None
                FindingDetail =
                    sprintf
                        "Descriptor hole '%s' is still unbound after null binding. bindNulls fills every declared hole with the empty filling, so this descriptor was hand-modified between binding and building."
                        name
            })

    /// Drive the [Phase 291] lifecycle over the composed component ids:
    /// resolve the init order, run each init probe, then dispose in
    /// reverse. Returns the per-component outcomes, the two orders, and
    /// any findings. Never throws — a probe's exception is a finding.
    let private driveLifecycle
        (opts: CompositionDryRunOptions)
        (composed: ComponentId list)
        : CompositionDryRunOutcome list * ComponentId list * ComponentId list * CompositionDryRunFinding list =
        let order = {
            Components = composed
            Edges = opts.DryRunEdges
        }

        match ComponentLifecycle.initSequence order with
        | Error message ->
            let finding = {
                FindingKind = LifecycleOrderUnsatisfiable
                FindingCode = LifecycleOrderCode
                FindingSeverity = DefectError
                FindingComponent = None
                FindingDetail =
                    sprintf
                        "%s No component was initialised — a cyclic order has no deterministic init sequence to drive."
                        message
            }

            [], [], [], [ finding ]
        | Ok initOrder ->
            // Init stops at the first throw: a later component may
            // legitimately depend on the one that failed, so continuing
            // would manufacture cascade findings that say nothing new.
            let mutable initFindings: CompositionDryRunFinding list = []

            let initialised =
                initOrder
                |> List.fold
                    (fun acc id ->
                        if not initFindings.IsEmpty then
                            acc
                        else
                            let outcome, elapsed =
                                timed (fun () ->
                                    try
                                        opts.DryRunInitProbe id
                                        Ok()
                                    with ex ->
                                        Error ex.Message)

                            match outcome with
                            | Ok() ->
                                acc
                                @ [
                                    {
                                        OutcomeComponent = id
                                        OutcomeInitialised = true
                                        OutcomeDisposed = false
                                        OutcomeInitMicros = elapsed
                                        OutcomeDisposeMicros = 0L
                                    }
                                ]
                            | Error message ->
                                initFindings <- [
                                    {
                                        FindingKind = ComponentInitFailed
                                        FindingCode = InitFailedCode
                                        FindingSeverity = DefectError
                                        FindingComponent = Some id
                                        FindingDetail =
                                            sprintf
                                                "Component '%s' failed to initialise during the dry run: %s. Init stopped here — components later in the order were not initialised."
                                                (ComponentId.value id)
                                                message
                                    }
                                ]

                                acc
                                @ [
                                    {
                                        OutcomeComponent = id
                                        OutcomeInitialised = false
                                        OutcomeDisposed = false
                                        OutcomeInitMicros = elapsed
                                        OutcomeDisposeMicros = 0L
                                    }
                                ])
                    []

            // Dispose only what initialised, in reverse. Unlike init,
            // dispose continues past a failure — a component that will not
            // release must not strand the ones behind it.
            let disposeOrder =
                initialised
                |> List.filter _.OutcomeInitialised
                |> List.map _.OutcomeComponent
                |> List.rev

            let mutable disposeFindings: CompositionDryRunFinding list = []
            let mutable disposeTimings: Map<ComponentId, int64> = Map.empty

            for id in disposeOrder do
                let outcome, elapsed =
                    timed (fun () ->
                        try
                            opts.DryRunDisposeProbe id
                            Ok()
                        with ex ->
                            Error ex.Message)

                disposeTimings <- Map.add id elapsed disposeTimings

                match outcome with
                | Ok() -> ()
                | Error message ->
                    disposeFindings <-
                        disposeFindings
                        @ [
                            {
                                FindingKind = ComponentDisposeFailed
                                FindingCode = DisposeFailedCode
                                FindingSeverity = DefectError
                                FindingComponent = Some id
                                FindingDetail =
                                    sprintf
                                        "Component '%s' failed to dispose during the dry run: %s. Dispose continued past it."
                                        (ComponentId.value id)
                                        message
                            }
                        ]

            let disposedIds = disposeFindings |> List.choose _.FindingComponent |> Set.ofList

            let outcomes =
                initialised
                |> List.map (fun outcome ->
                    match Map.tryFind outcome.OutcomeComponent disposeTimings with
                    | Some elapsed -> {
                        outcome with
                            OutcomeDisposed = not (disposedIds.Contains outcome.OutcomeComponent)
                            OutcomeDisposeMicros = elapsed
                      }
                    | None -> outcome)

            outcomes, initOrder, disposeOrder, (initFindings @ disposeFindings)

    /// 436.B — dry-run a descriptor: null-bind it, rebuild it through
    /// [Phase 284]'s `ofManifest` (with the [Phase 292] migration first),
    /// run the [Phase 281] / [Phase 294] rules over what came out, drive
    /// the [Phase 291] lifecycle, dispose in reverse, and report.
    ///
    /// Total. A composition defect is a `CompositionDryRunFinding`, never an
    /// exception, and the pass keeps going wherever a later stage is still
    /// meaningful — so one run reports every unresolved id, every rule
    /// violation, and the lifecycle outcome together.
    let runWith (opts: CompositionDryRunOptions) (descriptor: CompositionDescriptor) : CompositionDryRunReport =
        let started = Stopwatch.GetTimestamp()

        let elapsed () =
            micros (Stopwatch.GetTimestamp() - started)

        let bound, nullBoundSlots, filledHoles, bindFindings =
            bindNullsWithFindings descriptor

        match CompositionDescriptorVersion.ofManifest (nullCatalogue opts.DryRunCatalogue) bound with
        | Error error -> stoppedReport nullBoundSlots filledHoles (bindFindings @ buildFindings error) (elapsed ())
        | Ok app ->
            let manifest = ServerApp.compositionManifest app

            // Phase 583 — one builder, shared with the live composition
            // root, so a dry-run evaluates the rules over exactly the
            // reference edges a real boot would (including the
            // module-graph registrations the manifest collapses).
            let references: CompositionReferences = ServerApp.compositionReferences app

            let defects =
                CompositionValidator.checkClassWith StructuralRule references manifest
                @ (if opts.DryRunIncludeExternalProbes then
                       CompositionValidator.checkClassWith ExternalProbeRule references manifest
                   else
                       [])

            let ruleFindings =
                defects
                |> List.map (fun defect -> {
                    FindingKind = WellFormednessDefect
                    FindingCode = defect.RuleCode
                    FindingSeverity = defect.Severity
                    FindingComponent = attributeDefect manifest defect
                    FindingDetail = defect.Message
                })

            let composed =
                CompositionManifest.allComponents manifest |> List.map _.Id |> List.distinct

            let outcomes, initOrder, disposeOrder, lifecycleFindings =
                driveLifecycle opts composed

            let findings = bindFindings @ ruleFindings @ lifecycleFindings

            {
                ReportVerdict = verdictOf findings
                ReportFindings = findings
                ReportComponents = outcomes
                ReportInitOrder = initOrder
                ReportDisposeOrder = disposeOrder
                ReportNullBoundSlots = nullBoundSlots
                ReportNullFilledHoles = filledHoles
                ReportElapsedMicros = elapsed ()
            }

    /// `runWith` over the base options — the one-argument-plus-catalogue
    /// form. Modules resolve from `catalogue`; every companion slot is
    /// null-bound, so nothing external is reachable.
    let run (catalogue: RegistrationCatalogue) (descriptor: CompositionDescriptor) : CompositionDryRunReport =
        runWith (options catalogue) descriptor

    // ── Report rendering ────────────────────────────────────────────────

    let private renderFinding (finding: CompositionDryRunFinding) : string =
        let severity =
            match finding.FindingSeverity with
            | DefectError -> "error"
            | DefectWarning -> "warning"

        let attribution =
            match finding.FindingComponent with
            | Some id -> sprintf " (%s)" (ComponentId.value id)
            | None -> ""

        sprintf "  [%s/%s]%s %s" finding.FindingCode severity attribution finding.FindingDetail

    /// A readable multi-line summary — the verdict, every finding with its
    /// rule code and attribution, and the wall clock. This is the text a
    /// failing `DryRun.shouldCompose` raises, so a test failure reads the
    /// same as an operator's console.
    let renderReport (report: CompositionDryRunReport) : string =
        let header =
            match report.ReportVerdict with
            | Composes ->
                sprintf
                    "Composition dry run: COMPOSES (%d components, %d null-bound slots, %dµs)."
                    (List.length report.ReportComponents)
                    (List.length report.ReportNullBoundSlots)
                    report.ReportElapsedMicros
            | DoesNotCompose ->
                sprintf
                    "Composition dry run: DOES NOT COMPOSE — %d finding(s) (%dµs)."
                    (List.length report.ReportFindings)
                    report.ReportElapsedMicros

        match report.ReportFindings with
        | [] -> header
        | findings -> header + "\n" + (findings |> List.map renderFinding |> String.concat "\n")

/// 436.C — the one-line test affordance.
///
/// **Placement note.** The [Phase 285] contract packs live in
/// `ToolUp.Platform.Tests`, which is `IsPackable=false` — a consumer
/// cannot reference it, so a helper placed beside them would be
/// unreachable by the consumer test suites and cookbook verification
/// checklists this task exists to serve. It therefore ships here, in the
/// package every such consumer already references, and is deliberately
/// **Expecto-shaped but Expecto-free**: it raises a readable failure the
/// way an assertion does, so it drops into an Expecto (or xUnit, or
/// NUnit) test unchanged without dragging a test-framework dependency into
/// a runtime package. A consumer that never calls it pays nothing — it is
/// a function that is not invoked (GP 13).
module DryRun =

    /// Assert that `descriptor` composes against `catalogue` with every
    /// companion slot null-bound. Raises with the rendered report — every
    /// finding, each with its rule code and `ComponentId` attribution — on
    /// anything short of a clean compose. Returns the report on success,
    /// so a caller can go on to assert about timings or the init order.
    let shouldCompose (catalogue: RegistrationCatalogue) (descriptor: CompositionDescriptor) : CompositionDryRunReport =
        let report = CompositionDryRun.run catalogue descriptor

        match report.ReportVerdict with
        | Composes -> report
        | DoesNotCompose -> failwith (CompositionDryRun.renderReport report)

    /// `shouldCompose` with a wall-clock bound — the cookbook-verification
    /// shape, where "it composes" and "it composes *offline-fast*" are the
    /// same claim. A dry run that exceeds `budget` fails even when the
    /// composition is otherwise clean: overrunning an offline budget means
    /// something in the composition reached for the world.
    let shouldComposeWithin
        (budget: TimeSpan)
        (catalogue: RegistrationCatalogue)
        (descriptor: CompositionDescriptor)
        : CompositionDryRunReport =
        let report = shouldCompose catalogue descriptor
        let budgetMicros = int64 (budget.TotalMilliseconds * 1000.0)

        if report.ReportElapsedMicros > budgetMicros then
            failwithf
                "Composition dry run took %dµs, over the %dµs budget. The composition composes, but not at offline speed — something it composes is reaching outside the process (the dry run null-binds every companion slot, so a slow run means a module registration, not a companion).\n%s"
                report.ReportElapsedMicros
                budgetMicros
                (CompositionDryRun.renderReport report)

        report
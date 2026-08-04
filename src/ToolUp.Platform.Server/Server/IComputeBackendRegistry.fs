// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 484 — compute-backend registry + the routing decision ─────────
//
// Phase 318 composes ONE `IExternalComputeDispatcher`. A real deployment
// mixes backends — a CPU worker pool, an accelerator queue, a partner's
// service — and each is good for different work. This file is the
// **capability declaration** (what a backend serves) and the **routing
// decision** (which backend a given spec belongs to), as data and as a
// pure function. `RoutingComputeDispatcher.fs` is what applies it.
//
// **The decision is a pure function, deliberately.** `select` takes a
// registry and a spec and returns either the chosen registration or a
// typed refusal. It performs no I/O, touches no backend, and is total —
// so the routing precedence is testable without a dispatcher, a DI
// container, or a running deployment, and a refusal is a value a caller
// can log, persist, or hand to an operator rather than an exception it
// has to catch.
//
// **Supported profiles are DERIVED, never declared here.** Phase 478
// already gave a backend one authoritative way to say what it guarantees
// about an isolated environment — `IIsolatedComputeBackend.IsolationPosture`
// — and `ExecutionProfileGate` refuses a submission that contradicts it. A
// second, hand-written `SupportedProfiles` field on the registration would
// be a rival source of truth for the same claim, and the failure mode is
// specific and bad: routing would select a backend on the strength of the
// registration's claim, and the gate would then refuse the submission on
// the strength of the posture. The operator sees "routed, then refused"
// with two declarations disagreeing and no way to tell which is the lie.
// So `ComputeBackendRegistration.supportedProfiles` reads the posture, and
// the registration cannot overclaim by construction (GP 12 rule 1 — the
// capability is derived from the thing that will actually execute).
//
// **Duplicate-kind rejection is at compose and it is fatal.** Two
// registrations claiming one kind is not a preference to resolve by
// registration order: the kind is the routing key stamped onto every
// handle, so the loser's handles would `Poll` against the winner, which
// never minted them — and a backend asked about a native ref it has never
// seen reports a terminal failure for work that is running perfectly well.
// Silently picking one turns a compose-time typo into lost jobs. The
// registry therefore throws, naming both registrants and the contested
// kind, exactly as `AlgorithmProviderRegistry` does for a duplicate
// algorithm id and the `INotificationSink` registry does for a duplicate
// `Kind`; the diagnostic shape is deliberately the same family.

/// Phase 484 — one external-compute backend, with the capabilities it
/// declares. The unit a deployment hands to `ComputeFleetCompose`.
///
/// `[<NoComparison>]` because `Dispatcher` is a live instance: ordering
/// two registrations is meaningless, and F#'s generated comparison over
/// an interface field would raise at runtime rather than at compile time.
[<NoComparison>]
type ComputeBackendRegistration = {
    /// Stable routing key for this backend, and the value stamped onto
    /// `ExternalHandle.Backend` for every submission routed here (e.g.
    /// `"cpu-worker-pool"`, `"accelerator-queue"`). Unique across the
    /// registry — a duplicate is a compose-time failure.
    ///
    /// Normally identical to `Dispatcher.Backend`. It is a separate field
    /// because the registry's key is the deployment's to choose: the same
    /// companion may be composed twice against different clusters, and
    /// each instance then needs its own routing key even though both
    /// report the same `Backend` label.
    Kind: string
    /// The resource classes this backend serves, in the deployment's own
    /// vocabulary (e.g. `"cpu"`, `"gpu"`, `"high-memory"`). Matched
    /// against a spec's `resource-class` hint — see
    /// `ComputeBackendRouting.ResourceClassHint`.
    ///
    /// A `string` set rather than a typed enumeration for the reason
    /// `ExternalWorkSpec.ResourceHints` is a string map (GP 12 rule 1):
    /// any typed shape would encode one scheduler's accelerator and
    /// node-class vocabulary and stop being portable the first time a
    /// backend bills by something else.
    ResourceClasses: Set<string>
    /// The payload envelope versions this backend understands (e.g.
    /// `"v1"`, `"2026-06"`). Declared and surfaced for operator
    /// diagnosis; the platform never parses a payload, so it cannot and
    /// does not check a spec's envelope against this set. It is here so a
    /// fleet row can show that a backend has not been upgraded yet — a
    /// question an operator otherwise answers by reading deployment
    /// manifests.
    EnvelopeVersions: Set<string>
    /// `true` when this backend takes work that no resource hint
    /// discriminates. Exactly one registration may declare it — see
    /// `ComputeBackendRegistry`.
    IsDefault: bool
    /// The dispatcher that actually brokers the work. Its declared
    /// `IsolationPosture` (Phase 478) is what `supportedProfiles` reads.
    Dispatcher: IExternalComputeDispatcher
}

[<RequireQualifiedAccess>]
module ComputeBackendRegistration =

    /// A registration serving no declared resource class, no declared
    /// envelope version, and not the default — the minimum shape. Build
    /// it up with the `with*` helpers.
    let create (kind: string) (dispatcher: IExternalComputeDispatcher) : ComputeBackendRegistration = {
        Kind = kind
        ResourceClasses = Set.empty
        EnvelopeVersions = Set.empty
        IsDefault = false
        Dispatcher = dispatcher
    }

    /// Declare the resource classes this backend serves.
    let withResourceClasses
        (classes: string seq)
        (registration: ComputeBackendRegistration)
        : ComputeBackendRegistration =
        {
            registration with
                ResourceClasses = Set.ofSeq classes
        }

    /// Declare the payload envelope versions this backend understands.
    let withEnvelopeVersions
        (versions: string seq)
        (registration: ComputeBackendRegistration)
        : ComputeBackendRegistration =
        {
            registration with
                EnvelopeVersions = Set.ofSeq versions
        }

    /// Mark this backend as the one that takes work no hint
    /// discriminates. At most one registration may be the default.
    let asDefault (registration: ComputeBackendRegistration) : ComputeBackendRegistration = {
        registration with
            IsDefault = true
    }

    /// The isolation posture the backend declares (Phase 478). A
    /// dispatcher that does not implement `IIsolatedComputeBackend`
    /// declares nothing, which reads as `IsolationPosture.standardOnly`.
    let posture (registration: ComputeBackendRegistration) : IsolationPosture =
        ExecutionProfileGate.postureOf registration.Dispatcher

    /// The execution profiles this backend can honour — **derived** from
    /// the declared posture, never separately claimed. `Standard` is
    /// always present (every backend honours it, which is what keeps
    /// Phase 318's path untouched); `Isolated` only when the posture
    /// asserts all three clauses.
    let supportedProfiles (registration: ComputeBackendRegistration) : ExecutionProfile list =
        let declared = posture registration

        [
            ExecutionProfile.Standard
            if IsolationPosture.honours ExecutionProfile.Isolated declared then
                ExecutionProfile.Isolated
        ]

    /// `true` when this backend can honour `profile`.
    let honours (profile: ExecutionProfile) (registration: ComputeBackendRegistration) : bool =
        IsolationPosture.honours profile (posture registration)

    /// One-line operator description: kind, profiles, resource classes,
    /// envelope versions. The text a refusal quotes for each candidate,
    /// so "what was available" is legible without a second lookup.
    let describe (registration: ComputeBackendRegistration) : string =
        let listing (label: string) (values: Set<string>) =
            if Set.isEmpty values then
                sprintf "%s none declared" label
            else
                sprintf "%s [%s]" label (values |> Set.toList |> String.concat ", ")

        let profiles =
            supportedProfiles registration
            |> List.map ExecutionProfile.label
            |> String.concat "/"

        sprintf
            "'%s' (profiles %s; %s; %s%s)"
            registration.Kind
            profiles
            (listing "resource classes" registration.ResourceClasses)
            (listing "envelopes" registration.EnvelopeVersions)
            (if registration.IsDefault then "; default" else "")

/// Phase 484 — the compose-time index of registered compute backends,
/// keyed by `Kind`. Constructed eagerly by `ComputeFleetCompose`, so a
/// duplicate kind or a second default is a compose-time failure rather
/// than a first-submission failure.
///
/// Raises on construction when the registration list is empty, when two
/// registrations share a `Kind`, or when more than one declares
/// `IsDefault`. Each message names the offending registrations.
type ComputeBackendRegistry(registrations: ComputeBackendRegistration list) =

    do
        if List.isEmpty registrations then
            failwith
                "ToolUp.Platform: ComputeBackendRegistry was constructed with no backends. A routing dispatcher over an empty fleet can only ever refuse, which is strictly worse than the NoExternalComputeDispatcher default it would replace — that at least says so in one clear message. Register at least one backend, or leave ServerConfig.ExternalCompute = NoExternalCompute."

        let duplicates =
            registrations
            |> List.groupBy _.Kind
            |> List.filter (fun (_, group) -> List.length group > 1)

        if not (List.isEmpty duplicates) then
            let describe (kind: string, group: ComputeBackendRegistration list) =
                let backends =
                    group
                    |> List.map (fun registration -> sprintf "'%s'" registration.Dispatcher.Backend)
                    |> String.concat " and "

                sprintf "dispatchers %s both registered backend kind '%s'" backends kind

            let listing = duplicates |> List.map describe |> String.concat "; "

            failwithf
                "ToolUp.Platform: duplicate compute-backend registration — %s. One dispatcher per kind: the kind is the routing key stamped onto every handle, so resolving the clash by registration order would send the loser's polls to a backend that never minted them, and work that is running fine would be reported as a terminal failure. Give one of them a distinct Kind, or remove it."
                listing

        let defaults = registrations |> List.filter _.IsDefault

        if List.length defaults > 1 then
            let names =
                defaults
                |> List.map (fun registration -> sprintf "'%s'" registration.Kind)
                |> String.concat " and "

            failwithf
                "ToolUp.Platform: %d compute backends declared IsDefault — %s. The default is where work goes when no resource hint discriminates, so two of them is not a fallback chain, it is an unresolved choice the router would settle by registration order. Mark exactly one as the default."
                (List.length defaults)
                names

    let byKind = registrations |> List.map (fun r -> r.Kind, r) |> Map.ofList

    /// Every registration, in composition order. Order is load-bearing:
    /// it is the deterministic tie-break when two backends are equally
    /// eligible.
    member _.Registrations: ComputeBackendRegistration list = registrations

    /// Every registered kind, in composition order.
    member _.Kinds: string list = registrations |> List.map _.Kind

    /// The registration for `kind`, or `None` when no backend answers to
    /// it. `None` is the case a handle minted before this router existed
    /// takes — see `ComputeBackendRouting.selectForHandle`.
    member _.TryFind(kind: string) : ComputeBackendRegistration option = byKind |> Map.tryFind kind

    /// The registration declaring `IsDefault`, when one does.
    member _.Default: ComputeBackendRegistration option =
        registrations |> List.tryFind _.IsDefault

/// Phase 484 — the routing decision. Pure, total, and independent of any
/// dispatcher: `select` decides from declared capabilities alone.
[<RequireQualifiedAccess>]
module ComputeBackendRouting =

    /// The reserved `ExternalWorkSpec.ResourceHints` key carrying a
    /// spec's **hard** resource-class requirement, comma-separated (e.g.
    /// `"resource-class" -> "gpu,high-memory"`).
    ///
    /// **Why a reserved key rather than every hint key.** Treating every
    /// hint key as a routing requirement would refuse work over hints
    /// that are not resource classes at all — `"priority" -> "high"`,
    /// `"queue" -> "batch"` — and Phase 318's contract is explicit that a
    /// backend IGNORES a hint it does not understand. So an ordinary hint
    /// stays advisory and can only ever *prefer* a backend, while this
    /// one key is the caller saying "this work cannot run without these
    /// classes", which is the only claim the router is entitled to refuse
    /// on. A spec that omits the key is never refused for resources.
    ///
    /// A reserved key rather than a new `ExternalWorkSpec` field because
    /// the spec is a persisted, wire-crossing record: a routing hint that
    /// only some deployments use does not earn a field every consumer
    /// constructs, and the hint map is exactly the extension point Phase
    /// 318 provided for this (GP 11).
    let ResourceClassHint = "resource-class"

    /// The hard resource-class requirement `spec` declares, if any.
    /// Blank entries are dropped, so `"gpu, ,cpu"` reads as
    /// `{gpu; cpu}` rather than carrying an unsatisfiable empty class.
    let requiredResourceClasses (spec: ExternalWorkSpec) : Set<string> =
        match spec.ResourceHints |> Map.tryFind ResourceClassHint with
        | None -> Set.empty
        | Some declared ->
            declared.Split(',')
            |> Array.map _.Trim()
            |> Array.filter (System.String.IsNullOrWhiteSpace >> not)
            |> Set.ofArray

    /// The advisory hint keys that are NOT the reserved requirement key.
    /// These can prefer a backend, never refuse one.
    let private advisoryHintKeys (spec: ExternalWorkSpec) : Set<string> =
        spec.ResourceHints
        |> Map.toList
        |> List.map fst
        |> List.filter (fun key -> key <> ResourceClassHint)
        |> Set.ofList

    /// The refusal returned when no registered backend can serve `spec`.
    /// Names **what was required and what was available**, because
    /// "unsuitable" is not actionable and the operator's next move
    /// depends entirely on which side the gap is on: a profile gap means
    /// compose an isolating backend, a class gap means declare the class
    /// or add a node pool.
    ///
    /// Terminal, always. A fleet does not gain a capability by being
    /// asked twice — that is a composition change — and `Retriable =
    /// true` here would have a caller re-offering infeasible work on a
    /// timer forever.
    let private refusal
        (reason: string)
        (spec: ExternalWorkSpec)
        (candidates: ComputeBackendRegistration list)
        : ExternalComputeError =
        let required =
            let classes = requiredResourceClasses spec

            [
                sprintf "profile '%s'" (ExecutionProfile.label spec.Profile)
                if not (Set.isEmpty classes) then
                    sprintf "resource classes [%s]" (classes |> Set.toList |> String.concat ", ")
            ]
            |> String.concat ", "

        let available =
            match candidates with
            | [] -> "no backends are registered"
            | _ -> candidates |> List.map ComputeBackendRegistration.describe |> String.concat "; "

        ExternalComputeError.terminal (
            sprintf
                "no registered compute backend can serve work kind '%s': %s. Required: %s. Available: %s."
                spec.Kind
                reason
                required
                available
        )

    /// Choose the backend for `spec`, or refuse.
    ///
    /// **Precedence — profile, then resource fit, then the declared
    /// default.** Each step narrows the candidate set; the tie-break at
    /// every step is composition order, so the same registry and the same
    /// spec always select the same backend.
    ///
    /// 1. **Profile.** Only backends whose declared posture honours
    ///    `spec.Profile` are eligible. An `Isolated` spec therefore never
    ///    routes to a non-isolating backend — not as a preference the
    ///    later steps could override, but as the filter everything else
    ///    runs inside. This step is first precisely because it is the one
    ///    whose failure is a leak rather than a slow job.
    /// 2. **Hard resource classes.** Of those, only backends declaring
    ///    every class in the spec's `resource-class` hint. Empty here is
    ///    a refusal, not a fallback: no single backend covers the set, so
    ///    there is nowhere the work can actually run, and quietly sending
    ///    it to the default would trade a clear refusal in a second for a
    ///    backend-side failure much later (the same argument Phase 318's
    ///    "refuse a hint you understand but cannot honour" makes).
    /// 3. **Advisory hint fit.** Of those, the backends whose declared
    ///    classes cover the most remaining hint keys, when any covers at
    ///    least one. Advisory by construction — a spec whose hints match
    ///    nothing is not refused, it falls through.
    /// 4. **The declared default**, when it survived steps 1–2;
    ///    otherwise the sole survivor, otherwise the first in
    ///    composition order.
    let select
        (registry: ComputeBackendRegistry)
        (spec: ExternalWorkSpec)
        : Result<ComputeBackendRegistration, ExternalComputeError> =
        let all = registry.Registrations

        // 1 — profile.
        let profileEligible =
            all |> List.filter (ComputeBackendRegistration.honours spec.Profile)

        match profileEligible with
        | [] ->
            Error(
                refusal
                    (sprintf
                        "no backend declares a posture honouring ExecutionProfile.%s"
                        (ExecutionProfile.label spec.Profile))
                    spec
                    all
            )
        | eligible ->
            // 2 — hard resource classes.
            let required = requiredResourceClasses spec

            let classEligible =
                if Set.isEmpty required then
                    eligible
                else
                    eligible
                    |> List.filter (fun registration -> Set.isSubset required registration.ResourceClasses)

            match classEligible with
            | [] ->
                Error(refusal "no single profile-eligible backend declares every required resource class" spec eligible)
            | candidates ->
                // 3 — advisory hint fit.
                let advisory = advisoryHintKeys spec

                let scored =
                    candidates
                    |> List.map (fun registration ->
                        registration, Set.intersect advisory registration.ResourceClasses |> Set.count)

                let bestScore = scored |> List.map snd |> List.max

                if bestScore > 0 then
                    scored
                    |> List.filter (fun (_, score) -> score = bestScore)
                    |> List.head
                    |> fst
                    |> Ok
                else
                    // 4 — the declared default, then the sole survivor,
                    // then composition order.
                    let preferred =
                        candidates
                        |> List.tryFind _.IsDefault
                        |> Option.defaultValue (List.head candidates)

                    Ok preferred

    /// The backend a handle belongs to, or a typed refusal.
    ///
    /// **A handle whose kind is not registered is refused, never
    /// redirected.** `ExternalHandle.NativeRef` is the *issuing*
    /// backend's own opaque token, so polling it against a different
    /// backend asks a question about a job that backend has never heard
    /// of — and Phase 318 requires an unrecognised handle be reported as
    /// a terminal failure, so the redirect would manufacture a confident
    /// "this work failed" for work that is very likely running fine.
    ///
    /// This is the boundary for a handle minted **before** the router was
    /// composed (or by a backend since removed from the fleet): its kind
    /// is whatever the single Phase 318 dispatcher stamped. Register that
    /// dispatcher under its own `Backend` string as its `Kind` and every
    /// historical handle routes correctly; otherwise the outstanding ones
    /// are refused by name, which is at least legible and points straight
    /// at the fix.
    let selectForHandle
        (registry: ComputeBackendRegistry)
        (handle: ExternalHandle)
        : Result<ComputeBackendRegistration, ExternalComputeError> =
        match registry.TryFind handle.Backend with
        | Some registration -> Ok registration
        | None ->
            let registered =
                match registry.Kinds with
                | [] -> "none"
                | kinds -> kinds |> List.map (sprintf "'%s'") |> String.concat ", "

            Error(
                ExternalComputeError.terminal (
                    sprintf
                        "handle %O carries backend kind '%s', which is not registered in this compute fleet (registered: %s). The handle's NativeRef is the issuing backend's own opaque token, so it is refused rather than polled against a different backend — that would report a terminal failure for work the other backend has simply never heard of. If this handle predates the routing dispatcher, register its original dispatcher under Kind = '%s'."
                        handle.HandleId
                        handle.Backend
                        registered
                        handle.Backend
                )
            )
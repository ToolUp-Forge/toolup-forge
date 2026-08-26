// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.GrantPolicyGuard

open System
open ToolUp.Platform
open ToolUp.Platform.PermissionStore

// ─── Module-declared grant policy (Phase 551) ────────────────────────
//
// The admin-authored `ModulePermissions` map is the authority on WHO is
// granted a module. Before this phase it was the ONLY authority, so an
// accidental admin grant silently exposed module state and the module
// itself had no say. A module now declares a narrowing-only precondition
// on being granted at all — `ServerModule.withGrantPolicy` — and the
// declaration is enforced in TWO places:
//
//   * at the grant WRITE, by the `GrantPolicyPermissionStore` decorator
//     below (551.C), and
//   * at DISPATCH, by `RemotingHelpers.permissionGuardedApiCore` calling
//     `assertGrantLive` (551.D).
//
// **Both, not either.** The Phase 311 lesson is that a gate the write
// path must remember to call is a defect class rather than a control: a
// permission row written by a migration, a restored blob, a consumer
// calling the store directly, or an operator editing JSON never passes
// through the write guard at all. The dispatch check is the one that
// holds in every one of those cases, and it is the reason a grant row
// present without its consent artifact is INERT rather than merely
// unwritable.
//
// **Why a decorator and not a free helper.** `SchemaOnlyGuard` is a free
// helper deliberately — the store interface does not, and per GP 4 must
// not, carry caller identity, so the guard has to be threaded in by the
// handler. The grant-policy write guard has the opposite shape: it needs
// only the document and the compose-time policy registry, both of which
// the store already has or can be handed. Making it a decorator over
// `IPermissionStore` means there is nothing for a caller to remember,
// which is the whole point.
//
// **There is no second naming axis.** The registry is keyed by
// `ServerModule.Name` — the same string `permissionGuardedApiCore` is
// handed as its RBAC key and the same string `AccessContext.ModulePermissions`
// is keyed by — and it is accumulated in the same `addModule` fold that
// appends `ServerApp.ModuleNames`. A policy therefore cannot drift away
// from the module it governs the way Phase 36.A's `SourceModule` could
// drift from the permission-map keys it was matched against. `validate`
// below asserts the containment anyway, because a structural guarantee
// nobody checks is one refactor away from being a convention.

// ─── Registry ────────────────────────────────────────────────────────

/// The compose-time projection of every module's declared `GrantPolicy`,
/// keyed by module name. Value-typed (like `SurfaceRequirementRegistry`)
/// so the composition root can build it, assert over it, and hand the
/// same value to DI without a mutable service.
///
/// **Registered only when non-empty** (GP 13): a deployment where every
/// module carries the default `AdminDiscretion` composes no registry, so
/// the dispatch guard's service lookup misses, the store decorator is
/// never wrapped, and the per-request grant load never happens. Nothing
/// about such a deployment changes.
type ModuleGrantPolicyRegistry = {
    /// Module name → declared policy. Modules carrying the default
    /// `AdminDiscretion` are absent — `resolve` returns it for any name
    /// the map does not hold, so absence and the default are the same
    /// thing by construction.
    Policies: Map<string, GrantPolicy>
}

module ModuleGrantPolicyRegistry =
    let empty = { Policies = Map.empty }

    /// Build from the accumulated `(moduleName, policy)` declarations,
    /// dropping the `AdminDiscretion` default so an all-default
    /// deployment yields `empty` and pays nothing.
    let ofDeclarations (declarations: (string * GrantPolicy) list) = {
        Policies =
            declarations
            |> List.filter (fun (_, p) -> p <> GrantPolicy.AdminDiscretion)
            |> Map.ofList
    }

    let isEmpty (registry: ModuleGrantPolicyRegistry) = registry.Policies.IsEmpty

    /// The policy declared for a module. `AdminDiscretion` for any module
    /// that declared nothing — the pre-551 behaviour.
    let resolve (registry: ModuleGrantPolicyRegistry) (moduleName: string) =
        registry.Policies
        |> Map.tryFind moduleName
        |> Option.defaultValue GrantPolicy.AdminDiscretion

    /// Every module name the registry holds a policy for that is NOT in
    /// the composed module set. Empty by construction today (the registry
    /// is folded from the same `ServerModule` records that produce
    /// `ModuleNames`); checked at compose so a future refactor that
    /// introduced a second naming axis would fail loudly rather than
    /// silently stop enforcing — the Phase 36.A silent-failure shape.
    let orphans (registry: ModuleGrantPolicyRegistry) (moduleNames: string list) =
        let known = Set.ofList moduleNames

        registry.Policies
        |> Map.toList
        |> List.map fst
        |> List.filter (fun name -> not (known.Contains name))

// ─── Dispatch-time enforcement (551.D) ───────────────────────────────

/// `HttpContext.Items` key carrying the acting subject's grant records
/// for the active team, module-keyed. Stamped by
/// `ScopeResolutionMiddleware` alongside `ToolUp.ModulePermissions`, and
/// ONLY when a non-empty `ModuleGrantPolicyRegistry` is composed — a
/// deployment declaring no policy performs no extra store read (GP 13).
[<Literal>]
let ModuleGrantsItemsKey = "ToolUp.ModuleGrants"

/// The grant records stamped for this request, module-keyed. Empty when
/// nothing was stamped, which is the whole of a deployment that declares
/// no policy — and which is safe precisely because `resolve` then returns
/// `AdminDiscretion`, for which `isGrantLive` is unconditionally true.
let grantsFromItems (items: System.Collections.Generic.IDictionary<obj, obj>) : Map<string, ModuleGrantRecord> =
    match items.TryGetValue(box ModuleGrantsItemsKey) with
    | true, (:? Map<string, ModuleGrantRecord> as grants) -> grants
    | _ -> Map.empty

/// Is the caller's permission entry on `moduleName` actually live under
/// the module's declared policy? Pure — the audit emission is the
/// caller's, so this stays testable without a log.
let isDispatchGrantLive
    (registry: ModuleGrantPolicyRegistry)
    (grants: Map<string, ModuleGrantRecord>)
    (moduleName: string)
    =
    let policy = ModuleGrantPolicyRegistry.resolve registry moduleName
    GrantPolicy.isGrantLive policy (Map.tryFind moduleName grants)

/// The `UnconsentedGrantRefused` payload for a refusal on `moduleName`.
/// Built separately from the emission so a caller with no audit log (or
/// a test) can assert the exact row that would be written.
let refusalPayload
    (registry: ModuleGrantPolicyRegistry)
    (grants: Map<string, ModuleGrantRecord>)
    (userId: string)
    (moduleName: string)
    : UnconsentedGrantRefusedPayload =
    let policy = ModuleGrantPolicyRegistry.resolve registry moduleName

    {
        UserId = userId
        ModuleName = moduleName
        DeclaredPolicy = GrantPolicy.toToken policy
        InertReason = GrantPolicy.inertReason policy (Map.tryFind moduleName grants)
    }

/// **The whole dispatch-time control, decision and audit together.**
///
/// `Ok ()` when the caller's permission entry on `moduleName` carries
/// authority; `Error payload` — with the `UnconsentedGrantRefused` row
/// already emitted — when it is present but inert. The caller turns the
/// error into its protocol refusal (`permissionGuardedApiCore` raises
/// `UnauthorizedAccessException`, which the Remoting error handler
/// renders as HTTP 403).
///
/// `schedule` runs the best-effort audit emission. Production passes
/// `Async.Start` (audit is best-effort per the `IAuditLog` contract and
/// must never block a refusal); a test passes a synchronous scheduler
/// and observes the decision AND the row from one call — so "refuses"
/// and "audits" cannot drift apart with only one of them covered.
///
/// Emission failures are swallowed, deliberately: the control is the
/// refusal, not the row, and a downed audit pipeline must not turn a
/// denial into an admission.
let guardDispatch
    (registry: ModuleGrantPolicyRegistry)
    (grants: Map<string, ModuleGrantRecord>)
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (scopeId: string)
    (userId: string)
    (moduleName: string)
    : Result<unit, UnconsentedGrantRefusedPayload> =
    if ModuleGrantPolicyRegistry.isEmpty registry then
        // No module declares a policy — the pre-551 path, taken without
        // touching grants, the audit log, or the scheduler.
        Ok()
    elif isDispatchGrantLive registry grants moduleName then
        Ok()
    else
        let payload = refusalPayload registry grants userId moduleName

        match auditLog with
        | Some log ->
            schedule (
                async {
                    try
                        do! log.Record(scopeId, UnconsentedGrantRefused payload)
                    with _ ->
                        ()
                }
            )
        | None -> ()

        Error payload

// ─── The counterparty seam (Phase 552) ───────────────────────────────

/// Phase 552 — resolves whether a live, signature-valid, unexpired,
/// unrevoked consent record exists for one (subject × module) grant under
/// the party a module's `RequiresCounterpartyApproval` arm names.
///
/// **An interface rather than a function type, and declared HERE rather
/// than beside the store**, for one reason each. An interface because the
/// write guard below must be constructible with and without it and the
/// two constructors have to disambiguate by type; here because this file
/// compiles before `GrantConsentStore.fs`, which supplies the shipped
/// implementation over `IGrantConsentStore` + `IGrantConsentVerifier`.
///
/// The seam is what keeps Phase 551 honest about what it does NOT know.
/// 551 refused every counterparty grant because nothing in the estate
/// could produce the artifact; it did not hard-code that refusal into the
/// concept. `denyAll` below IS that refusal, named — so a deployment with
/// no consent registry behaves exactly as 551 shipped, and the difference
/// between "refuses because it is impossible" and "refuses because it is
/// unconfigured" stays visible rather than being folded into a `match`
/// arm nobody can compose over.
type CounterpartyConsentOracle =
    /// `true` only when consent is live for this exact grant under this
    /// exact party, verified NOW. Async because resolving it is a store
    /// read plus a signature check (GP 12 rule 2).
    abstract IsConsentLive: teamId: string * subjectId: string * moduleName: string * party: PartyRef -> Async<bool>

module CounterpartyConsentOracle =
    /// The pre-552 oracle: nothing is ever consented. What a deployment
    /// composing no consent registry gets, and what every existing
    /// construction of the decorator below keeps getting.
    let denyAll =
        { new CounterpartyConsentOracle with
            member _.IsConsentLive(_, _, _, _) = async { return false }
        }

// ─── Write-time enforcement (551.C) ──────────────────────────────────

/// Evidence an administrator presents with a grant. Carried explicitly
/// rather than ambiently, so a grant that satisfies
/// `RequiresAcknowledgement` is visibly a different call from one that
/// does not — the legacy `IPermissionStore.SetMemberPermissions`
/// signature has nowhere to put this, and that is why the decorator
/// refuses a policy-bearing grant written through it.
type GrantEvidence = {
    /// Explicit confirmation. `false` is not "unknown" — it is the
    /// absence of the acknowledgement the policy demands.
    Acknowledged: bool
    /// Free-text reason. Must be non-whitespace under
    /// `RequiresAcknowledgement` or stricter.
    Justification: string
}

module GrantEvidence =
    /// No evidence at all — what a legacy write path carries.
    let none = {
        Acknowledged = false
        Justification = ""
    }

    let acknowledged (justification: string) = {
        Acknowledged = true
        Justification = justification
    }

/// One grant, with its evidence. The `PermissionGrants.grantModuleAccess`
/// argument.
type ModuleGrantRequest = {
    TeamId: string
    /// The administrator performing the grant — audit attribution.
    ActorId: string
    /// The subject being granted.
    SubjectId: string
    ModuleName: string
    /// The permissions to grant. An empty list is a REVOCATION and is
    /// always admitted: narrowing never needs a precondition.
    Permissions: ModulePermission list
    Evidence: GrantEvidence
}

/// Evaluate one grant against a declared policy. Returns the record to
/// persist (`None` under `AdminDiscretion` — nothing is recorded, so an
/// undeclared deployment's documents keep their exact pre-551 bytes), or
/// a typed refusal naming the policy.
let evaluateGrant
    (policy: GrantPolicy)
    (moduleName: string)
    (subjectId: string)
    (permissions: ModulePermission list)
    (evidence: GrantEvidence)
    : Result<ModuleGrantRecord option, GrantRefusal> =
    if List.isEmpty permissions then
        // Revocation. Always admitted — a policy constrains the creation
        // of authority, never its removal.
        Ok None
    else
        match policy with
        | GrantPolicy.AdminDiscretion -> Ok None
        | GrantPolicy.RequiresCounterpartyApproval party ->
            // Phase 552 ships `IGrantConsentStore`; until it does there
            // is no artifact that could satisfy this arm, so it refuses
            // rather than admitting a grant nothing verified.
            Error(GrantRefusal.CounterpartyApprovalUnavailable(moduleName, party))
        | GrantPolicy.RequiresAcknowledgement
        | GrantPolicy.RequiresSubjectConsent ->
            if not evidence.Acknowledged then
                Error(GrantRefusal.AcknowledgementRequired(moduleName, policy))
            elif String.IsNullOrWhiteSpace evidence.Justification then
                Error(GrantRefusal.JustificationRequired(moduleName, policy))
            else
                // `subjectId` is not read on this path today; it is part
                // of the signature so Phase 552's counterparty arm can
                // bind a consent record to the subject without the
                // signature change rippling through the decorator.
                ignore subjectId

                Ok(
                    Some {
                        // Subject consent is not the admin's to give: the
                        // grant is recorded and inert until the grantee
                        // accepts. Acknowledgement is the admin's own
                        // ceremony, so that arm is live immediately.
                        State =
                            match policy with
                            | GrantPolicy.RequiresSubjectConsent -> GrantState.PendingConsent
                            | _ -> GrantState.Active
                        SatisfiedPolicy = policy
                        Justification = evidence.Justification.Trim()
                        ConsentedBy = None
                    }
                )

/// Does a written permission entry have adequate backing? Used by the
/// decorator to validate what a write PRODUCES, rather than trusting how
/// it was called.
let private isBacked (policy: GrantPolicy) (record: ModuleGrantRecord option) =
    match policy with
    | GrantPolicy.AdminDiscretion -> true
    // Phase 552 — the counterparty arm's backing is BOTH halves: this
    // record AND a live consent artifact. `isBacked` is the pure,
    // document-only half, so it answers `false` here and the async
    // `validateEntry` below adds the oracle. Keeping it out of this
    // function is deliberate: a pure predicate that silently meant
    // "backed, assuming someone else checked the registry" is the shape
    // that produces a guard nobody can read correctly.
    | GrantPolicy.RequiresCounterpartyApproval _ -> false
    | _ ->
        match record with
        | None -> false
        // A `PendingConsent` record is adequate BACKING even though it is
        // not live at dispatch: it is a legitimately written grant
        // awaiting acceptance. Liveness is the dispatch question; backing
        // is the write question, and conflating them would make the
        // pending state unwritable.
        | Some r -> GrantPolicy.strictness r.SatisfiedPolicy >= GrantPolicy.strictness policy

/// Phase 552 — is a counterparty-arm grant record adequate backing on the
/// DOCUMENT side? Exact policy equality, not a strictness comparison:
/// every `RequiresCounterpartyApproval` arm ranks 3, so `>=` would let a
/// record whose evidence was gathered for party A satisfy a module that
/// now requires party B. The parties are incomparable — which is the same
/// judgement `GrantPolicy.tighten` makes at compose time, applied here to
/// evidence rather than to declarations.
let private isCounterpartyRecordAdequate (policy: GrantPolicy) (record: ModuleGrantRecord option) =
    match record with
    | None -> false
    | Some r -> r.State = GrantState.Active && r.SatisfiedPolicy = policy

/// Validate one (subject, module, permissions) grant against the
/// registry, the records present in the document being written, and —
/// for the counterparty arm — the live consent registry.
///
/// Async because the counterparty arm consults a store; every other arm
/// resolves without touching it, so an estate that declares no
/// counterparty policy performs no consent work here at all (GP 13).
let private validateEntry
    (registry: ModuleGrantPolicyRegistry)
    (consent: CounterpartyConsentOracle)
    (teamId: string)
    (records: Map<string, Map<string, ModuleGrantRecord>>)
    (userId: string)
    (moduleName: string)
    (permissions: ModulePermission list)
    : Async<Result<unit, GrantRefusal>> =
    async {
        if List.isEmpty permissions then
            return Ok()
        else
            let policy = ModuleGrantPolicyRegistry.resolve registry moduleName

            match policy with
            | GrantPolicy.AdminDiscretion -> return Ok()
            | GrantPolicy.RequiresCounterpartyApproval party ->
                // Phase 552 — BOTH halves, and in this order. The consent
                // is asked about first because it is the expensive, live
                // fact; a document record without it is exactly the forged
                // row Phase 551 recorded as refusable, and a live consent
                // without a record is a grant nobody actually wrote.
                let record = records |> Map.tryFind userId |> Option.bind (Map.tryFind moduleName)

                let! consented = consent.IsConsentLive(teamId, userId, moduleName, party)

                if consented && isCounterpartyRecordAdequate policy record then
                    return Ok()
                elif consented then
                    // Consent is live but the document carries no adequate
                    // record — the write is trying to create authority
                    // without recording what satisfied the policy.
                    return Error(GrantRefusal.UnbackedGrant(moduleName, policy))
                else
                    return Error(GrantRefusal.CounterpartyApprovalUnavailable(moduleName, party))
            | _ ->
                let record = records |> Map.tryFind userId |> Option.bind (Map.tryFind moduleName)

                if isBacked policy record then
                    return Ok()
                else
                    return Error(GrantRefusal.UnbackedGrant(moduleName, policy))
    }

/// A policy-bearing module may never be handed out through team
/// DEFAULTS: a default applies to every member who lacks an explicit
/// entry, so there is no subject to acknowledge, consent, or be recorded
/// against. Refused rather than silently ineffective.
let private validateDefaults
    (registry: ModuleGrantPolicyRegistry)
    (previous: Map<string, ModulePermission list>)
    (defaults: Map<string, ModulePermission list>)
    : Result<unit, GrantRefusal> =
    defaults
    |> Map.toList
    |> List.filter (fun (moduleName, perms) ->
        // Only entries the write ADDS or CHANGES. An operator must be
        // able to repair a document without every untouched row being
        // re-litigated.
        not (List.isEmpty perms) && Map.tryFind moduleName previous <> Some perms)
    |> List.tryPick (fun (moduleName, _) ->
        match ModuleGrantPolicyRegistry.resolve registry moduleName with
        | GrantPolicy.AdminDiscretion -> None
        | policy -> Some(Error(GrantRefusal.UnbackedGrant(moduleName, policy))))
    |> Option.defaultValue (Ok())

/// `IPermissionStore` decorator enforcing module-declared grant policy on
/// every write path. Composed ONLY when the registry is non-empty, so a
/// deployment that declares no policy resolves the undecorated store and
/// is byte-for-byte unchanged (GP 11 / GP 13).
///
/// `auditLog` is optional so the decorator is constructible in tests and
/// in a composition with no audit substrate; a refusal without a log
/// still refuses (audit is best-effort per the `IAuditLog` contract, and
/// the control is the refusal, not the row).
/// **Phase 552 note on the constructors.** The primary now takes a
/// `CounterpartyConsentOracle`, and the secondary preserves the pre-552
/// argument shape exactly — `(inner, registry, ?auditLog, ?actorId)` —
/// delegating with `CounterpartyConsentOracle.denyAll`. That is a
/// deliberate two-constructor shape rather than a fifth optional
/// argument: an optional parameter folds into ONE widened constructor,
/// so adding it would DELETE the existing `..ctor` token from the
/// public-API baseline (a genuine break, per the documented Phase 175
/// rule), while an explicit secondary keeps that token and adds one.
/// Every existing call site, in the SDK and in a consumer, compiles and
/// behaves identically — and gets the pre-552 refusal by name.
type GrantPolicyPermissionStore
    (
        inner: IPermissionStore,
        registry: ModuleGrantPolicyRegistry,
        consent: CounterpartyConsentOracle,
        auditLog: IAuditLog option,
        actorId: string option
    ) =

    let actor = defaultArg actorId "unknown"

    let record (subjectId: string) (moduleName: string) (refusal: GrantRefusal) (teamId: string) =
        match auditLog with
        | None -> ()
        | Some log ->
            let payload: GrantPolicyRefusedPayload = {
                ActorId = actor
                SubjectId = subjectId
                ModuleName = moduleName
                DeclaredPolicy = ModuleGrantPolicyRegistry.resolve registry moduleName |> GrantPolicy.toToken
                RefusalCode = GrantRefusal.code refusal
            }

            // Best-effort per the IAuditLog contract — the refusal below
            // fires regardless of whether the row lands.
            async {
                try
                    do! log.Record($"team-{teamId}", GrantPolicyRefused payload)
                with _ ->
                    ()
            }
            |> Async.Start

    let refuse (subjectId: string) (moduleName: string) (refusal: GrantRefusal) (teamId: string) =
        record subjectId moduleName refusal teamId
        Error(GrantRefusal.describe refusal)

    /// The pre-552 shape: no consent registry, so the counterparty arm
    /// refuses every grant exactly as Phase 551 shipped it. Preserved as
    /// an explicit secondary constructor, not folded into an optional
    /// parameter — see the type's doc comment for why.
    new(inner: IPermissionStore, registry: ModuleGrantPolicyRegistry, ?auditLog: IAuditLog, ?actorId: string) =
        GrantPolicyPermissionStore(inner, registry, CounterpartyConsentOracle.denyAll, auditLog, actorId)

    interface IPermissionStore with
        member _.GetTeamPermissions teamId = inner.GetTeamPermissions teamId

        member _.GetEffectivePermissions(userId, teamId) =
            inner.GetEffectivePermissions(userId, teamId)

        member _.GetModuleExposure teamId = inner.GetModuleExposure teamId

        member _.SetModuleExposure(teamId, moduleName, state) =
            inner.SetModuleExposure(teamId, moduleName, state)

        member _.SetTeamPermissions(teamId, permissions) = async {
            // Whole-document replacement: validate every member entry the
            // write adds or widens, against the records the SAME document
            // carries. This is what makes `PermissionGrants.grantModuleAccess`
            // work through the decorator rather than around it — it writes
            // the permission entry and its record together, so the pair
            // validates.
            let! previous = inner.GetTeamPermissions teamId

            let changedEntries = [
                for KeyValue(userId, byModule) in permissions.Members do
                    let priorForUser =
                        previous.Members |> Map.tryFind userId |> Option.defaultValue Map.empty

                    for KeyValue(moduleName, perms) in byModule do
                        if Map.tryFind moduleName priorForUser <> Some perms then
                            userId, moduleName, perms
            ]

            // Sequential rather than parallel: the counterparty arm reads
            // the consent registry, and the first refusal is the answer —
            // fanning out would perform reads for entries whose verdict is
            // already decided, against an authorization store, per write.
            let! entryFailure =
                changedEntries
                |> List.fold
                    (fun acc (userId, moduleName, perms) -> async {
                        match! acc with
                        | Some _ as found -> return found
                        | None ->
                            match!
                                validateEntry registry consent teamId permissions.Grants userId moduleName perms
                            with
                            | Ok() -> return None
                            | Error e -> return Some(userId, moduleName, e)
                    })
                    (async { return None })

            match entryFailure with
            | Some(userId, moduleName, e) -> return refuse userId moduleName e teamId
            | None ->
                match validateDefaults registry previous.Defaults permissions.Defaults with
                | Error e ->
                    let moduleName =
                        match e with
                        | GrantRefusal.UnbackedGrant(m, _)
                        | GrantRefusal.CounterpartyApprovalUnavailable(m, _) -> m
                        | _ -> ""

                    return refuse "" moduleName e teamId
                | Ok() -> return! inner.SetTeamPermissions(teamId, permissions)
        }

        member _.SetMemberPermissions(teamId, userId, moduleName, permissions) = async {
            // The legacy write path. It has nowhere to carry
            // acknowledgement or justification, so a policy-bearing grant
            // through it is refused by construction and the caller is
            // pointed at `PermissionGrants.grantModuleAccess`. Revocation
            // (empty list) and every `AdminDiscretion` module are
            // unaffected — which is every existing call site.
            if List.isEmpty permissions then
                return! inner.SetMemberPermissions(teamId, userId, moduleName, permissions)
            else
                match ModuleGrantPolicyRegistry.resolve registry moduleName with
                | GrantPolicy.AdminDiscretion ->
                    return! inner.SetMemberPermissions(teamId, userId, moduleName, permissions)
                | GrantPolicy.RequiresCounterpartyApproval party as policy ->
                    // Phase 552 — symmetric with the arms below: a RE-grant
                    // of a module whose consent is live and whose record is
                    // already adequate is a no-op on the policy question and
                    // is let through. What this path still cannot do is
                    // CREATE the record — it has nowhere to carry the
                    // evidence — so a first counterparty grant refuses here
                    // and points at `GrantConsents.grantWithCounterpartyApproval`.
                    let! existing = inner.GetTeamPermissions teamId

                    let existingRecord =
                        existing.Grants |> Map.tryFind userId |> Option.bind (Map.tryFind moduleName)

                    let! consented = consent.IsConsentLive(teamId, userId, moduleName, party)

                    if consented && isCounterpartyRecordAdequate policy existingRecord then
                        return! inner.SetMemberPermissions(teamId, userId, moduleName, permissions)
                    elif consented then
                        return refuse userId moduleName (GrantRefusal.UnbackedGrant(moduleName, policy)) teamId
                    else
                        return
                            refuse
                                userId
                                moduleName
                                (GrantRefusal.CounterpartyApprovalUnavailable(moduleName, party))
                                teamId
                | policy ->
                    // An adequate record may already exist (a re-grant of
                    // an already-consented module), in which case the
                    // write is a no-op on the policy question and is let
                    // through.
                    let! existing = inner.GetTeamPermissions teamId

                    let record =
                        existing.Grants |> Map.tryFind userId |> Option.bind (Map.tryFind moduleName)

                    if isBacked policy record then
                        return! inner.SetMemberPermissions(teamId, userId, moduleName, permissions)
                    else
                        return
                            refuse userId moduleName (GrantRefusal.AcknowledgementRequired(moduleName, policy)) teamId
        }

        member _.SetTeamDefaults(teamId, defaults) = async {
            let! previous = inner.GetTeamPermissions teamId

            match validateDefaults registry previous.Defaults defaults with
            | Error e ->
                let moduleName =
                    match e with
                    | GrantRefusal.UnbackedGrant(m, _)
                    | GrantRefusal.CounterpartyApprovalUnavailable(m, _) -> m
                    | _ -> ""

                return refuse "" moduleName e teamId
            | Ok() -> return! inner.SetTeamDefaults(teamId, defaults)
        }

// ─── Policy-aware grant entry points ─────────────────────────────────

/// The write path a policy-aware admin surface uses. Writes the
/// permission entry and its grant record in ONE document update, so the
/// pair is never momentarily inconsistent and the decorator's validation
/// sees them together.
module PermissionGrants =

    /// Grant `request.Permissions` on `request.ModuleName` to
    /// `request.SubjectId`, honouring the module's declared policy.
    ///
    /// Returns the typed outcome — `Granted`, or `RecordedPendingConsent`
    /// when the module requires the subject's own acceptance — or a typed
    /// `GrantRefusal` naming the policy that refused. Nothing is
    /// persisted on a refusal.
    let grantModuleAccess
        (store: IPermissionStore)
        (registry: ModuleGrantPolicyRegistry)
        (request: ModuleGrantRequest)
        : Async<Result<GrantWriteOutcome, GrantRefusal>> =
        async {
            let policy = ModuleGrantPolicyRegistry.resolve registry request.ModuleName

            match evaluateGrant policy request.ModuleName request.SubjectId request.Permissions request.Evidence with
            | Error refusal -> return Error refusal
            | Ok recordOpt ->
                let! existing = store.GetTeamPermissions request.TeamId

                let priorForUser =
                    existing.Members
                    |> Map.tryFind request.SubjectId
                    |> Option.defaultValue Map.empty

                let updatedForUser =
                    if List.isEmpty request.Permissions then
                        priorForUser |> Map.remove request.ModuleName
                    else
                        priorForUser |> Map.add request.ModuleName request.Permissions

                let updatedMembers =
                    if Map.isEmpty updatedForUser then
                        existing.Members |> Map.remove request.SubjectId
                    else
                        existing.Members |> Map.add request.SubjectId updatedForUser

                let priorGrants =
                    existing.Grants
                    |> Map.tryFind request.SubjectId
                    |> Option.defaultValue Map.empty

                let updatedGrantsForUser =
                    match recordOpt with
                    | Some r -> priorGrants |> Map.add request.ModuleName r
                    // A revocation, or an `AdminDiscretion` grant, clears
                    // any stale record so the document does not accumulate
                    // evidence for authority that no longer exists.
                    | None -> priorGrants |> Map.remove request.ModuleName

                let updatedGrants =
                    if Map.isEmpty updatedGrantsForUser then
                        existing.Grants |> Map.remove request.SubjectId
                    else
                        existing.Grants |> Map.add request.SubjectId updatedGrantsForUser

                let! written =
                    store.SetTeamPermissions(
                        request.TeamId,
                        {
                            existing with
                                Members = updatedMembers
                                Grants = updatedGrants
                        }
                    )

                match written with
                | Error _ ->
                    // The decorator refused, or storage did. Either way
                    // nothing was persisted; surface it as the refusal the
                    // policy would have produced rather than inventing a
                    // success.
                    return Error(GrantRefusal.UnbackedGrant(request.ModuleName, policy))
                | Ok() ->
                    return
                        Ok(
                            match recordOpt with
                            | Some r when r.State = GrantState.PendingConsent ->
                                GrantWriteOutcome.RecordedPendingConsent request.SubjectId
                            | _ -> GrantWriteOutcome.Granted
                        )
        }

    /// The grantee accepts a grant recorded `PendingConsent` under
    /// `RequiresSubjectConsent`, making it live. Idempotent on an
    /// already-`Active` record. Refuses when no record exists — there is
    /// nothing to consent to, and minting one here would let the subject
    /// manufacture their own authority.
    let acceptGrant
        (store: IPermissionStore)
        (teamId: string)
        (subjectId: string)
        (moduleName: string)
        : Async<Result<unit, string>> =
        async {
            let! existing = store.GetTeamPermissions teamId

            let record =
                existing.Grants |> Map.tryFind subjectId |> Option.bind (Map.tryFind moduleName)

            match record with
            | None ->
                return
                    Error
                        $"GRANT-POLICY-NO-PENDING-GRANT: no grant record for subject '{subjectId}' on module '{moduleName}'."
            | Some r ->
                let accepted = {
                    r with
                        State = GrantState.Active
                        ConsentedBy = Some subjectId
                }

                let forUser =
                    existing.Grants
                    |> Map.tryFind subjectId
                    |> Option.defaultValue Map.empty
                    |> Map.add moduleName accepted

                return!
                    store.SetTeamPermissions(
                        teamId,
                        {
                            existing with
                                Grants = existing.Grants |> Map.add subjectId forUser
                        }
                    )
        }
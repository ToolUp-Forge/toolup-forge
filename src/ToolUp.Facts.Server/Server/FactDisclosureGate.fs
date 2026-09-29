// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactDisclosureGate (Phase 525 + Phase 562 taint) ────────────────
//
// The one `IFactDisclosureGate` implementation — every egress choke point
// (retrieval, tool results, narrative publication, export, webhook)
// consults this gate, and the gate applies the one `DisclosureEgress`
// predicate, so enforcement is never re-implemented per surface. Scope
// isolation is structural (GP 4): fact lookup goes through the scope-
// filtered `IFactStore.Get`, so an id from another scope is unresolvable —
// and an unresolvable id is *denied*, never disclosed. Disclosure
// therefore never widens scope, and scope never overrides a deny.
//
// **Phase 562 — taint propagation.** When a `DisclosureTaintConfig` is
// composed, the gate layers a derivation-walk verdict on top of the Phase
// 525 predicate: a fact whose derivation includes a
// `Restricted(TaintPropagating)` input is denied even when its own
// disclosure permits egress, *unless* the path crosses a declared
// declassification routine. Each declassification-crossing on a disclosed
// fact is audited (GP 6). An **empty** config (the default) skips the walk
// entirely — the gate is byte-for-byte the Phase 525 gate (GP 11 / GP 13).
//
// **Phase 674 — multi-party conjunction.** When policies declare a
// contributor scope, the taint verdict becomes a conjunction over the
// contributing parties: the derivation path must satisfy EVERY one, and a
// declassification routine clears only the parties that accepted it. One
// party's consent therefore never launders another's data. Fail-closed —
// absent or unevaluable acceptance denies — and inert when no policy
// declares a scope, so a single-party deployment is byte-for-byte the
// Phase 562 gate. The contributor scope is resolved from the composed
// policy vocabulary keyed by the ref the STORED fact carries; no party
// identity is ever read from the caller.
//
// **Phase 675 — declassification budgets.** A declassification routine
// that is safe to cross once is a routine a counterparty may cross a
// thousand times, and the walk above cannot notice: every crossing was
// permitted. When a `DeclassificationBudgetConfig` is composed the gate
// therefore RESERVES each budgeted crossing's charge before finalising
// the verdict and SETTLES it after, per contributing party, through the
// Phase 190 ledger seam. An exhausted ceiling denies with the same typed,
// audited refusal shape a policy denial takes (GP 6) — never a silent
// allow, and never a quantity on the wire. No config composed ⇒ the whole
// path is one option match and behaviour is byte-for-byte pre-675
// (GP 11 / GP 13). The honesty framing — accounting bounds *questions
// asked*; only a noise-drawing routine earns a DP claim; basic sequential
// composition; collusion out of scope — is stated in full in
// `DeclassificationBudget.fs`, which is also where the registration-time
// refusal of an epsilon charge on a deterministic routine lives.
//
// **Audit (GP 6).** Every deny writes a `FactDisclosureDenied` event
// (surface, fact id, metric, policy ref, principal — never the value); a
// declassification-cleared disclosure writes a `FactDisclosureDeclassified`
// event per crossing; and — Phase 674.C — a disclosure carrying a
// contributing party's data writes a `FactDisclosureAccessed` event. All
// three carry the contributor scopes involved, so the per-party
// projection (plan D4) is derivable from this one stream. They ride the
// reserved `_facts` source module, joining the assert / supersession trail
// in one queryable record. Emission is best-effort: an audit-write failure
// never flips the verdict.
//
// GP 12: stateless between calls; identity by value; async at the
// boundary; scope is the shard key.

/// The ambient claimed-purpose carrier (Phase 592). A request states the
/// purpose it claims — typically resolved by the composition's handler
/// from a header / tool argument — and the gate reads it at evaluation
/// time. Ambient per GP 7 (request context rides the async chain), so
/// the `IFactDisclosureGate` seam's signature stays unchanged and every
/// existing choke point is purpose-checked without edits. With a
/// declared taxonomy, an unclaimed purpose refuses at every
/// purpose-bound surface (default-deny-by-shape); without one the
/// carrier is inert data nothing reads.
module FactPurposeContext =

    let private claimed = System.Threading.AsyncLocal<string option>()

    /// The purpose the current async chain claims, if any.
    let current () : string option =
        match claimed.Value with
        | Some p -> Some p
        | _ -> None

    /// Claim a purpose for the current async chain (flows into child
    /// asyncs, never into siblings or parents).
    let claim (purpose: string) : unit = claimed.Value <- Some purpose

    /// Clear the current chain's claim.
    let clear () : unit = claimed.Value <- None

// ─── Phase 896 — the viewer a disclosure decision is made for ─────────
//
// The gate decides a `Restricted` fact for a VIEWER once team output
// visibility is composed. Who the viewer is comes from the request's
// platform-resolved access context through the ambient
// `RequestViewerContext` — established by the middleware below for an HTTP
// request, and by a background worker for a turn it runs on a user's
// behalf — never from an argument the calling door passes. A check with no
// established viewer (a job or a webhook over a carried scope) evaluates as
// the least-privileged viewer, and so does a door whose output reaches an
// audience wider than the requester.

/// Establishes the ambient request viewer for an HTTP request (Phase 896).
module FactViewerContext =

    // The request's viewer, read lazily off the items scope resolution
    // stamps (this middleware runs before it), and snapshotted when the
    // request ends, so work the request started that outlives it still
    // reads the same viewer — and never reads a recycled context.
    type private ViewerCell(ctx: HttpContext) =
        let sync = obj ()
        let mutable live = ctx
        let mutable snapshot: RequestViewer option = None
        let mutable closed = false

        member _.Read() : RequestViewer option =
            lock sync (fun () ->
                match snapshot with
                | Some _ -> snapshot
                | None when not closed ->
                    let viewer = RequestViewer.ofItems live.Items

                    if viewer.IsSome then
                        snapshot <- viewer

                    viewer
                | None -> None)

        member _.Close() =
            lock sync (fun () ->
                if not closed then
                    if snapshot.IsNone then
                        snapshot <- RequestViewer.ofItems live.Items

                    closed <- true
                    live <- null)

    /// The middleware body: establish the request's viewer for everything
    /// the request runs. Registered by `FactsCompose.withTeamOutputVisibility`
    /// ahead of scope resolution, which is why the viewer is read lazily.
    let middleware (ctx: HttpContext) (next: Func<Task>) : Task = task {
        let cell = ViewerCell ctx
        use _ = RequestViewerContext.establish cell.Read

        try
            do! next.Invoke()
        finally
            cell.Close()
    }

/// The lookups the gate resolves a viewer through (Phase 896). The viewer's
/// IDENTITY is never among them — that is the ambient request viewer — only
/// facts about teams: a team's output level, a member's role, and whether a
/// scope id names a team at all.
type DisclosureViewerSources = {
    /// The output level in force for a team: its own choice, else the
    /// deployment default. `Error` when it cannot be read — every
    /// `Restricted` fact is then denied.
    OutputVisibility: string -> Async<Result<TeamVisibilityLevel, string>>
    /// A user's role in a team (`teamId`, then `userId`); `None` when not a
    /// member.
    TeamRole: string -> string -> Async<TeamRole option>
    /// Whether a scope id names a team — consulted only for a string-keyed
    /// check whose scope the requester's own active team does not explain.
    IsTeam: string -> Async<bool>
}

/// The gate's viewer-aware facet (Phase 896), armed by
/// `FactsCompose.withTeamOutputVisibility`.
type ViewerAwareDisclosure = {
    /// The viewer-aware resolver. `None` uses the shipped rule over the
    /// gate's own resolver:
    /// `ViewerAwareDisclosurePolicyResolver.withTeamOutputVisibility`.
    Resolver: ViewerAwareDisclosurePolicyResolver option
    /// Where team facts are read.
    Sources: DisclosureViewerSources
}

/// Viewer resolution for the gate (Phase 896).
module ViewerAwareDisclosure =

    /// The policy ref a `Restricted` fact is denied under when the team's
    /// output level cannot be read (fail closed).
    [<Literal>]
    let UnreadableRef = "team-output-visibility-unreadable"

    /// Whether a surface's audience is the requester. Retrieval, tool
    /// results, exports and browsing reach the person asking, so they are
    /// decided for that person. Publishing a narrative, a webhook and a
    /// peer answer reach an audience wider than the requester, so they are
    /// decided for the least-privileged viewer — publishing is never a way
    /// to widen who sees restricted output.
    let audienceIsRequester (surface: FactEgressSurface) : bool =
        match surface with
        | FactRetrieval
        | FactToolResult
        | FactExport
        | FactBrowse -> true
        | FactNarrativePublication
        | FactWebhook
        | FactPeerEgress -> false

    /// The team a checked scope belongs to. `scopeTeam` is what the scope's
    /// own resolution says (`Some` for a `ResolvedScope`); a string-keyed
    /// check falls back to the requester's active team, then to the team
    /// store.
    let private teamOf
        (sources: DisclosureViewerSources)
        (requester: RequestViewer option)
        (scopeId: string)
        (scopeTeam: string option option)
        : Async<string option> =
        match scopeTeam with
        | Some team -> async.Return team
        | None ->
            match requester with
            | Some viewer when viewer.ActiveTeamId = Some scopeId -> async.Return(Some scopeId)
            | _ -> async {
                let! isTeam = sources.IsTeam scopeId
                return if isTeam then Some scopeId else None
              }

    /// The viewer a check is decided for. Outside a team scope no team
    /// level applies (`TeamVisible`). Under `TeamVisible` no role is looked
    /// up — the level admits every viewer, so a team that has chosen
    /// nothing costs one level read and is decided exactly as before. Under
    /// a narrower level the requester's role is read from the team store;
    /// with no requester, or at a wider-audience surface, the check is
    /// decided for the least-privileged viewer.
    let resolveViewer
        (sources: DisclosureViewerSources)
        (requester: RequestViewer option)
        (scopeId: string)
        (scopeTeam: string option option)
        (surface: FactEgressSurface)
        : Async<Result<DisclosureViewer, string>> =
        async {
            let requester = if audienceIsRequester surface then requester else None

            match! teamOf sources requester scopeId scopeTeam with
            | None ->
                return
                    Ok {
                        DisclosureViewer.leastPrivileged TeamVisible with
                            IsPlatformAdmin = requester |> Option.exists _.IsPlatformAdmin
                    }
            | Some team ->
                match! sources.OutputVisibility team with
                | Error message -> return Error message
                | Ok TeamVisible -> return Ok(DisclosureViewer.leastPrivileged TeamVisible)
                | Ok level ->
                    match requester with
                    | None -> return Ok(DisclosureViewer.leastPrivileged level)
                    | Some viewer ->
                        let! role = sources.TeamRole team viewer.UserId

                        return
                            Ok {
                                TeamRole = role
                                IsPlatformAdmin = viewer.IsPlatformAdmin
                                OutputVisibility = level
                            }
        }

/// The default `IFactDisclosureGate` over the composed fact store.
/// Construct via `FactDisclosureGate.create`; registered in DI by
/// `FactsCompose.withFactStore` alongside the store itself, so the fact
/// tier can never be composed without its egress gate.
type FactDisclosureGate
    private
    (
        store: IFactStore,
        events: IEventStore,
        resolvePolicy: DisclosurePolicyResolver option,
        taint: DisclosureTaintConfig option,
        purpose: DisclosurePurposeConfig option,
        budgets: DeclassificationBudgetConfig option,
        viewerAware: ViewerAwareDisclosure option
    ) =

    static let jsonOptions = FableConverters.create ()

    let taintConfig = defaultArg taint DisclosureTaintConfig.empty

    // Phase 592 — the purpose facet. `None` (no taxonomy declared) keeps
    // the facet absent: no claim is read, no refusal minted, behaviour
    // byte-for-byte pre-592 (GP 11 / GP 13).
    let purposeConfig = purpose

    // Phase 675 — the budget facet. `None` (no budgets declared) keeps
    // the facet absent: no ledger is read, no reservation opened,
    // behaviour byte-for-byte pre-675 (GP 11 / GP 13). A config that
    // declares no routine is treated as absent for the same reason — a
    // deployment that composed the knob and declared nothing pays
    // nothing.
    let budgetConfig =
        budgets |> Option.filter (DeclassificationBudgetConfig.isEmpty >> not)

    // The policy resolver: an explicit one wins; otherwise the registered
    // taint policy vocabulary drives it (Phase 562.A — the resolver's first
    // real vocabulary); otherwise the conservative deny-unknown default.
    let resolve =
        match resolvePolicy with
        | Some r -> r
        | None when not (DisclosureTaintConfig.isEmpty taintConfig) -> DisclosureTaintConfig.resolver taintConfig
        | None -> DisclosurePolicyResolver.denyUnknown

    // Phase 896 — the viewer-aware facet. `None` (team output visibility
    // not composed) keeps it absent: no viewer is resolved and every
    // `Restricted` fact is decided by `resolve` alone, byte-for-byte as
    // before (GP 11 / GP 13).
    let resolveForViewer =
        viewerAware
        |> Option.map (fun facet ->
            facet,
            facet.Resolver
            |> Option.defaultValue (ViewerAwareDisclosurePolicyResolver.withTeamOutputVisibility resolve))

    // Best-effort audit write under the reserved `_facts` source module — a
    // failed write is swallowed (the verdict already stands; auditing must
    // never turn a refusal into an exception on the answer path, nor block
    // a permitted disclosure).
    let writeEvent (scopeId: string) (eventType: string) (payload: string) : Async<unit> = async {
        try
            do!
                events.Write {
                    Id = Guid.NewGuid()
                    OccurredAt = DateTime.UtcNow
                    ScopeId = scopeId
                    SourceModule = FactEvents.SourceModule
                    EventType = eventType
                    Payload = payload
                }
        with _ ->
            ()
    }

    let writeDenied (scopeId: string) (payload: FactDisclosureDeniedEvent) : Async<unit> =
        writeEvent scopeId DisclosureEvents.DeniedType (JsonSerializer.Serialize(payload, jsonOptions))

    let writeDeclassified (scopeId: string) (payload: FactDisclosureDeclassifiedEvent) : Async<unit> =
        writeEvent scopeId DisclosureEvents.DeclassifiedType (JsonSerializer.Serialize(payload, jsonOptions))

    // Phase 674.C — the per-party access facet. Written only for a fact
    // that actually carries a contributing party's data, so a deployment
    // declaring no contributor scope never emits this row (GP 11 / GP 13).
    let writeAccessed (scopeId: string) (payload: FactDisclosureAccessedEvent) : Async<unit> =
        writeEvent scopeId DisclosureEvents.AccessedType (JsonSerializer.Serialize(payload, jsonOptions))

    // Phase 674 — the contributing party of a fact's OWN registered policy.
    // Resolved server-side from the composed vocabulary keyed by the ref the
    // stored fact carries; nothing a caller supplies reaches this lookup.
    let ownContributorScopes (fact: Fact option) : string list =
        match fact with
        | Some f ->
            match f.Disclosure with
            | Restricted policyRef -> DisclosureTaintConfig.scopeOf taintConfig policyRef |> Option.toList
            | _ -> []
        | None -> []

    // The Phase 525 base verdict over a fact's own disclosure classification,
    // plus its metric for the audit payload.
    let baseVerdict (surface: FactEgressSurface) (fact: Fact option) : FactDisclosureVerdict * string =
        match fact with
        | Some f -> DisclosureEgress.evaluateFact resolve surface f, f.Metric.Value
        // Unresolvable in this scope (unknown id, or a fact belonging to
        // another tenant) ⇒ deny, conservatively — never fail open.
        | None -> FactNotDisclosable "unknown-fact", ""

    // Phase 797 — one decision procedure, two ways of naming the shard.
    // The body is written once over an accessor pair so the string form
    // and the `ResolvedScope` form cannot drift: `getFact` / `queryAll`
    // are the store's own members bound to the scope the caller holds,
    // and `scopeId` is only ever the audit row's shard key.
    let check
        (getFact: string -> Async<Fact option>)
        (queryAll: unit -> Async<Fact list>)
        (scopeId: string)
        (scopeTeam: string option option)
        (principal: string)
        (surface: FactEgressSurface)
        (factIds: string list)
        : Async<Map<string, FactDisclosureVerdict>> =
        async {
            let ids = factIds |> List.distinct

            // Phase 896 — the viewer, resolved at most once per check and
            // only when a `Restricted` fact actually needs it. Started on
            // first demand; the ambient request viewer flows into it.
            let viewer =
                lazy
                    (match resolveForViewer with
                     | Some(facet, _) ->
                         async {
                             // A lookup that throws fails closed, exactly
                             // like an unreadable level.
                             try
                                 return!
                                     ViewerAwareDisclosure.resolveViewer
                                         facet.Sources
                                         (RequestViewerContext.current ())
                                         scopeId
                                         scopeTeam
                                         surface
                             with ex ->
                                 return Error ex.Message
                         }
                         |> Async.StartAsTask
                     | None -> Task.FromResult(Ok(DisclosureViewer.leastPrivileged TeamVisible)))

            let verdictFor (fact: Fact option) : Async<FactDisclosureVerdict * string> =
                match resolveForViewer, fact with
                | Some(_, resolveViewerAware), Some f ->
                    match f.Disclosure with
                    | Restricted _ -> async {
                        let! resolved = viewer.Force() |> Async.AwaitTask

                        match resolved with
                        | Ok v ->
                            return
                                DisclosureEgress.evaluateForViewer resolveViewerAware surface v f.Disclosure,
                                f.Metric.Value
                        | Error _ -> return FactNotDisclosable ViewerAwareDisclosure.UnreadableRef, f.Metric.Value
                      }
                    | Surfaceable
                    | Disclosure.Internal -> async.Return(baseVerdict surface fact)
                | _ -> async.Return(baseVerdict surface fact)

            // Phase 592 — the purpose facet, evaluated once per check
            // (the claim and the surface's allowed set are check-level,
            // not per-fact). With a declared taxonomy the claimed purpose
            // must be in this surface's allowed set; out-of-set or
            // missing claims refuse with the allowed set enumerated
            // (default-deny-by-shape). No taxonomy ⇒ inert.
            let claimedPurpose =
                match purposeConfig with
                | Some _ -> FactPurposeContext.current () |> Option.defaultValue ""
                | None -> ""

            let taxonomyVersion =
                purposeConfig |> Option.map _.Taxonomy.Version |> Option.defaultValue ""

            let purposeRefusal: string option =
                match purposeConfig with
                | None -> None
                | Some cfg ->
                    let allowed = DisclosurePurposeConfig.allowedFor cfg surface

                    let allowedText =
                        if List.isEmpty allowed then
                            "none"
                        else
                            String.concat ", " allowed

                    match FactPurposeContext.current () with
                    | Some p when List.contains p allowed -> None
                    | Some p -> Some(sprintf "purpose-denied:%s (allowed: %s)" p allowedText)
                    | None -> Some(sprintf "purpose-unclaimed (allowed: %s)" allowedText)

            // The derivation graph is built once per check, and only when a
            // taint config is composed — a deployment without taint policies
            // never loads the fact listing (GP 13, byte-identical to 525).
            // A purpose-refused check never builds it either: every fact is
            // already denied before any per-fact evaluation.
            let! graph =
                if DisclosureTaintConfig.isEmpty taintConfig || Option.isSome purposeRefusal then
                    async.Return None
                else
                    async {
                        let! all = queryAll ()
                        return Some(DisclosureTaint.buildGraph all)
                    }

            let! verdicts =
                ids
                |> List.map (fun factId -> async {
                    let! fact = getFact factId
                    let! baseV, metric = verdictFor fact

                    // Phase 592 — a purpose refusal denies every fact in
                    // the check before per-fact evaluation (the claim is
                    // check-level); otherwise layer the taint verdict only
                    // on a fact the Phase 525 predicate would disclose — a
                    // directly-denied fact stays denied on its own policy
                    // ref, and the taint walk is skipped.
                    let verdict =
                        match purposeRefusal with
                        | Some refusalRef -> FactNotDisclosable refusalRef, None
                        | None ->
                            match baseV, graph with
                            | FactDisclosable, Some g ->
                                let outcome = DisclosureTaint.analyze taintConfig g factId

                                // Phase 674 — the conjunction: the path must
                                // satisfy EVERY contributing party's policy.
                                // A surviving party-scoped restriction denies
                                // and the ref names the unsatisfied party
                                // (never the counterparty's data); an
                                // unscoped survivor keeps the Phase 562 ref,
                                // so single-party verdicts are unchanged.
                                match outcome.InheritedPolicyRef with
                                | Some inheritedRef ->
                                    let denyRef =
                                        if List.isEmpty outcome.UnsatisfiedScopes then
                                            inheritedRef
                                        else
                                            DisclosureContributorScope.unsatisfiedRef outcome.UnsatisfiedScopes

                                    FactNotDisclosable denyRef, Some outcome
                                | None -> FactDisclosable, Some outcome
                            | _ -> baseV, None

                    let preBudgetVerdict, taintOutcome = verdict

                    // ── Phase 675 — reserve BEFORE the verdict is final ──
                    //
                    // Only a disclosure that would otherwise be RELEASED
                    // and whose derivation actually crossed a routine can
                    // spend anything: a fact already denied on its own
                    // policy, on the conjunction, or on purpose has
                    // disclosed nothing and owes nothing, and charging it
                    // would spend a party's allowance on a question the
                    // gate never answered.
                    //
                    // The reservation is durable before the verdict
                    // stands, so a crossing cannot reach a caller on
                    // credit. `settle` below closes every hold once the
                    // verdict is known — including on the refusal path,
                    // where earlier crossings' holds are settled rather
                    // than leaked.
                    let! budgetHolds =
                        match preBudgetVerdict, taintOutcome, budgetConfig with
                        | FactDisclosable, Some outcome, Some config when not (List.isEmpty outcome.Crossings) -> async {
                            let! reserved = DeclassificationBudgetGate.reserve config outcome.Crossings
                            return Some(config, reserved)
                          }
                        | _ -> async.Return None

                    let finalVerdict =
                        match budgetHolds with
                        | Some(_, CrossingsRefused(policyRef, _)) -> FactNotDisclosable policyRef
                        | _ -> preBudgetVerdict

                    // Phase 674.C — the contribution facet on every audit
                    // row. With a taint outcome it is the whole walked
                    // lineage; without one (a directly-denied fact, or a
                    // deployment with no taint config) it is the fact's own
                    // registered policy — empty when no party is declared.
                    let contributorScopes =
                        match taintOutcome with
                        | Some outcome -> outcome.ContributorScopes
                        | None -> ownContributorScopes fact

                    let unsatisfiedScopes =
                        match taintOutcome with
                        | Some outcome -> outcome.UnsatisfiedScopes
                        | None -> []

                    match finalVerdict with
                    | FactNotDisclosable policyRef ->
                        do!
                            writeDenied scopeId {
                                Surface = FactEgressSurface.toString surface
                                FactId = factId
                                Metric = metric
                                PolicyRef = policyRef
                                Principal = principal
                                Purpose = claimedPurpose
                                TaxonomyVersion = taxonomyVersion
                                ContributorScopes = contributorScopes
                                UnsatisfiedScopes = unsatisfiedScopes
                            }
                    | FactDisclosable ->
                        // A disclosed fact whose derivation crossed a declared
                        // declassification routine audits each crossing (GP 6).
                        match taintOutcome with
                        | Some outcome ->
                            for crossing in outcome.Crossings do
                                do!
                                    writeDeclassified scopeId {
                                        Surface = FactEgressSurface.toString surface
                                        FactId = factId
                                        DeclassifierFactId = crossing.DeclassifierFactId
                                        OperationId = crossing.OperationId
                                        Rationale = crossing.Rationale
                                        Principal = principal
                                        Purpose = claimedPurpose
                                        TaxonomyVersion = taxonomyVersion
                                        ContributorScopes = outcome.ContributorScopes
                                        AcceptedScopes = crossing.AcceptedScopes
                                    }
                        | None -> ()

                        // Phase 674.C — the per-party ACCESS row. Only when
                        // a contributing party's data actually left, so a
                        // party-free deployment emits nothing new (GP 11).
                        if not (List.isEmpty contributorScopes) then
                            do!
                                writeAccessed scopeId {
                                    Surface = FactEgressSurface.toString surface
                                    FactId = factId
                                    Metric = metric
                                    ContributorScopes = contributorScopes
                                    Principal = principal
                                    Purpose = claimedPurpose
                                    TaxonomyVersion = taxonomyVersion
                                }

                    // Phase 675 — settle AFTER the verdict. A released
                    // disclosure commits its charges; a denied one is
                    // settled per each routine's own declared
                    // `WithholdCharge`, which is deployment policy and
                    // not an SDK opinion.
                    match budgetHolds with
                    | Some(config, CrossingsHeld held)
                    | Some(config, CrossingsRefused(_, held)) when not (List.isEmpty held) ->
                        let disclosed =
                            match finalVerdict with
                            | FactDisclosable -> true
                            | FactNotDisclosable _ -> false

                        do! DeclassificationBudgetGate.settle config disclosed held
                    | _ -> ()

                    return factId, finalVerdict
                })
                |> Async.Parallel

            return Map.ofArray verdicts
        }

    // NOTE on the widened constructor (Phase 675). `?budgets` widens the
    // SINGLE generated `.ctor`, so the five-argument token disappears
    // from the public-API baseline — a recorded retype, regenerated
    // surgically, under the 2026-08-04 record/contract widening
    // dispensation. Every existing call site still compiles unchanged
    // (F# optional arguments), and every source form remains valid.
    //
    // The obvious alternative — keeping the five-argument arity as an
    // explicit secondary constructor so the baseline diff stayed purely
    // additive — was implemented, measured and REJECTED: two overloads
    // differing only in a trailing optional argument are ambiguous to
    // overload resolution, so `FactDisclosureGate(store, events)` stopped
    // compiling with FS0041 at three call sites in this very file. It
    // traded a recorded, source-compatible baseline retype for a genuine
    // consumer break, which is the wrong direction.

    // Phase 896 — the public constructor, unchanged in shape. The primary
    // constructor above is private and takes every facet explicitly, so the
    // viewer-aware facet adds no optional argument here: a trailing optional
    // would widen this constructor's token (see the note above), and an
    // explicit overload differing only in a trailing optional would be
    // ambiguous. The facet is added with `WithViewerAwareness` instead.
    new
        (
            store: IFactStore,
            events: IEventStore,
            ?resolvePolicy: DisclosurePolicyResolver,
            ?taint: DisclosureTaintConfig,
            ?purpose: DisclosurePurposeConfig,
            ?budgets: DeclassificationBudgetConfig
        ) =
        FactDisclosureGate(store, events, resolvePolicy, taint, purpose, budgets, None)

    /// This gate with the Phase 896 viewer-aware facet armed: every other
    /// facet (resolver, taint, purpose, budgets) is kept as it is, and a
    /// `Restricted` fact is decided for the viewer. Composed by
    /// `FactsCompose.withTeamOutputVisibility`.
    member _.WithViewerAwareness(facet: ViewerAwareDisclosure) : IFactDisclosureGate =
        FactDisclosureGate(store, events, resolvePolicy, taint, purpose, budgets, Some facet) :> IFactDisclosureGate

    interface IFactDisclosureGate with

        member _.Check(scopeId: string, principal, surface, factIds) =
            check
                (fun factId -> store.Get(scopeId, factId))
                (fun () ->
                    store.Query(
                        scopeId,
                        {
                            FactQuery.all with
                                IncludeSuperseded = true
                        }
                    ))
                scopeId
                None
                principal
                surface
                factIds

        // Phase 797 — the request-path form. The store is asked through
        // its typed members, so the scope the gate re-resolves ids in is
        // the one the platform minted, by construction.
        member _.Check(scope: ResolvedScope, principal, surface, factIds) =
            check
                (fun factId -> store.Get(scope, factId))
                (fun () ->
                    store.Query(
                        scope,
                        {
                            FactQuery.all with
                                IncludeSuperseded = true
                        }
                    ))
                scope.ScopeId
                (Some(
                    scope.Storage
                    |> Option.filter (fun storage -> storage.Container.StartsWith("team-", StringComparison.Ordinal))
                    |> Option.map _.ScopeId
                ))
                principal
                surface
                factIds

module FactDisclosureGate =

    /// The default gate over the composed store + event store, with the
    /// conservative policy resolver (every `Restricted` ref denies until a
    /// policy vocabulary lands) and no taint propagation. Byte-for-byte the
    /// Phase 525 gate.
    let create (store: IFactStore) (events: IEventStore) : IFactDisclosureGate =
        FactDisclosureGate(store, events) :> IFactDisclosureGate

    /// A gate with a deployment-supplied `Restricted`-policy resolver (no
    /// taint propagation).
    let createWithPolicyResolver
        (resolvePolicy: DisclosurePolicyResolver)
        (store: IFactStore)
        (events: IEventStore)
        : IFactDisclosureGate =
        FactDisclosureGate(store, events, resolvePolicy) :> IFactDisclosureGate

    /// A gate with a Phase 562 taint configuration: the registered policy
    /// vocabulary drives the resolver (562.A) and taint propagates along the
    /// fact derivation graph, cleared only by a declared declassification
    /// routine (562.B/C). An empty config yields the Phase 525 gate exactly
    /// (GP 11).
    let createWithTaint (taint: DisclosureTaintConfig) (store: IFactStore) (events: IEventStore) : IFactDisclosureGate =
        FactDisclosureGate(store, events, ?resolvePolicy = None, taint = taint) :> IFactDisclosureGate

    /// Phase 592 — the fully-configured gate: optional taint (562) +
    /// optional purpose binding (592). With a purpose config, every check
    /// requires the ambient `FactPurposeContext` claim to be in the
    /// surface's allowed set; out-of-set or missing claims refuse with
    /// the allowed set enumerated, and grants and denials both stamp the
    /// claimed purpose + taxonomy version into the audit events. `None`
    /// on both axes is the Phase 525 gate exactly (GP 11 / GP 13). This
    /// is the shape `FactsCompose`'s DI factory builds from the optional
    /// registrations.
    let createConfigured
        (taint: DisclosureTaintConfig option)
        (purpose: DisclosurePurposeConfig option)
        (store: IFactStore)
        (events: IEventStore)
        : IFactDisclosureGate =
        FactDisclosureGate(store, events, ?resolvePolicy = None, ?taint = taint, ?purpose = purpose)
        :> IFactDisclosureGate

    /// Phase 675 — `createConfigured` with the third optional facet:
    /// declassification budgets. `None` on all three axes is the Phase
    /// 525 gate exactly; `None` on this one alone is the Phase 674 gate
    /// exactly (GP 11 / GP 13). This is the shape `FactsCompose`'s DI
    /// factory builds from the optional registrations.
    ///
    /// A new entry point rather than a widened `createConfigured`: the
    /// existing signature is public surface a consumer binds against, and
    /// retyping it would delete that token. Same reason the gate's
    /// pre-675 constructor is kept explicitly beside the widened one.
    let createConfiguredWithBudgets
        (taint: DisclosureTaintConfig option)
        (purpose: DisclosurePurposeConfig option)
        (budgets: DeclassificationBudgetConfig option)
        (store: IFactStore)
        (events: IEventStore)
        : IFactDisclosureGate =
        FactDisclosureGate(store, events, ?resolvePolicy = None, ?taint = taint, ?purpose = purpose, ?budgets = budgets)
        :> IFactDisclosureGate
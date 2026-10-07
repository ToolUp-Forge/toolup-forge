// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── ReactiveDataChange (Phase 623.C — react to data arrival) ─────────
//
// Phase 561 built `RecomputeJobHandler.reactToDataChange` and left
// it with no caller: nothing in a composed deployment told the fact tier
// that a data-object version had landed, so "reactive" recomputation was
// only ever schedule-driven. This file is the seam that closes it.
//
// **The seam is `IDataObjectStore`, and specifically its version-producing
// methods (`Save` / `Recover`).** That is where a data-object version
// *arrives* — the one event the invalidation walk is defined against. The
// two nearby candidates were considered and rejected:
//
//   * `ILineageStore.Record` fires when a *link* is written, and the link
//     names the object that was **produced** (`ToObjectId`) plus the
//     inputs it consumed. A brand-new object is cited by no fact yet, so
//     seeding the walk from a link invalidates nothing; seeding from its
//     `FromObjectId` would invalidate every fact citing an input that did
//     not change. Lineage is still walked — `invalidationSet` unions the
//     changed object's descendants — but it is the *derivation* of the
//     invalidation set, not its trigger.
//   * An `IEventStore` decorator (the `HookedEventStore` idiom) sits one
//     level below and would have to reconstruct "which object version is
//     this" from event payloads, re-deriving what `Save` already returns
//     typed.
//
// **The seed — and why it includes the object's PRIOR versions.** A fact
// cites its inputs by identity, and `Fact.compute` folds those identities
// (not the value) into the content address. So a `Computed` fact citing
// version v1 is invalidated by v2's arrival *because it cites v1*, and the
// recompute that follows produces a new head only by citing v2. The seed
// is therefore the whole identity set the arriving version supersedes or
// introduces: the stable `ObjectId` (the identity `ILineageStore` nodes
// carry), plus the `ContentHash` of **every** version of that object —
// the new one and the earlier ones the facts in the base actually name.
// Seeding only the new version's identities would invalidate nothing,
// because no fact can yet cite a version that has only just landed.
// `invalidationSet` then unions each seed's lineage descendants, so a fact
// computed from an object *derived from* the changed one is invalidated
// too.
//
// **Zero cost when unused (GP 13).** The decorator is registered only by
// a deployment that composes the fact tier, and it self-gates *before* the
// version read: unless a composed `Grounding.IMetricRegistry` declares at
// least one non-`Manual` `RecomputePolicy` *and* an `IJobScheduler` is
// present, a save costs one boolean test over an immutable singleton and
// touches no store. A deployment that declares no recompute policy
// therefore behaves — and pays — exactly as it did before Phase 623; the
// declaration IS the opt-in.
//
// **Never breaks a write.** The reaction runs after the inner store has
// committed and its outcome never changes the `Save` result: a throw is
// caught and logged at `Warn`. A fact base that failed to react is stale,
// which is recoverable; a data write that failed because the fact tier
// threw is not.
//
// **Awaited, not fire-and-forget.** The reaction is part of the same
// `Async` as the save. Firing it detached would make "the data landed" and
// "the facts reacted" unordered, which is untestable through a compose
// root and unobservable in production — the exact shape that let Phase
// 561's gap sit unnoticed. The cost is bounded by the gate above.
//
// **The scope rides the change (Phase 930).** Phase 818 gave jobs a typed
// scope, but could not type this hop: `IDataObjectStore.Save` / `Recover`
// hand the decorator a `scopeId: string` and nothing else, and minting a
// `ResolvedScope` from that string would be precisely the forgery Phase 797
// makes uncompilable. Phase 930 carries the ORIGIN's scope instead. A save
// made while serving a request is made under the scope the platform resolved
// for that request, and the decorator is handed that scope, never a string,
// through `currentScope` (in a composed deployment,
// `ScopeResolution.forRequest` over the ambient request, see `requestScope`).
// When the resolved scope names the shard the write landed in, the resolver's
// own value rides the change as `DataChangeScope.Resolved`, the recompute is
// scheduled through the typed `Schedule`, and the job runs under it.
//
// The equality test SELECTS the resolved scope; it never builds one. A write
// into any other shard, or one made off the request path (a boot seed, an
// ingestion or import job), rides as `DataChangeScope.Carried` with its
// string, and is scheduled through the string overload exactly as before.
// That arm is not a fallback waiting to be deleted: nothing on those paths
// holds a resolved scope, so the only way to type them would be the
// promotion 797 forbids. `RecomputeJobHandler.fs` states the other reason
// its carried arm stays (a scheduler that cannot re-mint).

/// The reaction a data-object version arrival triggers: given the scope
/// the write was made under (resolved, or carried as a string — Phase 930)
/// and the changed input identities, drive whatever the fact tier does
/// about it. Kept as a function seam (rather than a hard dependency on
/// `RecomputeJobHandler`) so the decorator is directly testable and the
/// DI resolution lives in `FactsCompose`.
type FactDataChangeReaction = DataChangeScope -> string list -> Async<unit>

/// Decorator over the composed `IDataObjectStore` that drives fact
/// invalidation when a version lands. Every non-version-producing member
/// delegates verbatim. `currentScope` yields the scope the platform
/// resolved for the principal making the write, when there is one
/// (Phase 930); the decorator only ever selects it, never builds one.
type ReactiveDataObjectStore
    (
        inner: IDataObjectStore,
        armed: unit -> bool,
        currentScope: unit -> ResolvedScope option,
        react: FactDataChangeReaction,
        logger: ILogger
    ) =

    /// The scope the change rides under: the resolved scope when it names
    /// the shard the write landed in, the carried string otherwise.
    let changeScope (scopeId: string) : DataChangeScope =
        match currentScope () with
        | Some scope when scope.ScopeId = scopeId -> DataChangeScope.Resolved scope
        | _ -> DataChangeScope.Carried scopeId

    /// Seed the invalidation walk from a landed version. Failures are
    /// contained — the write has already committed.
    let onVersion (scopeId: string) (dataObject: DataObject) : Async<unit> = async {
        if armed () then
            try
                // Every identity this object has ever had: the facts in
                // the base cite the SUPERSEDED versions, so seeding only
                // the new one would invalidate nothing.
                let! versions = inner.ListVersions(scopeId, dataObject.ObjectId)

                let changedIds =
                    dataObject.ObjectId
                    :: dataObject.ContentHash
                    :: (versions |> List.map _.ContentHash)
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                    |> List.distinct

                do! react (changeScope scopeId) changedIds
            with ex ->
                logger.Warn(
                    sprintf
                        "[Phase 623] Fact invalidation failed for data object %s in scope %s — the write stands, the fact base may be stale: %s"
                        dataObject.ObjectId
                        scopeId
                        ex.Message
                )
    }

    interface IDataObjectStore with

        member _.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy) = async {
            let! result = inner.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy)

            match result with
            | Ok dataObject -> do! onVersion scopeId dataObject
            | Error _ -> ()

            return result
        }

        member _.Recover(scopeId, objectId, version, createdBy) = async {
            let! result = inner.Recover(scopeId, objectId, version, createdBy)

            match result with
            | Ok dataObject -> do! onVersion scopeId dataObject
            | Error _ -> ()

            return result
        }

        member _.Get(scopeId, objectId) = inner.Get(scopeId, objectId)

        member _.GetVersion(scopeId, objectId, version) =
            inner.GetVersion(scopeId, objectId, version)

        member _.GetContent(scopeId, contentHash) = inner.GetContent(scopeId, contentHash)
        member _.ListVersions(scopeId, objectId) = inner.ListVersions(scopeId, objectId)
        member _.ListObjects scopeId = inner.ListObjects scopeId
        member _.Delete(scopeId, objectId) = inner.Delete(scopeId, objectId)
        member _.Evict(scopeId, objectId) = inner.Evict(scopeId, objectId)
        member _.Purge scopeId = inner.Purge scopeId

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

    // Phase 753 — the decorator must not hide the inner store's
    // compare-and-set capability, or every entity store composed over it
    // silently drops to the compare-then-save fallback. Forward through
    // the shared probe (which itself falls back when `inner` lacks the
    // capability), and react to the new version exactly as `Save` does.
    interface IConditionalDataObjectStore with
        member _.SaveIfVersion(scopeId, objectId, content, dataType, createdBy, metadata, policy, expectedVersion) = async {
            let! result =
                ConditionalDataObjectStore.saveIfVersion
                    inner
                    scopeId
                    objectId
                    content
                    dataType
                    createdBy
                    metadata
                    policy
                    expectedVersion

            match result with
            | Ok dataObject -> do! onVersion scopeId dataObject
            | Error _ -> ()

            return result
        }

        // Phase 806 — same forwarding through the shared probe; a delete
        // produces no version, so, like `Delete`, it triggers no reaction.
        member _.DeleteIfVersion(scopeId, objectId, expectedVersion) =
            ConditionalDataObjectStore.deleteIfVersion inner scopeId objectId expectedVersion

/// Construction + the DI-resolved reaction the fact tier composes.
module ReactiveDataChange =

    /// Does this deployment's grounding vocabulary ask for reactive
    /// recomputation at all? True when some registered metric declares a
    /// `RecomputePolicy` other than `Manual` — the declaration is the
    /// opt-in, so a deployment that declares none pays nothing (GP 13).
    let declaresReactivePolicy (registry: Grounding.IMetricRegistry option) : bool =
        match registry with
        | None -> false
        | Some reg ->
            reg.Metrics
            |> List.exists (fun metric ->
                match metric.RecomputePolicy with
                | Some policy -> policy <> Grounding.Manual
                | None -> false)

    /// The decorator's short-circuit, evaluated before any store read: is
    /// this deployment asking for reactive recomputation at all, and is
    /// there a scheduler for the `Eager` arm to run on? The registry is an
    /// immutable composed singleton, so the policy scan happens once.
    let gate
        (registry: unit -> Grounding.IMetricRegistry option)
        (scheduler: unit -> IJobScheduler option)
        : unit -> bool =
        let declared = lazy (declaresReactivePolicy (registry ()))
        fun () -> declared.Value && (scheduler ()).IsSome

    /// The reaction wired to `RecomputeJobHandler.reactToDataChange` over
    /// the composed substrate, resolved lazily from the built provider
    /// (nothing here is resolvable at compose time). `inner` is the
    /// *undecorated* lineage/fact substrate the decorator wraps, so the
    /// resolution can never re-enter this decorator.
    ///
    /// Re-checks the same `gate` conditions the decorator applied, so the
    /// reaction is safe to call on its own. The change's scope form picks
    /// the walk: typed for a resolved write, string for a carried one.
    let reaction
        (factStore: unit -> IFactStore)
        (lineage: unit -> ILineageStore)
        (scheduler: unit -> IJobScheduler option)
        (registry: unit -> Grounding.IMetricRegistry option)
        : FactDataChangeReaction =
        let armed = gate registry scheduler

        fun changeScope changedIds -> async {
            if not (armed ()) || List.isEmpty changedIds then
                return ()
            else
                match scheduler () with
                | None -> return ()
                | Some jobs ->
                    let! _outcomes =
                        RecomputeJobHandler.reactToChange
                            (lineage ())
                            (factStore ())
                            jobs
                            (registry ())
                            changeScope
                            changedIds

                    return ()
        }

    /// The scope source a composed deployment hands the decorator (Phase
    /// 930): the scope the platform's resolution recorded for the request
    /// being served, read through `ScopeResolution.forRequest`, the doors'
    /// one read. `None` off the request path (no request, or a request that
    /// has already completed), so such a write rides as carried.
    let requestScope (accessor: unit -> IHttpContextAccessor option) : unit -> ResolvedScope option =
        fun () ->
            match accessor () with
            | None -> None
            | Some accessor ->
                match accessor.HttpContext with
                | null -> None
                | ctx ->
                    try
                        Some(StorageScopeResolver.ScopeResolution.forRequest ctx)
                    with :? ObjectDisposedException ->
                        None

    /// Wrap `inner` so a landed version drives `react`, guarded by
    /// `armed` — which is consulted before the decorator reads anything.
    /// `currentScope` is the scope source (`requestScope` in a composed
    /// deployment; `fun () -> None` treats every write as carried).
    let decorate
        (inner: IDataObjectStore)
        (armed: unit -> bool)
        (currentScope: unit -> ResolvedScope option)
        (react: FactDataChangeReaction)
        (logger: ILogger)
        : IDataObjectStore =
        ReactiveDataObjectStore(inner, armed, currentScope, react, logger) :> IDataObjectStore
// ─── Phase 985 — grounded narratives react to data arrival ───────────
//
// A registered grounded narrative names the metrics it is written from
// (`GroundedNarrativeDefinition.DependsOnMetrics`). When a data-object
// version lands and the invalidation walk above finds current facts of one
// of those metrics whose inputs changed, the run is ENQUEUED — never run
// on the writer's thread, because a model call has no business inside a
// data save. Neither the trigger nor its job names a report: the
// deployment's registry says which runs exist and what each depends on.
//
// **The job waits for the recompute it raced.** A metric whose policy is
// `Eager` recomputes in its own job, scheduled by the recompute reaction in
// the same pass; nothing orders the two jobs (GP 12 rule 5). A narrative
// run that won the race would cite the head the recompute is about to
// supersede. So the narrative job re-derives the invalidated heads first
// and, while one it depends on is still an un-recomputed `Eager` head,
// reports a transient failure and lets the retry policy — data, not a
// callback — back it off. On its last attempt it runs regardless: a
// narrative over a superseded fact is caught by the supersession join and
// marked stale, which is honest; one never written is silent. `OnQuery`
// heads recompute when the run's own fact reads reach them, and `Manual`
// heads never recompute, so neither is waited for.
//
// **Only a RESOLVED change enqueues a run.** The run's model reads facts
// through the request-path fact tools, which take only a scope the platform
// resolved (Phase 797); the job is scheduled under the write's resolved
// scope and runs under it re-minted (Phase 818/935). A CARRIED change — a
// boot seed, an import job's write — has no resolved scope to give it, and
// promoting its string would be the forgery 797 exists to prevent, so it
// enqueues nothing: the run could only fail.
//
// **Zero cost when unused (GP 13).** The reaction does nothing — not even
// the invalidation walk — unless a `GroundedNarrativeRegistry` holding a
// run with a metric dependency AND an `IJobScheduler` are composed, and the
// job handler is registered with the scheduler only when a registry and an
// `IGroundedNarrativeRun` are.

module GroundedNarrativeTrigger =

    open System.Text.Json
    open ToolUp.Remoting.Json.SystemTextJson

    /// The job handler name the data-arrival runs are scheduled under.
    [<Literal>]
    let HandlerName = "_narratives.grounded-run"

    [<Literal>]
    let Actor = "grounded-narrative-trigger"

    /// The `GroundedNarrativeRequest.Trigger` a data-arrival run records.
    [<Literal>]
    let TriggerName = "data-arrival"

    /// What a narrative job carries: the run, and the invalidation set that
    /// caused it (so the job can tell whether a recompute is still pending).
    type GroundedNarrativeJobPayload = {
        RunKey: string
        InvalidatedInputs: string list
    }

    let private jsonOptions = FableConverters.create ()

    let payloadFor (runKey: string) (invalidated: Set<string>) : string =
        JsonSerializer.Serialize(
            {
                RunKey = runKey
                InvalidatedInputs = Set.toList invalidated
            },
            jsonOptions
        )

    let tryParsePayload (json: string) : GroundedNarrativeJobPayload option =
        try
            let parsed =
                JsonSerializer.Deserialize<GroundedNarrativeJobPayload>(json, jsonOptions)

            if isNull (box parsed) || String.IsNullOrWhiteSpace parsed.RunKey then
                None
            elif isNull (box parsed.InvalidatedInputs) then
                Some { parsed with InvalidatedInputs = [] }
            else
                Some parsed
        with _ ->
            None

    /// Six attempts, backing off from 30 s to 10 min — about twenty minutes
    /// for a raced recompute to land before the run proceeds regardless.
    let retryPolicy: JobRetryPolicy = {
        JobRetryPolicy.defaults with
            MaxAttempts = 6
            MaxBackoff = TimeSpan.FromMinutes 10.0
    }

    let private sha256Hex (s: string) : string =
        use sha = System.Security.Cryptography.SHA256.Create()

        sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes s)
        |> Array.map (sprintf "%02x")
        |> String.concat ""

    /// The job for one run over one change. Idempotency keys on the run AND
    /// the change, so a replayed change coalesces while a second, different
    /// change is never swallowed by the first's job. `ShardKey` is the run,
    /// so a distributed scheduler serialises one report's runs.
    let registrationFor (scopeId: string) (runKey: string) (invalidated: Set<string>) : JobRegistration =
        let changeDigest =
            (invalidated |> Set.toList |> String.concat "\n" |> sha256Hex).Substring(0, 16)

        {
            ScopeId = scopeId
            Handler = HandlerName
            Payload = payloadFor runKey invalidated
            Trigger = Trigger.Manual
            Idempotency =
                Some {
                    Key = sprintf "grounded-%s-%s" runKey changeDigest
                    TtlSeconds = 3600
                }
            RetryPolicy = retryPolicy
            ShardKey = Some runKey
            Precision = JobPrecision.Minute
            CreatedBy = Actor
            Tags = Map.ofList [ "origin", TriggerName; "run", runKey ]
        }

    /// Does the registry hold any run that reacts to data at all?
    let reacts (registry: GroundedNarrativeRegistry) : bool =
        registry.Definitions
        |> List.exists (fun d -> not (List.isEmpty d.DependsOnMetrics))

    /// The runs a set of invalidated heads touches.
    let affectedRuns (registry: GroundedNarrativeRegistry) (heads: Fact list) : GroundedNarrativeDefinition list =
        registry.DependentOn(heads |> List.map _.Metric.Value |> List.distinct)

    /// The data-arrival reaction: enqueue (schedule + fire) one job per run
    /// whose metrics the change invalidated. Returns the run keys enqueued.
    let enqueueFor
        (lineage: ILineageStore)
        (store: IFactStore)
        (scheduler: IJobScheduler)
        (registry: GroundedNarrativeRegistry)
        (changeScope: DataChangeScope)
        (changedIds: string list)
        : Async<string list> =
        async {
            match changeScope with
            | DataChangeScope.Carried _ -> return []
            | DataChangeScope.Resolved _ when List.isEmpty changedIds || not (reacts registry) -> return []
            | DataChangeScope.Resolved resolved ->
                let scopeId = resolved.ScopeId
                let! invalidated = FactInvalidation.invalidationSet lineage scopeId changedIds
                let! heads = FactInvalidation.invalidatedHeads store scopeId invalidated
                let mutable enqueued = []

                for definition in affectedRuns registry heads do
                    let registration = registrationFor scopeId definition.Key invalidated
                    let! scheduled = scheduler.Schedule(resolved, registration)

                    match scheduled with
                    | Ok jobId ->
                        let! _ = scheduler.TriggerOnce(scopeId, jobId, Actor)
                        enqueued <- definition.Key :: enqueued
                    | Error _ -> ()

                return List.rev enqueued
        }

    /// The reaction over the composed substrate, resolved lazily — the
    /// shape `ReactiveDataChange.reaction` takes.
    let reaction
        (factStore: unit -> IFactStore)
        (lineage: unit -> ILineageStore)
        (scheduler: unit -> IJobScheduler option)
        (registry: unit -> GroundedNarrativeRegistry option)
        : FactDataChangeReaction =
        fun changeScope changedIds -> async {
            match scheduler (), registry () with
            | Some jobs, Some runs when reacts runs ->
                let! _ = enqueueFor (lineage ()) (factStore ()) jobs runs changeScope changedIds
                return ()
            | _ -> return ()
        }

    /// Is the reaction armed: a registry with a reacting run, and a
    /// scheduler to enqueue on? Checked before the decorator reads anything.
    let armed
        (scheduler: unit -> IJobScheduler option)
        (registry: unit -> GroundedNarrativeRegistry option)
        : unit -> bool =
        fun () ->
            (scheduler ()).IsSome
            && (match registry () with
                | Some runs -> reacts runs
                | None -> false)

    /// The job: wait out a raced `Eager` recompute, then run.
    type GroundedNarrativeJobHandler
        (
            store: IFactStore,
            metrics: Grounding.IMetricRegistry option,
            registry: GroundedNarrativeRegistry,
            run: IGroundedNarrativeRun
        ) =
        interface IJobHandler with
            member _.Execute(ctx: JobContext) = async {
                match tryParsePayload ctx.Payload with
                | None -> return PermanentFailure "grounded narrative job: unreadable payload"
                | Some payload ->
                    match registry.TryResolve payload.RunKey with
                    | None ->
                        return PermanentFailure(sprintf "no grounded narrative run '%s' is registered" payload.RunKey)
                    | Some _ when ctx.Scope.IsAnonymous ->
                        return
                            PermanentFailure
                                "a grounded narrative job must run under a resolved scope; this one runs anonymous (its scheduler could not re-mint the scope it was scheduled under)"
                    | Some definition ->
                        let depends = Set.ofList definition.DependsOnMetrics

                        let! pending =
                            FactInvalidation.invalidatedHeads store ctx.ScopeId (Set.ofList payload.InvalidatedInputs)

                        let racing =
                            pending
                            |> List.filter (fun fact ->
                                depends.Contains fact.Metric.Value
                                && FactInvalidation.policyFor metrics fact = Grounding.Eager)

                        if not (List.isEmpty racing) && ctx.Attempt < retryPolicy.MaxAttempts then
                            return
                                TransientFailure(
                                    sprintf
                                        "waiting for %d fact recompute(s) before running '%s'"
                                        racing.Length
                                        definition.Key
                                )
                        else
                            let! outcome =
                                run.Run {
                                    RunKey = definition.Key
                                    Scope = ctx.Scope
                                    Access = None
                                    Trigger = TriggerName
                                }

                            match outcome with
                            | GroundedNarrativePublished _ -> return Success
                            // A refusal is a decision, recorded on the audit
                            // trail by the run; another attempt over the same
                            // facts would refuse again.
                            | GroundedNarrativeRefused _ ->
                                return PermanentFailure(GroundedNarrativeOutcome.describe outcome)
                            // A failure to produce a narrative (a provider
                            // outage, unparseable model output) may not recur.
                            | GroundedNarrativeFailed _ ->
                                return TransientFailure(GroundedNarrativeOutcome.describe outcome)
                            // A scope with no usable AI key (Phase 995) has
                            // none on the next attempt either: retrying would
                            // spend the backoff and publish nothing.
                            | GroundedNarrativeUnfunded _ ->
                                return PermanentFailure(GroundedNarrativeOutcome.describe outcome)
            }

    /// Register the job handler with the composed scheduler at startup —
    /// only when a registry and a run are composed (GP 13).
    let hostedService (sp: IServiceProvider) : Microsoft.Extensions.Hosting.IHostedService =
        { new Microsoft.Extensions.Hosting.IHostedService with
            member _.StartAsync(_ct) =
                match
                    sp.GetService(typeof<IJobScheduler>),
                    sp.GetService(typeof<GroundedNarrativeRegistry>),
                    sp.GetService(typeof<IGroundedNarrativeRun>)
                with
                | (:? IJobScheduler as scheduler),
                  (:? GroundedNarrativeRegistry as registry),
                  (:? IGroundedNarrativeRun as run) ->
                    let store = sp.GetService(typeof<IFactStore>) :?> IFactStore

                    let metrics =
                        match sp.GetService(typeof<Grounding.IMetricRegistry>) with
                        | :? Grounding.IMetricRegistry as m -> Some m
                        | _ -> None

                    scheduler.RegisterHandler(HandlerName, GroundedNarrativeJobHandler(store, metrics, registry, run))
                | _ -> ()

                System.Threading.Tasks.Task.CompletedTask

            member _.StopAsync(_ct) =
                System.Threading.Tasks.Task.CompletedTask
        }

module ReactiveDataChangeComposition =
    /// Two reactions as one, run in order — the recompute reaction first,
    /// so an `Eager` recompute is scheduled before a narrative job that
    /// depends on it.
    let combine (first: FactDataChangeReaction) (second: FactDataChangeReaction) : FactDataChangeReaction =
        fun changeScope changedIds -> async {
            do! first changeScope changedIds
            do! second changeScope changedIds
        }

    /// Two arming checks as one: armed when either is.
    let either (a: unit -> bool) (b: unit -> bool) : unit -> bool = fun () -> a () || b ()
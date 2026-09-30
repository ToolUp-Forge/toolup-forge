// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ScopeChokePointTests

// ─── Phase 797 — scope as a choke point ────────────────────────────
//
// Before this phase every fact door read its storage scope as a STRING
// from the request items, with a fallback to the user id and then to a
// literal, and the store keyed on whatever it was handed. The
// admissibility theorem over the model's input had to ASSUME the scope on
// a disclosed fact was the principal's, because nothing made it so.
//
// Now the scope is a value only the platform's scope resolution can mint:
// `ResolvedScope` has a private representation and an internal
// constructor; `ScopeResolution.remember` is the middleware's one write
// and `ScopeResolution.forRequest` the doors' one read; the store and the
// gate take the type. Three things are pinned here, each in the direction
// the phase's acceptance criteria name:
//
//   A. UNCONSTRUCTIBLE. A store handed a scope the resolver did not mint
//      cannot be constructed — the compiler refuses it, and this pack
//      pins the shape that makes the compiler refuse it (no public
//      constructor, no public union case, the mint internal) so a later
//      "helpful" public constructor cannot arrive silently.
//   B. NOT READ FROM THE BAG. A `StorageScope` planted in the request
//      items by anything other than the middleware's mint does not reach
//      a door; the door reads the anonymous scope instead. This is the
//      leaky-store test of Phases 559 / 703 inverted: not "a leak is
//      re-denied at the gate" but "the scope cannot be leaked INTO".
//   C. THE SOURCE SAYS SO. A source-reading guard over the three doors
//      refuses the old spelling — a `"ToolUp.StorageScope"` read, a
//      `StorageScope` pattern, the `scopeIdOf` fallback reader — with a classifier
//      pinned in both directions so the guard is known to fire before it
//      is trusted.
//
// The existing re-denial tests (a leaky store's cross-scope member is
// denied at the gate) are kept where they were; belt and braces both
// still hold.

open System
open System.IO
open System.Reflection
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ── Harness ───────────────────────────────────────────────────────

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef "revenue"
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ "h1" ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private queryArgs =
    """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue"}"""

let private storage (scopeId: string) : StorageScope = {
    ScopeId = scopeId
    Container = "container-" + scopeId
    Persist = true
}

let private newScopeId () = "team-" + Guid.NewGuid().ToString("N")

let private freshTier () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    let gate = FactDisclosureGate.create store events
    store, gate

let private assertUnder (store: IFactStore) (scope: ResolvedScope) (d: FactDraft) : Fact =
    match store.Assert(scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

let private factsOf (raw: string) : JsonElement list =
    (JsonDocument.Parse raw).RootElement.GetProperty("facts").EnumerateArray()
    |> Seq.map _.Clone()
    |> List.ofSeq

/// A request whose DI can serve the composed store + gate, with NOTHING
/// scope-shaped on it unless a case adds it.
let private bareRequest (store: IFactStore) (gate: IFactDisclosureGate) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IFactStore>(store) |> ignore
    services.AddSingleton<IFactDisclosureGate>(gate) |> ignore
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.UserId"] <- box "user-1"
    ctx :> HttpContext

// ── A. Unconstructible ────────────────────────────────────────────

let private resolvedScopeType = typeof<ResolvedScope>

let private publicSurfaceTests =
    testList "A — a scope the resolver did not mint cannot be constructed" [

        test "ResolvedScope exposes no public constructor" {
            let ctors =
                resolvedScopeType.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance)

            Expect.isEmpty ctors "a public constructor would let any caller mint a scope from a string"
        }

        test "ResolvedScope exposes no public union case — FSharpType does not even see it as a union" {
            // With a private representation the case constructors, tags
            // and `New*` factories are all non-public; FSharp.Reflection's
            // public view therefore reports NOT a union, and only the
            // non-public view sees the two cases.
            Expect.isFalse (FSharpType.IsUnion resolvedScopeType) "no public case is visible"

            Expect.isTrue
                (FSharpType.IsUnion(resolvedScopeType, BindingFlags.NonPublic))
                "the representation IS a union — it is private, not absent"

            let cases =
                FSharpType.GetUnionCases(resolvedScopeType, BindingFlags.NonPublic ||| BindingFlags.Public)

            Expect.sequenceEqual
                (cases |> Array.map _.Name |> Array.sort)
                [| "AnonymousScope"; "Resolved" |]
                "exactly the two cases the phase names — a resolved scope and the explicit anonymous one"
        }

        test "the mint on the core module is internal, and the anonymous scope is the only public entry" {
            let moduleType =
                resolvedScopeType.Assembly.GetType("ToolUp.Platform.ResolvedScopeModule")

            Expect.isNotNull moduleType "the companion module compiles under the ModuleSuffix name"

            let mint =
                moduleType.GetMethod("ofStorageScope", BindingFlags.NonPublic ||| BindingFlags.Static)

            Expect.isNotNull mint "the mint exists — the middleware needs it"
            Expect.isTrue mint.IsAssembly "and it is INTERNAL: reachable through InternalsVisibleTo, not by a consumer"

            Expect.isNull
                (moduleType.GetMethod("ofStorageScope", BindingFlags.Public ||| BindingFlags.Static))
                "there is no public spelling of the mint"

            let publicStatics =
                moduleType.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
                |> Array.map _.Name
                |> Array.filter (fun n -> not (n.StartsWith "get_"))
                |> Array.sort

            Expect.sequenceEqual publicStatics [| "scopeId" |] "the only public function is the accessor"

            Expect.isTrue ResolvedScope.anonymous.IsAnonymous "the anonymous scope is public, and says what it is"
        }

        test "the server-side mint and memo are internal too" {
            let scopeResolution =
                typeof<IStorageScopeResolver>.Assembly.GetType("ToolUp.Platform.StorageScopeResolver+ScopeResolution")

            Expect.isNotNull
                scopeResolution
                "the ScopeResolution module compiles as a nested type of the resolver module"

            for name in [ "ofStorageScope"; "remember" ] do
                let m =
                    scopeResolution.GetMethod(name, BindingFlags.NonPublic ||| BindingFlags.Static)

                Expect.isNotNull m (sprintf "%s exists" name)
                Expect.isTrue m.IsAssembly (sprintf "%s is internal" name)

                Expect.isNull
                    (scopeResolution.GetMethod(name, BindingFlags.Public ||| BindingFlags.Static))
                    (sprintf "%s has no public spelling" name)

            Expect.isNotNull
                (scopeResolution.GetMethod("forRequest", BindingFlags.Public ||| BindingFlags.Static))
                "forRequest — the doors' one read — is the public half"
        }

        test "the anonymous scope keys the anonymous shard and reports no storage" {
            Expect.equal
                ResolvedScope.anonymous.ScopeId
                ResolvedScope.AnonymousScopeId
                "the literal the shard is keyed on"

            Expect.equal (ResolvedScope.scopeId ResolvedScope.anonymous) "anonymous" "and the accessor agrees"
            Expect.isNone ResolvedScope.anonymous.Storage "no container, nothing persists"

            let minted = ScopeResolution.ofStorageScope (storage "team-x")
            Expect.isFalse minted.IsAnonymous "a minted scope is not anonymous"
            Expect.equal minted.ScopeId "team-x" "and keys its own shard"
            Expect.equal minted.Storage (Some(storage "team-x")) "carrying the resolver's record"
        }
    ]

// ── B. Not read from the bag ──────────────────────────────────────

let private requestPathTests =
    testList "B — the door reads the resolver's mint, never the request items" [

        test "forRequest returns the remembered scope, and the anonymous scope when nothing was remembered" {
            let ctx = DefaultHttpContext() :> HttpContext

            Expect.isTrue (ScopeResolution.forRequest ctx).IsAnonymous "an unresolved request is anonymous — explicitly"

            let remembered = ScopeResolution.remember ctx (storage "team-r")

            Expect.equal (ScopeResolution.forRequest ctx) remembered "the middleware's one write is the door's one read"
        }

        test "a StorageScope planted in the request items is NOT a resolved scope" {
            // The pre-797 fallback chain read exactly this key. A caller
            // (or a test harness, or a module) that plants it no longer
            // steers the fact doors: the door sees the anonymous scope.
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Items["ToolUp.StorageScope"] <- box (storage "team-planted")

            let seen = ScopeResolution.forRequest ctx

            Expect.isTrue seen.IsAnonymous "the planted record is not honoured"
            Expect.notEqual seen.ScopeId "team-planted" "and its id never reaches the door"
        }

        test "a user id on the request is not a scope either — the old middle rung is gone" {
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Items["ToolUp.UserId"] <- box "user-42"

            Expect.isTrue (ScopeResolution.forRequest ctx).IsAnonymous "no fallback to the principal's id"
        }

        testCaseAsync "end to end: a fact under a resolved scope is invisible to a request that planted that scope"
        <| async {
            let store, gate = freshTier ()
            let scopeId = newScopeId ()
            let minted = ScopeResolution.ofStorageScope (storage scopeId)
            let fact = assertUnder store minted (draft 12345.67m)

            // The same request the old code would have read as `scopeId`.
            let planted = bareRequest store gate
            planted.Items["ToolUp.StorageScope"] <- box (storage scopeId)

            let! raw = FactQueryTool.execute planted queryArgs

            Expect.isEmpty (factsOf raw) "the door queried the anonymous shard, not the planted one"
            Expect.isFalse (raw.Contains fact.FactId) "the fact's id is nowhere in the payload"
            Expect.isFalse (raw.Contains "12345.67") "nor its value"

            // And the request the MIDDLEWARE resolved sees it.
            let resolved = bareRequest store gate
            ScopeResolution.remember resolved (storage scopeId) |> ignore

            let! raw = FactQueryTool.execute resolved queryArgs

            Expect.equal (List.length (factsOf raw)) 1 "the resolver's scope reads its own shard"
        }

        testCaseAsync "the anonymous scope is its own shard, never a wildcard"
        <| async {
            let store, gate = freshTier ()
            let scoped = ScopeResolution.ofStorageScope (storage (newScopeId ()))

            assertUnder store scoped (draft 100m) |> ignore
            let anonymousFact = assertUnder store ResolvedScope.anonymous (draft 200m)

            let! raw = FactQueryTool.execute (bareRequest store gate) queryArgs
            let facts = factsOf raw

            Expect.equal (List.length facts) 1 "exactly the anonymously-asserted fact, not the scoped one as well"

            Expect.equal
                (facts.Head.GetProperty("factId").GetString())
                anonymousFact.FactId
                "and it is the anonymous shard's own fact"
        }

        test "the typed and string store forms key one shard — the typed form is the string form over ScopeId" {
            let store, _ = freshTier ()
            let scopeId = newScopeId ()
            let minted = ScopeResolution.ofStorageScope (storage scopeId)
            let viaTyped = assertUnder store minted (draft 5m)

            let viaString = store.Get(scopeId, viaTyped.FactId) |> Async.RunSynchronously

            Expect.equal viaString (Some viaTyped) "read back through the string key"

            let viaTypedAgain = store.Get(minted, viaTyped.FactId) |> Async.RunSynchronously

            Expect.equal viaTypedAgain (Some viaTyped) "and through the typed one"

            Expect.isNone
                (store.Get(ResolvedScope.anonymous, viaTyped.FactId) |> Async.RunSynchronously)
                "and not through a different scope"
        }
    ]

// ── C. The source says so ─────────────────────────────────────────

/// The three doors, by path under `src/`.
let private doors = [
    "ToolUp.Facts.Server/Server/FactQueryTool.fs"
    "ToolUp.Facts.Server/Server/PopulationQueryTool.fs"
    "ToolUp.Facts.Server/Server/CoverageTool.fs"
]

/// The spellings the doors must not carry. Assembled at runtime so this
/// file, which is itself inside `src/`, does not trip its own guard if the
/// scan is ever widened.
let private forbidden = [
    "\"ToolUp." + "StorageScope\"", "a `\"ToolUp.StorageScope\"` items read — the pre-797 scope source"
    ":? " + "StorageScope", "a `StorageScope` type-test pattern — the pre-797 cast"
    "let private " + "scopeIdOf", "a `scopeIdOf` reader — the pre-797 fallback chain (user id, then a literal)"
]

/// Pure classifier: the offences a door's source carries, as (needle, why).
let private offences (source: string) : (string * string) list =
    forbidden |> List.filter (fun (needle, _) -> source.Contains needle)

let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private sourceGuardTests =
    testList "C — no fact door reads the storage scope from the request items" [

        test "the classifier fires on the pre-797 shape (go-red)" {
            let planted =
                String.concat "\n" [
                    "    let private " + "scopeIdOf (ctx: HttpContext) : string ="
                    "        match ctx.Items.TryGetValue " + "\"ToolUp." + "StorageScope\" with"
                    "        | true, (" + ":? " + "StorageScope as scope) -> scope.ScopeId"
                    "        | _ -> " + "\"anonymous\""
                ]

            Expect.equal (List.length (offences planted)) 3 "all three spellings are caught"
        }

        test "the classifier is quiet on the post-797 shape" {
            let clean =
                "            executeWith store gate registry (StorageScopeResolver.ScopeResolution.forRequest ctx) (userIdOf ctx) argsJson"

            Expect.isEmpty (offences clean) "the resolver read is not an offence"
        }

        test "every door reads its scope through ScopeResolution.forRequest and carries none of the old spellings" {
            let src = Path.Combine(repoRoot (), "src")

            for door in doors do
                let path = Path.Combine(src, door)
                Expect.isTrue (File.Exists path) (sprintf "%s exists" door)
                let source = File.ReadAllText path

                Expect.stringContains
                    source
                    "ScopeResolution.forRequest ctx"
                    (sprintf "%s takes its scope from the resolver" door)

                Expect.stringContains
                    source
                    "(scope: ResolvedScope)"
                    (sprintf "%s's executor takes the typed scope" door)

                match offences source with
                | [] -> ()
                | found ->
                    failtestf
                        "%s carries pre-797 scope spellings:\n%s"
                        door
                        (found |> List.map (fun (_, why) -> "  - " + why) |> String.concat "\n")
        }
    ]

// ── D. The carried mint (Phase 818) ───────────────────────────────
//
// Phase 818 added a SECOND internal mint, `ofCarried` on the core module, for
// the one hop where a scope that WAS resolved is carried to a later
// dispatch: the job scheduler re-minting the scope a typed `Schedule`
// persisted. A second mint is a second place a string could be promoted,
// so the guard is widened to pin who may call it — the scheduler's
// provenance module and nothing else — and that the recompute path never
// mints at all: it carries the write's resolved scope or its string
// (Phase 930, section E), and builds neither.

/// Where the carried mint may be called from, by path under `src/`. Since
/// Phase 935 that is the platform's carrier, whose redeem path is the only
/// hop a carried scope re-mints through — for both schedulers and the
/// publication grants alike.
let private carriedMintHome = "ToolUp.Platform.Server/Server/CarriedScopeToken.fs"

/// Where the mints are DEFINED — excluded from the caller scan.
let private mintDefinitions = [
    "ToolUp.Platform.Core/Shared/Types/ResolvedScope.fs"
    "ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs"
]

/// The recompute path — it carries a scope end to end, so no mint belongs in it.
let private recomputePath = [
    "ToolUp.Facts.Server/Server/RecomputeJobHandler.fs"
    "ToolUp.Facts.Server/Server/ReactiveDataChange.fs"
]

/// A call of the carried mint. Assembled so this file does not match itself.
let private carriedMintCall = "ResolvedScope." + "ofCarried"

/// Every spelling that would promote a value to a `ResolvedScope`.
let private mintSpellings = [
    carriedMintCall, "the carried mint"
    "ResolvedScope." + "ofStorageScope", "the request-path mint"
    "ScopeResolution." + "ofStorageScope", "the server-side request-path mint"
    "ScopeResolution." + "remember", "the middleware's mint-and-record"
]

/// Pure classifier: the mint spellings a source carries, as (needle, why).
let private mints (source: string) : (string * string) list =
    mintSpellings |> List.filter (fun (needle, _) -> source.Contains needle)

let private carriedMintTests =
    testList "D — the carried mint (Phase 818)" [

        test "the carried mint is internal and has no public spelling" {
            let moduleType =
                resolvedScopeType.Assembly.GetType("ToolUp.Platform.ResolvedScopeModule")

            let mint =
                moduleType.GetMethod("ofCarried", BindingFlags.NonPublic ||| BindingFlags.Static)

            Expect.isNotNull mint "the carried mint exists — the scheduler needs it"
            Expect.isTrue mint.IsAssembly "and it is INTERNAL: reachable through InternalsVisibleTo, not by a consumer"

            Expect.isNull
                (moduleType.GetMethod("ofCarried", BindingFlags.Public ||| BindingFlags.Static))
                "there is no public spelling of the carried mint"
        }

        test "the mint classifier fires on each spelling (go-red) and is quiet on a typed read" {
            let planted =
                mintSpellings
                |> List.map (fun (needle, _) -> needle + "(x)")
                |> String.concat "\n"

            Expect.equal (List.length (mints planted)) (List.length mintSpellings) "every mint spelling is caught"
            Expect.isEmpty (mints "store.Get(ctx.Scope, payload.factId)") "reading ctx.Scope is not a mint"
        }

        test "the carried mint has exactly one caller — the platform's carrier" {
            let src = Path.Combine(repoRoot (), "src")

            let excluded =
                mintDefinitions |> List.map (fun p -> Path.GetFullPath(Path.Combine(src, p)))

            // Build output, npm trees and Fable output hold no source of
            // record; pruning them is what keeps this scan to a second.
            let pruned = set [ "bin"; "obj"; "node_modules"; "output"; ".fable" ]

            let rec sources (dir: string) : string seq = seq {
                yield! Directory.EnumerateFiles(dir, "*.fs")

                for sub in Directory.EnumerateDirectories dir do
                    if not (pruned.Contains(Path.GetFileName sub)) then
                        yield! sources sub
            }

            let callers =
                sources src
                |> Seq.filter (fun path -> not (List.contains (Path.GetFullPath path) excluded))
                |> Seq.filter (fun path -> (File.ReadAllText path).Contains carriedMintCall)
                |> Seq.map (fun path -> Path.GetRelativePath(src, path).Replace('\\', '/'))
                |> List.ofSeq

            Expect.equal
                callers
                [ carriedMintHome ]
                "a second caller of the carried mint is a second place a string could become a resolved scope"
        }

        test "the recompute path mints nothing — it carries the write's scope, and builds none" {
            let src = Path.Combine(repoRoot (), "src")

            for file in recomputePath do
                let path = Path.Combine(src, file)
                Expect.isTrue (File.Exists path) (sprintf "%s exists" file)

                match mints (File.ReadAllText path) with
                | [] -> ()
                | found ->
                    failtestf
                        "%s promotes a value to a ResolvedScope:\n%s"
                        file
                        (found |> List.map (fun (_, why) -> "  - " + why) |> String.concat "\n")

            Expect.stringContains
                (File.ReadAllText(Path.Combine(src, recomputePath.Head)))
                "store.Get(ctx.Scope, payload.factId)"
                "the recompute handler reads a re-minted job scope through the typed member"
        }
    ]

// ── E. The reactive path carries the write's scope (Phase 930) ─────
//
// Phase 818 could not type the reactive recompute hop: the data write it
// reacts to handed over a string and nothing else. Phase 930 carries the
// ORIGIN's scope instead: a write made while serving a request rides the
// change with the scope the platform resolved for that request, selected
// (never built) when it names the shard the write landed in, and the
// recompute is scheduled through the typed `Schedule` so the job runs
// under it. The job-admin API schedules through the typed overload too.
// Pinned here in both directions:
//
//   * a resolved write reaches the reaction as the resolver's own value;
//     a write into another shard, or one with no resolved scope, rides
//     carried with its string — the scope is selected, not re-derived;
//   * a recompute never reads the anonymous shard for a job registered
//     under a real one, whichever form its scope came back in;
//   * the source of the reactive path and the job-admin API carries no
//     mint and no pre-797 bag read, with the classifier shown to fire on a
//     planted string re-derivation before the scan is trusted.

open System.Collections.Concurrent

let private silent =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A draft citing one input identity, at a fixed subject / period.
let private draftCiting (inputHash: string) (value: decimal) : FactDraft = {
    draft value with
        Evidence = {
            ResultRef = None
            InputHashes = [ inputHash ]
            TriggerRef = None
        }
}

let private eagerRegistry () : Grounding.IMetricRegistry =
    let revenue: Grounding.MetricDefinition = {
        Id = "revenue"
        Name = "revenue"
        Unit = "GBP"
        Dimensionality = "currency"
        Direction = Grounding.HigherIsBetter
        DisplayFormat = "C0"
        Staleness = Grounding.UntilUpstreamChange
        ProducingOperation = Some "rollup"
        CanonicalMethod = None
        RecomputePolicy = Some Grounding.Eager
        RollUp = None
        Context = None
    }

    let registration: Grounding.MetricRegistration = {
        Module = "sales"
        Definition = revenue
    }

    Grounding.MetricRegistry.build [ registration ] []

/// Recomputes every fact to 120. The draft cites a fresh input identity:
/// a fact's address folds its inputs, not its value, so a draft citing the
/// same inputs would be the same fact and supersede nothing.
let private recomputeTo120 =
    { new IFactRecomputer with
        member _.Recompute(_, fact) = async {
            return Ok(Some(draftCiting (fact.Evidence.InputHashes.Head + ".recomputed") 120m))
        }
    }

/// A scheduler that behaves as the in-process one does about scope: a job
/// scheduled through the typed overload comes back with that scope on
/// `JobContext.Scope`; a job scheduled through the string overload comes
/// back anonymous there, with its carried `ScopeId`. `remints = false`
/// stands in for a scheduler outside the server tier, which hands every job
/// back anonymous.
type private CarryingScheduler(handler: unit -> IJobHandler, remints: bool) =
    let jobs = ConcurrentDictionary<JobId, JobRegistration * ResolvedScope option>()
    let typed = ConcurrentQueue<ResolvedScope * JobRegistration>()
    let untyped = ConcurrentQueue<JobRegistration>()
    let results = ConcurrentQueue<JobResult>()

    member _.Typed = typed |> List.ofSeq
    member _.Untyped = untyped |> List.ofSeq
    member _.Results = results |> List.ofSeq

    interface IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async { return Ok() }

        member _.Schedule(scope: ResolvedScope, registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            typed.Enqueue(scope, registration)
            jobs[id] <- (registration, Some scope)
            return Ok id
        }

        member _.Schedule(registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            untyped.Enqueue registration
            jobs[id] <- (registration, None)
            return Ok id
        }

        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs _ = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_, jobId, byUserId) = async {
            match jobs.TryGetValue jobId with
            | false, _ -> return Error "unknown job"
            | true, (registration, scope) ->
                let ctx: JobContext = {
                    JobId = jobId
                    ScopeId = registration.ScopeId
                    AccessContext = AccessContext.unrestricted (AuthenticatedUser byUserId)
                    Attempt = 1
                    Trigger = registration.Trigger
                    Scope =
                        match scope with
                        | Some s when remints -> s
                        | _ -> ResolvedScope.anonymous
                    TriggerSource = ScheduledManually byUserId
                    ScheduledAt = DateTime.UtcNow
                    RunningAt = DateTime.UtcNow
                    Payload = registration.Payload
                    DeadLetterDestination = None
                }

                let! result = (handler ()).Execute ctx
                results.Enqueue result
                return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

/// The reactive tier over one fact store: a data-object store decorated
/// the way `FactsCompose` decorates it, with `currentScope` as its scope
/// source and the reaction wired to the real walk + handler.
let private reactiveTier (currentScope: unit -> ResolvedScope option) (remints: bool) =
    let store, _ = freshTier ()

    let lineage =
        LineageStore.EventStoreLineageStore(InMemoryEventStore.InMemoryEventStore() :> IEventStore) :> ILineageStore

    let handler () =
        RecomputeJobHandler.create store recomputeTo120 silent

    let scheduler = CarryingScheduler(handler, remints)
    let registry = Some(eagerRegistry ())

    let react =
        ReactiveDataChange.reaction
            (fun () -> store)
            (fun () -> lineage)
            (fun () -> Some(scheduler :> IJobScheduler))
            (fun () -> registry)

    let objects =
        ReactiveDataChange.decorate
            (DataObjectStore.DataObjectStore(InMemoryBlobStorage(), silent))
            (ReactiveDataChange.gate (fun () -> registry) (fun () -> Some(scheduler :> IJobScheduler)))
            currentScope
            react
            silent

    store, scheduler, objects

let private saveInput (objects: IDataObjectStore) (scopeId: string) (body: string) : DataObject =
    match
        objects.Save(
            scopeId,
            "sales.rollup",
            Text.Encoding.UTF8.GetBytes body,
            "rollup",
            "tester",
            Map.empty,
            VersioningPolicy.Versioned
        )
        |> Async.RunSynchronously
    with
    | Ok dataObject -> dataObject
    | Error err -> failtestf "data-object save failed: %A" err

let private headValues (store: IFactStore) (scope: ResolvedScope) : FactValue list =
    store.Query(scope, FactQuery.all) |> Async.RunSynchronously |> List.map _.Value

/// The reactive path and the job-admin API, by path under `src/`.
let private reactivePath = [
    "ToolUp.Facts.Server/Server/ReactiveDataChange.fs"
    "ToolUp.Facts.Server/Server/RecomputeJobHandler.fs"
    "ToolUp.Platform.Server/Server/JobApiHandler.fs"
]

/// Every way this path could re-derive a scope from a string: a mint (the
/// only constructors there are) or the pre-797 bag reads that fed one.
let private rederivations (source: string) : (string * string) list = mints source @ offences source

let private reactivePathTests =
    testList "E — the reactive path carries the write's scope (Phase 930)" [

        test "a resolved write rides the change as the resolver's own value; any other write rides carried" {
            let resolved = ScopeResolution.ofStorageScope (storage (newScopeId ()))
            let seen = ConcurrentQueue<DataChangeScope>()

            let objects =
                ReactiveDataChange.decorate
                    (DataObjectStore.DataObjectStore(InMemoryBlobStorage(), silent))
                    (fun () -> true)
                    (fun () -> Some resolved)
                    (fun change _ -> async { seen.Enqueue change })
                    silent

            let otherShard = newScopeId ()
            saveInput objects resolved.ScopeId "v1" |> ignore
            saveInput objects otherShard "v1" |> ignore

            match List.ofSeq seen with
            | [ DataChangeScope.Resolved carried; DataChangeScope.Carried scopeId ] ->
                Expect.isTrue
                    (obj.ReferenceEquals(carried, resolved))
                    "the write in the resolved shard carries the resolver's value itself, not a copy built from its id"

                Expect.equal scopeId otherShard "a write into another shard rides carried, with its own string"
            | other -> failtestf "expected one resolved then one carried change, got %A" other
        }

        test "a write with no resolved scope rides carried — nothing is built from its string" {
            let seen = ConcurrentQueue<DataChangeScope>()

            let objects =
                ReactiveDataChange.decorate
                    (DataObjectStore.DataObjectStore(InMemoryBlobStorage(), silent))
                    (fun () -> true)
                    (fun () -> None)
                    (fun change _ -> async { seen.Enqueue change })
                    silent

            let scopeId = newScopeId ()
            saveInput objects scopeId "v1" |> ignore

            Expect.equal (List.ofSeq seen) [ DataChangeScope.Carried scopeId ] "carried, with the write's string"
        }

        test "requestScope reads the scope the middleware recorded, and nothing off the request path" {
            let ctx = DefaultHttpContext() :> HttpContext
            let accessor = HttpContextAccessor()

            let source =
                ReactiveDataChange.requestScope (fun () -> Some(accessor :> IHttpContextAccessor))

            Expect.isNone (source ()) "no request, no resolved scope"

            accessor.HttpContext <- ctx
            let remembered = ScopeResolution.remember ctx (storage (newScopeId ()))

            Expect.equal (source ()) (Some remembered) "the middleware's value, read through forRequest"

            let planted = DefaultHttpContext() :> HttpContext
            planted.Items["ToolUp.StorageScope"] <- box (storage "team-planted")
            accessor.HttpContext <- planted

            Expect.equal
                (source () |> Option.map _.IsAnonymous)
                (Some true)
                "a planted StorageScope is not a resolved scope here either"

            Expect.isNone (ReactiveDataChange.requestScope (fun () -> None) ()) "no accessor composed, no scope"
        }

        testCaseAsync "end to end: a resolved write's recompute is scheduled typed and runs under the write's scope"
        <| async {
            let resolved = ScopeResolution.ofStorageScope (storage (newScopeId ()))
            let store, scheduler, objects = reactiveTier (fun () -> Some resolved) true

            let v1 = saveInput objects resolved.ScopeId "v1"
            assertUnder store resolved (draftCiting v1.ContentHash 100m) |> ignore

            let decoy =
                assertUnder store ResolvedScope.anonymous (draftCiting v1.ContentHash 100m)

            saveInput objects resolved.ScopeId "v2" |> ignore

            Expect.hasLength scheduler.Typed 1 "the recompute went through the typed Schedule"
            Expect.isEmpty scheduler.Untyped "and not through the string one"

            Expect.isTrue
                (obj.ReferenceEquals(fst scheduler.Typed.Head, resolved))
                "under the scope the write was made under"

            Expect.equal scheduler.Results [ JobResult.Success ] "the job ran"
            Expect.equal (headValues store resolved) [ Scalar 120m ] "and recomputed the resolved shard's fact"

            Expect.equal
                (headValues store ResolvedScope.anonymous)
                [ decoy.Value ]
                "the anonymous shard was never read or written"
        }

        testCaseAsync "a recompute never reads the anonymous shard, whichever form its scope came back in"
        <| async {
            // (a) a carried write, (b) a resolved write on a scheduler that
            // cannot re-mint: both come back anonymous on JobContext.Scope
            // with the real shard on ScopeId. Each must recompute the real
            // shard's fact and leave the anonymous shard's decoy alone.
            let resolved = ScopeResolution.ofStorageScope (storage (newScopeId ()))

            for label, currentScope, remints in
                [
                    "a carried write", (fun () -> None), true
                    "a scheduler that cannot re-mint", (fun () -> Some resolved), false
                ] do
                let store, scheduler, objects = reactiveTier currentScope remints
                let shard = ScopeResolution.ofStorageScope (storage resolved.ScopeId)

                let v1 = saveInput objects resolved.ScopeId "v1"
                assertUnder store shard (draftCiting v1.ContentHash 100m) |> ignore

                let decoy =
                    assertUnder store ResolvedScope.anonymous (draftCiting v1.ContentHash 100m)

                saveInput objects resolved.ScopeId "v2" |> ignore

                Expect.equal scheduler.Results [ JobResult.Success ] (sprintf "%s: the job ran" label)

                Expect.equal
                    (headValues store shard)
                    [ Scalar 120m ]
                    (sprintf "%s: the job's own shard was recomputed" label)

                Expect.equal
                    (headValues store ResolvedScope.anonymous)
                    [ decoy.Value ]
                    (sprintf "%s: the anonymous shard was never read or written" label)
        }

        testCaseAsync "a typed job scope naming another shard is refused, never read under"
        <| async {
            let store, _ = freshTier ()
            let jobShard = ScopeResolution.ofStorageScope (storage (newScopeId ()))
            let otherShard = ScopeResolution.ofStorageScope (storage (newScopeId ()))
            let fact = assertUnder store otherShard (draftCiting "h1" 100m)

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = jobShard.ScopeId
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "tester")
                Attempt = 1
                Trigger = Trigger.Manual
                Scope = otherShard
                TriggerSource = ScheduledManually "tester"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = RecomputeJobHandler.payloadFor fact.FactId
                DeadLetterDestination = None
            }

            let! result = (RecomputeJobHandler.create store recomputeTo120 silent).Execute ctx

            match result with
            | JobResult.PermanentFailure _ -> ()
            | other -> failtestf "expected a refusal, got %A" other

            Expect.equal (headValues store otherShard) [ Scalar 100m ] "the other shard's fact is untouched"
        }

        testCaseAsync "the job-admin API schedules under the request's resolved scope, through the typed overload"
        <| async {
            let requestFor (remember: bool) =
                let scheduler = CarryingScheduler((fun () -> failwith "not run"), true)
                let services = ServiceCollection()
                services.AddSingleton<IJobScheduler>(scheduler) |> ignore

                services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser "user-930"))
                |> ignore

                let ctx = DefaultHttpContext()
                ctx.RequestServices <- services.BuildServiceProvider()

                let resolved =
                    if remember then
                        Some(ScopeResolution.remember ctx (storage "user-930"))
                    else
                        None

                scheduler, ctx :> HttpContext, resolved

            let registration: JobRegistration = {
                ScopeId = "someone-else"
                Handler = "h"
                Payload = "{}"
                Trigger = Trigger.Manual
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = JobPrecision.Minute
                CreatedBy = "forged"
                Tags = Map.empty
            }

            let scheduler, ctx, resolved = requestFor true
            let! scheduled = (JobApiHandler.jobApi ctx).Schedule registration
            Expect.isOk scheduled "scheduled"
            Expect.hasLength scheduler.Typed 1 "through the typed overload"
            Expect.isEmpty scheduler.Untyped "and not the string one"

            Expect.isTrue
                (obj.ReferenceEquals(fst scheduler.Typed.Head, resolved.Value))
                "under the scope the middleware recorded — so the two read one key"

            Expect.equal (snd scheduler.Typed.Head).ScopeId "user-930" "the caller's forged scope is still overwritten"

            let bypassed, ctx, _ = requestFor false
            let! scheduled = (JobApiHandler.jobApi ctx).Schedule registration
            Expect.isOk scheduled "scheduled"

            Expect.equal
                (bypassed.Untyped |> List.map _.ScopeId)
                [ "user-930" ]
                "a request the middleware resolved nothing for keeps the string overload — it builds no scope"

            Expect.isEmpty bypassed.Typed "and nothing typed was invented for it"
        }

        test "the re-derivation classifier fires on a planted string re-derivation (go-red) and is quiet on a selection" {
            let planted =
                String.concat "\n" [
                    "        | _ -> DataChangeScope.Resolved(ScopeResolution."
                    + "ofStorageScope { ScopeId = scopeId; Container = scopeId; Persist = true })"
                    "                            return! s.Schedule(ResolvedScope."
                    + "ofStorageScope scope, safeRegistration)"
                    "        match ctx.Items.TryGetValue \"ToolUp." + "StorageScope\" with"
                ]

            Expect.equal
                (rederivations planted |> List.map fst |> List.distinct |> List.length)
                3
                "each re-derivation is caught"

            Expect.isEmpty
                (rederivations "        | Some scope when scope.ScopeId = scopeId -> DataChangeScope.Resolved scope")
                "selecting the resolved scope is not a re-derivation"

            Expect.isEmpty
                (rederivations "                            return! s.Schedule(resolved, safeRegistration)")
                "scheduling under the request's scope is not one either"
        }

        test "the reactive path and the job-admin API re-derive no scope from a string" {
            let src = Path.Combine(repoRoot (), "src")

            for file in reactivePath do
                let path = Path.Combine(src, file)
                Expect.isTrue (File.Exists path) (sprintf "%s exists" file)

                match rederivations (File.ReadAllText path) with
                | [] -> ()
                | found ->
                    failtestf
                        "%s re-derives a scope from a string:\n%s"
                        file
                        (found |> List.map (fun (_, why) -> "  - " + why) |> String.concat "\n")

            let read (file: string) =
                File.ReadAllText(Path.Combine(src, file))

            Expect.stringContains
                (read "ToolUp.Facts.Server/Server/ReactiveDataChange.fs")
                "StorageScopeResolver.ScopeResolution.forRequest ctx"
                "the reactive path takes its scope from the doors' one read"

            Expect.stringContains
                (read "ToolUp.Facts.Server/Server/RecomputeJobHandler.fs")
                "scheduler.Schedule(scope, registrationFor scope.ScopeId fact)"
                "a resolved change is scheduled through the typed overload"

            Expect.stringContains
                (read "ToolUp.Platform.Server/Server/JobApiHandler.fs")
                "s.Schedule(resolved, safeRegistration)"
                "the job-admin API schedules through the typed overload"
        }
    ]


// ── F. The platform-owned carrier (Phase 935) ─────────────────────────
//
// Phase 935 moved the carried mint behind a token: a scope written down as
// a string any store can hold, and turned back into the same scope only by
// the platform. Pinned here:
//
//   * the carrier cannot be built over key material a caller chooses — its
//     constructors that take key material are internal, and the one public
//     constructor keeps its keys to itself;
//   * a forged, an altered and a re-purposed token each re-mint nothing,
//     each with its own typed refusal, and a genuine token re-mints the
//     scope it was issued with — across a restart over the same key ring;
//   * a job-store writer who copies a genuine token onto another job, or
//     moves a job to another scope, gets the anonymous scope;
//   * the carrier module is the one caller of the carried mint (section D,
//     re-pointed), and neither scheduler nor the grant store carries a
//     string-to-scope promotion — a planted one turns the scan red.

/// A key ring in memory — what a deployment's `BlobXmlRepository` is, over
/// a blob store that outlives each process.
let private ringStorage () =
    InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

let private carrierOver (storage: ToolUp.Platform.BlobStorage.IBlobStorage) =
    ScopeCarrier.ofKeyRepository (BlobXmlRepository(storage))

let private jobPurpose (scopeId: string) (jobId: Guid) =
    CarriedScopePurpose.ScheduledJob(scopeId, jobId)

let private issued (carrier: ScopeCarrier) (scope: ResolvedScope) (purpose: CarriedScopePurpose) : string =
    match carrier.Issue(scope, purpose) with
    | Ok(Some token) -> token
    | other -> failtestf "expected a token, got %A" other

/// The files a string-to-scope promotion must never appear in: both
/// schedulers and the grant store.
let private carriedPaths = [
    "ToolUp.Platform.Server/Server/IJobScheduler.fs"
    "ToolUp.Platform.Server/Server/JobScheduler.fs"
    "JobSchedulers/Quartz/QuartzJobScheduler.fs"
    "JobSchedulers/Quartz/QuartzJobStore.fs"
    "ToolUp.Facts.Server/Server/FactPublication.fs"
]

/// Every spelling that would promote a value to a `ResolvedScope` on a
/// carried path: the mints, and a carrier built over chosen key material.
let private promotionSpellings =
    mintSpellings
    @ [
        "ScopeCarrier." + "ofDataProtection", "a carrier over a chosen DataProtection provider"
        "ScopeCarrier." + "ofKeyRepository", "a carrier over a chosen key ring"
        "new " + "ScopeCarrier(", "a carrier constructed directly"
    ]

let private promotions (source: string) : (string * string) list =
    promotionSpellings |> List.filter (fun (needle, _) -> source.Contains needle)

let private carrierTests =
    testList "F — the platform-owned carrier (Phase 935)" [

        test "the carrier takes no key material from a caller" {
            let carrierType = typeof<ScopeCarrier>

            Expect.isEmpty
                (carrierType.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance))
                "no public constructor — a caller choosing the protector would choose the keys"

            let moduleType = carrierType.Assembly.GetType("ToolUp.Platform.ScopeCarrierModule")

            Expect.isNotNull moduleType "the companion module compiles under the ModuleSuffix name"

            for name in [ "ofDataProtection"; "ofKeyRepository" ] do
                let m = moduleType.GetMethod(name, BindingFlags.NonPublic ||| BindingFlags.Static)
                Expect.isNotNull m (sprintf "%s exists" name)
                Expect.isTrue m.IsAssembly (sprintf "%s is internal" name)

            let publicStatics =
                moduleType.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
                |> Array.map _.Name
                |> Array.filter (fun n -> not (n.StartsWith "get_"))
                |> Array.sort

            Expect.sequenceEqual publicStatics [| "ephemeral" |] "the one public constructor keeps its keys to itself"
        }

        test "a genuine token re-mints the scope it was issued with — across a restart over the same key ring" {
            let ring = ringStorage ()
            let scope = ScopeResolution.ofStorageScope (storage "team-a")
            let purpose = jobPurpose "team-a" (Guid.NewGuid())
            let token = issued (carrierOver ring) scope purpose

            match (carrierOver ring).Redeem(token, purpose) with
            | Ok redeemed ->
                Expect.equal redeemed scope "the same scope"
                Expect.equal redeemed.Storage (Some(storage "team-a")) "container and persistence included"
            | Error refusal -> failtestf "redeem: %s" (CarriedScopeRefusal.describe refusal)

            Expect.equal
                ((carrierOver ring).Verify(token, purpose))
                (Ok "team-a")
                "and Verify names the scope without minting it"

            Expect.equal
                ((carrierOver ring).Issue(ResolvedScope.anonymous, purpose))
                (Ok None)
                "the anonymous scope is never carried"
        }

        test "a forged token re-mints nothing" {
            let ring = ringStorage ()
            let scope = ScopeResolution.ofStorageScope (storage "team-a")
            let purpose = jobPurpose "team-a" (Guid.NewGuid())

            // Sealed by a carrier over another key ring: every byte of it is
            // a well-formed token, just not one this deployment issued.
            let foreign = issued (ScopeCarrier.ephemeral ()) scope purpose

            Expect.equal
                ((carrierOver ring).Redeem(foreign, purpose))
                (Error CarriedScopeRefusal.TokenNotIssuedHere)
                "a token another key ring sealed"

            Expect.equal
                ((carrierOver ring).Redeem("not-a-token", purpose))
                (Error CarriedScopeRefusal.TokenNotIssuedHere)
                "a string that was never sealed"

            match (carrierOver ring).Redeem("   ", purpose) with
            | Error(CarriedScopeRefusal.TokenMalformed _) -> ()
            | other -> failtestf "an empty token is malformed, got %A" other
        }

        test "an altered token re-mints nothing" {
            let ring = ringStorage ()
            let scope = ScopeResolution.ofStorageScope (storage "team-a")
            let purpose = jobPurpose "team-a" (Guid.NewGuid())
            let token = issued (carrierOver ring) scope purpose
            let at = token.Length / 2
            let flipped = if token[at] = 'A' then 'B' else 'A'
            let tampered = token.Substring(0, at) + string flipped + token.Substring(at + 1)

            Expect.equal
                ((carrierOver ring).Redeem(tampered, purpose))
                (Error CarriedScopeRefusal.TokenNotIssuedHere)
                "one byte changed is a token the ring did not issue"
        }

        test "a re-purposed token re-mints nothing" {
            let ring = ringStorage ()
            let carrier = carrierOver ring
            let scope = ScopeResolution.ofStorageScope (storage "team-a")
            let issuedFor = jobPurpose "team-a" (Guid.NewGuid())
            let token = issued carrier scope issuedFor

            for other in
                [
                    jobPurpose "team-a" (Guid.NewGuid())
                    jobPurpose
                        "team-b"
                        (match issuedFor with
                         | CarriedScopePurpose.ScheduledJob(_, id) -> id
                         | _ -> Guid.Empty)
                    CarriedScopePurpose.Grant("fact-publication.consent.target", "g-1")
                ] do
                match carrier.Redeem(token, other) with
                | Error(CarriedScopeRefusal.PurposeMismatch _) -> ()
                | result -> failtestf "presented for %A: expected a purpose mismatch, got %A" other result

                match carrier.Verify(token, other) with
                | Error(CarriedScopeRefusal.PurposeMismatch _) -> ()
                | result -> failtestf "verified for %A: expected a purpose mismatch, got %A" other result
        }

        test "a job-store writer who moves a token or a job gets the anonymous scope" {
            let carrier = carrierOver (ringStorage ())
            let scope = ScopeResolution.ofStorageScope (storage "team-a")

            let definition (jobId: Guid) (scopeId: string) (tags: Map<string, string>) : JobDefinition = {
                JobId = jobId
                ScopeId = scopeId
                Handler = "h"
                Payload = ""
                Trigger = Manual
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = Minute
                Status = Active
                CreatedAt = DateTime.UtcNow
                CreatedBy = "alice"
                NextRunAt = None
                LastRunAt = None
                LastRunStatus = None
                LastRunError = None
                ConsecutiveFailures = 0
                Tags = tags
            }

            let jobA = Guid.NewGuid()

            let tags =
                match CarriedJobScope.stamp carrier scope jobA (Map.ofList [ "origin", "caller" ]) with
                | Ok tags -> tags
                | Error e -> failtestf "stamp: %s" e

            Expect.equal
                (CarriedJobScope.ofDefinition carrier (definition jobA "team-a" tags))
                scope
                "the job it was issued for runs under the scope"

            Expect.isTrue
                (CarriedJobScope.ofDefinition carrier (definition (Guid.NewGuid()) "team-a" tags)).IsAnonymous
                "the token copied onto another job runs anonymous"

            Expect.isTrue
                (CarriedJobScope.ofDefinition carrier (definition jobA "team-b" tags)).IsAnonymous
                "the job moved to another scope runs anonymous"

            Expect.isTrue
                (CarriedJobScope.ofDefinition carrier (definition jobA "team-a" (CarriedJobScope.strip tags)))
                    .IsAnonymous
                "a job whose token was deleted runs anonymous"
        }

        test "neither scheduler nor the grant store promotes a string to a scope; a planted promotion is caught" {
            let src = Path.Combine(repoRoot (), "src")

            for file in carriedPaths do
                let path = Path.Combine(src, file)
                Expect.isTrue (File.Exists path) (sprintf "%s exists" file)
                let text = File.ReadAllText path

                match promotions text with
                | [] -> ()
                | found ->
                    failtestf
                        "%s promotes a value to a ResolvedScope:\n%s"
                        file
                        (found |> List.map (fun (_, why) -> "  - " + why) |> String.concat "\n")

                // Go-red, over the file's own text: each planted spelling fires.
                for needle, why in promotionSpellings do
                    Expect.isNonEmpty
                        (promotions (text + "\n" + needle + "(x)"))
                        (sprintf "a planted %s in %s is caught" why file)
        }
    ]

let tests =
    testList "Phase 797 — scope as a choke point" [
        publicSurfaceTests
        requestPathTests
        sourceGuardTests
        carriedMintTests
        reactivePathTests
        carrierTests
    ]
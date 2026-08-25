// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactQueryToolTests

open System
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI.AIToolRegistry
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 559 — the `query_facts` AI tool ───────────────────────────
//
// The fact base goes conversational: a ServerResident tool over the
// scope-resolved `IFactStore.Query`, every returned fact passing the
// Phase 525 disclosure gate at the `FactToolResult` surface. Covered
// here: the declaration shape (`_facts` reserved source / Both /
// ServerResident), the one-knob `FactsCompose.withFactStore`
// registration riding `ServerApp.AITools` (+ the composeAI-shaped
// registry pickup), allow / deny / unknown-id result + marker shapes,
// the deny audit row, scope isolation (a cross-scope fact id is denied
// at the door, never leaked — even through a leaky store), parameter
// validation, and the no-store / no-AI parity guarantees (GP 11/13).

// ── Shared harness (mirrors FactResolverComposeTests) ─────────────

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft (metric: string) (period: TemporalExtent) (inputHashes: string list) (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef metric
    Value = Scalar value
    Period = period
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = inputHashes
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private assertFact (store: IFactStore) (scope: string) (d: FactDraft) : Fact =
    match store.Assert(scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// A real (Phase 519) registry carrying one declared metric.
let private registryWith (staleness: StalenessPolicy) (displayFormat: string) : IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "TestModule"
            Definition = {
                Id = "revenue"
                Name = "Revenue"
                Unit = "GBP"
                Dimensionality = "currency"
                Direction = HigherIsBetter
                DisplayFormat = displayFormat
                Staleness = staleness
                ProducingOperation = None
                CanonicalMethod = None
                RecomputePolicy = None
                RollUp = None
            }
        }
    ] []

/// The composed fact tier the way a deployment builds it —
/// `FactsCompose.withFactStore` under the knob, its service config run
/// over a substrate-seeded collection. Returns (composed app, provider).
let private composedUnder (knob: FactStoreMode) (registry: IMetricRegistry option) : ServerApp * ServiceProvider =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = knob
                }
        }
        |> FactsCompose.withFactStore

    let services = ServiceCollection()

    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    registry
    |> Option.iter (fun r -> services.AddSingleton<IMetricRegistry>(r) |> ignore)

    match app.Extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    app, services.BuildServiceProvider()

/// A request context the registered executor resolves scope / DI from.
let private contextFor (sp: IServiceProvider) (scopeId: string) (userId: string) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- sp

    let scope: StorageScope = {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx :> HttpContext

let private executeVia (sp: IServiceProvider) (scopeId: string) (argsJson: string) : JsonElement =
    let result =
        FactQueryTool.execute (contextFor sp scopeId "user-1") argsJson
        |> Async.RunSynchronously

    (JsonDocument.Parse result).RootElement.Clone()

let private str (el: JsonElement) (name: string) : string = el.GetProperty(name).GetString()

let private items (el: JsonElement) (name: string) : JsonElement list =
    el.GetProperty(name).EnumerateArray() |> Seq.map _.Clone() |> List.ofSeq

let private queryArgs =
    """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue"}"""

// ── Declaration + registration (559.C) ────────────────────────────

let registrationTests =
    testList "Phase 559 query_facts registration" [

        test "the declaration: ServerResident, surface Both, reserved _facts source" {
            let def = FactQueryTool.definition
            Expect.equal def.Name "query_facts" "tool name"
            Expect.equal def.Location ServerResident "runs on the server"
            Expect.equal def.Surface Both "offered on both AI surfaces"

            Expect.equal
                def.SourceModule
                FactEvents.SourceModule
                "the fact store's reserved _facts source — exempt from the orphaned-tool-reference rule"

            Expect.isNone def.EmitsActions "chat-only tool"

            Expect.equal
                (def.Parameters |> List.filter _.Required |> List.map _.Name)
                [ "subject_hierarchy"; "metric" ]
                "exactly the subject hierarchy + metric id are required"

            Expect.stringContains
                (def.Description.ToLowerInvariant())
                "registry"
                "the description teaches that the subject/metric vocabulary comes from the registry"
        }

        test "EnabledFactStore declares the tool on ServerApp.AITools alongside the store — one knob" {
            let app, sp = composedUnder EnabledFactStore None

            Expect.equal (app.AITools |> List.map (fun (def, _) -> def.Name)) [ "query_facts" ] "one declaration"

            Expect.isFalse (isNull (box (sp.GetService<IFactStore>()))) "the store rides the same knob"

            // The declaration alone arms nothing AI-shaped: no registry,
            // no AI service enters DI from the facts compose (GP 13 — a
            // no-AI deployment never consumes the declaration).
            Expect.isTrue (isNull (sp.GetService(typeof<AIToolRegistry>))) "no AI tool registry in a no-AI composition"
        }

        test "NoFactStore composes byte-identically: no tool declared, the app untouched (GP 11 / GP 13)" {
            let before = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = NoFactStore
                    }
            }

            let after = FactsCompose.withFactStore before

            Expect.isTrue (obj.ReferenceEquals(before, after)) "withFactStore returns the app itself unchanged"
            Expect.isEmpty after.AITools "no query_facts declaration"
        }

        test "the composeAI-shaped pickup registers the tool into the AI tool registry" {
            // Mirror of `composeAI`'s module-tool pickup: every
            // `ServerApp.AITools` pair becomes a `RegisteredTool`.
            let app, _ = composedUnder EnabledFactStore None

            let registry = AIToolRegistry()
            registry.RegisterAll(app.AITools |> List.map (fun (def, exec) -> createTool def exec))

            match registry.FindByName "query_facts" with
            | Some tool -> Expect.equal tool.Definition.SourceModule "_facts" "the registered tool is the fact tool"
            | None -> failtest "query_facts did not reach the AI tool registry"
        }

        test "a deployment with neither store nor AI resolves no fact tool surface at all" {
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- ServiceCollection().BuildServiceProvider()

            let result =
                FactQueryTool.execute (ctx :> HttpContext) queryArgs |> Async.RunSynchronously

            let el = (JsonDocument.Parse result).RootElement

            Expect.stringContains (str el "error") "not composed" "the defensive arm names the missing substrate"
        }
    ]

// ── Allow / deny / unknown-id shapes (559.A + 559.B + 559.D) ──────

let egressTests =
    testList "Phase 559 query_facts egress" [

        testCase "a Surfaceable fact answers with rendering, freshness, and method identity — never store internals"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith UntilSuperseded "N0"))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            let fact = assertFact store scope (draft "revenue" q2 [ "h1" ] 21800m)

            let el = executeVia sp scope queryArgs

            match items el "facts" with
            | [ f ] ->
                Expect.equal (str f "factId") fact.FactId "the content-addressed id"
                Expect.equal (str f "subject") "brand/acme" "the canonical subject rendering"
                Expect.equal (str f "metric") "revenue" "the registered metric id"
                Expect.equal (str f "rendering") "21,800" "rendered under the declared N0 format"
                Expect.equal (str f "freshness") "Fresh" "a current head is fresh under UntilSuperseded"
                Expect.equal (str f "method") "computed:rollup:1:p0" "the method identity, not the payload"
                Expect.equal (str f "periodLabel") "Q2-2026" "the valid-time label"

                Expect.equal (f.GetProperty("supersededBy").ValueKind) JsonValueKind.Null "no successor exists"

                Expect.isFalse (f.TryGetProperty("evidence") |> fst) "raw evidence never rides the result"
                Expect.isFalse (f.TryGetProperty("disclosure") |> fst) "raw classification never rides the result"
            | other -> failtestf "expected exactly one fact, got %d" (List.length other)

            Expect.isEmpty (items el "withheld") "nothing withheld"

        testCase "an Internal fact projects to the typed marker — policy ref + canonical refusal, never the value"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore None
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            let surfaceable = assertFact store scope (draft "revenue" q2 [ "h1" ] 21800m)

            // A competing Internal fact (different method ⇒ both current).
            let internalFact =
                assertFact store scope {
                    draft "revenue" q2 [ "h2" ] 19999m with
                        Method = Computed("intermediate", "1", "p1")
                        Disclosure = Internal
                }

            let raw =
                FactQueryTool.execute (contextFor sp scope "user-1") queryArgs
                |> Async.RunSynchronously

            let el = (JsonDocument.Parse raw).RootElement

            Expect.equal
                (items el "facts" |> List.map (fun f -> str f "factId"))
                [ surfaceable.FactId ]
                "only the Surfaceable fact crosses the door"

            match items el "withheld" with
            | [ marker ] ->
                Expect.equal (str marker "factId") internalFact.FactId "the marker names the fact id"
                Expect.equal (str marker "policyRef") "Internal" "the marker names the classification"

                Expect.equal
                    (str marker "status")
                    (FactDisclosureVerdict.refusalText "Internal")
                    "the canonical refusal wording — refusal text never drifts between doors"
            | other -> failtestf "expected exactly one withheld marker, got %d" (List.length other)

            Expect.isFalse (raw.Contains "19999") "the denied value is nowhere in the payload"

        testCase "a query hitting only restricted facts returns markers, never an empty result"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore None
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            let restricted =
                assertFact store scope {
                    draft "revenue" q2 [ "h1" ] 555m with
                        Disclosure = Restricted "licence-x"
                }

            let el = executeVia sp scope queryArgs

            Expect.isEmpty (items el "facts") "nothing disclosable"

            match items el "withheld" with
            | [ marker ] ->
                Expect.equal (str marker "factId") restricted.FactId "the marker survives instead of an empty result"

                Expect.equal
                    (str marker "policyRef")
                    "licence-x"
                    "an unknown Restricted policy resolves conservatively to deny, naming the policy"
            | other -> failtestf "expected one marker, got %d" (List.length other)
    ]

// ── Deny audit + scope isolation (559.B + 559.D) ──────────────────

/// A deliberately *leaky* store double: `Query` ignores the caller's
/// scope and returns whatever it was handed — modelling a buggy or
/// hostile store implementation. The disclosure gate must still hold
/// the door: it re-resolves ids through the REAL scope-filtered store.
type private LeakyStore(leaked: Fact list) =
    interface IFactStore with
        member _.Assert(_, _) = async { return Error "read-only double" }
        member _.Get(_, _) = async { return None }
        member _.Query(_, _) = async { return leaked }

        member _.QueryWithCompetition(_, _) = async {
            return leaked |> List.map (fun f -> { Fact = f; CompetingMethods = [] })
        }

        member _.QuerySupersessionChain(_, _) = async { return [] }

        member _.QueryPopulation(_, _) = async { return Error "read-only double: no population read" }

let auditAndScopeTests =
    testList "Phase 559 query_facts audit + scope isolation" [

        testCaseAsync "a denied fact writes a FactDisclosureDenied audit row at the ToolResult surface"
        <| async {
            let _, sp = composedUnder EnabledFactStore None
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            assertFact store scope {
                draft "revenue" q2 [ "h1" ] 19999m with
                    Disclosure = Internal
            }
            |> ignore

            let! _ = FactQueryTool.execute (contextFor sp scope "user-1") queryArgs |> fun a -> a

            let events = sp.GetRequiredService<IEventStore>()
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal (List.length denies) 1 "exactly one deny audited"
            Expect.stringContains denies.Head.Payload "ToolResult" "the audit row names the egress surface"
            Expect.isFalse (denies.Head.Payload.Contains "19999") "the audit row never carries the value"
        }

        testCaseAsync "another scope's facts are structurally unreachable through the tool (GP 4)"
        <| async {
            let _, sp = composedUnder EnabledFactStore None
            let scopeA = newScope ()
            let scopeB = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            assertFact store scopeA (draft "revenue" q2 [ "h1" ] 21800m) |> ignore

            let own = executeVia sp scopeA queryArgs
            Expect.equal (List.length (items own "facts")) 1 "resolvable in its own scope"

            let foreign = executeVia sp scopeB queryArgs
            Expect.isEmpty (items foreign "facts") "no cross-scope fact"
            Expect.isEmpty (items foreign "withheld") "and nothing to mark — the query itself is scope-filtered"
        }

        testCaseAsync
            "a cross-scope fact id leaking past the query is denied at the door with the unknown-id marker, never disclosed"
        <| async {
            // The real store holds the fact in scope A only; the leaky
            // double surfaces it to a scope-B query. The gate — checked
            // against the REAL store under scope B — cannot resolve the
            // id and denies conservatively.
            let realStore =
                BlobFactStore.create (InMemoryBlobStorage()) (InMemoryEventStore.InMemoryEventStore())

            let scopeA = newScope ()
            let scopeB = newScope ()
            let crossScope = assertFact realStore scopeA (draft "revenue" q2 [ "h1" ] 12345m)

            let gate =
                FactDisclosureGate.create realStore (InMemoryEventStore.InMemoryEventStore())

            let! raw =
                FactQueryTool.executeWith
                    (LeakyStore [ crossScope ])
                    gate
                    None
                    (fun () -> DateTime.UtcNow)
                    scopeB
                    "user-1"
                    queryArgs

            let el = (JsonDocument.Parse raw).RootElement

            Expect.isEmpty (items el "facts") "the leaked fact never crosses the door"

            match items el "withheld" with
            | [ marker ] ->
                Expect.equal (str marker "factId") crossScope.FactId "the marker names the id"
                Expect.equal (str marker "policyRef") "unknown-fact" "unresolvable in this scope ⇒ unknown-id deny"

                Expect.equal
                    (str marker "status")
                    (FactDisclosureVerdict.refusalText "unknown-fact")
                    "the canonical refusal wording"
            | other -> failtestf "expected one marker, got %d" (List.length other)

            Expect.isFalse (raw.Contains "12345") "the cross-scope value is nowhere in the payload"
        }
    ]

// ── Temporal parameters (559.A) ───────────────────────────────────

let temporalTests =
    testList "Phase 559 query_facts temporal parameters" [

        testCaseAsync "include_superseded returns the correction history with supersession pointers"
        <| async {
            let mutable nowRef = DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)

            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let store =
                BlobFactStore.createWithClock (InMemoryBlobStorage()) events (fun () -> nowRef)

            let gate = FactDisclosureGate.create store events
            let scope = newScope ()

            let original = assertFact store scope (draft "revenue" q2 [ "h1" ] 19000m)
            nowRef <- nowRef.AddMinutes 5.0
            let successor = assertFact store scope (draft "revenue" q2 [ "h2" ] 21800m)
            Expect.equal successor.Supersedes (Some original.FactId) "harness sanity: one lineage"

            let run (args: string) =
                FactQueryTool.executeWith store gate None (fun () -> nowRef) scope "user-1" args

            // The default view: only the current head, fresh.
            let! headOnly = run queryArgs
            let headEl = (JsonDocument.Parse headOnly).RootElement

            Expect.equal
                (items headEl "facts" |> List.map (fun f -> str f "factId", str f "freshness"))
                [ successor.FactId, "Fresh" ]
                "the live view is the fresh successor only"

            // The full lineage under include_superseded.
            let! history =
                run
                    """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue","include_superseded":true}"""

            let histEl = (JsonDocument.Parse history).RootElement

            let byId =
                items histEl "facts" |> List.map (fun f -> str f "factId", f) |> Map.ofList

            Expect.equal byId.Count 2 "both lineage members return"

            let originalRow = byId[original.FactId]

            Expect.equal (str originalRow "supersededBy") successor.FactId "the pointer names the correction"
            Expect.equal (str originalRow "freshness") "Stale" "a superseded fact is stale under UntilSuperseded"
            Expect.equal (str originalRow "rendering") "19000" "history keeps its own value"

            // AsOf reconstruction: what we knew at the original's assert.
            let! asOfView =
                run (
                    sprintf
                        """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue","as_of":"%s"}"""
                        (original.AsOf.ToUniversalTime().ToString "o")
                )

            let asOfEl = (JsonDocument.Parse asOfView).RootElement

            match items asOfEl "facts" with
            | [ f ] ->
                Expect.equal (str f "factId") original.FactId "the head visible at as_of"
                Expect.equal (str f "supersededBy") successor.FactId "annotated with its later correction"
            | other -> failtestf "expected the reconstructed head, got %d" (List.length other)
        }

        testCaseAsync "the period window restricts to overlapping facts only"
        <| async {
            let _, sp = composedUnder EnabledFactStore None
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            let q1: TemporalExtent = {
                From = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                To = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                Label = Some "Q1-2026"
            }

            let q1Fact = assertFact store scope (draft "revenue" q1 [ "h1" ] 100m)
            assertFact store scope (draft "revenue" q2 [ "h2" ] 200m) |> ignore

            let el =
                executeVia
                    sp
                    scope
                    """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue","period_from":"2026-01-01T00:00:00Z","period_to":"2026-04-01T00:00:00Z"}"""

            Expect.equal
                (items el "facts" |> List.map (fun f -> str f "factId"))
                [ q1Fact.FactId ]
                "only the Q1-overlapping fact returns"
        }
    ]

// ── Parameter validation (559.D) ──────────────────────────────────

let validationTests =
    testList "Phase 559 query_facts parameter validation" [

        let expectError (args: string) (fragment: string) (label: string) =
            testCase label
            <| fun () ->
                let _, sp = composedUnder EnabledFactStore None
                let el = executeVia sp (newScope ()) args

                Expect.stringContains (str el "error") fragment "the error names the offending argument"

                Expect.isFalse (el.TryGetProperty("facts") |> fst) "no result payload on a validation failure"

        expectError """{"metric":"revenue"}""" "subject_hierarchy" "a missing subject_hierarchy is refused"

        expectError """{"subject_hierarchy":"brand"}""" "metric" "a missing metric is refused"

        expectError "not json at all" "not valid JSON" "malformed argument JSON is refused"

        expectError
            """{"subject_hierarchy":"brand","metric":"revenue","as_of":"yesterday-ish"}"""
            "as_of"
            "an unparseable as_of instant is refused"

        expectError
            """{"subject_hierarchy":"brand","metric":"revenue","period_from":"2026-04-01T00:00:00Z","period_to":"2026-01-01T00:00:00Z"}"""
            "period_from"
            "an inverted period window is refused"

        expectError
            """{"subject_hierarchy":"brand","metric":"revenue","include_superseded":"yes"}"""
            "include_superseded"
            "a non-boolean include_superseded is refused"

        expectError
            """{"subject_hierarchy":42,"metric":"revenue"}"""
            "subject_hierarchy"
            "a non-string subject is refused"
    ]

let tests =
    testList "Phase 559 query_facts AI tool" [
        registrationTests
        egressTests
        auditAndScopeTests
        temporalTests
        validationTests
    ]
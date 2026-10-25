// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GroundedNarrativeTests

// ─── Phase 985 — grounded narratives ─────────────────────────────────
//
// A model-written narrative is published only when every number in it is a
// reference to a current, disclosable Fact (or a declared citable
// reference) that states the value it refers to. This pack tries to DEFEAT
// that gate — a gate proved only by its happy path is not a gate:
//
//   A. the pure check, claim by claim: a bare number, a number in words, a
//      metric with no reference, a reference that does not resolve, one that
//      is superseded, one the surface may not disclose, a misstated value, a
//      computed percentage, an unregistered reference kind, figures hidden
//      in a label, a table cell or a chart's props — each refused with its
//      own reason, every offence named;
//   B. the fact-tier gate over a real store, and the certificate it issues
//      over a published narrative;
//   C. the run end to end on a RAG app composed with
//      `RAGServerApp.withFacts FactsCompose.withFactTier`: a scripted model
//      that reads a fact through `query_facts` and then writes each defeat
//      case — every one refused before publication and logged, and the
//      grounded one published, indexed, certified, and marked stale when its
//      fact is superseded;
//   D. both triggers: the report producer and the data-arrival reaction;
//   E. who pays (Phase 995): the run's provider resolves under the scope it
//      carries, so a team's own key funds it, and a scope with no usable key
//      is a typed, permanent failure;
//   F. its call budget (Phase 1109): a report that streams for longer than
//      the chat's 50 s completes under the run's own budget, the interactive
//      loop still stops at 50 s, a reply cut at the run's budget is a typed
//      permanent outcome naming how to raise it, the reply-length budget
//      reaches the provider call, and a transient failure IS retried by the
//      in-process scheduler;
//   G. the wording is the admin's (Phase 1111): a team's saved wording
//      changes the instruction's wording lines and nothing else, an
//      undeclared section is refused by name, a reset restores the
//      declaration, the audit row names the version, a wording that drops
//      citations still meets the gate, and one team's wording does not reach
//      another team's run.

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.Narrative
open ToolUp.Platform.Secrets
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.GroundedNarrativeRun
open ToolUp.ArtefactSigning
open ToolUp.Facts
open ToolUp.RAG
open ToolUp.RAG.RAGCompose
open ToolUp.Reporting
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ── Document builders ─────────────────────────────────────────────

let private para (spans: InlineSpan list) = NarrativeElement.Paragraph spans
let private text (s: string) = InlineSpan.Text s

let private metric label value reference =
    InlineSpan.Metric(label, value, reference)

/// A document whose STRUCTURE carries digits on purpose — the title and the
/// section heading are the deployment's, not the model's, and are not
/// inspected.
let private docOf (elements: NarrativeElement list) : NarrativeDocument = {
    Title = "Weekly summary 2026"
    Subtitle = None
    Sections = [
        {
            Id = "summary"
            Heading = "Week 40"
            Subheading = None
            Elements = elements
        }
    ]
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

let private revenueId = String('a', 64)
let private supersededId = String('b', 64)
let private internalId = String('c', 64)

let private resolutions: Map<string, GroundingResolution> =
    Map.ofList [
        revenueId, GroundingCurrent([ "1248300" ], Some 1248300m, false)
        supersededId, GroundingSuperseded(Some revenueId)
        internalId, GroundingWithheld "Internal"
        "calendar:launch", GroundingCurrent([ "5 May" ], None, false)
        "holiday:summer", GroundingUnregisteredKind "holiday"
    ]

let private resolve (reference: string) =
    resolutions.TryFind reference |> Option.defaultValue GroundingUnresolved

let private check (elements: NarrativeElement list) =
    NarrativeGrounding.check NarrativeGrounding.defaultPolicy resolve (docOf elements)

let private offencesOf (verdict: NarrativeGroundingVerdict) : GroundingOffence list =
    match verdict with
    | Ungrounded offences -> offences
    | Grounded citations -> failtestf "expected a refusal, the gate passed it citing %A" citations

let private reasonsOf verdict = offencesOf verdict |> List.map _.Reason

let private grounded (elements: NarrativeElement list) =
    match check elements with
    | Grounded citations -> citations
    | Ungrounded offences ->
        failtestf "expected a pass, refused: %s" (offences |> List.map GroundingOffence.describe |> String.concat "; ")

// ── A. The pure check ─────────────────────────────────────────────

let pureGateTests =
    testList "Phase 985 A — the grounding check refuses every ungrounded claim" [

        test "a fully grounded narrative passes and names what it cites" {
            let citations =
                grounded [
                    para [
                        text "Revenue reached "
                        metric "Revenue" "1,248,300" (Some revenueId)
                        text " this week."
                    ]
                ]

            Expect.equal
                citations
                [
                    {
                        ReferenceKind = NarrativeCitation.FactKind
                        Reference = revenueId
                    }
                ]
                "the one fact it cites"
        }

        test "a figure stated at a coarser precision passes; a wrong one at the same precision does not" {
            grounded [ para [ metric "Revenue" "1.25m" (Some revenueId) ] ] |> ignore
            grounded [ para [ metric "Revenue" "£1.25m" (Some revenueId) ] ] |> ignore
            grounded [ para [ metric "Revenue" "1m" (Some revenueId) ] ] |> ignore

            Expect.equal
                (reasonsOf (check [ para [ metric "Revenue" "1.3m" (Some revenueId) ] ]))
                [ MisstatedValue(revenueId, "1.3m", "1248300") ]
                "1,248,300 is 1.2m to one place, never 1.3m"
        }

        test "a bare number in prose is refused" {
            Expect.equal
                (reasonsOf (check [ para [ text "Revenue reached 1,248,300 this week." ] ]))
                [ UnreferencedNumber ]
                "an unreferenced numeral"
        }

        test "a number spelled out in words is refused" {
            for prose in
                [
                    "Revenue rose by twelve percent."
                    "Sales doubled."
                    "One million units shipped."
                ] do
                Expect.equal (reasonsOf (check [ para [ text prose ] ])) [ UnreferencedNumber ] prose

            Expect.equal
                (reasonsOf (check [ para [ text "Revenue rose by ½ again." ] ]))
                [ UnreferencedNumber ]
                "a vulgar fraction is a number character too"
        }

        test "a metric span with no reference is refused" {
            Expect.equal
                (reasonsOf (check [ para [ metric "Revenue" "1,248,300" None ] ]))
                [ MetricWithoutReference ]
                "a figure with nothing behind it"
        }

        test "a reference that does not resolve is refused" {
            let missing = String('e', 64)

            Expect.equal
                (reasonsOf (check [ para [ metric "Revenue" "1,248,300" (Some missing) ] ]))
                [ UnresolvedReference missing ]
                "a reference to a fact that does not exist"
        }

        test "a reference to a superseded fact is refused, naming its head" {
            Expect.equal
                (reasonsOf (check [ para [ metric "Revenue" "1,000" (Some supersededId) ] ]))
                [ SupersededReference(supersededId, Some revenueId) ]
                "superseded, with the current head"
        }

        test "a reference to a fact the surface may not disclose is refused, naming the policy" {
            Expect.equal
                (reasonsOf (check [ para [ metric "Cost" "900" (Some internalId) ] ]))
                [ UndisclosableReference(internalId, "Internal") ]
                "withheld at this surface"
        }

        test "a reference whose value the prose misstates is refused" {
            Expect.equal
                (reasonsOf (check [ para [ metric "Revenue" "1,300,000" (Some revenueId) ] ]))
                [ MisstatedValue(revenueId, "1,300,000", "1248300") ]
                "the right fact, the wrong number"
        }

        test "a percentage computed from two facts is refused, as prose and as a span (the 985.F decision)" {
            Expect.equal
                (reasonsOf (check [ para [ text "Margin was 12.5% of revenue." ] ]))
                [ UnreferencedNumber ]
                "as prose it is an unreferenced number"

            Expect.equal
                (reasonsOf (check [ para [ metric "Margin" "12.5%" (Some revenueId) ] ]))
                [ MisstatedValue(revenueId, "12.5%", "1248300") ]
                "citing an input, it misstates that input"
        }

        test "a declared reference kind resolves; an unregistered kind is refused by name" {
            grounded [
                para [ text "The launch was on "; metric "Launch" "5 May" (Some "calendar:launch") ]
            ]
            |> ignore

            Expect.equal
                (reasonsOf (check [ para [ metric "Holiday" "August" (Some "holiday:summer") ] ]))
                [ UnregisteredReferenceKind("holiday", "holiday:summer") ]
                "a kind no deployment registered"
        }

        test "a figure hidden in a metric's label is refused" {
            let offences =
                offencesOf (check [ para [ metric "Revenue 2025" "1,248,300" (Some revenueId) ] ])

            Expect.equal (offences |> List.map _.Reason) [ UnreferencedNumber ] "the label is prose"
            Expect.equal offences.Head.Claim "Revenue 2025" "and the offence quotes it"
        }

        test "EVERY offence is named, in document order, with a distinct location" {
            let offences =
                offencesOf (
                    check [
                        para [ text "We sold 40 units." ]
                        para [ metric "Revenue" "1,248,300" None ]
                        para [ metric "Cost" "900" (Some internalId) ]
                    ]
                )

            Expect.equal
                (offences |> List.map _.Reason)
                [
                    UnreferencedNumber
                    MetricWithoutReference
                    UndisclosableReference(internalId, "Internal")
                ]
                "three offences, three reasons, in order"

            Expect.equal (offences |> List.map _.Location |> List.distinct |> List.length) 3 "each at its own location"

            Expect.stringContains
                offences.[1].Location
                "section 'summary' › element 2"
                "locations name section and element"
        }

        test "numbers inside a deterministic table are checked the same way" {
            let table =
                NarrativeElement.Table(
                    [ "Brand", TableAlignment.Left; "Revenue", TableAlignment.Right ],
                    [
                        [ [ text "Acme" ]; [ metric "" "1,248,300" (Some revenueId) ] ]
                        [ [ text "Bolt" ]; [ text "990" ] ]
                    ]
                )

            let offences = offencesOf (check [ table ])
            Expect.equal (offences |> List.map _.Reason) [ UnreferencedNumber ] "one ungrounded cell"
            Expect.stringContains offences.Head.Location "row 2 › column 2" "and it is named"
        }

        test "a chart component's factRefs are citations; a figure in any other prop is prose" {
            let chart =
                NarrativeElement.Component(
                    "bar-chart",
                    Map.ofList [ "factRefs", revenueId + "," + supersededId; "title", "Top 5 brands" ]
                )

            Expect.equal
                (reasonsOf (check [ chart ]))
                [ SupersededReference(supersededId, Some revenueId); UnreferencedNumber ]
                "a superseded series and a figure in the title"

            let clean =
                NarrativeElement.Component("bar-chart", Map.ofList [ "factRefs", revenueId; "title", "Top brands" ])

            Expect.equal (grounded [ clean ]).Length 1 "a chart over current facts passes"
        }

        test "the stated-value parser reads scale, precision and sign, and refuses words" {
            Expect.equal (NarrativeGrounding.parseStated "1.25m") (Some(1_250_000m, 5_000m, false)) "scaled"
            Expect.equal (NarrativeGrounding.parseStated "-3.5%") (Some(-3.5m, 0.05m, true)) "a percentage"
            Expect.equal (NarrativeGrounding.parseStated "GBP 1,250") (Some(1_250m, 0.5m, false)) "a currency code"
            Expect.isNone (NarrativeGrounding.parseStated "about 1,250") "words before a figure are not a figure"
            Expect.isNone (NarrativeGrounding.parseStated "1,250 units") "nor are words after one"
        }
    ]

// ── B. The fact-tier gate over a real store ───────────────────────

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private september: TemporalExtent = {
    From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-09"
}

let private assertedDraft (metricId: string) (value: decimal) (disclosure: Disclosure) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef metricId
    Value = Scalar value
    Period = september
    Method = HumanAsserted "analyst"
    Evidence = {
        ResultRef = None
        InputHashes = []
        TriggerRef = None
    }
    Confidence = None
    Disclosure = disclosure
}

let private assertOk (store: IFactStore) (scopeId: string) (draft: FactDraft) : Fact =
    match store.Assert(scopeId, draft) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// A declared reference kind for dated calendar events.
let private calendarKind =
    { new ICitableReferenceKind with
        member _.Kind = "calendar"

        member _.Resolve(_, _, id) = async {
            return
                match id with
                | "launch" -> CitableReferenceCurrent([ "5 May" ], None)
                | "moved" -> CitableReferenceSuperseded(Some "launch")
                | _ -> CitableReferenceUnresolved
        }
    }

type private GateFixture = {
    Store: IFactStore
    Gate: INarrativeGroundingGate
    Secrets: ISecretStore
}

let private gateFixture () : GateFixture =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    let disclosure = FactDisclosureGate.create store events
    let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

    let graph =
        ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

    let secrets = InMemorySecretStore() :> ISecretStore
    let audit = AuditLog.NoOpAuditLog() :> IAuditLog
    let signer = DefaultArtefactSigner.createSystem secrets audit "grounding-v1" Ed25519

    let issuer =
        GroundingCertificate.createIssuer graph store disclosure events (Some signer)

    {
        Store = store
        Gate = FactNarrativeGroundingGate.create store disclosure None [ calendarKind ] issuer
        Secrets = secrets
    }

let private gateCheck (fixture: GateFixture) (scopeId: string) (elements: NarrativeElement list) =
    fixture.Gate.Check(scopeId, "reader", FactNarrativePublication, docOf elements)
    |> Async.RunSynchronously

let factGateTests =
    testList "Phase 985 B — the fact-tier gate resolves against the store" [

        test "current, superseded, withheld and missing facts each get their own verdict" {
            let fixture = gateFixture ()
            let scope = "team-" + Guid.NewGuid().ToString "N"
            let first = assertOk fixture.Store scope (assertedDraft "revenue" 1000m Surfaceable)
            let head = assertOk fixture.Store scope (assertedDraft "revenue" 1250m Surfaceable)
            let hidden = assertOk fixture.Store scope (assertedDraft "cost" 900m Internal)

            Expect.notEqual first.FactId head.FactId "a re-assertion of a different value is a new fact"

            match gateCheck fixture scope [ para [ metric "Revenue" "1,250" (Some head.FactId) ] ] with
            | Grounded [ c ] -> Expect.equal c.Reference head.FactId "the head passes"
            | other -> failtestf "the current head must pass, got %A" other

            Expect.equal
                (reasonsOf (gateCheck fixture scope [ para [ metric "Revenue" "1,000" (Some first.FactId) ] ]))
                [ SupersededReference(first.FactId, Some head.FactId) ]
                "the superseded fact is refused, naming its head"

            Expect.equal
                (reasonsOf (gateCheck fixture scope [ para [ metric "Cost" "900" (Some hidden.FactId) ] ]))
                [ UndisclosableReference(hidden.FactId, "Internal") ]
                "the Internal fact is withheld at the publication surface"

            let missing = String('f', 64)

            Expect.equal
                (reasonsOf (gateCheck fixture scope [ para [ metric "Revenue" "1,250" (Some missing) ] ]))
                [ UnresolvedReference missing ]
                "an id the scope does not hold"

            Expect.equal
                (reasonsOf (
                    gateCheck fixture ("team-other-" + scope) [ para [ metric "Revenue" "1,250" (Some head.FactId) ] ]
                ))
                [ UnresolvedReference head.FactId ]
                "another scope's fact does not resolve here (GP 4)"
        }

        test "declared reference kinds resolve through the registered kind; others are refused" {
            let fixture = gateFixture ()

            match gateCheck fixture "team-cal" [ para [ metric "Launch" "5 May" (Some "calendar:launch") ] ] with
            | Grounded [ c ] -> Expect.equal c.ReferenceKind "calendar" "cited under its kind"
            | other -> failtestf "a current declared reference passes, got %A" other

            Expect.equal
                (reasonsOf (gateCheck fixture "team-cal" [ para [ metric "Launch" "5 May" (Some "calendar:moved") ] ]))
                [ SupersededReference("calendar:moved", Some "launch") ]
                "a superseded declared reference is refused"

            Expect.equal
                (reasonsOf (gateCheck fixture "team-cal" [ para [ metric "Holiday" "August" (Some "holiday:summer") ] ]))
                [ UnregisteredReferenceKind("holiday", "holiday:summer") ]
                "a kind nobody registered"
        }

        test "the certificate is rooted at the narrative, cites its facts, and verifies offline" {
            let fixture = gateFixture ()
            let scope = "team-" + Guid.NewGuid().ToString "N"
            let fact = assertOk fixture.Store scope (assertedDraft "revenue" 1250m Surfaceable)
            let narrativeId = Guid.NewGuid().ToString()

            match
                fixture.Gate.Certify(scope, "reader", narrativeId, [ fact.FactId ])
                |> Async.RunSynchronously
            with
            | Error reason -> failtestf "certificate must issue: %s" reason
            | Ok cert ->
                Expect.equal cert.Root narrativeId "rooted at the narrative"
                Expect.equal cert.CitedFactIds [ fact.FactId ] "naming its facts"

                let sealedCert =
                    JsonSerializer.Deserialize<GroundingCertificate>(cert.CertificateJson, FableConverters.create ())

                Expect.isTrue
                    (sealedCert.Body.Nodes
                     |> List.exists (fun n -> n.Id = narrativeId && n.Kind = "NarrativeDocument"))
                    "the root node is a narrative, not a conversation message"

                Expect.isTrue
                    (sealedCert.Body.Edges
                     |> List.exists (fun e -> e.From = narrativeId && e.To = fact.FactId && e.Kind = "CitesFact"))
                    "with a CitesFact edge to the fact"

                Expect.equal
                    (GroundingCertificate.certificateDigest sealedCert.Body)
                    cert.Digest
                    "the digest it reports"

                let verifier = DefaultArtefactVerifier.create fixture.Secrets

                match GroundingCertificate.verify verifier sealedCert |> Async.RunSynchronously with
                | Ok() -> ()
                | Error e -> failtestf "offline verification must succeed: %s" (VerificationError.describe e)
        }
    ]

// ── C. The run, end to end, on a composed RAG app ─────────────────

let private brands: SubjectDefinition = {
    Id = "brand"
    Name = "Brand"
    Levels = [ "brand" ]
    Calendar = None
}

let private revenueDefinition: MetricDefinition = {
    Id = "revenue"
    Name = "revenue"
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private metrics: IMetricRegistry =
    MetricRegistry.build [
        {
            MetricRegistration.Module = "sales"
            Definition = revenueDefinition
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = brands
        }
    ]

/// What the scripted model writes once it has read the fact: given the id
/// and rendering `query_facts` returned, the final answer.
type private Writer = string -> string -> string

let private answer (paragraph: string) =
    JsonSerializer.Serialize {|
        sections = [
            {|
                id = "summary"
                paragraphs = [ paragraph ]
            |}
        ]
    |}

/// The model: one `query_facts` call, then the writer's answer. It records
/// the tools it was offered.
type private ScriptedModel(write: Writer) =
    member val OfferedTools: string list = [] with get, set

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "scripted-narrative"
            Model = "scripted-narrative-model"
        }

        member this.SendMessage(messages, tools, _systemPrompt, _onStream, _retryPolicy) = async {
            this.OfferedTools <- tools |> List.map _.Name

            match messages |> List.exists (fun m -> not (List.isEmpty m.ToolResults)) with
            | false ->
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "query_facts"
                                Arguments = """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue"}"""
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | _ ->
                let result =
                    messages
                    |> List.collect _.ToolResults
                    |> List.tryLast
                    |> Option.map _.Content
                    |> Option.defaultValue "{}"

                let fact =
                    match (JsonDocument.Parse result).RootElement.TryGetProperty "facts" with
                    | true, facts when facts.GetArrayLength() > 0 -> facts.EnumerateArray() |> Seq.head
                    | _ -> failwithf "query_facts returned no fact: %s" result

                return
                    Ok {
                        Content =
                            write (fact.GetProperty("factId").GetString()) (fact.GetProperty("rendering").GetString())
                        ToolCalls = []
                        StopReason = "end_turn"
                        Usage = None
                    }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

type private ScriptedFactory(provider: IAIProvider) =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Ok provider }
        member _.TryResolveByLabel(_, _) = async { return Error NoProviderConfigured }
        member _.BuildPlatform(_, _, _) = None

let private stubProfile =
    { new ToolUp.Platform.Providers.IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

let private constantEmbedder =
    { new IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }
        member _.GenerateEmbeddings texts = async { return texts |> Seq.map (fun _ -> unitVec) |> Seq.toArray }
        member _.ProviderId = "stub"
        member _.ModelId = "constant"
        member _.Dimensions = 8
    }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A knowledge base that records what it was asked to index.
type private RecordingIngestor() =
    let ingested = ConcurrentQueue<NarrativeDocument>()
    member _.Ingested = List.ofSeq ingested

    interface INarrativeIngestor with
        member _.Ingest(_scope, _principal, document) = async {
            ingested.Enqueue document
            return NarrativeIngested(sprintf "kb-%d" ingested.Count)
        }

/// An `IJobScheduler` that dispatches a triggered job synchronously, under
/// the scope it was SCHEDULED with — what a re-minting scheduler does.
type private DispatchingScheduler() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let jobs = ConcurrentDictionary<JobId, JobRegistration * ResolvedScope>()
    let results = ConcurrentQueue<JobResult>()
    let scheduled = ConcurrentQueue<JobRegistration * ResolvedScope>()

    member _.RegisteredHandlers = handlers.Keys |> List.ofSeq
    member _.Results = List.ofSeq results
    member _.Scheduled = List.ofSeq scheduled

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler
        member _.RegisterHandlerAsync(name, handler) = async { return Ok(handlers[name] <- handler) }

        member _.Schedule(scope: ResolvedScope, registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            jobs[id] <- (registration, scope)
            scheduled.Enqueue((registration, scope))
            return Ok id
        }

        member _.Schedule(registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            jobs[id] <- (registration, ResolvedScope.anonymous)
            scheduled.Enqueue((registration, ResolvedScope.anonymous))
            return Ok id
        }

        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs _ = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(scopeId, jobId, byUserId) = async {
            match jobs.TryGetValue jobId with
            | false, _ -> return Error "unknown job"
            | true, (registration, scope) ->
                match handlers.TryGetValue registration.Handler with
                | false, _ -> return Error(sprintf "no handler registered for %s" registration.Handler)
                | true, handler ->
                    let! result =
                        handler.Execute {
                            JobId = jobId
                            ScopeId = scopeId
                            AccessContext = AccessContext.unrestricted (AuthenticatedUser byUserId)
                            Attempt = 1
                            Trigger = registration.Trigger
                            Scope = scope
                            TriggerSource = ScheduledManually byUserId
                            ScheduledAt = DateTime.UtcNow
                            RunningAt = DateTime.UtcNow
                            Payload = registration.Payload
                            DeadLetterDestination = None
                        }

                    results.Enqueue result
                    return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

let private storageFor (teamId: string) : StorageScope = {
    ScopeId = teamId
    Container = $"team-{teamId}"
    Persist = true
}

let private summaryRun: GroundedNarrativeDefinition =
    GroundedNarrativeDefinition.create
        "monthly-brand-summary"
        "Monthly brand summary"
        "sales"
        "narrator"
        "Monthly brand summary"
        "Summarise the month's brand revenue."
        [
            {
                Id = "summary"
                Heading = "Summary"
                Guidance = "One paragraph on the month's revenue."
            }
        ]
    |> GroundedNarrativeDefinition.dependingOn [ "revenue" ]

type private Deployment = {
    Services: ServiceProvider
    Model: ScriptedModel
    Store: IFactStore
    Narratives: INarrativeStore
    Events: IEventStore
    Ingestor: RecordingIngestor
    Scheduler: DispatchingScheduler
    TeamId: string
    Scope: ResolvedScope
}

/// A deployment as one is written: a RAG app with the fact tier composed in
/// one call, grounded narratives composed over it, booted — its AI provider
/// factory built over the scripted model by `factoryFor`.
let private deployComposed
    (options: GroundedNarrativeRunOptions)
    (factoryFor: ScriptedModel -> IAIProviderFactory)
    (write: Writer)
    : Deployment =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let model = ScriptedModel write

    let app =
        RAGServerApp.create (factoryFor model) stubProfile constantEmbedder
        |> RAGServerApp.withStorage storage
        |> RAGServerApp.withFacts FactsCompose.withFactTier
        |> composeRAG
        |> GroundedNarratives.compose options [ summaryRun ]
        |> NarrativePromptHandler.withPromptAdmin

    let services = ServiceCollection() :> IServiceCollection
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let narratives = InMemoryNarrativeStore() :> INarrativeStore
    let ingestor = RecordingIngestor()
    let scheduler = DispatchingScheduler()
    let secrets = InMemorySecretStore() :> ISecretStore

    services.AddSingleton<IBlobStorage>(storage) |> ignore
    services.AddSingleton<IEventStore>(events) |> ignore
    services.AddSingleton<ILogger>(silentLogger) |> ignore
    services.AddSingleton<IMetricRegistry>(metrics) |> ignore
    services.AddSingleton<INarrativeStore>(narratives) |> ignore
    services.AddSingleton<INarrativeIngestor>(ingestor) |> ignore
    services.AddSingleton<IJobScheduler>(scheduler) |> ignore
    services.AddHttpContextAccessor() |> ignore

    services.AddSingleton<IFactTableRegistry>(
        FactTableRegistry.build [] [ BindAllFactTables DefaultFactTableWriter.Destination ]
    )
    |> ignore

    services.AddSingleton<IArtefactSigner>(
        DefaultArtefactSigner.createSystem secrets (AuditLog.NoOpAuditLog() :> IAuditLog) "grounding-v1" Ed25519
    )
    |> ignore

    services.AddSingleton<IDataObjectStore>(DataObjectStore.DataObjectStore(storage, silentLogger) :> IDataObjectStore)
    |> ignore

    let services =
        match app.Extensions.ServiceConfig with
        | Some configure -> configure services
        | None -> services

    let sp = services.BuildServiceProvider()

    for hosted in sp.GetServices<IHostedService>() do
        hosted.StartAsync(CancellationToken.None)
        |> Async.AwaitTask
        |> Async.RunSynchronously

    let teamId = "team-" + Guid.NewGuid().ToString "N"

    {
        Services = sp
        Model = model
        Store = sp.GetRequiredService<IFactStore>()
        Narratives = narratives
        Events = events
        Ingestor = ingestor
        Scheduler = scheduler
        TeamId = teamId
        Scope = ScopeResolution.ofStorageScope (storageFor teamId)
    }

let private deployWith (factoryFor: ScriptedModel -> IAIProviderFactory) (write: Writer) : Deployment =
    deployComposed (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools) factoryFor write

/// A deployment whose factory hands every request the scripted model.
let private deploy (write: Writer) : Deployment =
    deployWith (fun model -> ScriptedFactory(model) :> IAIProviderFactory) write

let private runIn (deployment: Deployment) (scope: ResolvedScope) : GroundedNarrativeOutcome =
    deployment.Services
        .GetRequiredService<IGroundedNarrativeRun>()
        .Run(
            {
                RunKey = summaryRun.Key
                Scope = scope
                Access = None
                Trigger = "test"
            }
        )
    |> Async.RunSynchronously

let private run (deployment: Deployment) = runIn deployment deployment.Scope

let private narrativeRows (deployment: Deployment) : ModuleEvent list =
    deployment.Events.ReadBySource(deployment.TeamId, GroundedNarrativeEvents.SourceModule)
    |> Async.RunSynchronously

let private published (deployment: Deployment) : NarrativeEntryInfo list =
    deployment.Narratives.List(deployment.TeamId, 50) |> Async.RunSynchronously

/// Run `write` against a deployment holding one revenue fact (and, for the
/// superseded / withheld cases, the facts named), and assert it is refused
/// with `expected`, unpublished and logged.
let private refusedWith
    (name: string)
    (write: Fact -> Fact -> Fact -> Writer)
    (expected: Fact -> Fact -> Fact -> GroundingOffenceReason)
    =
    test name {
        // The writer needs facts the deployment has not asserted yet, so it
        // reads them through a cell filled after the deployment is up.
        let facts = ref None

        let writer: Writer =
            fun id rendering -> let (h, o, i) = facts.Value.Value in write h o i id rendering

        let deployment = deploy writer

        let old =
            assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1000m Surfaceable)

        let head =
            assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1250m Surfaceable)

        let hidden =
            assertOk deployment.Store deployment.TeamId (assertedDraft "cost" 900m Internal)

        facts.Value <- Some(head, old, hidden)

        match run deployment with
        | GroundedNarrativeRefused offences ->
            Expect.contains (offences |> List.map _.Reason) (expected head old hidden) "refused for the right reason"
            Expect.isEmpty (published deployment) "and NOTHING was published"
            Expect.isEmpty deployment.Ingestor.Ingested "or indexed"

            let rows = narrativeRows deployment
            Expect.equal (rows |> List.map _.EventType) [ GroundedNarrativeEvents.RefusedType ] "one refusal row"

            Expect.stringContains
                rows.Head.Payload
                (GroundingOffenceReason.describe (expected head old hidden))
                "naming the offence"
        | other -> failtestf "expected a refusal, got %s" (GroundedNarrativeOutcome.describe other)
    }

let runTests =
    testList "Phase 985 C — the run, end to end: refused before publication, or published with provenance" [

        test "a grounded narrative is published, indexed, certified, audited — and goes stale with its fact" {
            let deployment =
                deploy (fun id rendering -> answer $"Acme revenue was [[Revenue|{rendering}|{id}]] in September.")

            let fact =
                assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1250m Surfaceable)

            match run deployment with
            | GroundedNarrativePublished(narrativeId, document, citations, indexed, certificate) ->
                Expect.equal (citations |> List.map _.Reference) [ fact.FactId ] "cites the fact it read"

                Expect.equal
                    (Set.ofList deployment.Model.OfferedTools)
                    (NarrativeGrounding.factTools |> List.map (fun (d, _) -> d.Name) |> Set.ofList)
                    "the model was offered the fact tools and nothing else"

                Expect.equal (published deployment |> List.map _.Id) [ narrativeId ] "published to the narrative store"

                Expect.contains (published deployment).Head.Tags "grounded" "tagged as grounded"

                Expect.equal deployment.Ingestor.Ingested [ document ] "indexed into the knowledge base"
                Expect.equal indexed (Some(NarrativeIngested "kb-1")) "and the outcome says so"

                match certificate with
                | Ok c ->
                    Expect.equal c.Root (string narrativeId) "the certificate is rooted at the narrative"
                    Expect.equal c.CitedFactIds [ fact.FactId ] "naming its facts"
                | Error reason -> failtestf "a certificate must issue with a signer composed: %s" reason

                Expect.equal
                    (narrativeRows deployment |> List.map _.EventType)
                    [ GroundedNarrativeEvents.PublishedType ]
                    "one publication row"

                Expect.equal document.Title summaryRun.Title "the title is the run's, not the model's"

                // Supersede the fact: the Phase 521 join flags the narrative.
                let corrected =
                    assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1300m Surfaceable)

                let stale =
                    NarrativeSupersession.findStaleNarratives
                        deployment.Narratives
                        deployment.TeamId
                        50
                        (Map.ofList [ fact.FactId, Some corrected.FactId ])
                    |> Async.RunSynchronously

                Expect.equal (stale |> List.map (fst >> _.Id)) [ narrativeId ] "the narrative citing it is stale"
            | other -> failtestf "expected publication, got %s" (GroundedNarrativeOutcome.describe other)
        }

        refusedWith
            "a bare number in prose is refused before publication"
            (fun _ _ _ _ _ -> answer "Acme revenue was 1250 in September.")
            (fun _ _ _ -> UnreferencedNumber)

        refusedWith
            "a metric span with no reference is refused before publication"
            (fun _ _ _ _ rendering -> answer $"Acme revenue was [[Revenue|{rendering}]] in September.")
            (fun _ _ _ -> MetricWithoutReference)

        refusedWith
            "a reference to a fact that does not exist is refused before publication"
            (fun _ _ _ _ rendering -> answer $"Acme revenue was [[Revenue|{rendering}|{String('e', 64)}]].")
            (fun _ _ _ -> UnresolvedReference(String('e', 64)))

        refusedWith
            "a reference to a superseded fact is refused before publication"
            (fun _ old _ _ _ -> answer $"Acme revenue was [[Revenue|1000|{old.FactId}]].")
            (fun head old _ -> SupersededReference(old.FactId, Some head.FactId))

        refusedWith
            "a reference to a fact the surface may not disclose is refused before publication"
            (fun _ _ hidden _ _ -> answer $"Acme cost was [[Cost|900|{hidden.FactId}]].")
            (fun _ _ hidden -> UndisclosableReference(hidden.FactId, "Internal"))

        refusedWith
            "a reference whose value the prose misstates is refused before publication"
            (fun _ _ _ id _ -> answer $"Acme revenue was [[Revenue|1300|{id}]].")
            (fun head _ _ -> MisstatedValue(head.FactId, "1300", "1250"))

        refusedWith
            "a number spelled out in words is refused before publication"
            (fun _ _ _ id rendering -> answer $"Acme revenue was [[Revenue|{rendering}|{id}]], twelve percent up.")
            (fun _ _ _ -> UnreferencedNumber)

        refusedWith
            "a percentage computed from two facts is refused before publication"
            (fun _ _ _ id _ -> answer ("Acme margin was [[Margin|28%|" + id + "]]."))
            (fun head _ _ -> MisstatedValue(head.FactId, "28%", "1250"))

        test "output that is not the narrative format publishes nothing and is logged as a failure" {
            let deployment = deploy (fun _ _ -> "Revenue was strong this month.")

            assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1250m Surfaceable)
            |> ignore

            match run deployment with
            | GroundedNarrativeFailed _ -> ()
            | other -> failtestf "expected a failure, got %s" (GroundedNarrativeOutcome.describe other)

            Expect.isEmpty (published deployment) "nothing published"

            Expect.equal
                (narrativeRows deployment |> List.map _.EventType)
                [ GroundedNarrativeEvents.FailedType ]
                "one failure row"
        }

        test "the run refuses the anonymous scope rather than read the anonymous shard" {
            let deployment = deploy (fun id rendering -> answer $"[[Revenue|{rendering}|{id}]]")

            match runIn deployment ResolvedScope.anonymous with
            | GroundedNarrativeFailed reason -> Expect.stringContains reason "anonymous" "naming why"
            | other -> failtestf "expected a failure, got %s" (GroundedNarrativeOutcome.describe other)
        }

        test "an unknown run key fails without consulting the model" {
            let deployment = deploy (fun _ _ -> failwith "the model must not be called")

            let outcome =
                deployment.Services
                    .GetRequiredService<IGroundedNarrativeRun>()
                    .Run(
                        {
                            RunKey = "no-such-run"
                            Scope = deployment.Scope
                            Access = None
                            Trigger = "test"
                        }
                    )
                |> Async.RunSynchronously

            match outcome with
            | GroundedNarrativeFailed reason -> Expect.stringContains reason "no-such-run" "naming the key"
            | other -> failtestf "expected a failure, got %s" (GroundedNarrativeOutcome.describe other)
        }

        test "composing two runs under one key fails the composition, naming both" {
            let twin = { summaryRun with DisplayName = "Twin" }

            Expect.throwsT<InvalidOperationException>
                (fun () ->
                    GroundedNarratives.compose
                        (GroundedNarrativeRunOptions.create [])
                        [ summaryRun; twin ]
                        ServerApp.empty
                    |> ignore)
                "a contested key must not compose"
        }
    ]

// ── D. The triggers ───────────────────────────────────────────────

let private documentFor (title: string) : NarrativeDocument = { docOf [] with Title = title }

let private stubRun (outcome: GroundedNarrativeOutcome) (seen: ConcurrentQueue<GroundedNarrativeRequest>) =
    { new IGroundedNarrativeRun with
        member _.Run request = async {
            seen.Enqueue request
            return outcome
        }
    }

let triggerTests =
    testList "Phase 985 D — both triggers reach the run, and neither names a report" [

        test "as a report producer: the job's resolved scope reaches the run, and the narrative fills the template" {
            let seen = ConcurrentQueue<GroundedNarrativeRequest>()
            let doc = documentFor "Monthly"

            let producer =
                GroundedNarrativeProducer.create
                    "grounded-monthly"
                    "Monthly"
                    (stubRun (GroundedNarrativePublished(Guid.NewGuid(), doc, [], None, Error "no signer")) seen)
                    "monthly-brand-summary"
                    "monthly-template"
                    "body"

            let scope = ScopeResolution.ofStorageScope (storageFor "team-producer")

            match
                ReportProducerScope.within scope (producer.Resolve "team-producer" Map.empty)
                |> Async.RunSynchronously
            with
            | Ok request ->
                Expect.equal request.TemplateId "monthly-template" "the deployment's template"

                match request.Values.TryFind "body" with
                | Some(NarrativeValue d) -> Expect.equal d doc "filled with the narrative"
                | other -> failtestf "expected the narrative placeholder, got %A" other
            | Error reason -> failtestf "expected a render request, got %s" reason

            let request = seen |> Seq.exactlyOne
            Expect.equal request.Scope scope "under the job's resolved scope"
            Expect.equal request.Trigger GroundedNarrativeProducer.TriggerName "recorded as a scheduled run"
        }

        test "as a report producer: a refusal is the producer's error, naming the offence; no scope is an error too" {
            let offence = {
                Location = "section 'summary' › element 1 › span 1"
                Claim = "40 units"
                Reason = UnreferencedNumber
            }

            let producer =
                GroundedNarrativeProducer.create
                    "grounded-monthly"
                    "Monthly"
                    (stubRun (GroundedNarrativeRefused [ offence ]) (ConcurrentQueue()))
                    "monthly-brand-summary"
                    "monthly-template"
                    "body"

            let scope = ScopeResolution.ofStorageScope (storageFor "team-producer")

            match
                ReportProducerScope.within scope (producer.Resolve "team-producer" Map.empty)
                |> Async.RunSynchronously
            with
            | Error reason -> Expect.stringContains reason "40 units" "the refusal names the claim"
            | Ok _ -> failtest "a refused narrative must not render"

            match producer.Resolve "team-producer" Map.empty |> Async.RunSynchronously with
            | Error reason -> Expect.stringContains reason "resolved scope" "no carried scope, no run"
            | Ok _ -> failtest "a producer with no carried scope must not run"
        }

        test "on data arrival: a resolved write enqueues the runs that depend on the changed metric, end to end" {
            let deployment =
                deploy (fun id rendering -> answer $"Acme revenue was [[Revenue|{rendering}|{id}]] in September.")

            Expect.contains
                deployment.Scheduler.RegisteredHandlers
                GroundedNarrativeTrigger.HandlerName
                "the job handler is registered at boot when runs are composed"

            // A computed fact citing the data object the write will touch.
            let computed = {
                assertedDraft "revenue" 1250m Surfaceable with
                    Method = Computed("rollup", "1", "p0")
                    Evidence = {
                        ResultRef = None
                        InputHashes = [ "sales.rollup" ]
                        TriggerRef = None
                    }
            }

            assertOk deployment.Store deployment.TeamId computed |> ignore

            // The write is made while serving a request the platform resolved
            // a scope for — what the scope middleware leaves on the context.
            let accessor = deployment.Services.GetRequiredService<IHttpContextAccessor>()
            let ctx = DefaultHttpContext()
            ScopeResolution.remember ctx (storageFor deployment.TeamId) |> ignore
            accessor.HttpContext <- ctx

            let objects = deployment.Services.GetRequiredService<IDataObjectStore>()

            match
                objects.Save(
                    deployment.TeamId,
                    "sales.rollup",
                    System.Text.Encoding.UTF8.GetBytes "v2",
                    "rollup",
                    "tester",
                    Map.empty,
                    VersioningPolicy.Versioned
                )
                |> Async.RunSynchronously
            with
            | Ok _ -> ()
            | Error e -> failtestf "save failed: %A" e

            let narrativeJobs =
                deployment.Scheduler.Scheduled
                |> List.filter (fun (r, _) -> r.Handler = GroundedNarrativeTrigger.HandlerName)

            Expect.equal narrativeJobs.Length 1 "one narrative job for the one dependent run"
            Expect.isFalse (snd narrativeJobs.Head).IsAnonymous "scheduled under the write's resolved scope"
            Expect.contains deployment.Scheduler.Results Success "the job ran the narrative and succeeded"
            Expect.equal (published deployment).Length 1 "and the narrative was published"
        }

        test "on data arrival: a carried write, or a change no run depends on, enqueues nothing" {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore
            let scheduler = DispatchingScheduler()
            let teamId = "team-" + Guid.NewGuid().ToString "N"

            let computed = {
                assertedDraft "revenue" 1250m Surfaceable with
                    Method = Computed("rollup", "1", "p0")
                    Evidence = {
                        ResultRef = None
                        InputHashes = [ "sales.rollup" ]
                        TriggerRef = None
                    }
            }

            assertOk store teamId computed |> ignore

            let registryOf (definition: GroundedNarrativeDefinition) =
                let r = GroundedNarrativeRegistry()
                r.Register definition |> ignore
                r

            let enqueue registry scope =
                GroundedNarrativeTrigger.enqueueFor lineage store scheduler registry scope [ "sales.rollup" ]
                |> Async.RunSynchronously

            Expect.equal
                (enqueue
                    (registryOf summaryRun)
                    (DataChangeScope.Resolved(ScopeResolution.ofStorageScope (storageFor teamId))))
                [ summaryRun.Key ]
                "a resolved change to a depended-on metric enqueues the run"

            Expect.isEmpty
                (enqueue (registryOf summaryRun) (DataChangeScope.Carried teamId))
                "a carried change has no resolved scope to give the run, so enqueues nothing"

            Expect.isEmpty
                (enqueue
                    (registryOf (summaryRun |> GroundedNarrativeDefinition.dependingOn [ "cost" ]))
                    (DataChangeScope.Resolved(ScopeResolution.ofStorageScope (storageFor teamId))))
                "a run that does not depend on the changed metric is not enqueued"
        }

        test "the job waits out a raced Eager recompute, runs on its last attempt, and refuses an anonymous scope" {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let teamId = "team-" + Guid.NewGuid().ToString "N"

            let eager =
                MetricRegistry.build [
                    {
                        MetricRegistration.Module = "sales"
                        Definition = {
                            revenueDefinition with
                                RecomputePolicy = Some Eager
                        }
                    }
                ] []

            let computed = {
                assertedDraft "revenue" 1250m Surfaceable with
                    Method = Computed("rollup", "1", "p0")
                    Evidence = {
                        ResultRef = None
                        InputHashes = [ "sales.rollup" ]
                        TriggerRef = None
                    }
            }

            assertOk store teamId computed |> ignore

            let registry = GroundedNarrativeRegistry()
            registry.Register summaryRun |> ignore
            let seen = ConcurrentQueue<GroundedNarrativeRequest>()

            let handler =
                GroundedNarrativeTrigger.GroundedNarrativeJobHandler(
                    store,
                    Some eager,
                    registry,
                    stubRun (GroundedNarrativePublished(Guid.NewGuid(), documentFor "x", [], None, Error "-")) seen
                )
                :> IJobHandler

            let scope = ScopeResolution.ofStorageScope (storageFor teamId)

            let contextAt (attempt: int) (jobScope: ResolvedScope) : JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = teamId
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "_platform")
                Attempt = attempt
                Trigger = Trigger.Manual
                Scope = jobScope
                TriggerSource = ScheduledManually "_platform"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = GroundedNarrativeTrigger.payloadFor summaryRun.Key (set [ "sales.rollup" ])
                DeadLetterDestination = None
            }

            match handler.Execute(contextAt 1 scope) |> Async.RunSynchronously with
            | TransientFailure reason -> Expect.stringContains reason "recompute" "waiting for the raced recompute"
            | other -> failtestf "expected a transient wait, got %A" other

            Expect.isEmpty seen "and the run was not started"

            Expect.equal
                (handler.Execute(contextAt GroundedNarrativeTrigger.retryPolicy.MaxAttempts scope)
                 |> Async.RunSynchronously)
                Success
                "on the last attempt it runs regardless"

            Expect.equal (seen |> Seq.exactlyOne).Scope scope "under the job's resolved scope"

            match handler.Execute(contextAt 1 ResolvedScope.anonymous) |> Async.RunSynchronously with
            | PermanentFailure reason -> Expect.stringContains reason "resolved scope" "an anonymous job never runs"
            | other -> failtestf "expected a permanent failure, got %A" other
        }
    ]

// ── E. Who pays: the provider resolves under the scope the run carries ─
//
// Phase 995. A grounded run is a background job — nobody is signed in — so
// the provider used to be resolved as the run's own principal, which under a
// strict bring-your-own-key policy holds no key: every run failed, retried,
// and published nothing while the team's key sat unused. These cases run the
// REAL provider factory (`DefaultAIProviderFactory`) over a profile and a
// secret store, and the data-arrival job over the real run, with a job
// context that — like a scheduler's — names no team.

let private scriptedVendor = "scripted-vendor"

let private scriptedDescriptor: AIProviderDescriptor = {
    Id = scriptedVendor
    DisplayName = "Scripted vendor"
    SupportedModels = [ "scripted-narrative-model" ]
    DefaultModel = "scripted-narrative-model"
    Capabilities = {
        Streaming = false
        ToolUse = true
        Vision = false
        SupportsPromptCaching = false
        SupportsTriage = false
        TriageModelId = None
        ProviderName = "scripted-narrative"
        Model = "scripted-narrative-model"
    }
}

/// A factory that records the access context every resolve was asked under.
type private RecordingFactory(inner: IAIProviderFactory) =
    let seen = ConcurrentQueue<AccessContext>()
    member _.Seen = List.ofSeq seen

    interface IAIProviderFactory with
        member _.Available = inner.Available
        member _.PlatformDescriptors = inner.PlatformDescriptors
        member _.PlatformDescriptor = inner.PlatformDescriptor

        member _.Resolve access =
            seen.Enqueue access
            inner.Resolve access

        member _.TryResolveByLabel(access, label) = inner.TryResolveByLabel(access, label)

        member _.BuildPlatform(providerId, apiKey, model) =
            inner.BuildPlatform(providerId, apiKey, model)

/// The funding substrate a deployment's factory reads: the real factory
/// under `policy`, a provider profile and a secret store, and — when
/// `platformKey` is given — a platform provider the deployment pays for. The
/// builder records every key a provider was built with.
type private Funding(policy: AIFallbackPolicy, platformKey: string option) =
    let keys = ConcurrentQueue<string>()
    let profiles = BlobProviderProfile.create (InMemoryBlobStorage())
    let secrets = InMemorySecretStore() :> ISecretStore
    let mutable recording: RecordingFactory option = None

    /// The keys providers were built with, in order.
    member _.KeysUsed = List.ofSeq keys

    /// The access contexts the factory was asked to resolve under.
    member _.Resolutions = recording |> Option.map _.Seen |> Option.defaultValue []

    member _.FactoryFor(model: ScriptedModel) : IAIProviderFactory =
        let build (key: string) (_model: string) : IAIProvider =
            keys.Enqueue key
            model :> IAIProvider

        let platform =
            platformKey
            |> Option.map (fun key -> {
                DefaultAIProviderFactory.AIPlatformProvider.Descriptor = scriptedDescriptor
                DefaultAIProviderFactory.AIPlatformProvider.Build = build
                DefaultAIProviderFactory.AIPlatformProvider.BootstrapKeyFromEnv = Some key
            })
            |> Option.toList

        let builder: AIProviderBuilder = {
            Descriptor = scriptedDescriptor
            Build = build
        }

        let factory =
            RecordingFactory(DefaultAIProviderFactory.create [ builder ] profiles secrets policy platform None)

        recording <- Some factory
        factory :> IAIProviderFactory

    /// Store a team's own key the way the AI settings surface does: an
    /// entry routed for the assistant surface in the team's profile, its
    /// secret in the team's container.
    member _.StoreTeamKey(teamId: string, key: string) =
        let scope = storageFor teamId

        match
            secrets.SetSecret(scope.Container, "team-own-key", key)
            |> Async.RunSynchronously
        with
        | Ok() -> ()
        | Error e -> failtestf "could not store the team's key: %A" e

        let profile = {
            ProviderProfile.empty () with
                Entries = [ ProviderEntry.pastedKey "team" scriptedVendor None "team-own-key" ]
                Routing = [
                    {
                        Surface = AIProviderSurface.aiAssistant
                        Context = None
                        EntryLabel = "team"
                    }
                ]
        }

        match profiles.Set(scope, profile) |> Async.RunSynchronously with
        | Ok() -> ()
        | Error e -> failtestf "could not store the team's profile: %s" e

let private citingWriter: Writer =
    fun id rendering -> answer $"Acme revenue was [[Revenue|{rendering}|{id}]] in September."

/// The data-arrival job over the deployment's REAL run, under the team's
/// resolved scope and — as a scheduler's synthesised context may — an access
/// context that names no team.
let private narrativeJob (deployment: Deployment) : JobResult =
    let handler =
        GroundedNarrativeTrigger.GroundedNarrativeJobHandler(
            deployment.Store,
            Some metrics,
            deployment.Services.GetRequiredService<GroundedNarrativeRegistry>(),
            deployment.Services.GetRequiredService<IGroundedNarrativeRun>()
        )
        :> IJobHandler

    handler.Execute {
        JobId = Guid.NewGuid()
        ScopeId = deployment.TeamId
        AccessContext = AccessContext.unrestricted (AuthenticatedUser "_platform")
        Attempt = 1
        Trigger = Trigger.Manual
        Scope = deployment.Scope
        TriggerSource = ScheduledManually "_platform"
        ScheduledAt = DateTime.UtcNow
        RunningAt = DateTime.UtcNow
        Payload = GroundedNarrativeTrigger.payloadFor summaryRun.Key Set.empty
        DeadLetterDestination = None
    }
    |> Async.RunSynchronously

let private withRevenueFact (deployment: Deployment) =
    assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1250m Surfaceable)
    |> ignore

let scopedProviderTests =
    testList "Phase 995 E — the run's AI provider resolves under the scope it carries" [

        test "strict BYOK: the team's own key, stored at the team scope, funds the data-arrival run, which publishes" {
            let funding = Funding(StrictBYOK, None)
            let deployment = deployWith funding.FactoryFor citingWriter
            funding.StoreTeamKey(deployment.TeamId, "the-team-own-key")
            withRevenueFact deployment

            Expect.equal (narrativeJob deployment) Success "the job runs the narrative and succeeds"
            Expect.equal (published deployment).Length 1 "and the narrative is published"
            Expect.equal funding.KeysUsed [ "the-team-own-key" ] "funded by the team's own key, and only it"

            let access = funding.Resolutions |> Seq.exactlyOne
            Expect.equal access.TeamId (Some deployment.TeamId) "resolved as a member of the team the run belongs to"

            Expect.equal
                (AccessContext.configScope access)
                (Some(storageFor deployment.TeamId))
                "whose configuration scope is exactly the run's scope"

            Expect.equal access.UserId summaryRun.Principal "the run's own principal stays the actor"

            Expect.equal
                (narrativeRows deployment |> List.map _.EventType)
                [ GroundedNarrativeEvents.PublishedType ]
                "one published row on the audit trail"
        }

        test
            "strict BYOK with no key at the scope: a typed permanent failure, no retry, no model call, nothing published" {
            let funding = Funding(StrictBYOK, None)
            let deployment = deployWith funding.FactoryFor citingWriter
            // A key at ANOTHER team's scope must not fund this one.
            funding.StoreTeamKey("team-" + Guid.NewGuid().ToString "N", "another-team-key")
            withRevenueFact deployment

            match narrativeJob deployment with
            | PermanentFailure reason -> Expect.stringContains reason "unfunded" "the job record names the cause"
            | other -> failtestf "expected a permanent failure (no retries), got %A" other

            match run deployment with
            | GroundedNarrativeUnfunded reason ->
                Expect.stringContains reason "No AI provider configured" "the factory's own answer, typed"
            | other -> failtestf "expected an unfunded outcome, got %s" (GroundedNarrativeOutcome.describe other)

            Expect.isEmpty funding.KeysUsed "no provider was built, not even with the other team's key"
            Expect.isEmpty deployment.Model.OfferedTools "the model was never called"
            Expect.isEmpty (published deployment) "nothing was published"

            let rows = narrativeRows deployment

            Expect.equal
                (rows |> List.map _.EventType)
                [ GroundedNarrativeEvents.FailedType; GroundedNarrativeEvents.FailedType ]
                "each attempt is one failed row on the audit trail"

            for row in rows do
                Expect.stringContains row.Payload "unfunded" "whose outcome says unfunded, not failed"
        }

        test "platform-only: unchanged, the deployment's platform key funds the run" {
            let funding = Funding(PlatformOnly, Some "the-platform-key")
            let deployment = deployWith funding.FactoryFor citingWriter
            withRevenueFact deployment

            Expect.equal (narrativeJob deployment) Success "the job succeeds"
            Expect.equal (published deployment).Length 1 "and publishes"
            Expect.equal funding.KeysUsed [ "the-platform-key" ] "on the platform's key"
        }

        test "an access context the request names still wins over the carried scope" {
            let funding = Funding(StrictBYOK, None)
            let deployment = deployWith funding.FactoryFor citingWriter
            funding.StoreTeamKey(deployment.TeamId, "the-team-own-key")
            withRevenueFact deployment
            let named = AccessContext.unrestricted (AuthenticatedUser "someone-else")

            let outcome =
                deployment.Services
                    .GetRequiredService<IGroundedNarrativeRun>()
                    .Run(
                        {
                            RunKey = summaryRun.Key
                            Scope = deployment.Scope
                            Access = Some named
                            Trigger = "test"
                        }
                    )
                |> Async.RunSynchronously

            Expect.equal funding.Resolutions [ named ] "resolved under the named context"

            match outcome with
            | GroundedNarrativeUnfunded _ -> ()
            | other ->
                failtestf
                    "a named principal with no key of its own is unfunded, got %s"
                    (GroundedNarrativeOutcome.describe other)
        }

        test "a report producer reports an unfunded run as its error" {
            let producer =
                GroundedNarrativeProducer.create
                    "grounded-monthly"
                    "Monthly"
                    (stubRun (GroundedNarrativeUnfunded "no key") (ConcurrentQueue()))
                    "monthly-brand-summary"
                    "monthly-template"
                    "body"

            match
                ReportProducerScope.within
                    (ScopeResolution.ofStorageScope (storageFor "team-producer"))
                    (producer.Resolve "team-producer" Map.empty)
                |> Async.RunSynchronously
            with
            | Error reason -> Expect.stringContains reason "unfunded" "named as unfunded"
            | Ok _ -> failtest "an unfunded narrative must not render"
        }

        test "AccessContext.forConfigScope inverts configScope exactly, and refuses to guess" {
            let team = storageFor "acme"

            let user = {
                ScopeId = "u-1"
                Container = "user-u-1"
                Persist = true
            }

            for scope in [ team; user ] do
                Expect.equal
                    (AccessContext.forConfigScope "narrator" scope
                     |> Option.bind AccessContext.configScope)
                    (Some scope)
                    $"round trip for {scope.Container}"

            let unprojectable = [
                {
                    ScopeId = "claim-1"
                    Container = "claim-1"
                    Persist = true
                }
                {
                    ScopeId = "s-1"
                    Container = "session-s-1"
                    Persist = false
                }
                // A team container that does not name the scope's own id.
                {
                    ScopeId = "acme"
                    Container = "team-other"
                    Persist = true
                }
            ]

            for scope in unprojectable do
                Expect.isNone
                    (AccessContext.forConfigScope "narrator" scope)
                    $"no subject guessed for {scope.Container}"
        }
    ]

// ── F. The run's call budget (Phase 1109) ─────────────────────────

/// Wraps a model so its final reply "streams" for `streamFor` of FAKE time:
/// under a per-call timeout shorter than that, the reply is cut mid-stream
/// exactly as a connector's per-call timeout cuts one — partial content,
/// `StreamingAborted` — and otherwise it arrives whole. A timeout of a few
/// seconds is waited out for real, so the loop measures the call reaching
/// its budget as it would against a live connector. Records the timeout
/// and the reply-length budget every call was sent with.
type private StreamingFor(inner: IAIProvider, streamFor: TimeSpan) =
    let timeouts = ConcurrentQueue<TimeSpan option>()
    let maxTokens = ConcurrentQueue<int option>()

    member _.Timeouts = List.ofSeq timeouts
    member _.MaxTokens = List.ofSeq maxTokens

    member _.Send
        (
            messages,
            tools,
            systemPrompt,
            onStream: (string -> unit) option,
            retryPolicy: ToolUp.Platform.Transport.RetryPolicy
        ) =
        async {
            timeouts.Enqueue retryPolicy.Timeout
            let! result = inner.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)

            match result, retryPolicy.Timeout with
            | Ok reply, Some timeout when List.isEmpty reply.ToolCalls && timeout < streamFor ->
                if timeout <= TimeSpan.FromSeconds 5.0 then
                    do! Async.Sleep(int timeout.TotalMilliseconds + 50)

                let partial = reply.Content.Substring(0, min 12 reply.Content.Length)
                onStream |> Option.iter (fun stream -> stream partial)

                return
                    Error(
                        StreamingAborted(
                            partial,
                            $"Timed out after {ToolUp.Platform.Transport.RetryPolicy.timeoutDescription retryPolicy} with partial content delivered"
                        )
                    )
            | _ -> return result
        }

    interface IAIProvider with
        member _.Capabilities = inner.Capabilities

        member this.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) =
            this.Send(messages, tools, systemPrompt, onStream, retryPolicy)

        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)

    interface IAIProviderModelOverride with
        member this.SendMessageWith(options, messages, tools, systemPrompt, onStream, retryPolicy) =
            maxTokens.Enqueue options.MaxTokens

            this.Send(messages, tools, systemPrompt, onStream, retryPolicy)
            |> AIProviderCallResponse.attach (ConfiguredModel inner.Capabilities.Model)

        member _.SendStructuredMessageWith(_options, messages, tools, systemPrompt, schema, retryPolicy) =
            inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)
            |> AIProviderCallResponse.attach (ConfiguredModel inner.Capabilities.Model)

/// A deployment whose model's report streams for `streamFor` of fake time.
let private deployStreaming (options: GroundedNarrativeRunOptions) (streamFor: TimeSpan) =
    let mutable streaming = None

    let factoryFor (model: ScriptedModel) =
        let provider = StreamingFor(model, streamFor)
        streaming <- Some provider
        ScriptedFactory(provider) :> IAIProviderFactory

    let deployment = deployComposed options factoryFor citingWriter
    deployment, streaming.Value

/// A model that answers at once with `content`, no tools.
let private replyOnly (content: string) =
    { new IAIProvider with
        member _.Capabilities = {
            Streaming = true
            ToolUse = false
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "scripted-reply"
            Model = "scripted-reply-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            return
                Ok {
                    Content = content
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback this messages tools systemPrompt schema retryPolicy
    }

let private seventySeconds = TimeSpan.FromSeconds 70.0

let budgetTests =
    testList "Phase 1109 F — a background run has its own call budget" [

        test "a report that streams for 70 s of fake time completes under the run's default budget" {
            let deployment, model =
                deployStreaming (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools) seventySeconds

            withRevenueFact deployment

            match run deployment with
            | GroundedNarrativePublished _ -> ()
            | other -> failtestf "expected the narrative published, got %s" (GroundedNarrativeOutcome.describe other)

            Expect.isNonEmpty model.Timeouts "the model was called"

            Expect.allEqual
                model.Timeouts
                (Some(TimeSpan.FromSeconds 300.0))
                "every call ran under the run's 300 s budget, not the chat's 50 s"

            Expect.equal
                GroundedNarrativeRunOptions.defaultCallBudget.CallTimeout
                (TimeSpan.FromSeconds 300.0)
                "the stated default"
        }

        test "the reply-length budget reaches the provider call" {
            let deployment, model =
                deployStreaming (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools) TimeSpan.Zero

            withRevenueFact deployment
            run deployment |> ignore

            Expect.isNonEmpty model.MaxTokens "the calls carried per-call options"
            Expect.allEqual model.MaxTokens (Some 16_384) "every call carried the run's 16,384-token reply budget"

            Expect.equal
                (ClaudeAIProvider.resolveCallMaxTokens
                    ClaudeAIProvider.DefaultMaxTokens
                    (AIProviderCallOptions.withMaxTokens 16_384 AIProviderCallOptions.none))
                16_384
                "and the Anthropic connector sends that budget as the call's reply cap"

            Expect.equal
                (ClaudeAIProvider.resolveCallMaxTokens ClaudeAIProvider.DefaultMaxTokens AIProviderCallOptions.none)
                ClaudeAIProvider.DefaultMaxTokens
                "a call naming no budget keeps the connector's own cap"

            Expect.equal
                (ClaudeAIProvider.resolveCallMaxTokens
                    ClaudeAIProvider.DefaultMaxTokens
                    (AIProviderCallOptions.withMaxTokens 0 AIProviderCallOptions.none))
                ClaudeAIProvider.DefaultMaxTokens
                "and a budget below 1 is ignored"
        }

        test "the interactive loop still stops a call at 50 s, with the provider's own error" {
            let model =
                StreamingFor(replyOnly "A reply that takes seventy seconds to write.", seventySeconds)

            let services = ServiceCollection()
            services.AddSingleton<ILogger>(silentLogger) |> ignore
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- services.BuildServiceProvider()
            let messages = [ AIProviderMessage.text "user" "hello" ]

            let outcome =
                AIAgentEngine.runAgentLoopWithInput
                    model
                    (AIToolRegistry.AIToolRegistry())
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    ctx
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    CancellationToken.None
                    messages
                    ModelInput.empty
                    ignore
                |> Async.Catch
                |> Async.RunSynchronously

            Expect.equal model.Timeouts [ Some(TimeSpan.FromSeconds 50.0) ] "one call, under the chat's 50 s"
            Expect.isEmpty model.MaxTokens "sent exactly as before: no per-call options"

            match outcome with
            | Choice1Of2 _ -> failtest "a reply cut at 50 s must fail the conversation"
            | Choice2Of2(:? AIAgentEngine.AgentCallBudgetExceeded) ->
                failtest "the interactive budget names no remedy, so its error stays the provider's"
            | Choice2Of2 ex -> Expect.stringContains ex.Message "Timed out after 50000 ms" "the provider's own error"
        }

        test
            "a reply still streaming at the run's budget: over budget, permanent, naming the budget and how to raise it" {
            let budget: AIAgentEngine.AgentCallBudget = {
                CallTimeout = TimeSpan.FromSeconds 1.0
                MaxTokens = Some 2048
                RaiseWith = None
            }

            let options =
                GroundedNarrativeRunOptions.create NarrativeGrounding.factTools
                |> GroundedNarrativeRunOptions.withCallBudget budget

            let deployment, _ = deployStreaming options seventySeconds
            withRevenueFact deployment

            match run deployment with
            | GroundedNarrativeOverBudget reason ->
                Expect.stringContains reason "a call budget of 1 s and a reply of up to 2048 tokens" "names the budget"
                Expect.stringContains reason "GroundedNarrativeRunOptions.withCallBudget" "and how to raise it"
                Expect.stringContains reason "Timed out after 1000 ms" "carrying the provider's account"
            | other -> failtestf "expected over budget, got %s" (GroundedNarrativeOutcome.describe other)

            Expect.isEmpty (published deployment) "nothing was published"

            let row = narrativeRows deployment |> List.exactlyOne
            Expect.equal row.EventType GroundedNarrativeEvents.FailedType "one failed row on the audit trail"
            Expect.stringContains row.Payload "over-budget" "whose outcome says why"

            match narrativeJob deployment with
            | PermanentFailure reason ->
                Expect.stringContains reason "over budget" "the job is not retried into the same cap"
            | other -> failtestf "expected a permanent failure, got %A" other
        }

        test "settling the retry question: a transient grounded-run failure IS retried by the in-process scheduler" {
            // Attempt 1's model answer is not the narrative format (a
            // `GroundedNarrativeFailed`, so the job's `TransientFailure`);
            // attempt 2 writes the grounded narrative.
            let mutable answers = 0

            let flaky: Writer =
                fun id rendering ->
                    answers <- answers + 1

                    if answers = 1 then
                        "not a narrative"
                    else
                        citingWriter id rendering

            let deployment = deploy flaky
            withRevenueFact deployment

            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let jobStore = JobStore.create (InMemoryBlobStorage() :> IBlobStorage) events

            use scheduler =
                new JobScheduler.InProcessJobScheduler(
                    jobStore,
                    events,
                    { new INotificationChannel with
                        member _.Publish(_, _) = async { return () }
                        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
                        member _.Unsubscribe(_) = async { return () }
                    },
                    ServerConfig.defaults,
                    silentLogger,
                    ToolUp.Platform.Tracing.NoOpActivitySink() :> ToolUp.Platform.Tracing.IActivitySink
                )

            let jobs = scheduler :> IJobScheduler

            jobs.RegisterHandler(
                GroundedNarrativeTrigger.HandlerName,
                GroundedNarrativeTrigger.GroundedNarrativeJobHandler(
                    deployment.Store,
                    Some metrics,
                    deployment.Services.GetRequiredService<GroundedNarrativeRegistry>(),
                    deployment.Services.GetRequiredService<IGroundedNarrativeRun>()
                )
            )

            // The data-arrival registration, its backoff shortened from 30 s
            // so the test reads the history after the first backoff in
            // milliseconds; attempts, shape and dispatch path are the job's.
            let registration = {
                GroundedNarrativeTrigger.registrationFor deployment.TeamId summaryRun.Key Set.empty with
                    RetryPolicy = {
                        GroundedNarrativeTrigger.retryPolicy with
                            InitialBackoff = TimeSpan.FromMilliseconds 100.0
                    }
            }

            let jobId =
                match jobs.Schedule(deployment.Scope, registration) |> Async.RunSynchronously with
                | Ok id -> id
                | Error e -> failtestf "schedule refused: %A" e

            match
                jobs.TriggerOnce(deployment.TeamId, jobId, "_platform")
                |> Async.RunSynchronously
            with
            | Ok() -> ()
            | Error e -> failtestf "trigger refused: %s" e

            let runs () =
                jobs.GetRecentRuns(deployment.TeamId, jobId, 20) |> Async.RunSynchronously

            let deadline = DateTime.UtcNow.AddSeconds 30.0

            while DateTime.UtcNow < deadline
                  && not (runs () |> List.exists (fun r -> r.Status = JobRunStatus.Succeeded)) do
                Thread.Sleep 100

            let history =
                runs ()
                |> List.filter (fun r -> r.Status <> JobRunStatus.Running)
                |> List.map (fun r -> r.Attempt, r.Status)
                |> List.sort

            Expect.equal
                history
                [ 1, JobRunStatus.Failed; 2, JobRunStatus.Succeeded ]
                "attempt 1 failed transiently and attempt 2 ran after the backoff, and published"

            Expect.equal (published deployment).Length 1 "the retried attempt published the narrative"
        }
    ]

// ── G. The wording is the admin's, the grounding rules are not (Phase 1111) ──

/// Marker a wording carries to have `PromptRecording`'s model obey it.
let private plainNumbersMarker = "WRITE PLAIN NUMBERS, NO CITATIONS"

/// A model that records the instruction of every call, and — like a real
/// model — follows a wording that tells it to drop citations: it then
/// writes the figure as a plain number.
type private PromptRecording(inner: IAIProvider) =
    let prompts = ConcurrentQueue<string>()

    member _.Prompts = List.ofSeq prompts |> List.distinct

    interface IAIProvider with
        member _.Capabilities = inner.Capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            systemPrompt |> Option.iter prompts.Enqueue
            let! result = inner.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)

            match result, systemPrompt with
            | Ok reply, Some prompt when List.isEmpty reply.ToolCalls && prompt.Contains plainNumbersMarker ->
                return
                    Ok {
                        reply with
                            Content = answer "Acme revenue was 1250 in September."
                    }
            | _ -> return result
        }

        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)

/// A deployment whose model records its instructions, with a revenue fact
/// in the deployment's team, and that team in a team store with an owner
/// (`olive`) and a member (`mo`).
let private deployRecording () =
    let mutable recording = None

    let factoryFor (model: ScriptedModel) =
        let provider = PromptRecording(model)
        recording <- Some provider
        ScriptedFactory(provider) :> IAIProviderFactory

    let deployment = deployWith factoryFor citingWriter
    withRevenueFact deployment

    let teams =
        TeamManagement.TeamStore(
            InMemoryBlobStorage() :> IBlobStorage,
            NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel,
            silentLogger
        )
        :> TeamManagement.ITeamStore

    async {
        let! _ = teams.CreateTeam(deployment.TeamId, "Team")
        let! _ = teams.AddMember(deployment.TeamId, "olive", TeamRole.Owner)
        let! _ = teams.AddMember(deployment.TeamId, "mo", TeamRole.Member)
        return ()
    }
    |> Async.RunSynchronously

    deployment, recording.Value, teams

/// The wording admin API as `userId`, a member of `teamId`, on a request the
/// platform resolved to `scope`. Audit rows land in `audit`.
let private promptApi
    (deployment: Deployment)
    (teams: TeamManagement.ITeamStore)
    (audit: ConcurrentQueue<string * AuditEvent>)
    (userId: string)
    (teamId: string)
    (scope: ResolvedScope)
    : NarrativePromptApi =
    let services = ServiceCollection()

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (TeamMember(userId, teamId)))
    |> ignore

    services.AddSingleton<TeamManagement.ITeamStore>(teams) |> ignore

    services.AddSingleton<IAuditLog>(
        { new IAuditLog with
            member _.Record(stream, event) = async { audit.Enqueue((stream, event)) }
            member _.GetAuditTrail(_, _, _) = async { return [] }
        }
    )
    |> ignore

    services.AddSingleton<IBlobStorage>(deployment.Services.GetRequiredService<IBlobStorage>())
    |> ignore

    services.AddSingleton<GroundedNarrativeRegistry>(
        deployment.Services.GetRequiredService<GroundedNarrativeRegistry>()
    )
    |> ignore

    services.AddSingleton<IEventStore>(deployment.Events) |> ignore
    services.AddSingleton<IJobScheduler>(deployment.Scheduler) |> ignore
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ScopeResolution.carry ctx scope
    NarrativePromptHandler.narrativePromptApi ctx

let private ownerApi (deployment: Deployment) teams audit =
    promptApi deployment teams audit "olive" deployment.TeamId deployment.Scope

let private toolNames =
    NarrativeGrounding.factTools |> List.map (fun (d, _) -> d.Name)

let private reworded: SaveNarrativePromptRequest = {
    RunKey = summaryRun.Key
    Prompt = "Explain the month's brand revenue to the board, plainly."
    Guidance = [
        {
            SectionId = "summary"
            Guidance = "Two sentences, the headline figure first."
        }
    ]
    BasedOnVersion = 0
}

let private ok (what: string) (result: Async<Result<'T, string>>) : 'T =
    match Async.RunSynchronously result with
    | Ok value -> value
    | Error reason -> failtestf "%s was refused: %s" what reason

let private lastRow (deployment: Deployment) : JsonElement =
    let row =
        narrativeRows deployment |> List.sortByDescending _.OccurredAt |> List.head

    (JsonDocument.Parse row.Payload).RootElement

let wordingTests =
    testList "Phase 1111 G — a report's wording is edited in the platform; the grounding rules are not" [

        test "an override changes the instruction the model receives — its wording lines and nothing else" {
            let deployment, recording, teams = deployRecording ()
            let api = ownerApi deployment teams (ConcurrentQueue())

            match run deployment with
            | GroundedNarrativePublished _ -> ()
            | other -> failtestf "the declared run must publish, got %s" (GroundedNarrativeOutcome.describe other)

            let declared = systemPrompt summaryRun toolNames
            Expect.equal recording.Prompts [ declared ] "the declared run's instruction is the declaration's"

            let view = api.SavePrompt reworded |> ok "the save"
            Expect.equal (view.Current |> Option.map _.Version) (Some 1) "the save is version 1 and in force"

            let document =
                match run deployment with
                | GroundedNarrativePublished(_, document, _, _, _) -> document
                | other -> failtestf "the reworded run must publish, got %s" (GroundedNarrativeOutcome.describe other)

            let overridden = recording.Prompts |> List.last

            let expected =
                match
                    GroundedNarrativeDefinition.reword
                        reworded.Prompt
                        [ "summary", reworded.Guidance.Head.Guidance ]
                        summaryRun
                with
                | Ok definition -> systemPrompt definition toolNames
                | Error reason -> failtestf "reword refused: %s" reason

            Expect.equal overridden expected "the model received the reworded instruction"

            let lines (s: string) =
                s.Split([| "\r\n"; "\n" |], StringSplitOptions.None) |> List.ofArray

            let before, after = lines declared, lines overridden
            Expect.equal after.Length before.Length "no line was added or removed"

            let changed =
                List.zip before after |> List.filter (fun (b, a) -> b <> a) |> List.map snd

            Expect.equal
                changed
                [
                    sprintf "Purpose: %s" reworded.Prompt
                    sprintf "- summary (Summary): %s" reworded.Guidance.Head.Guidance
                ]
                "exactly the purpose line and the section's guidance line changed"

            Expect.isTrue
                (overridden.TrimEnd().EndsWith((String.concat Environment.NewLine rules).TrimEnd()))
                "the grounding rules close the instruction unchanged"

            Expect.equal document.Title summaryRun.Title "the title is the declaration's"

            Expect.equal
                (document.Sections |> List.map (fun s -> s.Id, s.Heading))
                [ "summary", "Summary" ]
                "the section structure is the declaration's"
        }

        test "an override naming a section the narrative does not declare is refused by name, and nothing is saved" {
            let deployment, _, teams = deployRecording ()
            let audit = ConcurrentQueue()
            let api = ownerApi deployment teams audit

            let refused =
                api.SavePrompt {
                    reworded with
                        Guidance = [
                            {
                                SectionId = "outlook"
                                Guidance = "Forecast next month."
                            }
                        ]
                }
                |> Async.RunSynchronously

            match refused with
            | Error reason -> Expect.stringContains reason "'outlook'" "the refusal names the undeclared section"
            | Ok _ -> failtest "an undeclared section must be refused"

            Expect.isEmpty (api.ListVersions summaryRun.Key |> ok "the version list") "nothing was saved"
            Expect.isEmpty audit "and nothing was audited"
        }

        test
            "a reset restores the declaration — the instruction, the view and the history all say so; both acts are audited" {
            let deployment, recording, teams = deployRecording ()
            let audit = ConcurrentQueue()
            let api = ownerApi deployment teams audit

            api.SavePrompt reworded |> ok "the save" |> ignore
            let view = api.ResetPrompt summaryRun.Key |> ok "the reset"
            Expect.isNone view.Current "the declaration is in force"
            Expect.equal view.LatestVersion 2 "the reset is version 2"

            match run deployment with
            | GroundedNarrativePublished _ -> ()
            | other -> failtestf "expected publication, got %s" (GroundedNarrativeOutcome.describe other)

            Expect.equal
                (recording.Prompts |> List.last)
                (systemPrompt summaryRun toolNames)
                "after the reset the model receives the declared instruction"

            Expect.equal
                ((lastRow deployment).GetProperty("PromptVersion").GetInt32())
                0
                "and the run records the declaration"

            let history = api.ListVersions summaryRun.Key |> ok "the version list"

            Expect.equal
                (history |> List.map (fun v -> v.Version, v.IsReset, v.Author))
                [ 2, true, "olive"; 1, false, "olive" ]
                "both versions are kept, newest first, with their author"

            Expect.equal
                (audit
                 |> List.ofSeq
                 |> List.map (fun (stream, event) ->
                     match event with
                     | RemotingMethodAudited p -> stream, p.Kind, p.Payload["version"]
                     | other -> failtestf "unexpected audit event %A" other))
                [
                    deployment.TeamId, NarrativePromptHandler.NarrativePromptAudit.SavedKind, "1"
                    deployment.TeamId, NarrativePromptHandler.NarrativePromptAudit.ResetKind, "2"
                ]
                "the save and the reset are audited under the team"

            let again = api.ResetPrompt summaryRun.Key |> ok "a second reset"
            Expect.equal again.LatestVersion 2 "resetting the declaration again records nothing"
        }

        test "the published narrative's audit row names the prompt version that produced it" {
            let deployment, _, teams = deployRecording ()
            let api = ownerApi deployment teams (ConcurrentQueue())
            api.SavePrompt reworded |> ok "the save" |> ignore

            match run deployment with
            | GroundedNarrativePublished _ -> ()
            | other -> failtestf "expected publication, got %s" (GroundedNarrativeOutcome.describe other)

            let row = lastRow deployment
            Expect.equal (row.GetProperty("Outcome").GetString()) "published" "a publication row"
            Expect.equal (row.GetProperty("PromptVersion").GetInt32()) 1 "naming version 1"

            let view = api.GetNarrative summaryRun.Key |> ok "the narrative"

            Expect.equal
                (view.LastRun |> Option.map (fun r -> r.Outcome, r.PromptVersion))
                (Some("published", 1))
                "the narrative's page shows the run and its version"
        }

        test "a wording that tells the model to drop citations still meets the gate, which refuses the report" {
            let deployment, recording, teams = deployRecording ()
            let api = ownerApi deployment teams (ConcurrentQueue())

            api.SavePrompt {
                reworded with
                    Prompt = sprintf "Summarise revenue. %s." plainNumbersMarker
                    BasedOnVersion = 0
            }
            |> ok "the save"
            |> ignore

            match run deployment with
            | GroundedNarrativeRefused offences ->
                Expect.contains (offences |> List.map _.Reason) UnreferencedNumber "refused for the uncited figure"
            | other -> failtestf "the gate must refuse, got %s" (GroundedNarrativeOutcome.describe other)

            Expect.stringContains (recording.Prompts |> List.last) plainNumbersMarker "the model was given that wording"
            Expect.isEmpty (published deployment) "and nothing was published"

            let row = lastRow deployment
            Expect.equal (row.GetProperty("Outcome").GetString()) "refused" "the refusal is on the trail"
            Expect.equal (row.GetProperty("PromptVersion").GetInt32()) 1 "naming the wording that produced it"
        }

        test "one team's override does not reach another team's run" {
            let deployment, recording, teams = deployRecording ()
            let api = ownerApi deployment teams (ConcurrentQueue())
            api.SavePrompt reworded |> ok "the save" |> ignore

            let otherTeam = "team-" + Guid.NewGuid().ToString "N"
            let otherScope = ScopeResolution.ofStorageScope (storageFor otherTeam)

            assertOk deployment.Store otherTeam (assertedDraft "revenue" 1250m Surfaceable)
            |> ignore

            match runIn deployment otherScope with
            | GroundedNarrativePublished _ -> ()
            | other -> failtestf "expected publication, got %s" (GroundedNarrativeOutcome.describe other)

            Expect.equal
                (recording.Prompts |> List.last)
                (systemPrompt summaryRun toolNames)
                "the other team's run receives the declaration"

            let otherRow =
                deployment.Events.ReadBySource(otherTeam, GroundedNarrativeEvents.SourceModule)
                |> Async.RunSynchronously
                |> List.head

            Expect.equal
                ((JsonDocument.Parse otherRow.Payload).RootElement.GetProperty("PromptVersion").GetInt32())
                0
                "and records the declaration"
        }

        test "only an owner or admin of the team, on its own resolved scope, edits the wording" {
            let deployment, _, teams = deployRecording ()
            let audit = ConcurrentQueue()

            let asMember =
                promptApi deployment teams audit "mo" deployment.TeamId deployment.Scope

            match asMember.SavePrompt reworded |> Async.RunSynchronously with
            | Error reason -> Expect.stringContains reason "owners and admins" "a member is refused"
            | Ok _ -> failtest "a member must not save"

            let elsewhere =
                promptApi
                    deployment
                    teams
                    audit
                    "olive"
                    deployment.TeamId
                    (ScopeResolution.ofStorageScope (storageFor "team-elsewhere"))

            match elsewhere.SavePrompt reworded |> Async.RunSynchronously with
            | Error _ -> ()
            | Ok _ -> failtest "a request resolved to another scope must not save into it"

            Expect.isEmpty audit "nothing was audited"

            Expect.isEmpty
                ((ownerApi deployment teams audit).ListVersions summaryRun.Key
                 |> ok "the version list")
                "nothing was saved"
        }

        test "a save over a version saved since is refused, naming it" {
            let deployment, _, teams = deployRecording ()
            let api = ownerApi deployment teams (ConcurrentQueue())
            api.SavePrompt reworded |> ok "the first save" |> ignore

            match api.SavePrompt reworded |> Async.RunSynchronously with
            | Error reason ->
                Expect.stringContains reason "Version 1 was saved by olive" "the stale save names version 1"
            | Ok _ -> failtest "a save based on version 0 after version 1 must be refused"
        }

        test "regenerate runs the narrative through the scheduler's manual trigger, with the wording in force" {
            let deployment, recording, teams = deployRecording ()
            let api = ownerApi deployment teams (ConcurrentQueue())
            api.SavePrompt reworded |> ok "the save" |> ignore

            Expect.contains
                deployment.Scheduler.RegisteredHandlers
                NarrativePromptHandler.NarrativeRegenerateJob.HandlerName
                "the regenerate handler is registered at boot"

            api.Regenerate summaryRun.Key |> ok "the regeneration" |> ignore

            Expect.equal deployment.Scheduler.Results [ Success ] "the job ran and the narrative published"
            Expect.equal (published deployment).Length 1 "one narrative published"

            let registration, scope = deployment.Scheduler.Scheduled |> List.last
            Expect.equal scope.ScopeId deployment.TeamId "under the team's resolved scope"
            Expect.equal registration.Trigger Trigger.Manual "as a manual job"

            Expect.stringContains (recording.Prompts |> List.last) reworded.Prompt "with the saved wording"

            let view = api.GetNarrative summaryRun.Key |> ok "the narrative"

            Expect.equal
                (view.LastRun |> Option.map (fun r -> r.Outcome, r.Trigger, r.PromptVersion))
                (Some("published", NarrativePromptHandler.NarrativeRegenerateJob.TriggerName, 1))
                "the page shows the regenerated run and its version"
        }
    ]

// ─── The double run's first site, pinned pending ────────────────────
//
// `GroundedNarrativeTrigger.enqueueFor` schedules the run's job and then
// triggers the job it got back. A scheduler that coalesces on the
// idempotency key hands back the job already pending, so a change replayed
// inside the TTL triggers that one job twice. Pending until the scheduler
// says which job it returned (`ScheduleOutcome.Coalesced`, Phase 1066);
// Phase 998 fixes both sites and turns this on. The second site,
// `RecomputeJobHandler.applyPolicies`, is pinned in
// ReactiveRecomputeComposeTests.

/// Coalesces on the idempotency key, as a real scheduler does, and counts
/// the triggers its jobs receive.
type private CoalescingScheduler() =
    let byKey = ConcurrentDictionary<string, JobId>()
    let triggers = ConcurrentDictionary<JobId, int>()

    member _.Jobs = byKey.Count
    member _.AllTriggers = triggers.Values |> Seq.sum

    member private _.ScheduleKeyed(registration: JobRegistration) =
        let key =
            registration.Idempotency
            |> Option.map _.Key
            |> Option.defaultWith (fun () -> Guid.NewGuid().ToString "N")

        Ok(byKey.GetOrAdd(key, fun _ -> Guid.NewGuid()))

    interface IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async { return Ok() }

        member this.Schedule(registration: JobRegistration) = async { return this.ScheduleKeyed registration }

        member this.Schedule(_: ResolvedScope, registration: JobRegistration) = async {
            return this.ScheduleKeyed registration
        }

        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs _ = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_, jobId, _) = async {
            triggers.AddOrUpdate(jobId, 1, fun _ n -> n + 1) |> ignore
            return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

let doubleRunTests =
    testList "double run, first site" [

        ptestCaseAsync
            "998: a replayed change coalesced onto its pending narrative job is not triggered again (GroundedNarrativeTrigger.enqueueFor)"
        <| async {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore
            let scheduler = CoalescingScheduler()
            let teamId = "team-" + Guid.NewGuid().ToString "N"

            let computed = {
                assertedDraft "revenue" 1250m Surfaceable with
                    Method = Computed("rollup", "1", "p0")
                    Evidence = {
                        ResultRef = None
                        InputHashes = [ "sales.rollup" ]
                        TriggerRef = None
                    }
            }

            assertOk store teamId computed |> ignore
            let registry = GroundedNarrativeRegistry()
            registry.Register summaryRun |> ignore

            let scope =
                DataChangeScope.Resolved(ScopeResolution.ofStorageScope (storageFor teamId))

            for _ in 1..2 do
                let! _ = GroundedNarrativeTrigger.enqueueFor lineage store scheduler registry scope [ "sales.rollup" ]
                ()

            Expect.equal scheduler.Jobs 1 "the replayed change coalesced onto the pending job"
            Expect.equal scheduler.AllTriggers 1 "and that job was triggered once, not once per schedule call"
        }
    ]

let tests =
    testList "Phase 985 — grounded narrative generation" [
        pureGateTests
        factGateTests
        runTests
        triggerTests
        scopedProviderTests
        budgetTests
        wordingTests
        doubleRunTests
    ]
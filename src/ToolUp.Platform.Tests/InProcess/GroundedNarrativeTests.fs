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
open ToolUp.Facts.Presentation
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
    match store.Assert(TestScope.resolved scopeId, draft) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" (FactStoreError.describe e)

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
    fixture.Gate.Check(TestScope.resolved scopeId, "reader", FactNarrativePublication, docOf elements)
    |> Async.RunSynchronously

/// The sealed certificate a narrative certificate carries, read back from its
/// interchange JSON.
let private sealedOf (certificate: NarrativeGroundingCertificate) : GroundingCertificate =
    JsonSerializer.Deserialize<GroundingCertificate>(certificate.CertificateJson, FableConverters.create ())

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
                fixture.Gate.Certify(TestScope.resolved scope, "reader", narrativeId, [ fact.FactId ])
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
        // Phase 1136 — the brand table's second, internal column.
        {
            MetricRegistration.Module = "sales"
            Definition = {
                revenueDefinition with
                    Id = "cost"
                    Name = "cost"
            }
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
            return Ok(Scheduled id)
        }

        member _.Schedule(registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            jobs[id] <- (registration, ResolvedScope.anonymous)
            scheduled.Enqueue((registration, ResolvedScope.anonymous))
            return Ok(Scheduled id)
        }

        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs _ = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(scopeId, jobId, byUserId) = async {
            match jobs.TryGetValue jobId with
            | false, _ -> return Error(TriggerError.DispatchRefused "unknown job")
            | true, (registration, scope) ->
                match handlers.TryGetValue registration.Handler with
                | false, _ ->
                    return
                        Error(TriggerError.DispatchRefused(sprintf "no handler registered for %s" registration.Handler))
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
    // The refusal suites below pin what the gate refuses, so they run the
    // run as it was before the repair loop: no repair, refuse on the first
    // offence. The repair loop's own suite composes the default policy.
    |> GroundedNarrativeDefinition.withRepairPolicy GroundedRepairPolicy.refuseOnOffence

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
let private deployShaped
    (definition: GroundedNarrativeDefinition)
    (tables: FactTableDefinition list)
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
        |> GroundedNarratives.compose options [ definition ]
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
        FactTableRegistry.build
            (tables
             |> List.map (fun t -> {
                 FactTableRegistration.Module = "sales"
                 Definition = t
             }))
            [ BindAllFactTables DefaultFactTableWriter.Destination ]
    )
    |> ignore

    services.AddSingleton<IArtefactSigner>(
        DefaultArtefactSigner.createSystem secrets (AuditLog.NoOpAuditLog() :> IAuditLog) "grounding-v1" Ed25519
    )
    |> ignore

    services.AddSingleton<IDataObjectStore>(DataObjectStore.DataObjectStore(storage, silentLogger) :> IDataObjectStore)
    |> ignore

    let services =
        match ServerApp.serviceConfig app with
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

let private deployComposed options factoryFor write =
    deployShaped summaryRun [] options factoryFor write

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
            | GroundedNarrativePublished(narrativeId, document, citations, indexed, certificate, _) ->
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
                    (stubRun
                        (GroundedNarrativePublished(
                            Guid.NewGuid(),
                            doc,
                            [],
                            None,
                            Error "no signer",
                            GroundedRepairSummary.complete 0
                        ))
                        seen)
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

        test "on data arrival: a resolved change enqueues the run; a change no run depends on enqueues nothing" {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore
            let scheduler = DispatchingScheduler()
            let teamId = "team-" + Guid.NewGuid().ToString "N"

            // A handler for the run's job, so the trigger is accepted: since
            // Phase 1105 a run whose trigger is refused is not "enqueued".
            (scheduler :> IJobScheduler)
                .RegisterHandler(
                    GroundedNarrativeTrigger.HandlerName,
                    { new IJobHandler with
                        member _.Execute(_) = async { return JobResult.Success }
                    }
                )

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
                (enqueue (registryOf summaryRun) (ScopeResolution.ofStorageScope (storageFor teamId)))
                [ summaryRun.Key ]
                "a resolved change to a depended-on metric enqueues the run"

            // Phase 1157 retired the string scope form; a carried (DataChangeScope.Carried) change is gone.

            Expect.isEmpty
                (enqueue
                    (registryOf (summaryRun |> GroundedNarrativeDefinition.dependingOn [ "cost" ]))
                    (ScopeResolution.ofStorageScope (storageFor teamId)))
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
                    stubRun
                        (GroundedNarrativePublished(
                            Guid.NewGuid(),
                            documentFor "x",
                            [],
                            None,
                            Error "-",
                            GroundedRepairSummary.complete 0
                        ))
                        seen
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
                | Ok(Scheduled id | Coalesced id) -> id
                | Error e -> failtestf "schedule refused: %A" e

            match
                jobs.TriggerOnce(deployment.TeamId, jobId, "_platform")
                |> Async.RunSynchronously
            with
            | Ok() -> ()
            | Error e -> failtestf "trigger refused: %A" e

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
                | GroundedNarrativePublished(_, document, _, _, _, _) -> document
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

// ─── The double run's first site ────────────────────────────────────
//
// `GroundedNarrativeTrigger.enqueueFor` schedules the run's job and then
// triggers it. A scheduler that coalesces on the idempotency key hands back
// the job already pending, so a change replayed inside the TTL used to
// trigger that one job twice. The scheduler now says which it returned
// (`ScheduleOutcome.Coalesced`), and the trigger fires only a job it
// created. The second site, `RecomputeJobHandler.applyPolicies`, is pinned
// pending in ReactiveRecomputeComposeTests.

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

        let fresh = Guid.NewGuid()
        let jobId = byKey.GetOrAdd(key, fresh)

        Ok(if jobId = fresh then Scheduled jobId else Coalesced jobId)

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

        testCaseAsync
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

            let scope = ScopeResolution.ofStorageScope (storageFor teamId)

            for _ in 1..2 do
                let! _ = GroundedNarrativeTrigger.enqueueFor lineage store scheduler registry scope [ "sales.rollup" ]
                ()

            Expect.equal scheduler.Jobs 1 "the replayed change coalesced onto the pending job"
            Expect.equal scheduler.AllTriggers 1 "and that job was triggered once, not once per schedule call"
        }

        // Phase 1105 — a `Manual` narrative job never fires on its own, so a
        // refused trigger is a run that did not start: not reported as
        // enqueued, and raised by the reaction for the decorator to log.
        testCaseAsync "1105: a refused narrative trigger is not enqueued, and the reaction raises it"
        <| async {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore
            let teamId = "team-" + Guid.NewGuid().ToString "N"

            // A fresh scheduler per call: a second schedule on the same one
            // would coalesce onto the first job and attempt no trigger at all.
            let refusing () =
                let inner = CoalescingScheduler() :> IJobScheduler

                { new IJobScheduler with
                    member _.RegisterHandler(n, h) = inner.RegisterHandler(n, h)
                    member _.RegisterHandlerAsync(n, h) = inner.RegisterHandlerAsync(n, h)
                    member _.Schedule(registration: JobRegistration) = inner.Schedule registration

                    member _.Schedule(scope: ResolvedScope, registration: JobRegistration) =
                        inner.Schedule(scope, registration)

                    member _.Cancel(s, j) = inner.Cancel(s, j)
                    member _.Disable(s, j) = inner.Disable(s, j)
                    member _.Enable(s, j) = inner.Enable(s, j)
                    member _.Get(s, j) = inner.Get(s, j)
                    member _.ListJobs s = inner.ListJobs s
                    member _.GetRecentRuns(s, j, n) = inner.GetRecentRuns(s, j, n)
                    member _.TriggerOnce(_, _, _) = async { return Error TriggerError.SchedulerStopping }
                    member _.NotifyEventWritten(s, e, t) = inner.NotifyEventWritten(s, e, t)
                }

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

            let scope = ScopeResolution.ofStorageScope (storageFor teamId)

            let! enqueued =
                GroundedNarrativeTrigger.enqueueFor lineage store (refusing ()) registry scope [ "sales.rollup" ]

            Expect.isEmpty enqueued "a run whose trigger was refused is not reported as enqueued"

            let react =
                GroundedNarrativeTrigger.reaction
                    (fun () -> store)
                    (fun () -> lineage)
                    (fun () -> Some(refusing ()))
                    (fun () -> Some registry)

            let! outcome = react scope [ "sales.rollup" ] |> Async.Catch

            match outcome with
            | Choice2Of2 ex -> Expect.stringContains ex.Message "not started" "the raise names the unstarted run"
            | Choice1Of2() -> failtest "a refused narrative trigger must not be dropped by the reaction"
        }

        // The first pin in this list counts triggers on a double. This one counts what the
        // property is about, model calls and publications, through the
        // in-process scheduler and the real job handler: a replay while the
        // first run is in flight and a replay after it finished both coalesce
        // and run nothing, and a different change is a different job.
        testCaseAsync
            "998: one data-arrival change replayed runs the narrative once on the in-process scheduler; a different change runs it again"
        <| async {
            let answers = ref 0

            let counting: Writer =
                fun id rendering ->
                    Interlocked.Increment(&answers.contents) |> ignore
                    citingWriter id rendering

            let deployment = deploy counting

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
            let registry = deployment.Services.GetRequiredService<GroundedNarrativeRegistry>()

            jobs.RegisterHandler(
                GroundedNarrativeTrigger.HandlerName,
                GroundedNarrativeTrigger.GroundedNarrativeJobHandler(
                    deployment.Store,
                    None,
                    registry,
                    deployment.Services.GetRequiredService<IGroundedNarrativeRun>()
                )
            )

            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore
            let change = deployment.Scope

            let enqueue changedIds =
                GroundedNarrativeTrigger.enqueueFor lineage deployment.Store jobs registry change changedIds

            // Every job in the scope has a finished run, and there are `count`.
            let settled count = async {
                let deadline = DateTime.UtcNow.AddSeconds 30.0
                let mutable finished = false

                while not finished && DateTime.UtcNow < deadline do
                    let! listed = jobs.ListJobs deployment.TeamId

                    let! runs =
                        listed
                        |> List.map (fun job -> jobs.GetRecentRuns(deployment.TeamId, job.JobId, 20))
                        |> Async.Sequential

                    finished <-
                        listed.Length = count
                        && runs |> Array.forall (List.exists (fun r -> r.Status = JobRunStatus.Succeeded))

                    if not finished then
                        do! Async.Sleep 100

                return finished
            }

            let! first = enqueue [ "sales.rollup" ]
            let! inFlight = enqueue [ "sales.rollup" ]
            let! firstSettled = settled 1
            let! afterwards = enqueue [ "sales.rollup" ]
            do! Async.Sleep 300

            let! listed = jobs.ListJobs deployment.TeamId
            Expect.hasLength listed 1 "one job for the one change"
            Expect.equal answers.Value 1 "the model wrote the narrative once"
            Expect.hasLength (published deployment) 1 "and it was published once"
            Expect.equal first [ summaryRun.Key ] "the change enqueued the run"
            Expect.isEmpty inFlight "a replay while the run is in flight coalesced and enqueued nothing"
            Expect.isTrue firstSettled "the one run finished"
            Expect.isEmpty afterwards "a replay after the run finished coalesced and enqueued nothing"

            let! different = enqueue [ "sales.rollup"; "sales.rollup@v2" ]
            let! secondSettled = settled 2

            Expect.equal different [ summaryRun.Key ] "a different change enqueued the run again"
            Expect.isTrue secondSettled "as a second job, which ran"
            Expect.equal answers.Value 2 "so the model wrote it a second time, once per change"
        }
    ]

// ── H. Requested projections and conjecture (Phase 1136) ────────────

/// A day-grain table of brand sales: revenue disclosable, cost internal —
/// so a series read of cost withholds every point.
let private brandSales: FactTableDefinition = {
    Id = "brand-sales"
    SchemaVersion = 1
    Hierarchy = "brand"
    Level = "brand"
    Columns = [
        FactTableDefinition.column "revenue" FactTableValueShape.Scalar
        {
            (FactTableDefinition.column "cost" FactTableValueShape.Scalar) with
                Disclosure = Some FactTableDisclosure.Internal
        }
    ]
    PeriodGrain = FactTablePeriodGrain.Day
    ProducingOperation = "sales-rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.Replace
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

let private tradingDay = DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc)

/// The day's rows for two brands, through the composed table writer, plus
/// the September revenue fact the scripted model reads with `query_facts`.
let private withBrandSales (deployment: Deployment) : Deployment =
    assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1250m Surfaceable)
    |> ignore

    let writer = deployment.Services.GetRequiredService<IFactTableWriter>()

    let period: TemporalExtent = {
        From = tradingDay
        To = tradingDay.AddDays 1.0
        Label = None
    }

    let row (brand: string) (revenue: decimal) (cost: decimal) : FactTableRow = {
        Subject = [ brand ]
        Period = period
        Values = Map.ofList [ "revenue", Scalar revenue; "cost", Scalar cost ]
    }

    let opened =
        match writer.OpenRun(deployment.Scope, brandSales.Id) |> Async.RunSynchronously with
        | Ok r -> r
        | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

    match
        writer.WriteRows(deployment.Scope, opened.RunId, [ row "zenith" 900m 400m; row "acme" 1250m 700m ])
        |> Async.RunSynchronously
    with
    | Ok _ -> ()
    | Error e -> failtestf "write: %s" (FactTableWriteError.describe e)

    match writer.Commit(deployment.Scope, opened.RunId) |> Async.RunSynchronously with
    | Ok _ -> deployment
    | Error e -> failtestf "commit: %s" (FactTableWriteError.describe e)

/// The gated fact reader a projection context offers: the fact tier's
/// per-request API over the run's own request context.
let private readThrough (context: GroundedProjectionContext) =
    (FactBrowseHandler.factBrowseApi context.Request).QuerySeries

let private request (kind: string) (metrics: string list) : string =
    sprintf
        """{"kind":"%s","tableId":"%s","metrics":[%s],"period":{"day":"2026-10-28"}}"""
        kind
        brandSales.Id
        (metrics |> List.map (sprintf "\"%s\"") |> String.concat ",")

/// The model's answer: the section's paragraphs (each a JSON value, written
/// as JSON text) and its projections.
let private answerWith (paragraphs: string list) (projections: string list) : string =
    sprintf
        """{"sections":[{"id":"summary","paragraphs":[%s],"projections":[%s]}]}"""
        (String.concat "," paragraphs)
        (String.concat "," projections)

let private citing (id: string) (rendering: string) =
    JsonSerializer.Serialize $"Acme revenue was [[Revenue|{rendering}|{id}]] in September."

let private deployFor (definition: GroundedNarrativeDefinition) (write: Writer) : Deployment =
    deployShaped
        definition
        [ brandSales ]
        (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools)
        (fun model -> ScriptedFactory(model) :> IAIProviderFactory)
        write
    |> withBrandSales

let private publishedDocument (outcome: GroundedNarrativeOutcome) : NarrativeDocument * NarrativeCitation list =
    match outcome with
    | GroundedNarrativePublished(_, document, citations, _, _, _) -> document, citations
    | other -> failtestf "expected a publication, got %s" (GroundedNarrativeOutcome.describe other)

let private summaryElements (document: NarrativeDocument) : NarrativeElement list =
    document.Sections |> List.find (fun s -> s.Id = "summary") |> _.Elements

let private tablesIn (elements: NarrativeElement list) =
    elements
    |> List.choose (function
        | NarrativeElement.Table(columns, rows) -> Some(columns, rows)
        | _ -> None)

let private paragraphText (element: NarrativeElement) : string option =
    match element with
    | NarrativeElement.Paragraph spans ->
        spans
        |> List.map (function
            | InlineSpan.Text s -> s
            | _ -> "")
        |> String.concat ""
        |> Some
    | _ -> None

/// The brand-sales facts of a metric, by subject member.
let private brandFacts (deployment: Deployment) (metric: string) : Map<string, Fact> =
    deployment.Store.Query(
        deployment.Scope,
        {
            FactQuery.all with
                Metric = Some(MetricRef metric)
        }
    )
    |> TestScope.ok
    |> Async.RunSynchronously
    |> List.filter (fun f -> f.Period.From = tradingDay)
    |> List.map (fun f -> List.last f.Subject.Path, f)
    |> Map.ofList

let projectionTests =
    testList "Phase 1136 H — a run draws the tables its findings ask for, and labels a hypothesis" [

        test "a deterministic projection reads a series under the run's principal; a withheld point is named, not drawn" {
            let seen = ref None

            let definition =
                summaryRun
                |> GroundedNarrativeDefinition.withProjectionContext (fun context -> async {
                    seen.Value <- Some(context.Scope.ScopeId, context.Principal, context.Surface)

                    let! built =
                        PresentationNarrative.project (readThrough context) (request "table" [ "revenue"; "cost" ])

                    return built |> Result.map (fun elements -> [ "summary", elements ])
                })

            let deployment =
                deployFor definition (fun id rendering -> answerWith [ citing id rendering ] [])

            let document, citations = publishedDocument (run deployment)

            Expect.equal
                seen.Value
                (Some(deployment.TeamId, "narrator", FactNarrativePublication))
                "the projection ran under the run's scope, principal and surface"

            let revenue = brandFacts deployment "revenue"
            let cost = brandFacts deployment "cost"

            match tablesIn (summaryElements document) with
            | [ columns, rows ] ->
                Expect.equal (columns |> List.map fst) [ "brand"; "revenue"; "cost" ] "subjects by metrics"

                for row in rows do
                    match row with
                    | [ InlineSpan.Text subject ] :: [ InlineSpan.Metric(label, value, Some factId) ] :: [ [] ] ->
                        Expect.equal label "revenue" "the cell names its metric"
                        Expect.equal factId revenue.[subject].FactId "the cell cites the Fact it states"
                        Expect.contains (NarrativeCitation.factIds citations) factId "a fact the gate admitted"
                        Expect.isFalse (value.Contains "700" || value.Contains "400") "no withheld value is drawn"
                    | other -> failtestf "unexpected row %A" other

                Expect.equal (rows |> List.length) 2 "both brands"
            | other -> failtestf "expected one table, got %A" other

            let notes = summaryElements document |> List.choose paragraphText

            for brand in [ "acme"; "zenith" ] do
                Expect.isTrue
                    (notes
                     |> List.exists (fun n -> n.Contains $"{brand}, cost" && n.Contains "withheld"))
                    $"the withheld cost of {brand} is named"

            Expect.isFalse
                (citations
                 |> List.exists (fun c -> cost |> Map.exists (fun _ f -> f.FactId = c.Reference)))
                "no cost fact is cited"
        }

        test
            "a requested breakdown renders with every number a gated fact; an unregistered kind is refused and the run publishes" {
            let definition =
                summaryRun
                |> GroundedNarrativeDefinition.withRequestBuilder "breakdown" (fun context request ->
                    PresentationNarrative.project (readThrough context) request.Json)

            let deployment =
                deployFor definition (fun id rendering ->
                    answerWith [ citing id rendering ] [
                        request "breakdown" [ "revenue" ]
                        request "pie" [ "revenue" ]
                    ])

            let document, citations = publishedDocument (run deployment)
            let revenue = brandFacts deployment "revenue"

            match tablesIn (summaryElements document) with
            | [ columns, rows ] ->
                Expect.equal (columns |> List.map fst) [ "brand"; "revenue" ] "one metric column"

                let cited =
                    rows
                    |> List.map (function
                        | [ InlineSpan.Text subject ] :: [ InlineSpan.Metric(_, value, Some factId) ] :: [] ->
                            Expect.equal factId revenue.[subject].FactId "the cell cites the Fact it states"
                            Expect.contains (NarrativeCitation.factIds citations) factId "a fact the gate admitted"
                            subject, value
                        | other -> failtestf "unexpected row %A" other)

                Expect.equal (cited |> List.map fst) [ "acme"; "zenith" ] "largest first"
            | other -> failtestf "expected one table, got %A" other

            let row = lastRow deployment
            Expect.equal (row.GetProperty("Outcome").GetString()) "published" "the run published"
            Expect.equal (row.GetProperty("Projections").GetInt32()) 1 "one projection drawn"

            let refusals =
                row.GetProperty("ProjectionRefusals").EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq

            match refusals with
            | [ refusal ] ->
                Expect.stringContains refusal "kind 'pie'" "the refusal names the request"
                Expect.stringContains refusal "no builder is registered" "and why"
                Expect.stringContains refusal "'breakdown'" "and what is registered"
            | other -> failtestf "expected one refusal, got %A" other

            Expect.stringContains
                (systemPrompt definition [ "query_facts" ])
                "\"projections\""
                "the model is told the request form"
        }

        test "a digit in a conjecture is refused, citation token or not" {
            for conjecture in
                [
                    JsonSerializer.Serialize {|
                        conjecture = "Perhaps the 2 new stores helped."
                    |}
                    JsonSerializer.Serialize "Trading held up. {{perhaps acme rose [[Revenue|1250|anything]]}}"
                ] do
                let deployment =
                    deployFor (summaryRun |> GroundedNarrativeDefinition.withConjecture) (fun id rendering ->
                        answerWith [ citing id rendering; conjecture ] [])

                match run deployment with
                | GroundedNarrativeRefused offences ->
                    Expect.contains
                        (offences |> List.map _.Reason)
                        UnreferencedNumber
                        "the digit is an unreferenced number"

                    Expect.isEmpty (published deployment) "and nothing was published"
                | other -> failtestf "expected a refusal, got %s" (GroundedNarrativeOutcome.describe other)
        }

        test "a conjecture renders as a labelled callout, and the audit row counts it" {
            let deployment =
                deployFor (summaryRun |> GroundedNarrativeDefinition.withConjecture) (fun id rendering ->
                    answerWith [
                        JsonSerializer.Serialize
                            $"Acme revenue was [[Revenue|{rendering}|{id}]] in September. {{{{perhaps the weather helped}}}}"
                        JsonSerializer.Serialize {|
                            conjecture = "The launch may have drawn new shoppers."
                        |}
                    ] [])

            let document, _ = publishedDocument (run deployment)

            let callouts =
                summaryElements document
                |> List.choose (function
                    | NarrativeElement.Callout(_, spans) -> Some spans
                    | _ -> None)

            Expect.equal
                callouts
                [
                    [
                        InlineSpan.Strong ConjectureLabel
                        InlineSpan.Text ": perhaps the weather helped"
                    ]
                    [
                        InlineSpan.Strong ConjectureLabel
                        InlineSpan.Text ": The launch may have drawn new shoppers."
                    ]
                ]
                "each conjecture is a labelled callout, after the prose it sat in"

            Expect.isFalse
                (summaryElements document
                 |> List.choose paragraphText
                 |> List.exists (fun p -> p.Contains "weather"))
                "and is not left in the paragraph"

            Expect.equal ((lastRow deployment).GetProperty("Conjectures").GetInt32()) 2 "the audit row counts both"
        }

        test "a run with neither renders exactly as before" {
            let spans = parseParagraph "Acme revenue was [[Revenue|1250|fact-id]] in September."

            let provenance = {
                ModuleId = "sales"
                PageRoute = None
                GeneratedAt = DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero)
                SettingsKey = "grounded-narrative:monthly-brand-summary"
                SettingsDisplay = [ "run", "monthly-brand-summary" ]
            }

            let before: NarrativeDocument = {
                Title = summaryRun.Title
                Subtitle = None
                Sections = [
                    {
                        Id = "summary"
                        Heading = "Summary"
                        Subheading = None
                        Elements = [ NarrativeElement.Paragraph spans; NarrativeElement.Divider ]
                    }
                ]
                Provenance = Some provenance
                Lang = None
                CanonicalUrl = None
            }

            let answer =
                """{"sections":[{"id":"summary","paragraphs":["Acme revenue was [[Revenue|1250|fact-id]] in September.",""]}]}"""

            match parseOutput answer with
            | Ok drafts ->
                Expect.isTrue
                    (drafts |> List.forall (fun d -> List.isEmpty d.Projections))
                    "an answer without projections decodes with none"

                match assemble summaryRun drafts [ "summary", [ NarrativeElement.Divider ] ] [] provenance with
                | Ok document ->
                    let json (d: NarrativeDocument) =
                        JsonSerializer.Serialize(d, ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ())

                    Expect.equal (json document) (json before) "the document is byte-identical"
                | Error reason -> failtestf "assemble refused: %s" reason
            | Error reason -> failtestf "parse refused: %s" reason

            Expect.equal
                (rulesFor summaryRun)
                [
                    "The report is REFUSED, and never published, if any of these is broken:"
                    "- Write every figure as a citation token [[label|value|reference]]: the label in words with no digits, the value exactly as the tool rendered it (or that figure rounded), the reference the fact's id (or kind:id for a declared reference)."
                    "- Write no other digits anywhere, and no numbers in words."
                    "- Cite only figures a tool returned. Do not compute differences, ratios or percentages yourself."
                    "- Tables and charts are added for you. Write prose only."
                    ""
                    "Answer with ONLY a JSON object: {\"sections\":[{\"id\":\"<section id>\",\"paragraphs\":[\"...\"]}]}"
                ]
                "and the instruction's rules are the rules it always had"
        }
    ]

// ── I. One classification of an outcome as a job result ─────────

/// How a job should treat each outcome. The match has no wildcard, so a new
/// `GroundedNarrativeOutcome` case cannot compile here without the author
/// deciding whether another attempt is worth paying for.
type private Retry =
    | Settled
    | Retried
    | GivenUp

let private expectedRetry (outcome: GroundedNarrativeOutcome) : Retry =
    match outcome with
    | GroundedNarrativePublished _ -> Settled
    | GroundedNarrativeFailed _ -> Retried
    | GroundedNarrativeRefused _
    | GroundedNarrativeUnfunded _
    | GroundedNarrativeOverBudget _ -> GivenUp

let private everyOutcome: GroundedNarrativeOutcome list = [
    GroundedNarrativePublished(
        Guid.NewGuid(),
        documentFor "x",
        [],
        None,
        Error "no signer",
        GroundedRepairSummary.complete 0
    )
    GroundedNarrativeRefused []
    GroundedNarrativeFailed "provider outage"
    GroundedNarrativeUnfunded "no key"
    GroundedNarrativeOverBudget "budget reached"
]

let jobResultTests =
    testList "Phase 1116 I — a narrative outcome maps to a job result in one place" [

        for outcome in everyOutcome do
            let name = (outcome.GetType().Name)

            test (sprintf "%s is classified as its retry rule says" name) {
                match GroundedNarrativeOutcome.toJobResult outcome, expectedRetry outcome with
                | Success, Settled -> ()
                | TransientFailure reason, Retried ->
                    Expect.equal reason (GroundedNarrativeOutcome.describe outcome) "a retry names the outcome"
                | PermanentFailure reason, GivenUp ->
                    Expect.equal
                        reason
                        (GroundedNarrativeOutcome.describe outcome)
                        "a permanent failure names the outcome"
                | other, expected -> failtestf "%s: expected %A, got %A" name expected other
            }
    ]

// ── J. The trigger a run records (Phase 1045) ───────────────────────
//
// A grounded run's job records the trigger that enqueued it. A deployment
// that runs a narrative on a cadence registers the same handler on a cron
// trigger, and every such run used to record `data-arrival`. The handler
// now names a cron job's runs `cadence`, whoever fired the attempt, and
// every other job keeps the data-arrival name.

/// The trigger the handler hands the run for one job context.
let private recordedTrigger (job: Trigger) (source: TriggerSource) (attempt: int) : string =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    let registry = GroundedNarrativeRegistry()
    registry.Register summaryRun |> ignore
    let seen = ConcurrentQueue<GroundedNarrativeRequest>()
    let teamId = "team-" + Guid.NewGuid().ToString "N"

    let handler =
        GroundedNarrativeTrigger.GroundedNarrativeJobHandler(
            store,
            None,
            registry,
            stubRun
                (GroundedNarrativePublished(
                    Guid.NewGuid(),
                    documentFor "x",
                    [],
                    None,
                    Error "-",
                    GroundedRepairSummary.complete 0
                ))
                seen
        )
        :> IJobHandler

    let result =
        handler.Execute {
            JobId = Guid.NewGuid()
            ScopeId = teamId
            AccessContext = AccessContext.unrestricted (AuthenticatedUser "_platform")
            Attempt = attempt
            Trigger = job
            Scope = ScopeResolution.ofStorageScope (storageFor teamId)
            TriggerSource = source
            ScheduledAt = DateTime.UtcNow
            RunningAt = DateTime.UtcNow
            Payload = GroundedNarrativeTrigger.payloadFor summaryRun.Key Set.empty
            DeadLetterDestination = None
        }
        |> Async.RunSynchronously

    Expect.equal result Success "the run was started"
    (seen |> Seq.exactlyOne).Trigger

let triggerNameTests =
    testList "Phase 1045 J — a run records the trigger that enqueued it" [

        test "a data-arrival job (Manual) records data-arrival, as it always did" {
            Expect.equal
                (recordedTrigger Trigger.Manual (ScheduledManually GroundedNarrativeTrigger.Actor) 1)
                GroundedNarrativeTrigger.TriggerName
                "the data-arrival name is unchanged"

            Expect.equal GroundedNarrativeTrigger.TriggerName "data-arrival" "and its value is unchanged"
        }

        test "a cron job's tick records cadence" {
            Expect.equal (recordedTrigger (CronTrigger "0 6 * * *") ScheduledByCron 1) "cadence" "a tick"
        }

        test "a cron job's retry, or an operator's run-now, still records cadence" {
            Expect.equal (recordedTrigger (CronTrigger "0 6 * * *") ScheduledByCron 3) "cadence" "a retry"

            Expect.equal
                (recordedTrigger (CronTrigger "0 6 * * *") (ScheduledManually "operator") 1)
                "cadence"
                "a run-now from the jobs admin is still the cadence's job"
        }

        // Both paths through the in-process scheduler: the context the
        // scheduler synthesises carries the job's trigger, and the handler
        // reads it.
        testCaseAsync "on the in-process scheduler, a cadence job records cadence and a data-arrival job data-arrival"
        <| async {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore
            let teamId = "team-" + Guid.NewGuid().ToString "N"
            let scope = ScopeResolution.ofStorageScope (storageFor teamId)

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
                    store,
                    None,
                    registry,
                    stubRun
                        (GroundedNarrativePublished(
                            Guid.NewGuid(),
                            documentFor "x",
                            [],
                            None,
                            Error "-",
                            GroundedRepairSummary.complete 0
                        ))
                        seen
                )
            )

            let runs count = async {
                let deadline = DateTime.UtcNow.AddSeconds 30.0

                while seen.Count < count && DateTime.UtcNow < deadline do
                    do! Async.Sleep 50

                return seen |> Seq.map _.Trigger |> List.ofSeq
            }

            // The cadence: a cron job a deployment registers for the run,
            // fired now rather than waited for.
            let cadence = {
                GroundedNarrativeTrigger.registrationFor teamId summaryRun.Key Set.empty with
                    Trigger = CronTrigger "0 6 * * *"
                    Idempotency = None
                    Tags = Map.ofList [ "run", summaryRun.Key ]
            }

            match! jobs.Schedule(scope, cadence) with
            | Ok(Scheduled jobId) ->
                match! jobs.TriggerOnce(teamId, jobId, "operator") with
                | Ok() -> ()
                | Error e -> failtestf "the cadence job was not fired: %s" (TriggerError.message e)
            | other -> failtestf "the cadence job was not scheduled: %A" other

            let! afterCadence = runs 1
            Expect.equal afterCadence [ "cadence" ] "the cadence's run records cadence"

            let! enqueued = GroundedNarrativeTrigger.enqueueFor lineage store jobs registry (scope) [ "sales.rollup" ]

            Expect.equal enqueued [ summaryRun.Key ] "the change enqueued the run"
            let! afterChange = runs 2
            Expect.equal afterChange [ "cadence"; "data-arrival" ] "and the data-arrival run records data-arrival"
        }
    ]

// ── The repair loop ──────────────────────────────────────────────

let private offence (location: string) (claim: string) : GroundingOffence = {
    Location = location
    Claim = claim
    Reason = UnreferencedNumber
}

let private checkDoc (document: NarrativeDocument) =
    NarrativeGrounding.check NarrativeGrounding.defaultPolicy resolve document

/// A draft the scripted model writes, then its repairs, one answer per call:
/// two refused claims in the draft (an unreferenced number in element 2, a
/// figure with no reference in element 3); the first repair fixes element
/// 3, and every later one rewrites element 2 as it was.
let private repairingWriter () : Writer =
    let calls = ref 0

    fun id rendering ->
        calls.Value <- calls.Value + 1

        if calls.Value = 1 then
            JsonSerializer.Serialize {|
                sections = [
                    {|
                        id = "summary"
                        paragraphs = [
                            $"Acme revenue was [[Revenue|{rendering}|{id}]] in September."
                            "It was the best of the three months."
                            "Costs were [[Cost|900]]."
                        ]
                    |}
                ]
            |}
        elif calls.Value = 2 then
            """{"repairs":[{"section":"summary","element":3,"text":"Costs were not reported."}]}"""
        else
            """{"repairs":[{"section":"summary","element":2,"text":"It was the best of the three months."}]}"""

let repairTests =
    testList "Phase 1175 — a refused draft is repaired while it improves, then its grounded remainder is published" [

        test "a job row and a definition written before the warning fields read the warning as None (GP 11)" {
            let options = FableConverters.create ()

            let strip (field: string) (json: string) =
                let node = System.Text.Json.Nodes.JsonNode.Parse(json).AsObject()
                Expect.isTrue (node.Remove field) $"the property {field} exists to be removed"
                node.ToJsonString()

            let row: JobRun = {
                RunId = Guid.NewGuid()
                JobId = Guid.NewGuid()
                ScopeId = "legacy-scope"
                Attempt = 1
                StartedAt = DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                CompletedAt = Some(DateTime(2026, 1, 2, 3, 9, 5, DateTimeKind.Utc))
                Status = JobRunStatus.Succeeded
                Error = None
                DurationMs = Some 300_000L
                ExternalHandle = None
                Warning = Some "withheld"
            }

            let revivedRow =
                JsonSerializer.Deserialize<JobRun>(strip "Warning" (JsonSerializer.Serialize(row, options)), options)

            Expect.isNone revivedRow.Warning "an absent Warning reads as None"
            Expect.equal revivedRow.RunId row.RunId "and the rest of the row is untouched"

            let definition = {
                SchedulerPolicy.definitionFor
                    (Guid.NewGuid())
                    (GroundedNarrativeTrigger.registrationFor "legacy-scope" summaryRun.Key Set.empty)
                    DateTime.UtcNow with
                    LastRunWarning = Some "withheld"
            }

            let revivedDefinition =
                JsonSerializer.Deserialize<JobDefinition>(
                    strip "LastRunWarning" (JsonSerializer.Serialize(definition, options)),
                    options
                )

            Expect.isNone revivedDefinition.LastRunWarning "an absent LastRunWarning reads as None"
            Expect.equal revivedDefinition.JobId definition.JobId "and the rest of the definition is untouched"
        }

        test "progress: a shrinking set continues, a swapped set is progress, the same or a larger set is not" {
            let a = offence "section 's' › element 1 › span 1" "a"
            let b = offence "section 's' › element 2 › span 1" "b"
            let c = offence "section 's' › element 3 › span 1" "c"

            Expect.isTrue (GroundedRepair.isProgress [ a; b ] [ b ]) "fewer refused claims is progress"
            Expect.isTrue (GroundedRepair.isProgress [ a; b ] [ a; c ]) "as many, different members, is progress"
            Expect.isFalse (GroundedRepair.isProgress [ a; b ] [ b; a ]) "the same set is not"
            Expect.isFalse (GroundedRepair.isProgress [ a ] [ a; b ]) "a larger set is not"
        }

        test "stopping: the same set three times stops, the ceiling stops, the token budget stops" {
            let policy = GroundedRepairPolicy.defaults
            let afterProgress = GroundedRepair.start |> GroundedRepair.advance true 0L
            Expect.isNone (GroundedRepair.shouldStop policy afterProgress) "progress continues"

            let stalled =
                [ 1..3 ]
                |> List.fold (fun p _ -> GroundedRepair.advance false 0L p) afterProgress

            Expect.equal (GroundedRepair.shouldStop policy stalled) (Some GroundedRepair.NoProgress) "three stalls stop"

            let twoStalls =
                [ 1..2 ]
                |> List.fold (fun p _ -> GroundedRepair.advance false 0L p) afterProgress

            Expect.isNone (GroundedRepair.shouldStop policy twoStalls) "two do not"

            let atCeiling =
                [ 1 .. policy.MaxRepairRounds ]
                |> List.fold (fun p _ -> GroundedRepair.advance true 0L p) GroundedRepair.start

            Expect.equal (GroundedRepair.shouldStop policy atCeiling) (Some GroundedRepair.Ceiling) "the ceiling stops"

            Expect.equal
                (GroundedRepair.shouldStop policy {
                    afterProgress with
                        Tokens = policy.MaxRunTokens.Value
                })
                (Some GroundedRepair.TokenBudget)
                "the token budget stops"

            Expect.equal
                (GroundedRepair.shouldStop GroundedRepairPolicy.refuseOnOffence GroundedRepair.start)
                (Some GroundedRepair.Ceiling)
                "no rounds at all under the refuse-on-offence policy"
        }

        test "the place of every refused claim is read from the gate's own locations" {
            let offences =
                checkDoc (
                    docOf [
                        para [ text "Flat on last week." ]
                        para [ text "Up by "; metric "Revenue" "12" None ]
                        NarrativeElement.BulletList [ [ text "fine" ]; [ text "three stores" ] ]
                        NarrativeElement.Table(
                            [ "Store", Left; "Sales", Right ],
                            [ [ [ text "North" ]; [ text "17" ] ] ]
                        )
                        NarrativeElement.KeyValueGrid [
                            "Week", [ text "fourth" ]
                            "Sales", [ metric "Sales" "9" None ]
                        ]
                    ]
                )
                |> offencesOf

            Expect.equal
                (offences |> List.map (fun o -> GroundedRepair.tryPlace o.Location))
                [
                    Some {
                        Section = "summary"
                        Element = 2
                        Part = GroundedRepair.Span 2
                    }
                    Some {
                        Section = "summary"
                        Element = 3
                        Part = GroundedRepair.Item 2
                    }
                    Some {
                        Section = "summary"
                        Element = 4
                        Part = GroundedRepair.Row 1
                    }
                    Some {
                        Section = "summary"
                        Element = 5
                        Part = GroundedRepair.Entry 2
                    }
                ]
                "a span, an item, a row and an entry, each where the gate found it"
        }

        test "the splice: a repaired element replaces only itself" {
            let draft =
                docOf [
                    para [ text "First." ]
                    para [ text "Second, with three things." ]
                    NarrativeElement.Table([ "Store", Left ], [ [ [ text "North" ] ] ])
                ]

            let repaired =
                GroundedRepair.splice
                    [
                        {
                            Section = "summary"
                            Element = 2
                            Text = $"Second, with [[Revenue|1,248,300|{revenueId}]]."
                        }
                        // Not the model's prose: a projection is never rewritten.
                        {
                            Section = "summary"
                            Element = 3
                            Text = "replaced"
                        }
                    ]
                    draft

            Expect.equal
                repaired.Sections.Head.Elements
                [
                    para [ text "First." ]
                    para [
                        text "Second, with "
                        metric "Revenue" "1,248,300" (Some revenueId)
                        text "."
                    ]
                    NarrativeElement.Table([ "Store", Left ], [ [ [ text "North" ] ] ])
                ]
                "element two is the rewrite; elements one and three are untouched"

            Expect.equal
                (GroundedRepair.tokensOf (List.item 1 repaired.Sections.Head.Elements))
                $"Second, with [[Revenue|1,248,300|{revenueId}]]."
                "and an element reads back in the format the model writes"
        }

        test
            "the prune: a pruned span leaves its paragraph standing, a pruned last item removes its list, and the result passes the gate" {
            let draft =
                docOf [
                    para [
                        text "Revenue was "
                        metric "Revenue" "1,248,300" (Some revenueId)
                        text ", the best of three months."
                    ]
                    NarrativeElement.BulletList [ [ text "seven stores opened" ] ]
                    para [ text "Nothing else moved." ]
                ]

            let offences = checkDoc draft |> offencesOf
            Expect.equal (List.length offences) 2 "two refused claims: the prose run, and the list item"

            match GroundedRepair.prune offences draft with
            | Error why -> failtestf "the claims are placeable: %s" why
            | Ok pruned ->
                Expect.equal
                    pruned.Sections.Head.Elements
                    [
                        para [ text "Revenue was "; metric "Revenue" "1,248,300" (Some revenueId) ]
                        para [ text "Nothing else moved." ]
                    ]
                    "the span is gone and its paragraph stands; the list lost its last item and went"

                Expect.isTrue (NarrativeGroundingVerdict.isGrounded (checkDoc pruned)) "the pruned document passes"

                let noted = {
                    pruned with
                        Sections = pruned.Sections @ [ GroundedRepair.withheldNote draft offences ]
                }

                Expect.isTrue
                    (NarrativeGroundingVerdict.isGrounded (checkDoc noted))
                    "and so does it with the withheld-claims note, which states no number"
        }

        test
            "the withheld note names each claim's place and reason, never its value — even under a heading with a digit" {
            let draft =
                docOf [ para [ text "Revenue was "; metric "Revenue" "999" (Some revenueId) ] ]

            let offences = checkDoc draft |> offencesOf
            let note = GroundedRepair.withheldNote draft offences

            let lines =
                note.Elements
                |> List.collect (function
                    | NarrativeElement.BulletList items ->
                        items |> List.map (fun item -> GroundedRepair.tokensOf (Paragraph item))
                    | _ -> [])

            Expect.equal
                lines
                [
                    "part of a paragraph in a section: a figure that does not match its recorded value"
                ]
                "the place (a heading with a digit is named as a section) and the reason"

            Expect.isFalse
                (note.Elements
                 |> List.exists (fun e -> GroundedRepair.tokensOf e |> Seq.exists Char.IsDigit))
                "no digit anywhere in the note"

            Expect.isTrue
                (NarrativeGroundingVerdict.isGrounded (checkDoc { draft with Sections = [ note ] }))
                "the note passes the gate on its own"
        }

        test
            "end to end: the loop repairs one claim, stops on no progress, publishes the rest, and the job succeeds with a warning" {
            let deployment =
                deployShaped
                    (summaryRun
                     |> GroundedNarrativeDefinition.withRepairPolicy GroundedRepairPolicy.defaults)
                    []
                    (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools)
                    (fun model -> ScriptedFactory(model) :> IAIProviderFactory)
                    (repairingWriter ())

            let fact =
                assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1250m Surfaceable)

            let result = narrativeJob deployment

            let warning =
                match result with
                | SuccessWithWarning warning -> warning
                | other -> failtestf "expected a success with a warning, got %A" other

            Expect.stringContains warning "1 claim(s) withheld after 4 repair round(s)" "the count and the rounds"
            Expect.stringContains warning "element 2" "and the claim, with its place"
            Expect.stringContains warning (GroundingOffenceReason.describe UnreferencedNumber) "and its reason"

            // The report was published, without the claim that never grounded.
            let entries = published deployment
            Expect.equal (List.length entries) 1 "the report was published"

            let document =
                match deployment.Ingestor.Ingested with
                | [ document ] -> document
                | other -> failtestf "expected one indexed narrative, got %d" (List.length other)

            let summary = document.Sections |> List.find (fun s -> s.Id = "summary")

            match summary.Elements |> List.map GroundedRepair.tokensOf with
            | [ first; second ] ->
                Expect.stringStarts first "Acme revenue was [[Revenue|" "the grounded prose stands"
                Expect.stringContains first fact.FactId "citing its fact"
                Expect.equal second "Costs were not reported." "the repaired element replaced the refused one"
            | other -> failtestf "expected the grounded and the repaired element, got %A" other

            let note =
                document.Sections
                |> List.find (fun s -> s.Id = GroundedRepair.WithheldSectionId)

            Expect.contains
                (note.Elements
                 |> List.collect (function
                     | NarrativeElement.BulletList items ->
                         items |> List.map (fun item -> GroundedRepair.tokensOf (Paragraph item))
                     | _ -> []))
                "part of a paragraph in the section \"Summary\": a number that is not a recorded fact"
                "the note names it"

            // The job: its run row reads Succeeded, with the warning.
            let job =
                SchedulerPolicy.definitionFor
                    (Guid.NewGuid())
                    (GroundedNarrativeTrigger.registrationFor deployment.TeamId summaryRun.Key Set.empty)
                    DateTime.UtcNow

            let running: JobRun = {
                RunId = Guid.NewGuid()
                JobId = job.JobId
                ScopeId = job.ScopeId
                Attempt = 1
                StartedAt = DateTime.UtcNow
                CompletedAt = None
                Status = JobRunStatus.Running
                Error = None
                DurationMs = None
                ExternalHandle = None
                Warning = None
            }

            let ended =
                JobLifecycle.transition
                    (JobLifecycle.Loop(10L, None))
                    (Some job)
                    running
                    DateTime.UtcNow
                    (JobLifecycle.RunOutcome.ofJobResult result)

            Expect.equal (ended.Row |> Option.map _.Status) (Some JobRunStatus.Succeeded) "the run row is Succeeded"
            Expect.equal (ended.Row |> Option.bind _.Warning) (Some warning) "with the warning"
            Expect.equal (ended.Definition |> Option.bind _.LastRunWarning) (Some warning) "on the definition too"
            Expect.equal (ended.Definition |> Option.map _.ConsecutiveFailures) (Some 0) "a success, not a failure"
            Expect.isNone ended.Notify "and no dead-letter notification"

            // The audit trail: every round, then the publication.
            // The trail reads newest first.
            let rows = narrativeRows deployment |> List.rev

            Expect.equal
                (rows |> List.map _.EventType)
                [
                    yield! List.replicate 4 GroundedNarrativeEvents.RepairRoundType
                    GroundedNarrativeEvents.PublishedType
                ]
                "four rounds, then one publication row"

            let progressOf (row: ModuleEvent) =
                (JsonDocument.Parse row.Payload).RootElement.GetProperty("Progress").GetBoolean()

            Expect.equal
                (rows |> List.take 4 |> List.map progressOf)
                [ true; false; false; false ]
                "the first round progressed; three did not, and the loop stopped"

            Expect.stringContains (List.last rows).Payload "published-with-withheld" "the outcome says so"
        }
    ]

// ── Admissions: the number the instruction asked for, and the count ──

let private wording (version: int) (prompt: string) : GroundingInstruction = {
    Version = version
    Prompt = prompt
    Guidance = Map.empty
}

let private firstDraft (instruction: GroundingInstruction option) : GroundingCheckOptions = {
    Instruction = instruction
    LateStage = false
}

let private lateStage (instruction: GroundingInstruction option) : GroundingCheckOptions = {
    Instruction = instruction
    LateStage = true
}

let private subjectFacts: Map<string, string> =
    Map.ofList [
        String('1', 64), "brand/acme"
        String('2', 64), "brand/globex"
        String('3', 64), "brand/initech"
        String('4', 64), "brand/acme"
    ]

let private admitResolve (reference: string) =
    if subjectFacts.ContainsKey reference then
        GroundingCurrent([ "100" ], Some 100m, false)
    else
        resolve reference

let private checkUnder (options: GroundingCheckOptions) (elements: NarrativeElement list) =
    NarrativeGrounding.checkWith
        NarrativeGrounding.defaultPolicy
        admitResolve
        subjectFacts.TryFind
        options
        (docOf elements)

/// A paragraph stating `prose`, then one metric span per fact id.
let private citingSpans (prose: string) (factIds: string list) =
    para [
        text prose
        yield! factIds |> List.map (fun id -> metric "Revenue" "100" (Some id))
    ]

let private listOf (n: int) =
    NarrativeElement.BulletList [ for k in 1..n -> [ text (sprintf "Brand %c" (char (int 'A' + k - 1))) ] ]

let private top3 = wording 2 "Report on the top three categories by revenue."

let admissionTests =
    testList
        "Phase 1176 — the gate admits a number the instruction asked for, and, late, a count of what the document lists"
        [

            test "the matcher reads digits and words, singular and plural, with adjectives between" {
                let read text =
                    NarrativeGrounding.mentions NarrativeGrounding.defaultPolicy text
                    |> List.map (fun m -> m.Whole, m.Following)

                let table = [
                    "the top three categories", [ Some 3, [ "category" ] ]
                    "the top 3 categories", [ Some 3, [ "category" ] ]
                    "three category", [ Some 3, [ "category" ] ]
                    "the three largest categories all fell", [ Some 3, [ "largest"; "category" ] ]
                    "twenty-one markets", [ Some 21, [ "market" ] ]
                    "three of them", [ Some 3, [] ]
                    "three pounds", [ None, [ "pound" ] ]
                ]

                for (text, expected) in table do
                    Expect.equal (read text) expected (sprintf "'%s'" text)

                // A figure is never a whole number, however its digits split.
                for figure in
                    [
                        "3%"
                        "£3"
                        "GBP 3"
                        "-3"
                        "3.5 stores"
                        "1,200 stores"
                        "1/3 of stores"
                        "½ of stores"
                        "the 3rd store"
                        "3m"
                    ] do
                    let wholes = read figure |> List.map fst
                    Expect.isNonEmpty wholes (sprintf "'%s' states a number" figure)
                    Expect.allEqual wholes None (sprintf "'%s' is no whole number" figure)

                // "three percent": the number word "percent" is a mention too,
                // and neither is a whole number.
                Expect.equal
                    (read "three percent" |> List.map fst)
                    [ None; None ]
                    "a percentage written in words is not a whole number"

                let asked = NarrativeGrounding.mentions NarrativeGrounding.defaultPolicy top3.Prompt

                let echoes text =
                    NarrativeGrounding.mentions NarrativeGrounding.defaultPolicy text
                    |> List.forall (NarrativeGrounding.echoes asked)

                Expect.isTrue (echoes "the top three categories") "the echo"
                Expect.isTrue (echoes "the three largest categories all made less") "adjectives between"
                Expect.isTrue (echoes "3 categories") "digits for the word"
                Expect.isTrue (echoes "three category") "the singular"
                Expect.isFalse (echoes "three markets") "a different noun"
                Expect.isFalse (echoes "four categories") "a different number"
                Expect.isFalse (echoes "three largest markets") "an adjective in common is not the noun"
            }

            test "the echo passes on a FIRST draft, cited by its wording version and section" {
                let elements = [
                    citingSpans "The top three categories all grew; the largest, " [ String('1', 64) ]
                ]

                Expect.isFalse
                    (NarrativeGroundingVerdict.isGrounded (checkUnder (firstDraft None) elements))
                    "with no instruction the number is refused as before"

                match checkUnder (firstDraft (Some top3)) elements with
                | Grounded citations ->
                    Expect.contains citations (NarrativeCitation.instruction 2 "summary") "cited as the instruction"

                    Expect.equal
                        (List.last citations).Reference
                        "instruction:2#summary"
                        "written <kind>:<version>#<section>"

                    Expect.equal
                        (NarrativeCitation.admissions citations)
                        [ NarrativeCitation.instruction 2 "summary" ]
                        "the admissions are the non-Fact citations"

                    Expect.equal (NarrativeCitation.factIds citations) [ String('1', 64) ] "beside the Fact it cites"
                | Ungrounded offences ->
                    failtestf
                        "the echo was refused: %s"
                        (offences |> List.map GroundingOffence.describe |> String.concat "; ")
            }

            test "a section's guidance is wording in force for that section only" {
                let guided = {
                    wording 1 "Summarise the month." with
                        Guidance = Map.ofList [ "summary", "Name the five weakest stores." ]
                }

                Expect.isTrue
                    (NarrativeGroundingVerdict.isGrounded (
                        checkUnder (firstDraft (Some guided)) [ para [ text "The five weakest stores all shrank." ] ]
                    ))
                    "the section's guidance admits its own number"

                let elsewhere = {
                    guided with
                        Guidance = Map.ofList [ "outlook", "Name the five weakest stores." ]
                }

                Expect.isFalse
                    (NarrativeGroundingVerdict.isGrounded (
                        checkUnder (firstDraft (Some elsewhere)) [ para [ text "The five weakest stores all shrank." ] ]
                    ))
                    "another section's guidance does not"
            }

            test "a near-miss echo is refused: the wrong noun, or the wrong number" {
                for prose in [ "The top three markets all grew."; "The top four categories all grew." ] do
                    Expect.equal
                        (reasonsOf (checkUnder (lateStage (Some top3)) [ para [ text prose ] ]))
                        [ UnreferencedNumber ]
                        prose
            }

            test "after the instruction is reworded, the old number is refused" {
                let elements = [ para [ text "The top three categories all grew." ] ]
                let reworded = wording 3 "Report on the top five categories by revenue."

                Expect.isTrue
                    (NarrativeGroundingVerdict.isGrounded (checkUnder (firstDraft (Some top3)) elements))
                    "under version 2 it passes"

                Expect.equal
                    (reasonsOf (checkUnder (firstDraft (Some reworded)) elements))
                    [ UnreferencedNumber ]
                    "under version 3 it is refused"
            }

            test "a count of the cited subjects is refused on a first draft and passes at the late stage" {
                let elements = [
                    citingSpans "Three brands carried the month: " [ String('1', 64); String('2', 64); String('3', 64) ]
                ]

                Expect.equal
                    (reasonsOf (checkUnder (firstDraft (Some top3)) elements))
                    [ UnreferencedNumber ]
                    "a first draft is asked to cite or rephrase first"

                match checkUnder (lateStage (Some top3)) elements with
                | Grounded citations ->
                    Expect.equal
                        (NarrativeCitation.admissions citations |> List.map _.Reference)
                        [ "structural-count:section 'summary' › element 1 › span 1" ]
                        "cited as a structural count at its location"
                | Ungrounded offences ->
                    failtestf
                        "the count was refused: %s"
                        (offences |> List.map GroundingOffence.describe |> String.concat "; ")
            }

            test "subjects are counted DISTINCT, and a count that does not match is refused" {
                // Two of the three spans cite facts about the same brand.
                let twoBrands =
                    citingSpans "Two brands carried the month: " [ String('1', 64); String('2', 64); String('4', 64) ]

                Expect.isTrue
                    (NarrativeGroundingVerdict.isGrounded (checkUnder (lateStage None) [ twoBrands ]))
                    "two distinct subjects behind three spans"

                let four =
                    citingSpans "Four brands carried the month: " [ String('1', 64); String('2', 64); String('3', 64) ]

                Expect.equal
                    (reasonsOf (checkUnder (lateStage None) [ four ]))
                    [ UnreferencedNumber ]
                    "'four' with three cited subjects is refused"
            }

            test "a count of the list or table that immediately follows passes only at the late stage" {
                let withList = [ para [ text "Three brands led:" ]; listOf 3 ]

                Expect.isFalse
                    (NarrativeGroundingVerdict.isGrounded (checkUnder (firstDraft None) withList))
                    "refused on a first draft"

                Expect.isTrue
                    (NarrativeGroundingVerdict.isGrounded (checkUnder (lateStage None) withList))
                    "the list's item count passes late"

                let table =
                    NarrativeElement.Table(
                        [ "Brand", TableAlignment.Left ],
                        [ [ [ text "Acme" ] ]; [ [ text "Globex" ] ]; [ [ text "Initech" ] ] ]
                    )

                Expect.isTrue
                    (NarrativeGroundingVerdict.isGrounded (
                        checkUnder (lateStage None) [ para [ text "Three brands led:" ]; table ]
                    ))
                    "a table's row count passes late"

                Expect.isFalse
                    (NarrativeGroundingVerdict.isGrounded (
                        checkUnder (lateStage None) [
                            para [ text "Three brands led:" ]
                            NarrativeElement.Divider
                            listOf 3
                        ]
                    ))
                    "a list that does not IMMEDIATELY follow is not counted"

                Expect.isFalse
                    (NarrativeGroundingVerdict.isGrounded (
                        checkUnder (lateStage None) [ para [ text "Four brands led:" ]; listOf 3 ]
                    ))
                    "a count that does not match is refused"
            }

            test "a percentage, a currency amount and a fraction stay refused in every stage" {
                // Every stage, with an instruction naming the same figure, and
                // three cited subjects and a three-item list to count against.
                let asks =
                    wording 1 "Report the top 3% of categories, the £3 lines and the 1/3 share."

                let three = [ String('1', 64); String('2', 64); String('3', 64) ]

                for options in [ firstDraft (Some asks); lateStage (Some asks) ] do
                    for prose in
                        [
                            "The top 3% of categories: "
                            "The £3 lines: "
                            "The 1/3 share: "
                            "Three percent of categories: "
                        ] do
                        Expect.contains
                            (reasonsOf (checkUnder options [ citingSpans prose three; listOf 3 ]))
                            UnreferencedNumber
                            (sprintf "'%s' (late stage: %b)" prose options.LateStage)
            }

            test "every number in a run must be admitted: one admitted number does not carry another" {
                Expect.equal
                    (reasonsOf (
                        checkUnder (lateStage (Some top3)) [
                            para [ text "The top three categories made 40% of sales." ]
                        ]
                    ))
                    [ UnreferencedNumber ]
                    "the percentage beside the echo still refuses the run"
            }

            test "the existing check is the check with no instruction, at no late stage" {
                let elements = [
                    citingSpans "Three brands: " [ String('1', 64); String('2', 64); String('3', 64) ]
                    listOf 3
                ]

                Expect.equal
                    (NarrativeGrounding.check NarrativeGrounding.defaultPolicy admitResolve (docOf elements))
                    (checkUnder GroundingCheckOptions.none elements)
                    "check = checkWith none"

                Expect.isFalse
                    (NarrativeGroundingVerdict.isGrounded (checkUnder GroundingCheckOptions.none elements))
                    "and refuses"
            }

            test "the fact gate resolves each current Fact's subject for the late-stage count" {
                let fixture = gateFixture ()
                let scopeId = "team-" + Guid.NewGuid().ToString "N"

                let factFor (brand: string) =
                    assertOk fixture.Store scopeId {
                        assertedDraft "revenue" 1250m Surfaceable with
                            Subject = {
                                Hierarchy = "brand"
                                Path = [ brand ]
                            }
                    }

                let ids = [ "acme"; "globex"; "initech" ] |> List.map (fun b -> (factFor b).FactId)

                let gate =
                    match fixture.Gate with
                    | :? IInstructedNarrativeGroundingGate as instructed -> instructed
                    | _ -> failtest "the fact gate offers the instructed check"

                let checkAt options prose =
                    gate.CheckWith(
                        TestScope.resolved scopeId,
                        "reader",
                        FactNarrativePublication,
                        docOf [
                            para [
                                text prose
                                yield! ids |> List.map (fun id -> metric "Revenue" "1250" (Some id))
                            ]
                        ],
                        options
                    )
                    |> Async.RunSynchronously

                Expect.isTrue
                    (NarrativeGroundingVerdict.isGrounded (checkAt (lateStage None) "Three brands: "))
                    "three subjects"

                Expect.isFalse
                    (NarrativeGroundingVerdict.isGrounded (checkAt (lateStage None) "Four brands: "))
                    "not four"

                Expect.isFalse
                    (NarrativeGroundingVerdict.isGrounded (checkAt (firstDraft None) "Three brands: "))
                    "and not first"

                Expect.equal
                    (fixture.Gate.Check(
                        TestScope.resolved scopeId,
                        "reader",
                        FactNarrativePublication,
                        docOf [ para [ text "Three brands: " ] ]
                     )
                     |> Async.RunSynchronously
                     |> reasonsOf)
                    [ UnreferencedNumber ]
                    "the plain Check admits neither"
            }

            test "the gate seals each admission into a v2 certificate that verifies, and an edited admission does not" {
                let fixture = gateFixture ()
                let scopeId = "team-" + Guid.NewGuid().ToString "N"

                let fact =
                    assertOk fixture.Store scopeId (assertedDraft "revenue" 1250m Surfaceable)

                let options =
                    firstDraft (Some(wording 2 "Report on the top three brands by revenue."))

                let document =
                    docOf [
                        para [
                            text "Of the top three brands, Acme made "
                            metric "Revenue" "1250" (Some fact.FactId)
                        ]
                    ]

                let gate =
                    match fixture.Gate with
                    | :? IInstructedNarrativeGroundingGate as instructed -> instructed
                    | _ -> failtest "the fact gate offers the instructed check"

                let scope = TestScope.resolved scopeId

                let citations =
                    match
                        gate.CheckWith(scope, "reader", FactNarrativePublication, document, options)
                        |> Async.RunSynchronously
                    with
                    | Grounded citations -> citations
                    | Ungrounded offences -> failtestf "the document must pass: %A" offences

                let certify citations =
                    GroundingCertification.run
                        fixture.Gate
                        scope
                        "reader"
                        FactNarrativePublication
                        options
                        "narrative-1"
                        document
                        citations
                    |> Async.RunSynchronously

                match certify citations with
                | Error reason -> failtestf "a certificate must issue: %s" reason
                | Ok certificate ->
                    let sealedCert = sealedOf certificate
                    Expect.equal sealedCert.Body.Format GroundingCertificate.FormatV2 "issued as v2"
                    Expect.equal certificate.CitedFactIds [ fact.FactId ] "the chain is the Fact's"

                    let admission =
                        match sealedCert.Body.Admissions with
                        | Some [ admission ] -> admission
                        | other -> failtestf "expected one sealed admission, got %A" other

                    Expect.equal admission.Kind NarrativeCitation.InstructionKind "an instruction echo"
                    Expect.equal admission.Reference "instruction:2#summary" "cited as the gate cited it"
                    Expect.equal admission.Location "section 'summary' › element 1 › span 1" "at its location"

                    Expect.equal
                        (CertificateAdmission.attribute CertificateAdmission.WordingVersionAttribute admission)
                        (Some [ "2" ])
                        "with its wording version"

                    Expect.equal
                        (CertificateAdmission.attribute CertificateAdmission.SectionAttribute admission)
                        (Some [ "summary" ])
                        "and its section"

                    let verifier = DefaultArtefactVerifier.create fixture.Secrets

                    let verify (c: GroundingCertificate) =
                        GroundingCertificate.verify verifier c |> Async.RunSynchronously

                    Expect.equal (verify sealedCert) (Ok()) "the sealed certificate verifies offline"

                    let edited (field: string) (change: CertificateAdmission -> CertificateAdmission) =
                        field,
                        {
                            sealedCert with
                                Body = {
                                    sealedCert.Body with
                                        Admissions = Some [ change admission ]
                                }
                        }

                    for field, tampered in
                        [
                            edited "Kind" (fun a -> {
                                a with
                                    Kind = NarrativeCitation.StructuralCountKind
                            })
                            edited "Reference" (fun a -> {
                                a with
                                    Reference = "instruction:3#summary"
                            })
                            edited "Location" (fun a -> {
                                a with
                                    Location = "section 'summary' › element 2 › span 1"
                            })
                            edited "Attributes" (fun a -> { a with Attributes = [] })
                        ] do
                        Expect.equal
                            (verify tampered)
                            (Error Tampered)
                            (sprintf "an admission whose %s was edited fails verification" field)

                    let dropped = {
                        sealedCert with
                            Body = {
                                sealedCert.Body with
                                    Admissions = None
                            }
                    }

                    Expect.equal
                        (verify dropped)
                        (Error Tampered)
                        "and so does a certificate whose admission was removed"

                let factOnly =
                    citations |> List.filter (fun c -> c.ReferenceKind = NarrativeCitation.FactKind)

                match certify factOnly with
                | Error reason ->
                    Expect.stringContains reason "not the ones the verdict records" "a disagreement is refused"
                | Ok _ -> failtest "the gate must not seal admissions the verdict does not record"
            }

            test "end to end: the number the prompt asked for publishes on the first draft, listed on the certificate" {
                let definition = {
                    summaryRun with
                        Prompt = "Report on the top three brands by revenue."
                }

                let deployment =
                    deployShaped
                        definition
                        []
                        (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools)
                        (fun model -> ScriptedFactory(model) :> IAIProviderFactory)
                        (fun id rendering -> answer $"Of the top three brands, Acme made [[Revenue|{rendering}|{id}]].")

                let fact =
                    assertOk deployment.Store deployment.TeamId (assertedDraft "revenue" 1250m Surfaceable)

                match run deployment with
                | GroundedNarrativePublished(_, _, citations, _, certificate, repair) ->
                    Expect.equal repair.Rounds 0 "no repair round was needed"

                    Expect.equal
                        (citations |> List.map _.Reference)
                        [ fact.FactId; "instruction:0#summary" ]
                        "the Fact, and the instruction (the declaration is version 0)"

                    match certificate with
                    | Ok c ->
                        Expect.equal c.CitedFactIds [ fact.FactId ] "the signed chain is the Fact's"

                        let sealedBody = (sealedOf c).Body

                        Expect.equal sealedBody.Format GroundingCertificate.FormatV2 "a v2 certificate"

                        Expect.equal
                            (sealedBody.Admissions
                             |> Option.defaultValue []
                             |> List.map (fun a -> a.Kind, a.Reference, a.Location))
                            [
                                NarrativeCitation.InstructionKind,
                                "instruction:0#summary",
                                "section 'summary' › element 1 › span 1"
                            ]
                            "the admission is inside the signed body, at its location"
                    | Error reason -> failtestf "a certificate must issue: %s" reason
                | other -> failtestf "expected publication, got %s" (GroundedNarrativeOutcome.describe other)
            }

            test "end to end: a count of the table below is refused at first, and published once the repair loop stops" {
                let table =
                    NarrativeElement.Table(
                        [ "Brand", TableAlignment.Left ],
                        [ [ [ text "Acme" ] ]; [ [ text "Globex" ] ]; [ [ text "Initech" ] ] ]
                    )

                let calls = ref 0

                let writer: Writer =
                    fun id rendering ->
                        calls.Value <- calls.Value + 1

                        if calls.Value = 1 then
                            JsonSerializer.Serialize {|
                                sections = [
                                    {|
                                        id = "summary"
                                        paragraphs = [
                                            $"Acme revenue was [[Revenue|{rendering}|{id}]] in September."
                                            "The three brands are listed below."
                                        ]
                                    |}
                                ]
                            |}
                        else
                            """{"repairs":[{"section":"summary","element":2,"text":"The three brands are listed below."}]}"""

                let shaped policy =
                    summaryRun
                    |> GroundedNarrativeDefinition.withRepairPolicy policy
                    |> GroundedNarrativeDefinition.withProjection (fun _ -> async {
                        return Ok [ "summary", [ table ] ]
                    })

                // With no repair round there is no late stage: refused.
                let unrepaired =
                    deployShaped
                        (shaped GroundedRepairPolicy.refuseOnOffence)
                        []
                        (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools)
                        (fun model -> ScriptedFactory(model) :> IAIProviderFactory)
                        writer

                assertOk unrepaired.Store unrepaired.TeamId (assertedDraft "revenue" 1250m Surfaceable)
                |> ignore

                match run unrepaired with
                | GroundedNarrativeRefused offences ->
                    Expect.equal
                        (offences |> List.map _.Reason)
                        [ UnreferencedNumber ]
                        "the count, refused on the first draft"
                | other -> failtestf "expected a refusal, got %s" (GroundedNarrativeOutcome.describe other)

                calls.Value <- 0

                let repaired =
                    deployShaped
                        (shaped GroundedRepairPolicy.defaults)
                        []
                        (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools)
                        (fun model -> ScriptedFactory(model) :> IAIProviderFactory)
                        writer

                assertOk repaired.Store repaired.TeamId (assertedDraft "revenue" 1250m Surfaceable)
                |> ignore

                match run repaired with
                | GroundedNarrativePublished(_, document, citations, _, certificate, repair) ->
                    Expect.isTrue (GroundedRepairSummary.isComplete repair) "published whole, nothing withheld"
                    Expect.isTrue (repair.Rounds > 0) "after the repair loop stopped"

                    Expect.isFalse
                        (document.Sections
                         |> List.exists (fun s -> s.Id = GroundedRepair.WithheldSectionId))
                        "with no withheld-claims note"

                    Expect.contains
                        (citations |> List.map _.Reference)
                        "structural-count:section 'summary' › element 2 › span 1"
                        "the count is cited at its location"

                    match certificate with
                    | Ok c ->
                        Expect.equal
                            ((sealedOf c).Body.Admissions
                             |> Option.defaultValue []
                             |> List.map (fun a -> a.Kind, a.Location))
                            [
                                NarrativeCitation.StructuralCountKind, "section 'summary' › element 2 › span 1"
                            ]
                            "and sealed into the certificate"
                    | Error reason -> failtestf "a certificate must issue: %s" reason
                | other -> failtestf "expected publication, got %s" (GroundedNarrativeOutcome.describe other)
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
        projectionTests
        jobResultTests
        triggerNameTests
        repairTests
        admissionTests
    ]
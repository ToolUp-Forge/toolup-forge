// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DisclosureEgressTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Narrative
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.TeamManagement
open ToolUp.RAG.IngestionTypes
open ToolUp.Facts
open SharedTypes
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiNarrative
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 525 — disclosure egress filtering, first choke points ─────
//
// One predicate (`DisclosureEgress.evaluate`, Facts.Core), one gate
// (`FactDisclosureGate` over the fact store, audited), three doors:
// retrieval (denied facts absent before merge — never annotated), tool
// results (denied Metric values redacted to a policy-naming marker), and
// narrative publication (commit refused with a diagnostic naming the
// refs). Covered here: the predicate table, the gate's conservative
// resolution + audit emission + scope interplay, per-door allow/deny,
// and the plan-D17 parity guarantees (no gate / no classified facts ⇒
// byte-identical behaviour).

// ── Shared doubles ────────────────────────────────────────────────

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private noopNotifications =
    { new INotificationChannel with
        member _.Publish(_, _) = async.Return()

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()
    }

/// Preset-verdict gate double for the door tests — records its last call
/// so a test can pin the scope / principal / surface handed to it. Ids
/// absent from `verdicts` deny as `unknown-fact` (the conservative
/// contract the real gate honours).
type private PresetGate(verdicts: Map<string, FactDisclosureVerdict>) =
    let mutable lastCall: (string * string * FactEgressSurface * string list) option =
        None

    member _.LastCall = lastCall

    interface IFactDisclosureGate with
        member _.Check(scopeId, principal, surface, factIds) = async {
            lastCall <- Some(scopeId, principal, surface, factIds)

            return
                factIds
                |> List.distinct
                |> List.map (fun id ->
                    id, (verdicts.TryFind id |> Option.defaultValue (FactNotDisclosable "unknown-fact")))
                |> Map.ofList
        }

// ── Predicate (525.A) ─────────────────────────────────────────────

let private allSurfaces = [ FactRetrieval; FactToolResult; FactNarrativePublication ]

let predicateTests =
    testList "Phase 525 disclosure predicate" [

        test "Surfaceable is disclosable at every egress surface" {
            for surface in allSurfaces do
                Expect.equal
                    (DisclosureEgress.evaluate DisclosurePolicyResolver.denyUnknown surface Surfaceable)
                    FactDisclosable
                    (sprintf "Surfaceable passes at %A" surface)
        }

        test "Internal is denied at every egress surface, naming the classification" {
            for surface in allSurfaces do
                Expect.equal
                    (DisclosureEgress.evaluate DisclosurePolicyResolver.denyUnknown surface Internal)
                    (FactNotDisclosable "Internal")
                    (sprintf "Internal denied at %A" surface)
        }

        test "Restricted resolves conservatively: unknown policy ⇒ deny, naming the policy" {
            Expect.equal
                (DisclosureEgress.evaluate DisclosurePolicyResolver.denyUnknown FactRetrieval (Restricted "gdpr-k5"))
                (FactNotDisclosable "gdpr-k5")
                "an unregistered policy ref never fails open"
        }

        test "Restricted honours an affirmative policy resolver; a forbidding one still denies" {
            let permitsAtToolResult: DisclosurePolicyResolver =
                fun policyRef surface -> Some(policyRef = "licensed-derived" && surface = FactToolResult)

            Expect.equal
                (DisclosureEgress.evaluate permitsAtToolResult FactToolResult (Restricted "licensed-derived"))
                FactDisclosable
                "the named policy permits this surface"

            Expect.equal
                (DisclosureEgress.evaluate permitsAtToolResult FactRetrieval (Restricted "licensed-derived"))
                (FactNotDisclosable "licensed-derived")
                "the same policy forbids another surface"
        }

        test "refusal wording names the policy, never a value" {
            Expect.equal
                (FactDisclosureVerdict.refusalText "P")
                "computed, but not disclosable under policy P"
                "the canonical refusal wording"
        }
    ]

// ── Gate over the real fact store (525.A/E + scope interplay) ─────

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draftWith (metric: string) (disclosure: Disclosure) : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef metric
    Value = Scalar 100m
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ "hashA" ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = disclosure
}

let private newGateHarness () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    let gate = FactDisclosureGate.create store events
    store, events, gate

let private assertFact (store: IFactStore) (scope: string) (draft: FactDraft) =
    match store.Assert(scope, draft) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

let gateTests =
    testList "Phase 525 FactDisclosureGate" [

        testCaseAsync "a Surfaceable fact passes; Internal and Restricted deny, each deny audited (GP 6)"
        <| async {
            let store, events, gate = newGateHarness ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            let surfaceable = assertFact store scope (draftWith "revenue" Surfaceable)
            let internal' = assertFact store scope (draftWith "margin" Internal)

            let restricted =
                assertFact store scope (draftWith "syndicated" (Restricted "vendor-terms"))

            let! verdicts =
                gate.Check(scope, "user-1", FactRetrieval, [ surfaceable.FactId; internal'.FactId; restricted.FactId ])

            Expect.equal (verdicts.TryFind surfaceable.FactId) (Some FactDisclosable) "surfaceable passes"
            Expect.equal (verdicts.TryFind internal'.FactId) (Some(FactNotDisclosable "Internal")) "internal denied"

            Expect.equal
                (verdicts.TryFind restricted.FactId)
                (Some(FactNotDisclosable "vendor-terms"))
                "restricted denied under the conservative resolver, naming its policy"

            // Exactly one audit row per deny, under `_facts`, carrying the
            // surface / fact id / policy / principal — never the value.
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal denies.Length 2 "one deny row per denied fact"

            let payloads = denies |> List.map _.Payload |> String.concat "\n"
            Expect.stringContains payloads internal'.FactId "the internal fact id is audited"
            Expect.stringContains payloads "vendor-terms" "the policy ref is audited"
            Expect.stringContains payloads "Retrieval" "the surface is audited"
            Expect.stringContains payloads "user-1" "the principal is audited"
            Expect.isFalse (payloads.Contains "100") "the denied value never rides the audit row"
        }

        testCaseAsync "an unknown fact id denies conservatively as unknown-fact"
        <| async {
            let _, _, gate = newGateHarness ()
            let! verdicts = gate.Check("team-x", "user-1", FactToolResult, [ "no-such-fact" ])

            Expect.equal
                (verdicts.TryFind "no-such-fact")
                (Some(FactNotDisclosable "unknown-fact"))
                "an unresolvable id never fails open"
        }

        testCaseAsync
            "disclosure never widens scope: a Surfaceable fact from another scope is denied (unresolvable in the caller's shard)"
        <| async {
            let store, _, gate = newGateHarness ()
            let scopeA = "team-" + Guid.NewGuid().ToString("N")
            let scopeB = "team-" + Guid.NewGuid().ToString("N")

            let fact = assertFact store scopeA (draftWith "revenue" Surfaceable)

            let! ownScope = gate.Check(scopeA, "alice", FactRetrieval, [ fact.FactId ])
            Expect.equal (ownScope.TryFind fact.FactId) (Some FactDisclosable) "disclosable in its own scope"

            let! otherScope = gate.Check(scopeB, "bob", FactRetrieval, [ fact.FactId ])

            Expect.equal
                (otherScope.TryFind fact.FactId)
                (Some(FactNotDisclosable "unknown-fact"))
                "Surfaceable never overrides the scope boundary (GP 4)"
        }

        testCaseAsync "scope never overrides a deny: an Internal fact is denied even in its own scope"
        <| async {
            let store, _, gate = newGateHarness ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let fact = assertFact store scope (draftWith "margin" Internal)

            let! verdicts = gate.Check(scope, "owner-of-the-scope", FactNarrativePublication, [ fact.FactId ])

            Expect.equal
                (verdicts.TryFind fact.FactId)
                (Some(FactNotDisclosable "Internal"))
                "being in-scope grants access for computation, never egress"
        }
    ]

// ── Retrieval choke point (525.B) ─────────────────────────────────

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

type private ConstantEmbedder() =
    interface IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts =
            batchedFallback (fun _ -> async { return unitVec }) texts

        member _.Dimensions = 8
        member _.ProviderId = "test"
        member _.ModelId = "constant-v1"

type private FixedResolver(facts: ResolvedFact list) =
    interface IFactResolver with
        member _.Resolve(_, _) = async { return facts }

let private newVectorStore () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-disclosure-egress-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(storage, logger = noopLogger, flushIntervalMs = 60000)

    store :> IVectorStore, store :> IDisposable

let private resolvedFact (id: string) : ResolvedFact = {
    FactId = id
    Rendering = "£21,800"
    Freshness = FactFresh
    SupersededBy = None
    Metric = "revenue"
}

let private sampleClause: FactClause = {
    SubjectHierarchy = "brand"
    SubjectPath = [ "acme" ]
    Metric = "revenue"
    PeriodFrom = None
    PeriodTo = None
    AsOf = None
}

let private clausedRequest = {
    RetrievalRequest.create "q" [ User "u" ] 10 Interleaved with
        FactClause = Some sampleClause
}

let private factIdsIn (results: VectorMatch list) =
    results |> List.choose (fun m -> m.Metadata.TryFind ChunkMetadata.FactIdKey)

let retrievalTests =
    testList "Phase 525 retrieval choke point" [

        testCaseAsync "a denied fact is absent from the results — not annotated; permitted facts still lead"
        <| async {
            let store, dispose = newVectorStore ()

            try
                do!
                    store.Upsert (User "u") "c1" unitVec {
                        Content = "a chunk"
                        Metadata = Map.empty
                    }

                let resolver =
                    FixedResolver [ resolvedFact "fact-open"; resolvedFact "fact-secret" ]

                let gate =
                    PresetGate(
                        Map.ofList [ "fact-open", FactDisclosable; "fact-secret", FactNotDisclosable "Internal" ]
                    )

                let pipeline =
                    new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
                        store,
                        ConstantEmbedder(),
                        factResolver = FixedResolver [ resolvedFact "fact-open"; resolvedFact "fact-secret" ],
                        disclosureGate = gate
                    )
                    :> IRetrievalPipeline

                ignore resolver
                let! results = pipeline.Retrieve clausedRequest (AccessContext.unrestricted (AuthenticatedUser "u"))

                Expect.equal (factIdsIn results) [ "fact-open" ] "only the disclosable fact surfaces"

                Expect.isFalse
                    (results
                     |> List.exists (fun m -> m.Content.Contains "£21,800" && factIdsIn [ m ] = [ "fact-secret" ]))
                    "the denied fact's rendering is nowhere in the result set"

                Expect.equal
                    (results |> List.map _.ChunkId)
                    [ "fact:fact-open"; "c1" ]
                    "the permitted fact still merges ahead of the chunks"

                match gate.LastCall with
                | Some(scopeId, principal, surface, ids) ->
                    Expect.equal scopeId "u" "the gate is handed the caller's own fact scope"
                    Expect.equal principal "u" "the principal is the authenticated caller"
                    Expect.equal surface FactRetrieval "checked at the retrieval surface"
                    Expect.equal (List.sort ids) [ "fact-open"; "fact-secret" ] "every resolved fact is checked"
                | None -> failtest "the gate was never consulted"
            finally
                dispose.Dispose()
        }

        testCaseAsync "no gate wired ⇒ resolved facts pass through unchanged (GP 11)"
        <| async {
            let store, dispose = newVectorStore ()

            try
                let pipeline =
                    new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
                        store,
                        ConstantEmbedder(),
                        factResolver = FixedResolver [ resolvedFact "fact-open" ]
                    )
                    :> IRetrievalPipeline

                let! results = pipeline.Retrieve clausedRequest (AccessContext.unrestricted (AuthenticatedUser "u"))
                Expect.equal (factIdsIn results) [ "fact-open" ] "pass-through without a gate"
            finally
                dispose.Dispose()
        }

        testCaseAsync "gate wired + only disclosable facts ⇒ results identical to the gate-less pipeline (plan D17)"
        <| async {
            let store, dispose = newVectorStore ()

            try
                do!
                    store.Upsert (User "u") "c1" unitVec {
                        Content = "a chunk"
                        Metadata = Map.empty
                    }

                let mkPipeline (gate: IFactDisclosureGate option) =
                    match gate with
                    | Some g ->
                        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
                            store,
                            ConstantEmbedder(),
                            factResolver = FixedResolver [ resolvedFact "fact-open" ],
                            disclosureGate = g
                        )
                        :> IRetrievalPipeline
                    | None ->
                        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
                            store,
                            ConstantEmbedder(),
                            factResolver = FixedResolver [ resolvedFact "fact-open" ]
                        )
                        :> IRetrievalPipeline

                let ctx = AccessContext.unrestricted (AuthenticatedUser "u")

                let gate =
                    PresetGate(Map.ofList [ "fact-open", FactDisclosable ]) :> IFactDisclosureGate

                let! gated = (mkPipeline (Some gate)).Retrieve clausedRequest ctx
                let! ungated = (mkPipeline None).Retrieve clausedRequest ctx

                Expect.equal gated ungated "an all-Surfaceable deployment is unchanged by the gate"
            finally
                dispose.Dispose()
        }
    ]

// ── Tool-result redaction (525.C — the pure walk) ─────────────────

let private docOf (elements: NarrativeElement list) : NarrativeDocument = {
    Title = "T"
    Subtitle = None
    Sections = [
        {
            Id = "s"
            Heading = "H"
            Subheading = None
            Elements = elements
        }
    ]
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

let redactionTests =
    testList "Phase 525 tool-result redaction" [

        test "a denied Metric value is replaced by the policy-naming marker; label and ref survive" {
            let doc = docOf [ Paragraph [ Metric("margin", "12%", Some "fact-secret") ] ]

            let redacted =
                NarrativeFacts.redactDeniedFacts (Map.ofList [ "fact-secret", "Internal" ]) doc

            Expect.equal
                redacted.Sections[0].Elements
                [
                    Paragraph [
                        Metric("margin", "[not disclosable under policy Internal]", Some "fact-secret")
                    ]
                ]
                "the value is gone; the label, ref and structure remain"

            let markdown = NarrativeMarkdown.render redacted
            Expect.isFalse (markdown.Contains "12%") "the denied value never reaches the rendered markdown"
            Expect.stringContains markdown "not disclosable under policy Internal" "the marker names the policy"
        }

        test "redaction reaches nested bodies (cards, links, tables); undenied spans are untouched" {
            let doc =
                docOf [
                    Card {
                        Heading = Some "K"
                        Image = None
                        Body = [ Paragraph [ Link("/x", [ Metric("m", "1", Some "fact-secret") ]) ] ]
                    }
                    Table([ "Metric", TableAlignment.Left ], [ [ [ Metric("open", "2", Some "fact-open") ] ] ])
                ]

            let redacted =
                NarrativeFacts.redactDeniedFacts (Map.ofList [ "fact-secret", "P1" ]) doc

            let markdown = NarrativeMarkdown.render redacted
            Expect.isFalse (markdown.Contains "**m** 1") "the nested denied value is redacted"
            Expect.stringContains markdown "not disclosable under policy P1" "the nested marker renders"
            Expect.stringContains markdown "2" "an undenied span is untouched"
        }

        test "a document citing none of the denied ids is structurally unchanged" {
            let doc = docOf [ Paragraph [ Metric("open", "2", Some "fact-open") ] ]

            Expect.equal
                (NarrativeFacts.redactDeniedFacts (Map.ofList [ "fact-other", "P" ]) doc)
                doc
                "no cited denial ⇒ identity"

            Expect.equal (NarrativeFacts.redactDeniedFacts Map.empty doc) doc "empty denial set ⇒ identity"
        }
    ]

// ── Narrative-commit choke point (525.D) ──────────────────────────

let private mkKbDeps (storage: IBlobStorage) (gate: IFactDisclosureGate option) (container: string) : KnowledgeApiDeps = {
    Storage = storage
    Queue = IngestionQueue()
    OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
    TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
    Notifications = noopNotifications
    Logger = noopLogger
    Scope = {
        ScopeId = container
        Container = container
        Persist = true
    }
    UserId = "user-1"
    VectorScope = Deployment
    VectorStore = None
    IndexLifecycle = None
    EventStore = None
    EmbeddingProvider = None
    NarrativeStore = None
    AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
    OriginalResolver = KnowledgeBase.ServerOriginalSourceResolver.createDefault ()
    AuditLog = None
    RecordEnqueue = ignore
    PublishInventory = fun () -> async.Return()
    MarkIngestionFailed = fun _ _ _ -> async.Return()
    EnsureContextWriteAllowed = fun () -> async { return Ok() }
    ScopeResolvedFromRequest = true
    UploadPolicy = KnowledgeUploadPolicy.permissive
    DedupPolicy = KnowledgeDedupPolicy.enabled
    // Phase 510 — this pack pins the pre-510 upload path; versioning off.
    VersioningPolicy = KnowledgeVersioningPolicy.disabled
    // Phase 105 — retention not composed: the pre-105 convention-blob path.
    DataObjectStore = None
    // Phase 512 — these packs pin pre-512 paths; the unlimited /
    // retain-forever defaults keep them byte-identical.
    QuotaPolicy = KnowledgeQuotaPolicy.unlimited
    RetentionPolicy = KnowledgeRetentionPolicy.retainForever
    // Phase 515 — no scanner composed: the pre-515 upload path.
    ContentScanner = None
    ScanPolicy = ContentScanPolicy.defaults
    DisclosureGate = gate
    // Phase 511 — bulk import is not exercised here: archive guards at
    // their shipped defaults, URL ingestion inert, no transport.
    ArchiveImportPolicy = ArchiveImportPolicy.defaults
    UrlIngestionPolicy = UrlIngestionPolicy.disabled
    UrlFetcher = None
}

let private narrativeRequest (factRef: string option) : IngestNarrativeRequest = {
    Document = {
        Title = "Quarterly summary"
        Subtitle = None
        Sections = [
            {
                Id = "s1"
                Heading = "Results"
                Subheading = None
                Elements = [ Paragraph [ Text "Margin was "; Metric("margin", "12%", factRef) ] ]
            }
        ]
        Provenance =
            Some {
                ModuleId = "ModA"
                PageRoute = None
                GeneratedAt = DateTimeOffset.UtcNow
                SettingsKey = "k"
                SettingsDisplay = []
            }
        Lang = None
        CanonicalUrl = None
    }
    Overwrite = false
}

let narrativeCommitTests =
    testList "Phase 525 narrative-commit choke point" [

        testCaseAsync "a narrative referencing a denied fact is refused, naming the offending ref + policy"
        <| async {
            let gate = PresetGate(Map.ofList [ "fact-secret", FactNotDisclosable "Internal" ])

            let deps =
                mkKbDeps (InMemoryBlobStorage()) (Some(gate :> IFactDisclosureGate)) "team-a"

            let! result = ingestNarrative deps (narrativeRequest (Some "fact-secret"))

            match result with
            | Error(IngestFailed reason) ->
                Expect.stringContains reason "fact-secret" "the diagnostic names the offending ref"
                Expect.stringContains reason "policy Internal" "the diagnostic names the policy"
                Expect.isFalse (reason.Contains "12%") "the diagnostic never carries the value"
            | other -> failtestf "expected a disclosure refusal, got %A" other

            match gate.LastCall with
            | Some(scopeId, principal, surface, _) ->
                Expect.equal scopeId "team-a" "checked in the committing scope"
                Expect.equal principal "user-1" "the committing principal is audited"
                Expect.equal surface FactNarrativePublication "checked at the publication surface"
            | None -> failtest "the gate was never consulted"
        }

        testCaseAsync "a narrative whose facts are all disclosable commits normally through the gate"
        <| async {
            let gate = PresetGate(Map.ofList [ "fact-open", FactDisclosable ])

            let deps =
                mkKbDeps (InMemoryBlobStorage()) (Some(gate :> IFactDisclosureGate)) "team-b"

            let! result = ingestNarrative deps (narrativeRequest (Some "fact-open"))

            match result with
            | Ok doc -> Expect.equal doc.FileType "narrative" "committed as a narrative document"
            | Error e -> failtestf "expected a successful commit, got %A" e
        }

        testCaseAsync "no gate composed ⇒ the commit path is unchanged (GP 13); fact-less docs never consult the gate"
        <| async {
            // No gate: a fact-referencing narrative commits exactly as today.
            let ungated = mkKbDeps (InMemoryBlobStorage()) None "team-c"
            let! withoutGate = ingestNarrative ungated (narrativeRequest (Some "fact-secret"))
            Expect.isOk withoutGate "no gate ⇒ byte-identical commit path"

            // Gate present but the document cites no facts: never consulted.
            let gate = PresetGate(Map.empty)

            let gated =
                mkKbDeps (InMemoryBlobStorage()) (Some(gate :> IFactDisclosureGate)) "team-d"

            let! withoutRefs = ingestNarrative gated (narrativeRequest None)
            Expect.isOk withoutRefs "a fact-less narrative commits"
            Expect.isNone gate.LastCall "no fact refs ⇒ the gate is never consulted (plan D17)"
        }
    ]

// ── Purpose-bound disclosure — the declared-why facet (Phase 592) ─────

let private purposeConfig: DisclosurePurposeConfig = {
    Taxonomy = {
        Version = "v1"
        Purposes = [
            {
                PurposeId = "analytics"
                Description = "Internal analytical reporting"
            }
            {
                PurposeId = "billing"
                Description = "Invoice preparation"
            }
        ]
    }
    AllowedBySurface = Map.ofList [ FactRetrieval, [ "analytics" ]; FactToolResult, [ "analytics"; "billing" ] ]
}

let private newPurposeGateHarness () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events

    let gate =
        FactDisclosureGate.createConfigured None (Some purposeConfig) store events

    store, events, gate

/// Run `body` with the ambient purpose claim set (cleared afterwards, so
/// no claim leaks across tests sharing the async context).
let private withClaim (purpose: string option) (body: Async<unit>) : Async<unit> = async {
    match purpose with
    | Some p -> FactPurposeContext.claim p
    | None -> FactPurposeContext.clear ()

    try
        do! body
    finally
        FactPurposeContext.clear ()
}

let purposeTests =
    testList "Phase 592 purpose-bound disclosure" [

        testCaseAsync "no taxonomy declared ⇒ the facet is absent — no claim needed, behaviour byte-for-byte 525"
        <| async {
            // The plain harness (no purpose config): a Surfaceable fact
            // discloses with no claim set, exactly the pre-592 gate.
            let store, _, gate = newGateHarness ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let fact = assertFact store scope (draftWith "revenue" Surfaceable)

            do!
                withClaim
                    None
                    (async {
                        let! verdicts = gate.Check(scope, "user-1", FactRetrieval, [ fact.FactId ])
                        Expect.equal (verdicts.TryFind fact.FactId) (Some FactDisclosable) "inert without a taxonomy"
                    })
        }

        testCaseAsync "an in-set claim passes and a co-checked deny stamps the purpose + taxonomy version (GP 6)"
        <| async {
            let store, events, gate = newPurposeGateHarness ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let surfaceable = assertFact store scope (draftWith "revenue" Surfaceable)
            let internal' = assertFact store scope (draftWith "margin" Internal)

            do!
                withClaim
                    (Some "analytics")
                    (async {
                        let! verdicts =
                            gate.Check(scope, "user-1", FactRetrieval, [ surfaceable.FactId; internal'.FactId ])

                        Expect.equal
                            (verdicts.TryFind surfaceable.FactId)
                            (Some FactDisclosable)
                            "an in-set claim leaves the 525 verdict in charge"

                        Expect.equal
                            (verdicts.TryFind internal'.FactId)
                            (Some(FactNotDisclosable "Internal"))
                            "the classification deny stands on its own policy ref"

                        let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

                        let denies =
                            rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

                        Expect.equal denies.Length 1 "one deny row (the Internal fact)"
                        let payload = denies |> List.map _.Payload |> String.concat "\n"
                        Expect.stringContains payload "analytics" "the claimed purpose is stamped into the deny"
                        Expect.stringContains payload "\"v1\"" "the taxonomy version is stamped"
                    })
        }

        testCaseAsync "an out-of-set claim refuses every fact, enumerating the allowed set"
        <| async {
            let store, events, gate = newPurposeGateHarness ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let fact = assertFact store scope (draftWith "revenue" Surfaceable)

            // `billing` is allowed at ToolResult but NOT at Retrieval.
            do!
                withClaim
                    (Some "billing")
                    (async {
                        let! verdicts = gate.Check(scope, "user-1", FactRetrieval, [ fact.FactId ])

                        match verdicts.TryFind fact.FactId with
                        | Some(FactNotDisclosable policyRef) ->
                            Expect.stringContains policyRef "purpose-denied:billing" "the refusal names the claim"
                            Expect.stringContains policyRef "analytics" "the refusal enumerates the allowed set"
                        | other -> failtestf "expected a purpose refusal, got %A" other

                        // The same claim IS in ToolResult's allowed set — per-route.
                        let! atToolResult = gate.Check(scope, "user-1", FactToolResult, [ fact.FactId ])

                        Expect.equal
                            (atToolResult.TryFind fact.FactId)
                            (Some FactDisclosable)
                            "the allowed sets are per-surface (per-route)"

                        let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

                        let denies =
                            rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

                        let payload = denies |> List.map _.Payload |> String.concat "\n"
                        Expect.stringContains payload "billing" "the refused claim is stamped into the audit row"
                    })
        }

        testCaseAsync "a missing claim refuses (default-deny-by-shape), and a surface absent from the map serves none"
        <| async {
            let store, _, gate = newPurposeGateHarness ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let fact = assertFact store scope (draftWith "revenue" Surfaceable)

            do!
                withClaim
                    None
                    (async {
                        let! unclaimed = gate.Check(scope, "user-1", FactRetrieval, [ fact.FactId ])

                        match unclaimed.TryFind fact.FactId with
                        | Some(FactNotDisclosable policyRef) ->
                            Expect.stringContains policyRef "purpose-unclaimed" "a missing claim never passes"
                        | other -> failtestf "expected a purpose refusal, got %A" other
                    })

            // NarrativePublication is absent from `AllowedBySurface` — it
            // serves no purpose, so even a declared purpose refuses.
            do!
                withClaim
                    (Some "analytics")
                    (async {
                        let! sealed' = gate.Check(scope, "user-1", FactNarrativePublication, [ fact.FactId ])

                        match sealed'.TryFind fact.FactId with
                        | Some(FactNotDisclosable policyRef) ->
                            Expect.stringContains policyRef "allowed: none" "an undeclared surface is fully sealed"
                        | other -> failtestf "expected a purpose refusal, got %A" other
                    })
        }
    ]

let tests =
    testList "Phase 525 disclosure egress" [
        predicateTests
        gateTests
        retrievalTests
        redactionTests
        narrativeCommitTests
        purposeTests
    ]
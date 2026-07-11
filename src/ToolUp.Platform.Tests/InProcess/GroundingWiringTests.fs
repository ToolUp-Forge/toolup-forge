module ToolUp.Platform.Tests.InProcess.GroundingWiringTests

open System
open System.Collections.Concurrent
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts

// ─── Grounding-plane wiring follow-ups ───────────────────────────────
//
// Covers the three post-batch tidy-ups: FactsCompose.withFactStore
// (config-driven IFactStore DI registration), FactStoreEvidenceSource
// (the IFactStore -> IFactEvidenceSource adapter), and the grounding
// registrations folded into the ConfigDriftDetector snapshot.

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draftWith resultRef inputHashes value : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef "revenue"
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = resultRef
        InputHashes = inputHashes
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Disclosure.Surfaceable
}

/// Capturing IAuditLog for the drift test.
type private CapturingAuditLog() =
    let events = ConcurrentQueue<string * AuditEvent>()
    member _.Events = events |> Seq.map snd |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { events.Enqueue(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return events |> Seq.map snd |> Seq.toList }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let tests =
    testList "Grounding-plane wiring (tidy-ups)" [

        // ─── FactsCompose.withFactStore (item 1) ──────────────────────

        test "withFactStore registers IFactStore + IFactEvidenceSource when EnabledFactStore" {
            let app =
                {
                    ServerApp.empty with
                        Config = {
                            ServerConfig.defaults with
                                FactStore = EnabledFactStore
                        }
                }
                |> FactsCompose.withFactStore

            let services = ServiceCollection()

            services.AddSingleton<IBlobStorage>(InMemoryBlobStorage.InMemoryBlobStorage())
            |> ignore

            services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
            |> ignore

            match app.Extensions.ServiceConfig with
            | Some cfg -> cfg services |> ignore
            | None -> failtest "EnabledFactStore should append a service registration"

            let sp = services.BuildServiceProvider()
            Expect.isFalse (isNull (box (sp.GetService<IFactStore>()))) "IFactStore resolves from DI"
            Expect.isFalse (isNull (box (sp.GetService<IFactEvidenceSource>()))) "IFactEvidenceSource resolves from DI"
        }

        test "withFactStore is a no-op under NoFactStore (byte-identical)" {
            let before = ServerApp.empty
            let after = before |> FactsCompose.withFactStore
            Expect.isTrue after.Extensions.ServiceConfig.IsNone "no service registration under NoFactStore"
            Expect.isTrue (Object.ReferenceEquals(after, before)) "the app is returned unchanged"
        }

        // ─── FactStoreEvidenceSource (item 2) ─────────────────────────

        testCaseAsync "FactStoreEvidenceSource projects a stored fact onto FactEvidence"
        <| async {
            let store =
                BlobFactStore.create
                    (InMemoryBlobStorage.InMemoryBlobStorage())
                    (InMemoryEventStore.InMemoryEventStore())

            let scope = newScope ()
            let! asserted = store.Assert(scope, draftWith (Some "res-1") [ "obj-1" ] 100m)

            let fact =
                match asserted with
                | Ok f -> f
                | Error e -> failtestf "assert: %s" e

            let src = FactStoreEvidenceSource.create store

            let! ev = src.GetFact(scope, fact.FactId)

            match ev with
            | Some e ->
                Expect.equal e.FactId fact.FactId "fact id"
                Expect.equal e.ResultRef (Some "res-1") "result evidence ref"
                Expect.equal e.InputHashes [ "obj-1" ] "input hashes"
                Expect.equal e.Disclosure "Surfaceable" "disclosure rendered"
                Expect.equal e.Subject "geography/uk" "subject rendered"
            | None -> failtest "expected evidence for the stored fact"

            let! forResult = src.FactsForResult(scope, "res-1")
            Expect.equal (forResult |> List.map _.FactId) [ fact.FactId ] "fact found by its result ref"

            let! none = src.FactsForResult(scope, "res-other")
            Expect.isEmpty none "no facts for an unrelated result"
        }

        // ─── ConfigDriftDetector grounding parity (item 3) ────────────

        testCaseAsync "ConfigDriftDetector reports a grounding-registration change as drift"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let config = ServerConfig.defaults

            // First run seeds the snapshot (silent by design).
            do! ConfigDriftDetector.run blob audit silentLogger config [ "metric:revenue" ]
            // Second run: a subject registration was added.
            do! ConfigDriftDetector.run blob audit silentLogger config [ "metric:revenue"; "subject:geography" ]

            let driftPaths =
                audit.Events
                |> List.choose (fun e ->
                    match e with
                    | ConfigDrift p -> Some p
                    | _ -> None)
                |> List.collect (fun p -> p.Changes |> List.map _.Path)

            Expect.exists driftPaths (fun path -> path.Contains "_grounding") "the grounding change surfaced as drift"
        }

        testCaseAsync "identical grounding registrations produce no drift"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let config = ServerConfig.defaults

            do! ConfigDriftDetector.run blob audit silentLogger config [ "metric:revenue" ]
            do! ConfigDriftDetector.run blob audit silentLogger config [ "metric:revenue" ]

            let drifts =
                audit.Events
                |> List.filter (fun e ->
                    match e with
                    | ConfigDrift _ -> true
                    | _ -> false)

            Expect.isEmpty drifts "no drift when nothing changed"
        }
    ]
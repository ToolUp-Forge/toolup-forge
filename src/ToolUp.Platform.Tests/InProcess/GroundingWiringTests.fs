module ToolUp.Platform.Tests.InProcess.GroundingWiringTests

// One test holds an obsolete setter to the field it names, for the
// deprecation window; nothing else here calls an obsolete member.
#nowarn "44"

open System
open System.Collections.Concurrent
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open Fuaran.Facts
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.KernelFixtures
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Config
open ToolUp.Platform.Compose

// ─── Grounding-plane wiring follow-ups ───────────────────────────────
//
// Covers the three post-batch tidy-ups: FactsCompose.withOptions
// (config-driven IFactStore DI registration), FactStoreEvidenceSource
// (the IFactStore -> IFactEvidenceSource adapter), and the grounding
// registrations folded into the ConfigDriftDetector snapshot.

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent =
    period
        (DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc))
        (DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc))
        (Some "Q2-2026")

let private draftWith resultRef inputHashes value : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef "revenue"
    Value = scalar value
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
        member _.Record(AuditScope.StreamKey scopeId, _actor, audit) = async {
            events.Enqueue(scopeId, audit)
            return Ok()
        }

        member _.GetAuditTrail(AuditScope.StreamKey _, _, _) = async { return events |> Seq.map snd |> Seq.toList }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// ─── FactsComposeOptions: one record, refused once ──────────────────

let private enabledApp () = {
    ServerApp.empty with
        Config = {
            ServerConfig.defaults with
                Stores = {
                    ServerConfig.defaults.Stores with
                        FactStore = FeatureMode.Enabled
                }
        }
}

let private fieldsOf (refusals: CompositionRefusal list) =
    refusals
    |> List.map (fun refusal ->
        match refusal with
        | CompositionRefusal.MissingDependency(field, _, _)
        | CompositionRefusal.InvalidDeclaration(field, _)
        | CompositionRefusal.UnsupportedImplementation(field, _, _) -> field)

let private noRuns: string -> string list = fun _ -> []

let private optionsTests =
    testList "FactsComposeOptions — one record, refused once" [

        test "the defaults are refused for nothing, and compose the store alone" {
            Expect.isEmpty (FactsComposeOptions.refusals FactsComposeOptions.defaults) "nothing to refuse"

            let app = FactsCompose.withOptions FactsComposeOptions.defaults (enabledApp ())

            Expect.equal
                (app.AITools |> List.map (fun (d, _) -> d.Name))
                (NarrativeGrounding.factTools |> List.map (fun (d, _) -> d.Name))
                "the fact tools, and nothing on top of them"

            Expect.isEmpty app.FactTableBindings "no writer, no table bound"
        }

        test "a trigger with no table writer is refused by its field; a writer from either field admits it" {
            let delivery = {
                FactsComposeOptions.defaults with
                    DeliveryTrigger = Some noRuns
            }

            Expect.equal
                (fieldsOf (FactsComposeOptions.refusals delivery))
                [ "FactsComposeOptions.DeliveryTrigger" ]
                "the delivery trigger names itself"

            Expect.isEmpty
                (FactsComposeOptions.refusals { delivery with FactTableWriter = true })
                "the default writer admits it"

            Expect.isEmpty
                (FactsComposeOptions.refusals {
                    delivery with
                        DelegateFacts = [ "sku-sales" ]
                })
                "the delegate writer admits it"
        }

        test "a malformed declaration is refused by its field" {
            let visibility = {
                FactsComposeOptions.defaults with
                    TeamOutputVisibility = Some(TeamOutputVisibilityDeclaration.create TeamAdmins [ TeamVisible ])
            }

            Expect.equal
                (fieldsOf (FactsComposeOptions.refusals visibility))
                [ "FactsComposeOptions.TeamOutputVisibility" ]
                "a default outside the allowed set"
        }

        test "withOptions refuses the whole record once, before anything composes, naming the first field at fault" {
            let contradictory = {
                FactsComposeOptions.defaults with
                    DeliveryTrigger = Some noRuns
                    TeamOutputVisibility = Some(TeamOutputVisibilityDeclaration.create TeamAdmins [])
            }

            Expect.equal
                (fieldsOf (FactsComposeOptions.refusals contradictory))
                [
                    "FactsComposeOptions.TeamOutputVisibility"
                    "FactsComposeOptions.DeliveryTrigger"
                ]
                "every refusal, in the order they are checked"

            match
                (try
                    FactsCompose.withOptions contradictory (enabledApp ()) |> ignore
                    None
                 with CompositionRefused refusal ->
                     Some refusal)
            with
            | Some(CompositionRefusal.InvalidDeclaration(field, _)) ->
                Expect.equal field "FactsComposeOptions.TeamOutputVisibility" "the first refusal is raised"
            | other -> failtestf "expected one typed refusal, got %A" other

            Expect.isTrue
                (Object.ReferenceEquals(FactsCompose.withOptions contradictory ServerApp.empty, ServerApp.empty))
                "a deployment without the fact store composes nothing, and so refuses nothing (GP 11)"
        }

        test "a writer an earlier withOptions composed admits a later trigger, and the store composes once" {
            let first =
                FactsCompose.withOptions
                    {
                        FactsComposeOptions.defaults with
                            FactTableWriter = true
                    }
                    (enabledApp ())

            let second =
                FactsCompose.withOptions
                    {
                        FactsComposeOptions.defaults with
                            DeliveryTrigger = Some noRuns
                    }
                    first

            Expect.equal second.AITools.Length first.AITools.Length "the store's tools are declared once"

            Expect.equal second.FactTableBindings first.FactTableBindings "the writer is bound once"
        }

        test "an obsolete setter is the one field it names" {
            let viaSetter = FactsCompose.withFactTableWriter (enabledApp ())

            let viaRecord =
                FactsCompose.withOptions
                    {
                        FactsComposeOptions.defaults with
                            FactTableWriter = true
                    }
                    (enabledApp ())

            Expect.equal viaSetter.FactTableBindings viaRecord.FactTableBindings "the same binding"

            Expect.equal
                (viaSetter.AITools |> List.map (fun (d, _) -> d.Name))
                (viaRecord.AITools |> List.map (fun (d, _) -> d.Name))
                "the same tools"

            Expect.equal viaSetter.Contributions.Length viaRecord.Contributions.Length "the same contributions"
        }
    ]

let tests =
    testList "Grounding-plane wiring (tidy-ups)" [

        // ─── FactsCompose.withOptions (item 1) ────────────────────────

        test "withOptions registers IFactStore + IFactEvidenceSource when FeatureMode.Enabled" {
            let app =
                {
                    ServerApp.empty with
                        Config = {
                            ServerConfig.defaults with
                                Stores = {
                                    ServerConfig.defaults.Stores with
                                        FactStore = FeatureMode.Enabled
                                }
                        }
                }
                |> FactsCompose.withOptions FactsComposeOptions.defaults

            let services = ServiceCollection()

            services.AddSingleton<IBlobStorage>(InMemoryBlobStorage.InMemoryBlobStorage())
            |> ignore

            services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
            |> ignore

            match ServerApp.serviceConfig app with
            | Some cfg -> cfg services |> ignore
            | None -> failtest "FeatureMode.Enabled should append a service registration"

            let sp = services.BuildServiceProvider()
            Expect.isFalse (isNull (box (sp.GetService<IFactStore>()))) "IFactStore resolves from DI"
            Expect.isFalse (isNull (box (sp.GetService<IFactEvidenceSource>()))) "IFactEvidenceSource resolves from DI"
        }

        test "withOptions is a no-op under FeatureMode.Disabled (byte-identical)" {
            let before = ServerApp.empty
            let after = before |> FactsCompose.withOptions FactsComposeOptions.defaults
            Expect.isTrue (ServerApp.serviceConfig after).IsNone "no service registration under FeatureMode.Disabled"
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
            let! asserted = store.Assert(TestScope.resolved scope, draftWith (Some "res-1") [ "obj-1" ] 100m)

            let fact =
                match asserted with
                | Ok f -> f
                | Error e -> failtestf "assert: %s" (FactStoreError.describe e)

            let src = FactStoreEvidenceSource.create store

            let! ev = src.GetFact(TestScope.resolved scope, fact.FactId)

            match ev with
            | Some e ->
                Expect.equal e.FactId fact.FactId "fact id"
                Expect.equal e.ResultRef (Some "res-1") "result evidence ref"
                Expect.equal e.InputHashes [ "obj-1" ] "input hashes"
                Expect.equal e.Disclosure "Surfaceable" "disclosure rendered"
                Expect.equal e.Subject "geography/uk" "subject rendered"
            | None -> failtest "expected evidence for the stored fact"

            let! forResult = src.FactsForResult(TestScope.resolved scope, "res-1")
            Expect.equal (forResult |> List.map _.FactId) [ fact.FactId ] "fact found by its result ref"

            let! none = src.FactsForResult(TestScope.resolved scope, "res-other")
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

        optionsTests
    ]
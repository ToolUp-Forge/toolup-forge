module ToolUp.Platform.Tests.InProcess.CompositionGroundingTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Grounding

// ─── Phase 526 — composition introspection covers grounding ──────────
//
// The composition manifest (what is composed) reports registered metric /
// subject ids + the resolved fact-store kind; the composable-surface
// descriptor (what is composable) enumerates the fact-store slot,
// registration points, and disclosure defaults. A grounding-free
// composition reports the surfaces as available-uncomposed (descriptor)
// and absent (manifest), and is otherwise byte-identical.

let private metric id : MetricDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Unit = "count"
    Dimensionality = "count"
    Direction = HigherIsBetter
    DisplayFormat = "N0"
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private subject id : SubjectDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Levels = [ "root" ]
    Calendar = None
}

let tests =
    testList "Composition introspection — grounding (Phase 526)" [

        test "manifest reports registered metric / subject ids per module + the fact-store knob" {
            let sales =
                ServerModule.create "sales"
                |> ServerModule.declareMetrics [ metric "revenue"; metric "margin" ]
                |> ServerModule.declareSubjects [ subject "product" ]

            let app =
                {
                    ServerApp.empty with
                        Config = {
                            ServerConfig.defaults with
                                FactStore = EnabledFactStore
                        }
                }
                |> ServerApp.addModules [ sales ]

            let m = ServerApp.compositionManifest app

            let metricIds = m.Metrics |> List.map _.Label |> List.sort
            Expect.equal metricIds [ "margin"; "revenue" ] "both metrics reported"
            Expect.equal (m.Subjects |> List.map _.Label) [ "product" ] "subject reported"

            // ids are ComponentId-namespaced (rename-safe, collision-free).
            Expect.equal
                (m.Metrics |> List.map _.Id)
                (m.Metrics |> List.map (fun e -> ComponentId.forMetric e.Label))
                "metric entries keyed by ComponentId.forMetric"

            let factKnob = m.ConfigKnobs |> List.tryFind (fun k -> k.Name = "FactStore")
            Expect.equal (factKnob |> Option.map _.Value) (Some "EnabledFactStore") "resolved fact-store kind reported"
        }

        test "a grounding-free composition reports metrics/subjects absent + NoFactStore" {
            let m = ServerApp.compositionManifest ServerApp.empty

            Expect.isEmpty m.Metrics "no metrics composed"
            Expect.isEmpty m.Subjects "no subjects composed"

            let factKnob = m.ConfigKnobs |> List.tryFind (fun k -> k.Name = "FactStore")
            Expect.equal (factKnob |> Option.map _.Value) (Some "NoFactStore") "default fact-store kind is NoFactStore"
        }

        test "the composable-surface descriptor enumerates the grounding surface (available-uncomposed)" {
            let surface = ComposableSurface.describe ()
            let g = surface.Grounding

            Expect.equal g.FactStoreInterface "IFactStore" "fact-store slot interface named"
            Expect.equal g.FactStoreSlot (ComponentId.forCompanionSlot "IFactStore") "fact-store slot id stable"
            Expect.contains g.FactStoreModes "NoFactStore" "the default fact-store mode is available"
            Expect.contains g.FactStoreModes "EnabledFactStore" "the enabled fact-store mode is available"
            Expect.equal g.MetricSlotPrefix "metric" "metric registration point"
            Expect.equal g.SubjectSlotPrefix "subject" "subject registration point"
            // Plan-D14 disclosure defaults are described.
            Expect.contains
                g.DisclosureDefaults
                ("declared-metric", "Surfaceable")
                "declared metrics default Surfaceable"

            Expect.contains g.DisclosureDefaults ("intermediate", "Internal") "intermediates default Internal"
        }

        test "metric ids are rename-stable — surviving a module display rename" {
            let byNameA =
                ServerModule.create "sales" |> ServerModule.declareMetrics [ metric "revenue" ]

            let byNameB =
                ServerModule.create "finance"
                |> ServerModule.declareMetrics [ metric "revenue" ]

            let idA =
                (ServerApp.compositionManifest (ServerApp.empty |> ServerApp.addModules [ byNameA ])).Metrics
                |> List.head
                |> _.Id

            let idB =
                (ServerApp.compositionManifest (ServerApp.empty |> ServerApp.addModules [ byNameB ])).Metrics
                |> List.head
                |> _.Id

            Expect.equal idA idB "the metric id is independent of the declaring module's display name"
        }

        // Phase 592 — the manifest carries the declared purpose regime:
        // purpose entries beside the grounding entries, per-surface
        // allowed sets as `DisclosurePurposes.<Surface>` knobs.
        test "declared disclosure purposes surface in the manifest (entries + per-surface knobs)" {
            let purposes: RegisteredPurpose list = [
                {
                    PurposeId = "analytics"
                    Description = "Internal analytical reporting"
                    TaxonomyVersion = "v1"
                    AllowedSurfaces = [ "Retrieval"; "ToolResult" ]
                }
                {
                    PurposeId = "billing"
                    Description = "Invoice preparation"
                    TaxonomyVersion = "v1"
                    AllowedSurfaces = [ "ToolResult" ]
                }
            ]

            let m =
                ServerApp.compositionManifest (ServerApp.empty |> ServerApp.withRegisteredPurposes purposes)

            Expect.equal (m.Purposes |> List.map _.Label |> List.sort) [ "analytics"; "billing" ] "purposes reported"

            Expect.equal
                (m.Purposes |> List.map _.Id)
                (m.Purposes |> List.map (fun e -> ComponentId.forPurpose e.Label))
                "purpose entries keyed by ComponentId.forPurpose"

            Expect.equal
                (m.Purposes |> List.map _.Impl |> List.distinct)
                [ Some "v1" ]
                "the taxonomy version rides each entry"

            let knob name =
                m.ConfigKnobs |> List.tryFind (fun k -> k.Name = name) |> Option.map _.Value

            Expect.equal
                (knob "DisclosurePurposes.Retrieval")
                (Some "analytics")
                "the per-surface allowed set is a readable knob"

            Expect.equal
                (knob "DisclosurePurposes.ToolResult")
                (Some "analytics, billing")
                "a surface's full allowed set is enumerated"
        }

        test "a purpose-free composition reports no purpose entries and no purpose knobs" {
            let m = ServerApp.compositionManifest ServerApp.empty
            Expect.isEmpty m.Purposes "no purposes composed"

            Expect.isEmpty
                (m.ConfigKnobs |> List.filter (fun k -> k.Name.StartsWith "DisclosurePurposes."))
                "no purpose knobs on a purpose-free composition"
        }
    ]
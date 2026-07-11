module ToolUp.Platform.Tests.InProcess.MetricRegistryTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Grounding

// ─── Phase 519 — metric & subject registry ───────────────────────────
//
// The registry is a compose-time fan-in of module-declared business
// quantities (metrics) + entity hierarchies (subjects), with duplicate-id
// rejection and a synchronous read surface (`IMetricRegistry`). These
// tests exercise the builder's dedup/conflict semantics, the read-surface
// lookups, and the `ServerModule` → `ServerApp` fan-in accumulation — the
// last without starting a server.

let private metric id (op: string option) : MetricDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Unit = "count"
    Dimensionality = "count"
    Direction = HigherIsBetter
    DisplayFormat = "N0"
    Staleness = UntilSuperseded
    ProducingOperation = op
    CanonicalMethod = None
}

let private subject id (levels: string list) : SubjectDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Levels = levels
    Calendar = None
}

let private reg m def : MetricRegistration = { Module = m; Definition = def }
let private sreg m def : SubjectRegistration = { Module = m; Definition = def }

let tests =
    testList "MetricRegistry (Phase 519)" [

        // ─── Optionality / empty ──────────────────────────────────────

        test "empty registry answers every lookup with nothing" {
            let r = MetricRegistry.empty
            Expect.isEmpty r.Metrics "no metrics"
            Expect.isEmpty r.Subjects "no subjects"
            Expect.isNone (r.TryGetMetric "anything") "no metric by id"
            Expect.isNone (r.TryGetSubject "anything") "no subject by id"
            Expect.isEmpty (r.MetricsByModule "m") "no metrics by module"
            Expect.isEmpty (r.MetricsByOperation "op") "no metrics by operation"
        }

        test "a fresh ServerApp has no grounding registrations (byte-identical default)" {
            Expect.isEmpty ServerApp.empty.RegisteredMetrics "no metric registrations"
            Expect.isEmpty ServerApp.empty.RegisteredSubjects "no subject registrations"
        }

        // ─── Lookup semantics ─────────────────────────────────────────

        test "TryGetMetric / TryGetSubject resolve by id, miss otherwise" {
            let r =
                MetricRegistry.build [ reg "modA" (metric "elasticity" None) ] [
                    sreg "modA" (subject "geography" [ "country"; "region" ])
                ]

            Expect.equal (r.TryGetMetric "elasticity" |> Option.map _.Id) (Some "elasticity") "hit metric"
            Expect.isNone (r.TryGetMetric "unknown") "miss metric"

            Expect.equal
                (r.TryGetSubject "geography" |> Option.map _.Levels)
                (Some [ "country"; "region" ])
                "hit subject"

            Expect.isNone (r.TryGetSubject "unknown") "miss subject"
        }

        test "MetricsByModule returns only that module's metrics" {
            let r =
                MetricRegistry.build [
                    reg "modA" (metric "a1" None)
                    reg "modA" (metric "a2" None)
                    reg "modB" (metric "b1" None)
                ] []

            let aIds = r.MetricsByModule "modA" |> List.map _.Id |> List.sort
            Expect.equal aIds [ "a1"; "a2" ] "modA metrics"
            Expect.equal (r.MetricsByModule "modB" |> List.map _.Id) [ "b1" ] "modB metrics"
            Expect.isEmpty (r.MetricsByModule "unknown") "unknown module empty"
        }

        test "MetricsByOperation is the reverse index over ProducingOperation" {
            let r =
                MetricRegistry.build [
                    reg "modA" (metric "a1" (Some "op.fit"))
                    reg "modA" (metric "a2" (Some "op.fit"))
                    reg "modB" (metric "b1" (Some "op.other"))
                    reg "modB" (metric "b2" None)
                ] []

            let fitIds = r.MetricsByOperation "op.fit" |> List.map _.Id |> List.sort
            Expect.equal fitIds [ "a1"; "a2" ] "both fit-produced metrics"
            Expect.equal (r.MetricsByOperation "op.other" |> List.map _.Id) [ "b1" ] "other op"
            Expect.isEmpty (r.MetricsByOperation "op.none") "unlinked op empty"
        }

        // ─── Duplicate rejection ──────────────────────────────────────

        test "same metric id from two modules is a conflict naming both" {
            let result =
                MetricRegistry.tryBuild [ reg "modA" (metric "shared" None); reg "modB" (metric "shared" None) ] []

            match result with
            | Error msg ->
                Expect.stringContains msg "shared" "names the id"
                Expect.stringContains msg "modA" "names first module"
                Expect.stringContains msg "modB" "names second module"
            | Ok _ -> failtest "expected a duplicate-id conflict"
        }

        test "same subject id from two modules is a conflict naming both" {
            let result =
                MetricRegistry.tryBuild [] [ sreg "modA" (subject "geo" [ "c" ]); sreg "modB" (subject "geo" [ "c" ]) ]

            match result with
            | Error msg ->
                Expect.stringContains msg "geo" "names the id"
                Expect.stringContains msg "modA" "names first module"
                Expect.stringContains msg "modB" "names second module"
            | Ok _ -> failtest "expected a duplicate-subject conflict"
        }

        test "same module re-declaring the same id is idempotent, not a conflict" {
            // A single module declaring the same id twice is collapsed to
            // one entry — not a cross-module conflict.
            let result =
                MetricRegistry.tryBuild [ reg "modA" (metric "dup" None); reg "modA" (metric "dup" None) ] []

            match result with
            | Ok r -> Expect.equal (r.Metrics |> List.map _.Id) [ "dup" ] "collapsed to one"
            | Error msg -> failtestf "expected Ok (idempotent), got: %s" msg
        }

        test "build raises on a cross-module conflict (fail-fast at compose)" {
            Expect.throws
                (fun () ->
                    MetricRegistry.build [ reg "modA" (metric "x" None); reg "modB" (metric "x" None) ] []
                    |> ignore)
                "duplicate id raises"
        }

        // ─── Fan-in through ServerModule / ServerApp ──────────────────

        test "declareMetrics / declareSubjects accumulate onto the module" {
            let m =
                ServerModule.create "sales"
                |> ServerModule.declareMetrics [ metric "revenue" None; metric "margin" None ]
                |> ServerModule.declareSubjects [ subject "product" [ "brand"; "sku" ] ]

            Expect.equal (m.Metrics |> List.map _.Id) [ "revenue"; "margin" ] "metrics declared"
            Expect.equal (m.Subjects |> List.map _.Id) [ "product" ] "subjects declared"
        }

        test "addModule fans module declarations into the app registry lists" {
            let modA =
                ServerModule.create "modA" |> ServerModule.declareMetrics [ metric "a1" None ]

            let modB =
                ServerModule.create "modB"
                |> ServerModule.declareMetrics [ metric "b1" None ]
                |> ServerModule.declareSubjects [ subject "geo" [ "country" ] ]

            let app = ServerApp.empty |> ServerApp.addModules [ modA; modB ]

            let byModule =
                app.RegisteredMetrics
                |> List.map (fun r -> r.Module, r.Definition.Id)
                |> List.sort

            Expect.equal byModule [ "modA", "a1"; "modB", "b1" ] "metric registrations carry the module"

            Expect.equal
                (app.RegisteredSubjects |> List.map (fun r -> r.Module, r.Definition.Id))
                [ "modB", "geo" ]
                "subject registration carries the module"

            // And the accumulated registrations build a coherent registry.
            let registry = MetricRegistry.build app.RegisteredMetrics app.RegisteredSubjects

            Expect.equal (registry.Metrics |> List.map _.Id |> List.sort) [ "a1"; "b1" ] "both metrics in registry"
            Expect.equal (registry.MetricsByModule "modA" |> List.map _.Id) [ "a1" ] "modA lookup"
        }
    ]
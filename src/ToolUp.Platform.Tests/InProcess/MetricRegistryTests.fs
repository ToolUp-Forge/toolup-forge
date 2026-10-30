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
    RecomputePolicy = None
    RollUp = None
    Context = None
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

        // ─── Context (Phase 705) ──────────────────────────────────────

        test "Context round-trips through the registry, and is optional" {
            // Declared once with the METRIC, not once per subject: the
            // whole point of the field is that a discovery surface can
            // explain `elasticity` without a hand-authored chunk repeating
            // the same sentence beside every value.
            let narrative =
                "Price elasticity of demand, estimated weekly. Negative by convention."

            let r =
                MetricRegistry.build [
                    reg "modA" {
                        metric "elasticity" None with
                            Context = Some narrative
                    }
                    reg "modA" (metric "plain" None)
                ] []

            Expect.equal
                (r.TryGetMetric "elasticity" |> Option.bind _.Context)
                (Some narrative)
                "the declared narrative survives the fan-in verbatim"

            Expect.isNone
                (r.TryGetMetric "plain" |> Option.bind _.Context)
                "an undeclared context stays None — nothing is invented for a metric that declared none (GP 11)"

            Expect.equal
                (r.Metrics |> List.filter (fun d -> d.Context.IsSome) |> List.length)
                1
                "and the field is per-metric, not per-registry"
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

        // ─── Subject descriptions ─────────────────────────────────────

        let described moduleName (descriptions: SubjectDescription list) =
            ServerModule.create moduleName
            |> ServerModule.declareSubjects [ subject "geography" [ "site"; "market"; "country" ] ]
            |> ServerModule.describeSubjects descriptions

        let geographyDescription: SubjectDescription = {
            SubjectId = "geography"
            Description = "Where a sale is booked; each site belongs to one market and each market to one country."
            Levels = [
                "site", "A single trading outlet."
                "country", "The sovereign state a market is in."
            ]
        }

        let refusal (app: ServerApp) : string =
            try
                ServerApp.metricRegistry app |> ignore
                failtest "expected the composition to be refused"
            with Failure message ->
                message

        test "a module describes a subject and its levels, and the composed registry carries them" {
            let app =
                ServerApp.empty
                |> ServerApp.addModule (described "trade" [ geographyDescription ])

            let registry = (ServerApp.metricRegistry app).Value

            Expect.equal
                (MetricRegistry.trySubjectDescription registry "geography")
                (Some geographyDescription)
                "the registered description is read back by subject id"

            Expect.isNone (MetricRegistry.trySubjectDescription registry "unknown") "an unregistered subject has none"

            Expect.equal
                (registry.TryGetSubject "geography" |> Option.map _.Levels)
                (Some [ "site"; "market"; "country" ])
                "the declaration is unchanged"
        }

        test "an undescribed subject reads None, and a composition describing nothing registers the same registry" {
            let app = ServerApp.empty |> ServerApp.addModule (described "trade" [])

            let registry = (ServerApp.metricRegistry app).Value
            Expect.isNone (MetricRegistry.trySubjectDescription registry "geography") "declared, undescribed"
            Expect.isNone (ServerApp.metricRegistry ServerApp.empty) "nothing declared, nothing registered"
        }

        test "a registry without the capability has no descriptions" {
            let inner = MetricRegistry.empty

            let bare =
                { new IMetricRegistry with
                    member _.Metrics = inner.Metrics
                    member _.Subjects = inner.Subjects
                    member _.TryGetMetric id = inner.TryGetMetric id
                    member _.TryGetSubject id = inner.TryGetSubject id
                    member _.MetricsByModule m = inner.MetricsByModule m
                    member _.MetricsByOperation o = inner.MetricsByOperation o
                }

            Expect.isNone
                (MetricRegistry.trySubjectDescription bare "geography")
                "probing a registry that predates it yields nothing"
        }

        test "describeSubjects accumulates, and addModule tags each description with its module" {
            let m =
                described "trade" [ geographyDescription ]
                |> ServerModule.describeSubjects [
                    {
                        geographyDescription with
                            SubjectId = "other"
                    }
                ]

            Expect.equal (m.SubjectDescriptions |> List.map _.SubjectId) [ "geography"; "other" ] "adds, left to right"

            let app = ServerApp.empty |> ServerApp.addModule m

            Expect.equal
                (app.RegisteredSubjectDescriptions
                 |> List.map (fun r -> r.Module, r.Description.SubjectId))
                [ "trade", "geography"; "trade", "other" ]
                "registrations carry the module"
        }

        test "a description of a subject no module declares is refused, naming the module and the subject" {
            let app =
                ServerApp.empty
                |> ServerApp.addModule (
                    ServerModule.create "trade"
                    |> ServerModule.describeSubjects [
                        {
                            geographyDescription with
                                SubjectId = "ghost"
                        }
                    ]
                )

            let message = refusal app
            Expect.stringContains message "Module 'trade'" "names the describing module"
            Expect.stringContains message "'ghost'" "names the subject"
            Expect.stringContains message "no module declares" "says why"
        }

        test "a description of a level the subject does not carry is refused, naming module, subject and level" {
            let app =
                ServerApp.empty
                |> ServerApp.addModule (
                    described "trade" [
                        {
                            geographyDescription with
                                Levels = [ "region", "Not a level." ]
                        }
                    ]
                )

            let message = refusal app
            Expect.stringContains message "Module 'trade'" "names the describing module"
            Expect.stringContains message "level 'region'" "names the level"
            Expect.stringContains message "subject 'geography'" "names the subject"
            Expect.stringContains message "'site', 'market', 'country'" "and lists the levels it does carry"
        }

        test "a subject described twice, and a level described twice, are refused" {
            let twice =
                ServerApp.empty
                |> ServerApp.addModule (described "trade" [ geographyDescription; geographyDescription ])

            Expect.stringContains (refusal twice) "described more than once" "a subject is described once"

            let levelTwice =
                ServerApp.empty
                |> ServerApp.addModule (
                    described "trade" [
                        {
                            geographyDescription with
                                Levels = [ "site", "One."; "site", "Two." ]
                        }
                    ]
                )

            let message = refusal levelTwice
            Expect.stringContains message "level 'site'" "names the level"
            Expect.stringContains message "2 times" "says how many"
        }

        test "a subject declared by another module can be described by this one" {
            let describer =
                ServerModule.create "docs"
                |> ServerModule.describeSubjects [ geographyDescription ]

            let app =
                ServerApp.empty |> ServerApp.addModules [ described "trade" []; describer ]

            let registry = (ServerApp.metricRegistry app).Value

            Expect.equal
                (MetricRegistry.trySubjectDescription registry "geography"
                 |> Option.map _.Description)
                (Some geographyDescription.Description)
                "the declaration and its description need not share a module"
        }
    ]
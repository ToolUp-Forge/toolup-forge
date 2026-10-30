// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactDeclarationsValidatorTests

// ─── Preflight names what a deployment left undescribed ──────────────
//
// What this pack pins, one list each:
//
//   1. **Nothing to say.** A deployment whose metrics, tables and subjects
//      are all described reports no finding, and so does one that declared
//      nothing.
//   2. **Named, by module.** One undescribed metric, table and subject are
//      each named under the module that declared them, with a count and
//      the remedy; a blank description counts as none.
//   3. **Information, never a refusal.** The result is `Ok` whatever is
//      undescribed; the finding rides the report channel.
//   4. **An unattributing registry.** A registry that does not record its
//      declaring modules still has every gap named, under one group.
//   5. **The composition.** The fact tier registers the audit once, it
//      reads the registries and the logger it was composed beside, and a
//      composition without the fact store registers nothing.

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Facts

// ─── Fixtures ────────────────────────────────────────────────────────

let private metric (id: string) (context: string option) : MetricDefinition = {
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
    Context = context
}

let private subject (id: string) : SubjectDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Levels = [ "site"; "market" ]
    Calendar = None
}

let private table (id: string) (hierarchy: string) (metrics: string list) : FactTableDefinition = {
    Id = id
    SchemaVersion = 1
    Hierarchy = hierarchy
    Level = "site"
    Columns =
        metrics
        |> List.map (fun m -> FactTableDefinition.column m FactTableValueShape.Scalar)
    PeriodGrain = FactTablePeriodGrain.Month
    ProducingOperation = "rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.Replace
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

let private metricReg (m: string) (d: MetricDefinition) : MetricRegistration = { Module = m; Definition = d }
let private subjectReg (m: string) (d: SubjectDefinition) : SubjectRegistration = { Module = m; Definition = d }

let private tableReg (m: string) (d: FactTableDefinition) : FactTableRegistration = { Module = m; Definition = d }

let private subjectDescription (id: string) (text: string) : SubjectDescriptionRegistration = {
    Module = "geo"
    Description = {
        SubjectId = id
        Description = text
        Levels = []
    }
}

let private tableDescription (m: string) (id: string) (text: string) : FactTableDescriptionRegistration = {
    Module = m
    Description = {
        TableId = id
        Description = text
        Columns = []
    }
}

/// Two modules: `sales` declares two metrics and a table, `geo` a metric and
/// a subject. `describe` says which of the declarations carry a description.
let private deployment (describe: bool) : IMetricRegistry * IFactTableRegistry =
    let context = if describe then Some "what it means" else None

    let metrics =
        MetricRegistry.buildDescribed
            [
                metricReg "sales" (metric "revenue" context)
                metricReg "sales" (metric "units" context)
                metricReg "geo" (metric "footfall" context)
            ]
            [ subjectReg "geo" (subject "places") ]
            (if describe then
                 [ subjectDescription "places" "a place" ]
             else
                 [])

    let tables =
        FactTableRegistry.buildDescribed
            [ tableReg "sales" (table "sku-sales" "places" [ "revenue"; "units" ]) ]
            []
            (if describe then
                 [ tableDescription "sales" "sku-sales" "sales by sku" ]
             else
                 [])

    metrics, tables

let private audit (metrics: IMetricRegistry) (tables: IFactTableRegistry) : UndescribedDeclarations list =
    FactDeclarationsAudit.undescribed (Some metrics) (Some tables)

let private run (validator: ConfigValidation.IConfigValidator) : ConfigValidation.ValidationResult =
    validator.Validate() |> Async.RunSynchronously

/// The validator over `metrics` / `tables`, with the findings it reported.
let private validating (metrics: IMetricRegistry option) (tables: IFactTableRegistry option) =
    let reported = ResizeArray<string>()

    let validator =
        FactDeclarationsValidator((fun () -> metrics), (fun () -> tables), reported.Add)
        :> ConfigValidation.IConfigValidator

    validator, reported

let private contains (needle: string) (haystack: string) : bool =
    haystack.Contains(needle, StringComparison.Ordinal)

/// A metric registry that carries no attribution capability.
let private unattributing (inner: IMetricRegistry) : IMetricRegistry =
    { new IMetricRegistry with
        member _.Metrics = inner.Metrics
        member _.Subjects = inner.Subjects
        member _.TryGetMetric id = inner.TryGetMetric id
        member _.TryGetSubject id = inner.TryGetSubject id
        member _.MetricsByModule m = inner.MetricsByModule m
        member _.MetricsByOperation o = inner.MetricsByOperation o
    }

let private capturingLogger (lines: ResizeArray<string * string>) : ILogger =
    { new ILogger with
        member _.Debug m = lines.Add(("debug", m))
        member _.Info m = lines.Add(("info", m))
        member _.Warn m = lines.Add(("warn", m))
        member _.Error(m, _) = lines.Add(("error", m))
    }

let private composedServices (knob: FactStoreMode) : ServiceCollection =
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

    match ServerApp.serviceConfig app with
    | Some register -> register services |> ignore
    | None -> ()

    services

let private auditsIn (services: IServiceCollection) : ConfigValidation.IConfigValidator list =
    services
    |> Seq.choose (fun d ->
        match d.ImplementationInstance with
        | :? FactDeclarationsValidator as v -> Some(v :> ConfigValidation.IConfigValidator)
        | _ -> None)
    |> List.ofSeq

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "Fact declarations audit" [

        testList "nothing to say" [
            test "a fully described deployment has no gap" {
                let metrics, tables = deployment true
                Expect.isEmpty (audit metrics tables) "everything described"
            }

            test "a fully described deployment reports no finding" {
                let metrics, tables = deployment true
                let validator, reported = validating (Some metrics) (Some tables)
                run validator |> ignore
                Expect.isEmpty reported "no finding reported"
            }

            test "a deployment that declared nothing reports no finding" {
                let validator, reported = validating None None
                run validator |> ignore
                Expect.isEmpty reported "no finding reported"
            }
        ]

        testList "named, by module" [
            test "each undescribed metric, table and subject is named under its module" {
                let metrics, tables = deployment false

                Expect.equal
                    (audit metrics tables)
                    [
                        {
                            ModuleName = "sales"
                            Metrics = [ "revenue"; "units" ]
                            Tables = [ "sku-sales" ]
                            Subjects = []
                        }
                        {
                            ModuleName = "geo"
                            Metrics = [ "footfall" ]
                            Tables = []
                            Subjects = [ "places" ]
                        }
                    ]
                    "grouped by declaring module, first appearance order"
            }

            test "one finding carries every name, the counts and the remedy" {
                let metrics, tables = deployment false
                let validator, reported = validating (Some metrics) (Some tables)
                run validator |> ignore

                Expect.equal reported.Count 1 "one finding"
                let finding = reported[0]

                for expected in
                    [
                        "module 'sales'"
                        "module 'geo'"
                        "2 metric(s) without Context: 'revenue', 'units'"
                        "1 fact table(s) without a description: 'sku-sales'"
                        "1 subject(s) without a description: 'places'"
                        "5 declaration(s) in 2 module(s)"
                        "ServerModule.declareMetrics"
                        "ServerModule.describeFactTables"
                        "ServerModule.describeSubjects"
                    ] do
                    Expect.isTrue (contains expected finding) (sprintf "names %s: %s" expected finding)
            }

            test "only the undescribed declarations are named" {
                let metrics =
                    MetricRegistry.build [
                        metricReg "sales" (metric "revenue" (Some "described"))
                        metricReg "sales" (metric "units" None)
                    ] []

                let gaps = FactDeclarationsAudit.undescribed (Some metrics) None
                Expect.equal (gaps |> List.collect _.Metrics) [ "units" ] "only the metric without Context"
                Expect.isEmpty (gaps |> List.collect _.Tables) "no table declared"
            }

            test "a blank Context or description counts as none" {
                let metrics =
                    MetricRegistry.buildDescribed [ metricReg "sales" (metric "revenue" (Some "  ")) ] [
                        subjectReg "geo" (subject "places")
                    ] [ subjectDescription "places" "" ]

                let gaps = FactDeclarationsAudit.undescribed (Some metrics) None
                Expect.equal (gaps |> List.collect _.Metrics) [ "revenue" ] "blank Context"
                Expect.equal (gaps |> List.collect _.Subjects) [ "places" ] "blank subject description"
            }

            test "describing a gap removes it from the finding" {
                let metrics, _ = deployment false

                let tables =
                    FactTableRegistry.buildDescribed
                        [ tableReg "sales" (table "sku-sales" "places" [ "revenue"; "units" ]) ] [] [
                            tableDescription "sales" "sku-sales" "sales by sku"
                        ]

                let gaps = audit metrics tables
                Expect.isEmpty (gaps |> List.collect _.Tables) "the table is now described"
                Expect.isNonEmpty (gaps |> List.collect _.Metrics) "the metrics still are not"
            }
        ]

        testList "information, never a refusal" [
            test "the result is Ok however much is undescribed" {
                let metrics, tables = deployment false
                let validator, reported = validating (Some metrics) (Some tables)

                Expect.equal (run validator) ConfigValidation.ValidationResult.Ok "Ok, not Warning or Error"
                Expect.isNonEmpty reported "the finding still reached the report channel"
            }

            test "the validator keeps a stable name" {
                let validator, _ = validating None None
                Expect.equal validator.Name FactDeclarationsAudit.ValidatorName "name"
            }
        ]

        testList "an unattributing registry" [
            test "every gap is named under one unrecorded-module group" {
                let metrics, tables = deployment false

                let gaps = audit (unattributing metrics) tables

                let unrecorded =
                    gaps |> List.find (fun g -> g.ModuleName = FactDeclarationsAudit.Unattributed)

                Expect.equal unrecorded.Metrics [ "revenue"; "units"; "footfall" ] "every metric"
                Expect.equal unrecorded.Subjects [ "places" ] "the subject"
                Expect.equal (gaps |> List.collect _.Tables) [ "sku-sales" ] "the table keeps its module"
            }
        ]

        testList "the composition" [
            test "the fact tier registers the audit once, however often it is applied" {
                let services = composedServices EnabledFactStore

                let app =
                    {
                        ServerApp.empty with
                            Config = {
                                ServerConfig.defaults with
                                    FactStore = EnabledFactStore
                            }
                    }
                    |> FactsCompose.withFactStore
                    |> FactsCompose.withFactStore

                match ServerApp.serviceConfig app with
                | Some register -> register services |> ignore
                | None -> ()

                Expect.equal (auditsIn services).Length 1 "registered exactly once"
            }

            test "a composition without the fact store registers nothing" {
                Expect.isEmpty (auditsIn (composedServices NoFactStore)) "no audit"
            }

            test "the registered audit reads the registries and logger it was composed beside" {
                let services = composedServices EnabledFactStore
                let metrics, tables = deployment false
                let lines = ResizeArray<string * string>()

                services.AddSingleton<IMetricRegistry>(metrics) |> ignore
                services.AddSingleton<IFactTableRegistry>(tables) |> ignore
                services.AddSingleton<ILogger>(capturingLogger lines) |> ignore

                match auditsIn services with
                | [ validator ] ->
                    Expect.equal (run validator) ConfigValidation.ValidationResult.Ok "Ok"
                    Expect.equal lines.Count 1 "one line logged"
                    let level, message = lines[0]
                    Expect.equal level "info" "at Info"
                    Expect.isTrue (contains "'footfall'" message) message
                | other -> failtestf "expected one audit, found %d" other.Length
            }

            test "a fully described deployment logs nothing" {
                let services = composedServices EnabledFactStore
                let metrics, tables = deployment true
                let lines = ResizeArray<string * string>()

                services.AddSingleton<IMetricRegistry>(metrics) |> ignore
                services.AddSingleton<IFactTableRegistry>(tables) |> ignore
                services.AddSingleton<ILogger>(capturingLogger lines) |> ignore

                auditsIn services |> List.iter (run >> ignore)
                Expect.isEmpty lines "nothing logged"
            }
        ]
    ]
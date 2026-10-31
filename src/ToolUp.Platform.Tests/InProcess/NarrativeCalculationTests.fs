// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NarrativeCalculationTests

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.Narrative
open ToolUp.Platform.Secrets
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.ArtefactSigning
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Authorised narrative calculations ──────────────────
//
// A grounded report states a derived figure only as a `calc:` reference the
// server computed by a declared formula, and the gate admits it only after
// deriving the same figure again on its own. Covered here: the closed
// declaration set and its refusals; the two evaluations held equal over a
// generated corpus (zero denominators, negatives, ties, large magnitudes);
// the tool and the gate over a real fact store; every gate refusal; the
// signed calculation admission and its tamper check; and a deployment with
// no declarations seeing no change.

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

// ── Declarations ──────────────────────────────────────────────────

let private declaration
    (name: string)
    (operator: CalculationOperator)
    (metrics: string list)
    (places: int)
    (mode: CalculationRoundingMode)
    (format: CalculationFormat)
    : CalculationDeclaration =
    {
        Name = name
        Version = 1
        Operator = operator
        Metrics = metrics
        Rounding = { Places = places; Mode = mode }
        Format = format
    }

let private revenueChange =
    declaration
        "revenue-change"
        CalculationOperator.PercentChange
        [ "revenue" ]
        1
        CalculationRoundingMode.HalfEven
        CalculationFormat.Percent

let private revenueGap =
    declaration
        "revenue-gap"
        CalculationOperator.Difference
        [ "revenue"; "units" ]
        0
        CalculationRoundingMode.HalfAwayFromZero
        (CalculationFormat.Currency "GBP")

let private declared (declarations: CalculationDeclaration list) : NarrativeCalculations =
    match NarrativeCalculations.declare declarations with
    | Ok calculations -> calculations
    | Error refusals -> failtestf "the declarations must be valid: %A" refusals

let private calculations = declared [ revenueChange; revenueGap ]

let declarationTests =
    testList "declarations" [

        test "the operator set is closed, and anything else is refused by name" {
            for operator in CalculationOperator.all do
                Expect.equal
                    (CalculationOperator.tryParse (CalculationOperator.name operator))
                    (Ok operator)
                    "every operator round-trips its name"

            match CalculationOperator.tryParse "median" with
            | Error reason ->
                Expect.stringContains reason "'median'" "names what was asked for"
                Expect.stringContains reason "percentChange" "and lists the closed set"
            | Ok operator -> failtestf "an undeclared operator must be refused, got %A" operator
        }

        test "a malformed declaration is refused naming each problem" {
            let bad = [
                {
                    revenueChange with
                        Name = "has space"
                }
                { revenueGap with Version = 0 }
                {
                    revenueChange with
                        Name = "no-metric"
                        Metrics = []
                }
                {
                    revenueChange with
                        Name = "too-precise"
                        Rounding = {
                            Places = 11
                            Mode = CalculationRoundingMode.HalfEven
                        }
                }
                {
                    revenueChange with
                        Name = "change-in-pounds"
                        Format = CalculationFormat.Currency "GBP"
                }
                {
                    revenueGap with
                        Name = "gap-in-zorkmids"
                        Format = CalculationFormat.Currency "ZORK"
                }
            ]

            match NarrativeCalculations.declare (bad @ [ revenueChange; revenueChange ]) with
            | Ok _ -> failtest "malformed declarations must be refused"
            | Error refusals ->
                let all = String.concat "\n" refusals

                for needle in
                    [
                        "'has space'"
                        "version 0"
                        "'no-metric' must name at least one metric"
                        "'too-precise' rounds to 11 places"
                        "'change-in-pounds' declares percentChange with a format it cannot be shown in"
                        "'ZORK', which is not an ISO 4217 code"
                        "'revenue-change' is declared more than once"
                    ] do
                    Expect.stringContains all needle "each problem is named"
        }

        test "no declaration, no tool; declarations give the run exactly the calculate tool" {
            Expect.isEmpty (NarrativeCalculations.tools NarrativeCalculations.empty) "empty: no tool (GP 13)"
            Expect.isEmpty (NarrativeCalculations.tools (declared [])) "an empty list is the same"

            let names =
                NarrativeCalculations.tools calculations |> List.map (fun (d, _) -> d.Name)

            Expect.equal names [ AuthorisedCalculation.ToolName ] "one tool"

            let description = (NarrativeCalculations.definition calculations).Description

            Expect.stringContains description "revenue-change (percentChange over revenue" "lists each declaration"
            Expect.stringContains description "never compute a figure yourself" "and says the model never computes"
        }
    ]

// ── Two evaluations, held equal ───────────────────────────────────

let private maxDecimal = Decimal.MaxValue

/// The corpus values: zero, signs, ties at several places, tiny and large
/// magnitudes (the largest a decimal holds), and a seeded random spread.
let private corpusValues: decimal list =
    let fixedValues = [
        0m
        1m
        -1m
        0.5m
        -0.5m
        2.5m
        -2.5m
        0.125m
        0.135m
        0.0000000001m
        3m
        7m
        8m
        1000m
        1027m
        1250.5m
        -999.999m
        123456789012345678.9m
        12345678901234567890123456.78m
        maxDecimal
        -maxDecimal
    ]

    let random = Random 1180

    let generated = [
        for _ in 1..12 ->
            let whole = decimal (random.Next(-1_000_000, 1_000_000))
            let fraction = decimal (random.Next(0, 10_000)) / 10_000m
            whole + fraction
    ]

    fixedValues @ generated

let private corpusDeclarations: CalculationDeclaration list = [
    for places in [ 0; 2; 4 ] do
        for mode in [ CalculationRoundingMode.HalfEven; CalculationRoundingMode.HalfAwayFromZero ] do
            for operator, format in
                [
                    CalculationOperator.PercentChange, CalculationFormat.Percent
                    CalculationOperator.Share, CalculationFormat.Percent
                    CalculationOperator.Ratio, CalculationFormat.Number
                    CalculationOperator.Difference, CalculationFormat.Currency "GBP"
                    CalculationOperator.Difference, CalculationFormat.Scaled CalculationScale.Million
                    CalculationOperator.Sum, CalculationFormat.Number
                    CalculationOperator.Mean, CalculationFormat.Scaled CalculationScale.Thousand
                ] do
                declaration (CalculationOperator.name operator) operator [ "m" ] places mode format
]

let private inputsFor (operator: CalculationOperator) : decimal list list =
    match operator with
    | CalculationOperator.Sum
    | CalculationOperator.Mean -> [
        for a in corpusValues do
            for b in [ 0m; 1m; -2.5m; 0.125m; maxDecimal ] do
                yield [ a; b ]
                yield [ a; b; 7m ]
      ]
    | _ -> [
        for a in corpusValues do
            for b in corpusValues -> [ a; b ]
      ]

let private bothPaths (declaration: CalculationDeclaration) (values: decimal list) =
    CalculationCompute.evaluate declaration values, CalculationDeclaration.evaluate declaration values

let differentialTests =
    testList "two independent evaluations, held equal" [

        test "every operator over the generated corpus gives one answer on both paths" {
            let mutable cases = 0
            let mutable zeroDenominators = 0
            let mutable outOfRange = 0
            let disagreements = ResizeArray()

            for declaration in corpusDeclarations do
                for values in inputsFor declaration.Operator do
                    cases <- cases + 1
                    let viaCompute, viaDeclaration = bothPaths declaration values

                    match viaCompute, viaDeclaration with
                    | Ok a, Ok b when a = b -> ()
                    | Error(ZeroDenominator _), Error(ZeroDenominator _) -> zeroDenominators <- zeroDenominators + 1
                    | Error(ResultOutOfRange _), Error(ResultOutOfRange _) -> outOfRange <- outOfRange + 1
                    | a, b ->
                        if disagreements.Count < 10 then
                            disagreements.Add(sprintf "%A %A: compute %A, declaration %A" declaration values a b)

            Expect.isEmpty disagreements "the tool's path and the gate's path never disagree"
            Expect.isGreaterThan cases 10_000 "the corpus is not vacuous"
            Expect.isGreaterThan zeroDenominators 0 "zero denominators were exercised"
            Expect.isGreaterThan outOfRange 0 "results too large to state were exercised"
        }

        test "a zero denominator is a declared refusal on both paths, never infinity or NaN" {
            for operator in
                [
                    CalculationOperator.PercentChange
                    CalculationOperator.Ratio
                    CalculationOperator.Share
                ] do
                let d =
                    declaration "z" operator [ "m" ] 2 CalculationRoundingMode.HalfEven CalculationFormat.Percent

                let values =
                    match operator with
                    | CalculationOperator.PercentChange -> [ 0m; 5m ]
                    | _ -> [ 5m; 0m ]

                let viaCompute, viaDeclaration = bothPaths d values
                Expect.equal viaCompute (Error(ZeroDenominator "z")) "the tool's path refuses"
                Expect.equal viaDeclaration (Error(ZeroDenominator "z")) "the gate's path refuses"
        }

        test "an input count the operator does not take is refused on both paths" {
            for operator, values in
                [
                    CalculationOperator.PercentChange, [ 1m; 2m; 3m ]
                    CalculationOperator.Share, [ 1m ]
                    CalculationOperator.Sum, [ 1m ]
                    CalculationOperator.Mean, List.replicate (CalculationOperator.MaxInputs + 1) 1m
                ] do
                let d =
                    declaration "n" operator [ "m" ] 0 CalculationRoundingMode.HalfEven CalculationFormat.Number

                let viaCompute, viaDeclaration = bothPaths d values

                let expected =
                    Error(WrongInputCount("n", CalculationOperator.inputs operator, List.length values))

                Expect.equal viaCompute expected (sprintf "compute: %A" operator)
                Expect.equal viaDeclaration expected (sprintf "declaration: %A" operator)
        }

        test "ties round once, under the declared mode, on both paths" {
            let check operator format places mode values expected =
                let d = declaration "t" operator [ "m" ] places mode format
                let viaCompute, viaDeclaration = bothPaths d values
                Expect.equal viaCompute (Ok expected) (sprintf "compute: %A %A %A" operator mode values)
                Expect.equal viaDeclaration (Ok expected) (sprintf "declaration: %A %A %A" operator mode values)

            let even = CalculationRoundingMode.HalfEven
            let away = CalculationRoundingMode.HalfAwayFromZero
            let share = CalculationOperator.Share
            let percent = CalculationFormat.Percent
            check share percent 0 even [ 1m; 8m ] 12m
            check share percent 0 away [ 1m; 8m ] 13m
            check share percent 0 even [ 3m; 8m ] 38m
            check share percent 1 even [ 1m; 3m ] 33.3m
            check CalculationOperator.Difference CalculationFormat.Number 0 even [ 0m; -2.5m ] -2m
            check CalculationOperator.Difference CalculationFormat.Number 0 away [ 0m; -2.5m ] -3m
            check CalculationOperator.Mean CalculationFormat.Number 0 even [ 1m; 4m ] 2m
            check CalculationOperator.Mean CalculationFormat.Number 0 away [ 1m; 4m ] 3m
            check CalculationOperator.PercentChange percent 1 even [ 1000m; 1027m ] 2.7m
            check CalculationOperator.PercentChange percent 1 even [ -200m; -100m ] 50m
        }
    ]

// ── The tool and the gate over a real fact store ──────────────────

let private period (month: int) : TemporalExtent = {
    From = DateTime(2026, month, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, month + 1, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some(sprintf "2026-%02d" month)
}

let private draft (metricId: string) (month: int) (value: decimal) (disclosure: Disclosure) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef metricId
    Value = Scalar value
    Period = period month
    Method = HumanAsserted "analyst"
    Evidence = {
        ResultRef = None
        InputHashes = []
        TriggerRef = None
    }
    Confidence = None
    Disclosure = disclosure
}

let private metricDefinition (id: string) (unit: string) : MetricDefinition = {
    Id = id
    Name = id
    Unit = unit
    Dimensionality = (if unit = "count" then "count" else "currency")
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private registry: IMetricRegistry =
    MetricRegistry.build [
        for definition in [ metricDefinition "revenue" "GBP"; metricDefinition "units" "count" ] ->
            {
                MetricRegistration.Module = "sales"
                Definition = definition
            }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = {
                Id = "brand"
                Name = "Brand"
                Levels = [ "brand" ]
                Calendar = None
            }
        }
    ]

type private Fixture = {
    Store: IFactStore
    Disclosure: IFactDisclosureGate
    Issuer: IGroundingCertificateIssuer
    Secrets: ISecretStore
    Scope: string
}

let private fixture () : Fixture =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    let disclosure = FactDisclosureGate.create store events
    let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

    let graph =
        ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

    let secrets = InMemorySecretStore() :> ISecretStore
    let audit = AuditLog.NoOpAuditLog() :> IAuditLog

    let signer =
        DefaultArtefactSigner.createSystem secrets audit "grounding-calc" Ed25519

    {
        Store = store
        Disclosure = disclosure
        Issuer = GroundingCertificate.createIssuer graph store disclosure events (Some signer)
        Secrets = secrets
        Scope = "team-" + Guid.NewGuid().ToString "N"
    }

let private assertFact (f: Fixture) (d: FactDraft) : Fact =
    match f.Store.Assert(TestScope.resolved f.Scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" (FactStoreError.describe e)

let private gateOver (f: Fixture) (calcs: NarrativeCalculations) (registry: IMetricRegistry option) =
    FactNarrativeGroundingGate.createWithCalculations calcs f.Store f.Disclosure registry [] f.Issuer

/// Ask the `calculate` tool, as the model would.
let private calculate (f: Fixture) (calcs: NarrativeCalculations) (registry: IMetricRegistry option) name inputs =
    let args =
        JsonSerializer.Serialize {|
            calculation = name
            inputs = String.concat "," inputs
        |}

    let json =
        NarrativeCalculations.executeWith
            calcs
            f.Store
            f.Disclosure
            registry
            (TestScope.resolved f.Scope)
            "analyst"
            args
        |> Async.RunSynchronously

    JsonDocument.Parse(json).RootElement.Clone()

let private property (name: string) (root: JsonElement) =
    match root.TryGetProperty name with
    | true, v -> Some(v.GetString())
    | _ -> None

let private requested (f: Fixture) calcs registry name inputs : string * string =
    let root = calculate f calcs registry name inputs

    match property "reference" root, property "value" root with
    | Some reference, Some value -> reference, value
    | _ -> failtestf "the calculation must be computed, got %s" (root.GetRawText())

let private refusalOf (f: Fixture) calcs registry name inputs : string =
    match calculate f calcs registry name inputs |> property "error" with
    | Some error -> error
    | None -> failtestf "the calculation must be refused"

let private docOf (spans: InlineSpan list) : NarrativeDocument = {
    Title = "Weekly summary"
    Subtitle = None
    Sections = [
        {
            Id = "summary"
            Heading = "This week"
            Subheading = None
            Elements = [ NarrativeElement.Paragraph spans ]
        }
    ]
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

let private stating (value: string) (reference: string) =
    docOf [
        InlineSpan.Text "Revenue rose "
        InlineSpan.Metric("Revenue change", value, Some reference)
        InlineSpan.Text " on last month."
    ]

let private check (f: Fixture) (gate: INarrativeGroundingGate) (document: NarrativeDocument) =
    gate.Check(TestScope.resolved f.Scope, "reader", FactNarrativePublication, document)
    |> Async.RunSynchronously

let private reasonsOf (verdict: NarrativeGroundingVerdict) : GroundingOffenceReason list =
    match verdict with
    | Ungrounded offences -> offences |> List.map _.Reason
    | Grounded _ -> []

/// Revenue for September and October: 1,000 then 1,027 — a 2.7% rise.
let private revenueMonths (f: Fixture) : Fact * Fact =
    assertFact f (draft "revenue" 9 1000m Surfaceable), assertFact f (draft "revenue" 10 1027m Surfaceable)

let gateTests =
    testList "the calculate tool and the calc reference kind" [

        test "a declared percentage change is computed by the server, renders as declared and passes the gate" {
            let f = fixture ()
            let september, october = revenueMonths f

            let reference, value =
                requested f calculations (Some registry) "revenue-change" [ september.FactId; october.FactId ]

            Expect.equal value "2.7%" "rendered as declared: a percent at one place"

            Expect.stringStarts
                reference
                (sprintf "calc:revenue-change@1/%s/%s/" september.FactId october.FactId)
                "the reference names the declaration, its version and the inputs in order"

            match check f (gateOver f calculations (Some registry)) (stating "2.7%" reference) with
            | Grounded citations ->
                Expect.contains
                    citations
                    {
                        ReferenceKind = AuthorisedCalculation.ReferenceKind
                        Reference = reference
                    }
                    "cited as a calculation"
            | other -> failtestf "the gate must admit what it derives again, got %A" other

            Expect.isEmpty
                (reasonsOf (check f (gateOver f calculations (Some registry)) (stating "3%" reference)))
                "stated at a coarser precision, it still states the value"
        }

        test "a misquoted calculation is refused with its correct rendering in the offence" {
            let f = fixture ()
            let september, october = revenueMonths f

            let reference, _ =
                requested f calculations (Some registry) "revenue-change" [ september.FactId; october.FactId ]

            Expect.equal
                (reasonsOf (check f (gateOver f calculations (Some registry)) (stating "2.8%" reference)))
                [ MisstatedValue(reference, "2.8%", "2.7%") ]
                "the repair loop is told the figure the server derived"
        }

        test "an undeclared calculation is refused by the tool and by the gate" {
            let f = fixture ()
            let september, october = revenueMonths f

            Expect.stringContains
                (refusalOf f calculations (Some registry) "revenue-median" [ september.FactId; october.FactId ])
                "calculation 'revenue-median' is not declared; the declared ones are revenue-change, revenue-gap"
                "the tool names what is declared"

            let forged =
                sprintf "calc:revenue-median@1/%s/%s/%s" september.FactId october.FactId (String('0', 64))

            Expect.equal
                (reasonsOf (check f (gateOver f calculations (Some registry)) (stating "2.7%" forged)))
                [ UnresolvedReference forged ]
                "the gate resolves no undeclared calculation"
        }

        test "a stale input is refused, naming the reference the calculation has now" {
            let f = fixture ()
            let september, october = revenueMonths f

            let reference, _ =
                requested f calculations (Some registry) "revenue-change" [ september.FactId; october.FactId ]

            let restated = assertFact f (draft "revenue" 10 1100m Surfaceable)
            Expect.notEqual restated.FactId october.FactId "October is restated"

            match reasonsOf (check f (gateOver f calculations (Some registry)) (stating "2.7%" reference)) with
            | [ SupersededReference(cited, Some successor) ] ->
                Expect.equal cited reference "the stale reference is refused"

                Expect.stringStarts
                    successor
                    (sprintf "calc:revenue-change@1/%s/%s/" september.FactId restated.FactId)
                    "and the successor is the same calculation over the current head"
            | other -> failtestf "a stale input must be refused as superseded, got %A" other

            Expect.stringContains
                (refusalOf f calculations (Some registry) "revenue-change" [ september.FactId; october.FactId ])
                (sprintf "input '%s' is superseded by '%s'" october.FactId restated.FactId)
                "the tool refuses a stale input too"
        }

        test "a withheld input is refused by the tool and by the gate" {
            let f = fixture ()
            let september = assertFact f (draft "revenue" 9 1000m Surfaceable)
            let october = assertFact f (draft "revenue" 10 1027m Internal)

            Expect.stringContains
                (refusalOf f calculations (Some registry) "revenue-change" [ september.FactId; october.FactId ])
                (sprintf "input '%s' is not disclosable here" october.FactId)
                "the tool checks disclosure first"

            // A reference built by hand for the same inputs, as if issued
            // before the input was reclassified.
            let display =
                CalculationDeclaration.evaluate revenueChange [ 1000m; 1027m ]
                |> Result.defaultWith (fun e -> failtestf "%A" e)

            let record =
                CalculationDeclaration.record revenueChange [ september.FactId; october.FactId ] display
                |> Result.defaultWith (fun e -> failtestf "%A" e)

            match reasonsOf (check f (gateOver f calculations (Some registry)) (stating "2.7%" record.Reference)) with
            | [ UndisclosableReference(cited, _) ] -> Expect.equal cited record.Reference "withheld at the surface"
            | other -> failtestf "a withheld input must be refused, got %A" other
        }

        test "a declaration whose version moved, and an address that does not match, are refused" {
            let f = fixture ()
            let september, october = revenueMonths f

            let reference, _ =
                requested f calculations (Some registry) "revenue-change" [ september.FactId; october.FactId ]

            let bumped = declared [ { revenueChange with Version = 2 }; revenueGap ]

            match reasonsOf (check f (gateOver f bumped (Some registry)) (stating "2.7%" reference)) with
            | [ SupersededReference(_, Some successor) ] ->
                Expect.stringStarts successor "calc:revenue-change@2/" "the successor is under the new version"
            | other -> failtestf "a moved version must be refused as superseded, got %A" other

            let tampered =
                reference.Substring(0, reference.Length - 1)
                + (if reference.EndsWith "0" then "1" else "0")

            Expect.equal
                (reasonsOf (check f (gateOver f calculations (Some registry)) (stating "2.7%" tampered)))
                [ UnresolvedReference tampered ]
                "an address that does not match the derivation"

            let reRounded =
                declared [
                    {
                        revenueChange with
                            Rounding = {
                                Places = 2
                                Mode = CalculationRoundingMode.HalfEven
                            }
                    }
                ]

            Expect.equal
                (reasonsOf (check f (gateOver f reRounded (Some registry)) (stating "2.7%" reference)))
                [ UnresolvedReference reference ]
                "a declaration changed without a version bump no longer matches its address"
        }

        test "inputs of an undeclared metric, of another currency, or with no registry to check, are refused" {
            let f = fixture ()
            let september = assertFact f (draft "revenue" 9 1000m Surfaceable)
            let units = assertFact f (draft "units" 10 40m Surfaceable)
            let costs = assertFact f (draft "cost" 10 40m Surfaceable)

            Expect.stringContains
                (refusalOf f calculations (Some registry) "revenue-change" [ september.FactId; costs.FactId ])
                "is a 'cost' fact, and the calculation is declared over revenue"
                "the metric check"

            Expect.stringContains
                (refusalOf f calculations (Some registry) "revenue-gap" [ september.FactId; units.FactId ])
                (sprintf "input '%s' is in 'count', and the calculation is declared in GBP" units.FactId)
                "the currency check"

            Expect.stringContains
                (refusalOf f calculations None "revenue-gap" [ september.FactId; september.FactId ])
                "no metric registry is composed"
                "a currency cannot be checked without a registry"

            Expect.stringContains
                (refusalOf f calculations (Some registry) "revenue-change" [ september.FactId ])
                "takes from, to, and 1 were given"
                "the arity check"

            let zero = assertFact f (draft "revenue" 11 0m Surfaceable)
            let august = assertFact f (draft "revenue" 8 5m Surfaceable)

            Expect.stringContains
                (refusalOf f calculations (Some registry) "revenue-change" [ zero.FactId; august.FactId ])
                "would divide by zero"
                "a zero denominator is refused by name"
        }

        test "a figure without a calc reference is still refused" {
            let f = fixture ()
            let _, october = revenueMonths f
            let gate = gateOver f calculations (Some registry)

            Expect.equal
                (reasonsOf (check f gate (docOf [ InlineSpan.Text "Revenue rose 2.7% on last month." ])))
                [ UnreferencedNumber ]
                "a prose percentage"

            Expect.equal
                (reasonsOf (check f gate (stating "2.7%" october.FactId)))
                [ MisstatedValue(october.FactId, "2.7%", "1027") ]
                "a percentage citing one of its inputs misstates that input"
        }

        test "a deployment with no declarations is unchanged" {
            let f = fixture ()
            let september, october = revenueMonths f

            let reference, _ =
                requested f calculations (Some registry) "revenue-change" [ september.FactId; october.FactId ]

            let undeclared =
                FactNarrativeGroundingGate.create f.Store f.Disclosure (Some registry) [] f.Issuer

            Expect.equal
                (reasonsOf (check f undeclared (stating "2.7%" reference)))
                [ UnregisteredReferenceKind(AuthorisedCalculation.ReferenceKind, reference) ]
                "no declaration, no kind: a calc reference is an unregistered kind, as before"

            Expect.equal
                (reasonsOf (check f (gateOver f NarrativeCalculations.empty (Some registry)) (stating "2.7%" reference)))
                [ UnregisteredReferenceKind(AuthorisedCalculation.ReferenceKind, reference) ]
                "empty calculations are the same gate"
        }
    ]

let promptTests =
    testList "the run's instruction" [

        test "a run given the calculate tool is told to request, never compute; a run without it is unchanged" {
            let definition =
                GroundedNarrativeDefinition.create
                    "monthly"
                    "Monthly summary"
                    "sales"
                    "narrator"
                    "Monthly summary"
                    "Summarise the month's revenue."
                    [
                        {
                            Id = "summary"
                            Heading = "Summary"
                            Guidance = "One paragraph."
                        }
                    ]

            let rule =
                ToolUp.AI.GroundedNarrativeRun.calculationRule AuthorisedCalculation.ToolName

            let without =
                ToolUp.AI.GroundedNarrativeRun.systemPrompt definition [ "query_facts" ]

            let given =
                ToolUp.AI.GroundedNarrativeRun.systemPrompt definition [ "query_facts"; AuthorisedCalculation.ToolName ]

            Expect.isFalse (without.Contains rule) "no tool, no rule"
            Expect.stringContains given rule "the tool brings the rule"

            Expect.equal
                (given.Replace(rule + Environment.NewLine, ""))
                (without.Replace("query_facts", "query_facts, calculate"))
                "and nothing else moves"
        }
    ]

// ── Signing ───────────────────────────────────────────────────────

let signingTests =
    testList "the signed calculation admission" [

        testCaseAsync "every calculation a published report uses is sealed in its certificate, and an edit fails"
        <| async {
            let f = fixture ()
            let september, october = revenueMonths f

            let reference, _ =
                requested f calculations (Some registry) "revenue-change" [ september.FactId; october.FactId ]

            let gate = gateOver f calculations (Some registry)
            let document = stating "2.7%" reference
            let scope = TestScope.resolved f.Scope

            let! verdict = gate.Check(scope, "reader", FactNarrativePublication, document)

            let citations =
                match verdict with
                | Grounded citations -> citations
                | other -> failtestf "must pass first, got %A" other

            let instructed = gate :?> IInstructedNarrativeGroundingGate

            match!
                instructed.CertifyWith(
                    scope,
                    "reader",
                    FactNarrativePublication,
                    "narrative-calc",
                    document,
                    GroundingCheckOptions.none,
                    citations
                )
            with
            | Error reason -> failtestf "the certificate must issue: %s" reason
            | Ok issued ->
                Expect.containsAll
                    issued.CitedFactIds
                    [ september.FactId; october.FactId ]
                    "the chain reaches the calculation's inputs"

                let sealedCert =
                    JsonSerializer.Deserialize<GroundingCertificate>(issued.CertificateJson, FableConverters.create ())

                let admission =
                    match sealedCert.Body.Admissions with
                    | Some [ admission ] -> admission
                    | other -> failtestf "one admission must be sealed, got %A" other

                Expect.equal admission.Kind AuthorisedCalculation.ReferenceKind "a calculation admission"
                Expect.equal admission.Reference reference "as cited"
                Expect.equal admission.Location "section 'summary' › element 1 › span 2" "where it is cited"

                let attribute name =
                    CertificateAdmission.attribute name admission |> Option.defaultValue []

                Expect.equal (attribute CertificateAdmission.OperationAttribute) [ "percentChange" ] "its operator"

                Expect.equal
                    (attribute CertificateAdmission.InputsAttribute)
                    [ september.FactId; october.FactId ]
                    "its inputs, in order"

                Expect.equal (attribute CertificateAdmission.RoundingAttribute) [ "1"; "half-even" ] "its rounding"
                Expect.equal (attribute CertificateAdmission.ResultAttribute) [ "2.7" ] "its result"
                Expect.equal (attribute CertificateAdmission.DeclarationVersionAttribute) [ "1" ] "its version"

                let verifier = DefaultArtefactVerifier.create f.Secrets

                match! GroundingCertificate.verify verifier sealedCert with
                | Ok() -> ()
                | Error e -> failtestf "the certificate must verify: %s" (VerificationError.describe e)

                let edited = {
                    admission with
                        Attributes =
                            admission.Attributes
                            |> List.map (fun a ->
                                if a.Name = CertificateAdmission.ResultAttribute then
                                    { a with Values = [ "2.8" ] }
                                else
                                    a)
                }

                let tampered = {
                    sealedCert with
                        Body = {
                            sealedCert.Body with
                                Admissions = Some [ edited ]
                        }
                }

                match! GroundingCertificate.verify verifier tampered with
                | Error Tampered -> ()
                | other -> failtestf "an edited calculation admission must fail verification; got %A" other
        }
    ]

let tests =
    testList "Phase 1180 — authorised narrative calculations" [
        declarationTests
        differentialTests
        gateTests
        promptTests
        signingTests
    ]
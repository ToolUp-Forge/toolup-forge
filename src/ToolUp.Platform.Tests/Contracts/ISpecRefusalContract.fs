// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.ISpecRefusalContract

open System
open Microsoft.FSharp.Reflection
open Expecto
open ToolUp.Facts

/// Contract test list for an `ISpecRefusal` implementation: the refusal type a
/// spec tool answers with, which the model reads to repair its call.
///
/// Callers pass a display name, the refusal type, and one sample per case.
/// The laws are what a model can rely on whichever tool refused:
///
///   1. Every case is sampled, so no case escapes the laws below.
///   2. `Name` is a stable identifier of the cause: non-empty, one word, and
///      distinct across the type's cases.
///   3. `Name`, `Remedy` and `Admissible` are functions of the value.
///   4. `Remedy` is one non-empty line.
///   5. `Admissible` holds no empty or repeated entry, and the remedy names
///      every entry, because the remedy is the text the model reads.
let tests (name: string) (refusalType: Type) (samples: ISpecRefusal list) =
    let caseOf (r: ISpecRefusal) =
        (FSharpValue.GetUnionFields(r, refusalType) |> fst).Name

    testList $"{name} — ISpecRefusal contract" [
        test "every case of the refusal type is sampled" {
            let cases = FSharpType.GetUnionCases refusalType |> Array.map _.Name |> Set.ofArray

            let sampled = samples |> List.map caseOf |> Set.ofList
            Expect.equal (Set.difference cases sampled) Set.empty "every case has a sample"
        }

        test "Name is one word, and distinct across cases" {
            for r in samples do
                Expect.isTrue
                    (r.Name.Length > 0
                     && Char.IsUpper r.Name[0]
                     && r.Name |> Seq.forall Char.IsLetterOrDigit)
                    $"'{r.Name}' ({caseOf r}) is an identifier"

            let byName = samples |> List.groupBy _.Name

            for n, rs in byName do
                Expect.equal
                    (rs |> List.map caseOf |> List.distinct |> List.length)
                    1
                    $"the name '{n}' identifies one cause"
        }

        test "Name, Remedy and Admissible are functions of the value" {
            for r in samples do
                Expect.equal r.Name r.Name "Name is stable"
                Expect.equal r.Remedy r.Remedy "Remedy is stable"
                Expect.equal r.Admissible r.Admissible "Admissible is stable"
        }

        test "Remedy is one non-empty line" {
            for r in samples do
                Expect.isFalse (String.IsNullOrWhiteSpace r.Remedy) $"{r.Name} has a remedy"

                Expect.isFalse (r.Remedy.Contains '\n' || r.Remedy.Contains '\r') $"{r.Name}'s remedy is one line"
        }

        test "Admissible is a set the remedy names" {
            for r in samples do
                for a in r.Admissible do
                    Expect.isFalse (String.IsNullOrWhiteSpace a) $"{r.Name} admits no empty entry"
                    Expect.stringContains r.Remedy a $"{r.Name}'s remedy names '{a}'"

                Expect.equal
                    (r.Admissible |> List.distinct |> List.length)
                    r.Admissible.Length
                    $"{r.Name} admits no entry twice"
        }
    ]

// ── Samples of every shipped implementation, one per case ──────────

let private withheld: PipelineWithheldCount list = [ { PolicyRef = "policy-a"; Count = 2 } ]

/// One `PipelineRefusal` per case.
let pipelineRefusals: ISpecRefusal list = [
    PipelineRefusal.MalformedArguments "the arguments are not an object"
    PipelineRefusal.SliceRefused "unknown table 'sales'"
    PipelineRefusal.NoFacts "sales"
    PipelineRefusal.NothingDisclosable(4, withheld)
    PipelineRefusal.MalformedPipeline(Some 1, "a step has no verb")
    PipelineRefusal.NamedSource(2, "join")
    PipelineRefusal.UnboundParameter(1, [ "p1"; "p2" ])
    PipelineRefusal.UnpinnedClock 3
    PipelineRefusal.UnknownColumn(2, "x", [ "alpha"; "beta" ])
    PipelineRefusal.OpenSchema(1, "a pivot names its columns from the data")
    PipelineRefusal.EvaluationFailed "an overflow"
    PipelineRefusal.ResultOverCap(5000, 1000)
    PipelineRefusal.AssertionNotOffered
    PipelineRefusal.MalformedAssertion "no metric column"
    PipelineRefusal.AssertedColumnUnknown("margin", [ "revenue"; "cost" ])
    PipelineRefusal.AssertedColumnWithheld("revenue", withheld)
    PipelineRefusal.AssertedColumnNotNumeric("region", "text")
    PipelineRefusal.AssertedColumnHostRounded "share"
    PipelineRefusal.UnregisteredMetric "margin"
    PipelineRefusal.NotADerivedMetric("revenue", Some "ingest")
    PipelineRefusal.SubjectKeyUnresolved "no subject column"
    PipelineRefusal.SubjectRepeated "store-1"
    PipelineRefusal.ValueMissing "store-2"
    PipelineRefusal.InputsUnnamed "store-3"
    PipelineRefusal.InputsOverCap("store-4", 300, 256)
    PipelineRefusal.AssertionFailed "the batch conflicts"
    PipelineRefusal.OutsideProfile(1, "verb", "window", [ "filter"; "select" ])
    PipelineRefusal.JoinRefused(2, "the relation check refused it")
]

/// One `SpecDoorRefusal` per case.
let doorRefusals: ISpecRefusal list = [
    SpecDoorRefusal.MalformedArguments "the arguments are not an object"
    SpecDoorRefusal.SliceRefused "unknown table 'sales'"
    SpecDoorRefusal.NoFacts "sales"
    SpecDoorRefusal.NothingDisclosable(4, withheld)
    SpecDoorRefusal.ResultOverCap(5000, 1000)
    SpecDoorRefusal.UnregisteredMetric "margin"
    SpecDoorRefusal.NotADerivedMetric("revenue", Some "ingest", "total_metric")
    SpecDoorRefusal.InputsUnnamed "store-3"
    SpecDoorRefusal.InputsOverCap("store-4", 300, 256)
    SpecDoorRefusal.AssertionFailed "the batch conflicts"
]
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Remoting.ApiRecordCensusTests

open Expecto
open ToolUp.Remoting.Generator
open ToolUp.Remoting.Server

// ─── Phase 946 — the API-record census can exclude a record ───────────
//
// `Plan.isApiRecord` counts any record whose every field is a function, so
// a record of that shape that is NOT a remoting contract was reported as
// one. An attribute named `NotRemotingApiAttribute`, in any namespace,
// takes a record out of the census. Two planted records of the same shape
// prove the exclusion is the attribute's and nothing else's: the unmarked
// one is still counted.

/// Declared here, as a contract assembly that references only FSharp.Core
/// would declare it: the generator matches the attribute by name.
type NotRemotingApiAttribute() =
    inherit System.Attribute()

/// All-function, unmarked: the census counts it.
type PlantedCallbacks = {
    OnSaved: string -> unit
    OnFailed: string -> unit
}

/// The same shape, marked: the census must not count it.
[<NotRemotingApi>]
type PlantedMarkedCallbacks = {
    OnSaved: string -> unit
    OnFailed: string -> unit
}

let private censusOfThisAssembly () =
    Plan.apiRecordsIn typeof<PlantedCallbacks>.Assembly

let tests =
    testList "Phase 946 — the API-record census excludes a marked record" [

        test "an unmarked all-function record is counted (the shape rule is unchanged)" {
            Expect.isTrue (Plan.isApiRecord typeof<PlantedCallbacks>) "counted by shape"
            Expect.contains (censusOfThisAssembly ()) typeof<PlantedCallbacks> "in the assembly census"
        }

        test "a marked all-function record is excluded" {
            Expect.isFalse (Plan.isApiRecord typeof<PlantedMarkedCallbacks>) "the attribute excludes it"

            Expect.isFalse
                (censusOfThisAssembly () |> List.contains typeof<PlantedMarkedCallbacks>)
                "and the assembly census does not list it"
        }

        test "every platform API record reads to a contract with no refusal" {
            // `BudgetAccount` is a record of callbacks the shape census counts
            // but no dispatcher mounts; what counts as an API record is the
            // analyzer's definition to settle, not this census's.
            let notMounted = set [ "BudgetAccount" ]

            let platformRecords =
                Plan.apiRecordsIn typeof<ToolUp.Platform.RequiresRoleAttribute>.Assembly
                |> List.filter (fun record -> not (notMounted.Contains record.Name))

            Expect.equal (List.length platformRecords) 40 "the forty platform API records"

            let flags = {
                ContractFlags.defaults with
                    AuthResolverComposed = true
                    RecordSeamsComposed = true
            }

            let refused =
                platformRecords
                |> List.choose (fun record ->
                    match Contract.ofType flags record with
                    | Ok _ -> None
                    | Error refusals -> Some(record.Name, ContractRefusal.render refusals))

            if not (List.isEmpty refused) then
                failtestf
                    "%d platform API record(s) the contract refuses:
%s"
                    refused.Length
                    (refused
                     |> List.map (fun (name, why) -> name + ": " + why)
                     |> String.concat
                         "
")
        }

        test "the exclusion is matched by the attribute's name" {
            Expect.equal Plan.NotRemotingApiAttributeName typeof<NotRemotingApiAttribute>.Name "one name"
            Expect.isTrue (Plan.isExcludedFromApiCensus typeof<PlantedMarkedCallbacks>) "marked"
            Expect.isFalse (Plan.isExcludedFromApiCensus typeof<PlantedCallbacks>) "unmarked"
        }
    ]
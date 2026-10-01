// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Remoting.ApiRecordCensusTests

open Expecto
open ToolUp.Remoting.Generator

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

        test "the exclusion is matched by the attribute's name" {
            Expect.equal Plan.NotRemotingApiAttributeName typeof<NotRemotingApiAttribute>.Name "one name"
            Expect.isTrue (Plan.isExcludedFromApiCensus typeof<PlantedMarkedCallbacks>) "marked"
            Expect.isFalse (Plan.isExcludedFromApiCensus typeof<PlantedCallbacks>) "unmarked"
        }
    ]
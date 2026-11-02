// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 854 — the .NET host reads the `[<Cacheable>]` / `[<Invalidates>]`
/// declaration, and it is the same declaration the Fable pack registers.
/// The proxy behaviour itself is exercised under Fable
/// (`ToolUp.AI.Client.Tests` `ReadPolicyTests`), where the proxy runs.
module ToolUp.Platform.Tests.Remoting.ReadPolicyAttributeTests

open Expecto
open ToolUp.Remoting.Client
open ToolUp.Platform.Tests.Remoting.ReadPolicyFixture

type private NothingDeclared = {
    Read: string -> Async<int>
    Write: int -> Async<unit>
}

let tests =
    testList "Phase 854 — client read-policy attributes" [
        testCase "ofAttributes reads the record's attributes as the data a Fable client registers"
        <| fun () ->
            Expect.equal
                (ReadPolicies.ofAttributes typeof<ReadCatalogApi>)
                declarations
                "the attribute declaration and the registered declaration agree"

        testCase "an undeclared record yields no policy"
        <| fun () -> Expect.isEmpty (ReadPolicies.ofAttributes typeof<NothingDeclared>) "no policy"

        testCase "a negative max age is refused at declaration"
        <| fun () ->
            Expect.throwsT<System.ArgumentException> (fun () -> ReadPolicy.cacheable -1 |> ignore) "a negative max age"
    ]
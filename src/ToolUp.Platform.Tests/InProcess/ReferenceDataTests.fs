// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ReferenceDataTests

open System
open System.IO
open System.Text
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.Compose
open ToolUp.Platform.Testing

let private columns: ReferenceColumn list = [
    {
        Name = "Date"
        Type = ReferenceColumnType.Date
    }
    {
        Name = "Week"
        Type = ReferenceColumnType.Integer
    }
    {
        Name = "Label"
        Type = ReferenceColumnType.Text
    }
]

let private declare (text: string) : ReferenceDataset * byte[] =
    let bytes = Encoding.UTF8.GetBytes text

    {
        Id = "fiscal"
        Source = "fiscal.csv"
        ContentHash = ReferenceData.hashOf bytes
        Columns = columns
    },
    bytes

let private refusal (text: string) : string =
    let dataset, bytes = declare text

    match ReferenceData.read dataset bytes with
    | Error reason -> reason
    | Ok _ -> failtestf "'%s' was read" text

let private good = "Date,Week,Label\n2026-11-06,45,autumn\n"

[<Tests>]
let tests =
    testList "Declared reference data" [
        testCase "a dataset reads into typed cells, in column order"
        <| fun _ ->
            let dataset, bytes = declare good

            match ReferenceData.read dataset bytes with
            | Ok verified ->
                Expect.equal
                    verified.Rows
                    [
                        [
                            ReferenceCell.Date(DateOnly(2026, 11, 6))
                            ReferenceCell.Integer 45L
                            ReferenceCell.Text "autumn"
                        ]
                    ]
                    "the one row"
            | Error reason -> failtest reason

        testCase "CRLF and no final line separator read the same"
        <| fun _ ->
            let dataset, bytes = declare "Date,Week,Label\r\n2026-11-06,45,autumn"

            match ReferenceData.read dataset bytes with
            | Ok verified -> Expect.equal verified.Rows.Length 1 "one row"
            | Error reason -> failtest reason

        testCase "a comment-headed file reads, its comments hashed and otherwise ignored"
        <| fun _ ->
            let text = "# provenance\n# second line\n" + good
            let dataset, bytes = declare text

            match ReferenceData.read dataset bytes with
            | Ok verified -> Expect.equal verified.Rows.Length 1 "the comments are no rows"
            | Error reason -> failtest reason

            let changed = Encoding.UTF8.GetBytes("# edited\n# second line\n" + good)

            Expect.isError (ReferenceData.read dataset changed) "an edited comment changes the hash"

        testCase "bytes that do not hash to the declaration are refused, naming the dataset and both hashes"
        <| fun _ ->
            let dataset, _ = declare good
            let other = Encoding.UTF8.GetBytes(good + "2026-11-07,45,autumn\n")

            match ReferenceData.read dataset other with
            | Error reason ->
                Expect.stringContains reason "'fiscal'" "the dataset"
                Expect.stringContains reason dataset.ContentHash "the declared hash"
                Expect.stringContains reason (ReferenceData.hashOf other) "the actual hash"
            | Ok _ -> failtest "read"

        testCase "a header other than the declared one is refused"
        <| fun _ ->
            Expect.stringContains
                (refusal "Date,Label,Week\n2026-11-06,autumn,45\n")
                "the header is 'Date,Label,Week'"
                "the header"

        testCase "a comment after the header is a row, not a comment"
        <| fun _ -> Expect.stringContains (refusal "Date,Week,Label\n# late\n") "line 2" "the line"

        testCase "a file of comments alone has no header"
        <| fun _ -> Expect.stringContains (refusal "# only\n") "no header" "named"

        testCase "a cell that does not read as its column's type is refused, naming the line and column"
        <| fun _ ->
            let date = refusal "Date,Week,Label\n2026-02-30,45,autumn\n"
            Expect.stringContains date "line 2" "the line"
            Expect.stringContains date "column 'Date'" "the column"

            for bad in [ "+45"; " 45"; "4,5"; ""; "-" ] do
                let reason = refusal (sprintf "Date,Week,Label\n2026-11-06,%s,autumn\n" bad)
                Expect.stringContains reason "line 2" (sprintf "'%s' is refused" bad)

        testCase "a line with the wrong field count is refused, naming the line and both counts"
        <| fun _ ->
            let reason = refusal "Date,Week,Label\n2026-11-06,45,autumn\n2026-11-07,45\n"
            Expect.stringContains reason "line 3" "the line"
            Expect.stringContains reason "2 fields, and the header declares 3" "the counts"

        testCase "bytes that are not UTF-8 are refused by name, not raised as a decoder fault"
        <| fun _ ->
            let bytes = [| 0xC3uy; 0x28uy |]

            let dataset = {
                fst (declare good) with
                    ContentHash = ReferenceData.hashOf bytes
            }

            match ReferenceData.read dataset bytes with
            | Error reason -> Expect.stringContains reason "not UTF-8" "named"
            | Ok _ -> failtest "read"

        testCase "verifyAll names every failure: a missing file, a bad hash and a repeated id"
        <| fun _ ->
            use dir = TempDir.create "reference"
            let directory = dir.Path

            File.WriteAllText(Path.Combine(directory, "fiscal.csv"), good)
            let dataset, _ = declare good

            let verified = ReferenceData.verifyAll directory [ dataset ]
            Expect.equal verified.Length 1 "a good file verifies"

            let missing = {
                dataset with
                    Id = "missing"
                    Source = "missing.csv"
            }

            let tampered = {
                dataset with
                    Id = "tampered"
                    ContentHash = "sha256:00"
            }

            try
                ReferenceData.verifyAll directory [ missing; tampered; tampered ] |> ignore
                failtest "verified"
            with :? ReferenceDataRefusedException as refused ->
                Expect.stringContains refused.Message "'missing'" "the missing file"
                Expect.stringContains refused.Message "'tampered' is declared more than once" "the repeat"
                Expect.stringContains refused.Message "sha256:00" "the bad hash"

        testCase "withReferenceData refuses at the call, and what verifies is readable by id afterwards"
        <| fun _ ->
            use dir = TempDir.create "reference"
            let directory = dir.Path

            let path = Path.Combine(directory, "fiscal.csv")
            File.WriteAllText(path, good)

            let dataset = {
                fst (declare good) with
                    Source = path
            }

            let other = { dataset with Id = "other" }

            let app =
                ServerApp.empty
                |> ServerApp.withReferenceData [ dataset ]
                |> ServerApp.withReferenceData [ other ]

            let services = ServiceCollection()
            (ServerApp.serviceConfig app).Value services |> ignore
            use provider = services.BuildServiceProvider()

            let registry = provider.GetRequiredService<IReferenceData>()
            Expect.isSome (registry.Dataset "fiscal") "the first call's dataset"
            Expect.isSome (registry.Dataset "other") "the second call's dataset"
            Expect.isNone (registry.Dataset "never") "nothing else"

            Expect.throwsT<ReferenceDataRefusedException>
                (fun () ->
                    ServerApp.empty
                    |> ServerApp.withReferenceData [
                        {
                            dataset with
                                ContentHash = "sha256:00"
                        }
                    ]
                    |> ignore)
                "a dataset that does not verify stops the composition"
    ]
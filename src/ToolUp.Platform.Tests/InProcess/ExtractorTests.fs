module ToolUp.Platform.Tests.InProcess.ExtractorTests

// The knowledge base's CSV extractor reads records through the RFC 4180
// reader `ToolUp.Tabular` ships, so a quoted field keeps its embedded
// delimiters and line breaks. An unquoted file reads exactly as the
// line-and-comma split read it, so its chunk text does not move.

open System
open System.IO
open Expecto
open KnowledgeBase.ServerExtractors

let private noOpOcr = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
let private noOpTables = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()

/// The rule the extractor applied before it used the quoted reader:
/// non-empty lines, each split on ',' with every cell trimmed.
let private lineSplit (text: string) : string[] list =
    text.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun line -> line.Split(',') |> Array.map _.Trim())
    |> Array.toList

let private quotedReader (text: string) : string[] list =
    use reader = new StringReader(text)

    ToolUp.Tabular.Csv.parseRecords ',' reader
    |> Seq.choose (fun (_, record) ->
        match record with
        | Ok fields -> Some(fields |> Array.map _.Trim())
        | Error _ -> None)
    |> Seq.toList

let private quoted =
    "name,notes,amount\r\n\"Smith, J\",\"line one\nline two\",12\r\nJones,plain,7\r\n"

let private unquoted =
    "region,quarter,revenue\nnorth,Q1,100\n\nsouth,Q1,  200 \neast,Q2,300\n"

let tests =
    testList "KnowledgeBase CSV extraction" [
        test "a quoted comma and a quoted newline yield the quoted reader's cells" {
            let rows = csvRows quoted
            Expect.equal rows (quotedReader quoted) "the cells are the quoted reader's"

            Expect.equal
                rows[1]
                [| "Smith, J"; "line one\nline two"; "12" |]
                "the quoted fields keep their comma and their line break"

            Expect.notEqual rows (lineSplit quoted) "a ',' split would have broken the quoted fields apart"
        }

        test "the quoted cells reach the chunk text" {
            let chunks =
                extractChunks noOpOcr noOpTables "doc-1" "people.csv" (Text.Encoding.UTF8.GetBytes quoted)
                |> Async.RunSynchronously
                |> List.map (fun (chunk, _) -> chunk.Content)
                |> String.concat "\n"

            let expectedRow =
                ToolUp.RAG.Chunking.formatRow [| "name"; "notes"; "amount" |] [|
                    "Smith, J"
                    "line one\nline two"
                    "12"
                |]

            Expect.stringContains chunks expectedRow "the row is formatted from its real cells"
        }

        test "an unquoted CSV reads exactly as the line-and-comma split read it" {
            Expect.equal (csvRows unquoted) (lineSplit unquoted) "same cells, so the same chunk text"
        }
    ]
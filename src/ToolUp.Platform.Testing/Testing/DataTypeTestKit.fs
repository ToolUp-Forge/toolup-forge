// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Testing.DataTypeTestKit

open ToolUp.Platform.FileProcessor

// ─── DataType assertion helpers ───────────────────────────────────────
//
// Two halves of the data-type contract: `Detect` (does the input
// look like this type?) and `Process` (does the parser yield the
// expected payload + summary?). These helpers raise on assertion
// failure so they slot into any test runner — Expecto, xUnit,
// hand-rolled.

/// Assert that `dt.Detect content` resolves to `true`. Raises on
/// failure with the DataType's id for traceability.
let expectDetect (dt: DataType) (content: string) =
    let result = dt.Detect content |> Async.RunSynchronously

    if not result then
        failwithf "DataTypeTestKit.expectDetect: '%s' did not detect the supplied content" dt.Id

/// Assert that `dt.Detect content` resolves to `false`.
let expectNotDetect (dt: DataType) (content: string) =
    let result = dt.Detect content |> Async.RunSynchronously

    if result then
        failwithf "DataTypeTestKit.expectNotDetect: '%s' incorrectly detected the supplied content" dt.Id

/// Run `Process` and pass the result through a caller-supplied
/// predicate. Used for assertions that need to inspect the parsed
/// payload + summary together.
let expectProcess
    (dt: DataType)
    (fileName: string)
    (content: string)
    (assertion: ProcessedDataTypes.ProcessedData * ProcessedDataTypes.ProcessedFileEntry -> bool)
    =
    let result = dt.Process(fileName, content) |> Async.RunSynchronously

    if not (assertion result) then
        let processed, summary = result

        failwithf
            "DataTypeTestKit.expectProcess: '%s' processed output failed assertion. Payload=%A Summary=%A"
            dt.Id
            processed
            summary

/// Convenience: run `Process` and return the parsed pair so the
/// caller can inspect it inline.
let process_ (dt: DataType) (fileName: string) (content: string) =
    dt.Process(fileName, content) |> Async.RunSynchronously
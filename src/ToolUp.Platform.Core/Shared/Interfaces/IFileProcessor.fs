// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.FileProcessor

open DataManagementTypes
open ProcessedDataTypes

// ─── Unified data type registration ──────────────────────────────

/// A module's complete registration for a data type:
/// what it's called, how to detect it, and how to process it.
///
/// Both `Detect` and `Process` are async to permit I/O-bound implementations
/// (classifier services, remote parsing, cloud-warehouse lookups) without
/// requiring a future interface change. In-process implementations wrap
/// their sync logic in `async { return ... }`.
type DataType = {
    /// Shared metadata (Id + DisplayName), declared in the module's SharedTypes.
    Info: DataTypeInfo
    /// Unique identifier for this data type (e.g. "SalesData", "OptimisationData")
    Id: DataTypeId
    /// Attempt to detect this data type from raw content.
    /// Returns true if the content matches this type. The content parameter
    /// is opaque — could be CSV text, JSON, or any string representation.
    Detect: string -> Async<bool>
    /// Process the content into a tagged JSON payload and a summary entry.
    /// Parameters: (fileName, contents)
    /// Returns: `ProcessedData` (TypeName + JSON payload) plus a
    /// `ProcessedFileEntry` with summary info for client display.
    Process: string * string -> Async<ProcessedData * ProcessedFileEntry>
}

// ─── CSV detection helpers ────────────────────────────────────────

/// Optional helpers for CSV-based file type detection.
/// Modules are free to implement Detect any way they like;
/// these just make the common CSV header-matching case concise.
module CsvHeaders =

    /// Parse the first row of CSV content into a lowercase header set
    let parse (contents: string) : Set<string> =
        let firstLine =
            contents.Split([| "\r\n"; "\n"; "\r" |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead

        match firstLine with
        | Some line -> line.Split(',') |> Array.map _.Trim().ToLowerInvariant() |> Set.ofArray
        | None -> Set.empty

    /// True if the header set contains all of the given names
    let containsAll (required: string list) (headers: Set<string>) =
        required |> List.forall headers.Contains

    /// True if the header set contains at least one of the given names
    let containsAny (alternatives: string list) (headers: Set<string>) =
        alternatives |> List.exists headers.Contains

    /// The number of columns in the first row
    let columnCount (contents: string) : int =
        let firstLine =
            contents.Split([| "\r\n"; "\n"; "\r" |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead

        match firstLine with
        | Some line -> line.Split(',').Length
        | None -> 0
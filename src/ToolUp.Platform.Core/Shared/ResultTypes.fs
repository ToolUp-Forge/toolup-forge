// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Analytical-result types ──────────────────────────────────────
//
// Types returned by `IResultStore.CompareVersions`, plus the
// reserved `EventType` constant for `AnalysisCompleted` events. All
// Fable-shared so admin UIs and AI tools can decode the diff
// structure without server-only deps.

/// One leaf-level change between two versions of a JSON-formatted
/// result. `Path` is a JSON-pointer-like dot path (e.g.
/// `summary.revenue`); `From = None` means the field was added in
/// `To`; `To = None` means it was removed. Values are JSON-encoded
/// strings — callers parse if they need typed access.
type FieldChange = {
    Path: string
    From: string option
    To: string option
}

/// Result of `IResultStore.CompareVersions`. `Changes` is the
/// flattened list of leaf differences between `FromVersion` and
/// `ToVersion`. Empty list means the two versions are byte-for-byte
/// identical (which is rare but possible when the same content is
/// re-saved).
type ResultDiff = {
    ObjectId: string
    FromVersion: int
    ToVersion: int
    Changes: FieldChange list
}

/// Reserved `EventType` constants for analytical-result events.
/// `IEventStore.ReadByType(scopeId, ResultEventType.AnalysisCompleted)`
/// returns the audit trail of analyses run in a scope.
module ResultEventType =
    [<Literal>]
    let AnalysisCompleted = "AnalysisCompleted"

/// The `result { }` computation expression: `let!` binds an `Ok` and
/// short-circuits on the first `Error`, in the order the expression reads.
/// FSharp.Core only, and Fable-safe.
type ResultBuilder() =
    member _.Return(value: 'T) : Result<'T, 'E> = Ok value
    member _.ReturnFrom(result: Result<'T, 'E>) : Result<'T, 'E> = result
    member _.Bind(result: Result<'T, 'E>, binder: 'T -> Result<'U, 'E>) : Result<'U, 'E> = Result.bind binder result
    member _.Zero() : Result<unit, 'E> = Ok()
    member _.Delay(body: unit -> Result<'T, 'E>) : unit -> Result<'T, 'E> = body
    member _.Run(body: unit -> Result<'T, 'E>) : Result<'T, 'E> = body ()

    member _.Combine(first: Result<unit, 'E>, rest: unit -> Result<'T, 'E>) : Result<'T, 'E> = Result.bind rest first

/// The `result` builder and the patterns that read a validated value.
[<AutoOpen>]
module ResultExpression =
    /// `result { let! a = … ; return … }`.
    let result = ResultBuilder()

    /// A lowercase hexadecimal SHA-256 digest: 64 characters, `0-9a-f`.
    /// Lowercase is required rather than normalised, so one digest has one
    /// spelling.
    let (|Hex64|_|) (value: string) : string option =
        if
            not (isNull value)
            && value.Length = 64
            && value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
        then
            Some value
        else
            None
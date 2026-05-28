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
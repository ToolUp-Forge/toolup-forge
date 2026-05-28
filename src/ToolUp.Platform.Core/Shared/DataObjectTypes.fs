// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Data object types ───────────────────────────────────────────
//
// Versioned, immutable data objects with optional append-only
// history. The `DataObject` record is the metadata sidecar that
// `IDataObjectStore` writes alongside the content bytes; the
// implementation guarantees that once a version is written it is never
// modified — only superseded by a new version. Content-addressable
// dedup means identical content across versions of the same object
// shares a single `_content/{hash}.data` blob within the same scope.

/// Versioning behaviour for a data object. Set on the first `Save` and
/// sticky for the object's lifetime — subsequent saves with a
/// different policy return `PolicyMismatch`.
///
/// - `Unversioned`  — simple put/get; each save overwrites v1. Mirrors
///   the legacy `SessionFileStore` behaviour. `ListVersions` returns
///   at most one entry. Suitable for file uploads where history is
///   not required.
/// - `Versioned`    — append-only; each save creates a new version,
///   `Delete` removes all versions. History is preserved until the
///   object is deleted as a whole.
/// - `StrictlyVersioned` — append-only with no delete; `Delete` returns
///   `DeleteForbidden`. Required for analytical results, audit
///   artefacts, and any object that must prove tamper-evidence after
///   creation.
type VersioningPolicy =
    | Unversioned
    | Versioned
    | StrictlyVersioned

/// Metadata for a single version of a stored data object. The store
/// writes one of these per version (as JSON) alongside the content
/// blob; identical content across versions is deduplicated by
/// `ContentHash` within the same scope.
///
/// All fields are caller-supplied at `Save` except `Version`,
/// `CreatedAt`, and `Policy` which the store assigns (`Policy` is
/// recorded on v1 and copied verbatim into every subsequent
/// version's metadata so a single-blob read can answer
/// "is this object immutable?" without reaching back to v1).
/// `Version` numbers are linear per `(ScopeId, ObjectId)` starting
/// at 1; ordering across different `ObjectId`s or scopes is not
/// promised (portability rule 5 — within-shard ordering only).
type DataObject = {
    ObjectId: string
    Version: int
    CreatedAt: DateTime
    CreatedBy: string
    ScopeId: string
    DataType: string
    ContentHash: string
    Policy: VersioningPolicy
    Metadata: Map<string, string>
}

/// Typed errors from `IDataObjectStore`. Lifts the underlying
/// `IBlobStorage` `string` errors into a closed DU so callers
/// pattern-match on cases rather than parsing messages.
type DataObjectError =
    /// The object has no versions in this scope.
    | NotFound
    /// The object exists but the requested version does not.
    | VersionNotFound of int
    /// `Delete` was called on a `StrictlyVersioned` object.
    | DeleteForbidden
    /// `Save` supplied a policy different from the one recorded on v1.
    /// Policy is sticky — set once on first save, fixed thereafter.
    | PolicyMismatch of expected: VersioningPolicy * supplied: VersioningPolicy
    /// Underlying storage failure. Message is the raw error from the
    /// blob store; not stable, only useful for diagnostics.
    | StorageFailure of string
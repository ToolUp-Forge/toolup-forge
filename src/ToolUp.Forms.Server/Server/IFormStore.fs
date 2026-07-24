module ToolUp.Forms.IFormStore

open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission

// ─── Phase 21 — IFormStore interface ────────────────────────────────
//
// Server-side abstraction for form schema and submission persistence.
// Default implementation (`ToolUp.Forms.FormStore`) wraps `IEntityStore`
// (Phase 19) for typed entity persistence + indexed lookup, and emits
// `FormSubmitted` / `FormSubmissionUpdated` audit events via `IAuditLog`
// (Phase 9).
//
// **Scope isolation by construction.** Every method takes `scopeId`;
// implementations derive container paths from it so cross-scope reads
// / writes are structurally impossible. Same discipline as
// `IEntityStore` / `IBlobStorage`.
//
// **Six-rule portability audit (GP-12 / Phase 9c):**
//
//   1. Identity by value — `FormSchemaId` / `SubmissionId` are
//      `string`, `scopeId: string`, no live handles. A distributed
//      companion (Akka.NET / Orleans) returns the same primitives.
//
//   2. Async at every boundary — every method returns `Async<_>`.
//      No fire-and-forget `Tell` shapes; no synchronous reads.
//
//   3. Retry / supervision as data — failure surfaces through the
//      shared `FormError` DU; no callback parameters; no
//      supervision-strategy objects. Retries are caller-side.
//
//   4. Stateless between calls — no method assumes in-memory state
//      survives. Every call derives its result from parameters +
//      `IEntityStore`. A grain that deactivates between two calls
//      behaves identically.
//
//   5. No cross-shard ordering — submission versioning is
//      monotonically increasing within `(SubmissionId, scopeId)`;
//      across submissions or scopes no ordering is promised.
//
//   6. Precision at lower bound — N/A (no time semantics in the
//      interface; timestamps inside `Submission` are
//      `DateTimeOffset`, BCL 100ns precision).
//
// **Phase 21a re-audit (matrix field).** `MatrixField` extends only the
// value SHAPE: matrix cells flatten into `Submission.Values`
// (`Map<string, FieldValue>`) under `{key}[{row},{col}]` string
// sub-keys. No `IFormStore` method signature changes, no new live
// handle, no new ordering promise — the six rules above hold verbatim.
// Rule 1 in particular is preserved: the sub-keys are ordinary strings,
// so a distributed companion persists and returns the richer map with
// the same primitives it already used for the flat map.

/// Typed form store. Schemas and submissions persist as Phase 19
/// entities; query shapes flow through the Phase 19a `EntityQuery`
/// predicate layer.
///
/// **Stateless contract.** No method assumes in-memory state survives
/// between calls. Every operation derives its result from parameters +
/// `IEntityStore`. An Orleans grain or Akka actor implementing this
/// could deactivate / restart between any two calls and behave
/// identically (Phase 9c Rule 4).
type IFormStore =
    /// Save a form schema. On first save, the store assigns version 1
    /// (overwriting whatever the input record's `Version` field said);
    /// subsequent saves bump the version monotonically. Indexes are
    /// updated atomically with the version write.
    abstract SaveSchema: scopeId: string * schema: FormSchema -> Async<Result<FormSchema, FormError>>

    /// Fetch a form schema. `version = None` returns the latest;
    /// `version = Some n` returns that specific historical version
    /// (so submissions made against version 3 of the schema can
    /// re-render even after the schema has evolved to version 7).
    abstract GetSchema:
        scopeId: string * schemaId: FormSchemaId * version: int option -> Async<Result<FormSchema, FormError>>

    /// List the latest version of every form schema in the scope.
    /// Sorted by `Id` ascending for deterministic enumeration.
    abstract ListSchemas: scopeId: string -> Async<FormSchema list>

    /// Delete a form schema. Removes the head version's metadata and
    /// drops every declared index entry; historical versions remain
    /// in the underlying `IDataObjectStore` (per Phase 7's deletion
    /// semantics). Idempotent — deleting a non-existent schema
    /// returns `Ok ()`.
    ///
    /// Existing submissions referencing the schema are preserved
    /// (their `SchemaVersion` resolves through `GetSchema (_, _, Some)`
    /// which sees historical versions).
    abstract DeleteSchema: scopeId: string * schemaId: FormSchemaId -> Async<Result<unit, FormError>>

    /// Save a submission. On first save the store assigns version 1;
    /// subsequent saves (`UpdateDraft`) bump the version. Server-side
    /// callers fill in `SubmittedAt` / `SubmittedBy` / `SchemaVersion`
    /// before invoking — this method does not derive them.
    abstract SaveSubmission: scopeId: string * submission: Submission -> Async<Result<Submission, FormError>>

    /// Fetch the latest version of a submission.
    abstract GetSubmission: scopeId: string * submissionId: SubmissionId -> Async<Result<Submission, FormError>>

    /// Predicate-shaped query. Validated against the entity's
    /// declared indexes (`FormId`, `SubmittedBy`, `State`, compound
    /// `FormId+State`); non-indexed predicate leaves return
    /// `Error InvalidIndex` packed into `FormError.StorageFailed`.
    abstract ListSubmissions:
        scopeId: string * query: EntityQuery<Submission> -> Async<Result<Submission list, FormError>>

    /// Delete a submission. Idempotent. Removes head + indexes;
    /// historical versions remain via `GetVersion` per Phase 7
    /// deletion semantics.
    abstract DeleteSubmission: scopeId: string * submissionId: SubmissionId -> Async<Result<unit, FormError>>
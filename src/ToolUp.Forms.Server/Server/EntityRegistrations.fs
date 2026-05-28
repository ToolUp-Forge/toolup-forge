module ToolUp.Forms.EntityRegistrations

open ToolUp.Platform.EntityTypes
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission

// ─── Phase 21 — Entity registrations ────────────────────────────────
//
// Two entity types persist via `IEntityStore`:
//
//   * `FormSchema` — versioned schema. Single index on `Id` is
//     intrinsic (entities are addressable by `Id` already); we
//     register no extra indexes since `ListSchemas` enumerates all
//     and lookups are by `Id`.
//
//   * `Submission` — versioned submission. Indexes:
//     - `FormId` — "show me all submissions of this form"
//     - `Author` — "show me my submissions" / "show me submissions
//       for this token". Phase 21b — replaces the Phase 21
//       `SubmittedBy` index. Values are prefix-tagged
//       (`"u:{userId}"` or `"t:{tokenId}"`) so one index key serves
//       both authenticated and invited-respondent flavours.
//     - `State` — "show me all open submissions"
//     - Compound `FormId+State` — "show me all open submissions
//       of this form" (the most common admin query)
//
// `FormsServerApp.run` pipes these through `ServerApp.withEntity`
// at compose time so the entity store knows about the types and
// their indexes before any handler runs.

let formSchemaRegistration: EntityRegistration<FormSchema> =
    EntityRegistration.create<FormSchema> FormSchema.entityType

let submissionRegistration: EntityRegistration<Submission> =
    EntityRegistration.create<Submission> Submission.entityType
    |> EntityRegistration.withIndex Submission.indexFormId _.FormId
    |> EntityRegistration.withIndex Submission.indexAuthor (fun s -> SubmissionAuthor.toIndexValue s.Author)
    |> EntityRegistration.withIndex Submission.indexState (fun s -> SubmissionState.toIndexValue s.State)
    |> EntityRegistration.withCompoundIndex Submission.indexFormIdState (fun s -> [
        s.FormId
        SubmissionState.toIndexValue s.State
    ])
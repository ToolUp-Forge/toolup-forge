// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.FormSchema

// ─── Phase 21 — Forms companion shared schema types ─────────────────
//
// Typed, Fable-compatible declaration of a form's structure. Lives in
// Shared so the same schema definition drives server-side validation
// (FormValidator) and client-side rendering (FormRenderer).
//
// Departure from the design spec: the `Custom` validator is keyed by
// a registered name (string), not a closure. Closures don't survive
// Fable serialisation and would force this module to split. The
// server-side `CustomValidatorRegistry` resolves the name to a real
// `string -> Result<unit, string>` predicate at validation time.
// Same reason `Guard` / `Action` in `Workflow.fs` are name-keyed.

/// Stable identifier for a form schema. Type alias for `string` so
/// natural keys (`bug-report`, `intake-v3`) flow through unchanged.
type FormSchemaId = string

/// What kind of value a field accepts. Drives both the renderer's
/// input shape and the validator's type expectations. v1 is flat —
/// nested forms ride `NestedFormField` which renders inline; computed
/// and conditional fields are deferred follow-ups.
type FieldKind =
    | TextField of maxLength: int option
    | NumberField of min: float option * max: float option
    | DateField
    | DateTimeField
    | BoolField
    | ChoiceField of options: string list
    | MultiChoiceField of options: string list
    | FileField of allowedTypes: string list
    | EntityRefField of entityType: string
    | NestedFormField of FormSchemaId

/// A single per-field validation rule. Closed DU — extension via the
/// `Custom of name` escape hatch keyed against the server-side
/// `CustomValidatorRegistry`. `Required` is a separate concern from
/// `FieldSchema.Required` (the boolean) so the validator can emit
/// distinct error codes for "missing" vs "present-but-malformed".
type ValidationRule =
    /// Pattern match. Empty string is always considered valid by Regex
    /// (use `FieldSchema.Required = true` to require presence).
    | Regex of pattern: string * description: string option
    /// Numeric range, applied to `NumberField`. Either bound `None` =
    /// open-ended on that side. No-op for non-numeric kinds.
    | NumberRange of min: float option * max: float option
    /// Length bounds for `TextField` / `ChoiceField` / `MultiChoiceField`.
    | LengthRange of min: int option * max: int option
    /// Custom validator looked up by registered name in the server-side
    /// `CustomValidatorRegistry`. The registered predicate has shape
    /// `string -> Result<unit, string>` and runs server-side only —
    /// clients never see custom validator implementations.
    | Custom of name: string

/// A single field in a form schema. `Key` is the persistence identifier
/// (stable across renames of `DisplayName`); `DisplayName` is what the
/// renderer shows. `Validators` run after `Required` enforcement and
/// after coarse type coercion.
type FieldSchema = {
    Key: string
    DisplayName: string
    Description: string option
    Kind: FieldKind
    Required: bool
    Validators: ValidationRule list
}

/// Phase 21b — schema-level publication flag. `Internal` (default)
/// keeps the form authenticated-only — the existing `IFormApi.Submit`
/// path continues to accept it. `Publishable` opts the schema in to
/// the share-token-gated `IPublicFormApi.SubmitWithToken` path so the
/// form creator can issue tokens against it and distribute the link
/// to anonymous respondents. The schema's identity, validation, and
/// storage layout are unchanged across visibility settings — only
/// which API surfaces accept submissions for it varies.
type FormVisibility =
    | Internal
    | Publishable

/// A form schema. Persisted as a versioned entity via `IFormStore`;
/// `Version` is overwritten by the store on every save (mirrors Phase
/// 19 entity reflection contract). Submissions reference both `Id`
/// and `Version` so the schema-at-submit-time is recoverable for
/// historical re-renders.
type FormSchema = {
    /// Phase 19 reflection contract — entity primary key.
    Id: FormSchemaId
    /// Phase 19 reflection contract — entity type discriminator.
    Type: string
    /// Phase 19 reflection contract — overwritten by the store.
    Version: int
    DisplayName: string
    Description: string option
    Fields: FieldSchema list
    /// Phase 21b — schema-level publication flag. Default `Internal`;
    /// flip to `Publishable` to enable share-link distribution via
    /// `IShareTokenStore` + `IPublicFormApi`.
    Visibility: FormVisibility
}

module FormSchema =
    /// Stable entity-type discriminator for `IEntityStore` registration.
    [<Literal>]
    let entityType = "FormSchema"

    /// Construct a v1 schema with default `Type` / `Version` fields
    /// shaped for the entity store. Defaults `Visibility` to
    /// `Internal` — opt in to `Publishable` separately when the
    /// deployment wants to distribute the form via share-link.
    let create (id: FormSchemaId) (displayName: string) (fields: FieldSchema list) : FormSchema = {
        Id = id
        Type = entityType
        Version = 1
        DisplayName = displayName
        Description = None
        Fields = fields
        Visibility = Internal
    }

    /// Look up a field by its key. Returns `None` if no field with that
    /// key exists in the schema.
    let tryFindField (key: string) (schema: FormSchema) : FieldSchema option =
        schema.Fields |> List.tryFind (fun f -> f.Key = key)
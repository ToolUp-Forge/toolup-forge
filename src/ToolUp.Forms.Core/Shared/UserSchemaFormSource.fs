// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.UserSchemaFormSource

open ToolUp.Platform
open ToolUp.Forms.FormSchema

// ─── Phase 7b — IFormSchemaSource adapter ─────────────────────────────
//
// Validates the Layer-2 thesis "a schema authored once is consumed by
// multiple Layer-2 components": `ToolUp.Forms` accepts a Phase 7b
// `UserAuthoredSchema` (Platform.Core) through this adapter and renders a
// `FormSchema` from it without modification, mapping each `BIFriendlyType`
// to the appropriate Forms `FieldKind`.
//
// `IFormSchemaSource` is the seam — a source of a `FormSchema` for
// rendering, of which `UserAuthoredSchemaSource` is the first
// implementation. A consumer holds the seam and does not know (or care)
// that the schema came from the user-authoring substrate.

/// A source of a Forms `FormSchema` for rendering. The Layer-2 seam a
/// Forms consumer accepts so a schema authored elsewhere renders without
/// the consumer knowing its origin.
type IFormSchemaSource =
    /// Produce the `FormSchema` this source represents.
    abstract ToFormSchema: unit -> FormSchema

/// Map a `BIFriendlyType` to the Forms `FieldKind` whose renderer +
/// validator best express it.
let fieldKindOf (t: BIFriendlyType) : FieldKind =
    match t with
    | BIFriendlyType.String -> TextField None
    | BIFriendlyType.Number -> NumberField(None, None)
    | BIFriendlyType.Boolean -> BoolField
    | BIFriendlyType.Date -> DateField
    | BIFriendlyType.DateTime -> DateTimeField
    // A monetary amount is a number; currency code is carried on the
    // authored schema, not the Forms field kind.
    | BIFriendlyType.Currency _ -> NumberField(None, None)
    // Percentages render as a bounded number (0–100).
    | BIFriendlyType.Percentage _ -> NumberField(Some 0.0, Some 100.0)
    // ISO country code — a short text field.
    | BIFriendlyType.CountryCode -> TextField(Some 3)
    | BIFriendlyType.Enum values -> ChoiceField values
    // A reference to another type maps to the Forms entity-ref picker.
    | BIFriendlyType.Ref typeId -> EntityRefField typeId
    | BIFriendlyType.Id -> TextField None
    | BIFriendlyType.Email -> TextField None
    | BIFriendlyType.Url -> TextField None

/// Per-`BIFriendlyType` validators layered on top of the coarse kind:
/// `Email` / `Url` gain a format `Regex`; every other type has none by
/// default.
let validatorsOf (t: BIFriendlyType) : ValidationRule list =
    match t with
    | BIFriendlyType.Email -> [ Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", Some "must be a valid email") ]
    | BIFriendlyType.Url -> [ Regex(@"^https?://", Some "must be an absolute URL") ]
    | _ -> []

/// Map one `UserSchemaField` to a Forms `FieldSchema`. `Name` is the
/// stable persistence key; the semantic type drives the kind + validators.
let toFieldSchema (field: UserSchemaField) : FieldSchema = {
    Key = field.Name
    DisplayName = field.Name
    Description = field.Description
    Kind = fieldKindOf field.Type
    Required = field.Required
    Validators = validatorsOf field.Type
}

/// Render a `UserAuthoredSchema` as a Forms `FormSchema` without
/// modification — the Layer-2 adapter proper.
let toFormSchema (schema: UserAuthoredSchema) : FormSchema =
    let baseForm =
        FormSchema.create schema.SchemaId schema.DisplayName (schema.Fields |> List.map toFieldSchema)

    {
        baseForm with
            Description =
                if System.String.IsNullOrWhiteSpace schema.Description then
                    None
                else
                    Some schema.Description
    }

/// `IFormSchemaSource` over a `UserAuthoredSchema` — the concrete adapter
/// a Forms consumer accepts.
type UserAuthoredSchemaSource(schema: UserAuthoredSchema) =
    interface IFormSchemaSource with
        member _.ToFormSchema() = toFormSchema schema
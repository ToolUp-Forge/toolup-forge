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

/// Phase 21a — optional presentation labels for a `MatrixField`'s rows
/// and columns. Carried inline on the `MatrixField` case (see the note
/// on that case for why it lives here rather than on `FieldSchema`).
/// Either list may be shorter than the matrix dimension — the renderer
/// falls back to numeric `R{n}` / `C{n}` labels for any index a list
/// doesn't cover, so a partially-labelled matrix is well-defined.
type MatrixFieldLabels = {
    RowLabels: string list
    ColLabels: string list
}

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
    /// Phase 21a — a fixed-shape 2D grid of `cell`-typed inputs
    /// (`rows` × `cols`, both ≥ 1). Every cell shares the outer
    /// field's `Required` flag and `Validators`, applied per-cell.
    ///
    /// Cell values do NOT get their own `FieldValue` case: a matrix
    /// flattens into the existing `Map<string, FieldValue>` under
    /// `{key}[{row},{col}]` sub-keys (`Matrix.cellKey`), so entity-store
    /// persistence and submission replay are unchanged — the matrix is
    /// value SHAPE, not a new interface shape.
    ///
    /// **Labels live inline on the case, not on `FieldSchema`.** The
    /// phase spec described them as "FieldSchema metadata"; adding a
    /// field to the `FieldSchema` record is a breaking change to every
    /// construction site (and every external SDK consumer) for a purely
    /// additive feature. Carrying the optional labels on the new DU case
    /// is strictly additive to the public surface — no existing token
    /// moves — which honours the SDK's SemVer-on-0.x discipline (GP 11)
    /// while keeping the whole change inside the Forms package. Build a
    /// `MatrixField` via `Matrix.create`, which enforces the ≥ 1 bounds.
    | MatrixField of rows: int * cols: int * cell: FieldKind * labels: MatrixFieldLabels option

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

/// Phase 21a — helpers for the `MatrixField` case. The `cellKey` /
/// coordinate / label functions are the single source of truth shared
/// by the server validator and the client renderer, so both sides
/// agree byte-for-byte on the flattened sub-key layout (critical for
/// round-trip fidelity).
module Matrix =
    /// Persistence sub-key for the cell at (0-based) `row` / `col`
    /// inside the matrix field `key`. The canonical `{key}[{row},{col}]`
    /// shape the submission map is flattened into. This is the ONE place
    /// the format is defined — server and client both call it.
    let cellKey (key: string) (row: int) (col: int) : string = sprintf "%s[%d,%d]" key row col

    /// Enumerate every (row, col) coordinate of a `rows` × `cols`
    /// matrix in row-major order. 0-based, matching `cellKey`.
    let coords (rows: int) (cols: int) : (int * int) list = [
        for r in 0 .. rows - 1 do
            for c in 0 .. cols - 1 -> (r, c)
    ]

    /// Display label for a 0-based row index. Uses the supplied label
    /// when present and in range, else the numeric `R{n}` fallback
    /// (1-based for humans, so row 0 renders `R1`).
    let rowLabel (labels: MatrixFieldLabels option) (row: int) : string =
        labels
        |> Option.bind (fun l -> List.tryItem row l.RowLabels)
        |> Option.filter (System.String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue (sprintf "R%d" (row + 1))

    /// Display label for a 0-based column index. `C{n}` fallback.
    let colLabel (labels: MatrixFieldLabels option) (col: int) : string =
        labels
        |> Option.bind (fun l -> List.tryItem col l.ColLabels)
        |> Option.filter (System.String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue (sprintf "C%d" (col + 1))

    /// Smart constructor for a `MatrixField`. Enforces the ≥ 1 bounds
    /// on both dimensions and rejects a nested matrix cell (matrices of
    /// matrices are out of scope — the flattened sub-key layout has no
    /// representation for them). Raises `ArgumentException` on a
    /// violation so a malformed schema fails loudly at construction
    /// rather than silently mis-rendering.
    let create (rows: int) (cols: int) (cell: FieldKind) (labels: MatrixFieldLabels option) : FieldKind =
        if rows < 1 then
            invalidArg "rows" "matrix rows must be >= 1"
        elif cols < 1 then
            invalidArg "cols" "matrix cols must be >= 1"
        else
            match cell with
            | MatrixField _ ->
                invalidArg "cell" "a matrix cell may not itself be a MatrixField (nested matrices are out of scope)"
            | _ -> MatrixField(rows, cols, cell, labels)
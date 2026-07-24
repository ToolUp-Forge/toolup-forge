module ToolUp.Forms.FormValidator

open System
open System.Text.RegularExpressions
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission

// ─── Phase 21 — Server-side form validation ─────────────────────────
//
// Pure (modulo regex compilation) validation of a `FormSchema` against
// a submitted `Map<string, FieldValue>`. Runs three passes per field:
//
//   1. Required check — `Required = true` + missing value → "required"
//   2. Coarse type check — present value must match the `FieldKind`
//      shape (TextField → TextValue, NumberField → NumberValue, etc.)
//   3. Per-rule validation — Regex / NumberRange / LengthRange /
//      Custom-by-name. All rules accumulate; no short-circuit.
//
// `Custom of name` rules are looked up in the supplied registry.
// Missing-by-name custom validators are logged-and-skipped at the
// caller's discretion — the validator itself treats unknown custom
// names as a soft pass (returns no error) so renaming a validator
// doesn't break in-flight forms.
//
// Returns `Ok ()` when every field passes; `Error list` accumulating
// every failure across every field (UI shows them all at once).

/// One registered server-side custom validator. The string is the
/// raw string-form of the field's submitted value (`textValue` for
/// `TextValue`, `string n` for `NumberValue`, `b.ToString()` for
/// `BoolValue`, etc.). Predicates returning `Ok ()` pass; `Error`
/// short-circuits the field with the supplied message.
type CustomValidator = string -> Result<unit, string>

/// Registry of name → predicate. Populated at compose time via
/// `FormsServerApp.withCustomValidator`; immutable for process
/// lifetime.
type CustomValidatorRegistry = Map<string, CustomValidator>

let emptyRegistry: CustomValidatorRegistry = Map.empty

let private mkError (key: string) (code: string) (msg: string) : FieldError = {
    FieldKey = key
    Code = code
    Message = msg
}

// Render a FieldValue as a string for Custom validator dispatch.
let private fieldValueToString (v: FieldValue) : string =
    match v with
    | TextValue s -> s
    | NumberValue n -> string n
    | DateValue d -> d.ToString("yyyy-MM-dd")
    | DateTimeValue dt -> dt.ToString("o")
    | BoolValue b -> if b then "true" else "false"
    | ChoiceValue c -> c
    | MultiChoiceValue cs -> String.concat "," cs
    | FileValue id -> id
    | EntityRefValue id -> id
    | NestedSubmissionValue id -> id

// Compare a FieldKind shape with the supplied FieldValue. Returns
// Some msg if the shapes don't agree.
let private typeMismatch (kind: FieldKind) (value: FieldValue) : string option =
    match kind, value with
    | TextField _, TextValue _ -> None
    | NumberField _, NumberValue _ -> None
    | DateField, DateValue _ -> None
    | DateTimeField, DateTimeValue _ -> None
    | BoolField, BoolValue _ -> None
    | ChoiceField _, ChoiceValue _ -> None
    | MultiChoiceField _, MultiChoiceValue _ -> None
    | FileField _, FileValue _ -> None
    | EntityRefField _, EntityRefValue _ -> None
    | NestedFormField _, NestedSubmissionValue _ -> None
    | _ -> Some(sprintf "expected %A, got %A" kind value)

// Validate a single rule against a single value.
let private validateRule
    (registry: CustomValidatorRegistry)
    (field: FieldSchema)
    (value: FieldValue)
    (rule: ValidationRule)
    : FieldError list =
    match rule, value with
    | Regex(pattern, _), TextValue s ->
        if String.IsNullOrEmpty s then
            []
        else
            try
                if Regex.IsMatch(s, pattern) then
                    []
                else
                    [
                        mkError field.Key "regex" (sprintf "value does not match pattern '%s'" pattern)
                    ]
            with ex -> [ mkError field.Key "regex" (sprintf "invalid regex pattern: %s" ex.Message) ]
    | Regex _, _ -> [] // Regex on non-text values: silently skip.
    | NumberRange(minOpt, maxOpt), NumberValue n ->
        let underMin = minOpt |> Option.map (fun mn -> n < mn) |> Option.defaultValue false
        let overMax = maxOpt |> Option.map (fun mx -> n > mx) |> Option.defaultValue false

        if underMin then
            [
                mkError field.Key "range" (sprintf "value %g is below minimum %g" n minOpt.Value)
            ]
        elif overMax then
            [
                mkError field.Key "range" (sprintf "value %g is above maximum %g" n maxOpt.Value)
            ]
        else
            []
    | NumberRange _, _ -> []
    | LengthRange(minOpt, maxOpt), TextValue s ->
        let len = s.Length

        let underMin =
            minOpt |> Option.map (fun mn -> len < mn) |> Option.defaultValue false

        let overMax = maxOpt |> Option.map (fun mx -> len > mx) |> Option.defaultValue false

        if underMin then
            [
                mkError field.Key "length" (sprintf "value length %d is below minimum %d" len minOpt.Value)
            ]
        elif overMax then
            [
                mkError field.Key "length" (sprintf "value length %d is above maximum %d" len maxOpt.Value)
            ]
        else
            []
    | LengthRange(minOpt, maxOpt), MultiChoiceValue cs ->
        let len = cs.Length

        let underMin =
            minOpt |> Option.map (fun mn -> len < mn) |> Option.defaultValue false

        let overMax = maxOpt |> Option.map (fun mx -> len > mx) |> Option.defaultValue false

        if underMin then
            [
                mkError field.Key "length" (sprintf "selection count %d is below minimum %d" len minOpt.Value)
            ]
        elif overMax then
            [
                mkError field.Key "length" (sprintf "selection count %d is above maximum %d" len maxOpt.Value)
            ]
        else
            []
    | LengthRange _, _ -> []
    | ValidationRule.Custom name, _ ->
        match Map.tryFind name registry with
        | None -> [] // Soft-pass on missing-by-name; logging is the registry's job.
        | Some predicate ->
            match predicate (fieldValueToString value) with
            | Ok() -> []
            | Error msg -> [ mkError field.Key "custom" msg ]

// Choice / MultiChoice integrity check — values must be drawn from
// the declared options. ChoiceField with a value not in `options`
// fails with `choice-not-allowed`. Independent of the per-rule
// validators because it's inherent to the FieldKind, not optional.
let private choiceCheck (field: FieldSchema) (value: FieldValue) : FieldError list =
    match field.Kind, value with
    | ChoiceField options, ChoiceValue c ->
        if List.contains c options then
            []
        else
            [
                mkError field.Key "choice-not-allowed" (sprintf "'%s' is not one of the allowed options" c)
            ]
    | MultiChoiceField options, MultiChoiceValue cs ->
        let invalid = cs |> List.filter (fun c -> not (List.contains c options))

        if List.isEmpty invalid then
            []
        else
            [
                mkError field.Key "choice-not-allowed" (sprintf "options not allowed: %s" (String.concat ", " invalid))
            ]
    | _ -> []

// Validate a single already-present value against a field's kind,
// choice-integrity, and per-rule validators. Shared by flat fields and
// per-cell matrix validation.
let private validatePresent
    (registry: CustomValidatorRegistry)
    (field: FieldSchema)
    (value: FieldValue)
    : FieldError list =
    match typeMismatch field.Kind value with
    | Some msg -> [ mkError field.Key "wrong-type" msg ]
    | None ->
        let choiceErrors = choiceCheck field value

        let ruleErrors =
            field.Validators |> List.collect (validateRule registry field value)

        choiceErrors @ ruleErrors

// Validate a single flat "slot" — one entry looked up by `field.Key`
// in the values map. Absent + Required → "required"; absent + optional
// → clean; present → `validatePresent`.
let private validateSlot
    (registry: CustomValidatorRegistry)
    (values: Map<string, FieldValue>)
    (field: FieldSchema)
    : FieldError list =
    match Map.tryFind field.Key values with
    | None ->
        if field.Required then
            [ mkError field.Key "required" "field is required" ]
        else
            []
    | Some value -> validatePresent registry field value

let private validateField
    (registry: CustomValidatorRegistry)
    (values: Map<string, FieldValue>)
    (field: FieldSchema)
    : FieldError list =
    match field.Kind with
    // Phase 21a — a matrix flattens into `{key}[{row},{col}]` sub-keys.
    // Validate each cell as its own slot: a synthetic `FieldSchema`
    // carrying the cell key, the cell `FieldKind`, and the outer field's
    // `Required` + `Validators`. The emitted `FieldError.FieldKey` is the
    // cell sub-key, so the renderer highlights the exact offending cell —
    // the same error-emission shape as a flat field.
    | MatrixField(rows, cols, cell, _) ->
        Matrix.coords rows cols
        |> List.collect (fun (r, c) ->
            let cellField = {
                field with
                    Key = Matrix.cellKey field.Key r c
                    Kind = cell
            }

            validateSlot registry values cellField)
    | _ -> validateSlot registry values field

/// Validate every field in the schema against the supplied values
/// map. Returns `Ok ()` only when every field passes; otherwise an
/// `Error` carrying every accumulated failure.
let validate
    (registry: CustomValidatorRegistry)
    (schema: FormSchema)
    (values: Map<string, FieldValue>)
    : Result<unit, FieldError list> =
    let errors = schema.Fields |> List.collect (validateField registry values)

    if List.isEmpty errors then Ok() else Error errors
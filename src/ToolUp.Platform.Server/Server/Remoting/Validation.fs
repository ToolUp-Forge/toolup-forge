namespace ToolUp.Remoting.Server

open System
open System.Reflection
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69e — typed validation on input records
// =============================================================================
//
// Validation attributes decorate FIELDS of input record types. The dispatcher
// reflects over each API method's input type at startup, walks for validation
// attributes, and at request time deserialises the input + evaluates each
// attribute. Violations short-circuit with a categorised
// `ErrorCategory.Validation` envelope carrying per-field details; the
// handler never runs.
//
// Default: on whenever attributes exist. No opt-in fluent helper needed —
// methods whose input records have no validation attributes pay zero
// per-call cost (the engine's classifier returns an empty map, the
// per-call lookup misses fast).

/// Base class for validation attributes. Subclassed by the concrete
/// validators below; the engine treats any attribute deriving from this
/// as a validator and dispatches via a `Validate` virtual method.
[<AbstractClass>]
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type ValidationAttribute() =
    inherit Attribute()
    /// Returns `None` on pass, `Some message` on fail. The dispatcher
    /// wraps the message into a `FieldViolation` carrying the field
    /// path it was evaluated on.
    abstract Validate: value: obj -> string option

/// Phase 69e — value's string length must be at least `n` characters.
/// Null strings always fail. For non-string values the attribute passes
/// (use `[<NotNull>]` or strong typing instead).
type MinLengthAttribute(n: int) =
    inherit ValidationAttribute()
    member _.MinLength = n

    override _.Validate value =
        match value with
        | :? string as s when isNull s -> Some(sprintf "expected non-null string of length >= %d" n)
        | :? string as s when s.Length < n -> Some(sprintf "string length %d below minimum %d" s.Length n)
        | _ -> None

/// Phase 69e — value's string length must be at most `n` characters.
type MaxLengthAttribute(n: int) =
    inherit ValidationAttribute()
    member _.MaxLength = n

    override _.Validate value =
        match value with
        | :? string as s when not (isNull s) && s.Length > n ->
            Some(sprintf "string length %d above maximum %d" s.Length n)
        | _ -> None

/// Phase 69e — string must be non-empty (length > 0 after trim).
type NotEmptyAttribute() =
    inherit ValidationAttribute()

    override _.Validate value =
        match value with
        | :? string as s when isNull s || String.IsNullOrWhiteSpace s -> Some "value is required (non-empty after trim)"
        | _ -> None

/// Phase 69e — string must match the given regex anchored or not, as
/// supplied by the consumer. Pattern is compiled once at attribute
/// construction (cached on the attribute instance).
type RegexAttribute(pattern: string) =
    inherit ValidationAttribute()
    let compiled = Regex(pattern, RegexOptions.Compiled)
    member _.Pattern = pattern

    override _.Validate value =
        match value with
        | :? string as s when not (isNull s) ->
            if compiled.IsMatch s then
                None
            else
                Some(sprintf "value did not match pattern '%s'" pattern)
        | _ -> None

/// Phase 69e — convenience attribute for an RFC-5322-shaped email.
/// Uses a permissive but useful pattern; consumers needing stricter
/// validation use `[<Regex>]` with their own pattern.
type EmailAttribute() =
    inherit ValidationAttribute()
    static let emailPattern = Regex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled)

    override _.Validate value =
        match value with
        | :? string as s when not (isNull s) ->
            if emailPattern.IsMatch s then
                None
            else
                Some "value is not a syntactically valid email"
        | _ -> None

/// Phase 69e — numeric value must be in the inclusive range [min, max].
/// Accepts `int`, `int64`, `float`, `decimal` via boxed comparisons.
type RangeAttribute(min: float, max: float) =
    inherit ValidationAttribute()
    member _.Min = min
    member _.Max = max

    override _.Validate value =
        let asFloat =
            match value with
            | :? int as i -> Some(float i)
            | :? int64 as i -> Some(float i)
            | :? float as f -> Some f
            | :? decimal as d -> Some(float d)
            | _ -> None

        match asFloat with
        | None -> None
        | Some v when v < min || v > max -> Some(sprintf "value %g outside allowed range [%g, %g]" v min max)
        | Some _ -> None

// -----------------------------------------------------------------------------

/// Phase 69e — per-field violation surfaced in the categorised envelope.
/// `Path` is the dotted field path (e.g. `Address.Postcode`) for nested
/// records. v0 ships the top-level field name; nested traversal lands
/// in a follow-up.
type FieldViolation = {
    Path: string
    Code: string
    Message: string
}

// -----------------------------------------------------------------------------

module internal Validation =

    /// Extract the input type from an API record field. Fable.Remoting
    /// method signatures are F# function types (`'input -> Async<'output>`);
    /// for multi-argument methods the signature is curried, so we walk
    /// until we hit a non-function (the eventual `Async<'T>`).
    /// Returns the FIRST input type (v0 — multi-arg validation is a
    /// follow-up).
    let firstInputType (apiField: PropertyInfo) : Type option =
        let fieldType = apiField.PropertyType

        if FSharpType.IsFunction fieldType then
            let inputT, _ = FSharpType.GetFunctionElements fieldType
            Some inputT
        else
            None

    /// Walk fields of a record type once at startup and report whether
    /// any field carries a `ValidationAttribute`. Used to fast-skip the
    /// per-request engine when no method requires validation.
    let private recordHasValidations (recordType: Type) : bool =
        if not (FSharpType.IsRecord recordType) then
            false
        else
            FSharpType.GetRecordFields recordType
            |> Array.exists (fun pi -> pi.GetCustomAttributes(true) |> Array.exists (fun a -> a :? ValidationAttribute))

    /// Cache per-method: input type ONLY when the method's input is a
    /// record carrying at least one validation attribute. Methods with
    /// non-record inputs (or records without validators) are absent
    /// from the map, so per-request lookup is a fast Map.tryFind miss.
    let classify (apiType: Type) : Map<string, Type> =
        if not (FSharpType.IsRecord apiType) then
            Map.empty
        else
            FSharpType.GetRecordFields apiType
            |> Array.choose (fun apiField ->
                match firstInputType apiField with
                | Some inputT when recordHasValidations inputT -> Some(apiField.Name, inputT)
                | _ -> None)
            |> Map.ofArray

    /// Evaluate every validation attribute on the record's fields
    /// against the deserialised input value. Returns the list of
    /// violations; empty list means pass.
    let evaluate (inputType: Type) (inputValue: obj) : FieldViolation list =
        if isNull inputValue then
            []
        else
            FSharpType.GetRecordFields inputType
            |> Array.collect (fun pi ->
                let attrs =
                    pi.GetCustomAttributes(true)
                    |> Array.choose (fun a ->
                        match a with
                        | :? ValidationAttribute as v -> Some v
                        | _ -> None)

                let value = pi.GetValue inputValue

                attrs
                |> Array.choose (fun attr ->
                    match attr.Validate value with
                    | Some message ->
                        let code = attr.GetType().Name.Replace("Attribute", "")

                        Some {
                            Path = pi.Name
                            Code = code
                            Message = message
                        }
                    | None -> None))
            |> Array.toList

    /// Parse the request body (a JSON `[arg1, arg2, ...]` array, post
    /// body-normalisation) and pull the first argument as the
    /// `inputType`. Returns `None` when the body isn't a parseable
    /// array with at least one element (defensive — the proxy handles
    /// these cases too).
    let parseFirstArgFromBody
        (bodyText: string)
        (inputType: Type)
        (options: System.Text.Json.JsonSerializerOptions)
        : obj option =
        try
            use doc = System.Text.Json.JsonDocument.Parse bodyText

            if doc.RootElement.ValueKind <> System.Text.Json.JsonValueKind.Array then
                None
            elif doc.RootElement.GetArrayLength() = 0 then
                None
            else
                let firstArg = doc.RootElement[0]
                let rawJson = firstArg.GetRawText()
                Some(System.Text.Json.JsonSerializer.Deserialize(rawJson, inputType, options))
        with _ ->
            // Defensive: deserialisation failures aren't validation
            // failures — they're the proxy's responsibility to handle
            // (with an Exception InvocationResult). Don't pre-empt it.
            None
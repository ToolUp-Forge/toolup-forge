namespace ToolUp.Remoting.Server

open System
open System.Collections
open System.Collections.Generic
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

/// Phase 69e — shared numeric coercion for the range / min / max
/// attributes. Boxes `int` / `int64` / `float` / `decimal` to `float`;
/// anything else is `None` (the attribute passes — strong typing or a
/// dedicated attribute covers non-numeric shapes).
module internal Numeric =
    let asFloat (value: obj) : float option =
        match value with
        | :? int as i -> Some(float i)
        | :? int64 as i -> Some(float i)
        | :? float as f -> Some f
        | :? decimal as d -> Some(float d)
        | _ -> None

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

/// Phase 69e — numeric value must be >= `n`. Accepts `int` / `int64` /
/// `float` / `decimal` via boxed comparison; non-numeric values pass.
type MinValueAttribute(n: float) =
    inherit ValidationAttribute()
    member _.MinValue = n

    override _.Validate value =
        match Numeric.asFloat value with
        | Some v when v < n -> Some(sprintf "value %g below minimum %g" v n)
        | _ -> None

/// Phase 69e — numeric value must be <= `n`.
type MaxValueAttribute(n: float) =
    inherit ValidationAttribute()
    member _.MaxValue = n

    override _.Validate value =
        match Numeric.asFloat value with
        | Some v when v > n -> Some(sprintf "value %g above maximum %g" v n)
        | _ -> None

/// Phase 69e — string value must parse as an absolute URI.
type UriAttribute() =
    inherit ValidationAttribute()

    override _.Validate value =
        match value with
        | :? string as s when not (isNull s) ->
            match Uri.TryCreate(s, UriKind.Absolute) with
            | true, _ -> None
            | false, _ -> Some "value is not a valid absolute URI"
        | _ -> None

// -----------------------------------------------------------------------------

/// Phase 69e — per-request validation context handed to an
/// `IFieldValidator`. Carries the Phase 69b request context (subject id
/// + correlation id) so a custom validator can make a context-dependent
/// decision (e.g. "unique within this tenant"). Async resolution is a
/// future extension; v0 validators are synchronous.
type IValidationContext =
    abstract SubjectId: string
    abstract CorrelationId: string option

/// Phase 69e — custom field-validator escape hatch. The single method
/// returns `None` on pass, `Some message` on fail — same convention as
/// the built-in attributes, but with the per-request context available.
/// Implementations must have a parameterless constructor (the engine
/// instantiates by type) and be stateless between calls.
type IFieldValidator =
    abstract Validate: value: obj * context: IValidationContext -> string option

/// Phase 69e — wires a consumer-supplied `IFieldValidator` onto a field.
/// `[<Custom(typeof<MyValidator>)>]` where `MyValidator :> IFieldValidator`.
/// The base `Validate` is a no-op (the engine dispatches the custom
/// validator with the request context, which the attribute can't see).
type CustomAttribute(validatorType: Type) =
    inherit ValidationAttribute()
    member _.ValidatorType = validatorType
    override _.Validate(_value: obj) = None

// -----------------------------------------------------------------------------

/// Phase 69e — per-field violation surfaced in the categorised envelope.
/// `Path` is the dotted/indexed field path: `Address.Postcode` for a
/// nested record field, `Lines[2].Sku` for a list element. `Code` is the
/// attribute name minus the `Attribute` suffix (`MinLength`, `Custom`, …).
type FieldViolation = {
    Path: string
    Code: string
    Message: string
}

// -----------------------------------------------------------------------------

/// Phase 69e — empty/anonymous validation context. The dispatcher
/// supplies a real one (subject id + correlation id from the Phase 69b
/// request context); tests and non-dispatcher callers use this.
[<RequireQualifiedAccess>]
module ValidationContext =
    let none =
        { new IValidationContext with
            member _.SubjectId = "anonymous"
            member _.CorrelationId = None
        }

// -----------------------------------------------------------------------------

/// Phase 69e.C — message-catalogue helpers. The built-in English message
/// already lives on each attribute; this module turns a `ViolationCode ->
/// template` map into an `IValidationMessages` so a deployment can supply
/// localised templates (or override the wording) without writing an
/// interface implementation by hand.
[<RequireQualifiedAccess>]
module ValidationMessages =

    /// Substitute `{token}` placeholders in `template` from `args`, plus
    /// the special `{path}` token. Unknown tokens are left verbatim so a
    /// typo in a template is visible rather than silently blanked.
    let internal applyTemplate (path: string) (args: Map<string, string>) (template: string) : string =
        let withPath = template.Replace("{path}", path)

        args
        |> Map.fold (fun (acc: string) k v -> acc.Replace("{" + k + "}", v)) withPath

    /// Build an `IValidationMessages` from a `ViolationCode -> template`
    /// map. A template may reference the violation's structured args by
    /// `{name}` (`{min}`, `{max}`, `{actual}`, `{pattern}`) and the field
    /// `{path}`. A code absent from the map falls through to the built-in
    /// English message (the resolver returns `None`).
    let fromTemplates (templates: Map<string, string>) : IValidationMessages =
        { new IValidationMessages with
            member _.Resolve request =
                templates
                |> Map.tryFind request.Code
                |> Option.map (applyTemplate request.Path request.Args)
        }

    /// The default English template set, keyed by violation code. Supplied
    /// as the documented starting point a localiser copies + translates;
    /// passing it to `fromTemplates` reproduces (close to) the built-in
    /// wording, so a deployment can diff its overrides against a known
    /// baseline.
    let englishTemplates: Map<string, string> =
        Map [
            "MinLength", "string length {actual} below minimum {min}"
            "MaxLength", "string length {actual} above maximum {max}"
            "NotEmpty", "value is required (non-empty after trim)"
            "Regex", "value did not match pattern '{pattern}'"
            "Email", "value is not a syntactically valid email"
            "Uri", "value is not a valid absolute URI"
            "Range", "value {actual} outside allowed range [{min}, {max}]"
            "MinValue", "value {actual} below minimum {min}"
            "MaxValue", "value {actual} above maximum {max}"
        ]

module internal Validation =

    // Reflect over public AND non-public records — input DTOs are usually
    // public, but a consumer may declare an internal input record; the
    // engine must see its fields either way (Phase 69d.tail parity).
    let private reflectionFlags = BindingFlags.Public ||| BindingFlags.NonPublic

    // Phase 69e — family-agnostic attribute recognition. forge API records
    // sit in Platform.Core (Fable-compiled) and carry the tier-shared
    // `ToolUp.Platform.*` validation mirrors, which are pure-metadata
    // attributes that DON'T inherit the server-tier `ValidationAttribute`
    // (they can't — the Core tier has no server dependency). Without this
    // bridge those mirrors are invisible to the engine — validation on a
    // Fable API record silently never fires (the same defect class the
    // 69d.tail auth classifier closed). `tryNormalise` maps either family
    // to the server-tier attribute whose `Validate` carries the logic,
    // reading the mirror's properties reflectively by simple type name.
    //
    // ── Phase 727 severity assessment — the validation family ─────────
    //
    // VERDICT: KEEP simple-name matching. This is the one family of the
    // four that Phase 335's CLR-identity mechanism should NOT be extended
    // to, and the reasoning is recorded here so the next sweep does not
    // re-derive it and reach the opposite conclusion from consistency
    // alone. Three arguments, in order of weight.
    //
    // 1. A forgery cannot relax anything. The family has no "skip
    //    validation" marker — every attribute in it ADDS a constraint,
    //    and `validate` accumulates violations rather than subtracting
    //    them. So an unsanctioned attribute that is honoured produces
    //    extra 400s on a well-formed request: an availability / UX defect,
    //    visible, self-limiting, and diagnosable from the categorised
    //    envelope, which names the violating field and code. It cannot
    //    grant access, expose a value, or suppress a sibling validator.
    //    That is the opposite failure direction from `[<PiiSafe>]` (which
    //    STOPS redaction) and from the auth markers 335 fixed (which
    //    OPENED a method) — and failure direction, not name-forgeability,
    //    is what set those two apart.
    //
    // 2. Tightening here would be a silent fail-OPEN, and — unlike rate
    //    limiting and idempotency — the refusal that buys the safety back
    //    is not available. A consumer whose own mirror attribute is
    //    picked up today would, under identity matching, silently stop
    //    validating: input reaches the handler unchecked with nothing
    //    saying so. The other three families answer that with a startup
    //    collision refusal; this one cannot, because (3) its marker names
    //    collide with a BCL family that appears on the same records for
    //    entirely legitimate unrelated reasons. Identity matching without
    //    a refusal is strictly worse than the status quo.
    //
    // 3. The collision surface is the widest of the four and its
    //    collisions are mostly BENIGN BY INTENT.
    //    `System.ComponentModel.DataAnnotations` ships `MinLength` /
    //    `MaxLength` / `Range` / `Regex`-shaped attributes whose simple
    //    names collide exactly, and those sit on consumer DTOs for EF
    //    Core column mapping and MVC model binding — nothing to do with
    //    this engine. Refusing to start on one would break a correct
    //    deployment on an SDK upgrade: a GP 11 violation with no defect
    //    behind it.
    //
    // What keeps (3) safe today is the TYPED reflective property read
    // below, not the name: `DataAnnotations.MinLengthAttribute` exposes
    // `Length`, not `MinLength`, so `intProp a "MinLength"` misses and the
    // attribute is ignored. That is currently true by luck rather than by
    // design, so it is pinned by a test (`ValidationAttributeRecognition`
    // in `ToolUp.Platform.Tests`) — a future arm added here that reads a
    // property name the BCL family also exposes would start honouring
    // BCL attributes, and the pin is what would say so.
    //
    // The server-tier arm needs no change either way: `:? ValidationAttribute`
    // is already identity-based, because satisfying it means DERIVING from
    // a type in this assembly — which requires referencing it, and that
    // reference IS the sanction (`CustomAttribute` + `IFieldValidator` are
    // the documented consumer extension point).
    let private intProp (a: obj) (name: string) : int option =
        match a.GetType().GetProperty name with
        | null -> None
        | p ->
            match p.GetValue a with
            | :? int as i -> Some i
            | _ -> None

    let private floatProp (a: obj) (name: string) : float option =
        match a.GetType().GetProperty name with
        | null -> None
        | p ->
            match p.GetValue a with
            | :? float as f -> Some f
            | :? int as i -> Some(float i)
            | _ -> None

    let private strProp (a: obj) (name: string) : string option =
        match a.GetType().GetProperty name with
        | null -> None
        | p ->
            match p.GetValue a with
            | :? string as s when not (isNull s) -> Some s
            | _ -> None

    let private tryNormalise (a: obj) : ValidationAttribute option =
        match a with
        // Server-tier family (incl. CustomAttribute, itself a
        // ValidationAttribute) passes straight through.
        | :? ValidationAttribute as v -> Some v
        | _ ->
            match a.GetType().Name with
            | "MinLengthAttribute" ->
                intProp a "MinLength"
                |> Option.map (fun n -> MinLengthAttribute n :> ValidationAttribute)
            | "MaxLengthAttribute" ->
                intProp a "MaxLength"
                |> Option.map (fun n -> MaxLengthAttribute n :> ValidationAttribute)
            | "NotEmptyAttribute" -> Some(NotEmptyAttribute() :> ValidationAttribute)
            | "RegexAttribute" ->
                strProp a "Pattern"
                |> Option.map (fun p -> RegexAttribute p :> ValidationAttribute)
            | "EmailAttribute" -> Some(EmailAttribute() :> ValidationAttribute)
            | "UriAttribute" -> Some(UriAttribute() :> ValidationAttribute)
            | "RangeAttribute" ->
                match floatProp a "Min", floatProp a "Max" with
                | Some lo, Some hi -> Some(RangeAttribute(lo, hi) :> ValidationAttribute)
                | _ -> None
            | "MinValueAttribute" ->
                floatProp a "MinValue"
                |> Option.map (fun n -> MinValueAttribute n :> ValidationAttribute)
            | "MaxValueAttribute" ->
                floatProp a "MaxValue"
                |> Option.map (fun n -> MaxValueAttribute n :> ValidationAttribute)
            | _ -> None

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

    /// The F# record type reachable directly from a field's type, if the
    /// field is a record, an array / list / seq of records, or an option
    /// of a record. Used both to decide whether to recurse at request
    /// time and whether a method needs validation at startup.
    let private nestedRecordType (t: Type) : Type option =
        if FSharpType.IsRecord(t, reflectionFlags) then
            Some t
        elif t.IsArray then
            let e = t.GetElementType()

            if FSharpType.IsRecord(e, reflectionFlags) then
                Some e
            else
                None
        elif t.IsGenericType then
            let gtd = t.GetGenericTypeDefinition()

            if gtd = typedefof<_ list> || gtd = typedefof<_ option> || gtd = typedefof<seq<_>> then
                let e = t.GetGenericArguments()[0]

                if FSharpType.IsRecord(e, reflectionFlags) then
                    Some e
                else
                    None
            else
                None
        else
            None

    /// Recursively report whether a record type — or any nested record /
    /// list-of-record / option-of-record reachable from it — carries a
    /// `ValidationAttribute`. `visited` guards against cyclic types.
    let rec private recordHasValidations (visited: HashSet<Type>) (recordType: Type) : bool =
        if not (FSharpType.IsRecord(recordType, reflectionFlags)) then
            false
        elif not (visited.Add recordType) then
            false
        else
            FSharpType.GetRecordFields(recordType, reflectionFlags)
            |> Array.exists (fun pi ->
                let hasDirect =
                    pi.GetCustomAttributes(true)
                    |> Array.exists (fun a -> tryNormalise a |> Option.isSome)

                hasDirect
                || (match nestedRecordType pi.PropertyType with
                    | Some nested -> recordHasValidations visited nested
                    | None -> false))

    /// Cache per-method: input type ONLY when the method's input record
    /// (or a record nested within it) carries at least one validation
    /// attribute. Methods with non-record inputs (or records without
    /// validators anywhere in the tree) are absent from the map, so
    /// per-request lookup is a fast `Map.tryFind` miss.
    let classify (apiType: Type) : Map<string, Type> =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            Map.empty
        else
            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.choose (fun apiField ->
                match firstInputType apiField with
                | Some inputT when recordHasValidations (HashSet<Type>()) inputT -> Some(apiField.Name, inputT)
                | _ -> None)
            |> Map.ofArray

    /// Instantiate an `IFieldValidator` by type (parameterless ctor).
    /// Returns `None` if the type doesn't implement the interface or
    /// can't be constructed — a misconfigured custom validator must not
    /// crash the request path (it just doesn't run).
    let private instantiateValidator (validatorType: Type) : IFieldValidator option =
        try
            match Activator.CreateInstance validatorType with
            | :? IFieldValidator as v -> Some v
            | _ -> None
        with _ ->
            None

    /// Unwrap an F# `option` value reflectively — `Some inner` → `Some
    /// inner`, `None` → `None`.
    let private optionInner (optionType: Type) (value: obj) : obj option =
        if isNull value then
            None
        else
            let case, fields = FSharpValue.GetUnionFields(value, optionType)
            if case.Name = "Some" then Some fields[0] else None

    /// Phase 69e.C — structured args for a built-in attribute violation,
    /// so an `IValidationMessages` resolver can rebuild a localised message
    /// without parsing the English default. Keys mirror the `{token}`
    /// placeholders the default templates use (`min`, `max`, `actual`,
    /// `pattern`). `actual` is the offending value formatted the same way
    /// the built-in message formats it (string length as an integer,
    /// numerics via `%g`); `[<Custom>]` validators own their message and
    /// carry no structured args.
    let private buildArgs (attr: ValidationAttribute) (value: obj) : Map<string, string> =
        let strLen =
            match value with
            | :? string as s when not (isNull s) -> Some s.Length
            | _ -> None

        let withActualLen pairs =
            match strLen with
            | Some l -> ("actual", string l) :: pairs
            | None -> pairs

        let withActualNum pairs =
            match Numeric.asFloat value with
            | Some v -> ("actual", sprintf "%g" v) :: pairs
            | None -> pairs

        match attr with
        | :? MinLengthAttribute as a -> Map.ofList (withActualLen [ "min", string a.MinLength ])
        | :? MaxLengthAttribute as a -> Map.ofList (withActualLen [ "max", string a.MaxLength ])
        | :? RegexAttribute as a -> Map.ofList [ "pattern", a.Pattern ]
        | :? RangeAttribute as a -> Map.ofList (withActualNum [ "min", sprintf "%g" a.Min; "max", sprintf "%g" a.Max ])
        | :? MinValueAttribute as a -> Map.ofList (withActualNum [ "min", sprintf "%g" a.MinValue ])
        | :? MaxValueAttribute as a -> Map.ofList (withActualNum [ "max", sprintf "%g" a.MaxValue ])
        | _ -> Map.empty // NotEmpty / Email / Uri carry no structured params

    /// Phase 69e.C — apply the optional message resolver to one violation.
    /// `None` (no seam composed) or a resolver returning `None` falls back
    /// to the attribute's built-in English message, so the wire shape is
    /// unchanged by default (GP 11 / GP 13).
    let private resolveMessage
        (messages: IValidationMessages option)
        (code: string)
        (path: string)
        (args: Map<string, string>)
        (defaultMessage: string)
        : string =
        match messages with
        | None -> defaultMessage
        | Some m ->
            match
                m.Resolve {
                    Code = code
                    Path = path
                    Args = args
                    DefaultMessage = defaultMessage
                }
            with
            | Some localised -> localised
            | None -> defaultMessage

    /// Evaluate every validation attribute across the input record's
    /// fields AND recursively into nested records, list / array / seq
    /// elements, and option-wrapped records. Violations carry a dotted /
    /// indexed path (`Address.Postcode`, `Lines[2].Sku`). Collect-then-
    /// emit: every violation from one bad input is reported in a single
    /// pass (no short-circuit). `context` is handed to any `[<Custom>]`
    /// `IFieldValidator`. `messages` (Phase 69e.C) optionally localises /
    /// overrides each violation's message; `None` keeps the built-in
    /// English text.
    let evaluate
        (messages: IValidationMessages option)
        (context: IValidationContext)
        (inputType: Type)
        (inputValue: obj)
        : FieldViolation list =
        let violations = ResizeArray<FieldViolation>()

        let rec walk (prefix: string) (recordType: Type) (recordValue: obj) =
            if isNull recordValue || not (FSharpType.IsRecord(recordType, reflectionFlags)) then
                ()
            else
                for pi in FSharpType.GetRecordFields(recordType, reflectionFlags) do
                    let value = pi.GetValue recordValue

                    let path = if prefix = "" then pi.Name else prefix + "." + pi.Name

                    for a in pi.GetCustomAttributes(true) do
                        match tryNormalise a with
                        | Some(:? CustomAttribute as c) ->
                            match instantiateValidator c.ValidatorType with
                            | Some v ->
                                match v.Validate(value, context) with
                                | Some message ->
                                    violations.Add {
                                        Path = path
                                        Code = "Custom"
                                        Message = resolveMessage messages "Custom" path Map.empty message
                                    }
                                | None -> ()
                            | None -> ()
                        | Some attr ->
                            match attr.Validate value with
                            | Some message ->
                                let code = attr.GetType().Name.Replace("Attribute", "")

                                violations.Add {
                                    Path = path
                                    Code = code
                                    Message = resolveMessage messages code path (buildArgs attr value) message
                                }
                            | None -> ()
                        | None -> ()

                    descend path pi.PropertyType value

        and descend (path: string) (fieldType: Type) (value: obj) =
            if isNull value then
                ()
            elif FSharpType.IsRecord(fieldType, reflectionFlags) then
                walk path fieldType value
            elif fieldType.IsArray then
                let e = fieldType.GetElementType()

                if FSharpType.IsRecord(e, reflectionFlags) then
                    (value :?> IEnumerable)
                    |> Seq.cast<obj>
                    |> Seq.iteri (fun i item -> walk (sprintf "%s[%d]" path i) e item)
            elif fieldType.IsGenericType then
                let gtd = fieldType.GetGenericTypeDefinition()
                let e = fieldType.GetGenericArguments()[0]

                if gtd = typedefof<_ option> && FSharpType.IsRecord(e, reflectionFlags) then
                    match optionInner fieldType value with
                    | Some inner -> walk path e inner
                    | None -> ()
                elif
                    (gtd = typedefof<_ list> || gtd = typedefof<seq<_>>)
                    && FSharpType.IsRecord(e, reflectionFlags)
                then
                    (value :?> IEnumerable)
                    |> Seq.cast<obj>
                    |> Seq.iteri (fun i item -> walk (sprintf "%s[%d]" path i) e item)

        walk "" inputType inputValue
        violations |> List.ofSeq

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
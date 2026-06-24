module ToolUp.Forms.Server.ValidationAttributeBridge

open System
open System.Reflection
open Microsoft.FSharp.Reflection
open ToolUp.Forms.FormSchema

// =============================================================================
// Phase 69e.F — Forms integration seam
// =============================================================================
//
// The SAME validation attributes that drive ToolUp.Remoting transport
// validation (Phase 69e) drive a Forms `FieldSchema` when an input record is
// shared between the transport and forms layers. A `[<MinLength 3>]` on a
// shared record field becomes a `LengthRange(Some 3, None)` validator on the
// inferred `FieldSchema`, so client-side form rendering matches server-side
// transport enforcement — one source of truth for "what shape is acceptable".
//
// Read family-agnostically by simple attribute name + reflected properties —
// the same bridge shape the dispatcher's validation engine uses — so a
// Fable-compiled Core API record carrying the tier-shared `ToolUp.Platform.*`
// mirrors is honoured here exactly like a server-tier record. The Forms layer
// takes no dependency on the Remoting attribute types; it reads metadata.

let private reflectionFlags = BindingFlags.Public ||| BindingFlags.NonPublic

let private attrName (a: obj) = a.GetType().Name

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

let private isNumericType (t: Type) =
    t = typeof<int> || t = typeof<int64> || t = typeof<float> || t = typeof<decimal>

/// The permissive email pattern the Remoting `EmailAttribute` uses —
/// kept in sync so the form rule matches transport enforcement.
[<Literal>]
let private emailPattern = @"^[^\s@]+@[^\s@]+\.[^\s@]+$"

/// Build the `FieldSchema` list for a shared input record. One entry per
/// record field; the validation attributes become the field's `Kind`
/// bounds, `Validators`, and `Required` flag. Returns `[]` for a
/// non-record type.
///
/// `DisplayName` defaults to the field name — callers typically post-
/// process the result to set friendlier labels / descriptions; the
/// validation shape is what this bridge owns.
let fieldsFromRecord (recordType: Type) : FieldSchema list =
    if not (FSharpType.IsRecord(recordType, reflectionFlags)) then
        []
    else
        FSharpType.GetRecordFields(recordType, reflectionFlags)
        |> Array.toList
        |> List.map (fun pi ->
            let attrs = pi.GetCustomAttributes(true)

            let has name =
                attrs |> Array.exists (fun a -> attrName a = name)

            let pick name reader =
                attrs |> Array.tryPick (fun a -> if attrName a = name then reader a else None)

            let minLen = pick "MinLengthAttribute" (fun a -> intProp a "MinLength")
            let maxLen = pick "MaxLengthAttribute" (fun a -> intProp a "MaxLength")

            let rangeBounds =
                pick "RangeAttribute" (fun a ->
                    match floatProp a "Min", floatProp a "Max" with
                    | Some lo, Some hi -> Some(Some lo, Some hi)
                    | _ -> None)

            let minVal = pick "MinValueAttribute" (fun a -> floatProp a "MinValue")
            let maxVal = pick "MaxValueAttribute" (fun a -> floatProp a "MaxValue")
            let regex = pick "RegexAttribute" (fun a -> strProp a "Pattern")

            let customName =
                pick "CustomAttribute" (fun a ->
                    match a.GetType().GetProperty "ValidatorType" with
                    | null -> Some "custom"
                    | p ->
                        match p.GetValue a with
                        | :? Type as t -> Some t.Name
                        | _ -> Some "custom")

            let numericLow =
                match rangeBounds with
                | Some(lo, _) -> lo
                | None -> minVal

            let numericHigh =
                match rangeBounds with
                | Some(_, hi) -> hi
                | None -> maxVal

            let kind =
                if isNumericType pi.PropertyType then
                    NumberField(numericLow, numericHigh)
                elif pi.PropertyType = typeof<bool> then
                    BoolField
                elif pi.PropertyType = typeof<DateTime> then
                    DateTimeField
                else
                    TextField maxLen

            let validators = ResizeArray<ValidationRule>()

            match minLen, maxLen with
            | None, None -> ()
            | lo, hi -> validators.Add(LengthRange(lo, hi))

            match numericLow, numericHigh with
            | None, None -> ()
            | lo, hi -> validators.Add(NumberRange(lo, hi))

            match regex with
            | Some p -> validators.Add(Regex(p, None))
            | None -> ()

            if has "EmailAttribute" then
                validators.Add(Regex(emailPattern, Some "email"))

            if has "UriAttribute" then
                validators.Add(Custom "uri")

            match customName with
            | Some n -> validators.Add(Custom n)
            | None -> ()

            {
                Key = pi.Name
                DisplayName = pi.Name
                Description = None
                Kind = kind
                Required = has "NotEmptyAttribute"
                Validators = List.ofSeq validators
            })
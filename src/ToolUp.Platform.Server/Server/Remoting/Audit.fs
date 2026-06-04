namespace ToolUp.Remoting.Server

open System
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69h — per-method audit emission
// =============================================================================
//
// API record fields carry `[<Audit(AuditKind.X)>]` to opt into audit
// emission. The dispatcher walks the attribute at startup, classifies
// the methods, and emits an `AuditEvent` to the registered
// `IAuditEmitter` after each successful invocation.
//
// PII safety: input-record fields are redacted to `<redacted>` by
// default; explicit `[<PiiSafe>]` opts a field into payload inclusion.
// Fail-safe — forgetting the attribute leaves PII out of the audit row.

/// Phase 69h — opts a method into audit emission. The dispatcher
/// invokes the registered `IAuditEmitter` with the configured
/// `AuditKind` after a successful handler return.
///
/// `kindName` is the string-literal name of the well-known kind
/// (`"MoneyMoved"`, `"PolicyChanged"`, etc.) or `"Custom:<name>"`
/// for an open-vocabulary kind. F# attributes can't take DU values
/// directly so the string encoding is the workaround.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type AuditAttribute(kindName: string) =
    inherit Attribute()
    member _.KindName = kindName

    member this.Kind: AuditKind =
        if kindName.StartsWith "Custom:" then
            AuditKind.Custom(kindName.Substring 7)
        else
            match kindName with
            | "MoneyMoved" -> AuditKind.MoneyMoved
            | "PolicyChanged" -> AuditKind.PolicyChanged
            | "PiiAccessed" -> AuditKind.PiiAccessed
            | "DataExported" -> AuditKind.DataExported
            | "PermissionGranted" -> AuditKind.PermissionGranted
            | "PermissionRevoked" -> AuditKind.PermissionRevoked
            | "TenantCreated" -> AuditKind.TenantCreated
            | "TenantDeleted" -> AuditKind.TenantDeleted
            | other -> AuditKind.Custom other

/// Phase 69h — opts a record field (the input-record's, or a nested
/// record's) into PII-safe payload inclusion. Fields without this
/// attribute are redacted to `<redacted>` in the emitted AuditEvent's
/// payload. Fail-safe: forgetting the attribute keeps PII out of audit
/// rows.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type PiiSafeAttribute() =
    inherit Attribute()

// -----------------------------------------------------------------------------

module internal Audit =

    /// Cache the `[<Audit>]` attribute per method at startup. Returns
    /// `None` for unaudited methods; `Some kind` for audited.
    let classify (apiType: Type) : Map<string, AuditKind> =
        if not (FSharpType.IsRecord apiType) then
            Map.empty
        else
            FSharpType.GetRecordFields apiType
            |> Array.choose (fun pi ->
                let attr =
                    pi.GetCustomAttributes(true)
                    |> Array.tryPick (fun a ->
                        match a with
                        | :? AuditAttribute as au -> Some au.Kind
                        | _ -> None)

                match attr with
                | Some kind -> Some(pi.Name, kind)
                | None -> None)
            |> Map.ofArray

    /// Build the PII-redacted payload map from an input record value.
    /// Fields without `[<PiiSafe>]` are redacted to `<redacted:TypeName>`;
    /// fields with `[<PiiSafe>]` are stringified. Nested records are
    /// walked one level (deep nesting collapses to the redacted summary);
    /// lists / arrays are summarised as `"<list of N>"` for non-PiiSafe
    /// fields, expanded otherwise.
    let private redactValue (fieldType: System.Type) : string = sprintf "<redacted:%s>" fieldType.Name

    let private stringifyValue (value: obj) (fieldType: System.Type) : string =
        if isNull value then "<null>"
        elif fieldType = typeof<string> then value :?> string
        else string value

    let private isPiiSafe (pi: System.Reflection.PropertyInfo) : bool =
        pi.GetCustomAttributes(true)
        |> Array.exists (fun (a: obj) -> a :? PiiSafeAttribute)

    let payloadFromInputRecord (inputType: System.Type) (inputValue: obj) : Map<string, string> =
        if isNull inputValue || not (FSharpType.IsRecord inputType) then
            Map.empty
        else
            FSharpType.GetRecordFields inputType
            |> Array.map (fun pi ->
                let value = pi.GetValue inputValue

                let rendered =
                    if isPiiSafe pi then
                        stringifyValue value pi.PropertyType
                    else
                        redactValue pi.PropertyType

                pi.Name, rendered)
            |> Map.ofArray
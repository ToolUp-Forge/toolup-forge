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
            | "RateLimitExceeded" -> AuditKind.RateLimitExceeded
            | "IdempotencyReplay" -> AuditKind.IdempotencyReplay
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

    /// Decode the string-literal kind name shared by both attribute
    /// families ("MoneyMoved", "Custom:<name>", …) into an `AuditKind`.
    let decodeKind (kindName: string) : AuditKind =
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
            | "RateLimitExceeded" -> AuditKind.RateLimitExceeded
            | "IdempotencyReplay" -> AuditKind.IdempotencyReplay
            | other -> AuditKind.Custom other

    // ── Phase 727 severity assessment — the audit family ───────────────
    //
    // Phase 69h.tail recognised both attributes by bare simple type name,
    // to bridge this assembly's `ToolUp.Remoting.Server.*` family and the
    // tier-shared `ToolUp.Platform.*` mirror a Fable-compiled API record
    // carries. Phase 335 fixed the same defect in the auth classifier;
    // this is the assessment for the two markers here, and they are NOT
    // the same severity as each other.
    //
    // `[<PiiSafe>]` — SHARPEST OF THE FOUR FAMILIES, and the only one
    // whose forgery is a DATA-EXPOSURE defect rather than an availability
    // or correctness one. `isPiiSafe` returning true is what STOPS a
    // field being redacted: the value is stringified into the emitted
    // audit row's payload verbatim. So a foreign attribute named
    // `PiiSafeAttribute` on a consumer's input-record field silently
    // un-redacts it, and an audit row is not a local artefact — it is
    // replicated by every composed `IAuditSink` (S3 / GCS / Azure Blob /
    // Splunk / Datadog), i.e. straight out of the deployment's trust
    // boundary. The attribute's whole documented contract is "fail-safe:
    // forgetting the attribute keeps PII out of audit rows", and
    // simple-name matching meant a name a consumer never intended as ours
    // could satisfy it. GP 6: a row that claims PII-safety must have been
    // classified by something a forgery cannot satisfy. VERDICT: fix —
    // CLR identity, plus the startup collision refusal below.
    //
    // `[<Audit(kind)>]` — MEDIUM. A forgery does not expose anything: it
    // adds an audit row (noise, with a `KindName` decoded from a foreign
    // string property). The sharp direction is the OTHER one, and it is
    // the reason the fix needs the collision refusal rather than a
    // silent tightening: a consumer whose own `AuditAttribute` was being
    // honoured by accident would, under a bare identity fix, silently
    // stop emitting audit rows for a method they believe audited — a
    // compliance surface going quiet with nothing anywhere saying so.
    // Refusing at startup and naming the attribute is what makes the
    // tightening safe. VERDICT: fix — CLR identity + collision refusal.
    //
    // Both families are compile-time referenceable here (Platform.Server
    // references Platform.Core), so the sets are built from `typeof<>`.
    let private auditMarkers =
        MarkerFamily [ typeof<AuditAttribute>; typeof<ToolUp.Platform.AuditAttribute> ]

    let private piiMarkers =
        MarkerFamily [ typeof<PiiSafeAttribute>; typeof<ToolUp.Platform.PiiSafeAttribute> ]

    let private tryAuditKind (a: obj) : AuditKind option =
        let t = a.GetType()

        if not (auditMarkers.IsSanctioned t) then
            None
        else
            match a with
            | :? AuditAttribute as au -> Some au.Kind
            | _ ->
                // The sanctioned tier-shared mirror: same shape, read
                // reflectively because it cannot inherit the server-tier
                // type. The identity gate above has already established
                // that this IS the mirror, so the property read is a
                // decode of a known type rather than a name-based guess.
                match t.GetProperty "KindName" with
                | null -> None
                | p ->
                    match p.GetValue a with
                    | :? string as kindName when not (isNull kindName) -> Some(decodeKind kindName)
                    | _ -> None

    // Same non-public reflection rule as `AuthClassifier.reflectionFlags`:
    // internal/private API records (and private input records) must
    // classify identically to public ones — an empty map here silently
    // skips audit emission for them (fail-open).
    let private reflectionFlags =
        System.Reflection.BindingFlags.Public
        ||| System.Reflection.BindingFlags.NonPublic

    /// Cache the `[<Audit>]` attribute per method at startup. Returns
    /// `None` for unaudited methods; `Some kind` for audited.
    let classify (apiType: Type) : Map<string, AuditKind> =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            Map.empty
        else
            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.choose (fun pi ->
                let attr = pi.GetCustomAttributes(true) |> Array.tryPick tryAuditKind

                match attr with
                | Some kind -> Some(pi.Name, kind)
                | None -> None)
            |> Map.ofArray

    /// Phase 69h.tail — cache the first-argument record type per audited
    /// method so the dispatcher can build the PII-redacted payload even
    /// when the input type carries no `ValidationAttribute` (pre-69h.tail
    /// the payload extraction rode the validation classifier only, so
    /// audited methods without validators always emitted empty payloads).
    /// Same first-input derivation as `Validation.firstInputType` —
    /// replicated locally because this file compiles before Validation.fs.
    let inputTypes (apiType: Type) : Map<string, Type> =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            Map.empty
        else
            let audited = classify apiType

            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.choose (fun pi ->
                if not (audited.ContainsKey pi.Name) then
                    None
                elif FSharpType.IsFunction pi.PropertyType then
                    let inputT, _ = FSharpType.GetFunctionElements pi.PropertyType

                    if FSharpType.IsRecord(inputT, reflectionFlags) then
                        Some(pi.Name, inputT)
                    else
                        None
                else
                    None)
            |> Map.ofArray

    /// Phase 727 — marker-name collisions across the audit surface, as
    /// `(surface, subject, renderedAttributeType)` triples for the
    /// dispatcher's startup refusal (`MarkerCollision.refusal`).
    ///
    /// Two scans, because the two markers sit on different records: an
    /// `[<Audit>]` collision is on the API record's own fields, while a
    /// `[<PiiSafe>]` collision is on the fields of an AUDITED method's
    /// input record — which is why this runs after `inputTypes`.
    ///
    /// The PII scan is deliberately one level deep, matching
    /// `payloadFromInputRecord`'s reach exactly: that function stringifies
    /// or redacts the input record's own fields and does not recurse, so
    /// scanning deeper would report collisions on fields the redaction
    /// switch never consults. If the payload walk ever gains real nesting,
    /// this scan follows it in the same commit.
    ///
    /// A method whose `[<Audit>]` is itself foreign is not audited, so its
    /// input record never reaches the payload builder — the audit
    /// collision is reported and the PII scan simply has nothing to say
    /// about it.
    let foreignMarkers (apiType: Type) : (string * string * string) list =
        let onApiRecord =
            auditMarkers.Collisions(apiType, reflectionFlags)
            |> List.map (fun (field, rendered) -> "audit emission", field, rendered)

        let onInputRecords =
            inputTypes apiType
            |> Map.toList
            |> List.collect (fun (methodName, inputType) ->
                piiMarkers.Collisions(inputType, reflectionFlags)
                |> List.map (fun (field, rendered) ->
                    "PII-safe audit payload", sprintf "%s input field %s" methodName field, rendered))

        onApiRecord @ onInputRecords

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
        // Phase 727 — CLR identity, not simple name. This predicate is
        // the redaction switch: returning true puts the field's value
        // into the audit payload un-redacted. See the severity note at
        // the head of this module for why a forgeable switch here is a
        // data-exposure defect rather than a tidiness one.
        pi.GetCustomAttributes(true)
        |> Array.exists (fun (a: obj) -> piiMarkers.IsSanctioned(a.GetType()))

    let payloadFromInputRecord (inputType: System.Type) (inputValue: obj) : Map<string, string> =
        if isNull inputValue || not (FSharpType.IsRecord(inputType, reflectionFlags)) then
            Map.empty
        else
            FSharpType.GetRecordFields(inputType, reflectionFlags)
            |> Array.map (fun pi ->
                let value = pi.GetValue inputValue

                let rendered =
                    if isPiiSafe pi then
                        stringifyValue value pi.PropertyType
                    else
                        redactValue pi.PropertyType

                pi.Name, rendered)
            |> Map.ofArray
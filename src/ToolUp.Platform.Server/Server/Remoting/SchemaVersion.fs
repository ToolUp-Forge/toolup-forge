namespace ToolUp.Remoting.Server

open System
open System.Globalization
open System.Reflection
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69j — schema-versioned wire envelopes
// =============================================================================
//
// Phase 69j.A shipped the envelope half: `CategorisedErrorResult` carries
// `__schema_version`, `RemotingOptions.SchemaVersion` defaults to 1 and
// `Errors.categorisedWithSchema` stamps it. This file ships the NEGOTIATION
// half — the `X-Remoting-Schema` request header, the per-method supported-
// version vector, and the routing decision the adapter acts on.
//
// ─── The shape, in one paragraph ─────────────────────────────────────────
//
// A record field named `<Name>_V<n>` is a VERSIONED HANDLER for the logical
// method `<Name>`. The versions it serves are whatever `[<SupportsSchema>]`
// declares on it, defaulting to the `<n>` in its own suffix. A field with no
// `_V<n>` suffix and no attribute is the ordinary, unversioned case — the
// whole surface as it stands today — and serves exactly one version: the
// server's configured `RemotingOptions.SchemaVersion` (default 1). A caller
// asking for a version the addressed method does not serve is REFUSED with
// the supported vector rather than silently served the wrong shape, which is
// the entire point of having a version on the wire.
//
// ─── Why the refusal is not a new `ErrorCategory` case ───────────────────
//
// `ErrorCategory` is a closed, public, `[<RequireQualifiedAccess>]` DU that
// consumers match exhaustively. Adding a case to it would break every such
// match at compile time across every downstream consumer — an ADDITION that
// is nonetheless a source break. An unsupported schema version is a
// caller-side mistake, which is precisely what `ErrorCategory.User` means, so
// the refusal rides the existing category and carries its discriminating
// detail in a structured payload (`schemaVersion.requested` +
// `schemaVersion.supported`). A client distinguishes it by the presence of
// that field, not by a union case it had to recompile to see.
//
// ─── Why negotiation is one function, not two adapter copies ─────────────
//
// `negotiate` is total, pure and adapter-agnostic: it takes the raw header
// value, the server default, the classification table and the addressed
// method name, and returns a decision. The Giraffe adapter acts on it. The
// AspNetCore middleware adapter does NOT — it refuses at compose time to
// serve a schema-versioned record at all (see the seam-parity guard in
// `AspNetCore/Middleware.fs`), which is the same posture it already takes to
// every other Phase 69b–69k seam. Sharing the decision rather than the
// enforcement is what keeps "the header means the same thing wherever it is
// honoured" true without pretending the second adapter honours it.

/// Phase 69j — declares which wire-schema versions an API record field
/// serves.
///
/// Placed on a versioned handler (`GetThing_V2`) it OVERRIDES the version
/// implied by the suffix, so one handler can serve several versions:
/// `[<SupportsSchema [| 1; 2 |]>] GetThing_V1` serves both. Placed on an
/// un-suffixed field it declares that field's versions outright.
///
/// A method carrying no `[<SupportsSchema>]` and no `_V<n>` suffix is
/// version-1-only (more precisely: it serves the server's configured
/// default version and nothing else) — which is every method that exists
/// today, so the attribute changes nothing until someone reaches for it.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type SupportsSchemaAttribute(versions: int[]) =
    inherit Attribute()

    /// Single-version convenience — `[<SupportsSchema 2>]`.
    new(version: int) = SupportsSchemaAttribute [| version |]

    /// The wire-schema versions this handler serves.
    member _.Versions = versions

/// Phase 69j — marks a wire-schema version as scheduled for retirement.
///
/// `retireAfter` is an ISO-8601 date (`"2027-01-01"`) or date-time, NOT a
/// `System.DateTime`: CLR attribute arguments must be compile-time constants
/// (primitives, `string`, `Type`, enums and arrays of those), and
/// `DateTime` is none of those — an attribute taking one does not compile.
/// The string is parsed once at classification time under the invariant
/// culture; an unparseable value is a compose-time refusal rather than a
/// silently-ignored annotation.
///
/// Serving a deprecated version emits one line to the composed
/// `RemotingOptions.DiagnosticsLogger` per call. It does NOT refuse: the
/// whole purpose of a deprecation window is that the old version keeps
/// working while its callers migrate. Retirement is the operator deleting
/// the handler on the timeline they published, not the dispatcher deciding
/// unilaterally on a date.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type DeprecatedSchemaAttribute(retireAfter: string) =
    inherit Attribute()

    /// The published retirement date, ISO-8601.
    member _.RetireAfter = retireAfter

/// Phase 69j — everything the dispatcher knows about one logical method's
/// wire-schema support, computed once at compose time.
type MethodSchema = {
    /// version → the API record field name that serves it. Non-empty for
    /// every entry `classify` produces; the empty case is admitted only so
    /// `supportedVersions` is total over a value a consumer constructed.
    Handlers: Map<int, string>
    /// version → published retirement date, for versions annotated
    /// `[<DeprecatedSchema>]`.
    Deprecated: Map<int, DateTime>
}

/// Phase 69j — what the dispatcher should do with a request, given the
/// version the caller asked for.
[<RequireQualifiedAccess>]
type SchemaRouting =
    /// Nothing to negotiate: the addressed method is unversioned and the
    /// caller either sent no header or asked for the version the server
    /// already serves. Dispatch exactly as before. This is also the case a
    /// request for another API's method lands on in a multi-API `choose`
    /// composition, so it must never be treated as an error.
    | AsAddressed
    /// Serve `Field` at `Version`. `Field` is the API record field name;
    /// when it differs from the addressed name the adapter rewrites the
    /// route's trailing segment to it. `DeprecatedUntil` is `Some` when the
    /// served version carries `[<DeprecatedSchema>]`.
    | Serve of Field: string * Version: int * DeprecatedUntil: DateTime option
    /// The caller asked for a version this method does not serve. Refused
    /// with the supported vector. `Requested` is echoed as the caller sent
    /// it, so a malformed header is reported rather than coerced.
    | Refused of Requested: string * Supported: int list

module SchemaVersion =

    /// The request header a client sends to pin the wire schema it expects.
    /// Absent ⇒ the server serves its configured default
    /// (`RemotingOptions.SchemaVersion`, itself 1 unless composed otherwise).
    [<Literal>]
    let Header = "X-Remoting-Schema"

    /// Phase 69d.tail's flags, for the reason recorded there and in every
    /// sibling classifier: an internal or private API record is a record
    /// too, and without `NonPublic` its fields are invisible — which would
    /// make this classifier silently return an empty table for exactly the
    /// module-internal contracts the platform composes most.
    let internal reflectionFlags = BindingFlags.Public ||| BindingFlags.NonPublic

    /// Compose-time refusal. Kept beside the classifier so the diagnostic
    /// and the rule that produces it cannot drift apart.
    let private refuse<'a> (apiTypeName: string) (detail: string) : 'a =
        invalidOp (
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' declares an invalid wire-schema versioning. %s Fix the annotation, or drop it — a method with neither a [<SupportsSchema>] attribute nor a _V<n> field-name suffix serves the composed default version only."
                apiTypeName
                detail
        )

    /// Split a field name into `(logicalName, declaredVersion)`.
    /// `GetThing_V2` → `("GetThing", Some 2)`; anything else →
    /// `(fieldName, None)`.
    ///
    /// The suffix must be `_V` followed by one or more DIGITS and nothing
    /// else, so `Method_Variant` and `Load_V2Beta` stay ordinary names. A
    /// leading zero (`_V01`) parses to the same version as `_V1`, which
    /// would make two fields collide; that collision is caught by the
    /// duplicate-version refusal in `classify` rather than quietly resolved.
    let internal splitVersionSuffix (fieldName: string) : string * int option =
        let marker = fieldName.LastIndexOf "_V"

        if marker <= 0 || marker + 2 >= fieldName.Length then
            fieldName, None
        else
            let tail = fieldName.Substring(marker + 2)

            if tail |> Seq.forall Char.IsDigit then
                match Int32.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, n -> fieldName.Substring(0, marker), Some n
                | _ -> fieldName, None
            else
                fieldName, None

    let private recordFields (apiType: Type) : PropertyInfo[] =
        if FSharpType.IsRecord(apiType, reflectionFlags) then
            FSharpType.GetRecordFields(apiType, reflectionFlags)
        else
            [||]

    let private declaredVersions (field: PropertyInfo) : int list option =
        field.GetCustomAttributes(true)
        |> Array.tryPick (fun a ->
            match a with
            | :? SupportsSchemaAttribute as s -> Some(s.Versions |> Array.toList |> List.distinct |> List.sort)
            | _ -> None)

    let private declaredRetirement (field: PropertyInfo) : string option =
        field.GetCustomAttributes(true)
        |> Array.tryPick (fun a ->
            match a with
            | :? DeprecatedSchemaAttribute as d -> Some d.RetireAfter
            | _ -> None)

    /// Classify one API record's wire-schema support. The result is keyed
    /// by LOGICAL method name — the name a caller addresses — and holds
    /// ONLY the methods that participate in versioning. An ordinary
    /// unversioned method is absent, so the per-request lookup is a fast
    /// `Map.tryFind` miss on every record that never opted in.
    ///
    /// Refuses (rather than resolving quietly) on three shapes that cannot
    /// be served correctly: a version served by two handlers, a
    /// `[<SupportsSchema>]` declaring a non-positive version, and a
    /// `[<DeprecatedSchema>]` whose date does not parse. Each is a
    /// composition whose author believes it means something specific, and
    /// each would otherwise take effect as something else.
    let classify (apiType: Type) : Map<string, MethodSchema> =
        let entries =
            recordFields apiType
            |> Array.collect (fun field ->
                let logical, suffix = splitVersionSuffix field.Name

                let versions =
                    match declaredVersions field, suffix with
                    | Some vs, _ -> vs
                    | None, Some n -> [ n ]
                    | None, None -> []

                versions
                |> List.iter (fun v ->
                    if v <= 0 then
                        refuse
                            apiType.Name
                            (sprintf
                                "Field '%s' declares wire-schema version %d; versions are positive integers."
                                field.Name
                                v))

                let retirement =
                    declaredRetirement field
                    |> Option.map (fun raw ->
                        match
                            DateTime.TryParse(
                                raw,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                            )
                        with
                        | true, parsed -> parsed
                        | _ ->
                            refuse
                                apiType.Name
                                (sprintf
                                    "Field '%s' carries [<DeprecatedSchema \"%s\">], which is not an ISO-8601 date. Use a form like \"2027-01-01\"."
                                    field.Name
                                    raw))

                versions
                |> List.map (fun v -> logical, v, field.Name, retirement)
                |> List.toArray)

        entries
        |> Array.groupBy (fun (logical, _, _, _) -> logical)
        |> Array.map (fun (logical, forMethod) ->
            let handlers =
                forMethod
                |> Array.fold
                    (fun acc (_, version, fieldName, _) ->
                        match Map.tryFind version acc with
                        | Some existing when existing <> fieldName ->
                            refuse
                                apiType.Name
                                (sprintf
                                    "Method '%s' has two handlers for wire-schema version %d ('%s' and '%s'). One version, one handler."
                                    logical
                                    version
                                    existing
                                    fieldName)
                        | _ -> Map.add version fieldName acc)
                    Map.empty

            let deprecated =
                forMethod
                |> Array.choose (fun (_, version, _, retirement) -> retirement |> Option.map (fun d -> version, d))
                |> Map.ofArray

            logical,
            {
                Handlers = handlers
                Deprecated = deprecated
            })
        |> Map.ofArray

    /// The supported-version vector for one logical method, sorted
    /// ascending. An unversioned method supports the composed server
    /// default and nothing else.
    let supportedVersions (serverDefault: int) (schema: MethodSchema) : int list =
        if Map.isEmpty schema.Handlers then
            [ serverDefault ]
        else
            schema.Handlers |> Map.toList |> List.map fst

    /// Phase 69j.D — every logical method on `apiType` with the versions it
    /// serves: the deploy-time discovery vector. Versioned handlers appear
    /// ONCE, under their logical name (`GetThing`, not `GetThing_V1` and
    /// `GetThing_V2`), because the logical name is what a caller addresses
    /// and the vector is what they may ask for. Unversioned methods appear
    /// with the composed server default, so the list is the whole API
    /// surface rather than only the part that opted in. Sorted by name, so
    /// two calls over the same record produce the same list.
    let describe (serverDefault: int) (apiType: Type) : (string * int list) list =
        let table = classify apiType

        let versionedFields =
            table
            |> Map.toList
            |> List.collect (fun (_, schema) -> schema.Handlers |> Map.toList |> List.map snd)
            |> Set.ofList

        let unversioned =
            recordFields apiType
            |> Array.filter (fun field -> not (versionedFields.Contains field.Name))
            |> Array.map (fun field -> field.Name, [ serverDefault ])
            |> Array.toList

        let versioned =
            table
            |> Map.toList
            |> List.map (fun (name, schema) -> name, supportedVersions serverDefault schema)

        unversioned @ versioned |> List.sortBy fst

    /// Read the requested version from the raw `X-Remoting-Schema` value.
    /// `None` for an absent or blank header. A present-but-unparseable
    /// value is `Some(raw, None)` — the caller asked for SOMETHING and got
    /// it wrong, which is a refusal and not a silent default.
    let internal parseRequested (raw: string option) : (string * int option) option =
        match raw with
        | None -> None
        | Some value when String.IsNullOrWhiteSpace value -> None
        | Some value ->
            let trimmed = value.Trim()

            match Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, n -> Some(trimmed, Some n)
            | _ -> Some(trimmed, None)

    /// The routing decision for one request. Total: every combination of
    /// (header absent / present / malformed) × (method versioned /
    /// unversioned) lands on exactly one case.
    ///
    /// Absent header ⇒ the server's configured default for an unversioned
    /// method, and the HIGHEST supported version for a versioned one. A
    /// method only becomes versioned when its author adds a `_V<n>` handler
    /// or the attribute, so no existing call site changes shape: before any
    /// second handler exists, highest is the only one there is.
    let negotiate
        (table: Map<string, MethodSchema>)
        (serverDefault: int)
        (rawHeader: string option)
        (addressedMethod: string)
        : SchemaRouting =
        match Map.tryFind addressedMethod table with
        | None ->
            // Unversioned — which includes a request for some OTHER API's
            // method in a multi-API `choose` composition. Serving is
            // unchanged; only a header naming a version this server does not
            // serve is a refusal.
            match parseRequested rawHeader with
            | Some(raw, Some n) when n <> serverDefault -> SchemaRouting.Refused(raw, [ serverDefault ])
            | Some(raw, None) -> SchemaRouting.Refused(raw, [ serverDefault ])
            | _ -> SchemaRouting.AsAddressed
        | Some schema ->
            let supported = supportedVersions serverDefault schema

            let serve version =
                let field =
                    schema.Handlers |> Map.tryFind version |> Option.defaultValue addressedMethod

                SchemaRouting.Serve(field, version, Map.tryFind version schema.Deprecated)

            match parseRequested rawHeader with
            | None -> serve (List.max supported)
            | Some(raw, None) -> SchemaRouting.Refused(raw, supported)
            | Some(raw, Some n) ->
                if List.contains n supported then
                    serve n
                else
                    SchemaRouting.Refused(raw, supported)

    /// The wire payload of a refusal — the requested value as the caller
    /// sent it and the vector the method does serve.
    let refusalPayload (methodName: string) (requested: string) (supported: int list) : obj =
        box {|
            methodName = methodName
            schemaVersion = {|
                requested = requested
                supported = supported
            |}
            message =
                sprintf
                    "Unsupported wire-schema version '%s' for method '%s'. Supported: [%s]."
                    requested
                    methodName
                    (supported |> List.map string |> String.concat "; ")
        |}

    /// Apply a routing decision to the request route, rewriting only the
    /// trailing segment. The prefix is preserved verbatim, so a sub-routed
    /// or custom-built route keeps whatever shape it arrived with — and a
    /// decision that names the addressed field returns the route unchanged.
    let applyRouting (routing: SchemaRouting) (route: string) : string =
        match routing with
        | SchemaRouting.Serve(field, _, _) ->
            let lastSlash = route.LastIndexOf '/'

            if lastSlash >= 0 then
                route.Substring(0, lastSlash + 1) + field
            else
                field
        | _ -> route

    /// One line to the composed diagnostics logger when a deprecated
    /// version is served. Silent when no logger is composed — the seam pays
    /// nothing on a deployment that never opted into diagnostics (GP 13),
    /// and the retirement date is published through the docs schema
    /// regardless.
    let logDeprecation
        (logger: (string -> unit) option)
        (methodName: string)
        (version: int)
        (retireAfter: DateTime)
        : unit =
        logger
        |> Option.iter (fun log ->
            log (
                sprintf
                    "ToolUp.Remoting: method '%s' served wire-schema version %d, which is DEPRECATED and scheduled for retirement after %s. Migrate callers to a supported version before that date."
                    methodName
                    version
                    (retireAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            ))
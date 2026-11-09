namespace ToolUp.Remoting.Analyzers

// =============================================================================
// Compile-time analyzer recognition core
// =============================================================================
//
// PURE, dependency-free (FSharp.Core only) recognition logic for the classes the
// dispatcher refuses to start without:
//
//   * the auth classification `AuthClassifier` applies at startup in
//     ToolUp.Platform.Server/Server/Remoting/Auth.fs, raising "ToolUp.Remoting
//     refused to start: API record … unclassified" for any field carrying none
//     of the five classifications.
//   * the `[<Audit>]` / `[<PiiSafe>]` recognition in Audit.fs.
//
// The runtime matches a marker by CLR IDENTITY; the analyzer sees syntax, so it
// states the runtime's rule as two rules over names:
//
//   * TUR0001 — a field carrying no marker with a classifying SIMPLE NAME is
//     unclassified. `simpleName` reduces a source name (`[<RequiresRole "...">]`)
//     or a CLR name (`RequiresRoleAttribute`) to one key.
//   * TUR0003 — a marker with a classifying simple name that is NOT the SDK's
//     attribute, by the names the file shows (`isForeignMarker`), is a foreign
//     marker the runtime refuses as a collision. Where the file does not show
//     where an unqualified name comes from, it is taken to be the SDK's.
//
// This file is SOURCE-LINKED into the Platform test pack (a `<Compile Include>`
// link, NOT a project reference, so the lean test pack never drags FCS in via
// the analyzer's FSharp.Analyzers.SDK dependency). The parity test runs THIS
// decision code against the real internal `AuthClassifier` over fixture record
// types declared with the platform's own attributes.
//
// The analyzer (`Analyzer.fs`) extracts a normalised `FieldAttributes` list from
// the syntax tree and feeds it here.

module Recognition =

    /// One API-record field reduced to the data the recognisers need: the
    /// field (method) name and the names of every attribute applied to it.
    /// Names may be in either source form (`RequiresRole`) or CLR form
    /// (`RequiresRoleAttribute`) — `simpleName` reconciles them — and may be
    /// dotted (`ToolUp.Platform.RequiresRole`): the trailing segment is the
    /// name TUR0001 keys on, and the qualifier is what `isForeignMarker` reads.
    type FieldAttributes = {
        FieldName: string
        AttributeNames: string list
    }

    /// An API method paired with the audit-relevant facts about its input
    /// record (TUR0002). `InputRecordHasPiiSafeField` is true when
    /// the method's first-argument record carries at least one `[<PiiSafe>]`
    /// field — the signal that PII flows through the method.
    type ApiMethod = {
        Field: FieldAttributes
        InputRecordHasPiiSafeField: bool
    }

    type Code =
        | TUR0001
        | TUR0002
        | TUR0003

    let codeString =
        function
        | TUR0001 -> "TUR0001"
        | TUR0002 -> "TUR0002"
        | TUR0003 -> "TUR0003"

    type Finding = {
        Code: Code
        /// The offending field (method) name.
        FieldName: string
        /// Human-readable explanation, suitable as the diagnostic message.
        Message: string
        /// The placeholder attribute the codefix inserts to resolve the
        /// finding. Never a real role — always a fail-closed / TODO marker the
        /// author must replace deliberately. Empty when no insertion resolves
        /// the finding (TUR0003: the foreign marker has to go).
        SuggestedAttribute: string
    }

    /// Normalise an attribute reference to the single key both the analyzer
    /// (source names) and the runtime (CLR `.Name`) agree on: take the last
    /// dotted segment, then strip a trailing "Attribute" suffix.
    let simpleName (name: string) : string =
        let lastSegment =
            match name.LastIndexOf '.' with
            | -1 -> name
            | i -> name.Substring(i + 1)

        let trimmed = lastSegment.Trim()

        if trimmed.EndsWith "Attribute" && trimmed.Length > "Attribute".Length then
            trimmed.Substring(0, trimmed.Length - "Attribute".Length)
        else
            trimmed

    /// The five classifications — the exact set `AuthClassifier`
    /// recognises (RequiresRole / RequiresClaim → requirements; TenantScoped →
    /// TenantRequired; AllowAnonymous → Anonymous; PublicEndpoint → Public). A
    /// field carrying any one of these is classified; carrying none is the
    /// `Unclassified` verdict the dispatcher refuses to start on.
    let classifyingAuthAttributes =
        Set.ofList [
            "RequiresRole"
            "RequiresClaim"
            "TenantScoped"
            "AllowAnonymous"
            "PublicEndpoint"
        ]

    let private normalisedNames (f: FieldAttributes) : Set<string> =
        f.AttributeNames |> List.map simpleName |> Set.ofList

    /// True when the field carries ≥1 of the five classifications — i.e. the
    /// runtime would classify it Public / Anonymous / RequiresAuth rather than
    /// Unclassified.
    let isAuthClassified (f: FieldAttributes) : bool =
        normalisedNames f
        |> Set.intersect classifyingAuthAttributes
        |> Set.isEmpty
        |> not

    /// True when the field carries `[<Audit(...)>]` (either attribute family).
    let hasAudit (f: FieldAttributes) : bool =
        normalisedNames f |> Set.contains "Audit"

    /// The TUR0001 finding set for one API record's fields: every field with no
    /// auth classification. This is the parity anchor — it must equal
    /// `AuthClassifier.unclassified (AuthClassifier.classify apiType)` for the
    /// same record.
    let unclassifiedAuthFieldNames (fields: FieldAttributes list) : string list =
        fields |> List.filter (isAuthClassified >> not) |> List.map _.FieldName

    /// TUR0001 findings (missing auth classification) for an API record.
    let authFindings (fields: FieldAttributes list) : Finding list =
        fields
        |> List.filter (isAuthClassified >> not)
        |> List.map (fun f -> {
            Code = TUR0001
            FieldName = f.FieldName
            Message =
                sprintf
                    "API method '%s' carries no authorisation classification. Add one of [<RequiresRole>], [<RequiresClaim>], [<TenantScoped>], [<AllowAnonymous>], or [<PublicEndpoint>] — otherwise ToolUp.Remoting refuses to start when this record is composed (Phase 69d)."
                    f.FieldName
            // Fail-closed placeholder: AllowAnonymous still respects an auth
            // context for telemetry, and forces the author to confirm the
            // method really is open. We NEVER auto-pick a real role.
            SuggestedAttribute = "[<AllowAnonymous>]"
        })

    /// TUR0002 findings (PII flows through but the method isn't audited) for an
    /// API record's methods. Off by default at the analyzer seam — this is the
    /// pure decision; the analyzer gates emission on opt-in.
    let auditFindings (methods: ApiMethod list) : Finding list =
        methods
        |> List.filter (fun m -> m.InputRecordHasPiiSafeField && not (hasAudit m.Field))
        |> List.map (fun m -> {
            Code = TUR0002
            FieldName = m.Field.FieldName
            Message =
                sprintf
                    "API method '%s' takes an input record with a [<PiiSafe>] field but carries no [<Audit(...)>]. Compliance-sensitive methods should opt into dispatcher-emitted audit (Phase 69h)."
                    m.Field.FieldName
            SuggestedAttribute = "[<Audit(\"Custom:TODO\")>]"
        })

    /// The namespaces the SDK's markers are declared in: the server-tier
    /// family and the tier-shared mirrors, the two `AuthClassifier` accepts.
    let sdkMarkerNamespaces = set [ "ToolUp.Remoting.Server"; "ToolUp.Platform" ]

    /// What the scan sees of one file's name resolution.
    type NameScope = {
        /// The namespaces and modules the file opens or is declared in, each
        /// enclosing prefix included, so a partially qualified name resolves.
        Opened: string list
        /// The simple names (`simpleName`) of the types the file declares.
        Declared: Set<string>
    }

    /// True when `name`, a marker with a classifying simple name, is certainly
    /// not the SDK's attribute by what the file shows: qualified by a path that
    /// reaches no SDK namespace through any open, or unqualified while the file
    /// declares a type of that name itself (a local declaration is what the
    /// unqualified name resolves to). An unqualified name the file does not
    /// declare is the SDK's: the scan cannot see where it comes from, and a
    /// guess there would report what the runtime accepts.
    let isForeignMarker (scope: NameScope) (name: string) : bool =
        let simple = simpleName name

        if not (classifyingAuthAttributes.Contains simple) then
            false
        else
            let trimmed = name.Trim()

            match trimmed.LastIndexOf '.' with
            | -1 -> scope.Declared.Contains simple
            | i ->
                // `global.` roots the path; the parser spells it `` `global` ``.
                let qualifier =
                    let q = trimmed.Substring(0, i)

                    match q.IndexOf '.' with
                    | dot when dot > 0 && (q.StartsWith "global." || q.StartsWith "`global`.") -> q.Substring(dot + 1)
                    | _ -> q

                ("" :: scope.Opened)
                |> List.exists (fun opened ->
                    sdkMarkerNamespaces.Contains(if opened = "" then qualifier else opened + "." + qualifier))
                |> not

    /// TUR0003 findings (a foreign-named authorisation marker) for an API
    /// record's fields: one per marker that has a classifying simple name and
    /// is not the SDK's. TUR0001 reads such a field as classified; the runtime
    /// refuses the record at startup as a marker collision.
    let foreignMarkerFindings (scope: NameScope) (fields: FieldAttributes list) : Finding list =
        fields
        |> List.collect (fun f ->
            f.AttributeNames
            |> List.filter (isForeignMarker scope)
            |> List.map (fun name -> {
                Code = TUR0003
                FieldName = f.FieldName
                Message =
                    sprintf
                        "API method '%s' carries [<%s>], an authorisation marker by name that is not the SDK's (ToolUp.Platform or ToolUp.Remoting.Server). The runtime matches markers by type identity and refuses a foreign-named marker as a collision, so ToolUp.Remoting refuses to start when this record is composed. Use the SDK's attribute, qualified if a local type shadows it."
                        f.FieldName
                        name
                SuggestedAttribute = ""
            }))
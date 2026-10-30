namespace ToolUp.Remoting.Analyzers

// =============================================================================
// Phase 195 — compile-time analyzer recognition core
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
// THE ANALYZER CLASSIFIES BY SIMPLE NAME; THE RUNTIME MATCHES BY CLR IDENTITY.
// The analyzer sees syntax, so `simpleName` reduces a source name
// (`[<RequiresRole "...">]`) or a CLR name (`RequiresRoleAttribute`) to one key.
// `AuthClassifier` instead compares the attribute's type with the platform's own.
// The two agree on every platform attribute, and they part on one case: a
// foreign attribute carrying a platform marker's simple name is a runtime
// refusal that this analyzer reads as classified. That gap is a known finding,
// not a parity this file provides.
//
// This file is SOURCE-LINKED into ToolUp.Platform.Tests (a `<Compile Include>`
// link, NOT a project reference, so the lean test pack never drags FCS in via
// the analyzer's FSharp.Analyzers.SDK dependency). The parity test runs THIS
// decision code against the real internal `AuthClassifier` over fixture record
// types declared with the platform's own attributes, which is the set the two
// agree on.
//
// The analyzer (`Analyzer.fs`) extracts a normalised `FieldAttributes` list from
// the syntax tree and feeds it here.

module Recognition =

    /// One API-record field reduced to the data the recognisers need: the
    /// field (method) name and the names of every attribute applied to it.
    /// Names may be in either source form (`RequiresRole`) or CLR form
    /// (`RequiresRoleAttribute`) — `simpleName` reconciles them — and may be
    /// dotted (`ToolUp.Platform.RequiresRole`); only the trailing segment
    /// matters here, where the runtime compares the attribute's CLR type.
    type FieldAttributes = {
        FieldName: string
        AttributeNames: string list
    }

    /// An API method paired with the audit-relevant facts about its input
    /// record (Phase 69h / TUR0002). `InputRecordHasPiiSafeField` is true when
    /// the method's first-argument record carries at least one `[<PiiSafe>]`
    /// field — the signal that PII flows through the method.
    type ApiMethod = {
        Field: FieldAttributes
        InputRecordHasPiiSafeField: bool
    }

    type Code =
        | TUR0001
        | TUR0002

    let codeString =
        function
        | TUR0001 -> "TUR0001"
        | TUR0002 -> "TUR0002"

    type Finding = {
        Code: Code
        /// The offending field (method) name.
        FieldName: string
        /// Human-readable explanation, suitable as the diagnostic message.
        Message: string
        /// The placeholder attribute the codefix inserts to resolve the
        /// finding. Never a real role — always a fail-closed / TODO marker the
        /// author must replace deliberately.
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

    /// The five Phase 69d classifications — the exact set `AuthClassifier`
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
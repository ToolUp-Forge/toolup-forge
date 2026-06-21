namespace ToolUp.Remoting.Analyzers

// =============================================================================
// Phase 195 — ToolUp.Remoting auth/audit analyzer
// =============================================================================
//
// FSharp.Analyzers.SDK analyzer that shifts the Phase 69d startup refusal
// (`AuthClassifier` raising "ToolUp.Remoting refused to start: API record …
// unclassified") LEFT to an IDE squiggle / CI diagnostic:
//
//   * TUR0001 (Error) — a Remoting API-record field with no Phase 69d
//     authorisation classification. Mirrors `AuthClassifier`'s by-simple-name
//     recognition exactly (via `Recognition`), so the analyzer flags precisely
//     the set the dispatcher would refuse to start on.
//   * TUR0002 (Warning, opt-in) — an unaudited API method whose input record
//     carries a `[<PiiSafe>]` field (Phase 69h). Off by default; enabled by
//     setting the `TOOLUP_REMOTING_ANALYZER_AUDIT` environment variable.
//
// Each finding carries a one-keystroke codefix that inserts a fail-closed
// placeholder (`[<AllowAnonymous>]` / `[<Audit("Custom:TODO")>]`) — never a
// real role; the author must replace the placeholder deliberately.
//
// Pure tooling (GP 1/11/13): a consumer that never installs the analyzer is
// byte-for-byte unchanged, and the runtime classifier in Auth.fs is untouched.

module Analyzer =

    open FSharp.Compiler.Syntax
    open FSharp.Compiler.Text
    open FSharp.Analyzers.SDK

    [<Literal>]
    let private auditEnvVar = "TOOLUP_REMOTING_ANALYZER_AUDIT"

    let private auditEnabled () =
        match System.Environment.GetEnvironmentVariable auditEnvVar with
        | null
        | "" -> false
        | v ->
            v <> "0"
            && not (System.String.Equals(v, "false", System.StringComparison.OrdinalIgnoreCase))

    let private severityOf =
        function
        | Recognition.TUR0001 -> Severity.Error
        | Recognition.TUR0002 -> Severity.Warning

    /// Build a Message for one finding, sited on `field` and carrying the
    /// placeholder-insertion codefix.
    let private toMessage
        (record: Extraction.ExtractedRecord)
        (field: Extraction.ExtractedField)
        (finding: Recognition.Finding)
        : Message =
        let fix: Fix = {
            FromText = ""
            FromRange = field.InsertAt
            ToText = finding.SuggestedAttribute + " "
        }

        {
            Type = "ToolUp.Remoting.Analyzers"
            Message = finding.Message
            Code = Recognition.codeString finding.Code
            Severity = severityOf finding.Code
            Range = field.Range
            Fixes = [ fix ]
        }

    /// Pure analysis over a parsed input — the testable core the
    /// ToolUp.Remoting.Analyzers.Tests pack drives directly.
    let analyzeParseTree (auditOptIn: bool) (input: ParsedInput) : Message list =
        let allRecords = Extraction.records input

        // Records (anywhere in the file) declaring a [<PiiSafe>] field — the
        // TUR0002 input-record signal.
        let piiRecordNames =
            allRecords
            |> List.filter Extraction.hasPiiSafeField
            |> List.map (fun r -> r.Name.Substring(r.Name.LastIndexOf '.' + 1))
            |> Set.ofList

        allRecords
        |> List.filter Extraction.isApiRecordCandidate
        |> List.collect (fun record ->
            let byName = record.Fields |> List.map (fun f -> f.Name, f) |> Map.ofList

            let asAttrs (f: Extraction.ExtractedField) : Recognition.FieldAttributes = {
                FieldName = f.Name
                AttributeNames = f.AttributeNames
            }

            let authFindings = record.Fields |> List.map asAttrs |> Recognition.authFindings

            let auditFindings =
                if not auditOptIn then
                    []
                else
                    record.Fields
                    |> List.map (fun f -> {
                        Recognition.ApiMethod.Field = asAttrs f
                        Recognition.ApiMethod.InputRecordHasPiiSafeField =
                            match f.InputTypeName with
                            | Some t -> piiRecordNames.Contains t
                            | None -> false
                    })
                    |> Recognition.auditFindings

            authFindings @ auditFindings
            |> List.choose (fun finding ->
                byName
                |> Map.tryFind finding.FieldName
                |> Option.map (fun field -> toMessage record field finding)))

    let private run (parseTree: ParsedInput) : Async<Message list> = async {
        return analyzeParseTree (auditEnabled ()) parseTree
    }

    [<CliAnalyzer("ToolUpRemotingAuthAudit",
                  "Flags Remoting API methods missing an auth classification (TUR0001) or audit annotation (TUR0002).",
                  "https://github.com/ToolUp-Forge/toolup-forge/blob/main/src/ToolUp.Remoting.Analyzers/README.md")>]
    let cliAnalyzer: Analyzer<CliContext> =
        fun (ctx: CliContext) -> run ctx.ParseFileResults.ParseTree

    [<EditorAnalyzer("ToolUpRemotingAuthAudit",
                     "Flags Remoting API methods missing an auth classification (TUR0001) or audit annotation (TUR0002).",
                     "https://github.com/ToolUp-Forge/toolup-forge/blob/main/src/ToolUp.Remoting.Analyzers/README.md")>]
    let editorAnalyzer: Analyzer<EditorContext> =
        fun (ctx: EditorContext) -> run ctx.ParseFileResults.ParseTree
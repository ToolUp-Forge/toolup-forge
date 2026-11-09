namespace ToolUp.Remoting.Analyzers

// =============================================================================
// ToolUp.Remoting auth/audit analyzer
// =============================================================================
//
// FSharp.Analyzers.SDK analyzer that shifts the dispatcher's startup refusals
// (`AuthClassifier` raising "ToolUp.Remoting refused to start: API record …")
// LEFT to an IDE squiggle / CI diagnostic:
//
//   * TUR0001 (Error) — a Remoting API-record field with no authorisation
//     classification: no marker with a classifying simple name.
//   * TUR0003 (Error) — a field carrying a marker named like a classification
//     that the file shows is not the SDK's attribute. The runtime matches by
//     identity and refuses it as a collision; TUR0001 reads it as classified.
//     Together the two state the runtime's identity rule over names
//     (`Recognition`).
//   * TUR0002 (Warning, opt-in) — an unaudited API method whose input record
//     carries a `[<PiiSafe>]` field. Off by default; enabled by setting the
//     `TOOLUP_REMOTING_ANALYZER_AUDIT` environment variable.
//
// TUR0001 and TUR0002 carry a one-keystroke codefix that inserts a fail-closed
// placeholder (`[<AllowAnonymous>]` / `[<Audit("Custom:TODO")>]`) — never a
// real role; the author must replace the placeholder deliberately. TUR0003 has
// none: the foreign marker has to go.
//
// Pure tooling (GP 1/11/13): a consumer that never installs the analyzer is
// byte-for-byte unchanged, and the runtime classifier in Auth.fs is untouched.

module Analyzer =

    open FSharp.Compiler.Syntax
    open FSharp.Compiler.Text
    open FSharp.Analyzers.SDK

    [<Literal>]
    let private auditEnvVar = "TOOLUP_REMOTING_ANALYZER_AUDIT"

    /// The on spellings the config-key registry accepts for a boolean
    /// (`ConfigKeys.trueTokens`), repeated here because the analyzer
    /// cannot reference `ToolUp.Platform.Core`. The key is `Permissive`:
    /// anything else, a typo included, reads as off.
    let private onTokens = set [ "1"; "true"; "yes"; "on" ]

    let private auditEnabled () =
        match System.Environment.GetEnvironmentVariable auditEnvVar with
        | null -> false
        | v -> onTokens.Contains(v.Trim().ToLowerInvariant())

    let private severityOf =
        function
        | Recognition.TUR0001
        | Recognition.TUR0003 -> Severity.Error
        | Recognition.TUR0002 -> Severity.Warning

    /// Build a Message for one finding, sited on `field` and carrying the
    /// placeholder-insertion codefix.
    let private toMessage
        (record: Extraction.ExtractedRecord)
        (field: Extraction.ExtractedField)
        (finding: Recognition.Finding)
        : Message =
        let fixes: Fix list =
            if finding.SuggestedAttribute = "" then
                []
            else
                [
                    {
                        FromText = ""
                        FromRange = field.InsertAt
                        ToText = finding.SuggestedAttribute + " "
                    }
                ]

        {
            Type = "ToolUp.Remoting.Analyzers"
            Message = finding.Message
            Code = Recognition.codeString finding.Code
            Severity = severityOf finding.Code
            Range = field.Range
            Fixes = fixes
        }

    /// What the file shows of name resolution, per record: every namespace
    /// or module the file is declared in or opens (each enclosing prefix
    /// included, so a partially qualified name resolves), and the simple name
    /// of every type declared where an unqualified name in that record reaches
    /// it — the record's own module, an enclosing one, or one the file opens.
    /// Opens count file-wide, which errs toward reading a marker as the SDK's.
    let private scopesOf (input: ParsedInput) : range -> Recognition.NameScope =
        let names (ids: Ident list) = ids |> List.map (fun i -> i.idText)

        let prefixes (path: string list) = [ for i in 1 .. path.Length -> String.concat "." (List.take i path) ]

        let opens = ResizeArray<string>()
        let enclosing = ResizeArray<string>()
        let declared = ResizeArray<string list * string>()
        let records = ResizeArray<range * string list>()

        let rec walk (path: string list) (decls: SynModuleDecl list) =
            for decl in decls do
                match decl with
                | SynModuleDecl.Open(SynOpenDeclTarget.ModuleOrNamespace(SynLongIdent(ids, _, _), _), _) ->
                    opens.Add(String.concat "." (names ids))
                | SynModuleDecl.Types(defns, _) ->
                    for defn in defns do
                        let (SynTypeDefn(SynComponentInfo(longId = ids), _, _, _, range, _)) = defn
                        declared.Add(path, Recognition.simpleName (String.concat "." (names ids)))
                        records.Add(range, path)
                | SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(longId = ids); decls = nested) ->
                    walk (path @ names ids) nested
                | _ -> ()

        match input with
        | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
            for SynModuleOrNamespace(longId = ids; decls = decls) in modules do
                enclosing.AddRange(prefixes (names ids))
                walk (names ids) decls
        | ParsedInput.SigFile _ -> ()

        let opened =
            List.distinct (
                List.ofSeq enclosing
                @ (List.ofSeq opens
                   |> List.collect (fun o -> prefixes (List.ofArray (o.Split '.'))))
            )

        fun recordRange ->
            let path =
                records
                |> Seq.tryFind (fun (r, _) -> r = recordRange)
                |> Option.map snd
                |> Option.defaultValue []

            let reaches (declPath: string list) =
                (declPath.Length <= path.Length && List.take declPath.Length path = declPath)
                || opens.Contains(String.concat "." declPath)

            {
                Opened = opened
                Declared = declared |> Seq.filter (fst >> reaches) |> Seq.map snd |> Set.ofSeq
            }

    /// Pure analysis over a parsed input — the testable core the
    /// ToolUp.Remoting.Analyzers.Tests pack drives directly.
    let analyzeParseTree (auditOptIn: bool) (input: ParsedInput) : Message list =
        let allRecords = Extraction.records input
        let scopeOf = scopesOf input

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

            let fields = record.Fields |> List.map asAttrs
            let authFindings = Recognition.authFindings fields

            let foreignFindings =
                Recognition.foreignMarkerFindings (scopeOf record.Range) fields

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

            authFindings @ foreignFindings @ auditFindings
            |> List.choose (fun finding ->
                byName
                |> Map.tryFind finding.FieldName
                |> Option.map (fun field -> toMessage record field finding)))

    let private run (parseTree: ParsedInput) : Async<Message list> = async {
        return analyzeParseTree (auditEnabled ()) parseTree
    }

    [<CliAnalyzer("ToolUpRemotingAuthAudit",
                  "Flags Remoting API methods missing an auth classification (TUR0001), carrying a foreign-named auth marker (TUR0003) or missing an audit annotation (TUR0002).",
                  "https://github.com/ToolUp-Forge/toolup-forge/blob/main/src/ToolUp.Remoting.Analyzers/README.md")>]
    let cliAnalyzer: Analyzer<CliContext> =
        fun (ctx: CliContext) -> run ctx.ParseFileResults.ParseTree

    [<EditorAnalyzer("ToolUpRemotingAuthAudit",
                     "Flags Remoting API methods missing an auth classification (TUR0001), carrying a foreign-named auth marker (TUR0003) or missing an audit annotation (TUR0002).",
                     "https://github.com/ToolUp-Forge/toolup-forge/blob/main/src/ToolUp.Remoting.Analyzers/README.md")>]
    let editorAnalyzer: Analyzer<EditorContext> =
        fun (ctx: EditorContext) -> run ctx.ParseFileResults.ParseTree

    // The containment rules (TUA001–TUA003), registered as their own analyser so a host can list, enable or silence the family apart
    // from the Remoting auth/audit pair above.

    let private runContainment (parseTree: ParsedInput) : Async<Message list> = async {
        return ContainmentRules.analyze parseTree |> List.map ContainmentRules.toMessage
    }

    [<CliAnalyzer("ToolUpContainment",
                  "Flags container resolution outside registration (TUA001), an HttpClient built without the egress handler (TUA002) and Async.RunSynchronously outside an entry point (TUA003).",
                  "https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/platform/analyzer-rules.md")>]
    let containmentCliAnalyzer: Analyzer<CliContext> =
        fun (ctx: CliContext) -> runContainment ctx.ParseFileResults.ParseTree

    [<EditorAnalyzer("ToolUpContainment",
                     "Flags container resolution outside registration (TUA001), an HttpClient built without the egress handler (TUA002) and Async.RunSynchronously outside an entry point (TUA003).",
                     "https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/platform/analyzer-rules.md")>]
    let containmentEditorAnalyzer: Analyzer<EditorContext> =
        fun (ctx: EditorContext) -> runContainment ctx.ParseFileResults.ParseTree
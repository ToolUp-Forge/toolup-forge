namespace ToolUp.Remoting.Analyzers

// =============================================================================
// Phase 195 — syntax-tree extraction
// =============================================================================
//
// Walks an F# parse tree (untyped AST — attributes are syntactic, so no typed
// tree / project options are needed) and reduces every record type to the
// normalised shape `Recognition` consumes. This is the analyzer-only half: it
// references FCS syntax types and therefore is NOT source-linked into the lean
// Platform.Tests pack — it is exercised by the dedicated
// ToolUp.Remoting.Analyzers.Tests pack, which parses fixture sources offline.
//
// "API record" detection is a heuristic with no runtime analog (the dispatcher
// is handed the type explicitly at `Api.make`; the analyzer must guess from
// shape): a record is treated as a Remoting API contract when it has ≥1 field
// and EVERY field is a function type (`'a -> Async<'b>` etc.). Ordinary data
// records are never flagged. Classification of the fields, once a record is a
// candidate, is in lock-step with the runtime via `Recognition`.

module Extraction =

    open FSharp.Compiler.Syntax
    open FSharp.Compiler.Text

    /// A record field reduced to what the analyzer needs to classify it and to
    /// site a diagnostic / codefix.
    type ExtractedField = {
        Name: string
        AttributeNames: string list
        IsFunction: bool
        /// Last segment of the function-domain type when it is a named type
        /// (the method's input record), else None.
        InputTypeName: string option
        /// Range of the field identifier — where the squiggle lands.
        Range: range
        /// Zero-width range at the field's start, where the codefix inserts a
        /// placeholder attribute.
        InsertAt: range
    }

    type ExtractedRecord = {
        Name: string
        Fields: ExtractedField list
        Range: range
    }

    let private longIdentText (SynLongIdent(id, _, _)) =
        id |> List.map (fun (i: Ident) -> i.idText) |> String.concat "."

    let private attributeNames (attrs: SynAttributes) : string list =
        attrs
        |> List.collect (fun (al: SynAttributeList) -> al.Attributes)
        |> List.map (fun a -> longIdentText a.TypeName)

    /// Last segment of a named type used as a function domain, else None.
    let rec private namedTypeLastSegment (t: SynType) : string option =
        match t with
        | SynType.LongIdent lid -> Some(longIdentText lid |> fun s -> s.Substring(s.LastIndexOf '.' + 1))
        | SynType.App(typeName, _, _, _, _, _, _) -> namedTypeLastSegment typeName
        | SynType.Paren(inner, _) -> namedTypeLastSegment inner
        | _ -> None

    let private fieldFromSyn (field: SynField) : ExtractedField =
        let (SynField(attrs, _, idOpt, fieldType, _, _, _, range, _)) = field

        let name =
            match idOpt with
            | Some(id: Ident) -> id.idText
            | None -> ""

        let isFunction, inputTypeName =
            match fieldType with
            | SynType.Fun(argType, _, _, _) -> true, namedTypeLastSegment argType
            | _ -> false, None

        let identRange =
            match idOpt with
            | Some id -> id.idRange
            | None -> range

        // Zero-width insertion point at the start of the field declaration
        // (before any leading attributes; for an unattributed field this is
        // the identifier start).
        let insertStart =
            match attrs with
            | first :: _ -> first.Range.Start
            | [] -> identRange.Start

        let insertAt = Range.mkRange range.FileName insertStart insertStart

        {
            Name = name
            AttributeNames = attributeNames attrs
            IsFunction = isFunction
            InputTypeName = inputTypeName
            Range = identRange
            InsertAt = insertAt
        }

    let private recordFromTypeDefn (typeDefn: SynTypeDefn) : ExtractedRecord option =
        let (SynTypeDefn(SynComponentInfo(longId = longId), repr, _, _, range, _)) =
            typeDefn

        match repr with
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Record(_, fields, _), _) ->
            let name = longId |> List.map (fun (i: Ident) -> i.idText) |> String.concat "."

            Some {
                Name = name
                Fields = fields |> List.map fieldFromSyn
                Range = range
            }
        | _ -> None

    let rec private fromModuleDecls (decls: SynModuleDecl list) : ExtractedRecord list =
        decls
        |> List.collect (fun decl ->
            match decl with
            | SynModuleDecl.Types(typeDefns, _) -> typeDefns |> List.choose recordFromTypeDefn
            | SynModuleDecl.NestedModule(decls = nested) -> fromModuleDecls nested
            | _ -> [])

    /// All record type definitions in a parsed input.
    let records (input: ParsedInput) : ExtractedRecord list =
        match input with
        | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
            modules
            |> List.collect (fun (SynModuleOrNamespace(decls = decls)) -> fromModuleDecls decls)
        | ParsedInput.SigFile _ -> []

    /// A record is an API-contract candidate when it has ≥1 field and every
    /// field is a function type.
    let isApiRecordCandidate (r: ExtractedRecord) : bool =
        not (List.isEmpty r.Fields) && r.Fields |> List.forall _.IsFunction

    /// True when a record declares ≥1 field carrying `[<PiiSafe>]`.
    let hasPiiSafeField (r: ExtractedRecord) : bool =
        r.Fields
        |> List.exists (fun f -> f.AttributeNames |> List.exists (fun n -> Recognition.simpleName n = "PiiSafe"))
namespace ToolUp.Remoting.Analyzers

// =============================================================================
// TUA004 — a message over a kept or tree-built list that carries the
// enumeration index
// =============================================================================
//
// The rule (`docs/platform/authoring-trees.md`, "Row identity is the dispatch
// key"): a message sent from a row of a kept list carries the row's identity
// (`Bump row.Id`), never its position (`Bump i`). The metadata, rationale and
// sanctioned form live in `Rules.fs`.
//
// Syntax only, like TUA001–TUA003. An ENUMERATION INDEX is a name bound by:
//
//   * the first parameter of a lambda handed to `mapi` / `iteri` / `mapi2` /
//     `iteri2` (`List`, `Seq`, `Array`, any qualifier);
//   * the first component of a tuple pattern over an `indexed` source: a
//     lambda handed to a `map`-family function whose input is `*.indexed …`
//     (direct or piped), or a `for (i, x) in *.indexed … do` loop;
//   * the variable of a `for i in a .. b do` loop;
//   * in the build lambda of a `Keep.create`, a tuple component typed `int`,
//     or the first tuple component when the file feeds that keep (by its
//     binding name) through `Keep.list <name>` over an `indexed` source.
//
// A MESSAGE is an application whose head is an upper-case name (`Bump i`,
// `Msg.Select (i, x)`) other than the option / result / binding cases in
// `notMessages`, with the index named inside its argument. The outermost such
// application is the finding, so `Select (Some i)` is one site.
//
// SCOPE: a `Keep.create` build is always in scope. An index lambda or loop is
// in scope when its body builds a tree — it names `Nodes.*`, `Fuaran.*` or
// `Keep.*` — so a Feliz view, whose rows nothing keeps, is not reported.

module KeptListIndexAnalyzer =

    open FSharp.Compiler.Syntax
    open FSharp.Compiler.Text
    open FSharp.Analyzers.SDK

    [<Literal>]
    let Code = "TUA004"

    let private indexedFunctions = set [ "mapi"; "iteri"; "mapi2"; "iteri2" ]

    let private mapFamily =
        set [ "map"; "collect"; "iter"; "choose"; "filter"; "sortBy"; "groupBy" ]

    let private treeQualifiers = set [ "Nodes"; "Fuaran"; "Keep" ]

    let private notMessages =
        set [
            "Some"
            "ValueSome"
            "Ok"
            "Error"
            "Literal"
            "Static"
            "Choice1Of2"
            "Choice2Of2"
        ]

    let rec private strip (expr: SynExpr) =
        match expr with
        | SynExpr.Paren(expr = inner)
        | SynExpr.TypeApp(expr = inner)
        | SynExpr.Typed(expr = inner) -> strip inner
        | _ -> expr

    let private idsOf (expr: SynExpr) : Ident list =
        match strip expr with
        | SynExpr.Ident id -> [ id ]
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> ids
        | _ -> []

    let private lastName (expr: SynExpr) =
        match idsOf expr with
        | [] -> None
        | ids -> Some (List.last ids).idText

    let private isKeepCall (name: string) (expr: SynExpr) =
        match idsOf expr |> List.map _.idText |> List.rev with
        | n :: "Keep" :: _ -> n = name
        | _ -> false

    /// An operator parses as a one-name `LongIdent` (or an `Ident`), so the
    /// pipe is read by name either way.
    let private isPipe (expr: SynExpr) =
        match idsOf expr with
        | [ op ] -> op.idText = "op_PipeRight"
        | _ -> false

    /// `a |> f` as `(a, f)`.
    let private (|Piped|_|) (expr: SynExpr) =
        match strip expr with
        | SynExpr.App(funcExpr = SynExpr.App(funcExpr = op; argExpr = lhs); argExpr = rhs) when isPipe op ->
            Some(lhs, rhs)
        | _ -> None

    /// `*.indexed xs`, `xs |> *.indexed`, or a bare `*.indexed` value.
    let private isIndexed (expr: SynExpr) =
        match strip expr with
        | Piped(_, f) -> lastName f = Some "indexed"
        | SynExpr.App(funcExpr = f) -> lastName f = Some "indexed"
        | e -> lastName e = Some "indexed"

    let private lambdaOf (expr: SynExpr) =
        match strip expr with
        | SynExpr.Lambda(parsedData = Some(pats, body)) -> Some(pats, body)
        | _ -> None

    let rec private nameOf (pat: SynPat) =
        match pat with
        | SynPat.Named(ident = SynIdent(id, _)) -> Some id.idText
        | SynPat.Typed(pat = inner)
        | SynPat.Paren(pat = inner) -> nameOf inner
        | _ -> None

    let rec private tupleOf (pat: SynPat) =
        match pat with
        | SynPat.Paren(pat = inner) -> tupleOf inner
        | SynPat.Tuple(elementPats = elements) -> Some elements
        | _ -> None

    let rec private isIntTyped (pat: SynPat) =
        match pat with
        | SynPat.Paren(pat = inner) -> isIntTyped inner
        | SynPat.Typed(targetType = SynType.LongIdent(SynLongIdent(id = [ t ]))) -> t.idText = "int"
        | _ -> false

    let private firstOfTuple (pats: SynPat list) =
        match pats with
        | first :: _ ->
            match tupleOf first with
            | Some(element :: _) -> nameOf element
            | _ -> None
        | [] -> None

    /// A body in which named names are enumeration indices.
    type private Scope = {
        Indices: Set<string>
        Body: range
        /// Always in scope (a `Keep.create` build), else only when the body
        /// builds a tree.
        Kept: bool
    }

    type private Collector() =
        inherit ASTCollecting.SyntaxCollectorBase()

        member val Scopes = ResizeArray<Scope>()
        /// `let name = Keep.create (fun pats -> body)`.
        member val KeepBindings = ResizeArray<string * SynPat list * range>()
        /// Keeps the file feeds an `indexed` source through `Keep.list`.
        member val IndexFedKeeps = ResizeArray<string>()
        member val Applications = ResizeArray<string * range * range>()
        member val Mentions = ResizeArray<string * range>()
        member val TreeMentions = ResizeArray<range>()

        member this.AddLambdaScope (indices: string option list) (body: SynExpr) (kept: bool) =
            let names = indices |> List.choose id |> Set.ofList

            if not names.IsEmpty then
                this.Scopes.Add {
                    Indices = names
                    Body = body.Range
                    Kept = kept
                }

        override this.WalkBinding(_, binding) =
            let (SynBinding(headPat = pat; expr = rhs)) = binding

            match nameOf pat, strip rhs with
            | Some name, SynExpr.App(funcExpr = f; argExpr = arg) when isKeepCall "create" f ->
                match lambdaOf arg with
                | Some(pats, body) -> this.KeepBindings.Add(name, pats, body.Range)
                | None -> ()
            | _ -> ()

        override this.WalkExpr(_, expr) =
            match expr with
            | SynExpr.Ident id -> this.Mentions.Add(id.idText, expr.Range)
            | SynExpr.LongIdent(longDotId = SynLongIdent(id = first :: _)) when treeQualifiers.Contains first.idText ->
                this.TreeMentions.Add expr.Range
            | _ -> ()

            match expr with
            | SynExpr.App(funcExpr = f; argExpr = arg) ->
                // A message: an upper-case head applied to an argument.
                match lastName f with
                | Some name when System.Char.IsUpper name[0] && not (notMessages.Contains name) ->
                    this.Applications.Add(name, expr.Range, arg.Range)
                | _ -> ()

                // `List.mapi (fun i x -> …)` / `Keep.create (fun … -> …)`.
                match lastName f, lambdaOf arg with
                | Some name, Some(pats, body) when indexedFunctions.Contains name ->
                    this.AddLambdaScope [ pats |> List.tryHead |> Option.bind nameOf ] body false
                | _, Some(pats, body) when isKeepCall "create" f ->
                    let typed =
                        pats
                        |> List.collect (fun p -> tupleOf p |> Option.defaultValue [ p ])
                        |> List.filter isIntTyped
                        |> List.map nameOf

                    this.AddLambdaScope typed body true
                | _ -> ()

                // `List.map (fun (i, x) -> …) (List.indexed xs)` and
                // `Keep.list keep (List.indexed xs)`.
                match strip f with
                | SynExpr.App(funcExpr = g; argExpr = lam) when isIndexed arg ->
                    match lastName g, lambdaOf lam with
                    | Some name, Some(pats, body) when mapFamily.Contains name ->
                        this.AddLambdaScope [ firstOfTuple pats ] body false
                    | _ when isKeepCall "list" g -> lastName lam |> Option.iter this.IndexFedKeeps.Add
                    | _ -> ()
                | _ -> ()

                // The piped forms of both.
                match expr with
                | Piped(lhs, rhs) when isIndexed lhs ->
                    match strip rhs with
                    | SynExpr.App(funcExpr = g; argExpr = lam) ->
                        match lastName g, lambdaOf lam with
                        | Some name, Some(pats, body) when mapFamily.Contains name ->
                            this.AddLambdaScope [ firstOfTuple pats ] body false
                        | _ when isKeepCall "list" g -> lastName lam |> Option.iter this.IndexFedKeeps.Add
                        | _ -> ()
                    | _ -> ()
                | _ -> ()
            | SynExpr.ForEach(pat = pat; enumExpr = source; bodyExpr = body) ->
                match strip source with
                | SynExpr.IndexRange _ -> this.AddLambdaScope [ nameOf pat ] body false
                | s when isIndexed s -> this.AddLambdaScope [ firstOfTuple [ pat ] ] body false
                | _ -> ()
            | _ -> ()

    let private within (outer: range) (inner: range) = Range.rangeContainsRange outer inner

    /// Every TUA004 finding in one parsed file, in source order.
    let analyze (input: ParsedInput) : ContainmentRules.Finding list =
        let c = Collector()
        ASTCollecting.walkAst c input

        let fed = Set.ofSeq c.IndexFedKeeps

        let keptByFeed =
            c.KeepBindings
            |> Seq.filter (fun (name, _, _) -> fed.Contains name)
            |> Seq.choose (fun (_, pats, body) ->
                firstOfTuple pats
                |> Option.map (fun i -> {
                    Indices = Set.singleton i
                    Body = body
                    Kept = true
                }))

        let scopes =
            Seq.append c.Scopes keptByFeed
            |> Seq.filter (fun s -> s.Kept || c.TreeMentions |> Seq.exists (within s.Body))
            |> List.ofSeq

        let sites =
            c.Applications
            |> Seq.filter (fun (_, app, arg) ->
                scopes
                |> List.exists (fun s ->
                    within s.Body app
                    && c.Mentions
                       |> Seq.exists (fun (name, at) -> s.Indices.Contains name && within arg at)))
            |> Seq.map (fun (_, app, _) -> app)
            |> Seq.distinct
            |> List.ofSeq

        // The outermost message is the site: `Select (Some i)` is one finding.
        sites
        |> List.filter (fun r -> not (sites |> List.exists (fun o -> o <> r && within o r)))
        |> List.sortBy (fun r -> r.StartLine, r.StartColumn)
        |> List.map (fun r -> {
            ContainmentRules.Finding.Code = Code
            ContainmentRules.Finding.Range = r
        })

    let toMessage (finding: ContainmentRules.Finding) : Message = {
        Type = "ToolUp.Analyzers.KeptListIndex"
        Message = Rules.message finding.Code
        Code = finding.Code
        Severity = Severity.Warning
        Range = finding.Range
        Fixes = []
    }
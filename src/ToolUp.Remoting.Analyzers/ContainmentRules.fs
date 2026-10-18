namespace ToolUp.Remoting.Analyzers

// =============================================================================
// Phase 776 — TUA001–TUA003, the containment rules
// =============================================================================
//
// Three syntactic rules, each the compile-time half of a runtime check (the
// metadata, rationale and sanctioned forms live in `Rules.fs`):
//
//   * TUA001 — container resolution (`GetService` / `GetRequiredService` /
//     `GetServices` / the keyed forms / `RequestServices`) outside service
//     registration. Sanctioned by shape: an occurrence inside the argument of
//     a registration call (`AddSingleton` / `AddScoped` / `AddTransient` /
//     `AddKeyed*` / `TryAdd*` / `AddHostedService`), or inside a binding that
//     itself implements `GetService` (a provider delegating to its inner one).
//   * TUA002 — an `HttpClient` constructed without the platform egress
//     handler. Sanctioned by shape: a construction whose arguments name
//     `PlatformHttpClient`.
//   * TUA003 — `Async.RunSynchronously` outside a program entry point.
//     Sanctioned by shape: an occurrence inside an `[<EntryPoint>]` binding.
//
// The untyped tree cannot see types, so each rule matches NAMES. That is the
// trade the Phase 195 analyser already made: no project options, so it runs
// in the editor, on the CLI and over a whole tree in seconds. A name-match
// can over-report (a user type with a `GetService` method of its own), which
// is why the in-repo gate carries a reasoned inventory rather than treating
// every finding as unanswerable.

module ContainmentRules =

    open FSharp.Compiler.Syntax
    open FSharp.Compiler.Text
    open FSharp.Analyzers.SDK

    type Finding = { Code: string; Range: range }

    let private resolutionNames =
        set [
            "GetService"
            "GetRequiredService"
            "GetServices"
            "GetKeyedService"
            "GetRequiredKeyedService"
            "GetKeyedServices"
            "RequestServices"
        ]

    let private providerImplNames =
        set [
            "GetService"
            "GetRequiredService"
            "GetKeyedService"
            "GetRequiredKeyedService"
        ]

    let private registrationPrefixes = [
        "AddSingleton"
        "AddScoped"
        "AddTransient"
        "AddKeyed"
        "TryAdd"
        "AddHostedService"
    ]

    let private isRegistrationCall (name: string) =
        registrationPrefixes
        |> List.exists (fun p -> name.StartsWith(p, System.StringComparison.Ordinal))

    let private idsOf (expr: SynExpr) : Ident list =
        match expr with
        | SynExpr.Ident id -> [ id ]
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> ids
        | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) -> ids
        | _ -> []

    /// The last name a function expression applies — `services.AddSingleton<T>`
    /// yields `AddSingleton` through the type application.
    let rec private appliedName (expr: SynExpr) : string option =
        match expr with
        | SynExpr.TypeApp(expr = inner) -> appliedName inner
        | _ ->
            match idsOf expr with
            | [] -> None
            | ids -> Some (List.last ids).idText

    let private isAsyncRunSynchronously (ids: Ident list) =
        ids
        |> List.pairwise
        |> List.exists (fun (a, b) -> a.idText = "Async" && b.idText = "RunSynchronously")

    let private isHttpClientType (ids: Ident list) =
        match ids with
        | [] -> false
        | _ -> (List.last ids).idText = "HttpClient"

    let private attributeNames (attrs: SynAttributes) =
        attrs
        |> List.collect _.Attributes
        |> List.choose (fun a ->
            match a.TypeName with
            | SynLongIdent(id = []) -> None
            | SynLongIdent(id = ids) -> Some (List.last ids).idText)

    let private bindingName (pat: SynPat) =
        match pat with
        | SynPat.LongIdent(longDotId = SynLongIdent(id = ids)) when not ids.IsEmpty -> Some (List.last ids).idText
        | SynPat.Named(ident = SynIdent(id, _)) -> Some id.idText
        | _ -> None

    type private Collector() =
        inherit ASTCollecting.SyntaxCollectorBase()

        member val Resolutions = ResizeArray<range>()
        member val HttpClientConstructions = ResizeArray<range * range>()
        member val RunSynchronously = ResizeArray<range>()
        member val PlatformHttpClientMentions = ResizeArray<range>()
        member val RegistrationArguments = ResizeArray<range>()
        member val ProviderImplementations = ResizeArray<range>()
        member val EntryPoints = ResizeArray<range>()

        override this.WalkBinding(_, binding) =
            let (SynBinding(attributes = attrs; headPat = pat)) = binding
            let range = binding.RangeOfBindingWithRhs

            match bindingName pat with
            | Some name when providerImplNames.Contains name -> this.ProviderImplementations.Add range
            | _ -> ()

            let names = attributeNames attrs

            if names |> List.exists (fun n -> n = "EntryPoint" || n = "EntryPointAttribute") then
                this.EntryPoints.Add range

        override this.WalkExpr(_, expr) =
            let ids = idsOf expr

            if ids |> List.exists (fun i -> resolutionNames.Contains i.idText) then
                this.Resolutions.Add expr.Range

            if isAsyncRunSynchronously ids then
                this.RunSynchronously.Add expr.Range

            if ids |> List.exists (fun i -> i.idText = "PlatformHttpClient") then
                this.PlatformHttpClientMentions.Add expr.Range

            match expr with
            | SynExpr.New(targetType = SynType.LongIdent(SynLongIdent(id = typeIds)); expr = args; range = r) when
                isHttpClientType typeIds
                ->
                this.HttpClientConstructions.Add(r, args.Range)
            | SynExpr.App(funcExpr = f; argExpr = args; range = r) ->
                match appliedName f with
                | Some name when isRegistrationCall name -> this.RegistrationArguments.Add args.Range
                | Some "HttpClient" when isHttpClientType (idsOf f) -> this.HttpClientConstructions.Add(r, args.Range)
                | _ -> ()
            | _ -> ()

    let private within (outers: ResizeArray<range>) (inner: range) =
        outers |> Seq.exists (fun outer -> Range.rangeContainsRange outer inner)

    /// Every TUA finding in one parsed file, in source order.
    let analyze (input: ParsedInput) : Finding list =
        let c = Collector()
        ASTCollecting.walkAst c input

        let tua001 =
            c.Resolutions
            |> Seq.filter (fun r -> not (within c.RegistrationArguments r || within c.ProviderImplementations r))
            |> Seq.map (fun r -> { Code = "TUA001"; Range = r })

        let tua002 =
            c.HttpClientConstructions
            |> Seq.filter (fun (_, args) ->
                not (c.PlatformHttpClientMentions |> Seq.exists (Range.rangeContainsRange args)))
            |> Seq.map (fun (r, _) -> { Code = "TUA002"; Range = r })

        let tua003 =
            c.RunSynchronously
            |> Seq.filter (fun r -> not (within c.EntryPoints r))
            |> Seq.map (fun r -> { Code = "TUA003"; Range = r })

        Seq.concat [ tua001; tua002; tua003 ]
        |> Seq.sortBy (fun f -> f.Range.StartLine, f.Range.StartColumn, f.Code)
        |> List.ofSeq

    let toMessage (finding: Finding) : Message = {
        Type = "ToolUp.Analyzers.Containment"
        Message = Rules.message finding.Code
        Code = finding.Code
        Severity = Severity.Warning
        Range = finding.Range
        Fixes = []
    }
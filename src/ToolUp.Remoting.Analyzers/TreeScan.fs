namespace ToolUp.Remoting.Analyzers

// =============================================================================
// Phase 776 — the whole-tree run, and the inventory it is reconciled against
// =============================================================================
//
// The `Analyzers` leg of `VerifyAll` calls `verify`: parse every tracked
// `.fs` file under `src/` (parse-only, no project options), run the TUA
// rules, and reconcile what was found with the committed inventory
// (`src/ToolUp.Remoting.Analyzers/analyzer-inventory.json`). Then compare the
// committed rule document with the render of `Rules.all`.
//
// The inventory has two parts:
//
//   * `exclude` — paths out of scope, each with a reason. A `prefix` entry
//     matches a path that starts with it; a `segmentSuffix` entry matches a
//     path with any directory segment ending in it (every `*.Tests` pack,
//     wherever it sits). An exclusion that matches no tracked file is a
//     finding: a scope that quietly stopped meaning anything is exactly what a
//     gate must not carry.
//   * `suppressions` — groups of known sites. Each group names one rule, a
//     one-line reason, and a per-file count. The count must EQUAL what the
//     tree holds: more is a new site, fewer is an entry the tree no longer
//     needs. Both are findings, so the inventory moves only deliberately.
//
// `reconcile` is pure, so the test pack exercises it without a filesystem.

module TreeScan =

    open System
    open System.IO
    open System.Text.Json

    [<Literal>]
    let InventoryPath = "src/ToolUp.Remoting.Analyzers/analyzer-inventory.json"

    [<Literal>]
    let RulesDocPath = "docs/platform/analyzer-rules.md"

    type Exclusion =
        | Prefix of string
        | SegmentSuffix of string

    type ScopeEntry = { Exclusion: Exclusion; Reason: string }

    type SuppressionGroup = {
        Rule: string
        Reason: string
        Sites: Map<string, int>
    }

    type Inventory = {
        Exclude: ScopeEntry list
        Suppressions: SuppressionGroup list
    }

    let private normalise (path: string) = path.Replace('\\', '/')

    let isExcludedBy (exclusion: Exclusion) (path: string) =
        let path = normalise path

        match exclusion with
        | Prefix p -> path.StartsWith(p, StringComparison.Ordinal)
        | SegmentSuffix s ->
            let segments = path.Split('/')
            // Directory segments only: the last segment is the file name.
            segments
            |> Array.take (max 0 (segments.Length - 1))
            |> Array.exists (fun seg -> seg.EndsWith(s, StringComparison.Ordinal))

    let isExcluded (inventory: Inventory) (path: string) =
        inventory.Exclude |> List.exists (fun e -> isExcludedBy e.Exclusion path)

    /// Parse the inventory. Every structural problem is an `Error` naming it,
    /// because an inventory the gate half-understood is a gate half-applied.
    let parseInventory (json: string) : Result<Inventory, string list> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let errors = ResizeArray<string>()

            let str (e: JsonElement) (name: string) (where: string) =
                match e.TryGetProperty name with
                | true, v when
                    v.ValueKind = JsonValueKind.String
                    && not (String.IsNullOrWhiteSpace(v.GetString()))
                    ->
                    Some(v.GetString())
                | _ ->
                    errors.Add(sprintf "%s: missing or empty `%s`" where name)
                    None

            let array (e: JsonElement) (name: string) =
                match e.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
                | _ ->
                    errors.Add(sprintf "inventory: missing array `%s`" name)
                    []

            let exclude =
                array root "exclude"
                |> List.mapi (fun i e ->
                    let where = sprintf "exclude[%d]" i
                    let reason = str e "reason" where

                    let exclusion =
                        match e.TryGetProperty "prefix", e.TryGetProperty "segmentSuffix" with
                        | (true, p), (false, _) when p.ValueKind = JsonValueKind.String -> Some(Prefix(p.GetString()))
                        | (false, _), (true, s) when s.ValueKind = JsonValueKind.String ->
                            Some(SegmentSuffix(s.GetString()))
                        | _ ->
                            errors.Add(sprintf "%s: exactly one of `prefix` / `segmentSuffix` is required" where)
                            None

                    match exclusion, reason with
                    | Some x, Some r -> Some { Exclusion = x; Reason = r }
                    | _ -> None)
                |> List.choose id

            let known = Rules.all |> List.map _.Id |> Set.ofList

            let suppressions =
                array root "suppressions"
                |> List.mapi (fun i e ->
                    let where = sprintf "suppressions[%d]" i
                    let rule = str e "rule" where
                    let reason = str e "reason" where

                    rule
                    |> Option.iter (fun r ->
                        if not (known.Contains r) then
                            errors.Add(sprintf "%s: unknown rule `%s`" where r))

                    let sites =
                        match e.TryGetProperty "sites" with
                        | true, v when v.ValueKind = JsonValueKind.Object ->
                            v.EnumerateObject()
                            |> Seq.choose (fun p ->
                                match p.Value.ValueKind with
                                | JsonValueKind.Number when p.Value.GetInt32() > 0 ->
                                    Some(normalise p.Name, p.Value.GetInt32())
                                | _ ->
                                    errors.Add(sprintf "%s: site `%s` needs a positive count" where p.Name)
                                    None)
                            |> List.ofSeq
                        | _ ->
                            errors.Add(sprintf "%s: missing object `sites`" where)
                            []

                    match rule, reason with
                    | Some r, Some why -> Some(r, why, sites)
                    | _ -> None)
                |> List.choose id

            // One (rule, file) pair lives in exactly one group: two groups
            // would be two reasons for one count, and nothing could say which
            // the count belongs to.
            suppressions
            |> List.collect (fun (rule, _, sites) -> sites |> List.map (fun (path, _) -> rule, path))
            |> List.countBy id
            |> List.filter (fun (_, n) -> n > 1)
            |> List.iter (fun ((rule, path), _) ->
                errors.Add(sprintf "%s %s is listed in more than one group" rule path))

            if errors.Count > 0 then
                Error(List.ofSeq errors)
            else
                Ok {
                    Exclude = exclude
                    Suppressions =
                        suppressions
                        |> List.map (fun (rule, why, sites) -> {
                            Rule = rule
                            Reason = why
                            Sites = Map.ofList sites
                        })
                }
        with :? JsonException as ex ->
            Error [ sprintf "inventory is not valid JSON: %s" ex.Message ]

    /// Compare the sites found with the inventory. `tracked` is every path the
    /// scan was offered, before exclusion; `observed` maps (rule, path) to the
    /// line numbers of each site in the scanned (non-excluded) files.
    let reconcile
        (inventory: Inventory)
        (tracked: string list)
        (observed: Map<string * string, int list>)
        : string list =
        let findings = ResizeArray<string>()

        for e in inventory.Exclude do
            if not (tracked |> List.exists (isExcludedBy e.Exclusion)) then
                let shown =
                    match e.Exclusion with
                    | Prefix p -> sprintf "prefix `%s`" p
                    | SegmentSuffix s -> sprintf "segmentSuffix `%s`" s

                findings.Add(sprintf "stale exclusion: %s matches no tracked file — remove it from the inventory" shown)

        let allowed =
            inventory.Suppressions
            |> List.collect (fun g -> g.Sites |> Map.toList |> List.map (fun (path, n) -> (g.Rule, path), n))
            |> Map.ofList

        for KeyValue((rule, path), _) in allowed do
            if isExcluded inventory path then
                findings.Add(
                    sprintf
                        "%s %s: the inventory suppresses a site in a path its own scope excludes — remove the suppression"
                        rule
                        path
                )

        let keys =
            Set.union (allowed |> Map.keys |> Set.ofSeq) (observed |> Map.keys |> Set.ofSeq)

        for (rule, path) in keys do
            let lines = observed |> Map.tryFind (rule, path) |> Option.defaultValue []
            let found = List.length lines
            let permitted = allowed |> Map.tryFind (rule, path) |> Option.defaultValue 0

            if found > permitted then
                findings.Add(
                    sprintf
                        "%s %s: %d site(s) at line(s) %s, the inventory accounts for %d. %s"
                        rule
                        path
                        found
                        (lines |> List.map string |> String.concat ", ")
                        permitted
                        (Rules.message rule)
                )
            elif found < permitted && not (isExcluded inventory path) then
                findings.Add(
                    sprintf
                        "%s %s: the inventory accounts for %d site(s), the tree holds %d — tighten the inventory to %d"
                        rule
                        path
                        permitted
                        found
                        found
                )

        List.ofSeq findings

    // ── The impure half: enumerate, parse, compare ───────────────────────

    let private checker = lazy (FSharp.Compiler.CodeAnalysis.FSharpChecker.Create())

    /// Parse one source and return its TUA findings as (rule, line), or the
    /// parse diagnostics when the file could not be read cleanly — a file the
    /// analyser cannot parse has not been shown to be clean.
    let scanSource (path: string) (source: string) : Result<(string * int) list, string> =
        let options = {
            FSharp.Compiler.CodeAnalysis.FSharpParsingOptions.Default with
                SourceFiles = [| path |]
        }

        let result =
            checker.Value.ParseFile(path, FSharp.Compiler.Text.SourceText.ofString source, options)
            |> Async.RunSynchronously

        if result.ParseHadErrors then
            let first =
                result.Diagnostics
                |> Array.tryHead
                |> Option.map (fun d -> sprintf "line %d: %s" d.StartLine d.Message)
                |> Option.defaultValue "no diagnostic text"

            Error first
        else
            ContainmentRules.analyze result.ParseTree
            |> List.map (fun f -> f.Code, f.Range.StartLine)
            |> Ok

    let private trackedSources (root: string) : Result<string list, string> =
        let psi =
            Diagnostics.ProcessStartInfo("git", "ls-files -- src", WorkingDirectory = root)

        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false

        use p = Diagnostics.Process.Start psi
        let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        p.WaitForExit()

        if p.ExitCode <> 0 then
            Error(sprintf "git ls-files exited %d: %s" p.ExitCode (error.Trim()))
        else
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.map normalise
            |> Array.filter (fun f -> f.EndsWith(".fs", StringComparison.Ordinal))
            |> List.ofArray
            |> Ok

    let private lf (s: string) = s.Replace("\r\n", "\n")

    /// The leg: returns the findings, empty when the tree is clean.
    let verify (root: string) : string list =
        let findings = ResizeArray<string>()

        let docPath = Path.Combine(root, RulesDocPath)

        if not (File.Exists docPath) then
            findings.Add(sprintf "%s is missing — regenerate it with --emit-rules-doc" RulesDocPath)
        elif lf (File.ReadAllText docPath) <> Rules.renderDocument () then
            findings.Add(
                sprintf
                    "%s differs from the render of Rules.fs — regenerate it with --emit-rules-doc rather than editing it"
                    RulesDocPath
            )

        match parseInventory (File.ReadAllText(Path.Combine(root, InventoryPath))), trackedSources root with
        | Error errors, _ -> findings.AddRange(errors |> List.map (sprintf "%s: %s" InventoryPath))
        | _, Error e -> findings.Add e
        | Ok inventory, Ok tracked ->
            let inScope = tracked |> List.filter (isExcluded inventory >> not)
            let observed = Collections.Generic.Dictionary<string * string, ResizeArray<int>>()

            for path in inScope do
                match scanSource path (File.ReadAllText(Path.Combine(root, path))) with
                | Error e -> findings.Add(sprintf "%s: could not be parsed (%s)" path e)
                | Ok sites ->
                    for (rule, line) in sites do
                        match observed.TryGetValue((rule, path)) with
                        | true, lines -> lines.Add line
                        | _ -> observed[(rule, path)] <- ResizeArray [ line ]

            let observedMap =
                observed |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq

            printfn
                "Analyzers: %d tracked .fs file(s) under src/, %d in scope, %d site(s) found, %d accounted for by the inventory."
                tracked.Length
                inScope.Length
                (observedMap |> Map.toSeq |> Seq.sumBy (snd >> List.length))
                (inventory.Suppressions
                 |> List.sumBy (fun g -> g.Sites |> Map.toSeq |> Seq.sumBy snd))

            findings.AddRange(reconcile inventory tracked observedMap)

        List.ofSeq findings
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.AGE

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Npgsql
open ToolUp.Graph

// ─── Phase 68c — Cypher→AGE SQL wrapping + agtype mapping (pure) ──────────────
//
// The seam between the portable `IGraphStore` value model and Apache AGE's SQL
// surface. Every function here is pure (no connection, no I/O), so the always-on
// unit pack in `AgeConformance.fs` exercises it on a fresh checkout with no live
// Postgres — the full `IGraphStoreContract` binding is env-gated behind a live
// AGE-enabled server, but the wrapping / mapping / injection-binding logic is
// covered unconditionally. This is where the real fresh-checkout coverage lives.
//
// AGE runs a Cypher query through a SQL function:
//
//     SELECT c0, c1
//     FROM   cypher('<graph>', $ct$ <cypher> $ct$, @p)
//            AS (c0 agtype, c1 agtype)
//
//   • The graph name is a derived `[a-z0-9_]` identifier (never raw scope text)
//     embedded as a SQL literal — injection-safe by construction.
//   • The Cypher text is dollar-quoted with the `$ct$` tag. It is developer-
//     authored (`CypherQuery.Text`), never untrusted parameter data.
//   • Parameters ride a SINGLE bound Npgsql parameter `@p` — an agtype map the
//     Cypher body reads via `$name`. Parameter VALUES are never string-
//     interpolated into the `$ct$…$ct$` body, so an injection-attempt value
//     (`'; DROP …`) is a literal map entry, never executed (task 68c.A).
//
//     `@p` is passed BARE — no `::agtype` cast. AGE parses the `cypher()` call
//     itself and requires its third argument to be a plain `Param` node; a cast
//     wraps the parameter in a `CoerceViaIO` and AGE rejects the call outright
//     with `22023: third argument of cypher function must be a parameter`. The
//     store binds it as `NpgsqlDbType.Unknown` so the value goes out untyped and
//     Postgres resolves it to `agtype` from the `cypher()` signature.
//   • The projected columns are selected UNCAST, and the store reads them with
//     `AllResultTypesAreUnknown` so plain Npgsql hands back each agtype cell's
//     text form (no agtype OID handling needed at the driver) — the robustness
//     choice that lets this ship over the already-pinned Npgsql.
//
//     They are deliberately NOT re-selected `::text`. `agtype::text` routes
//     through `agtype_value_to_text`, which handles only SCALARS: a vertex or
//     edge cell fails with `agtype_value_to_text: unsupported argument agtype`,
//     and a string scalar comes back UNQUOTED (`carol`, not `"carol"`) — which
//     is not parseable JSON, so `agtypeToGraphValue` would fold it to `VNull`.
//     The uncast form yields exactly the annotated text the mapping half below
//     is written against (`{…}::vertex`, `"carol"`). Both defects were latent
//     until Phase 607 first ran this companion's conformance arm against a live
//     AGE server; the pure pack's fixtures had always pinned the uncast shape.
//
// Storage shape mirrors the Neo4j companion (68b): a reserved `_id` carries the
// substrate `NodeId`/`EdgeId` (value identity — never AGE's internal graphid,
// rule 1); `_from`/`_to` carry an edge's endpoints for value-model round-trip;
// `_scope` is stamped under property-partition. AGE vertices are single-label:
// a `GraphNode` is stored under its sole label (or the `_v` sentinel when it has
// none / more than one — see the README "Divergences").

/// Raised by `CypherToAgeSql.injectScopeGuard` when a `Query` in
/// property-partition mode contains a node pattern the guard cannot safely
/// scope. Fail-closed: refusing is safer than running unscoped against a shared
/// multi-tenant graph. Compose `GraphPerTenant` isolation (the default) to run
/// arbitrary Cypher verbatim.
type AgeScopeGuardException(cypher: string) =
    inherit
        Exception(
            sprintf
                "property-partition scope guard could not safely scope a node pattern in this query; refusing rather than running it unscoped. Query: %s. Use GraphPerTenant isolation for full arbitrary-Cypher support."
                cypher
        )

    member _.Cypher = cypher

// Not `[<RequireQualifiedAccess>]`: the store + shared-transaction seam `open`
// this module for the reserved-key literals + the wrapping/mapping helpers, so
// the identical statement-building is shared without re-qualifying every call.
module CypherToAgeSql =

    /// Reserved property key carrying the substrate `NodeId` / `EdgeId`.
    [<Literal>]
    let IdKey = "_id"

    /// Reserved property key carrying the tenant scope (property-partition mode).
    [<Literal>]
    let ScopeKey = "_scope"

    /// Reserved property keys carrying an edge's endpoint ids.
    [<Literal>]
    let FromKey = "_from"

    [<Literal>]
    let ToKey = "_to"

    /// Cypher parameter name for the scope value (property-partition guard).
    [<Literal>]
    let ScopeParam = "__scope"

    /// The AGE label a substrate node with no single label is stored under.
    [<Literal>]
    let DefaultLabel = "_v"

    /// The dollar-quote tag wrapping the Cypher body in the generated SQL.
    [<Literal>]
    let DollarTag = "ct"

    let private reserved = set [ IdKey; ScopeKey; FromKey; ToKey ]

    // ── AGE identifier escaping ──────────────────────────────────────

    /// Double-quote-escape an AGE label used inside a Cypher pattern (a label is
    /// not parameterisable, so it is embedded as an escaped literal). AGE labels
    /// are ordinary identifiers; a backtick-quoted Cypher label doubles internal
    /// backticks.
    let escapeLabel (label: string) : string = "`" + label.Replace("`", "``") + "`"

    /// The single AGE label for a substrate node: its sole label, or the `_v`
    /// sentinel when the node has none or more than one (AGE vertices are
    /// single-label). See the README "Divergences".
    let ageLabelOf (labels: Set<string>) : string =
        match Set.toList labels with
        | [ single ] -> single
        | _ -> DefaultLabel

    // ── PropertyValue ⇄ agtype JSON ──────────────────────────────────

    /// Escape a string for embedding in an agtype/JSON string literal.
    let private jsonString (s: string) : string =
        // System.Text.Json produces a spec-correct, injection-safe JSON string
        // literal (quotes, backslashes, control chars all escaped).
        JsonSerializer.Serialize s

    /// Render a `PropertyValue` as its agtype JSON literal. Precision floor
    /// (rule 6): `PInt` → a bare integer, `PFloat` → a JSON number; no widening.
    /// `PDateTime` → an ISO-8601 string (AGE has no native temporal agtype, so a
    /// timestamp round-trips as a string — see the README).
    let propertyValueJson (v: PropertyValue) : string =
        match v with
        | PString s -> jsonString s
        | PInt i -> string i
        | PFloat f -> JsonSerializer.Serialize f
        | PBool b -> if b then "true" else "false"
        | PDateTime d -> jsonString (d.ToString("o", System.Globalization.CultureInfo.InvariantCulture))

    /// A Cypher parameter argument. A scalar (`$id`, `$from`) or a nested map
    /// (`$props`, for a `SET n = $props` that replaces the whole property set).
    type AgtypeArg =
        | AScalar of PropertyValue
        | AMap of Map<string, PropertyValue>

    /// Lift a flat scalar parameter map (e.g. a `CypherQuery.Parameters`) to the
    /// arg model.
    let ofScalars (parameters: Map<string, PropertyValue>) : Map<string, AgtypeArg> =
        parameters |> Map.map (fun _ v -> AScalar v)

    let private mapJson (m: Map<string, PropertyValue>) : string =
        let sb = StringBuilder()
        sb.Append '{' |> ignore

        m
        |> Map.toList
        |> List.iteri (fun i (k, v) ->
            if i > 0 then
                sb.Append ',' |> ignore

            sb.Append(jsonString k).Append(':').Append(propertyValueJson v) |> ignore)

        sb.Append '}' |> ignore
        sb.ToString()

    /// Serialise a Cypher parameter map to the agtype map literal passed as the
    /// single bound `@p` parameter. Keys are JSON-escaped; scalar values go
    /// through `propertyValueJson`, nested maps through `mapJson`. This is the
    /// safe parameter channel — an injection string lands here as an escaped
    /// JSON value, never as SQL.
    let paramsJson (parameters: Map<string, AgtypeArg>) : string =
        let sb = StringBuilder()
        sb.Append '{' |> ignore

        parameters
        |> Map.toList
        |> List.iteri (fun i (k, arg) ->
            if i > 0 then
                sb.Append ',' |> ignore

            let valJson =
                match arg with
                | AScalar v -> propertyValueJson v
                | AMap m -> mapJson m

            sb.Append(jsonString k).Append(':').Append(valJson) |> ignore)

        sb.Append '}' |> ignore
        sb.ToString()

    // ── agtype text → GraphValue ─────────────────────────────────────

    // AGE returns each cell as agtype text: a JSON value optionally carrying a
    // trailing `::vertex` / `::edge` / `::path` / `::numeric` annotation. We
    // strip those annotation tokens, then parse the remainder as JSON.
    let private annotationRegex =
        Regex(@"::(vertex|edge|path|numeric|graphid)\b", RegexOptions.Compiled)

    /// Strip AGE's agtype annotation suffixes, leaving parseable JSON.
    let stripAnnotations (agtype: string) : string = annotationRegex.Replace(agtype, "")

    /// A JSON scalar element back to a `PropertyValue` (precision floor: integers
    /// at `int64`, reals at `float`). `None` for null / container.
    let propertyValueOfJson (el: JsonElement) : PropertyValue option =
        match el.ValueKind with
        | JsonValueKind.String -> Some(PString(el.GetString()))
        | JsonValueKind.True -> Some(PBool true)
        | JsonValueKind.False -> Some(PBool false)
        | JsonValueKind.Number ->
            match el.TryGetInt64() with
            | true, i -> Some(PInt i)
            | _ -> Some(PFloat(el.GetDouble()))
        | _ -> None

    let private propertiesOf (props: JsonElement) : Map<string, PropertyValue> =
        if props.ValueKind <> JsonValueKind.Object then
            Map.empty
        else
            props.EnumerateObject()
            |> Seq.choose (fun p ->
                if reserved.Contains p.Name then
                    None
                else
                    propertyValueOfJson p.Value |> Option.map (fun pv -> p.Name, pv))
            |> Map.ofSeq

    /// The reserved `_id` string of a vertex/edge's `properties`, or "" when a
    /// node the substrate did not write (arbitrary `CREATE`) carries none.
    let private reservedString (props: JsonElement) (key: string) : string =
        if props.ValueKind = JsonValueKind.Object then
            match props.TryGetProperty key with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
        else
            ""

    /// Map an AGE vertex JSON object (`{"id":…,"label":…,"properties":{…}}`) to a
    /// `GraphNode`. Identity from the reserved `_id`; the single AGE label
    /// becomes the label set (empty for the `_v` sentinel); reserved keys are
    /// stripped from properties.
    let nodeOfVertex (vertex: JsonElement) : GraphNode =
        let props =
            match vertex.TryGetProperty "properties" with
            | true, p -> p
            | _ -> vertex

        let label =
            match vertex.TryGetProperty "label" with
            | true, l when l.ValueKind = JsonValueKind.String -> l.GetString()
            | _ -> DefaultLabel

        {
            Id = NodeId(reservedString props IdKey)
            Labels =
                (if label = DefaultLabel then
                     Set.empty
                 else
                     Set.singleton label)
            Properties = propertiesOf props
        }

    /// Map an AGE edge JSON object to a `GraphEdge` (endpoints from the reserved
    /// `_from` / `_to` carried on the edge's properties, so it round-trips
    /// without a second fetch).
    let edgeOfEdge (edge: JsonElement) : GraphEdge =
        let props =
            match edge.TryGetProperty "properties" with
            | true, p -> p
            | _ -> edge

        let label =
            match edge.TryGetProperty "label" with
            | true, l when l.ValueKind = JsonValueKind.String -> l.GetString()
            | _ -> ""

        {
            Id = EdgeId(reservedString props IdKey)
            Label = label
            From = NodeId(reservedString props FromKey)
            To = NodeId(reservedString props ToKey)
            Properties = propertiesOf props
        }

    /// Classify a parsed agtype JSON element as a vertex, an edge, or neither.
    /// AGE vertices carry `id` + `label` + `properties`; edges additionally
    /// carry `start_id` + `end_id`.
    let private classifyObject (el: JsonElement) : GraphValue option =
        let has (k: string) =
            match el.TryGetProperty k with
            | true, _ -> true
            | _ -> false

        if has "label" && has "properties" && has "id" then
            if has "start_id" && has "end_id" then
                Some(VEdge(edgeOfEdge el))
            else
                Some(VNode(nodeOfVertex el))
        else
            None

    /// Map one parsed agtype JSON element to a `GraphValue`: a whole vertex, a
    /// whole edge, a list, a scalar, or null.
    let rec graphValueOfJson (el: JsonElement) : GraphValue =
        match el.ValueKind with
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> VNull
        | JsonValueKind.Object ->
            match classifyObject el with
            | Some gv -> gv
            | None -> VNull // a bare map projection is not part of the subset floor
        | JsonValueKind.Array -> VList(el.EnumerateArray() |> Seq.map graphValueOfJson |> List.ofSeq)
        | _ ->
            match propertyValueOfJson el with
            | Some pv -> VProperty pv
            | None -> VNull

    /// Map a single agtype text cell (as read uncast from Npgsql under
    /// `AllResultTypesAreUnknown`) to a
    /// `GraphValue`. A blank / null cell is `VNull`.
    let agtypeToGraphValue (cell: string) : GraphValue =
        if String.IsNullOrWhiteSpace cell then
            VNull
        else
            let json = stripAnnotations cell

            try
                use doc = JsonDocument.Parse json
                graphValueOfJson doc.RootElement
            with _ ->
                VNull

    /// Read an agtype boolean cell (e.g. an `IS NOT NULL` projection). Anything
    /// that is not an explicit agtype `true` reads as `false`.
    let agtypeBool (cell: string) : bool =
        match agtypeToGraphValue cell with
        | VProperty(PBool b) -> b
        | _ -> false

    // ── RETURN-projection column names ───────────────────────────────

    /// Split a comma-separated list at *top-level* commas only (ignoring commas
    /// nested in parens / brackets / braces / string literals).
    let private splitTopLevel (s: string) : string list =
        let parts = ResizeArray<string>()
        let sb = StringBuilder()
        let mutable depth = 0
        let mutable inStr = false
        let mutable strCh = ' '
        let mutable i = 0

        while i < s.Length do
            let c = s.[i]

            if inStr then
                sb.Append c |> ignore

                if c = strCh then
                    inStr <- false
            else
                match c with
                | '\''
                | '"' ->
                    inStr <- true
                    strCh <- c
                    sb.Append c |> ignore
                | '('
                | '['
                | '{' ->
                    depth <- depth + 1
                    sb.Append c |> ignore
                | ')'
                | ']'
                | '}' ->
                    depth <- depth - 1
                    sb.Append c |> ignore
                | ',' when depth = 0 ->
                    parts.Add(sb.ToString())
                    sb.Clear() |> ignore
                | _ -> sb.Append c |> ignore

            i <- i + 1

        parts.Add(sb.ToString())
        parts |> List.ofSeq

    // The projection runs from the LAST top-level `RETURN` to the first trailing
    // `ORDER BY` / `SKIP` / `LIMIT` (all case-insensitive).
    let private returnKeyword = Regex(@"\breturn\b", RegexOptions.IgnoreCase)

    let private tailKeyword =
        Regex(@"\b(?:order\s+by|skip|limit)\b", RegexOptions.IgnoreCase)

    let private asAliasRegex =
        Regex(@"\s+as\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)\s*$", RegexOptions.IgnoreCase)

    /// The output column names of a Cypher query, derived from its RETURN
    /// projection: an explicit `AS alias` wins, else the item's expression text
    /// verbatim (`n`, `n.name`) — matching openCypher's own column naming, which
    /// is what the `GraphResultSet.Columns` contract and the subset-floor corpus
    /// assert. Returns `[]` when there is no RETURN (a pure write).
    let returnColumns (cypher: string) : string list =
        let returns = returnKeyword.Matches cypher

        if returns.Count = 0 then
            []
        else
            let last = returns.[returns.Count - 1]
            let afterReturn = cypher.Substring(last.Index + last.Length)
            let tail = tailKeyword.Match afterReturn

            let projText =
                if tail.Success then
                    afterReturn.Substring(0, tail.Index)
                else
                    afterReturn

            let proj = projText.Trim()

            if proj = "" || proj = "*" then
                []
            else
                splitTopLevel proj
                |> List.map (fun item ->
                    let item = item.Trim()
                    let asMatch = asAliasRegex.Match item

                    if asMatch.Success then
                        asMatch.Groups.["alias"].Value
                    else
                        item)

    // ── cypher() SQL wrapping ────────────────────────────────────────

    /// Build the AGE SQL wrapping a Cypher query. `graphName` is a validated
    /// `[a-z0-9_]` identifier (see `AgeGraph.nameFor`). `columns` is the derived
    /// RETURN-column list; when empty the query is a write and a single sentinel
    /// column is projected (AGE requires a non-empty column definition list).
    /// `hasParams` decides whether the bare `@p` parameter argument is passed
    /// (bare, never cast — see the header note on AGE's `Param`-node check).
    ///
    /// Fail-closed on a dollar-quote collision: if the Cypher body contains the
    /// `$ct$` tag it could break out of the dollar quote, so the wrap is
    /// **refused** (`Error`) rather than emitted — the body is developer text,
    /// so this is a robustness guard, not the injection boundary (that is the
    /// bound `@p` parameter).
    let wrapQuery
        (graphName: string)
        (cypher: string)
        (hasParams: bool)
        (columns: string list)
        : Result<string * string list, string> =
        let tag = sprintf "$%s$" DollarTag

        if cypher.Contains tag then
            Error(sprintf "Cypher body contains the reserved dollar-quote tag %s; refusing to wrap it." tag)
        else
            // Synthetic SQL aliases c0..cN; the real Cypher column names ride in
            // the returned list. A write (no RETURN) projects one sentinel col.
            let effective = if List.isEmpty columns then [ "_w" ] else columns
            let n = List.length effective

            let asList = [ 0 .. n - 1 ] |> List.map (sprintf "c%d agtype") |> String.concat ", "

            let selList = [ 0 .. n - 1 ] |> List.map (sprintf "c%d") |> String.concat ", "

            let paramArg = if hasParams then ", @p" else ""

            let sql =
                sprintf
                    "SELECT %s FROM cypher('%s', %s %s %s%s) AS (%s)"
                    selList
                    graphName
                    tag
                    cypher
                    tag
                    paramArg
                    asList

            Ok(sql, effective)

    // ── Property-partition scope guard (mirrors the 68b Neo4j guard) ──

    let private nodePatternRegex =
        Regex(@"(^|[^\w`])\(([^()]*)\)", RegexOptions.Compiled)

    /// Inject a `_scope` constraint into every node pattern of `cypher`
    /// (property-partition mode). Fail-closed: a pattern the guard cannot
    /// confidently rewrite raises `AgeScopeGuardException` rather than run
    /// unscoped. In `GraphPerTenant` mode this is never called — the graph is
    /// the boundary.
    let injectScopeGuard (cypher: string) : string =
        nodePatternRegex.Replace(
            cypher,
            fun (m: Match) ->
                let lead = m.Groups.[1].Value
                let inner = m.Groups.[2].Value.Trim()
                let scopeClause = sprintf "%s: $%s" ScopeKey ScopeParam

                if inner.Contains "{" then
                    if inner.LastIndexOf '}' < inner.IndexOf '{' then
                        raise (AgeScopeGuardException cypher)

                    let braceIdx = inner.IndexOf '{'
                    let head = inner.Substring(0, braceIdx + 1)
                    let tail = inner.Substring(braceIdx + 1)
                    sprintf "%s(%s%s, %s)" lead head scopeClause tail
                elif inner = "" then
                    sprintf "%s({%s})" lead scopeClause
                else
                    sprintf "%s(%s {%s})" lead inner scopeClause
        )

    // ── Transient-fault classification (GP 12 rule 3) ────────────────

    /// A `PostgresException.SqlState` that names a retryable transient condition:
    /// connection exceptions (class 08), transaction rollback / serialization /
    /// deadlock (class 40), insufficient resources (class 53), and operator-
    /// intervention shutdowns (57P0x). These fold to `TransientFailure` — the
    /// retryable value the caller loops on, never a thrown live exception.
    let isTransientSqlState (sqlState: string) : bool =
        not (String.IsNullOrEmpty sqlState)
        && (sqlState.StartsWith "08"
            || sqlState.StartsWith "40"
            || sqlState.StartsWith "53"
            || sqlState.StartsWith "57P")

    /// A `SqlState` naming a developer-facing query defect (syntax / undefined
    /// object / datatype mismatch — class 42, plus AGE's own graph-not-found).
    /// These fold to `MalformedQuery`, distinct from a transient.
    let isMalformedSqlState (sqlState: string) : bool =
        not (String.IsNullOrEmpty sqlState) && sqlState.StartsWith "42"

    /// Classify an Npgsql exception into the `IGraphStore` error channel.
    /// Retryable Postgres transients become `GraphError.TransientFailure`; a
    /// class-42 syntax/undefined error becomes `MalformedQuery`; a connection-
    /// level `NpgsqlException.IsTransient` also folds to `TransientFailure`;
    /// everything else maps to `StorageFailure`.
    ///
    /// The store reaches Npgsql through `Async.AwaitTask`, which surfaces a
    /// faulted task as an `AggregateException`, so the `PostgresException`
    /// arrives WRAPPED. Unwrapping first is load-bearing rather than defensive:
    /// without it every match below falls through to `StorageFailure`, and the
    /// whole `SqlState` classification — including the retryable/non-retryable
    /// split GP 12 rule 3 requires callers to act on — silently never fires.
    /// (Latent until Phase 607 first ran this arm against a live AGE server: a
    /// malformed query returned `StorageFailure`, not `MalformedQuery`.)
    let rec classifyError (ex: exn) : GraphError =
        match ex with
        | :? AggregateException as agg when not (isNull agg.InnerException) ->
            classifyError (agg.Flatten().InnerException)
        | :? PostgresException as px when isTransientSqlState px.SqlState -> TransientFailure px.Message
        | :? PostgresException as px when isMalformedSqlState px.SqlState -> MalformedQuery px.Message
        | :? PostgresException as px -> StorageFailure px.Message
        | :? NpgsqlException as nx when nx.IsTransient -> TransientFailure nx.Message
        | :? NpgsqlException as nx -> StorageFailure nx.Message
        | _ -> StorageFailure ex.Message
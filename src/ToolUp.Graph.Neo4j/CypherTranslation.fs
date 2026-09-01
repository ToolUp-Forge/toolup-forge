// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.Neo4j

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Neo4j.Driver
open ToolUp.Graph

// ─── Phase 68b — Cypher translation + value mapping (pure, unit-testable) ─────
//
// The seam between the portable `IGraphStore` value model (`GraphNode` /
// `GraphEdge` / `PropertyValue` / `GraphResultSet`) and the Neo4j wire. Every
// function here is pure (no driver session, no I/O), so the always-on unit pack
// in `Neo4jConformance.fs` exercises the translation on a fresh checkout with
// no live server — the full `IGraphStoreContract` binding is env-gated behind a
// live Neo4j, but the mapping logic is covered unconditionally.
//
// Storage shape. A `GraphNode` is a Neo4j node whose labels are the node's
// `Labels`, plus a reserved `_id` property carrying the substrate's `NodeId`
// (our value-identity — never Neo4j's internal element id, which is not stable
// across the substrate's value contract, rule 1). A `GraphEdge` is a Neo4j
// relationship whose type is the edge `Label`, plus a reserved `_id` carrying
// the `EdgeId`. In `PropertyPartition` mode a reserved `_scope` property is
// stamped on both, so a single shared database still isolates tenants.

/// Raised by `CypherTranslation.injectScopeGuard` when a `Query` in
/// property-partition mode contains a node pattern the guard cannot safely
/// scope. Fail-closed: refusing the query is safer than running it unscoped
/// against a shared multi-tenant database. Compose `MultiDatabase` isolation
/// (the default) to run arbitrary Cypher verbatim.
type CypherScopeGuardException(cypher: string) =
    inherit
        System.Exception(
            sprintf
                "property-partition scope guard could not safely scope a node pattern in this query; refusing rather than running it unscoped. Query: %s. Use MultiDatabase isolation for full arbitrary-Cypher support."
                cypher
        )

    member _.Cypher = cypher

[<RequireQualifiedAccess>]
module CypherTranslation =

    /// Reserved property key carrying the substrate `NodeId` / `EdgeId`.
    [<Literal>]
    let IdKey = "_id"

    /// Reserved property key carrying the tenant scope (property-partition mode).
    [<Literal>]
    let ScopeKey = "_scope"

    /// Cypher parameter name for the scope value (property-partition guard).
    [<Literal>]
    let ScopeParam = "__scope"

    let private reserved = set [ IdKey; ScopeKey ]

    // ── PropertyValue ⇄ Neo4j value ──────────────────────────────────

    /// A `PropertyValue` as the CLR object the driver serialises to a Bolt
    /// value. Precision floor (rule 6): `PInt` → `int64`, `PFloat` → `float`;
    /// no widening. `PDateTime` → `System.DateTime`, which the driver encodes as
    /// a Neo4j temporal.
    let toNeo4j (v: PropertyValue) : obj =
        match v with
        | PString s -> box s
        | PInt i -> box i
        | PFloat f -> box f
        | PBool b -> box b
        | PDateTime d -> box d

    /// A Neo4j scalar back to a `PropertyValue`. Neo4j returns `Int64` for
    /// integers, `Double` for reals, plus its own temporal types
    /// (`LocalDateTime` / `ZonedDateTime`), each of which carries a `DateTime`
    /// projection. Returns `None` for a container / null (handled by the
    /// `GraphValue` mapper).
    let ofNeo4jScalar (o: obj) : PropertyValue option =
        match o with
        | null -> None
        | :? string as s -> Some(PString s)
        | :? bool as b -> Some(PBool b)
        | :? int64 as i -> Some(PInt i)
        | :? int as i -> Some(PInt(int64 i))
        | :? float as f -> Some(PFloat f)
        | :? single as f -> Some(PFloat(float f))
        | :? DateTime as d -> Some(PDateTime d)
        | :? LocalDateTime as ldt -> Some(PDateTime(ldt.ToDateTime()))
        | :? ZonedDateTime as zdt -> Some(PDateTime zdt.UtcDateTime)
        | _ -> None

    /// Reconstruct the property map of a node/edge, dropping the reserved keys.
    let private propertiesOf (props: IReadOnlyDictionary<string, obj>) : Map<string, PropertyValue> =
        props
        |> Seq.choose (fun kv ->
            if reserved.Contains kv.Key then
                None
            else
                ofNeo4jScalar kv.Value |> Option.map (fun pv -> kv.Key, pv))
        |> Map.ofSeq

    /// The reserved `_id` of an entity, or its Neo4j element id as a last
    /// resort (a node the substrate did not write — e.g. arbitrary `CREATE`).
    let private identityOf (e: IEntity) : string =
        match e.Properties.TryGetValue IdKey with
        | true, (:? string as s) -> s
        | _ -> e.ElementId

    /// Map a Neo4j `INode` to a `GraphNode` (identity from `_id`, labels
    /// verbatim, reserved keys stripped from properties).
    let nodeOf (n: INode) : GraphNode = {
        Id = NodeId(identityOf (n :> IEntity))
        Labels = n.Labels |> Set.ofSeq
        Properties = propertiesOf n.Properties
    }

    /// Map a Neo4j `IRelationship` to a `GraphEdge`. The endpoints are the
    /// reserved `_id`s carried on the relationship (`_from` / `_to`), so the
    /// edge round-trips through the value model without a second node fetch.
    let edgeOf (r: IRelationship) : GraphEdge =
        let endpoint (key: string) =
            match r.Properties.TryGetValue key with
            | true, (:? string as s) -> s
            | _ -> ""

        {
            Id = EdgeId(identityOf (r :> IEntity))
            Label = r.Type
            From = NodeId(endpoint "_from")
            To = NodeId(endpoint "_to")
            Properties = propertiesOf r.Properties
        }

    /// Map an arbitrary Bolt value (a `RETURN` cell) to a `GraphValue`: a whole
    /// node, a whole relationship, a list, a scalar, or null.
    let rec graphValueOf (o: obj) : GraphValue =
        match o with
        | null -> VNull
        | :? INode as n -> VNode(nodeOf n)
        | :? IRelationship as r -> VEdge(edgeOf r)
        | :? IReadOnlyList<obj> as xs -> VList(xs |> Seq.map graphValueOf |> List.ofSeq)
        | :? System.Collections.IEnumerable as xs when not (o :? string) -> VList([ for x in xs -> graphValueOf x ])
        | scalar ->
            match ofNeo4jScalar scalar with
            | Some pv -> VProperty pv
            | None -> VNull

    // ── Property-map builders for the structured operations ──────────

    /// The stored property dictionary for a node: user properties + `_id`
    /// (+ `_scope` in property-partition mode). Passed as the `$props` map to a
    /// `SET n = $props` that replaces the whole property set.
    let nodePropertyMap (scope: string option) (node: GraphNode) : Dictionary<string, obj> =
        let d = Dictionary<string, obj>()

        for kv in node.Properties do
            d.[kv.Key] <- toNeo4j kv.Value

        d.[IdKey] <- box (NodeId.value node.Id)
        scope |> Option.iter (fun s -> d.[ScopeKey] <- box s)
        d

    /// The stored property dictionary for an edge: user properties + `_id` +
    /// the endpoint ids (`_from` / `_to`) + `_scope` in partition mode.
    let edgePropertyMap (scope: string option) (edge: GraphEdge) : Dictionary<string, obj> =
        let d = Dictionary<string, obj>()

        for kv in edge.Properties do
            d.[kv.Key] <- toNeo4j kv.Value

        d.[IdKey] <- box (EdgeId.value edge.Id)
        d.["_from"] <- box (NodeId.value edge.From)
        d.["_to"] <- box (NodeId.value edge.To)
        scope |> Option.iter (fun s -> d.[ScopeKey] <- box s)
        d

    // ── Label / identifier escaping ──────────────────────────────────

    /// Backtick-escape a Cypher label / relationship-type identifier (labels
    /// cannot be parameterised in a pattern, so they are embedded as escaped
    /// literals — internal backticks are doubled).
    let escapeIdent (ident: string) : string = "`" + ident.Replace("`", "``") + "`"

    /// The `:L1:L2` label suffix for a node's label set (empty when no labels).
    let labelSuffix (labels: Set<string>) : string =
        labels |> Seq.map (fun l -> ":" + escapeIdent l) |> String.concat ""

    // ── Property-partition scope guard for arbitrary Query Cypher ────

    // A node pattern is `(` … `)` whose `(` is NOT immediately preceded by an
    // identifier char (that would be a function call, e.g. `count(n)`), and
    // whose interior contains no nested parens. We inject `_scope: $__scope`
    // into each such pattern's inline property map (merging with any existing
    // map). Relationship patterns live in `[...]`, so they are untouched.
    let private nodePatternRegex =
        Regex(@"(^|[^\w`])\(([^()]*)\)", RegexOptions.Compiled)

    /// Inject a `_scope` constraint into every node pattern of `cypher`
    /// (property-partition mode). Fail-closed: a pattern the guard cannot
    /// confidently rewrite raises `CypherScopeGuardException` rather than run
    /// unscoped, so a single shared database never leaks across tenants. In
    /// `MultiDatabase` mode this is never called — isolation is the database.
    let injectScopeGuard (cypher: string) : string =
        nodePatternRegex.Replace(
            cypher,
            fun (m: Match) ->
                let lead = m.Groups.[1].Value
                let inner = m.Groups.[2].Value.Trim()
                let scopeClause = sprintf "%s: $%s" ScopeKey ScopeParam

                if inner.Contains "{" then
                    // Existing inline property map — merge into it. Guard against
                    // a brace the simple splitter cannot handle (a `{` inside a
                    // string literal): fail closed.
                    if inner.LastIndexOf '}' < inner.IndexOf '{' then
                        raise (CypherScopeGuardException cypher)

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

    /// True for a Neo4j status code in the `Neo.ClientError.Statement.*`
    /// family — the query AS SUBMITTED is wrong (`SyntaxError`,
    /// `ParameterMissing`, `TypeError`, `ArgumentError`, `SemanticError`).
    ///
    /// The whole family, deliberately, not one member of it: this is the Bolt
    /// analogue of the AGE binding's Postgres class-`42` test, and matching a
    /// single code is what made a missing parameter — the exact case the
    /// conformance pack asserts — read as a `StorageFailure`. Security, schema
    /// and transaction client-errors are NOT malformed queries and stay out.
    let isMalformedStatementCode (code: string) : bool =
        not (String.IsNullOrEmpty code)
        && code.StartsWith("Neo.ClientError.Statement.", StringComparison.Ordinal)

    /// Classify a driver exception into the `IGraphStore` error channel.
    /// Retryable faults (a `Neo4jException` marked `IsRetriable` — connection
    /// blips, causal-cluster leader re-election, deadlocks) become
    /// `GraphError.TransientFailure` (retryable *data* the caller loops on),
    /// never a thrown live exception across the async boundary. A
    /// `ClientException` in the `Neo.ClientError.Statement.*` family is a
    /// `MalformedQuery`. Everything else maps to `StorageFailure`.
    ///
    /// **The `AggregateException` unwrap is load-bearing (Phase 752).** The
    /// store reaches the driver through `Async.AwaitTask`, which surfaces a
    /// faulted task as an `AggregateException`, so before this the driver's
    /// exception arrived WRAPPED and every arm below fell through to
    /// `StorageFailure` — the entire classification, including the
    /// retryable/non-retryable split GP 12 rule 3 requires callers to act on,
    /// was silently dead. Phase 607 found and fixed exactly this in the AGE
    /// binding and predicted the twin here; the first live Neo4j run
    /// (Phase 752) confirmed it.
    let rec classifyError (ex: exn) : GraphError =
        match ex with
        | :? AggregateException as agg when not (isNull agg.InnerException) ->
            classifyError (agg.Flatten().InnerException)
        | :? Neo4jException as nx when nx.IsRetriable -> TransientFailure(nx.Message)
        | :? ServiceUnavailableException as sx -> TransientFailure(sx.Message)
        | :? SessionExpiredException as sx -> TransientFailure(sx.Message)
        | :? ClientException as cx when isMalformedStatementCode cx.Code -> MalformedQuery(cx.Message)
        | :? Neo4jException as nx -> StorageFailure(nx.Message)
        | _ -> StorageFailure(ex.Message)
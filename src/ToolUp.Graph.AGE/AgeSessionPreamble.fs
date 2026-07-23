// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.AGE

open System
open System.Security.Cryptography
open System.Text
open Npgsql

// ─── Phase 68c — session preamble + tenant graph derivation ───────────────────
//
// Apache AGE lives inside an ordinary PostgreSQL database. Two things must be
// true on every connection that runs a `cypher(...)` call:
//
//   1. the `age` extension library is loaded (`LOAD 'age';`), and
//   2. `ag_catalog` is on the `search_path`, so the `agtype` operators and the
//      `cypher` function resolve unqualified.
//
// This preamble is applied **per connection on open, never snapshotted once**
// (task 68c.D, the workspace build-once/read-per-call lens). An
// `NpgsqlDataSource` is a host-lifetime connection pool; the preamble is run as
// the first statements each time the store borrows a connection, so a pooled
// physical connection that has been recycled still carries the preamble, and no
// stale session state can leak across a borrow. `LOAD` + `SET search_path` are
// both idempotent, so re-applying on every borrow is cheap and always correct.
//
// Tenant context is likewise NOT snapshotted: which AGE graph a call targets is
// derived from the *current* `scopeId` on every call (`AgeGraph.nameFor`), never
// baked onto the data source at construction — so a scope change between two
// calls is reflected on the second.

/// How a `scopeId` maps onto AGE's isolation primitives.
type TenantIsolation =
    /// **Graph-per-tenant** (default). AGE namespaces graphs — each graph is its
    /// own schema under `ag_catalog` — so each `scopeId` targets its own AGE
    /// graph, derived deterministically from the scope via `AgeGraph.nameFor`.
    /// Cross-tenant reads are structurally impossible: a `cypher('<graphA>', …)`
    /// call cannot name graph B's subgraph, so even arbitrary `Query` Cypher
    /// runs **verbatim** and stays isolated. This is the recommended mode.
    | GraphPerTenant of graphPrefix: string

    /// **Property-partition fallback** — a single shared AGE graph. Every node
    /// and edge carries a reserved `_scope` property; structured operations
    /// constrain on it, and arbitrary `Query` Cypher has a scope guard injected
    /// into its node patterns (`CypherToAgeSql.injectScopeGuard`). A query whose
    /// patterns the guard cannot safely rewrite is **refused (fail-closed)**
    /// rather than run unscoped. Use when a single named graph is preferred
    /// (one schema, one backup unit); prefer `GraphPerTenant` otherwise.
    | PropertyPartition of graph: string

/// Connection + AGE configuration for the companion. All values default
/// (`AgeGraphStoreConfig.defaults`); a deployment tunes only what it needs.
type AgeGraphStoreConfig = {
    /// Tenant-isolation strategy. Default `GraphPerTenant "tenant"`.
    Isolation: TenantIsolation
    /// Per-statement command timeout (seconds). Default 30. Applied to every
    /// command the store issues; a slow traversal surfaces as a transient.
    CommandTimeoutSeconds: int
}

[<RequireQualifiedAccess>]
module AgeGraphStoreConfig =
    /// Graph-per-tenant isolation (prefix `tenant`), 30s command timeout.
    let defaults: AgeGraphStoreConfig = {
        Isolation = GraphPerTenant "tenant"
        CommandTimeoutSeconds = 30
    }

    /// Property-partition isolation over a single named graph (default `graph`).
    let propertyPartition (graph: string) : AgeGraphStoreConfig = {
        defaults with
            Isolation = PropertyPartition graph
    }

[<RequireQualifiedAccess>]
module AgeGraph =
    /// Short, lowercase, collision-resistant hash suffix for a raw scope id, so
    /// two distinct scopes that sanitise to the same alphanumeric body still map
    /// to distinct AGE graphs.
    let private shortHash (scopeId: string) : string =
        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes scopeId)
        (bytes |> Array.truncate 6 |> Array.map (sprintf "%02x") |> String.concat "")

    /// Derive a **valid AGE graph name** for a scope. An AGE graph name becomes
    /// a PostgreSQL schema, so it must be a valid identifier: begin with a
    /// lowercase letter and contain only `[a-z0-9_]`, capped at 63 bytes
    /// (Postgres's `NAMEDATALEN` limit). We lowercase, keep `[a-z0-9]` from the
    /// prefix + scope, join `prefix_body_hash` with underscores, and cap at 63 —
    /// deterministic, always-valid, and collision-resistant. Because the name is
    /// derived (never the raw scope string) it is also injection-safe: the graph
    /// name is embedded in the `cypher('<name>', …)` SQL literal, and only
    /// `[a-z0-9_]` can reach it.
    let nameFor (config: AgeGraphStoreConfig) (scopeId: string) : string =
        match config.Isolation with
        | PropertyPartition graph -> graph
        | GraphPerTenant prefix ->
            let cleanPrefix =
                let p = (prefix |> String.filter Char.IsLetterOrDigit).ToLowerInvariant()
                if p.Length > 0 && Char.IsLetter p.[0] then p else "tenant"

            let body = (scopeId |> String.filter Char.IsLetterOrDigit).ToLowerInvariant()
            let hash = shortHash scopeId
            let full = sprintf "%s_%s_%s" cleanPrefix body hash
            if full.Length <= 63 then full else full.Substring(0, 63)

[<RequireQualifiedAccess>]
module AgeSessionPreamble =
    /// The per-connection preamble: load the AGE library and put `ag_catalog`
    /// first on the search path so `cypher(…)` and the `agtype` operators
    /// resolve. `"$user"` + `public` are preserved so the connection still
    /// behaves normally for any relational statement the same deployment runs
    /// (the shared-transaction seam depends on that).
    [<Literal>]
    let Sql = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;"

    /// Apply the preamble to an already-open connection. Idempotent — safe to
    /// run on every borrow (that is exactly how the store uses it: applied
    /// per-connection on open, never snapshotted).
    let applyAsync (conn: NpgsqlConnection) : Async<unit> = async {
        use cmd = new NpgsqlCommand(Sql, conn)
        let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
        ()
    }
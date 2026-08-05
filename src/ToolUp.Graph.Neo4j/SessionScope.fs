// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.Neo4j

open System
open System.Security.Cryptography
open System.Text
open Neo4j.Driver

// ─── Phase 68b — per-call session scope (the build-once/read-per-call shape) ──
//
// The single most important shape in this companion (task 68b.A, the
// build-once/read-per-call lens): the `IDriver` is expensive
// and is a **host-lifetime singleton**, but tenant context — which database a
// call targets, or which impersonated user it runs as — is applied **per
// session, opened fresh on each `IGraphStore` call from the current `scopeId`**.
// It is NEVER snapshotted at driver-construction time. A driver-construction-
// time snapshot of tenant context would ship the stale scope on every later
// call (the classic "build-once, read-never" defect); `SessionScope.openSession`
// takes `scopeId` as a call-time argument precisely so that a scope change
// between two `Query` calls is reflected on the second call.

/// How a `scopeId` maps onto Neo4j's isolation primitives.
type TenantIsolation =
    /// **Database-per-tenant** — the clean isolation Neo4j 4.0+ (Enterprise /
    /// AuraDB) supports. Each `scopeId` targets its own Neo4j database, derived
    /// deterministically from the scope via `SessionScope.databaseName`. Cross-
    /// tenant reads are structurally impossible: a session bound to tenant A's
    /// database cannot name tenant B's subgraph, so even arbitrary `Query`
    /// Cypher runs **verbatim** and stays isolated. This is the default and the
    /// recommended mode. Minimum server: Neo4j **4.0** (multi-database);
    /// creating a database is an Enterprise/Aura operation (Community edition
    /// hosts a single `neo4j` database — use `PropertyPartition` there).
    | MultiDatabase of databasePrefix: string

    /// **Property-partition fallback** for a single shared database (e.g. Neo4j
    /// Community, which hosts one database). Every node and relationship carries
    /// a reserved `_scope` property; structured operations constrain on it, and
    /// arbitrary `Query` Cypher has a scope guard injected into its node
    /// patterns (`CypherTranslation.injectScopeGuard`). Less clean than
    /// multi-database — a query whose patterns the guard cannot safely rewrite
    /// is **refused (fail-closed)** rather than run unscoped — so prefer
    /// `MultiDatabase` on any server that supports it.
    | PropertyPartition of database: string

/// Connection-lifecycle + pool configuration for the Neo4j companion. All
/// values have sane defaults (`Neo4jGraphStoreConfig.defaults`); a deployment
/// tunes only what it needs.
type Neo4jGraphStoreConfig = {
    /// Tenant-isolation strategy. Default `MultiDatabase "tenant"`.
    Isolation: TenantIsolation
    /// Upper bound on pooled connections (driver `WithMaxConnectionPoolSize`).
    /// Default 100 — the driver default; raise for high write concurrency.
    MaxConnectionPoolSize: int
    /// How long `AsyncSession`/query acquisition waits for a pooled connection
    /// before failing (driver `WithConnectionAcquisitionTimeout`). Default 60s.
    ConnectionAcquisitionTimeout: TimeSpan
    /// Ceiling on the driver's own managed-transaction retry window. The
    /// companion surfaces exhausted transients as `GraphError.TransientFailure`
    /// (retryable data — GP 12 rule 3); this bounds how long the driver retries
    /// internally before that surfaces. Default 30s (the driver default).
    MaxTransactionRetryTime: TimeSpan
}

[<RequireQualifiedAccess>]
module Neo4jGraphStoreConfig =
    /// Multi-database isolation (`tenant<scope>`), driver-default pool sizing.
    let defaults: Neo4jGraphStoreConfig = {
        Isolation = MultiDatabase "tenant"
        MaxConnectionPoolSize = 100
        ConnectionAcquisitionTimeout = TimeSpan.FromSeconds 60.0
        MaxTransactionRetryTime = TimeSpan.FromSeconds 30.0
    }

    /// Property-partition isolation over a single named database (default
    /// `neo4j`) — the Community-edition / single-database posture.
    let propertyPartition (database: string) : Neo4jGraphStoreConfig = {
        defaults with
            Isolation = PropertyPartition database
    }

[<RequireQualifiedAccess>]
module SessionScope =
    /// Short, lowercase, collision-resistant hash suffix for a raw scope id, so
    /// two distinct scopes that sanitise to the same alphanumeric prefix still
    /// map to distinct databases.
    let private shortHash (scopeId: string) : string =
        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes scopeId)

        (bytes |> Array.truncate 6 |> Array.map (sprintf "%02x") |> String.concat "")

    /// Derive a **valid Neo4j database name** for a scope. Neo4j 5.x database
    /// names are case-insensitive, 3–63 chars, must begin with an ASCII letter,
    /// and may contain only ASCII alphanumerics, dot and dash (NOT underscore).
    /// We lowercase, keep `[a-z0-9]` from the scope, join the configured prefix
    /// + sanitised body + a short hash of the raw scope with dashes, and cap at
    /// 63 chars — deterministic, always-valid, and collision-resistant.
    let databaseName (prefix: string) (scopeId: string) : string =
        let cleanPrefix =
            let p = (prefix |> String.filter Char.IsLetterOrDigit).ToLowerInvariant()

            if p.Length > 0 && Char.IsLetter p.[0] then p else "tenant"

        let body = (scopeId |> String.filter Char.IsLetterOrDigit).ToLowerInvariant()

        let hash = shortHash scopeId
        // prefix starts with a letter (guaranteed above); dashes + a-z0-9 only.
        let full = sprintf "%s-%s-%s" cleanPrefix body hash
        if full.Length <= 63 then full else full.Substring(0, 63)

    /// Open a **fresh** `IAsyncSession` for `scopeId`, applying the tenant
    /// context (database selection) at call time. This is the build-once/
    /// read-per-call-safe seam: the `driver` was built once (host-lifetime),
    /// but the scope is read **now**, on this call, so a scope change between
    /// two calls is honoured. `access` steers causal-cluster routing (reads to
    /// followers, writes to the leader).
    let openSession
        (driver: IDriver)
        (config: Neo4jGraphStoreConfig)
        (scopeId: string)
        (access: AccessMode)
        : IAsyncSession =
        let database =
            match config.Isolation with
            | MultiDatabase prefix -> databaseName prefix scopeId
            | PropertyPartition db -> db

        driver.AsyncSession(fun (cb: SessionConfigBuilder) ->
            cb.WithDatabase(database).WithDefaultAccessMode(access) |> ignore)
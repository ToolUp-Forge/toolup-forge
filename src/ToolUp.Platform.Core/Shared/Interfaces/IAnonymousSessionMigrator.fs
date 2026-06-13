// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── IAnonymousSessionMigrator — Phase 66 Stream C.1 (substrate slice) ─
//
// Optional hook invoked on the first authenticated request that follows
// an anonymous session in the same browser. The migrator has access to
// both the anonymous (`session-{sid}`) and the now-authenticated
// (`user-{uid}` / `team-{tid}`) scopes via injected substrate, and
// copies whatever data it owns into the user's persistent scope.
//
// Deployments without a migration story leave the registration off and
// `compose` registers `NoOpAnonymousSessionMigrator` as the default —
// every Migrate call returns `Error (NotEligible "no migrator wired")`
// so the trigger-detection path can update `LastSeenAnonymousSessionId`
// uniformly without a special-case.
//
// **Scoped slice — Phase 66 C.1 substrate only.** The interface, the
// summary / error types, and the no-op default ship here. Deferred
// (lands in a later C.1 continuation):
//   * Middleware integration on the first authenticated request.
//   * `LastSeenAnonymousSessionId` field on the user record.
//   * `SemaphoreSlim`-per-userId double-migration guard.
//   * Audit-log emission on `Ok` / `PartialFailure` / `InfrastructureFailed`.
//
// **Phase 9c portability rules (all six honoured):**
//
//   1. Identity by value. `anonymousSessionId` is a string; the
//      `Subject` payload is value-typed; the summary / error records
//      carry only strings / ints / lists of strings. No live handles.
//   2. Async at every boundary. `Migrate` returns `Async<_>`.
//   3. Retry / supervision as data. Failures returned as
//      `MigrationError` cases — no `OnFailure` callbacks. The caller
//      (middleware) reads the `Result` and decides whether to update
//      `LastSeenAnonymousSessionId`.
//   4. Stateless handlers between invocations. The interface does not
//      assume per-instance state across Migrate calls. The recommended
//      pattern is one short-lived migrator instance composed from
//      cohesive per-module migrators via
//      `AnonymousSessionMigrator.compose`.
//   5. No cross-shard ordering promises. Each Migrate call is
//      independent. The trigger-detection path in the middleware
//      enforces "once per session id per user record" via
//      `LastSeenAnonymousSessionId`; the migrator itself need not.
//   6. Precision N/A — not a timing primitive.

/// Result of a successful (or partially-successful) migration. Surfaced
/// to the audit log and operator-facing diagnostics so the volume of
/// migrated data is observable. `Modules` lists module names that
/// contributed items (e.g. `["Forms"; "AI"]`) so a partial-failure
/// runbook knows which module's data is in-flight.
type MigrationSummary = {
    ItemsMigrated: int
    BytesMigrated: int64
    Modules: string list
}

module MigrationSummary =
    /// Empty summary — nothing migrated. Useful as the seed for the
    /// `AnonymousSessionMigrator.compose` fold.
    let empty: MigrationSummary = {
        ItemsMigrated = 0
        BytesMigrated = 0L
        Modules = []
    }

    /// Combine two summaries — sums counts, concatenates module
    /// lists (de-duplicated to keep the operator-visible list tidy).
    let combine (a: MigrationSummary) (b: MigrationSummary) : MigrationSummary = {
        ItemsMigrated = a.ItemsMigrated + b.ItemsMigrated
        BytesMigrated = a.BytesMigrated + b.BytesMigrated
        Modules = a.Modules @ b.Modules |> List.distinct
    }

/// Reasons a migration can fail or refuse to run. Design §3.7 + OQ5.
/// `[<RequireQualifiedAccess>]` because the case names are common
/// (`NotEligible` / `PartialFailure`) and ambiguous if unqualified.
[<RequireQualifiedAccess>]
type MigrationError =
    /// The migrator declines for a non-failure reason — session id
    /// already migrated, no data found in the anonymous scope, the
    /// authenticated subject is a shape the migrator does not support
    /// (e.g. a per-user migrator receiving a `ClaimBearer`), etc. The
    /// middleware updates `LastSeenAnonymousSessionId` and moves on
    /// without retrying. `reason` is operator-visible diagnostic text.
    | NotEligible of reason: string
    /// Migration failed due to substrate infrastructure — storage
    /// unavailable, cache corruption, downstream timeout. The
    /// middleware does NOT update `LastSeenAnonymousSessionId`, so the
    /// next authenticated request from the same user retries the
    /// migration. Logged at warning level; message is operator-visible.
    | InfrastructureFailed of message: string
    /// Some items migrated, others failed. Per OQ5 resolution
    /// (option b): the partial summary is exposed so the operator's
    /// runbook can decide whether to retry, surface to the user, or
    /// abandon. `LastSeenAnonymousSessionId` updates regardless —
    /// preventing migration thrash — because the failed items are
    /// not generically retriable (item-specific failure modes need
    /// item-specific recovery, surfaced via audit-log entry +
    /// structured warning).
    | PartialFailure of partial: MigrationSummary * failedItems: int * lastError: string

/// Optional hook invoked on the first authenticated request that
/// follows an anonymous session in the same browser. Default impl
/// (`NoOpAnonymousSessionMigrator`) returns `Error NotEligible` for
/// every call.
///
/// Compose multiple per-module migrators via
/// `AnonymousSessionMigrator.compose` (Phase 66 Stream C.1 — Server
/// tier).
type IAnonymousSessionMigrator =
    /// Migrate session-scoped data into the authenticated subject's
    /// scope. Receives the anonymous session id and the resolved
    /// authenticated `Subject` (an `AuthenticatedUser` or `TeamMember` —
    /// `AnonymousSession` and `ClaimBearer` are not valid targets and
    /// implementations may return `Error (NotEligible _)` for them).
    ///
    /// **Security (Phase 135).** `anonymousSessionId` is a non-secret,
    /// client-generated value (it rides the `X-User-Id` header and the
    /// SSE `?userId=` query string). When invoked by
    /// `AnonymousSessionMigrationMiddleware`, the id has ALREADY been
    /// proven to belong to the calling browser via a server-issued,
    /// HttpOnly, DataProtection-sealed binding cookie
    /// (`AnonymousSessionBinding`) — the migration does not fire
    /// otherwise. Implementations that invoke a migrator from any OTHER
    /// path MUST perform the same ownership check first: treating a
    /// self-asserted session id as proof of ownership lets a signed-in
    /// attacker migrate a victim's anonymous-session data into their own
    /// account (horizontal data theft).
    abstract Migrate:
        anonymousSessionId: string * authenticatedSubject: Subject -> Async<Result<MigrationSummary, MigrationError>>
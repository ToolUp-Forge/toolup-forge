// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI.McpHost

open System

// ─── Phase 489 — the agent tool-grant seam ───────────────────────────
//
// The one piece of 489.B that Phase 527's service accounts do not
// already carry: which registered AI tools an agent principal may
// discover and invoke.
//
// **Why a separate store rather than a field on `ServiceAccount`.** Two
// reasons, and the second is the durable one. A required field added to
// that shipped public record breaks every full-literal construction of
// it (FS0764) — a breaking change to a released Core type for a
// companion's benefit. And the axis genuinely belongs here: a service
// account is a platform-level machine principal that may never touch an
// AI tool at all (the SCIM seam named in that phase's header is one such
// consumer), so a tool-grant field on it would be a field most accounts
// leave empty and no platform code reads.
//
// **Six portability rules (GP 12) — audited.**
//   1. *Identity by value.* `scopeId` / `accountId` are strings; the
//      grant set is a `Set<string>` of tool names. No live handle
//      crosses the surface.
//   2. *Async at every boundary.* Every member returns `Async<_>`.
//   3. *Retry / supervision as data.* Failures are
//      `AgentGrantError` cases in the error channel; nothing takes a
//      failure callback.
//   4. *Stateless between invocations.* `Read` re-reads persisted state
//      on every call. This is load-bearing rather than incidental: the
//      acceptance criterion "revocation takes effect on the next
//      request" holds for a grant exactly as it does for a credential,
//      and an implementation that cached a grant set in memory would
//      break it silently on the node that held the cache.
//   5. *No cross-shard ordering promises.* Two accounts' records are
//      independent.
//   6. *Precision at the lower bound.* `UpdatedAt` is a timestamp, not a
//      version counter; no sub-second ordering is promised.

/// Phase 489 — failure shape for `IAgentToolGrantStore`.
///
/// `[<RequireQualifiedAccess>]` because `NotFound` / `StorageFailed`
/// collide with `ServiceAccountError` and half a dozen sibling DUs.
[<RequireQualifiedAccess>]
type AgentGrantError =
    /// The caller's scope does not own the named account. A management
    /// call never reaches across scopes (GP 4).
    | ScopeMismatch
    /// The stored record could not be read as a grant set. Distinct from
    /// "no record" — an absent record is the empty grant set, which is a
    /// legitimate answer, while an unreadable one is a defect an operator
    /// must see rather than have silently read as default-deny.
    | Corrupt of detail: string
    /// Underlying storage raised. Operator-visible; never surfaced raw to
    /// a caller.
    | StorageFailed of detail: string

/// Phase 489 — where an agent principal's tool grants live.
///
/// A read never fails on absence: an account nobody has granted anything
/// returns `AgentToolGrants.empty`, which reaches nothing. The only
/// failures are the ones that mean "I could not answer", and those are
/// refusals rather than a silent fall back to the empty set — a store
/// outage must not read as a deliberate revocation, and it must not read
/// as a grant either.
type IAgentToolGrantStore =
    /// The grant set for one account. Returns the empty set for an
    /// account with no record.
    abstract Read: scopeId: string * accountId: string -> Async<Result<AgentToolGrants, AgentGrantError>>

    /// Replace one account's grant set. `updatedBy` is the authenticated
    /// Owner/Admin performing the act; the store records it (GP 6).
    ///
    /// Replace rather than add/remove deliberately: an operator reading a
    /// grants screen is reading the whole authority, and an API whose
    /// primitives are deltas makes "what is this agent allowed" a
    /// question you answer by replaying history.
    abstract Write:
        scopeId: string * accountId: string * grantedTools: Set<string> * updatedBy: string ->
            Async<Result<AgentToolGrants, AgentGrantError>>

    /// Every account in `scopeId` that has a stored grant record. The
    /// enumeration an admin surface renders; bounded by the scope, so it
    /// cannot reach another tenant's agents (GP 4).
    abstract List: scopeId: string -> Async<AgentToolGrants list>

    /// Remove one account's grant record entirely, returning it to
    /// default-deny. Idempotent — removing a record that does not exist
    /// succeeds, because the post-state is the same either way.
    abstract Remove: scopeId: string * accountId: string -> Async<Result<unit, AgentGrantError>>

/// Phase 489 — where agent grants live in blob storage. Shared so an
/// admin surface or a sweep enumerates exactly what the store writes.
[<RequireQualifiedAccess>]
module AgentGrantLayout =
    /// Blob-name prefix for the whole of what this store writes.
    [<Literal>]
    let Prefix = "mcp-agent-grants/"

    /// Every blob for one scope. The scope segment is what makes the
    /// layout structurally isolating (GP 4) — a lookup for one scope
    /// cannot construct a path under another, and the only prefix a
    /// caller can enumerate is bounded by the scope it resolved.
    let scopePrefix (scopeId: string) : string = Prefix + scopeId + "/"

    /// The grant record for one account.
    let grantBlob (scopeId: string) (accountId: string) : string =
        scopePrefix scopeId + accountId + ".json"
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI.McpHost

open System
open ToolUp.Platform

// ─── Phase 489 — agent principals + the MCP wire vocabulary ──────────
//
// An **agent principal** is not a new credential. Phase 527 already
// shipped the machine principal this companion needs — a `ServiceAccount`
// owned by a storage scope, minting salted-hashed, expiring, revocable
// bearer tokens that resolve to `Subject.ClaimBearer` through the shipped
// `ISubjectResolver`, re-read on EVERY validation so a revoke or a
// disable bites on the next request. Everything the phase asks for under
// "registered identity (name, key or token credential, owning team
// scope)" is that account, and re-founding it here would have produced a
// second credential store with a second revocation path — the shape an
// operator cannot reason about.
//
// What a service account does NOT carry is a per-TOOL grant set. Its
// `Permissions` are module-level RBAC — the right ceiling for "may this
// caller reach this module's data at all", and the wrong granularity for
// "which of the registered tools may this agent see". So the agent
// principal below is a PROJECTION: a validated `ServiceAccountPrincipal`
// plus the one thing that is genuinely new — an explicit, default-deny
// set of tool names.
//
// **Default-deny is structural, not a policy default.** `AgentToolGrants`
// has no "grant everything" case and no wildcard. An account with no
// stored record reaches nothing, because the absence of a record and an
// empty grant set are the same value — the same reasoning Phase 527
// applies to an empty permission map, arrived at from the other
// direction (there, empty reads as unrestricted and is refused; here,
// empty reads as nothing and is the floor).

/// Phase 489 — the tool names one agent principal is granted, as stored.
///
/// Keyed by `(ScopeId, AccountId)` in storage; both ride the value so a
/// read that crossed scopes is visibly wrong rather than silently usable
/// (GP 4).
type AgentToolGrants = {
    /// The `ServiceAccount.AccountId` these grants belong to.
    AccountId: string
    /// The owning storage scope. Equal to the account's `ScopeId`.
    ScopeId: string
    /// Tool names this agent may discover and invoke. Matched against
    /// `AIToolDefinition.Name` — the authored name, not the
    /// provider-sanitised one — so a grant an operator wrote survives a
    /// change to the provider naming rules.
    ///
    /// Empty means the agent reaches nothing. There is deliberately no
    /// wildcard: a grant set that can say "everything" is a grant set
    /// that silently widens the moment a module registers a new tool.
    GrantedTools: Set<string>
    /// Authenticated user who last wrote this set — grants are an
    /// Owner/Admin act and the record says whose (GP 4 / GP 6).
    UpdatedBy: string
    UpdatedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
module AgentToolGrants =
    /// The grant set an account with no stored record has: nothing.
    ///
    /// Returned by a store rather than an option, so "never granted
    /// anything" and "granted an empty set" are one value at every call
    /// site and neither can be mistaken for "unrestricted".
    let empty (scopeId: string) (accountId: string) : AgentToolGrants = {
        AccountId = accountId
        ScopeId = scopeId
        GrantedTools = Set.empty
        UpdatedBy = ""
        UpdatedAt = DateTimeOffset(DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    }

    /// Is `toolName` in this grant set?
    let grants (toolName: string) (grantSet: AgentToolGrants) : bool =
        grantSet.GrantedTools |> Set.contains toolName

/// Phase 489 — the accounting period and ceilings one agent principal's
/// MCP traffic is governed by.
///
/// Expressed over the Phase 689 budget seam rather than a budget model of
/// its own: `MaxConcurrentCalls` is that seam's `InFlight` reservation
/// count and `CallAllowance` its `Spent` accumulation, so one composed
/// `IBudgetLedger` serves this domain alongside compute and token
/// budgets, and a refusal is the shipped `BudgetDenial` a client already
/// knows how to read.
///
/// Both dimensions are independently disableable at `<= 0`, so a
/// deployment that wants only a burst cap sets `MaxConcurrentCalls` and
/// never has to invent a spend model.
type AgentBudget = {
    /// Window `CallAllowance` is measured over.
    Period: BudgetPeriod
    /// Tool calls admitted and not yet settled at one time. `<= 0` is
    /// unrestricted. The dimension an agent trips first: a fan-out is
    /// refused before it has consumed anything.
    MaxConcurrentCalls: int
    /// Tool calls admissible per period. `<= 0` is unrestricted.
    CallAllowance: decimal
}

[<RequireQualifiedAccess>]
module AgentBudget =
    /// The budget an agent with no declared ceilings has. Structurally
    /// identical to having no budget at all, which is what makes "no
    /// budget configured" and "a budget permitting everything" one code
    /// path rather than two (GP 11 / GP 13).
    let unrestricted: AgentBudget = {
        Period = BudgetPeriod.Daily
        MaxConcurrentCalls = 0
        CallAllowance = 0M
    }

    /// `true` when no dimension constrains anything — the fast path
    /// admission short-circuits on before touching a ledger.
    let isUnrestricted (budget: AgentBudget) : bool =
        budget.MaxConcurrentCalls <= 0 && budget.CallAllowance <= 0M

    /// A concurrency-only budget.
    let concurrency (maxConcurrent: int) : AgentBudget = {
        unrestricted with
            MaxConcurrentCalls = maxConcurrent
    }

    /// A per-period call allowance.
    let callsPer (period: BudgetPeriod) (calls: decimal) : AgentBudget = {
        unrestricted with
            Period = period
            CallAllowance = calls
    }

/// Phase 489 — a per-agent inbound rate limit, counted through the
/// Phase 56 `IRateLimitStore`.
///
/// Distinct from `AgentBudget` because the two answer different questions
/// and have different remedies: a budget is an allowance an operator
/// raises, a rate limit is a pace an agent backs off from. The refusal a
/// caller receives says which.
type AgentRateLimit = {
    /// Calls admitted per `Window`.
    MaxCalls: int
    /// Counting window.
    Window: RateLimitWindow
}

/// Phase 489 — everything a deployment declares about one agent's
/// resource envelope. Applied to every agent unless an account-keyed
/// override says otherwise.
type AgentPolicy = {
    Budget: AgentBudget
    /// `None` = no per-agent rate limit. Also the effective value for a
    /// deployment that composed no `IRateLimitStore`, since there is then
    /// nothing to count through.
    RateLimit: AgentRateLimit option
}

[<RequireQualifiedAccess>]
module AgentPolicy =
    /// No ceilings on any axis — the policy an unconfigured deployment
    /// has, and the one that reaches neither a ledger nor a rate-limit
    /// store.
    let unrestricted: AgentPolicy = {
        Budget = AgentBudget.unrestricted
        RateLimit = None
    }

    /// `true` when this policy constrains nothing.
    let isUnrestricted (policy: AgentPolicy) : bool =
        AgentBudget.isUnrestricted policy.Budget && policy.RateLimit.IsNone

/// Phase 489 — a resolved agent principal: a validated machine credential
/// plus the tool names it is granted.
///
/// Built per request from the `ServiceAccountPrincipal` the shipped
/// Phase 527 middleware stamped on `HttpContext.Items`, so the identity
/// half is re-read — and therefore re-checked for revocation, expiry and
/// account disablement — on every MCP call. Nothing here is cached
/// between requests (GP 12 rule 4).
type AgentPrincipal = {
    /// `ServiceAccount.AccountId`.
    AccountId: string
    /// Operator-facing account name. Attribution only, never an identity.
    DisplayName: string
    /// Owning storage scope. The GP 4 partition every audit row, budget
    /// key and grant read is bound to.
    ScopeId: string
    /// The presented token's id. The rate-limit partition, and the
    /// forensic "which credential" axis.
    TokenId: string
    /// Hard expiry of the presented token.
    ExpiresAt: DateTimeOffset
    /// Tool names this agent may discover and invoke, before the
    /// platform's own RBAC and grant-policy filters narrow it further.
    GrantedTools: Set<string>
}

/// Phase 489 — why an MCP interaction was refused, as data.
///
/// Every case maps to a JSON-RPC error object (see `McpProtocol`), and
/// every case is distinguishable by a client: an agent deciding whether
/// to narrow its plan, wait, or stop needs to tell "you may not have
/// this" from "not right now" from "your credential is finished".
///
/// `[<RequireQualifiedAccess>]` because `Unauthorized` / `Protocol` are
/// collision-prone names in a namespace this size.
[<RequireQualifiedAccess>]
type McpError =
    /// No agent credential was presented, or the one presented did not
    /// validate. Deliberately carries no detail — the endpoint must not
    /// become an oracle for which accounts or tokens exist (the posture
    /// Phase 527's error DU takes at the wire).
    | Unauthorized
    /// The named tool is not registered in this deployment at all.
    ///
    /// **Observably identical to `ToolDenied` at the wire**, and that is
    /// the point: discovery is authorisation-filtered, so an agent able
    /// to tell "no such tool" from "not yours" could enumerate the
    /// deployment's whole tool surface through `tools/call`. The cases
    /// stay distinct here so the audit trail can say which happened.
    | ToolNotFound of toolName: string
    /// The tool exists and this agent may not reach it — its grant set
    /// does not name the tool, or the caller holds no `Read` on the
    /// tool's source module, or that module's grant policy is not live.
    | ToolDenied of toolName: string * reason: string
    /// A budget ceiling refused the call. Carries the shipped
    /// `BudgetDenial`, so the client receives the dimension, the quota,
    /// the spend and the period rather than a sentence.
    | BudgetExhausted of BudgetDenial
    /// The agent's rate limit refused the call.
    | RateLimited of RateLimitedError
    /// Well-formed JSON-RPC whose parameters were not usable — a missing
    /// tool name, an `arguments` value that is not an object.
    | InvalidParams of detail: string
    /// The tool ran and raised. The message is the tool's own,
    /// control-stripped and truncated before it reaches the wire.
    | ToolFailed of toolName: string * detail: string
    /// Not valid JSON-RPC 2.0, or a method this server does not
    /// implement.
    | Protocol of detail: string

/// Phase 489 — stable constants of the MCP surface. Literals rather than
/// inlined strings because each is a contract: the protocol version is
/// negotiated with clients, the audit source is what an operator query
/// cuts on, and the budget domain namespaces a ledger row.
[<RequireQualifiedAccess>]
module McpHostConstants =
    /// The MCP protocol revision this host implements and reports from
    /// `initialize`. A client asking for a different revision is answered
    /// with this one — the specification's own negotiation rule — and is
    /// free to disconnect.
    [<Literal>]
    let ProtocolVersion = "2025-06-18"

    /// Server name reported in `initialize`'s `serverInfo`. Vendor- and
    /// deployment-neutral: it names the substrate, never the deployment.
    [<Literal>]
    let ServerName = "toolup-mcp-host"

    /// Default mount path for the streamable-HTTP endpoint.
    [<Literal>]
    let DefaultRoute = "/mcp"

    /// `ModuleEvent.SourceModule` for the MCP interaction trail. Every
    /// list, call, result and denial lands here with the agent identity
    /// attached; filtering a scope's events on this constant returns the
    /// agent trail and nothing else (GP 6).
    [<Literal>]
    let AuditSourceModule = "_platform.ai.mcp"

    /// `BudgetSubject.Domain` for per-agent MCP budgets. Namespaces the
    /// ledger key and the blob prefix, so a deployment running compute
    /// and agent budgets together can never have one read the other's
    /// consumption.
    [<Literal>]
    let BudgetDomain = "mcp-agent"

    /// Blob container for the agent grant store — the reserved platform
    /// container every other SDK store writes under.
    [<Literal>]
    let GrantContainer = "_platform"

    /// Cost one tool call reserves against `AgentBudget.CallAllowance`.
    /// One call, one unit: the allowance is a call count, not a spend
    /// estimate, because the host cannot know what a tool will cost
    /// before it runs, and a budget that mis-estimates is worse than one
    /// that counts.
    let CallCost: decimal = 1M
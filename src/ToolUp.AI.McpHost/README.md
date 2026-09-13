# ToolUp.AI.McpHost

Opt-in **Model Context Protocol server host** for ToolUp.Platform. An external LLM or agent stack connects to a deployment with its own credential, discovers exactly the tools it has been granted, and invokes them — with every interaction recorded under the agent's identity.

Companion package. A deployment that does not compose `McpServerApp.run` is byte-for-byte unchanged: no handler, no DI registration, no middleware (GP 13).

## What it is

The SDK already has a registry of AI tools — modules declare them, the built-in agent loop calls them. This package projects that registry onto an **open protocol**, so the caller no longer has to be the platform's own AI loop.

An **agent principal** is a [service account](../ToolUp.Platform.Core/Shared/ServiceAccountTypes.fs) plus a **default-deny set of tool names**:

- the credential, its expiry, its revocation and the owning scope are the service account's, re-read on every request — so revoking a token or disabling an account takes effect on the next call, on any node;
- the **tool grants** are this package's, stored per account and empty until an Owner/Admin writes them. There is no wildcard: an account with no record reaches nothing, and a module registering a new tool never widens an existing grant.

**Discovery is authorisation-filtered, not just invocation.** A tool the agent is not granted does not appear in `tools/list`, and `tools/call` refuses it with a message indistinguishable from "no such tool" — otherwise an agent could enumerate the whole tool surface one call at a time and get back exactly what filtering the listing took away.

## Wiring it

```fsharp
open ToolUp.Platform
open ToolUp.AI.AICompose
open ToolUp.AI.McpHost
open ToolUp.AI.McpHost.McpCompose

AIServerApp.create factory providerProfile
|> AIServerApp.withConfig { config with ServiceAccounts = EnabledServiceAccounts }
|> AIServerApp.withStorage blobStorage
|> AIServerApp.withAIConfig assistant
|> McpServerApp.create
|> McpServerApp.withServerVersion "1.4.0"
|> McpServerApp.withDefaultPolicy {
    Budget = AgentBudget.callsPer BudgetPeriod.Daily 500M
    RateLimit = Some { MaxCalls = 60; Window = PerMinute }
   }
|> McpServerApp.run
```

`ServiceAccounts = EnabledServiceAccounts` is a hard precondition — the host authenticates agents with service-account bearer tokens and nothing else, so a composition without it is refused at startup rather than answering `401` to every call.

## Granting tools

Grants are written through `IAgentToolGrantStore`, resolved from DI:

```fsharp
let store = services.GetRequiredService<IAgentToolGrantStore>()
let! _ = store.Write(scopeId, accountId, set [ "sales.summarise"; "sales.forecast" ], adminUserId)
```

Tool names are the **authored** `AIToolDefinition.Name`, not the provider-sanitised alias.

## The protocol

JSON-RPC 2.0 over HTTP at `POST /mcp` (configurable), protocol revision `2025-06-18`. `initialize`, `ping`, `tools/list`, `tools/call`, and notifications. Batching is not supported — that revision removed it. `GET` on the same path answers `405`: this host serves no server-initiated stream, and the streamable-HTTP transport makes that optional.

No third-party MCP SDK is taken as a dependency; the wire is implemented directly (GP 1 / GP 2).

## Budgets and rate limits

Both ride shipped substrate rather than a model of their own:

| Ceiling | Substrate | Refusal |
|---|---|---|
| concurrent calls, calls per period | `IBudgetLedger` (domain `mcp-agent`, class `agent`) | typed `BudgetDenial` on the JSON-RPC error's `data` |
| calls per window | `IRateLimitStore`, partitioned on the token | typed `RateLimitedError` + `Retry-After` |

Both **fail open** when their store is unavailable, which is the shipped posture of each: a budget that fails closed is a budget an operator switches off after the first incident. A composition that declares ceilings with no way to count them is refused at startup instead.

## Audit

Every list, call, result and denial lands on `_platform.ai.mcp` with the agent identity, the tool, its source module, the outcome and the timing — never the tool's arguments or result body, which can carry anything the calling model chose to put in them.

Denials additionally land on the `_platform.ai.tool_allowlist_denial` stream, so they surface in the `/dev/ai-allowlist` rollup and the admin panel beside in-app denials, and on the uniform `/dev/auth-denials` rollup through the platform's shipped authorization-audit hook.

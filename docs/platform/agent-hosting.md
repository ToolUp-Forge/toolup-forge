# Wiring an agent stack to a deployment

An external LLM or agent stack can drive a ToolUp deployment directly, over the **Model Context Protocol** — connecting with its own credential, discovering exactly the tools it has been granted, and invoking them, with every interaction recorded under its identity.

This page is the operator's route from "we have modules with AI tools" to "an agent stack is connected and governed". The package is `ToolUp.AI.McpHost`; a deployment that does not compose it is byte-for-byte unchanged (GP 13).

## What an agent principal is

Not a new kind of credential. An agent principal is a **service account** ([`ServiceAccountTypes.fs`](../../src/ToolUp.Platform.Core/Shared/ServiceAccountTypes.fs)) — a named, non-human identity owned by a storage scope, minting scoped, expiring, revocable bearer tokens — plus one thing service accounts do not carry: a **default-deny set of tool names**.

That split is worth understanding before you wire anything, because it decides where each operator action goes:

| Question | Where it is answered |
|---|---|
| Who is this agent, and is its credential still good? | the service account — created, disabled and revoked through the Owner/Admin service-account API |
| What storage scope does it act in? | the service account's `ScopeId`; a token issued under one team cannot reach another |
| Which **modules** may it read? | the service account's declared `Permissions` — ordinary platform RBAC |
| Which **tools** may it see and invoke? | its tool grants, in `IAgentToolGrantStore` |

The credential half is re-read on every request, so **disabling an account or revoking a token takes effect on the next call**, on any node, with no restart and no cache to invalidate. The grant half behaves the same way for the same reason.

## Discovery is authorisation-filtered

A tool the agent has not been granted does not appear in its `tools/list` at all, and `tools/call` on it is refused with a message **indistinguishable from "no such tool"**.

The second half is deliberate and is easy to get wrong. If a refusal told the caller "that tool exists but is not yours", an agent could walk the deployment's entire tool surface one call at a time — recovering exactly what filtering the listing took away. So the wire says one thing for both cases; the audit trail records which actually happened.

Three gates compose, in this order, and all three must admit:

1. the account holds `Read` on the tool's declaring module (platform RBAC);
2. that module's grant is live and its consent has not been revoked;
3. the tool's name is in the agent's grant set.

## Wiring

### 1. Enable service accounts

The host authenticates agents one way and one way only. Without this, every call answers `401` — so a composition that omits it is refused at startup rather than discovered in production.

```fsharp skip=fragment
let config = {
    ServerConfig.defaults with
        ServiceAccounts = EnabledServiceAccounts
}
```

### 2. Compose the host

```fsharp skip=fragment
open ToolUp.AI.McpHost
open ToolUp.AI.McpHost.McpCompose

AIServerApp.create factory providerProfile
|> AIServerApp.withConfig config
|> AIServerApp.withStorage blobStorage
|> AIServerApp.withAIConfig assistant
|> AIServerApp.addModules modules
|> McpServerApp.create
|> McpServerApp.withServerVersion "1.4.0"
|> McpServerApp.run
```

`McpServerApp` wraps a composed `AIServerApp`, because the thing it projects — the registry of AI tools with their schemas — exists only once the AI tier has composed. Every `AIServerApp` helper stays reachable through `McpServerApp.over`.

To carry one composition root across environments and turn the agent surface off in some of them, add `McpServerApp.withMcpHost NoMcpHost`; `run` then short-circuits to `AIServerApp.run` of the same base, contributing no handler, no DI registration and no middleware.

### 3. Create the agent and mint its credential

Through the ordinary service-account admin surface. The mint response is the **only** exposure of the secret that will ever exist — there is no server-side copy to retrieve.

Declare the account's module permissions here: they are the authority ceiling every one of its tokens is bounded by, and narrowing them bites on every outstanding token at its next use.

### 4. Grant it tools

```fsharp skip=fragment
let store = services.GetRequiredService<IAgentToolGrantStore>()

let! _ = store.Write(scopeId, accountId, set [ "sales.summarise"; "sales.forecast" ], adminUserId)
```

Tool names are the **authored** names — what the module declared, not the sanitised alias some providers require. There is no wildcard, deliberately: a grant set that could say "everything" would silently widen the moment a module registered a new tool.

Withdraw a grant with `Write` carrying a narrower set, or `Remove` to return the agent to default-deny. Either takes effect on the agent's next request.

### 5. Point the client at it

`POST /mcp`, protocol revision `2025-06-18`, with the minted token on the standard header:

```
Authorization: Bearer tusa_<tokenId>.<secret>
```

An off-the-shelf MCP client needs nothing else. Change the path with `McpServerApp.withRoute`.

Two things a client author may notice: batching is unsupported (that revision removed it, so an array body is refused rather than partially handled), and `GET` on the same path answers `405` — this host serves no server-initiated stream, which the streamable-HTTP transport makes optional. `initialize` reports `tools.listChanged: false` honestly: the registry is fixed at compose.

## Budgets and rate limits

Both are declared per agent, and both ride substrate the platform already has rather than a model of their own.

```fsharp skip=fragment
|> McpServerApp.withDefaultPolicy {
    Budget = AgentBudget.callsPer BudgetPeriod.Daily 500M
    RateLimit = Some { MaxCalls = 60; Window = PerMinute }
   }
|> McpServerApp.withAgentPolicy "acct-nightly-export" {
    Budget = AgentBudget.concurrency 4
    RateLimit = None
   }
```

An account named in an override is governed **entirely** by that entry — overrides replace, they do not merge. An operator who wrote one agent's ceilings should not have to read a second place to know what that agent is allowed.

| Ceiling | Counted by | What the client receives |
|---|---|---|
| `MaxConcurrentCalls` | the [budget ledger](budgets.md), domain `mcp-agent` | a JSON-RPC error whose `data` names the dimension, quota, spend and period |
| `CallAllowance` per period | the same ledger | the same, with `dimension: "calls"` |
| `MaxCalls` per window | the inbound rate-limit store, partitioned on the **token** | a JSON-RPC error plus a `Retry-After` header |

Two behaviours worth knowing before you rely on either:

- **They fail open.** If the ledger or the rate-limit store is unavailable, the call is admitted. That is the shipped posture of both substrates, and it is the right one: a budget that fails closed is a budget an operator switches off after the first incident, which leaves them with no budget at all.
- **A composition that declares ceilings it cannot count is refused at startup.** Failing open at request time and failing loud at compose time are not in tension — the first keeps a storage blip from becoming an outage, the second keeps a composition mistake from becoming a silent non-control.

Rate limiting partitions on the **token** rather than the account, so a leaked credential can be throttled without throttling the account's other, legitimate ones.

## What gets recorded

Every list, call, result and denial lands on the `_platform.ai.mcp` event stream, in the agent's own scope, carrying the account id, display name, token id, the tool, its declaring module, the outcome and the timing.

It never carries the tool's **arguments** or its **result body**. Both can contain anything the calling model chose to put in them, which is the same reasoning the platform applies to its own unauthorized-tool rows.

Denials additionally reach two existing operator surfaces, so an MCP-driven campaign is visible beside an in-app one rather than in a stream nobody queries:

- the **`/dev/ai-allowlist` rollup** and its production-safe admin panel, in that view's own payload shape;
- the uniform **`/dev/auth-denials` rollup**, through the platform's shipped authorization-audit hook.

All audit writes are best-effort and never block: the control is the refusal, not the row. A wedged event store must not turn an authorisation decision into a `500`.

## See also

- [`src/ToolUp.AI.McpHost/README.md`](../../src/ToolUp.AI.McpHost/README.md) — the package's own reference
- [Budgets](budgets.md) — the ledger seam the per-agent ceilings ride
- [AI tool authoring](../ai/extending.md) — how a module declares the tools an agent can be granted

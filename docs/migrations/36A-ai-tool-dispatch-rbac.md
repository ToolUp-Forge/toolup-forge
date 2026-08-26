# AI tool-dispatch RBAC (Phase 36.A) — consumer migration

**What changes.** AI tools are now gated by the caller's per-module `Read`
permission on the tool's declared `SourceModule`. Before this phase the agent
loop filtered its per-turn tool list by *surface* only and dispatched by name,
so a user holding no `Read` on a module could still have that module's tools
invoked on their behalf — the module's own RBAC gate sat one layer below,
unreached (GP 4).

**Scope.** Four enforcement points, all driven by one predicate
(`AIToolRegistry.isToolSourcePermittedFor`) so they cannot drift apart:

| Point | Effect |
|---|---|
| Per-turn provider tool list (`AIToolRegistry.ListAccessible`) | inaccessible tools are never described to the model |
| `IAIAssistantApi.GetAvailableTools` | a user cannot enumerate a module's tool surface |
| Agent-loop dispatch, immediately before `Execute` | typed `Denied` tool-result + audit |
| `POST /api/ai/tool-result` | `403`; the pending call is left registered |

## Do you have to do anything?

**No, if your deployment has not configured per-module permissions.**
`AccessContext.hasPermission` treats an empty `ModulePermissions` map as
unrestricted, so an unconfigured deployment sees the identical tool list in the
identical order — byte-for-byte unchanged (GP 11). Record `n-a`.

**No code change, if you have.** The enforcement uses the permission grants you
already declared; there is no new interface to implement, no compose call to
add, and no config knob.

**But there is one thing to CHECK, and it fails silently.** Each tool's
`SourceModule` string is now matched against the **keys of your permission
map**. If a module registers its tools under a `SourceModule` that differs from
the name you key permissions by — a casing difference, a slug (`"media-
optimisation"`) against a display name (`"MediaOptimisation"`), a renamed module
whose tool declarations were not updated — then for every RBAC-configured team
those tools now **disappear from the model's tool list with no error**. The
model simply stops having the capability, and the symptom presents as "the
assistant got worse", not as a permission failure.

So, once, per module that declares AI tools:

```fsharp
// The SourceModule in the tool declaration ...
SourceModule = "MediaOptimisation"

// ... must equal the key you grant permissions under.
ModulePermissions = Map.ofList [ "MediaOptimisation", [ ModulePermission.Read ] ]
```

A quick way to confirm on a running deployment: sign in as a user with a
restricted permission map and call `IAIAssistantApi.GetAvailableTools` — the
list it returns is exactly what the model will be offered.

## Permission semantics

- The `Read` / `Write` / `Admin` hierarchy is the platform's own
  (`ModulePermission.implies`): `Write` and `Admin` both satisfy the `Read`
  requirement. The gate does not re-implement it.
- A module present in the map with an **empty** permission list is denied.
- Permissions are resolved **once per turn**, from the request items the agent
  loop's background `HttpContext` carries forward. A permission revoked
  mid-conversation takes effect on the next turn, not mid-batch.

## SDK built-in tools are exempt

A tool whose `SourceModule` begins `_` (`_platform.ai`, `_sdk.FeatureFlags`,
`_sdk.ModuleVisibility`, `_algorithms`, `_facts`, `_scheduling`) or `ToolUp.`
(`ToolUp.Platform` — the narrative tools; `ToolUp.KnowledgeBase`) passes the
gate. No permission map names those keys, so gating them on their own
`SourceModule` would make every built-in family vanish the moment a deployment
configured RBAC. The `_platform.ai.*` cross-module family enforces RBAC
internally, per requested *target* module.

The exemption is **not absolute**: naming a reserved source explicitly in
`ModulePermissions` re-arms the gate on it, so a deployment that wants to gate
(say) `_algorithms` still can.

**If one of your own modules is named inside a reserved namespace** — a module
whose name starts with `_` or `ToolUp.` — it inherits the exemption. Either
rename it, or name it explicitly in your permission map to re-arm the gate.

## Client-resident tools

`ClientToolDispatchRegistry.RegisterPending` gained an overload recording the
dispatched tool's `SourceModule`; `POST /api/ai/tool-result` reads it back and
re-checks the POSTing caller's permission before completing the suspended agent
loop. A refused POST returns `403` and does **not** complete the pending call —
and deliberately does not abort it either, so a refusal cannot double as a
cancellation lever against a legitimate in-flight dispatch. The agent loop's own
90-second timeout remains the owner of that lifetime.

The pre-existing single-argument `RegisterPending(toolCallId)` is unchanged and
leaves the completion ungated exactly as before.

## Audit

A refusal writes two rows, deliberately kept distinct:

- a `_platform.ai.unauthorized_tool` / `UnauthorizedTool` `ModuleEvent` in the
  caller's scope — separate from the existing
  `_platform.ai.tool_allowlist_denial` stream, because an allowlist deny is a
  policy the deployment authored while an unauthorized-tool deny means the model
  reached for something the *user* may not have;
- an `AuthorizationDenied` row through the Phase 120 `IAuthAuditHook` under
  `ModulePermissionDenialRequirement`, verdict `unauthorized_tool`, so it joins
  the uniform `/dev/auth-denials` rollup rather than founding a ninth
  per-subsystem stream.

Both are best-effort and never block the turn. PII envelope: the tool name and
source module — never the tool arguments, which can carry anything the model
chose to put in them.

## Rollback

There is no opt-out flag, by design: an authorization gate a deployment can
switch off is one that gets switched off. To restore the prior behaviour for a
specific module, grant `Read` on it — which is the same act as declaring that
its tools are available to that team.

## See also

- `src/ToolUp.AI/TECHNICAL_GUIDE.md` § "Tool dispatch RBAC (Phase 36.A)"
- `src/ToolUp.Platform.Tests/InProcess/AIToolDispatchRbacTests.fs` — 11 cases
  covering the list filter, the reserved-namespace exemption and its re-arm, the
  forged-name dispatch refusal, and both `/api/ai/tool-result` outcomes.

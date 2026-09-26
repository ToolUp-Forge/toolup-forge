# ToolUp.AI.UiAwareness

An opt-in AI companion that lets the assistant read **what is on the user's screen right now** — the
live state of the module they are viewing — so a question such as "what filters do I currently have
applied?" is answered from the interface itself rather than from documentation.

It registers one read-only, client-resident tool, `_platform.ui.inspect_active_module`, built
entirely on retained ToolUp substrate:

- a module declares its live state as a pure `'Model -> UiStateReport` projection with
  `ClientModule.withInspectState`;
- the shell records the latest report per module at its publish points
  (`ModuleStateObserver.tryInspect`);
- the agent loop dispatches the tool to the browser (`Location = ClientResident`), where this
  companion's executor reads the active module's report and returns it.

The tool is offered on both the side panel and the full-page assistant (`Surface = Both`), declares
`ReadFacts` only, and changes nothing.

## Packages

| Package | Tier | Contents |
|---|---|---|
| `ToolUp.AI.UiAwareness.Core` | shared (.NET + Fable) | the tool name and result vocabulary |
| `ToolUp.AI.UiAwareness.Server` | server | the tool definition and `AICompose.register` |
| `ToolUp.AI.UiAwareness.Client` | Fable | the browser executor and `InspectActiveModule.install` |

## Composing it

Nothing is wired unless you compose it: `composeAI` never registers the tool, so a deployment that
does not use the companion is unchanged and pays nothing.

```fsharp skip=fragment
// Server composition root
aiApp |> ToolUp.AI.UiAwareness.Server.AICompose.register

// Client boot, alongside the AI client configuration
ToolUp.AI.UiAwareness.Client.InspectActiveModule.install ()
```

`register` is idempotent. Modules become inspectable by declaring a projection:

```fsharp skip=fragment
ClientModule.create definition
|> ClientModule.withInspectState (fun model ->
    UiStateReport.empty
    |> UiStateReport.withField "region" (sprintf "\"%s\"" model.Region)
    |> UiStateReport.withSelection "rows" model.SelectedRows
    |> UiStateReport.withAction "export" (not model.SelectedRows.IsEmpty))
```

## What the tool returns

- `{ "moduleId": …, "activePage": … | null, "snapshot": { "fields": …, "selections": …, "actions": … } }`
  — field values are embedded as JSON (a report's field values are JSON-encoded leaves).
- `{ "status": "no-active-module" }` — the request carried no active module.
- `{ "status": "no-observable-state", "moduleId": … }` — the module declares no projection, has not
  published since the shell started, or its projector threw.

The module inspected is always the request's active module, never one the model names: the tool
takes no arguments.

## Access control

No new gate is introduced; the tool passes the ones every tool passes.

- **Per-module RBAC.** The tool is sourced from the SDK-reserved `_platform.ui`, so it is admitted
  unless a deployment names `_platform.ui` in its module permission map, in which case the caller
  needs `Read` on it. The report returned is the calling user's own browser state for the module
  they are looking at.
- **`IClientToolAuthorizer`.** The agent loop consults the composed authorizer before every
  client-resident call; with none composed the answer is `Allow`.
- **Tool effect policy.** The tool declares `ReadFacts`, so a read-only `ToolPolicy` ceiling keeps it.

# ToolUp.AI.SampleClientTool.Server

Apache-2.0 reference companion — server-side compose for the sample client-resident-tool calculator.

**Reference-only.** Not a production companion. See [`Core/README.md`](../ToolUp.AI.SampleClientTool.Core/README.md) for the broader motivation.

## What's in here

- `toolDefinition` — the `AIToolDefinition` the agent loop sees. `Location = ClientResident` means the executor never runs server-side; the loop emits `ClientToolInvoke` over SSE and waits for the browser handler's POST.
- `register : ServerApp -> ServerApp` — appends the tool to the deployment's `AITools` with no authorizer. The seam-absent default in `AIAgentEngine.runAgentLoop` is `Allow`, so the model can fire the tool unrestricted. Suitable for sample / test use only.
- `registerWithPolicy : IClientToolAuthorizer -> ServerApp -> ServerApp` — appends the tool + adds an operator-supplied authorizer with `ServerApp.addServiceConfig`. The agent loop's DI resolution finds the authorizer and consults it before any `ClientToolInvoke` SSE emit.

## Wiring

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig config
|> ToolUp.AI.SampleClientTool.Server.Compose.register
|> AICompose.withAI (AIOptions.create factory providerProfile)
|> ServerApp.run
```

Or with an authorizer:

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig config
|> ToolUp.AI.SampleClientTool.Server.Compose.registerWithPolicy myAuthorizer
|> AICompose.withAI (AIOptions.create factory providerProfile)
|> ServerApp.run
```

## See also

- [`ToolUp.AI.SampleClientTool.Client`](../ToolUp.AI.SampleClientTool.Client/README.md) — the browser-side handler that pairs with this server compose.
- [`docs/ai/extending.md`](../../../docs/ai/extending.md) §"Client-resident tool authorization contract" — the seam-level walkthrough.
- [`src/ToolUp.AI/TECHNICAL_GUIDE.md`](../../ToolUp.AI/TECHNICAL_GUIDE.md) §"Client-resident companion authoring" — the full companion-authoring guide + contract-pack bindings.

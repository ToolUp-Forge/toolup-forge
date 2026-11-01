# FormsAndAI

Phase 1h reference. Stacks the Forms companion AND the AI companion onto a single `ServerApp` composition pipeline via the additive `FormsCompose.withForms` / `AICompose.withAI` extensions, rather than choosing one terminal `*ServerApp.run` and dropping the other.

```bash
cd samples/FormsAndAI
dotnet build src/FormsAndAI.Server/FormsAndAI.Server.fsproj
```

## What this demonstrates

An application that needs a Forms `WorkflowDefinition` AND an AI assistant composes both companions onto one `ServerApp`: each takes its own options record (`FormsOptions`, `AIOptions`) and is composed when the deployment resolves, so a single pipeline layers both:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withLogger logger
|> ServerApp.withStorage blobStorage
|> FormsCompose.withForms (
    FormsOptions.create ()
    |> FormsOptions.withFormSchema demoSchema
    |> FormsOptions.withWorkflow demoWorkflow
    |> FormsOptions.withAction "stampSubmission" stampSubmission
)
|> AICompose.withAI (
    AIOptions.create aiFactory providerProfile
    |> AIOptions.withAIConfig aiAssistantConfig
)
|> ServerApp.run
```

`Server.fs` is the whole sample — substrate construction + the pipeline above. Two shapes show up:

1. **One `ServerApp.empty |> ... |> ServerApp.run` pipeline** — no companion `run`. Both companions contribute via `withForms` / `withAI`.
2. **A workflow action resolving `IEntityStore` from `ctx.Services`** (`stampSubmission` in `Server.fs`). The Phase 1h `WorkflowGuard` / `WorkflowAction` signature change replaced the prior `Submission * AccessContext` tuple with a `WorkflowContext` record carrying the resolved `IServiceProvider`. Actions reach DI directly without compose-time capture — the same provider every other handler in the app sees, including the AI agent loop registered alongside.

## Scope

Compile-target sample. Server-only by design — same posture as [`samples/MinimalApp`](../MinimalApp/README.md). The sample's job is to verify that the combined composition shape compiles and that the companions thread their dependencies cleanly. Actually running the binary requires:

- `TOOLUP_ENTITY_STORE` set so the Forms companion's `IEntityStore` requirement is satisfied (the workflow action will throw on the cast otherwise).
- A real `IAIProvider` companion (e.g. `ToolUp.AIProviders.Claude`) and a key store wired via `DefaultAIProviderFactory.create` instead of `DefaultAIProviderFactory.empty`. The sample uses the empty no-op factory so it boots clean without keys; agent-loop requests fail clearly with `NoProviderConfigured`.

## See also

- [Phase 1h migration doc](../../docs/migrations/01h-combinable-composition-roots.md) — `WorkflowContext` signature change, `composeForms` / `composeAI` seams, `withForms` / `withAI` additive extensions, conflict validator.
- [Composition roots doc](../../docs/platform/composition-roots.md) — five-step composition-root pattern; the "Combining companions" section covers the Forms+AI stacking shape this sample demonstrates.
- [`samples/MinimalApp`](../MinimalApp/README.md) — Anonymous-mode minimal composition root (no Forms, no AI). The starting shape this sample builds on.

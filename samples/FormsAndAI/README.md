# FormsAndAI

Phase 1h reference. Stacks the Forms companion AND the AI companion onto a single `ServerApp` composition pipeline via the additive `FormsCompose.withForms` / `AICompose.withAI` extensions, rather than choosing one terminal `*ServerApp.run` and dropping the other.

```bash
cd samples/FormsAndAI
dotnet build src/FormsAndAI.Server/FormsAndAI.Server.fsproj
```

## What this demonstrates

Before Phase 1h, an application that needed a Forms `WorkflowDefinition` AND an AI assistant had to pick one composition root and drop the other — `FormsServerApp.run`, `AIServerApp.run`, and `RAGServerApp.run` were mutually exclusive. Phase 1h refactored both `FormsServerApp` and `AIServerApp` onto a flat `composeX : XServerApp -> ServerApp` seam and added additive `withForms` / `withAI` extensions, so a single pipeline can layer both:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withLogger logger
|> ServerApp.withStorage blobStorage
|> FormsCompose.withForms (fun f ->
    f
    |> FormsServerApp.withFormSchema demoSchema
    |> FormsServerApp.withWorkflow demoWorkflow
    |> FormsServerApp.withAction "stampSubmission" stampSubmission)
|> AICompose.withAI aiFactory providerProfile (fun ai ->
    ai |> AIServerApp.withAIConfig aiAssistantConfig)
|> ServerApp.run
```

`Server.fs` is the whole sample — substrate construction + the pipeline above. Three Phase 1h shapes show up:

1. **One `ServerApp.empty |> ... |> ServerApp.run` pipeline** — no terminal `FormsServerApp.run` / `AIServerApp.run`. Both companions contribute via `withForms` / `withAI`.
2. **A workflow action resolving `IEntityStore` from `ctx.Services`** (`stampSubmission` in `Server.fs`). The Phase 1h `WorkflowGuard` / `WorkflowAction` signature change replaced the prior `Submission * AccessContext` tuple with a `WorkflowContext` record carrying the resolved `IServiceProvider`. Actions reach DI directly without compose-time capture — the same provider every other handler in the app sees, including the AI agent loop registered alongside.
3. **The Phase 1h conflict validator** — calling `withForms` twice on the same pipeline fails fast at compose time with a single-line diagnostic (`ToolUp.Forms: companion already composed on this ServerApp pipeline. ...`) instead of cascading into the duplicate-entity-registration / double-mounted-route failures the pre-Phase-1h shape would have surfaced at first request. Uncomment the trailing `withForms` line in `Server.fs` to see it fire.

## Scope

Compile-target sample. Server-only by design — same posture as [`samples/MinimalApp`](../MinimalApp/README.md). The sample's job is to verify that the combined composition shape compiles, that the additive extensions thread their dependencies cleanly, and that the conflict validator fires when expected. Actually running the binary requires:

- `TOOLUP_ENTITY_STORE` set so the Forms companion's `IEntityStore` requirement is satisfied (the workflow action will throw on the cast otherwise).
- A real `IAIProvider` companion (e.g. `ToolUp.AIProviders.Claude`) and a key store wired via `DefaultAIProviderFactory.create` instead of `DefaultAIProviderFactory.empty`. The sample uses the empty no-op factory so it boots clean without keys; agent-loop requests fail clearly with `NoProviderConfigured`.

## See also

- [Phase 1h migration doc](../../docs/migrations/01h-combinable-composition-roots.md) — `WorkflowContext` signature change, `composeForms` / `composeAI` seams, `withForms` / `withAI` additive extensions, conflict validator.
- [Composition roots doc](../../docs/platform/composition-roots.md) — five-step composition-root pattern; the "Combining companions" section covers the Forms+AI stacking shape this sample demonstrates.
- [`samples/MinimalApp`](../MinimalApp/README.md) — Anonymous-mode minimal composition root (no Forms, no AI). The starting shape this sample builds on.

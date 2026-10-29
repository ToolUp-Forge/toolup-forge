# A turn is one record in, one outcome out

**Ships in:** ToolUp.AI.Server 0.37.0, the next minor after the tagged 0.36.0. The change is breaking-class: one
removed function and one record field.

**Affected:** code that calls `AIAgentEngine.runAgentLoop` (the string-prompt entry), and code that builds
`AIAssistantServerConfig` as a full record literal or reads `MaxHistoryMessages`. A caller of
`runAgentLoopWithInput` or `runAgentLoopWithBudget` changes nothing. The `AITaskStatus` a client is sent does not
change, word for word.

## What changes

1. **`runAgentLoop` is removed.** It was the `[<Obsolete>]` entry taking a `string option` system prompt.
2. **`AIAssistantServerConfig.Limits: AgentLoopLimits` replaces `MaxHistoryMessages`.** `AgentLoopLimits` holds
   `MaxTurns` (15), `MaxConsecutiveErrored` (2), `OuterRetries` (1), `ProviderSendTimeout` (50 s), `ClientToolTimeout`
   (90 s) and `HistoryWindow` (60). `AgentLoopLimits.defaults` is those values, the ones the loop always used.
   `AIAssistantServerConfig.limits` reads them from an optional configuration.
3. **The loop has a record entry.** `AIAgentEngine.TurnContext.create` builds one turn's context.
   `AIAgentEngine.run turn plan messages` returns `Result<LoopState, LoopStop>`. A hard stop is a value: `MaxTurns`,
   `ProviderFatal`, `CallBudgetExceeded` or `Cancelled`. A soft stop explains itself in the chat and returns the state,
   with `LoopState.Stopped` naming it: `Confusion` or `ConsecutiveErrors`. A turn's end is a `TurnOutcome`, and
   `TurnOutcome.toStatus` is the one rendering to `AITaskStatus`.
4. **Cancellation reaches the provider and the tool.** The turn's token runs as the ambient token of
   `provider.SendMessage` and of a server-resident tool's body. A cancel or a host shutdown during a provider call
   ends the loop at once, instead of at the next turn boundary.
5. **A prompt builder's short-circuit reply streams to the chat.** It goes through the same reply path as the triage
   intercept. `PromptContribution.collect` reads what a builder contributed.

## What to do

- Run the codemod: `dotnet run --project Build.fsproj -- Codemod <your source dir>`. In `SDK.Codemod.fs`, rule
  `1077-history-window-default` rewrites `MaxHistoryMessages = None` to `Limits = AgentLoopLimits.defaults`.
  `1077-history-window-literal` rewrites `Some n`, and `1077-history-window-constant` rewrites
  `DefaultMaxHistoryMessages`. Review rule `1077-run-agent-loop` lists every `runAgentLoop` call.
- For each listed call, use `runAgentLoopWithInput` with the same arguments. Pass
  `ModelInput.ofSystemPrompt "SystemPromptBuilder" systemPrompt messages` where the system prompt was.

## Verify

Build. In the Platform pack, `Phase 1077 — the agent loop's rails` pins the four rails and cancellation, and
`Phase 1077 — TurnOutcome.toStatus` pins every status word for word.

## Rollback

Pin the previous package version. No stored data changes shape.

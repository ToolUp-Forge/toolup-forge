# ToolUp.AIProviders.Microsoft365Copilot

Microsoft 365 Copilot `IAIProvider` implementation for the `ToolUp.AI` companion, over the
[Work IQ Chat API](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/rest/copilotroot-post-conversations).
Copilot answers from the signed-in user's Microsoft 365 data under the organisation's access
controls. It is a different service from Azure OpenAI, which `ToolUp.AIProviders.Copilot` targets.

- **Auth:** delegated Microsoft Entra ID only, scope `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`.
  Supply an `Azure.Core` `TokenCredential` that yields a user token (for example
  `OnBehalfOfCredential`) or a function returning the requesting user's token.
- **Prerequisites:** a Microsoft 365 Copilot licence per user, Work IQ enabled for the tenant, and
  admin consent for the scope.
- **Capabilities:** streaming; no tool use, vision, prompt caching or triage; no token usage.
- **Stateless:** each call opens a fresh conversation and sends the system prompt and earlier
  transcript as grounding context.

```fsharp skip=fragment
let copilot =
    Microsoft365CopilotAIProvider.createWithTokenSource
        userWorkIqToken
        (Microsoft365CopilotOptions.defaults |> Microsoft365CopilotOptions.withTimeZone "Europe/London")
```

The full walkthrough is in the AI provider companions guide (`docs/companions/ai-providers.md`).

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

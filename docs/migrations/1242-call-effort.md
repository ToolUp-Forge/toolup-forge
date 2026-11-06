# forge#1242: a provider call can name an effort

**What changes.** `AIProviderCallOptions` gains `Effort: string option`, and `SidePanelModel` gains
`RunningTool: string option`. Each is a record gaining a field, so a full record literal of either stops compiling
(FS0764). Nothing changes at runtime for a deployment that sets no effort: the Claude request body is byte-identical.

**The change, per file that has a literal.**

```diff
-let options: AIProviderCallOptions = { Model = Some "claude-sonnet-5-5"; MaxTokens = None }
+let options = AIProviderCallOptions.forModel "claude-sonnet-5-5"
```

Or add `Effort = None` to the literal. A literal of `SidePanelModel` adds `RunningTool = None`.

**Choosing an effort (optional).** `ClaudeAIProvider.createWithApiKeyModelAndEffort key model (Some "high")`, or the
constructors taking `effort: string option`. For one call, `AIProviderCallOptions.withEffort`. Effort trades against
answer quality per Anthropic's guidance, and changing it is a prompt-cache miss. See
[the provider's notes](../companions/ai-providers.md).

**A package that wraps the side panel.** `ConversationPanel.View` keeps its signature and draws no status line;
`ConversationPanel.ViewWithStatus` takes the line first.

**Verify.** `dotnet build`. With no effort set, a captured request has no `output_config` member.

**Roll back.** Remove the field from the literals and pin the previous version.

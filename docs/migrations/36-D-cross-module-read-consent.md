# Migration — Phase 36.D: cross-module read consent

**What changes:** before a `_platform.ai.*` tool reads from a module, the user is asked. The
default is `RememberPerConversation`, so **a deployment that adopts this version starts prompting**.

This is the second place the SDK deliberately inverts GP 11's byte-for-byte default, after
[per-module AI-queryability](per-module-ai-queryability.md), and for the same reason: an agent
reading across a user's modules without ever asking them is the behaviour this phase exists to
remove, so shipping the gate off by default would ship nothing.

## Who is affected

Only deployments that have adopted **both** Phase 36.B (the `_platform.ai.*` family, which
`composeAI` registers automatically) **and** Phase 36.C (at least one module declaring
`ServerModule.withAIExposure ModuleAIExposure.Queryable`). A deployment where no module has opted
in is already refusing every cross-module read with `UnqueryableModule`, which is an outer gate —
no dialog is ever reached, and nothing changes.

## What a user sees

The first time a conversation reads from a given module, the AI side panel shows:

> **Allow the assistant to read this module?**
> The assistant is about to read from `MoodJournal` to answer this.
> `_platform.ai.list_results`
> `[ Deny ]  [ Allow once ]  [ Allow for this conversation ]`

| Button | This read | The next read from the same module |
|---|---|---|
| Allow once | proceeds | prompts again |
| Allow for this conversation | proceeds | proceeds, no prompt |
| Deny | refused | refused again, without re-prompting |

A conversation is the unit. A new conversation asks again; there is no cross-conversation memory.

## What you have to do

### If the built-in client is what you ship — nothing

`ToolUp.AI.Client` renders the dialog and POSTs the decision. The modal is mounted by
`ConversationPanel.View`, which `AIClientConfig.withSidePanel` composes as shell chrome, so it is
present whether or not the panel is expanded and for both the side panel and the full-page
assistant.

### If you have a custom client — one of two things

A client that does not handle the new `AIConsentRequired` SSE event will never answer the prompt,
and every cross-module read will fail after the 90-second suspended-dispatch timeout with a
`UserDenied` refusal the model is told not to retry. It is a clean failure, not a hang, but it is a
failure. Either:

**(a) handle the event.** Route `AIConsentRequired(taskId, consentId, conversationId, toolName,
targetModule, intendedQueryKey, redactedPayloadPreview)` to your own dialog, and POST the answer:

```
POST /api/ai/consent
Content-Type: application/json

{ "ConsentId": "<the consentId from the event>",
  "Decision": "AllowOnce" | "AllowForConversation" | "Denied" }
```

Every field of both shapes is a `Guid` or a `string` — there is no F# type to model. An
unrecognised `Decision` token is read as `Denied` (fail closed).

**(b) opt out deliberately.** One line in the composition root:

```fsharp
AIServerApp.create factory providerProfile
|> AIServerApp.withAIConsentMode TrustEverything
|> ...
```

`TrustEverything` is the pre-36.D flow exactly: no dialog, no suspended dispatch, and no consent
audit row, since there is no decision to record. It is the right answer for a single-user or
self-hosted deployment where the person driving the agent and the person who owns the data are the
same person. It is the wrong answer for a multi-tenant one, which is why it is not the default.

The third mode, `AlwaysAsk`, prompts on every read and never honours a standing allowance.

## Verification

1. `dotnet build` — the only source change a consumer needs is the optional `withAIConsentMode`
   line. `AIServerApp` gained a field, but it is built through `create` / `createFrom` and the
   `with*` combinators, so no consumer construction site is retyped.
2. Start a conversation and ask a question that reaches another module. The dialog appears.
3. Click **Allow for this conversation**; ask again. No second dialog.
4. Check the audit stream: one `ModuleEvent` per decision under
   `SourceModule = "_platform.ai.consent"`, carrying `UserId` / `ConversationId` / `TargetModule` /
   `Decision`.

## Rollback

`AIServerApp.withAIConsentMode TrustEverything` restores the previous behaviour without downgrading
the package. Per-conversation records already written are simply never consulted.

## Notes

- The decision is persisted as `ai-conversations/{id}.consent.json`, a fourth sibling beside the
  conversation's existing blobs, in the same scope container. Nothing migrates: an absent record is
  the normal state for every conversation that predates this version, and it reads as "ask".
- The consent wait uses the **same** 90-second suspended-dispatch budget as the client-resident
  tool round trip — one declaration, two callers — so there is no second timeout to tune.
- Multi-instance deployments need SSE/POST affinity, exactly as the client-resident tool round trip
  already does: the decision POST has to reach the process holding the suspended read. The startup
  validator that warns about the in-process client-tool dispatch registry covers the same topology.

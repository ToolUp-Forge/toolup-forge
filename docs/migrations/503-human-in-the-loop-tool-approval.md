# Migration — Phase 503: human-in-the-loop tool approval

**Stability impact:** additive. **Consumer action required:** none.

**What changes:** a deployment can now declare that particular tool invocations need a *person* to
approve them before they run. It declares this by composing an `IToolApprovalPolicy`. A deployment
that composes none is **byte-for-byte unchanged** (GP 11 / GP 13) — no prompt, no suspended
dispatch, no audit row, and one failed `GetService` per tool call as the whole cost.

This is deliberately **not** the inverted default that Phase 36.C and Phase 36.D took. Those two
gates could be safe-by-default because the SDK knows what they protect — a module's data, and a
cross-module read. Here the SDK cannot know which of a consumer's tools is consequential: only the
deployment knows that `archive_records` is reversible and `delete_records` is not. A default that
guessed would either prompt on everything, which trains users to click through, or prompt on
nothing, which is what the default already is honestly.

## The gate, and where it sits

Five gates now stand between a model's tool call and the tool running. The first four are the
**deployment's** answers, decided ahead of time. The fifth is a **person's**, decided in the moment,
about that call, with its arguments on screen.

| # | Gate | Question | Phase |
|---|---|---|---|
| 1 | RBAC | may this caller read the tool's source module | 36.A |
| 2 | grant liveness | is the authority behind that permission live | 730 |
| 3 | static allowlist | did the operator allowlist this client-resident invocation | 46 |
| 4 | **approval** | **does a person approve THIS call, with THESE arguments** | **503** |
| 5 | AI-queryability + consent | (inside the `_platform.ai.*` reach tools only) | 36.C / 36.D |

Approval is consulted **after** 1–3 and **before** the tool runs, for both server-resident and
client-resident tools. After, because an action an outer gate already forbids must never surface a
dialog — a prompt for something the deployment does not permit leaks what it exposes and teaches the
user to click through. Before the server-vs-client routing, because the actions this phase exists
for — writes, deletions, spend, outbound calls — are **server-resident**, and the client-resident
allowlist seam is not where they pass.

## Declaring a policy

```fsharp skip=fragment
open ToolUp.AI

type SpendAndDeleteApprovalPolicy() =
    interface IToolApprovalPolicy with
        member _.Requires(toolName, sourceModule, argsJson, _activeModule, _activePage) =
            match toolName with
            | "billing.issue_refund" ->
                ApprovalRequired {
                    Summary = "Issue this refund?"
                    Detail = "This moves money and cannot be undone from here."
                }
            | "records.delete" when argsJson.Contains "\"all\"" ->
                ApprovalRequired {
                    Summary = "Delete every matching record?"
                    Detail = "This filter matches the whole collection."
                }
            | _ -> ApprovalNotRequired
```

Register it as a singleton in the composition root's service configuration, beside any
`IClientToolAuthorizer` you compose:

```fsharp skip=fragment
|> ServerApp.withServices (fun s ->
    s.AddSingleton<IToolApprovalPolicy>(SpendAndDeleteApprovalPolicy()))
```

The policy sees the **invocation**, not only the tool: `argsJson` is what lets it hold a deletion
whose filter matches everything and wave through one that names a single row. It must be cheap (it
runs on the dispatch path) and it must not throw — a policy that raises is treated as
*approval-required*, never as a yes, and the fact is logged.

## What a user sees

```
Approve running 'records.delete'?
This filter matches the whole collection.
records.delete · CustomerRecords
{"filter":"all"}
[ Don't run it ]  [ Approve ]
```

Two buttons, and deliberately no third. There is no "approve this tool for the rest of the
conversation": the user is approving **arguments they can see**, and a standing allowance would
authorise arguments nobody has read yet — which is the protection this phase adds. (Contrast Phase
36.D's `AllowForConversation`, which is safe precisely because its subject is a *module* rather than
a *call*.)

## What happens on each outcome

| Outcome | The tool | What the model is told |
|---|---|---|
| Approve | runs, exactly as described | its ordinary result |
| Don't run it | never runs | a typed `Denied` refusal naming the user's decision, with instructions not to re-plan the same action through another tool |
| Nobody answers within 90 s | never runs | a typed `Denied` refusal naming the timeout, and that the action is still outstanding |
| Policy requires approval but there is no interactive session | never runs | a typed `Denied` refusal naming it as a configuration matter |

That last row is the one asymmetry worth knowing: this gate **fails closed** where 36.D's consent
gate abstains. Consent sits behind three gates that have already permitted the read, so abstaining
is safe; here the deployment has affirmatively declared *this* invocation consequential, and running
it because nobody was listening is precisely the outcome the declaration exists to prevent.

## Audit

Two streams, and each is the one an operator already reads for that question.

**The decision trail** — `SourceModule = "_platform.ai.tool_approval"`, with four event types:

| Event type | Written when | By |
|---|---|---|
| `ToolApprovalRequested` | the prompt is put in front of the user | the gate |
| `ToolApprovalGranted` | the user approves | `POST /api/ai/tool-approval` |
| `ToolApprovalRejected` | the user refuses | `POST /api/ai/tool-approval` |
| `ToolApprovalExpired` | nobody answers inside the budget | the gate |

Every row carries `UserId`, `ConversationId`, `ToolName`, `SourceModule`, `ArgumentsDigest` (a
`sha256:`-prefixed digest of the exact argument JSON) and `ArgumentsPreview` (the capped rendering
the user actually read). The digest and the preview answer different questions: the preview says
what was on screen, the digest says — exactly, at any length — which invocation it was.

**The denial stream** — a refusal *additionally* writes the existing
`_platform.ai.tool_allowlist_denial` / `ToolAllowlistDenied` row, so it reaches Phase 47's
`/dev/ai-allowlist` rollup, the `IAIDenialRollupProbe` admin panel and the sustained-denial rate
monitor with no second reader. That is reuse of the denial family the estate already has, not a
parallel one.

A deployment that composes no policy writes **neither** — there is no decision to record.

## The client

If you ship `ToolUp.AI.Client`, nothing to do: `ToolApprovalDialog.View` is mounted once per tab by
`ConversationPanel.View`, in both its open and collapsed branches, and `SSEClient` routes the
`ToolApprovalRequired` event to it out of band — the same shape `ClientToolInvoke` and
`AIConsentRequired` already use.

If you render your own AI surface, subscribe to the bridge and post the answer yourself:

```fsharp skip=fragment
let unsubscribe =
    ToolUp.AI.Client.ToolApprovalDialog.subscribe (fun pending ->
        // render `pending`, then:
        // ToolApprovalDialog.answer request.ApprovalId Approved
        ())
```

Or answer the wire directly: `POST /api/ai/tool-approval` with
`{ "ApprovalId": "<guid>", "Decision": "Approved" | "Rejected" }`. The endpoint re-checks the
caller's `Read` permission on the source module recorded server-side, so a client can only answer a
question it was actually asked. An unrecognised decision token reads as `Rejected`.

## New public surface

| Assembly | Added |
|---|---|
| `ToolUp.AI.Core` | `ApprovalPrompt`, `ToolApprovalRequirement`, `IToolApprovalPolicy`, `ToolApprovalDecision` (+ its token module), `ToolApprovalDecisionRequest`, and the `AIStreamEvent.ToolApprovalRequired` case |
| `ToolUp.AI.Server` | `SuspendedPrompt` (the shared suspended-dispatch mechanism), `ToolApprovalDispatch`, `ToolApprovalHandler` |
| `ToolUp.AI.Client` | `ToolApprovalDialog` |

`AIStreamEvent` gains a case. A consumer that pattern-matches the SSE event DU exhaustively will see
`FS0025` and needs one arm — the same one-line adoption Phase 523's `AnswerVerified` and Phase 36.D's
`AIConsentRequired` each required. Nothing was removed, renamed or retyped.

**`ClientToolAuthDecision` is deliberately untouched.** The phase shard proposed extending it to
`Allow | Deny | RequireApproval`; that is a breaking change to a released closed DU *and* would have
confined approval to client-resident tools, which is where the consequential actions are not. The
approval policy is its own seam for both reasons.

## One mechanism, three callers

`SuspendedPrompt` is where the suspend/resume round trip now lives: a registry of
`TaskCompletionSource`s keyed by a correlation `Guid`, a read that does not consume the entry (so a
POST handler can authorise before it completes), and one 90-second budget. Phase 36.D's consent
registry is a delegating facade over it with its public members unchanged; this phase's approval
registry is the second. `ClientToolDispatchRegistry` is deliberately left as it is — its completion
carries a result string and its abandonment raises, a different contract with a different recovery
path in the agent loop.

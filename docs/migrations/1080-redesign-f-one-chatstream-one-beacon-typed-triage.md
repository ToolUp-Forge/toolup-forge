# One chat stream per tab, one telemetry beacon, the triage vocabularies as unions

**Ships in:** the 0.42.0 draft slot (ToolUp.AI.Core, ToolUp.AI.Client, ToolUp.AI.Server). Breaking-class: public
members are removed or retyped in all three.

**Affected:** code that subscribes to the AI stream itself (`SSEClient.subscribe`), that installs or calls the
fast-path resolver (`FastPathBridge`), that builds or reads the side panel's model (`AIClientConfig.SidePanelModel`)
or the full page's (`AIAssistantUI.Model`) as a record literal, and server code that reads the Tier-3 triage
vocabulary (`FastPathTriageResolver`). The wire is unchanged: the AI SSE frames are byte-identical, and every
telemetry row carries the same strings.

## What changes

1. **One stream per tab.** `ChatStream.AIStream.subscribe : (AIStreamEvent -> unit) -> IDisposable` is the tab's one
   subscription. The first subscriber opens the `EventSource` (re-keyed on an identity change, as before), the last
   one to dispose closes it, and `ClientToolInvoke`, `AIConsentRequired` and `ToolApprovalRequired` are routed once
   per tab, whatever surfaces are mounted. Before, the side panel and the full page each opened a stream, so with
   both mounted a client-resident tool ran twice and POSTed its result twice. `SSEClient.subscribe` is internal now.
   `ChatStream.AIStream.cancel taskId` is the one cancel POST.
2. **One reducer.** `ChatStream.reduce : ChatStreamState -> AIStreamEvent -> ChatStreamState * StreamEffect list`
   folds every case of the grammar, with `fire` and `polled` for the idle watchdog and its status poll. Both surfaces
   project their model to `ChatStreamState` and apply the effects their own way (the side panel shows a fault as an
   inline note, the full page in its error banner). `ConversationMessage.note participant conversationId content`
   (AI.Core) builds a client-side message.
3. **The side panel's behaviour now matches the full page's**, except where a row below says otherwise:

   | Event | Was (side panel / full page) | Now (both) |
   |---|---|---|
   | `MessageDelta` | correlated by the panel's / the page's conversation | unchanged |
   | `MessageComplete` | commit; clear the watchdog and the task | unchanged; the commit also carries any attached block (1131) |
   | `ToolCallStarted` | debug note only / debug note, else `[Calling X...]` written into the reply buffer | debug note only; the running tool is named by the wait line on both surfaces, and no marker is written into the reply (it was committed with it) |
   | `TaskStatusChanged … AITaskFailed` | inline note, ends the turn / debug-only note, the task kept until the watchdog's poll | shown at once on both (inline note / error banner) and the turn ends |
   | `StreamError` | inline note, watchdog kept / banner, task cleared | shown on both; the watchdog AND the task are kept, so the status poll can still recover the reply |
   | `StreamCancelled` | commit with footer; the panel cleared the task, the page did not | commit with footer; the task is cleared on both |
   | `AnswerVerified` | ignored / re-arms the watchdog | re-arms the watchdog |
   | watchdog fires | note / with a task on record, poll `GetTaskStatus` first | poll first on both; a finished task re-reads the conversation |
   | `Debug on` / `Debug off` | two parsers | one (`ChatStream.debugCommand`) |

4. **An async fast-path resolver.** `FastPathBridge.setAsyncResolver : (FastPathRequest -> Async<ResolveOutcome>)
   option -> unit` installs a resolver whose answer needs I/O. `setResolver` keeps its signature and keeps working;
   it is held in the async form. The surfaces consult `FastPathBridge.resolve`, which awaits either shape without
   blocking the UI and falls through to the model path when the resolver fails or has not answered within
   `FastPathBridge.ResolveTimeoutMs` (2 s). `FastPathBridge.tryResolve` is removed.
5. **`AIStreamEvent.kind`** (AI.Core) is the event's case name; `SSEHandler.sendEvent` reads it for the trace label.
6. **One telemetry beacon handler** serves the three sequenced fast-path endpoints. The routes, bodies, status codes
   and events are unchanged.
7. **The triage vocabularies are unions** (`ToolUp.AI.FastPathTriageResolver`): `TriageOutcome`, `TriageRoute`,
   `VerdictShape`, `TriageVerdict` and `Eligibility` (with `ReadSignal`), each with a `Token` that is the telemetry
   string. `TriageModelDecision` is `{ Decision: TriageVerdict; Confidence; Reason }` (the verdict carries its own
   members, so a mixed verdict cannot be built). `TriagePlan.TriageFallThrough` carries a `TriageOutcome`.
   Removed: the `Outcome*`, `Verdict*`, `VerdictShape*`, `TriageRoute*` and `Ineligible*` literals, `outcomes`,
   `fallThroughOutcomes`, `verdictShapes`, `verdictShape`, `isEligibleInstruction`, `ineligibilityReason` and
   `TriageEventPayload.coerceLegacy`. `TriageEventPayload` keeps its string fields (the persisted row); read them
   through `TriageEventPayload.outcome`, `route`, `servedModel` and `shape`, which give a row written before a field
   existed that field's old value.

## What to do

- A third surface that called `SSEClient.subscribe dispatch`: call `ChatStream.AIStream.subscribe dispatch` and dispose
  the returned handle when the surface unmounts. Out-of-band events no longer reach it; they are handled by the tab.
- A fast-path resolver: nothing to do. A resolver that needs I/O installs with `FastPathBridge.setAsyncResolver`.
  A caller of `FastPathBridge.tryResolve` awaits `FastPathBridge.resolve` instead.
- A record literal of `SidePanelModel`: `CurrentTaskId: Guid option` is `CurrentTask: AITask option`, and
  `InFlightParts = []` is new. Of `AIAssistantUI.Model`: add `RunningTool = None` and `InFlightParts = []`.
- Triage: `isEligibleInstruction max s` is `Eligibility.isEligible (Eligibility.assess max s)`; a reason is
  `(Eligibility.assess max s).Token`. An outcome literal `OutcomeHit` is `TriageOutcome.Hit` (its string
  `TriageOutcome.Hit.Token`). A `TriageModelDecision` literal names its verdict: `{ Decision =
  TriageVerdict.SetField(Some "country", Some "UK"); Confidence = 0.9; Reason = "" }`.

## Verify

`dotnet run --project Build.fsproj -- VerifyFable`: `Phase 1080 — the chat stream` holds one stream and one tool run
with both surfaces mounted, each row of the table above on both surfaces, and the async resolver (answered, sync,
failing, slower than the timeout). In the Platform pack, `Phase 69c.F — legacy AI SSE channel byte pin` holds the
frames byte-for-byte and every `kind` to its frame's case, and `Phase 6j.B — Tier-3 fast-path triage` pins every
token string.

## Rollback

Revert the phase's commits. No stored data changes shape.

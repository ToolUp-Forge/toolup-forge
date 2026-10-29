# One tool-fault vocabulary, and one conversation record

**Ships in:** ToolUp.AI.Core and ToolUp.AI.Server 0.36.0. Rides the 0.36.0 draft (untagged, publicly unpinned,
breaking-class since Phase 1128). This change is breaking-class too, through one record field and one signature.

**Affected:** code that constructs `ModelInput.ToolResult` as a full record literal, or calls
`AICrossModuleAudit.outcomeOf`. Nothing else needs an edit. The text the model is shown for a tool result does not
change.

## What changes

1. **`ToolOutcome`** (AI.Core, shared with the client tier) is every way a tool call ends: `Produced` content, a
   `Refused` call (`ToolRefusal`), or a `Faulted` call (`ToolFault`). `ToolOutcome.toModel` is the one rendering the
   model reads, on the server and in the browser. `ToolOutcome.isError` is what the agent loop's rails read. A
   browser-side fault and a built-in tool's argument refusal now count toward the consecutive-errored-turns stop and
   are recorded `Errored = true` on the latency row. Before, they did not.
2. **`ModelInput.ToolResult` gains `ToolCallId`.** `ToolName` is now the name of the tool the matching call named. It
   used to repeat the call id. A result that no earlier call names keeps an empty `ToolName`.
3. **`AICrossModuleAudit.outcomeOf` takes the `ToolOutcome`**, not the rendered string. For produced content it still
   reads the `error` field.
4. **A tool body can report a fault as a value.** Raise `ToolHelpers.ToolFaulted fault`, or call
   `ToolHelpers.refuseArguments fields` to refuse its arguments with a JSON body. The legacy `require*` helpers raise
   `ToolArgumentError`, so a missing argument reads to the model as invalid arguments, not as a tool failure.
5. **The conversation's four blobs follow one rule.** The UI messages, history, metadata and consent record are each
   empty when absent and an error when present but unreadable. Such a blob is never written over, so an override set
   on unreadable metadata is refused. A turn and a fast-path beacon both write the history first and then the UI.

## What to do

- Add `ToolCallId = <the call id>` to every full `ToolResult` record literal.
- Wrap an `outcomeOf` argument in `ToolOutcome.Produced` when you hold the rendered string.

## Verify

Build. The `Phase 1076` test lists in the Platform pack and the AI providers pack pin the rails, the rendered bytes,
the record's read rule and write order, and Claude's 429 floor.

## Rollback

Pin the previous package version. No stored data changes shape.

# A tool result attaches a block to the assistant turn

**Ships in:** ToolUp.AI.Core and ToolUp.AI.Server 0.37.0 (and every package built with them). It rides the untagged
0.37.0 draft, whose class is already breaking. This change is additive for a tool author. It is breaking only for
code that matches `AIStreamEvent` exhaustively, or that builds a `RegisteredTool` as a full record literal.

**Affected:** a server-resident tool that wants to show a block (a chart or table, Phase 1128) in the conversation.
Also affected: code with an exhaustive match over `AIStreamEvent`, and code that writes `RegisteredTool` as a literal
instead of calling `createTool`. A tool whose executor returns a string changes nothing, and neither does a deployment
with no tool that attaches a part.

## What changes

1. **The record form of a tool answer.** `ToolHelpers.ToolAnswer = { Text; Parts }` holds the text the model reads,
   which is exactly what a string executor returns and under the same result budget. It also holds the parts the
   assistant message will carry. Register a tool that answers it with
   `AIToolRegistry.createAnsweringTool definition answer`. `createTool` and the string executor are unchanged.
2. **`RegisteredTool.Answer`.** A new field: `Some` for a tool built by `createAnsweringTool`, `None` for every other
   tool. `Execute` keeps the text alone, so readers of the string contract (the MCP host) see what they always saw.
3. **Where the parts go.** The model never sees a part. The loop collects attached parts across tool rounds in tool-call
   order (`LoopState.Attached`). The stream emits each one as `BlockAttached(conversationId, part)` when its round
   completes, framed like every other event. The part is encoded exactly as the conversation record encodes it. The
   final assistant message carries the parts after its text part (`LoopState.messageParts`) and the conversation record
   persists them, so a reloaded conversation shows each block where the stream placed it. A turn with no attached part
   still saves a plain-text message.
4. **Client-resident tools cannot attach.** `createAnsweringTool` refuses a client-resident definition at compose
   time. A hand-built client-resident record that carries `Answer` is refused by name when called, with
   `ToolOutcome.Refused(ToolRefusal.Denied ...)`. It never reaches the browser.

## What to do

A tool author who wants to attach a block registers the record form:

```fsharp skip=fragment
AIToolRegistry.createAnsweringTool renderTableDefinition (fun ctx args -> async {
    let! node, provenance = renderTable ctx args
    return { Text = $"A table of {provenance.Metric} by site"; Parts = [ BlockPart(node, provenance) ] }
})
```

A consumer with an exhaustive match over `AIStreamEvent` adds one arm, for example
`| BlockAttached(_, part) -> ...`. A literal `RegisteredTool` gains `Answer = None`.

## Verification

`dotnet build` names every match that needs the arm (FS0025) and every literal that needs the field (FS0764). In the
Platform pack, the `Phase 1131` cases under the agent loop's rails check the order, the stream, the refusal and the
reloaded conversation. The framing pin checks the event's bytes on both channels.

## Rollback

Revert to 0.36.x, drop the arms and the field, and register the tool with `createTool`, returning its text. A
conversation saved with a block keeps it, and the 0.36.0 reader still decodes it.

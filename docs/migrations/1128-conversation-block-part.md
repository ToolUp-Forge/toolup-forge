# A conversation message carries a block part

**Ships in:** ToolUp.AI.Wire 0.36.0 (and every package built with it). It opens the 0.36.0 draft, because v0.35.0
is tagged. The class is breaking: `AIContentPart` is a closed union and gains a case, so every exhaustive match over
it needs one more arm.

**Affected:** code that matches on `AIContentPart`, most often a custom `IAIProvider` whose request builder walks
`AIProviderMessage.Parts`. Code that only constructs `TextPart` or `ImagePart`, or passes parts through untouched,
changes nothing. A deployment whose messages never hold a block behaves exactly as before.

## What changes

1. **A new case.** `BlockPart of node: string * provenance: BlockProvenance` is a chart or table shown in the
   conversation. `node` is a Fuaran-UI node as its canonical wire JSON. This tier holds it as a string and names no
   Fuaran type.
2. **The provenance record.** `BlockProvenance` records what produced the block:
   - `Kind`: the request's kind. `BlockProvenance.TableKind` (`"table"`) marks a table.
   - `Request`: the presentation request, as JSON.
   - `FactIds`: the facts the read answered with.
   - `Withheld`: the points withheld, as `BlockWithheld` (`PolicyRef`, `Count`) per policy.
   - `TableId`, `Metric` and `Period`.
3. **Persisted and replayed.** A block persists in the conversation blob and replays with its message. A blob written
   before this release still reads as before, with no block in it.
4. **Kept off the provider round-trip.** The bundled Claude, OpenAI and Gemini request builders send a block as a text
   block holding `AIContentPart.redactedSummary`, never its node. The summary names the kind, table, metric, period,
   fact count and withheld count. `AIProviderMessage.multipart` puts the summary in `Content`, and `isMultimodal`
   reads a block as not an image.
5. **The export.** `AIContentPart.exportPlaceholder` renders a table block as a markdown table of its provenance (no
   rows) and any other block as a placeholder that quotes its request.

Nothing else moves. `TextPart`, `ImagePart` and every other wire type keep their shapes and their JSON.

## What to do

Add the arm to each exhaustive match. A provider sends the summary text in the vendor's text-block shape:

```fsharp skip=fragment
match part with
| TextPart s -> textBlock s
| ImagePart payload -> imageBlock payload
| BlockPart _ -> textBlock (AIContentPart.redactedSummary part)
```

Never put `node` in a request body. Code that reads parts for display can draw the block, or fall back to
`exportPlaceholder`.

## Verification

`dotnet build` finds every match that needs the arm: FS0025 names it. Under .NET and Fable, the wire conformance
pack's `block parts` cases check the summary, the export and each bundled provider's request body. The Platform
pack's `Phase 1128` cases check the blob round-trip, the read of an older blob and the replay. The Fable client pack
decodes the server's pinned bytes.

## Rollback

Revert to 0.35.x and remove the arms. An older reader cannot decode a stored conversation that holds a block. It
refuses that blob as undecodable and leaves it untouched, so delete or archive such conversations before you roll
back, or keep them for a later upgrade.

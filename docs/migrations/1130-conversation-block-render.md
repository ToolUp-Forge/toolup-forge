# The conversation panel draws a block part

**Ships in:** ToolUp.AI.Client and ToolUp.Facts.Client 0.36.0. Rides the 0.36.0 draft (untagged, publicly unpinned,
breaking-class since Phase 1128). The class here is additive: new modules and one new function, and nothing existing
moves.

**Affected:** nobody until they opt in. A client that composes the fact store registers one resolver to have its
conversation blocks drawn. A deployment without the fact store changes nothing.

## What changes

1. **The panel draws a block part** (Phase 1128) through the published `Fuaran.UI.Renderer`. It never draws the
   stored node. It re-runs the block's presentation request through a **block series resolver**: the reader's own
   series read, under the disclosure gate, when the block is drawn. It then rebuilds the node with the Phase 1129
   builders. Beside the block it shows:
   - a citation strip that opens each fact in the fact browse module;
   - a note per disclosure policy that withheld points;
   - a drift note naming any fact restated or superseded since the answer was written (the newer figures are drawn);
   - a link to the fact table.
2. **A block that cannot be drawn is a placeholder naming why.** That is a malformed request, a request every read
   refuses, or a node the builder refuses. With no resolver registered, the placeholder says no fact reader is
   composed.
3. **The block wears the page's theme.** Its container carries no class, colour, font or background. The renderer
   themes from the page's `--fuaran-*` variables. Charts draw under the chart style the host installed
   (`Fuaran.UI.Charts.installChartStyle*`), the same style as every other Fuaran-UI chart on the page.
4. **The conjecture mark.** Text a model marks with `[conjecture]` (`ConversationPanel.ConjectureTag`) renders with the
   panel's `[unverified]` badge, labelled "conjecture".
5. **One package joins the graph from nuget.org:** `Fuaran.UI.Renderer` 0.98.1 (Apache-2.0, Fable-clean), on the
   `FuaranUIVersion` pin. ToolUp.AI.Client also references `ToolUp.Facts.Presentation`.

A message with no block renders the same markup as before. The markdown export is unchanged.

## What to do

In a client boot that composes the fact store, register the fact client's resolver before `run`:

```fsharp skip=fragment
open ToolUp.AI.Client
open ToolUp.Facts.Client

AIClientConfig.registerBlockSeriesResolver BlockSeriesResolver.resolve
AIClientConfig.run aiMode config modules
```

The links in the block open the fact browse module only when it is in the shell's module list. Add
`FactBrowseView.create None` to the modules if it is not already there. To give the block a house look, install a
Fuaran-UI theme on the page (`Theme.toCss`, or the reference stylesheet) and its chart style. The panel installs
neither.

## Verification

The Fable pack's `Conversation block render (Phase 1130)` cases cover the following: a block drawn from fixture facts
through a fake resolver, the drift note, the refusal placeholders, the citation and table links, the page's tokens on a
non-default token set, and one series palette shared with a hosted chart. A conversation with no block is held to
markup recorded from the panel before this phase.

## Rollback

Stop registering the resolver: blocks render the no-reader placeholder. Nothing is persisted.

# 1173 — Tabbed sections on a stored page

**Ships in:** ToolUp.Facts.Presentation.Server 0.38.0. It rides the 0.38.0 draft, and its class is additive:
`layout_set_section` gains two actions, and the refusal type gains one case and one new type. `LayoutRefusal` is
new in the same unreleased draft (Phase 1167), so no released `match` over it breaks.

**Affected:** nobody until an assistant asks for tabs. A deployment that composes the layout tools
(docs/migrations/1167-layout-tools.md) gets the actions with no change.

## What changes

1. **`layout_set_section` takes `action: "tabs"`.** It names the section and every tab, in order, each as a label
   and the ids of the blocks it holds, in order. Every block of the section goes in exactly one tab.

   ```json
   {
     "module": "trade",
     "page": "daily",
     "action": "tabs",
     "section": "breakdowns",
     "tabs": [
       { "label": "By channel", "blocks": ["by-channel"] },
       { "label": "By site", "blocks": ["by-site", "by-site-first-day"] }
     ]
   }
   ```

   The same action changes an existing set of tabs. A tab whose label is unchanged is kept as it is. To keep a tab
   under a new label, give its id as `tab`. A tab no longer listed is removed once its blocks have moved to the
   tabs that now hold them.
2. **`layout_set_section` takes `action: "untab"`.** It puts a tabbed section's blocks back in the section, where
   its tabs stood and in tab order, and removes the tabs.
3. **`title` is now required only by `add` and `rename`.** The schema marks it optional, and those two actions
   refuse its absence by name, as before.
4. **New refusals, typed and naming the id** (`TabRefusal`, under `LayoutRefusal.Tabs`, cause `tabs-…`): a block
   named twice; a node that is not a block of the section; a block of the section that no tab holds; a `tab` id
   that is not one of the section's tabs, or is kept twice; `untab` on a section with no tabs; a section holding
   more than one set of tabs. A block id the page does not hold at all is `unknown-node`. `tabs` passed with any
   other action is refused rather than ignored.

## The shape on the page

A tabbed section holds one `Tabs` node with `TabHeaders = None`. Each child of it is a `Box` with
`Role = BoxRole.Card` whose `Heading` is the tab's label. The renderer infers each tab's label from its card's
heading. The `Tabs` node's `ActiveIndex` is bound to `Binding.State` keyed by the tab set's own id, and `OnSelect`
is `None`. Which tab is open is therefore the viewer's UI state, which the renderer writes back on a click. It is
never stored. A tab is an ordinary card, so `layout_retitle` and `layout_set_section rename` relabel one, and
`layout_move_block` moves a block into one.

## Why no op re-states a node's children

The stored op vocabulary has no edit of one item of a list-valued prop. An `EditNode` of a section or a tab set
replaces its children wholesale. If a team saved tabs that way, a block the platform layer added to the section
later, or a retitle it made under it, would vanish on the next fold, and no conflict would report it. The fold
test "a section tabbed by structural ops keeps a lower layer's later edits; re-stating its children drops them
silently" shows both halves.

So every act is a structural op or one heading edit:

| Act | Ops |
|---|---|
| tab a section | `InsertChild` the tab set, `InsertChild` each tab, `MoveNode` each block into its tab |
| rename a tab | `UpdateProp` of that tab's `Heading` |
| reorder tabs, or blocks in a tab | `ReorderChildren` |
| move a block to another tab | `MoveNode` |
| drop a tab | `MoveNode` its blocks out, then `RemoveNode` the emptied tab |
| untab | `MoveNode` each block back into the section, `RemoveNode` the tab set, `ReorderChildren` if needed |

Blocks keep their ids, and nothing is recreated. An edit another layer made inside a block, before or after the
tabbing, therefore still finds its target. The verb never emits an `EditNode` or a `ReplaceRoot`, and never an op
outside the section's subtree. The tests check every op it emits for all three.

## Semantics worth knowing

- **A genuine conflict is reported.** Suppose the individual layer retitled a tab, and the team later drops that
  tab. The individual's edit has no target, and the fold reports it as a `LayerConflict`. The conflict names the
  individual's act, and its `ConflictCause.Removed` names the team's removal. The rest of the page folds.
- **A block another layer adds to a tabbed section stays in the section,** beside the tab set and outside every
  tab. The next `tabs` call by a layer that sees it must place it, because every block of the section is assigned.
- **Tabbing does not change a request.** The request sidecar is saved unchanged.

## Verify

```powershell
dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter "ToolUp.Platform.Tests.Layout tools"
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter "ToolUp.Platform.Tests.Page store and layer fold"
```

The list "tabbed sections" covers the acceptance edit, the fold property at two layers and the refusals.

## Rollback

Revert the commit. Tabs already saved to a layer stay valid stored ops and fold as before, and a tab remains an
ordinary card the other tools edit. If a section should read as plain blocks again, `untab` it before reverting.

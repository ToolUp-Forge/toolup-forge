# Authoring a view as a Fuaran-UI tree

A forge module's view can be authored as a Fuaran-UI tree instead of Feliz: a `Node<'Msg>` value
built from the toolkit's node builders, lowered to the `ReactElement` the module contract still
returns. A tree is data. The estate's tools can read it, an accessibility or parity check can walk
it, and it renders through the published Fuaran-UI renderer. This page is the idiom every view
moved off Feliz copies.

## The idiom

Three pieces, all in the SDK:

| Piece | Where | What it is |
|---|---|---|
| `Toolup.UIToolkit.Nodes` | `ToolUp.Platform.UI` | The toolkit's shapes as `Node<'Msg>` builders: `Nodes.Layout` (row, column, grid), `Nodes.Panel` (card, section), `Nodes.Actions` (button, primary, secondary, danger, banner), `Nodes.Forms` (text, number, checkbox, required, form, dropdown), `Nodes.pageHeader`, `Nodes.heading`, `Nodes.text`. |
| `Toolup.UIToolkit.Keep` | `ToolUp.Platform.UI` | A table mapping a model sub-value to its node, by identity, held weakly. |
| `TreeLowering.lower` | `ToolUp.Platform.Client` (internal) | Lowers a tree to a `ReactElement`, the renderer loading on demand, with the subtree memo on and the render context kept across dispatches. |

A view reads its strings from `ctx.Messages`, builds its tree from the model, and lowers it:

```fsharp skip=fragment
open Toolup.UIToolkit

let private memberRow =
    Keep.create (fun (m: Member) ->
        Nodes.Layout.row (Nodes.Ids.keyed "member" m.Id) [
            Nodes.text (Nodes.Ids.keyed "member-name" m.Id) m.Name
            Nodes.Actions.danger (Nodes.Ids.keyed "member-remove" m.Id) "Remove" (Remove m.Id)
        ])

let tree (model: Model) (msgs: TeamManagerMessages) : Node<Msg> =
    Nodes.Layout.column "members" [
        Nodes.pageHeader "members-header" msgs.Title [ Nodes.Actions.primary "members-add" msgs.Add OpenAdd ]
        yield! Keep.list memberRow model.Members
    ]

let view (model: Model) (ctx: RenderContext<Msg>) =
    TreeLowering.lower (TreeLowering.TreeHost.ofContext ctx HostBindingSources.empty) ctx.Dispatch (tree model ctx.Messages.TeamManager)
```

What the builders guarantee:

- **One id per shape.** A builder takes the id of the node it returns; a builder that returns several
  derives the inner ids from it (`pageHeader "members-header"` gives `members-header-title` and
  `members-header-actions`). A list item takes its id from the item's identity through
  `Nodes.Ids.keyed`, never from its index.
- **Every visible string is a parameter.** The toolkit holds no literal a reader sees, exactly as the
  Feliz toolkit does, so the localization boundary does not move.
- **Events are messages.** A control's handler is `Action.Dispatch msg`; the renderer hands it to the
  dispatch the tree was lowered with. A node never closes over a dispatch function, which is what
  lets a kept node stay valid across renders.

`TreeHost` is what a tree resolves against beyond its nodes: the `HostBindingSources` the module's
`IHostStateProjection` projects, the locale (`TreeHost.ofContext` takes the view context's, which
`Binding.Format` reads when a binding names none), and optionally the host's `IHostI18nResolver`
(`TreeHost.withI18n`) for `TextSource.I18n` keys. A capability-bag view lowers with
`TreeLowering.lowerHosted host capabilities node`.

A surface that must cost the shell's first load nothing builds its tree in a file of its own and
mounts it through `TreeSurface.deferred`; the trade is described there.

## The `Keep` rule

The renderer's subtree memo serves the element it built last time for **the same `Node` value under
the same render context**. A view that rebuilds every node from its model on each dispatch gets
nothing from it, by construction: every node is new. So:

1. **Build a subtree that depends on one model sub-value through a `Keep` keyed on that sub-value.**
   `update`'s copy-and-update keeps every sub-value it did not touch by reference, so a kept row
   renders once until its record is replaced.
2. **Hold the `Keep` for the life of the view** (a module-level `let`). A `Keep` created during a
   render is an empty table and keeps nothing.
3. **The build function depends on its input alone.** The node is reused for as long as the input is
   the same object, so anything else it reads (a locale, a selection, a flag, a string from the
   catalog) goes stale when it changes. Put it in the input: key the `Keep` on a small record of
   everything the node shows.
4. **Key on a record, a list or a class, never a string or a number.** The table is a `WeakMap` in
   the browser, which cannot hold a primitive; a primitive key builds its node on every call (correct,
   but unkept).

The lowering keeps its render context while the projected `QueryResults` and `State` maps are the same
objects, the locale and resolver are unchanged, and the tree declares the same state seeds and
fragments. Project those maps from model fields (so they keep their identity across dispatches)
rather than building a new `Map` in the projection on every render, or the context, and with it the
memo, is rebuilt each time. A write to a renderer store (a `SetState`, a filter or a selection)
advances the store generation and rebuilds everything once, as it must.

What it buys, on the 200-row table `ClientBench`'s browser leg measures: a dispatch drops from about 15.6 ms with
every row rebuilt to about 1.7 ms with the rows kept, at about the Feliz table's cost (the figures and the method are
in [the budgets page](budgets.md#a-tree-surface-is-judged-on-a-browser-dispatch-not-on-viewperdispatchus)).

## Row identity is the dispatch key

A kept node captures the message it sends. A row built with `Bump i` keeps its index: after a reorder
or a removal it dispatches the wrong message, silently. Over a kept list the message carries the
row's identity (`Bump row.Id`) and `update` finds the row by that id. Without `Keep` this was a bad
habit; with it, it is a defect.

## What stays an island

`TreeLowering.island id render` embeds a region that stays React, as a `Custom` node: a component
that owns hooks, a grid, a chart, a third-party widget, anything the vocabulary has no node for.
Islands are refreshed on every render of the tree, so an island must not sit inside a kept subtree:
the memo would serve the element it rendered first and the island would stop updating. Place islands
in the part of the tree a view rebuilds.

## A `Model` field or `Binding.State`

- **A `Model` field** is anything `update` reads or the server must hear about: a form's values, a
  selection that drives a request, a flag another view shows. The tree shows it as a literal or a
  `Binding.Static` and sends a message when it changes.
- **`Binding.State`** is presentation state no `update` reads: which disclosure is open, which tab
  of a purely visual set is showing. The renderer holds it, a write rebuilds the tree once, and it is
  gone on remount.

When in doubt it is a `Model` field: state the renderer holds is invisible to the module's tests and
to the shell's inspection of the model.

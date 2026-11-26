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

A kept node captures the message it sends. Over a kept list the message carries the row's identity
(`Bump row.Id`) and `update` finds the row by that id. A row built with `Bump i` costs two things,
[set out below](#the-index-shape-and-the-identity-shape-side-by-side): a `Keep` keyed on the index
keeps nothing, and `update` reads the index against the model as it is when the message arrives. Without `Keep` this was a bad
habit; with it, it is a defect.

## The index shape and the identity shape, side by side

| Fails: the row's position | Holds: the row's identity |
|---|---|
| `Keep.create (fun (i, row: Row) -> …)` | `Keep.create (fun (row: Row) -> …)` |
| `Nodes.Actions.danger (Nodes.Ids.keyed "member-remove" row.Id) "Remove" (Remove i)` | `Nodes.Actions.danger (Nodes.Ids.keyed "member-remove" row.Id) "Remove" (Remove row.Id)` |
| `Keep.list memberRow (List.indexed model.Members)` | `Keep.list memberRow model.Members` |
| `update`: `List.removeAt i model.Members` | `update`: `model.Members \|> List.filter (fun m -> m.Id <> id)` |

What the left column costs is two things:

- **It keeps nothing.** A key that carries the index, here the tuple `List.indexed` makes, is a new object on
  every render, so the `Keep` misses on every row and every row is rebuilt on every dispatch. The view looks
  kept and runs like one that is not. A `Keep` built from its input alone cannot hold a stale index: the index
  can only reach a kept node through its key, and a key that carries it is never reused.
- **It names whichever row is there when the message arrives.** `update` reads `Remove 2` against the model
  current at the time, not the one the row was drawn from, so a removal, a reorder or a server push in between
  sends it to the wrong row, or past the end. The identity shape names the same row whatever moved.

The analyzer rule [`TUA004`](analyzer-rules.md#tua004--a-message-over-a-kept-or-tree-built-list-that-carries-the-enumeration-index)
reports the left column: an index bound by `mapi` or `iteri`, by a tuple over `indexed`, by a `for i in a .. b`
loop, or as an `int` in a `Keep.create` build, reaching a message inside a `Keep.create` build or a lambda or
loop that builds a tree. A Feliz list is outside it, because nothing keeps a Feliz row. An index may still name
a node through `Nodes.Ids.keyed` where a row has no identity of its own; it never goes in a message.

## What stays an island

`TreeLowering.island id render` embeds a region that stays React, as a `Custom` node: a component
that owns hooks, a grid, a chart, a third-party widget, anything the vocabulary has no node for.
Islands are refreshed on every render of the tree, so an island must not sit inside a kept subtree:
the memo would serve the element it rendered first and the island would stop updating. Place islands
in the part of the tree a view rebuilds.

## Five rules, and what each shape costs

Each rule below comes with the figure that makes it a rule. The figures are `ClientBench`'s tree lever
(`levers.treeShapes` in the measurement document `perf-budget-gate.ps1` writes, recorded and not gated): the
200-row table of the bench, a heading and per row a label, a value and a button, built and rendered to static markup
under Node with React's production build, the minimum of three runs. `viewPerDispatchUs`'s ceiling, 4,400 µs, is the
line a shape is read against; it times the Feliz table's construction alone, which is why every tree figure here is
construction and render together (`renderToStaticMarkup`), beside the Feliz table timed the same way.

| Shape: 200 rows, a label, a value and a button each | Build (µs) | Build and render to markup (µs) | Markup (characters) | Browser dispatch, desktop / 6x CPU (ms) |
|---|---:|---:|---:|---:|
| Feliz table, the reference | 313.3 | 433.3 | 12,960 | 1.72 / 10.61 |
| `DataGrid`, a `CellKind.Button` column | 1.6 | 1,479.7 | 45,086 | 4.61 / 30.05 |
| A row of boxes per record, `Text` cells | 67.0 | 4,471.8 | 170,033 | 7.79 / 60.67 |
| The same, rows kept with `Keep` | 12.3 | 3,965.6 | 171,633 | 1.02 / 10.20 |
| A row of boxes per record, `Markdown` cells | 66.8 | 6,971.6 | 183,633 | 9.84 / 97.18 |

The browser column is `levers.browserDispatch`: one host dispatch of the same table in headless Edge, the view rebuilt,
React's commit and a forced layout, the median of 30 samples, the minimum of three runs. Kept rows pay a first render
in full (the Node column renders each pass from nothing, so the subtree memo has nothing to serve); what `Keep` buys is
the dispatch after it, where the kept table costs what the Feliz table does. Of a tree's render, the renderer's own
`RenderContext.create` is 64.0 µs for the boxes and 2.2 µs for the grid: the rest is the walk and React's elements.

1. **The table form is `DataGrid`.** Rows of the same fields are a grid: `Fuaran.grid` with a column per field and,
   for a per-row action, a `CellKind.Button` column whose message carries the row's identity. A row of boxes per
   record is the shape that cost the original measurement its verdict: a node per cell, each with its own wrapper.
2. **Running text is `Text`.** `Nodes.text` writes the vocabulary's `Text` node, which is never parsed. `Markdown`
   is for text that carries formatting, and it pays a parse per cell and a raw-HTML sink React reparses on commit.
3. **Boxes are for layout.** `Nodes.Layout.row` / `column` / `grid` arrange regions of a page (a header, a panel, an
   action bar), not the cells of a table.
4. **Every node needs a stable id.** A builder takes the id of the node it returns, a list item takes its id from the
   item's identity (`Nodes.Ids.keyed`), and the id is what the renderer keys reconciliation and the subtree memo on.
5. **Islands are for hook-owning components only.** A component that owns a hook (display state for a text input, a
   document-wide listener, focus or scrolling, its own requests) stays React behind `TreeLowering.island`; anything
   that is a function of the model is a node. The team manager's directory typeahead and transfer filter, the
   visibility editor's form (its note is display state) and the command palette's overlay are the cases on main.

### A surface the shell imports at boot is deferred

The node vocabulary carries `Fuaran.Compute.DataFrame` and `Fuaran.Core.Column` in its closure, so a view that builds
its tree in the module the shell reads at boot puts all of it in the first load, and the gate refuses that build by
name. The team manager, the visibility editor and the palette are in the shell's boot closure, so each builds its tree
in a file of its own (`TeamManagerTree`, `ModuleVisibilityAdminTree`, `CommandPaletteTree`) that its module mounts
through `TreeSurface.deferred` with a dynamic import of the file:

```fsharp skip=fragment
[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

let private loadSurface () = surfaceOf (importDynamic "./TeamManagerTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "TeamManagerTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.TeamManager
            Locale = ctx.Locale
        |}
    )
```

Fable spells the import of an `.fs` file as the compiled module's path, so the static module needs no compile-time
reference to the tree file, which comes after it. The tree file exports a `Surface` component that builds the tree and
draws it with `TreeLoweringRuntime.Tree (TreeLowering.treeProps host props.Dispatch node)`, and ships a `.js` re-export
beside it for a feed consumer, as `TreeLoweringRuntime.js` does. The palette is mounted this way only while it is
open, so a shell that never opens it never fetches the renderer; its document-wide shortcut stays in the static module.

### A row that shows more than its record

The `Keep` rule asks a row's build to depend on its input alone, and a real row rarely does: a team row shows the
catalog's strings and whether it is the active team, a member row the caller's role. The team manager keeps such rows
with one `Keep` per context, replaced when the context changes (the catalog by identity, the scalars by value), so a
row node is reused while both its record and its context are the same, and every row is rebuilt once when the context
moves.

## A `Model` field or `Binding.State`

- **A `Model` field** is anything `update` reads or the server must hear about: a form's values, a
  selection that drives a request, a flag another view shows. The tree shows it as a literal or a
  `Binding.Static` and sends a message when it changes.
- **`Binding.State`** is presentation state no `update` reads: which disclosure is open, which tab
  of a purely visual set is showing. The renderer holds it, a write rebuilds the tree once, and it is
  gone on remount.

When in doubt it is a `Model` field: state the renderer holds is invisible to the module's tests and
to the shell's inspection of the model.

## The three assertions a view moving to a tree carries

A view moved from Feliz to a tree proves three things, through `ModuleHarness` in
`ToolUp.Platform.Testing`, at every state the view's tests reach through the module's real `update`:

| Assertion | The call | What it proves |
|---|---|---|
| **Accessible** | `h.AssertAccessibleView(ViewMount.mount, ModuleHarness.lowered lower tree, Accessibility.Strict)` | The tree passes the same rules the Feliz view is held to. `lowered` turns the tree and its lowering into the `'Model -> dispatch -> view` shape a Feliz view has, so one call covers both idioms. |
| **Behaves** | `h.Interact(ViewMount.mountAndInteract, treeView, drive).AssertModel(...)` | A click or a field edit on the mounted tree dispatches a message, the message runs through `update`, and the assertion is on the `Model`. An interaction that dispatches nothing fails `Interact` by name: the control is not wired to the dispatch the tree was lowered with. |
| **At parity** | `h.AssertViewParity(ViewMount.mount, felizView, treeView)` | For one model, the Feliz view and the tree expose the same controls and headings, by role and accessible name, in the same tab order. The failure is a diff: what is missing from the tree, what only the tree has, and where the tab order first differs. |

No assertion reads markup. A lowered tree's markup is the renderer's (it is many times the Feliz
view's), and an assertion tied to it breaks on every renderer release while proving nothing about the
module. Parity compares what a user of assistive technology meets (`TreeViewParity`): names are
computed as a browser computes them, so two renders with different ids and wrappers compare equal when
they announce the same thing.

A parity difference is not always the tree's defect. Where the Feliz view is wrong (an input whose
`<label>` is not associated with it) and the tree is right, the phase
records the difference, says which side was wrong, and asserts that parity names it, rather than
copying the defect into the tree. The reverse holds too: where a toolkit shape exposes less than the
Feliz shape it replaces, parity says so, and the view uses a shape that does not. The team manager's invite dialog is the worked case, in
`ToolUp.AI.Client.Tests/TreeViewParityTests.fs`, beside the kit's go-red proofs: a tree missing a
label fails the floor, a dispatch that does not reach `update` fails `Interact`, and a reordered tab
stop fails parity.

The tests draw the tree through the renderer's component directly (`lowerTree` in that file), as a
deferred surface's own file does: the module's `TreeLowering.lower` puts the same component behind a
dynamic import, which a synchronous mount cannot wait for. So the module exposes its tree builder
(`internal tree : ... -> Node<Msg>`) and its view is `TreeLowering.lower` over it.

The cost is the fourth thing a move records, and it is measured, not asserted here: the browser
dispatch figure of [the budgets page](budgets.md#a-tree-surface-is-judged-on-a-browser-dispatch-not-on-viewperdispatchus),
before and after.

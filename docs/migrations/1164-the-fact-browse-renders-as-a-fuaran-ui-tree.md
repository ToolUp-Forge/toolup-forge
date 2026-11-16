# The fact browse renders as a Fuaran-UI tree

**Ships in:** the 0.43.0 draft. `ToolUp.Facts.Client` only; no public signature changes.

**Affected:** a deployment that composes the fact browse (`FactBrowseView.create` or `appendFactBrowseModule`). The
module id, its pages and routes, its `FactBrowseLinks` topic subscriptions and every link into it are unchanged, so
nothing has to be edited. Read this if you test the browse's markup, or if you resolve instructions against it.

## What changed

The Tables page and the Table page are authored as Fuaran-UI trees and lowered through the shell's tree seam, which
loads the renderer on demand. The Rows page is unchanged.

- **The table select.** Both pages carry a select named `table`: one option per listed table, the selected table
  chosen, and `OpenTable tableId` on a change. On the Tables page choosing a table also shows the Table page, as the
  listing's links did; on the Table page it opens the chosen table in place. The listing's per-row links are gone: the
  select is the one way to open a table from the browse, so it is the one control an instruction addresses.
- **The listing** is a static table with a "Latest run" column carrying the latest run's outcome and reason, which the
  Feliz listing showed only as a hover title. The freshness cell is a pill in the standing's tone, with its own words.
- **The Table page** shows each declared column's description in a "Description" column rather than beneath the metric
  id with a hover title. The population figures are tiles; the run history is unchanged and stays React inside the
  tree.
- **While the renderer chunk loads**, each page shows the seam's placeholder (`role="status"`, `aria-busy`), and a chunk
  that fails shows the module boundary's error with its Reload button.

## What a resolver reads now

Nothing declares a field on the browse. A host derives the select's affordance from the tree
(`Fuaran.UI.Operability.derive`): it is named `table`, its value space is the listed table ids, and it answers
`set table to {value}`, `change table to {value}`, `what is table`, `open {value}`, `open the {value} table` and
`show the {value} table`. [What the fact browse lets a resolver set](../platform/facts.md#what-the-fact-browse-lets-a-resolver-set-phase-1162)
has the whole surface, and what is not a control yet (a day, a level, descriptions as aliases).

## What to do

- **Nothing**, if you compose the browse and do not test its markup.
- **A test that mounts `FactBrowseView.tablesView` or `FactBrowseView.TablePage`** sees the loading placeholder on its
  first mount. Mount once, let the chunk arrive, then mount again; the second mount draws the tree. Markup assertions
  that read the per-row link buttons, the column description's `title` attribute or the listing's freshness `title`
  move to the select, the "Description" column and the "Latest run" column.
- **A host that routed "open the … table" through `FactBrowseLinks.OpenTableTopic`** can keep doing so; the topic sets
  the same selection the select does.

## Rollback

Pin the previous `ToolUp.Facts.Client`. The model, the API and the topics did not change, so nothing else moves with it.

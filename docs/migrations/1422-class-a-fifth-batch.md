# Moving a pure view to a tree: the fifth batch

The usage dashboard, the data-source admin, the composition inspector's five pages and the no-active-team landing
render as Fuaran-UI trees. No public signature moves: `UsageDashboard` keeps its `Model`, `Msg`, `init`, `update`,
`view`, `moduleId`, `builtIn` and `adminTile`; `DataIngestionUI` and `CompositionInspectorUI` keep theirs (and the
inspector its `PanelData`); the landing's members were internal and stay so. No `Msg` case and no `Model` field is
added. The connector credential-form contract (`DataSourceCredentialHandler`, registered through
`DataSourceCredentialUIRegistry`) is unchanged. A consumer has nothing to change. The recipe is
[the first batch's](1190-class-a-first-batch.md), as [the second](1419-class-a-second-batch.md),
[the third](1420-class-a-third-batch.md) and [the fourth](1421-class-a-fourth-batch.md) met it; this page records where
the fifth batch met something none of them did.

## What the batch did as the recipe says

Each tree is a file of its own (`UsageDashboardTree.fs`, `DataIngestionTree.fs`, `CompositionInspectorTree.fs`,
`NoActiveTeamLandingTree.fs`, each with its `.js` re-export in the project's packed `Content`), mounted through
`TreeSurface.deferred`, since the shell reads all four modules at boot. The Feliz pages moved to the client test pack as
the parity reference. The tables are `Nodes.Table` over the columns each catalog section names: the usage aggregate
(two), the data sources (six), and the inspector's component groups (four), configuration knobs and canonical methods
(two each), rules (four), preflight findings (three), disclosure declarations (three) and provenance (two). Every load,
export and download stays an effect `update` returns; the trees only dispatch.

## What differed

**A host-supplied React element is hosted, not redrawn.** A connector's credential form is the connector's own
element, contributed through the public `DataSourceCredentialHandler` contract. The data-source tree hosts each form as
an island (`TreeLowering.island`) inside the panel the tree draws, and hands it the refresh it calls once it saved. The
handler returns its element itself, so the tree file names no Feliz to host it.

**A module of several pages mounts one surface per page.** The inspector's five pages each mount the same deferred
surface with the panel they draw. The sections of a page are set further apart than a section's own lines, so each
heading reads with the table under it.

**Lists that arrive whole are kept per table.** The inspector's lists arrive whole with their panel's read and never
change in place, so each table keeps its rows per record under the catalog section (`KeepScoped.Rows`), one keep per
table: an export builds no row. The provenance table's rows are the view's three flags, not records, so that table is
kept whole on the view (`KeepScoped.One`). The data-source rows are kept on the source and its own entries in the model
(`KeepScoped.RowInputs`): one source's status arriving builds that row alone. The registered connector kinds are
strings, so the add strip is kept whole on them.

**Lists with no column headers stay card lists.** The declared surfaces (a heading and the descriptor's JSON each) and
the connector kinds (an add button each) hold no column names in their catalog sections, so neither is a table.

**Instants are formatted from their components.** The Feliz Rules page printed its capture instant with the `"u"`
format, which Fable does not print, so it threw whenever its panel was read; the data-source page printed its instants
with a custom format. The trees write both from the instant's parts. The parity assertion holds the Rules page's read
states to throwing on the Feliz side and leaves them uncompared.

**The landing is held in the test packs, not in the shell.** The landing is drawn for a team-scoped deployment whose
caller is on no team. In the sample's team-only composition the server refuses such a caller outright, and once an
individual surface admits one, the shell draws its own onboarding surface in place of the built-in landing. So the
landing has no browser scenario; the parity and accessibility packs hold it in every state.

## Differences a reader of assistive technology meets

- The usage grouping is a select its label wraps, named by the label, where the Feliz select carried an `aria-label`.
- A data source's name is its credential panel's toggle and says whether the panel is open.

## The census

The four page files leave the Feliz census, and none enters it.

Class A outside the platform client is now the first-party samples and templates, the published UI toolkit and the
other client projects, counted per project in the census's `sources`.

## Trying it in the HelloWorld sample

The sample opens the usage dashboard, the data-source admin and the composition inspector. Under the development
identity the server enables data ingestion (no source is declared and no connector form is registered, so the page
starts empty) and pins the OAuth redirect base to the sample's own origin.

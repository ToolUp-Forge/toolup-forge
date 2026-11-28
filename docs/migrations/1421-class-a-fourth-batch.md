# Moving a pure view to a tree: the fourth batch

The health monitor, the background-jobs admin, the service status board and the data-migration admin render as
Fuaran-UI trees. No public signature moves: each module keeps its `Model`, `Msg`, `init`, `update`, `moduleId` and
`builtIn` (and, where it has one, `adminTile`), and the jobs admin keeps its pure decisions (`standingOf`, `rerunOffer`,
`warningOf`, `ordered`, `attemptReading` and the rest). No `Msg` case and no `Model` field is added. A consumer has
nothing to change. The recipe is [the first batch's](1190-class-a-first-batch.md), as [the second](1419-class-a-second-batch.md)
and [the third](1420-class-a-third-batch.md) met it; this page records where the fourth batch met something none of them did.

## What the batch did as the recipe says

Each tree is a file of its own (`HealthMonitorTree.fs`, `JobsAdminTree.fs`, `ServiceStatusBoardTree.fs`,
`MigrationStatusTree.fs`, each with its `.js` re-export in the project's packed `Content`), mounted through
`TreeSurface.deferred`, since the shell reads all four modules at boot. The Feliz pages moved to the client test pack as
the parity reference. The tables are `Nodes.Table` over the columns each catalog section names: the probes (six), the
validator outcomes (four), the jobs (four) and the data types (five). The jobs poll stays the declared effect value it
already was, and every refresh stays an effect `update` returns: the trees only dispatch. No view here holds a text field,
a checkbox, a select or a radio, so none of the four draws an island.

## What differed

**A row shows its record and that record's entries in the model.** A job's row reads the job, its recent attempts, its
re-run progress and whether its attempts are open; a data type's row reads the type, its recorded status, its trigger in
flight and whether its failures are open. The attempts, the re-runs and the statuses are maps on the model, so a
`Rows` table under one context would rebuild every row whenever any job was polled. `KeepScoped` gains `RowInputs`: it
hands the tree the row input it drew last for a record whose own parts are the same objects, and a `Keep` over those
inputs builds one job's row when that job's attempts arrive, and one type's row when its trigger is answered. The
reloads clear first (`Refresh` sets the list loading), so there is nothing to keep across one; a poll replaces one job in
place and leaves the others the objects they were.

**Panels with no columns are kept per section.** The status board's six sections are named panels in a fixed order, not
a list. A section's refresh replaces that section's summary and leaves the other five the objects they were, so each
panel is kept whole on its summary and its own flags (collapsed, refreshing, changed): one section's answer builds that
panel alone. The health monitor's cards (degraded capabilities, AI action denials, the scheduler's missed ticks) each
arrive whole with their read and are kept whole on it.

**Lists with no column headers stay card lists.** The degraded capabilities, the denial groups and recent denials, the
scheduler telemetry and the status board's sections hold no column names in their catalog sections. They are cards of
labels beside their values, not tables: a column header would be a catalog field, a field on a released record.

**A toggle says whether it is open.** The jobs admin's attempts toggle, the migrations admin's failures toggle and the
status board's section toggles are buttons carrying `aria-expanded`, as the Feliz page's attempts toggle did.

**A changed status fades in.** The Feliz pages pulsed a probe or a section whose status changed since the previous read.
The vocabulary has no pulse that lasts; a changed status is a fresh badge node that fades in as it mounts.

**Instants are formatted from their components.** The Feliz health monitor printed the scheduler card's instant, and
the Feliz status board its generated instant, with the `"u"` format, which Fable does not print: the health monitor threw
whenever a job scheduler was registered, and the status board threw drawing any board it had read. The trees write the
universal sortable form from the instant's parts, and the other instants (the snapshot's, a capability's, a denial's)
the same way rather than as the browser's long date string. The parity assertion holds those states to throwing on the
Feliz side and leaves them uncompared.

**The section toggle sits on its own line.** A status-board section's name toggles it and its refresh sits at the
right; the severity badge and the headline share the next line, so the badge stays in view while the section is
collapsed and is not top-aligned against a button.

## Differences a reader of assistive technology meets

- The health monitor's tabs are one tab stop with the active tab's panel after it, as in the earlier batches.
- A status-board section's toggle is named by the section, where the Feliz button's name ran its chevron, the section
  and the severity together.
- The re-run confirmation is the renderer's modal: a dialog with a close control and its heading at level 2.

## The census

The four page files leave the Feliz census, and none enters it: no tree here draws an island.

## Trying it in the HelloWorld sample

The sample opens all four. Under the development identity its module declares one scheduled job in the bootstrap
team's scope and one data type, so the jobs admin and the migrations admin each have a row and their actions a server
answer; the server composes the in-process scheduler and the migration routes without a startup sweep.

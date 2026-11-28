# Moving a pure view to a tree: the third batch

The audit trail, the data-subject-request admin and the external-contact admin render as Fuaran-UI trees. No public
signature moves: each module keeps its `Model`, `Msg`, `init`, `update`, `moduleId` and `builtIn` (and the external-contact
admin its `ContactForm`, `OptInDraft` and `View`). No `Msg` case and no `Model` field is added. A consumer has nothing to
change. The recipe is [the first batch's](1190-class-a-first-batch.md), as [the second batch](1419-class-a-second-batch.md)
met it; this page records where the third batch met something neither did.

## What the batch did as the recipe says

Each tree is a file of its own (`AuditLogTree.fs`, `DataSubjectRequestAdminTree.fs`, `ExternalContactManagerTree.fs`, each
with its `.js` re-export in the project's packed `Content`), mounted through `TreeSurface.deferred`, since the shell reads
all three modules at boot. Row ids come from `Nodes.Ids.keyed` over the record's id and every row message carries it
(`ToggleDetail event.Id`, `OpenContact contact.Id`, `WithdrawOptIn(contact.Id, channel)`). The Feliz pages moved to the client
test pack as the parity reference. The poll delay of a background export stays the declared effect value it already was,
and both downloads (the audit CSV, the subject export) stay effects in `update`: the trees only dispatch.

## What differed

**One island module, not a copy per tree.** The label-wrapped text field and the labelled checkbox had been written once
per tree. They are now `TreeIslands`, an internal module compiled before every tree: `textField` (a visible label),
`hiddenLabelTextField` (a label only assistive technology reads), `checkbox`, and the two this batch needed, `select`
and `radios`. The platform-users, permissions and
Platform Management trees draw theirs through it, with the same markup as before, and the three new trees name no Feliz at
all.

**The tables take the columns the catalog names, and no other.** The audit trail's table had a fifth column, headed by
nothing, holding the detail toggle; the contact grid had a sixth, holding Manage. Neither header is in the catalog, and a
catalog field is a record field (a breaking change on a released record). So the audit trail is a table of four columns
whose summary cell holds the toggle, and the contact list a table of five whose name cell holds Manage.

**An open row is drawn by a second kept table.** The audit trail's rows are kept per event, but a row looks different
while its detail is open. Keying the open set into the rows' context would rebuild every row on every toggle; instead an
open row is served from a second `Keep` over the same records, so a toggle builds the row it opened and nothing else.

**A reload clears, so only the contacts keep by reference.** A contact's save, delete or consent change ends in the whole
list loaded again, which now folds through `Memo.keepEqual` by id, as the team manager's does. The audit trail's Refresh
and Apply start a fresh sequence with the rows cleared first, so there is nothing to keep across one.

**Map-held lists are kept whole on their record.** The erasure preview and the run summary each list one row per handler
from a map that arrives whole with its record and never changes in place; a map's entries are structs, which cannot key
a `Keep`. Each panel is kept whole on its record by reference (`KeepScoped.One`), its row ids from the handler's name.
A contact's consents are kept the same way, whole on the contact.

**A filter is a chip, a choice is a select, and what the vocabulary cannot say is an island.** The audit trail's time
window is two date chips (`Filters`, whose change carries a message and writes no store), and its event type the
toolkit's select (`Nodes.Forms.dropdown`): a choice chip draws an empty option of its own beside the catalog's "All event
types". Inside the contact form the channel is `TreeIslands.select`, a labelled select drawn as the form's text fields
are, where the node select draws its label in a style of its own. The background-export switch is a checkbox outside a
form that filters nothing, so it is `TreeIslands.checkbox`. The erasure policy is `TreeIslands.radios`: a button per
choice, the module-visibility editor's shape, could not say which policy is chosen, and outside a form the vocabulary has
no radio, so each policy is a radio again, with what it means under it.

**A run summary the Feliz page could not draw.** The page formatted the run's start and end with a custom
`DateTimeOffset` format, which Fable does not render: a confirmed erasure threw. The tree formats the instant from its
components. The parity assertion leaves that one state uncompared and says why.

## Differences a reader of assistive technology meets

- The data-subject page's tabs are one tab stop with the active tab's panel after it, as in the earlier batches.
- The contact form's fields and the consent source are named by their labels; the Feliz page's labels were not associated
  with their inputs, which were named by their placeholders. The data-subject fields have no label in the catalog, so a
  label only assistive technology reads names each by its placeholder, as before.
- The event type and the channel are selects their labels wrap; the Feliz channel select had no name.

## The census

The three page files leave the Feliz census, and so do the platform-users and permissions trees, which drew their islands
with Feliz; `TreeIslands` enters it.

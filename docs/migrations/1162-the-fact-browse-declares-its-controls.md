# The fact browse's controls: what a resolver can set, and why the browse declares no fields yet

**Ships in:** documentation only, against the 0.38.0 draft. No package surface changes and no behaviour changes.

**Affected:** nobody has to change anything. A host that installs a browser fast path should read
[What the fact browse lets a resolver set](../platform/facts.md#what-the-fact-browse-lets-a-resolver-set-phase-1162)
before it declares instructions against the browse.

## What was asked, and what the tree says

Phase 1162 set out to declare the browse's table, day and level as fields a browser fast path resolves, with
the table descriptions (Phase 1150) as aliases, so "open the category table" or "show yesterday" would need no
model turn. Checked against the tree before anything was built, three of its premises do not hold:

1. **Only the table is a selection.** `Model.SelectedTable`, set by `OpenTable tableId`, is the one of the
   three that the module's Elmish loop holds and a message sets. There is no day: the browse reads each
   table's current run, and neither the model nor the read contract (`FactRowQuery`) carries a day or an
   as-of. There is no level selection: a table sits at one declared level (`FactTableListing.Level`), which
   the pages show and nothing chooses. A field declared over a value with no message behind it is the shape
   a resolver must never be given, because it matches and then has nothing to dispatch.
2. **The descriptions are not on the listing.** A table's description reaches the client on the selected
   table's detail (`FactTableDetail.Description`), so the browse holds no description for a table it has
   not opened, and an alias set over every table's description has nothing to read.
3. **The declaration surface is not this SDK's.** The browser fast path is a resolver a host installs
   through `FastPathBridge.setResolver`; the field-declaration builders that resolver reads ship with it and
   consume `ClientModule`, not the other way round, so a package in this SDK cannot reference them. A
   declaration of that kind also composes the module's action decoder, so it needs the typed
   `ClientModule<Model, Msg>` before `ClientModule.register`. `FactBrowseView.create` registers inside and
   returns an `ErasedModule`, so nothing outside the module can declare fields on it today either.

The browse therefore declares no fields in this release. That is the outcome, not a deferral in disguise: a
declaration written against the browse as it stands would have bound patterns to two values that cannot be
set and imported a dependency the SDK may not take.

## What would have to change, and where

- **A day control is a read-contract change first.** The browse would need a day (or as-of) on its run and
  row reads, and a model field and message that select it, before any instruction about a day can do
  anything here.
- **The table field needs a way in.** Either the browse exposes its typed module before registration, so
  the host's fast-path install can decorate it, or the browse handles a set-field action itself through its
  own action decoder. Which of the two is a design decision for the next phase, recorded here so it is not
  taken by accident.
- **Aliases from descriptions need the descriptions on the listing.** `FactTableListing` would carry each
  table's description, an additive field.
- **None of it outlives the move off Feliz.** When the browse renders as a declarative tree, its named
  controls derive what can be set from the tree itself, and any hand-written field declaration on this
  module retires with the Feliz view. A change made for the interim should be the smallest one that serves
  it.

## What to do

Nothing. A host that wants "open the … table" resolved without a model turn today can route it through the
cross-module topic the browse already subscribes to (`FactBrowseLinks.OpenTableTopic`), which sets the same
selection `OpenTable` does.

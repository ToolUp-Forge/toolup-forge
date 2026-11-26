# Moving a pure view to a tree: the first batch

The team manager, the module-visibility editor and Platform Management render as Fuaran-UI trees. No public signature
moves: each module's `View` still returns `PageContent`, and Platform Management's two public confirmation views
(`PlatformAdminUI.AssignConfirm.view`, `PlatformAdminUI.ConfirmDeleteTeam.view`) keep their signatures and return a
`ReactElement`. A consumer has nothing to change. This page is the recipe the next views copy; the idiom itself is
[authoring a view as a tree](../platform/authoring-trees.md).

## The mechanical steps

1. **Deferred, because the shell reads the module at boot.** The tree is built in a file of its own (`<Module>Tree.fs`,
   with a `.js` re-export beside it and listed in the project's packed `Content`), and the module's view mounts it
   through `TreeSurface.deferred` with a dynamic import. The static module keeps `Model`, `Msg`, `init`, `update` and
   the definition, and opens no Feliz.
2. **Every list the model holds is kept.** A list of records is kept per row (`KeepScoped.Rows`, one `Keep` per row
   context: the catalog section, the directory, a pending set). A list of strings cannot key a `Keep`, so the section
   that shows it is kept whole on the sub-values it shows (`KeepScoped.One`). A context member is compared by
   reference when `update` keeps it by reference (a list, a map, the catalog), by value when it is a scalar.
3. **Ids and messages come from identity.** A row's node ids come from `Nodes.Ids.keyed` over the row's id, and every
   row message carries that id (`SetTeamArchived(team.TeamId, true)`, `RequestRevoke userId`), never an index.
4. **What the tree reads from the session arrives as a value.** Platform Management's "Self" affordance takes the
   caller as a `Caller` record the deferred surface reads, so the tree is a function of its arguments.
5. **The page it replaced becomes the parity reference.** The Feliz page moves to the client test pack
   (`FelizReferenceViews`), and the module's own tree is held to the three assertions against it: the Strict floor in
   every state the tests reach through `update`, a click or edit that reaches `update` asserted on the `Model`, and
   accessible parity, where each difference that remains is judged and named exactly by its test.
6. **What a dispatch builds is asserted.** The kit counts the nodes of the tree after a dispatch that are new objects:
   one team renamed among 200 builds 10 of the team manager's 1,403 nodes and 39 of Platform Management's 4,018.

## The two judgement calls

**What became a `Model` field: nothing new.** Every value the trees show was already on the model, and the views stay
additive: no `Msg` case is added. Platform Management's text and pickers send the messages they sent before
(`SetNewTeamName`, `UpdateAssignInput`, `SetNewTeamOwnerUserId`), and its tabs dispatch `SwitchTab`.

**What stayed an island: every component that owns a hook.** The visibility editor's form (its note is display state,
committed with `Save`), Platform Management's team-name input (committed on leaving it or on Enter, as before), the
directory typeaheads, the public-utility widgets, and the team manager's typeahead, transfer filter and readers panel.
The renderer's local buffer (`Binding.local` flushed on submit) is the node that would replace a note-style island,
and it is not used yet: its submit listener outlives each render of the buffer, so a submit commits every value the
field held, in order, the empty one first. `TreeViewParityTests` pins that behaviour; when the pin goes red, the
buffer can replace these islands with no `Msg` case.

## Differences a reader of assistive technology meets

- The visibility editor names each kind button by its label, with the description beside it, where the Feliz page put
  the description inside the button's name.
- The note, the team name and the initial owner are named by their labels; the Feliz page's labels were not associated
  with their inputs, which were named by their placeholders.
- Platform Management's tabs are one tab stop with the active tab's panel after it, as a tab list's keyboard pattern
  asks, where the Feliz bar put every tab in the tab order.
- A confirmation is the renderer's modal: a dialog with a close control and its heading at level 2.

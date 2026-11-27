# Moving a pure view to a tree: the second batch

Session security, the permissions admin and the platform-users admin render as Fuaran-UI trees. No public signature
moves: each module keeps its `Model`, `Msg`, `init`, `update`, `moduleId` and `builtIn`, and `SessionSecurityUI.view`
and `SessionSecurityUI.lastSeenLabelWith` keep theirs. No `Msg` case and no `Model` field is added. A consumer has
nothing to change. The recipe is [the first batch's](1190-class-a-first-batch.md); this page records where the second
batch met something the first did not.

## What the batch did as the recipe says

Each tree is a file of its own (`SessionSecurityTree.fs`, `PermissionsAdminTree.fs`, `PlatformUsersTree.fs`, each with
its `.js` re-export in the project's packed `Content`), mounted through `TreeSurface.deferred`, since the shell reads all
three modules at boot. Every list the model holds is kept: the sessions, the principals, the members and the offboard
dialog's preview and outcome rows per record (`KeepScoped.Rows`); the permission matrices and the modules table, whose
rows are module names, whole on what they show (`KeepScoped.One`). Row ids come from `Nodes.Ids.keyed` over the record's
id and every row message carries it (`AskRevoke sessionId`, `OpenOffboard(userId, kind)`, `SelectMember userId`).
Tables are `Nodes.Table`. The Feliz pages moved to the client test pack as the parity reference.

A reload keeps what it did not change: `SessionsLoaded` and `PrincipalsLoaded` fold the answer through
`Memo.keepEqual`, as the team manager's `TeamsLoaded` does, so a session or principal that equals the one the page
holds stays that object and its row is not built again.

## What differed

**The clock arrives as a value.** The session page read `DateTimeOffset.UtcNow` while it drew. The tree takes the
minute it reads "last seen" against, which the deferred surface reads as it renders; the labels are minute-grained, so
a row stays kept across the dispatches of one minute.

**The caller is read only where it is shown.** The permissions admin names the signed-in caller by display name in the
member list. Its tree takes the caller as a `Caller` read through a function, called only while the Members tab draws,
as Platform Management's Teams tab does: a deferred surface that read the session store on every render made the
browser's storage a dependency of every tab.

**A control that sends several messages.** The team-defaults matrix's "Select all" and "Clear all" toggled each module
whose cell was not yet as asked, one `ToggleDefault` each. With no `Msg` case to add, the tree's button sends an
`Action.Chain` of those dispatches, which reach `update` in order.

**A permission cell is an island; the team-less filter is a chip.** Outside a form the vocabulary's only checkbox is a
filter chip (`Filters`), and a chip whose change carries a message dispatches it and writes no filter store. The
platform-users list's "Team-less only" is a filter, so it is that chip. A permission matrix cell is not: a chip draws its
label above the box in a padded panel of its own and has no disabled state, where a cell is a box beside its level that
a save in flight must make inert. The matrices' checkboxes are therefore islands: a labelled `<input type="checkbox">`,
named by its label as the Feliz one was.

**Text fields stay islands, as before.** The offboard dialog's reason and confirmation token hold what is typed in a
hook and commit it on leaving the field or on Enter. The islands' labels now wrap them.

**Lists without column headers stay lists.** The sessions and the principals are lists of rows, not tables: the
message catalog has no column headers for them, and adding one is a field on a public record.

## Differences a reader of assistive technology meets

- The permissions admin's tabs are one tab stop with the active tab's panel after it, as in Platform Management.
- A member in the permissions admin is a button named by the member; the override badge and the role sit beside it,
  where the Feliz page put both inside the button's name.
- The offboard dialog is the renderer's modal: a dialog with a close control and its heading at level 2.
- The reason and the token are named by their labels; the Feliz page's labels were not associated with their inputs,
  which were named by their placeholders.

## The census

The three page files leave the Feliz census; the two trees that draw an island with Feliz (the permissions admin's
checkboxes, platform users' labelled text fields) enter it, so `sources` falls by one. Session security's tree names no
Feliz.

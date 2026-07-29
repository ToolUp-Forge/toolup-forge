# Command palette (Ctrl+K)

An opt-in overlay that lets a user jump to any page they can reach by typing part of its name. Off by default; one config line turns it on.

Once a deployment grows nested multi-page modules and a separate administration area, finding a page stops being "scan the rail" and becomes "search it". The palette is that search surface — keyboard-first, and scoped to exactly the pages the current user could have clicked to.

## Enabling it

```fsharp
open ToolUp.Platform

let clientConfig = {
    ClientConfig.defaults with
        AppName = "Acme Analytics"
        CommandPalette = DefaultCommandPalette
}
```

`CommandPaletteMode` has two cases:

| Case | Effect |
|---|---|
| `NoCommandPalette` | **Default.** No keyboard listener is registered, no overlay enters the React tree. Byte-identical to a build without the feature. |
| `DefaultCommandPalette` | Mounts the SDK palette. |

There is no `CustomCommandPalette` arm, unlike `ToastCentreMode` or `NotAuthorisedMode`. That is deliberate — see [Why entries cannot be customised](#why-entries-cannot-be-customised).

Nothing else needs wiring. The palette reads the module list the shell already has; modules declare nothing and register nothing.

## Keybinding

**Ctrl+K**, or **Cmd+K** on macOS. The listener is attached to `document` when the palette is enabled and removed when the shell unmounts.

It is deliberately ignored while the caret is in an `<input>`, `<textarea>`, `<select>`, or a `contenteditable` element. Ctrl+K is a text-editing chord in several editors and in every readline-style input, so hijacking it inside a form field would break the field to make a shortcut work. Ctrl+Alt+K is likewise left alone (it is an AltGr composition on some keyboard layouts).

Inside the palette:

| Key | Action |
|---|---|
| Type | Filters live |
| ↑ / ↓ | Move the highlight (wraps at both ends) |
| ↵ | Open the highlighted page |
| Esc | Close |

Clicking the backdrop also closes it, and hovering a row moves the highlight there so ↵ always activates the row under the pointer.

Opening the palette always starts from an empty query with the first entry highlighted — a shortcut whose result depends on when it was last used is a shortcut nobody trusts.

## What appears in it

One entry per **destination**, in sidebar order:

- a single-page module contributes one entry, its module name;
- a multi-page module (registered with `ClientModule.withPages` and declaring two or more pages) contributes one entry per page, labelled `Module › Page`.

A module registered with `withPages` but declaring only *one* page is a leaf in the sidebar, so it is a single entry here too. The multi-page parent row is not listed separately: in the narrow rail it navigates to the module's first page, which is already the first entry.

Each row shows the icon the sidebar would have rendered for the same destination, and a context label on the right — the module's `withGroup` label, or (under `AdminSurface = SeparateArea`) its navigation area.

Selecting an entry dispatches the same navigation the sidebar does, so switching between pages of the module you are already in preserves that module's state exactly as a sidebar click would.

### Matching

Typing filters by fuzzy subsequence over the module name plus the page title, so `sku` reaches `Sales Analysis › SKU analysis` and `salan` reaches `Sales Analysis`. Matching is case-insensitive, and whitespace in the query is ignored — a query is a fragment you are typing, not a phrase. Ranking favours matches that start a word and matches that run contiguously, then earlier matches; entries that score equally stay in sidebar order.

An empty query lists everything, which makes the palette usable as a browse surface as well as a search one.

## Visibility — the palette can never widen access

The entries are **not** the registered module list. They are derived from the same visibility fold that produces the sidebar and guards deep links (`SidebarVisibility.visible`), with a page expansion applied to what survives. Concretely, that means the palette automatically respects:

- the server's accessible-modules response (RBAC, and per-team module exposure);
- each module's `withNavRole` gate (`PlatformAdminOnly` / `TeamOwnerAdmin`);
- each module's own `Visibility` predicate over the resolved subject kind;
- the no-active-team landing collapse.

So a Member never sees an administration page in their palette, and enabling the palette cannot reveal anything a user could not already reach from the rail. This is structural, not a rule someone has to remember: there is no palette-side filter that could fall out of step, because there is no palette-side filter. A contract pack asserts the equivalence across the full subject × mode × exposure matrix.

As everywhere else in the client, this is UX coherence rather than the security boundary — the server's per-route guards remain the enforcement (GP 12). A caller who forges a request past the palette meets a 403, not data.

### Why entries cannot be customised

A deployment-supplied renderer would take a list of entries and could just as easily take the raw module list — and that is the one place the property above could be lost, with the failure mode being a hidden administration page two keystrokes from every user. Theming hooks are the supported customisation instead.

## Theming hooks

The palette draws its colours from the [client-toolkit tokens](../client-toolkit-tokens.md) (`--surface`, `--text-strong`, `--text`, `--muted`, `--radius`, plus the `brand` colour for the active row), so it re-skins with the rest of the shell by default.

For finer control, every part carries a stable attribute:

| Selector | Part |
|---|---|
| `[data-toolup-palette="backdrop"]` | Full-screen backdrop |
| `[data-toolup-palette="panel"]` | The dialog panel |
| `[data-toolup-palette="input"]` | Query box |
| `[data-toolup-palette="results"]` | Scrolling result list |
| `[data-toolup-palette="empty"]` | "No pages match that." state |
| `[data-toolup-palette="footer"]` | Key-hint footer |
| `[data-toolup-palette-index="<n>"]` | A result row, by position |
| `[data-toolup-palette-group="<label>"]` | A row's context label |

For example, to widen the panel and drop the footer:

```css
[data-toolup-palette="panel"] { max-width: 48rem; }
[data-toolup-palette="footer"] { display: none; }
```

The panel is a `role="dialog"` with `aria-modal`, the result list is a `role="listbox"`, and the active row carries `aria-selected`.

## Cost when disabled

Under the default `NoCommandPalette` the shell renders `Html.none` in the palette's slot: the component is never mounted, so no `keydown` listener is registered, no candidate list is derived, and Ctrl+K keeps whatever meaning the browser gave it (GP 13).

## See also

- [`modules.md`](modules.md) — the module convention, including `withPages` for multi-page modules.
- [`client-toolkit-tokens.md`](../client-toolkit-tokens.md) — the CSS custom properties the palette themes from.
- [`surfaces.md`](surfaces.md) — the subject model the visibility fold resolves against.

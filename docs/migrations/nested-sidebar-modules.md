# Nested multi-page module sidebar entries (consumer migration)

**What changes.** The SDK shell sidebar previously rendered a multi-page module (one whose `ClientModule` declares more than one `PageConfig`) as one flat rail entry *per page* — a 10-page module produced 10 sibling entries. It now renders a multi-page module as **one collapsible parent entry** that expands to show its pages as nested children. Single-page (and legacy single-`PageConfig`) modules are unchanged — they still render as one leaf entry.

This is a **presentation-only** change. Page routing is untouched: each page child keeps its composite `"{moduleId}{pageRoute}"` sidebar id (the same id that round-trips through the shell's page-nav path), so deep links, the active-page highlight, and same-module page navigation behave exactly as before. The header still resolves the active page's name/icon.

**Scope.** Client-side, shell-only. No server change. No consumer-facing rename or removed member. Consumers do not construct the sidebar view/section records or invoke the shell/sidebar components directly (the SDK's own composition root owns those calls), so the widened component signatures and record shapes below require **no consumer code change**.

## Diff to apply

### Default behaviour (no change required)

A consumer that composes modules through the SDK program builder inherits the nested presentation automatically. A multi-page module's pages now sit under a collapsible parent instead of as flat siblings; nothing in the module declaration (`ClientModule` / `PageConfig` / routes) changes.

Behaviour worth knowing (all automatic):

- **Expand/collapse** persists per user in browser localStorage (a new `UserSidebarPreferences.ExpandedModules` set, keyed by bare module id). Multi-page modules render collapsed by default; the module that owns the **active** page is force-expanded so a deep-linked page is always visible.
- **Collapsed (narrow `w-20`) rail:** clicking a multi-page module navigates to its **first page** (there is no room for a subtree in the narrow rail; hover-expanding the sidebar reveals the pages).
- **Pinning** works at whichever granularity the id is pinned. Pinning a multi-page module's **parent** pins the module (its pinned-section entry navigates to the first page); pinning an individual **page** (a composite id) still lifts that one page into the pinned section as its own entry. An individually-pinned page is suppressed from its module's page subtree, mirroring how a pinned module is lifted out of its home group.
- **Drag-reorder** stays module-level (top-level entries only); page order within a module is fixed.

### Preferences blob upgrade (automatic, no action)

`UserSidebarPreferences` gains the `ExpandedModules` field. A preferences blob written by an older app version does not contain it. Because the client preferences parser requires every record field and throws on a missing one, `SidebarPreferences.load` backfills any absent field into the raw blob before typed parsing — so an older blob upgrades **in place**, preserving the user's existing pins / order / expanded-groups rather than resetting them. No consumer action, and no user re-configuration.

## Verification

1. `dotnet build ToolUp.Forge.sln` clean.
2. Boot a consumer app with a module that declares multiple `PageConfig`s. Confirm the sidebar shows **one** entry for that module (not one per page), and that clicking it (in the hover-expanded rail) expands to the pages.
3. Navigate to a page via a child entry; confirm the URL / active-page highlight matches the pre-migration behaviour, and that same-module page-to-page navigation does not re-initialise the module (Phase 12b `PageViews` semantics unchanged).
4. Deep-link directly to a multi-page module's non-first page; confirm the owning module is expanded and the correct child is highlighted.
5. Pin an individual page, then a whole module; confirm both appear in the pinned section and behave as described above.
6. With a preferences blob saved by the prior version in localStorage, reload; confirm existing pins / order survive (the blob is upgraded, not reset).

## Rollback

There is no consumer-side opt-out flag — the presentation change is unconditional for multi-page modules. To revert, pin the SDK client packages to the prior version; a downgrade reads the same localStorage blob unchanged (the extra `ExpandedModules` field is simply ignored by the older parser's field set).

## Consumers

The migration is **N-A** for any deployment whose modules are all single-page — those render identically to before. Deployments with multi-page modules get the nested presentation automatically with no code change; the only observable differences are the collapsible entry, the automatic per-user expand state, and the pinning/rail semantics described above.

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AriaProp

open Feliz

// ─── Typed aria-* + role attribute helpers ───────────────────────
//
// ARIA's `aria-*` and `role` attributes are the standard hooks for
// assistive technology — screen readers, voice control, switch
// access. React renders them correctly via a hardcoded exception in
// its attribute normaliser, the same protection `data-*` enjoys
// (see `DataProp.fs`).
//
// The hazard is the same as the `data-*` family: reaching for these
// attributes via `prop.custom (name, value)` puts a kebab-case
// string on the same code path that silently drops other kebab-case
// DOM attributes. Today React's hardcoded exception saves the call;
// a future React release tightening the heuristic would silently
// regress every consumer-site aria attribute. Cheap to insulate
// today via this typed-helper module.
//
// Feliz exposes most ARIA properties through `prop.ariaXxx` /
// `prop.role`, and consumers should prefer those where Feliz
// already covers the attribute. This module exists for symmetry
// with `dataProp` and `svgProp` — every `prop.custom ("aria-…", …)`
// or `prop.custom ("role", …)` site routes through here so the
// audit ratchet (`DomAttrCustomAuditTests.fs`) can flag any
// regression. The forge client tree carries zero `aria-*` /
// `role` `prop.custom` sites today (Feliz `prop.ariaXxx` /
// `prop.role` cover all current needs); these helpers are the
// blessed escape hatch for consumer modules with a Feliz-uncovered
// attribute.
//
// **Never use raw `prop.custom` with an `aria-*` or `role`
// attribute name in new code.** Either use Feliz's typed
// `prop.ariaXxx` / `prop.role` (preferred — already typed), or add
// a helper here / call `ariaProp.custom rawName value`.

/// Wrappers around `prop.custom` keyed by ARIA attribute names.
/// Module name is lowercase to match the call-site shape of Feliz's
/// own `prop.*` API.
///
/// React's attribute normaliser preserves `aria-*` names verbatim
/// (kebab-case is correct here — the DOM attribute is literally
/// `aria-label`, not `ariaLabel`). The helpers exist for symmetry
/// with `dataProp` / `svgProp` and to give audits a single seam to
/// ratchet against `prop.custom ("aria-…", …)`.
[<RequireQualifiedAccess>]
module ariaProp =

    /// `aria-label` — accessible name for an element whose visible
    /// label is unsuitable (e.g. icon-only button). Prefer Feliz's
    /// `prop.ariaLabel` where it covers your call site.
    let label (v: string) : IReactProperty = prop.custom ("aria-label", v)

    /// `aria-labelledby` — references the id(s) of element(s) whose
    /// text content provides the accessible name. Space-separated.
    let labelledBy (v: string) : IReactProperty = prop.custom ("aria-labelledby", v)

    /// `aria-describedby` — references the id(s) of element(s) whose
    /// text content provides additional descriptive text. Space-separated.
    let describedBy (v: string) : IReactProperty = prop.custom ("aria-describedby", v)

    /// `aria-hidden` — `true` removes the element from the
    /// accessibility tree. Use sparingly — visually-decorative
    /// elements (icons paired with text) are the typical case.
    let hidden (v: bool) : IReactProperty =
        prop.custom ("aria-hidden", if v then "true" else "false")

    /// `aria-expanded` — current expanded/collapsed state of a
    /// disclosure widget (accordion, dropdown, etc.).
    let expanded (v: bool) : IReactProperty =
        prop.custom ("aria-expanded", if v then "true" else "false")

    /// `aria-selected` — current selected state of an item in a
    /// selectable container (listbox, grid, tab, etc.).
    let selected (v: bool) : IReactProperty =
        prop.custom ("aria-selected", if v then "true" else "false")

    /// `aria-checked` — current checked state of a checkbox / radio /
    /// switch / menuitemcheckbox / menuitemradio / treeitem.
    /// Three-state values: `"true"`, `"false"`, `"mixed"`.
    let checked' (v: string) : IReactProperty = prop.custom ("aria-checked", v)

    /// `aria-disabled` — element is perceivable but not operable.
    let disabled (v: bool) : IReactProperty =
        prop.custom ("aria-disabled", if v then "true" else "false")

    /// `aria-busy` — element is being updated. Screen readers may
    /// wait for `aria-busy="false"` before announcing changes.
    let busy (v: bool) : IReactProperty =
        prop.custom ("aria-busy", if v then "true" else "false")

    /// `aria-live` — politeness level for live-region announcements.
    /// Valid values: `"off"`, `"polite"`, `"assertive"`.
    let live (v: string) : IReactProperty = prop.custom ("aria-live", v)

    /// `aria-atomic` — when a live region updates, announce the
    /// entire region (`true`) or just the changed nodes (`false`).
    let atomic (v: bool) : IReactProperty =
        prop.custom ("aria-atomic", if v then "true" else "false")

    /// `aria-controls` — references the id(s) of element(s) whose
    /// contents or presence are controlled by this element.
    let controls (v: string) : IReactProperty = prop.custom ("aria-controls", v)

    /// `aria-current` — represents the current item within a set.
    /// Valid values: `"page"`, `"step"`, `"location"`, `"date"`,
    /// `"time"`, `"true"`, `"false"`.
    let current (v: string) : IReactProperty = prop.custom ("aria-current", v)

    /// `aria-pressed` — current pressed state of a toggle button.
    /// Three-state values: `"true"`, `"false"`, `"mixed"`.
    let pressed (v: string) : IReactProperty = prop.custom ("aria-pressed", v)

    /// `aria-haspopup` — indicates the availability and type of an
    /// interactive popup. Valid values: `"true"`, `"false"`, `"menu"`,
    /// `"listbox"`, `"tree"`, `"grid"`, `"dialog"`.
    let hasPopup (v: string) : IReactProperty = prop.custom ("aria-haspopup", v)

    /// `aria-modal` — `true` marks a dialog as modal — assistive
    /// tech should restrict navigation to the dialog's contents.
    let modal (v: bool) : IReactProperty =
        prop.custom ("aria-modal", if v then "true" else "false")

    /// Generic `aria-*` escape hatch. `name` is passed verbatim as
    /// the DOM attribute name; callers should include the `aria-`
    /// prefix. Use this for one-off `aria-*` attributes that don't
    /// merit a typed helper, while still routing through this module
    /// so audits can see the intent.
    let custom (name: string) (v: string) : IReactProperty = prop.custom (name, v)

    /// `role` — overrides the element's implicit ARIA role.
    /// Examples: `"button"`, `"dialog"`, `"navigation"`, `"alert"`,
    /// `"status"`. Singular helper — there is no `role-X` pattern in
    /// the spec, so this is the only `role` accessor needed.
    /// Prefer Feliz's `prop.role` where it covers your call site.
    let role (v: string) : IReactProperty = prop.custom ("role", v)
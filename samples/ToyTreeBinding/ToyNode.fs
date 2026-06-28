// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Toolup.Samples.ToyTreeBinding.ToyNode

open Feliz
open ToolUp.Platform

// ─── A second, in-tree reference tree-binding (neutrality proof) ──────
//
// `ToyNode` is a deliberately tiny, hand-rolled typed-tree UI algebra
// that exists for exactly one reason: to be a SECOND consumer of the
// host-neutral client/server seams (`ClientHostCapabilities` /
// `withElementView`; the server-rendered-fragment source; the
// scope-isolated live-session channel; the host-neutral action
// authorizer). The seams claim to be renderer-neutral — "any external
// typed-tree language binds an adapter onto these hooks". A claim with
// exactly one consumer is asserted, not demonstrated; a second,
// trivially-different tree language exercises every hook and discharges
// the SDK's own "attempt a second implementation before declaring the
// interface stable" discipline.
//
// The toy lives in samples/tests only. It adds nothing to any shipped
// consumer pipeline and is byte-for-byte absent from a build that never
// references it. It carries NO platform-specific vocabulary — the whole
// point is that it is a stranger to the substrate, binding only the
// public seams.
//
// Three lowerings, deliberately split by tier:
//   - `lower`       → `ReactElement` (the client/Fable surface; routes
//                     events through a host-supplied callback, which the
//                     `withElementView` binding wires onto the four
//                     `ClientHostCapabilities`).
//   - `lowerToHtml` → a static HTML string (tier-neutral, server-safe;
//                     drives the server-rendered-fragment + live-frame
//                     seams — neither of which is Fable-reachable, so the
//                     string lowering is the bridge a .NET host renders).
//   - `toAction`    → an `ActionDescriptor` (Core; lets a host gate a
//                     toy event default-deny through the action
//                     authorizer before routing it).

/// Severity a toy "notify" event carries. Maps 1:1 onto the platform
/// toast levels at the binding boundary — the toy itself names only its
/// own three-value vocabulary.
type ToyLevel =
    | Info
    | Warning
    | Error

/// The toy's action vocabulary. The four cases mirror the four
/// host capabilities a tree's typed actions route through — the
/// neutrality proof is that each binds without the seam knowing anything
/// toy-specific.
type ToyEvent =
    /// Route the shell to a sidebar id (→ `Navigate`).
    | NavigateTo of sidebarId: string
    /// Raise a toast (→ `Notify`).
    | NotifyWith of level: ToyLevel * text: string
    /// Dispatch a message into the hosting module's MVU loop (→ `Dispatch`).
    | DispatchBump
    /// Ride a remoting-shaped async call, mapping the outcome to a Msg
    /// (→ `Call`).
    | CallEcho of input: string

/// The toy typed-tree. Four constructors — text, a data binding (a key
/// resolved against the host's projected `HostBindingSources`), element
/// (a tag plus children), and an event wrapper that turns any subtree
/// into an interaction site. That is the whole language.
type ToyNode =
    | Text of string
    /// A data binding: at render time `key` is resolved against the
    /// host-projected `HostBindingSources` (Phase 264 read-side). The toy
    /// names only its own key — the seam knows nothing toy-specific.
    | Bind of key: string
    | Element of tag: string * children: ToyNode list
    | OnClick of event: ToyEvent * child: ToyNode

// ─── Read-side — resolve `Bind` against `HostBindingSources` ───────────
//
// The read-side half of the neutrality proof (Phase 264). `route` (in
// Binding.fs) binds the toy's ACTION vocabulary onto the four capability
// hooks; `resolve` binds the toy's DATA vocabulary onto the host's
// projected namespace. Both halves prove the same thing: the seam is
// renderer-neutral — a stranger tree language binds it without the seam
// knowing anything tree-specific.
//
// `resolve` is tier-neutral (no Feliz), so the .NET test runner resolves
// a tree against a CSR-projected and an SSR-projected namespace
// identically — one tree language, both projection paths.

/// Resolve every `Bind key` node against a host-projected
/// `HostBindingSources`, rewriting it to a `Text` node carrying the
/// resolved value's `string` form. `QueryResults` wins over `State` (the
/// shared resolution rule, via `HostBindingSources.tryResolve`); an
/// unbound key renders a stable `{unbound:key}` marker rather than
/// throwing, so a partial projection degrades visibly instead of
/// crashing the render.
let rec resolve (sources: HostBindingSources) (node: ToyNode) : ToyNode =
    match node with
    | Bind key ->
        match HostBindingSources.tryResolve key sources with
        | Some value -> Text(string value)
        | None -> Text $"{{unbound:{key}}}"
    | Text _ -> node
    | Element(tag, children) -> Element(tag, children |> List.map (resolve sources))
    | OnClick(event, child) -> OnClick(event, resolve sources child)

// ─── Client lowering — `ReactElement` (Fable surface) ─────────────────

let private elementOf (tag: string) : ReactElement list -> ReactElement =
    match tag with
    | "section" -> Html.section
    | "p" -> Html.p
    | "ul" -> Html.ul
    | "li" -> Html.li
    | "span" -> Html.span
    | "button" -> Html.button
    | _ -> Html.div

/// Lower a `ToyNode` to a `ReactElement`, routing every `OnClick`
/// through `onEvent`. The binding builds `onEvent` from the
/// `ClientHostCapabilities` bag, so the toy never sees the host directly
/// — it speaks only its own `ToyEvent` vocabulary.
let rec lower (onEvent: ToyEvent -> unit) (node: ToyNode) : ReactElement =
    match node with
    | Text s -> Html.text s
    // An unresolved binding — `resolve` rewrites `Bind` to `Text` before
    // lowering, so reaching this arm means the tree was lowered without a
    // projection; render the stable miss marker rather than throwing.
    | Bind key -> Html.text $"{{unbound:{key}}}"
    | Element(tag, children) -> elementOf tag (children |> List.map (lower onEvent))
    | OnClick(event, child) ->
        Html.span [ prop.onClick (fun _ -> onEvent event); prop.children [ lower onEvent child ] ]

// ─── Tier-neutral lowering — static HTML string ───────────────────────

let private escape (s: string) : string =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

/// A stable label for a toy event — the `data-toy-event` attribute on a
/// server-rendered fragment and the `Target` of the action descriptor.
let eventLabel (event: ToyEvent) : string =
    match event with
    | NavigateTo id -> $"navigate:{id}"
    | NotifyWith(_, _) -> "notify"
    | DispatchBump -> "dispatch:bump"
    | CallEcho _ -> "call:echo"

/// Lower a `ToyNode` to a static HTML string. No interactivity — a
/// server-rendered fragment is static — but each event site is recorded
/// as a `data-toy-event` attribute, so the toy's vocabulary survives the
/// round-trip through the fragment + live-frame seams without those
/// seams knowing anything toy-specific.
let rec lowerToHtml (node: ToyNode) : string =
    match node with
    | Text s -> escape s
    // Unresolved binding (see `lower`) — `resolve` rewrites it first.
    | Bind key -> escape $"{{unbound:{key}}}"
    | Element(tag, children) ->
        let inner = children |> List.map lowerToHtml |> String.concat ""
        $"<{tag}>{inner}</{tag}>"
    | OnClick(event, child) -> $"""<span data-toy-event="{escape (eventLabel event)}">{lowerToHtml child}</span>"""

// ─── Core lowering — `ActionDescriptor` ───────────────────────────────

/// Map a toy event to a host-neutral `ActionDescriptor` so a host can
/// gate it default-deny through `IActionAuthorizer` before routing it to
/// the matching capability. `scope` pins the action to a scope container
/// when the host knows it (the authorizer denies a cross-scope action
/// structurally). The `Kind` strings are the host-defined vocabulary the
/// authorizer's policy rules match against.
let toAction (scope: string option) (event: ToyEvent) : ActionDescriptor =
    match event with
    | NavigateTo id -> {
        Kind = "navigate"
        Target = id
        Scope = scope
      }
    | NotifyWith(_, _) -> {
        Kind = "notify"
        Target = "toast"
        Scope = scope
      }
    | DispatchBump -> {
        Kind = "dispatch"
        Target = "bump"
        Scope = scope
      }
    | CallEcho _ -> {
        Kind = "call"
        Target = "echo"
        Scope = scope
      }
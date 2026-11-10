// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BootDegradation

open System
open Feliz

// ─── Phase 121 — typed boot-degradation surface ──────────────────────
//
// The shell's boot loaders historically swallowed failures into
// benign-looking defaults (`loadMyTeams` → `[]`, `loadActiveTeam` →
// `None`, …), so a server outage or auth misfire at boot rendered a
// shell indistinguishable from a genuinely teamless user — no team
// switcher, no team-scoped modules, no log anywhere. Users file "my
// teams disappeared" tickets and the operator has nothing to go on.
//
// This module is the typed accumulator + standard banner for those
// failures: each failed boot load records a `BootDegradation` entry on
// the shell Model (deduped by `Source`), and the shell renders one
// dismissible banner listing the failed sources with per-source retry.
// A failed load must be distinguishable from empty data (GP 9); the
// banner renders nothing when no degradations exist, so a clean boot
// pays zero footprint (GP 13).
//
// Render-time failures are the sibling concern handled by the Phase
// 12c per-module error boundaries; this module covers *data*-load
// failures only.

/// One degraded boot load. Pure data — the shell maps `Source` back to
/// the matching loader `Cmd` when the user clicks Retry, so no
/// function values live on the Model (records stay comparable and the
/// type stays usable from any tier).
type BootDegradation = {
    /// The source's `BootSources` key. Dedup key, and the handle the
    /// shell's Retry resolves to the source's loader.
    Source: string
    /// Human-readable name for the banner row.
    Label: string
    /// Error detail (exception message / typed server error). Shown as
    /// the row's hover title, not inline — the banner stays one line.
    Error: string
    /// Whether the shell can re-run this load on demand. `false` for
    /// sources that retry themselves (the auth bridge's refresh
    /// interval) — the banner shows the entry without a Retry button.
    Retryable: bool
    OccurredAt: DateTime
}

/// Add with dedup-by-source — a repeated failure replaces its prior
/// entry (latest error message + timestamp win) rather than stacking
/// one row per attempt.
let add (entry: BootDegradation) (existing: BootDegradation list) : BootDegradation list =
    entry :: (existing |> List.filter (fun e -> e.Source <> entry.Source))

/// Remove the entry for `source`, if present. Called on load success
/// (the data arrived after all) and on retry dispatch (optimistic
/// clear — a re-failure re-adds via the failure message).
let remove (source: string) (existing: BootDegradation list) : BootDegradation list =
    existing |> List.filter (fun e -> e.Source <> source)

/// The display name for a boot source, in the caller's locale: the source's
/// row in `BootSources.all` names its catalog field. Only the label is
/// localised; the `Source` key the dedup and Retry run on is not. A source
/// the table does not hold falls through to `fallback` (the entry's own
/// `Label`).
let localisedLabel (msgs: BootSourceMessages) (source: string) (fallback: string) : string =
    BootSources.label msgs source fallback (BootSources.all<obj, obj> ())

/// Dismissible top-of-viewport banner listing every degraded source
/// with a per-source Retry affordance. Returns `Html.none` when the
/// list is empty (GP 13 — zero footprint on a clean boot).
///
/// Phase 444 — localised through `msgs`. This is a NEW entry point
/// rather than a parameter added to `banner`: `banner` is public
/// surface, and widening its arity would read as a removal in the
/// public-API baseline and break every caller. `banner` delegates here
/// with the built-in English catalog, so its behaviour is unchanged.
let bannerWith
    (msgs: BootDegradationMessages)
    (degradations: BootDegradation list)
    (onRetry: string -> unit)
    (onDismiss: unit -> unit)
    : ReactElement =
    if List.isEmpty degradations then
        Html.none
    else
        Html.div [
            prop.className
                "fixed top-0 inset-x-0 z-50 bg-amber-50 border-b border-amber-300 text-amber-900 text-sm shadow-sm"
            prop.role "alert"
            prop.children [
                Html.div [
                    prop.className "max-w-5xl mx-auto px-4 py-2 flex items-start gap-3"
                    prop.children [
                        Html.span [ prop.className "font-medium whitespace-nowrap"; prop.text msgs.Heading ]
                        Html.div [
                            prop.className "flex-1 flex flex-wrap items-center gap-x-4 gap-y-1"
                            prop.children (
                                degradations
                                |> List.map (fun d ->
                                    Html.span [
                                        prop.key d.Source
                                        prop.className "inline-flex items-center gap-2"
                                        prop.title d.Error
                                        prop.children [
                                            Html.span [ prop.text (localisedLabel msgs.Sources d.Source d.Label) ]
                                            if d.Retryable then
                                                Html.button [
                                                    prop.className "underline hover:no-underline font-medium"
                                                    prop.text msgs.Retry
                                                    prop.onClick (fun _ -> onRetry d.Source)
                                                ]
                                        ]
                                    ])
                            )
                        ]
                        Html.button [
                            prop.className "shrink-0 px-1 leading-none text-lg"
                            prop.ariaLabel msgs.Dismiss
                            prop.text "×"
                            prop.onClick (fun _ -> onDismiss ())
                        ]
                    ]
                ]
            ]
        ]

/// Back-compat entry point — the pre-444 signature, rendering the
/// built-in English catalog. Kept so existing callers (and the public
/// surface) are unchanged; the shell calls `bannerWith`.
let banner (degradations: BootDegradation list) (onRetry: string -> unit) (onDismiss: unit -> unit) : ReactElement =
    bannerWith MessageCatalog.english.BootDegradation degradations onRetry onDismiss
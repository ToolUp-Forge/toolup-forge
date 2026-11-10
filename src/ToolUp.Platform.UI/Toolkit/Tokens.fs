// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform

/// Design tokens - foundational values from the design system
module Tokens =
    // Phase 221: neutrals / surface / shape / status read the client-toolkit
    // theming tokens (defined in the canonical client-styling/tailwind/index.css
    // at current-look defaults). Override the token VALUES in a consumer :root to
    // re-skin; do not hardcode the literals back here.
    module Text =
        let primary = "text-[var(--text-strong)]"
        let secondary = "text-[var(--text)]"
        /// Hints, captions, empty-state prose and timestamps.
        let muted = "text-[var(--muted)]"
        let success = "text-[var(--pos)]"
        let danger = "text-[var(--neg)]"

    module Bg =
        let panel = "bg-bg-light"
        let card = "bg-[var(--surface)]"
        /// A recessed surface: table headers, read-only fields, hover rows.
        let subtle = "bg-[var(--surface-2)]"
        /// Status backgrounds, tinted from the status tokens so a re-skin of
        /// `--pos` / `--neg` re-tints them too.
        let success = "bg-[color-mix(in_srgb,var(--pos)_10%,transparent)]"
        let danger = "bg-[color-mix(in_srgb,var(--neg)_10%,transparent)]"

    module Border =
        let defaultValue = "border border-border"
        let panel = "rounded-[var(--radius)]"

    module Button =
        let primary =
            "bg-brand text-white px-12 py-4 font-medium uppercase rounded-[var(--radius)] hover:bg-brand-dark transition-colors"

        let secondary =
            "bg-transparent text-[var(--text-strong)] border border-border px-6 py-2 rounded-[var(--radius)] hover:bg-gray-100 transition-colors"

    module Colours =
        let brand = "bg-brand"
        let brandHover = "bg-brand-dark"
        let brandText = "text-white"
        let success = "text-[var(--pos)]"
        let error = "text-[var(--neg)]"
        let neutral = "text-[var(--text)]"

    module Spacing =
        let buttonPaddingX = "px-12"
        let buttonPaddingY = "py-4"
        let secondaryButtonPaddingX = "px-6"
        let secondaryButtonPaddingY = "py-2"

    module Typography =
        let buttonText = "font-medium uppercase"
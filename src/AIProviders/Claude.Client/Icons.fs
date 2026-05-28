// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Claude.Icons

open Fable.Core.JsInterop
open Fable.React
open ToolUp.Platform

// Anthropic Claude brand glyph. Lives with the provider companion so a
// deployment that omits `src/AIProviders/Claude/` doesn't carry the
// asset. Brand colours stay in the SVG source — `currentColor` is not
// used here so the logo doesn't pick up the sidebar's `text-brand`
// recolouring (Anthropic's mark should render as Anthropic's mark).

let private claudeSvg: obj = importDefault "./icons/claude.svg?react"

let claude: ReactElement = Icon.ofImport claudeSvg
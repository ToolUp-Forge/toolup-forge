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
    module Text =
        let primary = "text-gray-900"
        let secondary = "text-gray-600"

    module Bg =
        let panel = "bg-bg-light"
        let card = "bg-white"

    module Border =
        let defaultValue = "border border-border"
        let panel = "rounded-lg"

    module Button =
        let primary =
            "bg-brand text-white px-12 py-4 font-medium uppercase rounded-lg hover:bg-brand-dark transition-colors"

        let secondary =
            "bg-transparent text-gray-900 border border-border px-6 py-2 rounded-lg hover:bg-gray-100 transition-colors"

    module Colours =
        let brand = "bg-brand"
        let brandHover = "bg-brand-dark"
        let brandText = "text-white"
        let success = "text-green-600"
        let error = "text-red-600"
        let neutral = "text-gray-600"

    module Spacing =
        let buttonPaddingX = "px-12"
        let buttonPaddingY = "py-4"
        let secondaryButtonPaddingX = "px-6"
        let secondaryButtonPaddingY = "py-2"

    module Typography =
        let buttonText = "font-medium uppercase"
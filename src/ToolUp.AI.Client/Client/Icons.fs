// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Icons

open Fable.Core.JsInterop
open Fable.React
open ToolUp.Platform

let private aiAssistantSvg: obj = importDefault "./icons/ai-assistant.svg?react"

let aiAssistant: ReactElement = Icon.ofImport aiAssistantSvg

let private aiSettingsSvg: obj = importDefault "./icons/ai-settings.svg?react"

let aiSettings: ReactElement = Icon.ofImport aiSettingsSvg

let private chatSvg: obj = importDefault "./icons/chat.svg?react"

let chat: ReactElement = Icon.ofImport chatSvg
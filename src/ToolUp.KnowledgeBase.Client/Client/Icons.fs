// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.KnowledgeBase.Icons

open Fable.Core.JsInterop
open Fable.React
open ToolUp.Platform

let private knowledgeSvg: obj = importDefault "./icons/knowledge.svg?react"

let knowledge: ReactElement = Icon.ofImport knowledgeSvg

let private documentSvg: obj = importDefault "./icons/document.svg?react"

let document: ReactElement = Icon.ofImport documentSvg

let private noteSvg: obj = importDefault "./icons/note.svg?react"

let note: ReactElement = Icon.ofImport noteSvg

let private aiContextSvg: obj = importDefault "./icons/ai-context.svg?react"

let aiContext: ReactElement = Icon.ofImport aiContextSvg
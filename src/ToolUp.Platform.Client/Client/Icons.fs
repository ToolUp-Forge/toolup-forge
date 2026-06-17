// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Icons

open Fable.Core.JsInterop
open Fable.React

// ─── SDK built-in icons ───────────────────────────────────────────
//
// Icons consumed by SDK-built-in modules (FileManagerUI, TeamManagerUI,
// TeamConfigUI, WebhookAdminUI, HealthMonitorUI, UsageDashboard) plus
// a small set of generic fallbacks (target, arrow-upwards, interconnected,
// settings) preserved for apps that supplied them via `Configured*Mode`.
// SVGs live next to this file in `./icons/`. Each `?react` import is
// resolved by vite-plugin-svgr at bundle time.

let private uploadSvg: obj = importDefault "./icons/upload.svg?react"

let upload: ReactElement = Icon.ofImport uploadSvg

let private usersSvg: obj = importDefault "./icons/users.svg?react"

let users: ReactElement = Icon.ofImport usersSvg

let private teamConfigSvg: obj = importDefault "./icons/team-config.svg?react"

let teamConfig: ReactElement = Icon.ofImport teamConfigSvg

let private webhookSvg: obj = importDefault "./icons/webhook.svg?react"

let webhook: ReactElement = Icon.ofImport webhookSvg

let private healthSvg: obj = importDefault "./icons/health.svg?react"

let health: ReactElement = Icon.ofImport healthSvg

let private usageSvg: obj = importDefault "./icons/usage.svg?react"

let usage: ReactElement = Icon.ofImport usageSvg

let private settingsSvg: obj = importDefault "./icons/settings.svg?react"

let settings: ReactElement = Icon.ofImport settingsSvg

// Phase 171 — Home / Overview landing module.
let private homeSvg: obj = importDefault "./icons/home.svg?react"

let home: ReactElement = Icon.ofImport homeSvg

let private targetSvg: obj = importDefault "./icons/target.svg?react"

let target: ReactElement = Icon.ofImport targetSvg

let private arrowUpwardsSvg: obj = importDefault "./icons/arrow-upwards.svg?react"

let arrowUpwards: ReactElement = Icon.ofImport arrowUpwardsSvg

let private interconnectedSvg: obj =
    importDefault "./icons/interconnected.svg?react"

let interconnected: ReactElement = Icon.ofImport interconnectedSvg

let private lockSvg: obj = importDefault "./icons/lock.svg?react"

let lock: ReactElement = Icon.ofImport lockSvg
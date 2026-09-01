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

// The "go back" mark, carried by the Phase 567 product-area switcher
// ("Back to app") in `sidebarSections`. Authored as `arrow-upwards.svg`
// rotated a quarter turn — same two paths, same 1.75 stroke, same round
// caps — so the pair reads as one family rather than two arrows drawn by
// different hands.
let private arrowLeftSvg: obj = importDefault "./icons/arrow-left.svg?react"

let arrowLeft: ReactElement = Icon.ofImport arrowLeftSvg

let private interconnectedSvg: obj =
    importDefault "./icons/interconnected.svg?react"

let interconnected: ReactElement = Icon.ofImport interconnectedSvg

let private lockSvg: obj = importDefault "./icons/lock.svg?react"

let lock: ReactElement = Icon.ofImport lockSvg

// ─── Loading indicators ───────────────────────────────────────────
//
// Two animated "data loading" marks. `dataLoading` is the ToolUp brand
// mark (chevron + dot) that both spins (SMIL `<animateTransform
// type="rotate">`) and cycles its gradient pink → magenta → violet →
// blue (SMIL `<animate>` on the stops) — self-coloured, so it ignores
// the surrounding `currentColor` cascade. (The colour cycle alone read
// as too subtle to register as "working"; the rotation is the dominant
// signal, with the colour cycle kept underneath.) `spinner` is a
// conventional rotating arc that uses `currentColor` for deployments
// that prefer a neutral, theme-tinted indicator over the brand mark.
// Both animate via SMIL (not a CSS `<style>` block) so they survive the
// vite-plugin-svgr → SVGO pipeline intact.

let private dataLoadingSvg: obj = importDefault "./icons/data-loading.svg?react"

let dataLoading: ReactElement = Icon.ofImport dataLoadingSvg

let private spinnerSvg: obj = importDefault "./icons/spinner.svg?react"

let spinner: ReactElement = Icon.ofImport spinnerSvg
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Browser-availability gate: the rendering arms run only when a
/// Playwright-managed Chromium exists locally; on a checkout without
/// one they report Pending, not Failed — mirroring the live-API
/// env-gating convention in the AI provider test pack.
module ToolUp.Reporting.HtmlPdf.Tests.BrowserGate

open System
open System.IO
open Expecto

let private cacheRoots () = [
    match Environment.GetEnvironmentVariable "PLAYWRIGHT_BROWSERS_PATH" with
    | null
    | "" -> ()
    | custom -> custom

    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "ms-playwright")
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cache", "ms-playwright")
]

let chromiumAvailable: Lazy<bool> =
    lazy
        (cacheRoots ()
         |> List.exists (fun root ->
             Directory.Exists root
             && not (Directory.EnumerateDirectories(root, "chromium*") |> Seq.isEmpty)))

/// Wrap browser-dependent tests: pass through when Chromium is
/// installed; otherwise a single Pending entry names the group as
/// skipped (rather than silently absent from the report).
let gated (groupName: string) (browserTests: Test list) : Test =
    if chromiumAvailable.Value then
        testList groupName browserTests
    else
        testList groupName [
            ptestCase "skipped — Chromium not installed (run `pwsh playwright.ps1 install chromium`)"
            <| fun _ -> ()
        ]
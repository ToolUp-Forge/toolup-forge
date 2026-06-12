// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Reporting.HtmlPdf

open System

// ─── Phase 126 — HTML→PDF render options ─────────────────────────
//
// All immutable records (GP 5). The defaults are deliberately
// "print CSS wins": a template that declares `@page` size / margins
// renders exactly as its author intended; the option fields exist
// for templates that don't.

/// Per-edge page margins as CSS length strings (`"20mm"`, `"0.5in"`).
type PdfMargins = {
    Top: string
    Bottom: string
    Left: string
    Right: string
}

module PdfMargins =
    let uniform (value: string) : PdfMargins = {
        Top = value
        Bottom = value
        Left = value
        Right = value
    }

/// How the filled HTML is printed to PDF. Defaults defer to the
/// template's print CSS wherever the CSS speaks.
type PdfRenderOptions = {
    /// Paper format ("A4", "Letter", "A5", …). `None` lets the
    /// template's `@page` rule (or the browser default) decide.
    Format: string option
    /// Page margins. `None` lets `@page margin` (or the browser
    /// default) decide.
    Margins: PdfMargins option
    /// Print element backgrounds (colours, images). On by default —
    /// report templates almost always want their styling.
    PrintBackground: bool
    /// Render scale (0.1–2.0). `None` = 1.0.
    Scale: float option
    /// Browser header template (HTML; supports the component's
    /// `pageNumber` / `totalPages` / `date` / `title` span classes).
    /// Supplying either template turns the header/footer band on.
    HeaderTemplate: string option
    /// Browser footer template — same contract as `HeaderTemplate`.
    FooterTemplate: string option
    /// A template-declared `@page` size beats `Format` (default true —
    /// print-CSS-wins).
    PreferCssPageSize: bool
    /// Wait for network idle before printing — needed only when the
    /// template references external assets (images, webfonts) rather
    /// than inlined data URLs.
    WaitForNetworkIdle: bool
}

module PdfRenderOptions =
    let defaults = {
        Format = None
        Margins = None
        PrintBackground = true
        Scale = None
        HeaderTemplate = None
        FooterTemplate = None
        PreferCssPageSize = true
        WaitForNetworkIdle = false
    }

/// Renderer-level configuration: how the warm browser behaves plus
/// the per-render defaults.
type HtmlPdfRendererConfig = {
    RenderOptions: PdfRenderOptions
    /// Close the warm browser after this much idle time; the next
    /// render relaunches it. Launch is seconds-class, a warm render
    /// is milliseconds-class.
    BrowserIdleTimeout: TimeSpan
    /// Extra Chromium launch args. Containerised deployments running
    /// as root typically need `"--no-sandbox"` — see the README's
    /// deployment-image section before reaching for it.
    LaunchArgs: string list
}

module HtmlPdfRendererConfig =
    let defaults = {
        RenderOptions = PdfRenderOptions.defaults
        BrowserIdleTimeout = TimeSpan.FromMinutes 5.0
        LaunchArgs = []
    }
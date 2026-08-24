// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Enterprise AG Grid + AG Chart prompt-builder sub-companion (Phase 12e).
///
/// Mirrors `AgChartAICookbook` but reads the Enterprise `COOKBOOK.md` and adds
/// the Enterprise series shortcuts (Sankey / Sunburst / Treemap / OHLC /
/// Candlestick / Sparkline) plus Enterprise grid features (Set Filter /
/// Master-Detail / Excel Export / Status Bar / Sidebar). Loaded ONLY when a
/// deployment composes it — a Community-only deployment never references this
/// companion, so it neither pays the extra token cost nor exposes
/// un-licensable feature names in the prompt (the licensing-boundary intent
/// extended into the prompt-content layer).
///
/// Reuses `AgChartAICookbook`'s header-keyed parser + bounded builder so the
/// extraction rules stay in one place.
module AgGridEnterpriseAICookbook

open System
open System.IO
open ToolUp.Platform
open ToolUp.AI.SystemPromptBuilder

/// The heading the extracted Enterprise guidance is prepended under.
[<Literal>]
let Heading = "# Authoring AG Charts and Grids in F# (Enterprise)"

/// Same H2 sections as the Community builder, lifted from the Enterprise book.
let extractedHeaders = AgChartAICookbook.extractedHeaders

/// The name this companion's cookbook is copied and packed under — distinct from
/// `AgChartAICookbook.CookbookFileName` (see the rationale there). A consumer
/// composing both builders holds both files in one output directory, so the two
/// names must never converge.
[<Literal>]
let CookbookFileName = "COOKBOOK.Enterprise.md"

/// Candidate Enterprise-cookbook paths: the `TOOLUP_ENTERPRISE_COOKBOOK_PATH`
/// override (distinct from the Community override so both can be set), the
/// assembly-relative copy shipped in the companion output, then a dev
/// repo-relative path back to the source Enterprise cookbook.
let candidatePaths (fileName: string) : string list =
    let asmDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    [
        match Environment.GetEnvironmentVariable ConfigKeys.Names.enterpriseCookbookPath with
        | null
        | "" -> ()
        | p -> if Directory.Exists p then Path.Combine(p, fileName) else p

        Path.Combine(asmDir, fileName)
        // dev: bin/Debug/net10.0 -> repo src/AgGridEnterprise/COOKBOOK.md
        Path.Combine(asmDir, "..", "..", "..", "..", "AgGridEnterprise", "COOKBOOK.md")
    ]

/// Build the Enterprise builder from an explicit cookbook path (test seam).
let buildFromFile (cookbookPath: string) (logger: ILogger option) : SystemPromptBuilder =
    AgChartAICookbook.buildFromFile Heading extractedHeaders cookbookPath logger

/// The composable Enterprise builder. Resolves the Enterprise cookbook path
/// automatically; compose alongside the Community builder in `composeWithAI`.
let systemPromptBuilder (logger: ILogger option) : SystemPromptBuilder =
    match candidatePaths CookbookFileName |> List.tryFind File.Exists with
    | Some path -> buildFromFile path logger
    | None ->
        logger
        |> Option.iter (fun l ->
            l.Warn
                $"AgGridEnterpriseAICookbook: {CookbookFileName} not found on any candidate path; Enterprise chart-authoring guidance disabled.")

        fun _ -> async { return "" }
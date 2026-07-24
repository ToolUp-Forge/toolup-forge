// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Community AG Chart + AG Grid prompt-builder sub-companion (Phase 12e).
///
/// Exposes a `SystemPromptBuilder` a deployment composes into `composeWithAI`'s
/// prompt list so the in-app AI assistant can author charts and grids in F#
/// against the typed bindings. At construction it reads the Community
/// `COOKBOOK.md`, extracts its critical-constraints + shortest-possible-chart
/// sections via a header-keyed parse, and prepends them under an authoring
/// heading — bounded to ~600 tokens for predictable per-conversation cost.
///
/// Opt-in only: `ToolUp.AI` has no dependency on the UI bindings, so nothing
/// auto-injects this. The deployment composition root is the only place that
/// sees both AI and the cookbook. Mirrors `KnowledgeBase.Server`'s
/// `standingContextBuilder`.
module AgChartAICookbook

open System
open System.IO
open System.Text
open ToolUp.Platform
open ToolUp.AI.SystemPromptBuilder

/// The heading the extracted guidance is prepended under.
[<Literal>]
let Heading = "# Authoring AG Charts and Grids in F#"

/// The H2 sections lifted from the cookbook into the prompt.
let extractedHeaders = [ "## Critical constraints"; "## The shortest possible chart" ]

/// Extract the named H2 sections (header line + body until the next H2) from a
/// markdown document, in document order. Header match is exact on the trimmed
/// line. H3+ headings and code-fence lines never match — they don't start with
/// the literal `"## "` prefix — so a recipe's `### ` sub-heading or an fsharp
/// snippet can't accidentally close or open a captured section.
let extractSections (headers: string list) (markdown: string) : string =
    let lines = markdown.Replace("\r\n", "\n").Split('\n')
    let sb = StringBuilder()
    let mutable capturing = false

    for line in lines do
        if line.StartsWith "## " then
            capturing <- List.contains (line.Trim()) headers

            if capturing then
                sb.AppendLine line |> ignore
        elif capturing then
            sb.AppendLine line |> ignore

    sb.ToString().Trim()

/// ~600-token bound (~4 chars/token). Truncate on a line boundary when over,
/// so the prompt stays predictable and never balloons per conversation.
let boundToTokens (maxTokens: int) (text: string) : string =
    let maxChars = maxTokens * 4

    if text.Length <= maxChars then
        text
    else
        let cut = text.Substring(0, maxChars)
        let lastNl = cut.LastIndexOf '\n'

        (if lastNl > 0 then cut.Substring(0, lastNl) else cut)
        + "\n\n… (guidance truncated to bound token cost)"

/// Build a `SystemPromptBuilder` from an explicit cookbook file path. Reads and
/// extracts ONCE at construction; the returned builder returns the cached
/// guidance for every turn. On a read failure or an empty extraction: a single
/// startup `Warn` and a no-op builder (empty string), so a deployment that
/// can't ship its `COOKBOOK.md` degrades silently rather than crashing.
let buildFromFile
    (heading: string)
    (headers: string list)
    (cookbookPath: string)
    (logger: ILogger option)
    : SystemPromptBuilder =
    let content =
        try
            let md = File.ReadAllText cookbookPath
            let sections = extractSections headers md

            if String.IsNullOrWhiteSpace sections then
                logger
                |> Option.iter (fun l ->
                    l.Warn(
                        sprintf
                            "AgChartAICookbook: no recognised sections in %s; chart-authoring guidance disabled."
                            cookbookPath
                    ))

                ""
            else
                heading + "\n\n" + boundToTokens 600 sections
        with ex ->
            logger
            |> Option.iter (fun l ->
                l.Warn(
                    sprintf
                        "AgChartAICookbook: could not read %s (%s); chart-authoring guidance disabled."
                        cookbookPath
                        ex.Message
                ))

            ""

    fun (_: PromptContext) -> async { return content }

/// Candidate cookbook paths, most-specific first: the `TOOLUP_COOKBOOK_PATH`
/// override (a file, or a directory holding `COOKBOOK.md`), the assembly-
/// relative copy shipped in the companion output, then a dev repo-relative
/// path from the build output back to the source cookbook.
let candidatePaths (fileName: string) : string list =
    let asmDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    [
        match Environment.GetEnvironmentVariable "TOOLUP_COOKBOOK_PATH" with
        | null
        | "" -> ()
        | p -> if Directory.Exists p then Path.Combine(p, fileName) else p

        Path.Combine(asmDir, fileName)
        // dev: bin/Debug/net10.0 -> repo src/ToolUp.Platform.Client/Client/UI/COOKBOOK.md
        Path.Combine(asmDir, "..", "..", "..", "..", "ToolUp.Platform.Client", "Client", "UI", "COOKBOOK.md")
    ]

/// The composable Community builder. Resolves the cookbook path automatically;
/// compose the result into `composeWithAI`'s prompt list.
let systemPromptBuilder (logger: ILogger option) : SystemPromptBuilder =
    match candidatePaths "COOKBOOK.md" |> List.tryFind File.Exists with
    | Some path -> buildFromFile Heading extractedHeaders path logger
    | None ->
        logger
        |> Option.iter (fun l ->
            l.Warn "AgChartAICookbook: COOKBOOK.md not found on any candidate path; chart-authoring guidance disabled.")

        fun _ -> async { return "" }
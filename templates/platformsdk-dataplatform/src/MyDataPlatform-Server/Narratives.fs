module MyDataPlatform.Server.Narratives

open ToolUp.Platform
open ToolUp.Facts
open ToolUp.AI.GroundedNarrativeRun

// ─── The day's report: model-written, every number a Fact ─────────
//
// The run offers the model only the fact tools. Its answer is checked
// before it is published: a figure that is not a reference to a Fact the
// scope holds, or that misstates one, refuses the whole report and the
// refusal is logged. The title and headings are authored here; the model
// writes the paragraphs.

/// The run key: what a delivery triggers and what the report pages read.
[<Literal>]
let DailyReport = "daily-trading-report"

/// The tag the run stamps on every narrative it publishes, so the pages can
/// select this run's reports in the narrative store.
[<Literal>]
let DailyReportTag = "daily-trading-report"

let dailyReport: GroundedNarrativeDefinition =
    GroundedNarrativeDefinition.create
        DailyReport
        "Daily trading report"
        DailyTrade.Server.ModuleName
        "report-narrator"
        "Daily trading report"
        "Write the day's trading report from the trading facts: what happened by site, what drove it, and what deserves a closer look. Quote every figure as the fact it is."
        [
            {
                Id = "headline"
                Heading = "Headline by site"
                Guidance = "Orders, revenue and average order value per site."
            }
            {
                Id = "drivers"
                Heading = "Channel drivers"
                Guidance = "Which channels moved revenue, up or down."
            }
            {
                Id = "questions"
                Heading = "Questions for a closer look"
                Guidance = "What the figures raise that deserves a closer look."
            }
        ]
    |> GroundedNarrativeDefinition.dependingOn [ "orders"; "revenue"; "aov"; "shoppers" ]
    |> GroundedNarrativeDefinition.withTags [ DailyReportTag ]

/// Register the runs over the fact tier's tools.
let compose (app: ServerApp) : ServerApp =
    app
    |> GroundedNarratives.compose (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools) [ dailyReport ]
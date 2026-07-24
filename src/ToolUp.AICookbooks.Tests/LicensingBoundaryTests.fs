// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AICookbooks.Tests.LicensingBoundaryTests

open System.IO
open Expecto
open ToolUp.AI.SystemPromptBuilder

// The builders ignore the PromptContext (they return content read once at
// construction), so an unchecked default is a safe test seam that avoids
// constructing the heavy PromptContext record.
let private runBuilder (b: SystemPromptBuilder) : string =
    b (Unchecked.defaultof<PromptContext>) |> Async.RunSynchronously

/// Enterprise feature names that must NOT appear in a Community-only prompt.
let private enterpriseFeatureNames = [
    "Set Filter"
    "Master/Detail"
    "MasterDetail"
    "createRangeChart"
    "Excel Export"
    "excelStyles"
    "Sparkline"
    "Sankey"
    "Sunburst"
    "Treemap"
    "Candlestick"
    "SSRM"
    "Status Bar"
]

let private communityCookbook () =
    AgChartAICookbook.candidatePaths "COOKBOOK.md" |> List.tryFind File.Exists

let private enterpriseCookbook () =
    AgGridEnterpriseAICookbook.candidatePaths "COOKBOOK.md"
    |> List.tryFind File.Exists

let tests =
    testList "AICookbooks licensing boundary" [
        test "community cookbook resolves on a candidate path" {
            Expect.isSome (communityCookbook ()) "Community COOKBOOK.md should resolve"
        }

        test "enterprise cookbook resolves on a candidate path" {
            Expect.isSome (enterpriseCookbook ()) "Enterprise COOKBOOK.md should resolve"
        }

        test "community builder produces authoring guidance" {
            let path = (communityCookbook ()).Value

            let prompt =
                runBuilder (
                    AgChartAICookbook.buildFromFile
                        AgChartAICookbook.Heading
                        AgChartAICookbook.extractedHeaders
                        path
                        None
                )

            Expect.stringContains prompt AgChartAICookbook.Heading "prompt carries the authoring heading"
            Expect.stringContains prompt "Critical constraints" "prompt carries the constraints section"
            Expect.stringContains prompt "shortest possible chart" "prompt carries the shortest-possible section"
        }

        test "community-only prompt leaks zero Enterprise feature names" {
            let path = (communityCookbook ()).Value

            let prompt =
                (runBuilder (
                    AgChartAICookbook.buildFromFile
                        AgChartAICookbook.Heading
                        AgChartAICookbook.extractedHeaders
                        path
                        None
                ))
                    .ToLowerInvariant()

            for name in enterpriseFeatureNames do
                Expect.isFalse
                    (prompt.Contains(name.ToLowerInvariant()))
                    (sprintf "Community prompt must not mention the Enterprise feature '%s'" name)
        }

        test "community prompt stays within the ~600-token bound" {
            let path = (communityCookbook ()).Value

            let prompt =
                runBuilder (
                    AgChartAICookbook.buildFromFile
                        AgChartAICookbook.Heading
                        AgChartAICookbook.extractedHeaders
                        path
                        None
                )
            // ~600 tokens at ~4 chars/token, plus the heading + a small margin.
            Expect.isLessThan
                prompt.Length
                (600 * 4 + AgChartAICookbook.Heading.Length + 80)
                "prompt bounded to ~600 tokens"
        }

        test "enterprise builder adds an Enterprise section" {
            let path = (enterpriseCookbook ()).Value
            let prompt = runBuilder (AgGridEnterpriseAICookbook.buildFromFile path None)
            Expect.stringContains prompt AgGridEnterpriseAICookbook.Heading "enterprise prompt carries its own heading"
            // The Enterprise section legitimately names Enterprise features.
            let mentionsEnterprise =
                let lower = prompt.ToLowerInvariant()

                [ "enterprise"; "sparkline"; "sankey"; "set filter" ]
                |> List.exists lower.Contains

            Expect.isTrue mentionsEnterprise "enterprise prompt names Enterprise features"
        }

        test "missing cookbook degrades to a silent no-op, not a throw" {
            let bogus = Path.Combine(Path.GetTempPath(), "toolup-no-such-cookbook.md")

            let prompt =
                runBuilder (
                    AgChartAICookbook.buildFromFile
                        AgChartAICookbook.Heading
                        AgChartAICookbook.extractedHeaders
                        bogus
                        None
                )

            Expect.equal prompt "" "unreadable cookbook yields an empty no-op prompt"
        }

        test "composing both builders yields two distinct sections" {
            let cp = (communityCookbook ()).Value
            let ep = (enterpriseCookbook ()).Value

            let community =
                runBuilder (
                    AgChartAICookbook.buildFromFile AgChartAICookbook.Heading AgChartAICookbook.extractedHeaders cp None
                )

            let enterprise = runBuilder (AgGridEnterpriseAICookbook.buildFromFile ep None)
            Expect.isGreaterThan community.Length 0 "community section present"
            Expect.isGreaterThan enterprise.Length 0 "enterprise section present"
            Expect.notEqual community enterprise "the two sections are distinct"
        }
    ]
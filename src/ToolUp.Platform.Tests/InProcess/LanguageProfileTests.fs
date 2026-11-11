// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.LanguageProfileTests

// ─── The assistant's language profile ────────────────────────────────
//
// A deployment registers the Fuaran-UI subset its assistant may compose.
// What this pack pins:
//
//   1. **Registration reads the published schema.** An unknown kind, field or
//      node property refuses the registration by name, and so does a field
//      that carries rows inline.
//   2. **The fragment is rendered once** and served unchanged on every turn;
//      a deployment with no profile gets the prompt it got before, byte for
//      byte, and the boot log reports the fragment's size.
//   3. **The grammar.** A model-composed block naming a kind outside the
//      profile, a field it does not admit, or a chart or grid carrying rows
//      inline is refused, naming the node, the kind and the field.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Scope
open ToolUp.AI
open ToolUp.AI.SystemPromptBuilder
open ToolUp.Facts.Presentation

/// The registration the migration note shows: a pilot deployment's
/// summary card beside a chart or a table.
let private pilotProfile: LanguageProfile =
    LanguageProfile.named "pilot"
    |> LanguageProfile.withKind "Box" [ "children"; "heading"; "layout"; "role" ]
    |> LanguageProfile.withKind "Heading" [ "level"; "text" ]
    |> LanguageProfile.withKind "Markdown" [ "text" ]
    |> LanguageProfile.withKind "Callout" [ "body"; "heading"; "tone" ]
    |> LanguageProfile.withKind "Chart" [ "kind"; "source"; "title"; "xField"; "yFields" ]
    |> LanguageProfile.withKind "DataGrid" [ "columns"; "rowKeyField"; "source" ]

let private registered (profile: LanguageProfile) : RegisteredLanguageProfile =
    match LanguageProfile.register RenderTools.languageSchema profile with
    | Ok r -> r
    | Error refusals -> failwithf "the profile did not register: %A" refusals

let private refusalsOf (profile: LanguageProfile) : LanguageProfileRefusal list =
    match LanguageProfile.register RenderTools.languageSchema profile with
    | Ok _ -> failwith "the profile registered, and should have been refused"
    | Error refusals -> refusals

/// The presentation requests the conversation holds, by key.
let private requests: Map<string, PresentationRequest> =
    Map.ofList [
        "revenue-by-day", FactRenderToolTests.RenderFixtures.trendRequest
        "revenue-by-site", FactRenderToolTests.RenderFixtures.trendRequest
    ]

let private box (children: string list) =
    sprintf
        """{"id":"card","kind":{"$type":"Box","children":[%s],"layout":{"$type":"Auto"},"role":"Group"}}"""
        (String.concat "," children)

let private heading =
    """{"id":"title","kind":{"$type":"Heading","level":3,"text":"Revenue this month"}}"""

let private chartWith (source: string) =
    sprintf
        """{"id":"trend","kind":{"$type":"Chart","kind":"Line","source":%s,"xField":"day","yFields":["revenue"]}}"""
        source

let private queryChart = chartWith """{"$type":"Query","name":"revenue-by-day"}"""

let private grid (extra: string) =
    sprintf
        """{"id":"sites","kind":{"$type":"DataGrid","columns":[{"field":"site","kind":{"$type":"Text"},"label":"Site"}],"rowKeyField":"site","source":{"$type":"Query","name":"revenue-by-site"}%s}}"""
        extra

let private refusedBlock (profile: RegisteredLanguageProfile) (json: string) : ComposedBlockRefusal list =
    match RenderTools.validateComposedBlock profile requests json with
    | Ok _ -> failwithf "the block was admitted, and should have been refused: %s" json
    | Error refusals -> refusals

let private context: PromptContext = {
    Access = AccessContexts.forSubject (AuthenticatedUser "u")
    ActiveModule = None
    ActivePage = None
    ActivePageNarrative = None
    ModuleContexts = Map.empty
    CurrentMessage = Some "How did revenue move this month?"
    ConversationHistory = []
    RetrievalFilters = None
    RetrievedSources = ref []
    ShortCircuit = ref None
    PlannedAnswerId = ref None
    Scope = ResolvedScope.anonymous
    Services = PromptContext.noServices
}

let private capturingLogger () =
    let lines = ResizeArray<string>()

    let logger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info m = lines.Add m
            member _.Warn m = lines.Add m
            member _.Error(m, _) = lines.Add m
        }

    logger, lines

let registrationTests =
    testList "Language profile registration" [

        test "the schema is the vocabulary the pinned Fuaran.UI package publishes" {
            let schema = RenderTools.languageSchema

            Expect.equal
                (schema.Kinds |> Map.keys |> Set.ofSeq)
                (Set.ofList Fuaran.UI.Generated.allWireTags)
                "every wire kind, by its discriminator"

            Expect.isTrue (schema.Kinds["DataGrid"].Contains "staticRows") "a kind's wire fields"
            Expect.equal (schema.Categories.TryFind "Chart") (Some "Visualisation") "a kind's category"
            Expect.isTrue (schema.NodeProperties.Contains "style") "the node properties"
        }


        test "a profile of known kinds and fields registers" {
            let r = registered pilotProfile
            Expect.equal r.Profile.Kinds.Count 6 "every kind is admitted"
            Expect.isGreaterThan r.FragmentBytes 0 "the fragment is rendered at registration"
        }

        test "a kind the language does not have is refused by name" {
            let refusals =
                refusalsOf (pilotProfile |> LanguageProfile.withKind "Hologram" [ "beam" ])

            Expect.equal refusals [ LanguageProfileRefusal.UnknownKind "Hologram" ] "the kind is named"

            Expect.equal
                (LanguageProfileRefusal.describe refusals.Head)
                "the language has no kind 'Hologram'"
                "the sentence names it"
        }

        test "a field the kind does not have is refused by name" {
            let refusals =
                refusalsOf (pilotProfile |> LanguageProfile.withKind "Chart" [ "rows"; "colour" ])

            Expect.equal
                refusals
                [
                    LanguageProfileRefusal.UnknownField("Chart", "colour")
                    LanguageProfileRefusal.UnknownField("Chart", "rows")
                ]
                "each field is named with its kind"
        }

        test "a field that carries rows inline is refused" {
            let refusals =
                refusalsOf (pilotProfile |> LanguageProfile.withKind "DataGrid" [ "staticRows" ])

            Expect.equal refusals [ LanguageProfileRefusal.InlineRowsField("DataGrid", "staticRows") ] "named"
        }

        test "an unknown node property, an empty name and an empty profile are refused" {
            Expect.equal
                (refusalsOf (pilotProfile |> LanguageProfile.withNodeProperties [ "style"; "glow" ]))
                [ LanguageProfileRefusal.UnknownNodeProperty "glow" ]
                "the unknown property is named; style is known"

            Expect.equal
                (refusalsOf (LanguageProfile.named " "))
                [ LanguageProfileRefusal.NoName; LanguageProfileRefusal.NoKinds ]
                "both are reported"
        }
    ]

let fragmentTests =
    testList "Language profile fragment and prompt" [

        test "the fragment names every admitted kind and field, and the request rule" {
            let fragment = (registered pilotProfile).Fragment

            Expect.stringStarts fragment "## Composing a block (language profile 'pilot')" "headed by the profile"

            Expect.stringContains
                fragment
                "- `Chart` (Visualisation): `kind`, `source`, `title`, `xField`, `yFields`; `source` draws from a request"
                "a kind's line lists its fields and its request slot"

            Expect.stringContains fragment "- `Markdown` (Display): `text`" "a display kind"
            Expect.stringContains fragment "{\"$type\":\"Query\",\"name\":\"<request key>\"}" "the request form"
            Expect.isFalse (fragment.Contains "`Metric`") "a kind outside the profile is not mentioned"
        }

        test "a profile with no data-bearing kind carries no request rule" {
            let fragment =
                (registered (LanguageProfile.named "words" |> LanguageProfile.withKind "Markdown" [ "text" ])).Fragment

            Expect.isFalse (fragment.Contains "Query") "nothing to draw, so nothing to say about requests"
        }

        testAsync "a deployment with no profile gets the same prompt as before" {
            let platform = SystemPromptBuilder.fromStatic "You are the analysts' assistant."
            let! before = SystemPromptBuilder.compose [ platform; SystemPromptBuilder.activeModuleContext ] context

            let! after =
                SystemPromptBuilder.compose
                    [
                        platform
                        SystemPromptBuilder.activeModuleContext
                        SystemPromptBuilder.promptFragments None []
                    ]
                    context

            Expect.equal after before "byte-identical"
        }

        testAsync "a registered profile adds its one cached fragment, and boot reports its size" {
            let profile = registered pilotProfile
            let logger, lines = capturingLogger ()

            let builder =
                SystemPromptBuilder.promptFragments (Some logger) [ PromptFragment.ofLanguageProfile profile ]

            Expect.equal
                (List.ofSeq lines)
                [
                    sprintf "Language profile 'pilot': 6 kind(s), prompt fragment %d bytes." profile.FragmentBytes
                ]
                "construction logs the size once"

            let! first = builder context
            let! second = builder context
            Expect.isTrue (Object.ReferenceEquals(first, second)) "the same string every turn"
            Expect.equal first profile.Fragment "the fragment registration rendered"
            Expect.equal lines.Count 1 "a turn logs nothing"

            let! prompt =
                SystemPromptBuilder.compose [ SystemPromptBuilder.fromStatic "You are the assistant."; builder ] context

            Expect.equal prompt ("You are the assistant.\n\n" + profile.Fragment) "appended as one layer"
        }

        testAsync "a deployment with both profiles gets both fragments, in order, and boot reports each" {
            let language = registered pilotProfile

            let pipeline =
                match ToolUp.Facts.PipelineProfile.register ToolUp.Facts.PipelineProfile.defaultProfile with
                | Ok r -> r
                | Error refusals -> failwithf "the default pipeline profile did not register: %A" refusals

            let logger, lines = capturingLogger ()

            let builder =
                SystemPromptBuilder.promptFragments (Some logger) [
                    PromptFragment.ofLanguageProfile language
                    PromptFragment.create pipeline.Description pipeline.Fragment
                ]

            Expect.equal
                (List.ofSeq lines)
                [
                    sprintf "Language profile 'pilot': 6 kind(s), prompt fragment %d bytes." language.FragmentBytes
                    sprintf "Pipeline profile 'default': 7 verb(s), prompt fragment %d bytes." pipeline.FragmentBytes
                ]
                "one size line per fragment"

            let! prompt =
                SystemPromptBuilder.compose [ SystemPromptBuilder.fromStatic "You are the assistant."; builder ] context

            Expect.equal
                prompt
                ("You are the assistant.\n\n" + language.Fragment + "\n\n" + pipeline.Fragment)
                "both fragments, as one layer, language first"
        }
    ]

let grammarTests =
    testList "Language profile grammar" [

        test "a block of admitted kinds drawing from requests is admitted" {
            match
                RenderTools.validateComposedBlock
                    (registered pilotProfile)
                    requests
                    (box [ heading; queryChart; grid "" ])
            with
            | Ok node -> Expect.equal node.Id "card" "the decoded block"
            | Error refusals -> failwithf "refused: %A" refusals
        }

        test "an unregistered kind is refused" {
            let narrow =
                registered (
                    LanguageProfile.named "charts"
                    |> LanguageProfile.withKind "Box" [ "children"; "layout"; "role" ]
                    |> LanguageProfile.withKind "Chart" [ "kind"; "source"; "xField"; "yFields" ]
                )

            let refusals = refusedBlock narrow (box [ heading; queryChart ])

            Expect.equal refusals [ ComposedBlockRefusal.KindNotInProfile("title", "Heading") ] "named by node and kind"

            Expect.equal
                (ComposedBlockRefusal.describe refusals.Head)
                "node 'title' is a 'Heading', which the profile does not admit"
                "the sentence"
        }

        test "inline chart rows are refused, naming the kind and the field" {
            let profile = registered pilotProfile

            for source in
                [
                    """[{"day":"2026-10-01","revenue":1200}]"""
                    """{"$type":"Static","value":[{"day":"2026-10-01","revenue":1200}]}"""
                    """{"$type":"Query"}"""
                ] do
                Expect.equal
                    (refusedBlock profile (box [ chartWith source ]))
                    [ ComposedBlockRefusal.InlineRows("trend", "Chart", "source") ]
                    (sprintf "refused: %s" source)
        }

        test "a grid's static rows are refused even beside a request source" {
            let refusals =
                refusedBlock (registered pilotProfile) (box [ grid ""","staticRows":[{"site":"s1"}]""" ])

            Expect.contains
                refusals
                (ComposedBlockRefusal.InlineRows("sites", "DataGrid", "staticRows"))
                "the static rows are named"
        }

        test "a request the conversation does not hold is refused" {
            Expect.equal
                (refusedBlock (registered pilotProfile) (box [ chartWith """{"$type":"Query","name":"made-up"}""" ]))
                [ ComposedBlockRefusal.UnknownRequest("trend", "Chart", "made-up") ]
                "named"
        }

        test "a field or node property outside the profile is refused, though the decoder would drop it" {
            let withTitleText =
                """{"id":"title","kind":{"$type":"Heading","level":3,"text":"Revenue","variant":"Display"},"style":{}}"""

            Expect.equal
                (refusedBlock (registered pilotProfile) (box [ withTitleText ]))
                [
                    ComposedBlockRefusal.FieldNotInProfile("title", "Heading", "variant")
                    ComposedBlockRefusal.NodePropertyNotInProfile("title", "style")
                ]
                "both are named"
        }

        test "a block that is not JSON, or not a node, is refused" {
            let profile = registered pilotProfile

            Expect.equal
                (refusedBlock profile "{ not json")
                [ ComposedBlockRefusal.Malformed "the block is not JSON" ]
                "not JSON"

            Expect.equal
                (refusedBlock profile """{"rows":[]}""")
                [ ComposedBlockRefusal.Malformed "the block is not a node" ]
                "not a node"
        }

        test "a block the decoder refuses is refused with its reason" {
            let noRole =
                """{"id":"card","kind":{"$type":"Box","children":[],"layout":{"$type":"Auto"}}}"""

            match refusedBlock (registered pilotProfile) noRole with
            | [ ComposedBlockRefusal.Decode reason ] ->
                Expect.stringContains reason "role" "the decoder names the field"
            | other -> failwithf "expected one decode refusal, got %A" other
        }
    ]

let tests =
    testList "Language profile" [ registrationTests; fragmentTests; grammarTests ]
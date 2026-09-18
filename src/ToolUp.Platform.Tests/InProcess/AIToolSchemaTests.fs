module ToolUp.Platform.Tests.InProcess.AIToolSchemaTests

open System
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.ToolHelpers

// ─── Phase 508 — rich AI tool parameter schemas ──────────────────────
//
// The phase lifts one bottleneck: every shipped provider accepts full
// JSON Schema for a tool's parameters, and the registry was emitting a
// flat type name and a description, so a structured argument had to
// arrive as a string and be hand-parsed. What the pack has to hold is
// therefore three properties, not five tasks:
//
//   * **Flat declarations do not move.** GP 11 is the whole licence for
//     shipping this into a released surface, so the pin is the pre-508
//     emission formula written out independently below and asserted byte
//     for byte — not a recorded snapshot of whatever the current code
//     happens to produce, which would ratify a regression as readily as
//     it would catch one.
//
//   * **A rich declaration reaches the wire intact, escaped.** Nested
//     member names and enum values are new places a quote or a backslash
//     can terminate a JSON string early; the flat path has always had to
//     handle that for three fields, and this extends it to arbitrarily
//     many. The check is a property over adversarial strings, because
//     examples only cover the metacharacters somebody thought of.
//
//   * **The declaration is usable at BOTH ends.** A schema that only
//     describes the call is half a feature: the executor still walks the
//     document by hand. So the decode helper is held to the same
//     declaration, and its refusals have to name the path that was
//     wrong.

// ─── Fixtures ────────────────────────────────────────────────────────

let private defWith (name: string) (parameters: ToolParameterSchema list) : AIToolDefinition = {
    Name = name
    Description = "schema test tool"
    Parameters = parameters
    SourceModule = "test"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = UndeclaredEffects
}

let private flat (name: string) (typeName: string) (description: string) (required: bool) : ToolParameterSchema = {
    Name = name
    Type = typeName
    Description = description
    Required = required
    Default = None
}

/// The pre-508 emission formula, written out here independently of the
/// implementation. This is the GP-11 oracle: if `toProviderDef` ever
/// changes what a FLAT declaration produces, this disagrees, and a
/// consumer's tool definitions would have changed under them on an SDK
/// upgrade.
let private pre508InputSchema (def: AIToolDefinition) : string =
    let escape (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

    let properties =
        def.Parameters
        |> List.map (fun p ->
            "\""
            + escape p.Name
            + "\":{\"type\":\""
            + escape p.Type
            + "\",\"description\":\""
            + escape p.Description
            + "\"}")
        |> String.concat ","

    let required =
        def.Parameters
        |> List.filter _.Required
        |> List.map (fun p -> "\"" + escape p.Name + "\"")
        |> String.concat ","

    "{\"type\":\"object\",\"properties\":{"
    + properties
    + "},\"required\":["
    + required
    + "]}"

/// The canonical rich declaration this phase exists for: a nested object
/// and an array of enum values, the two shapes the acceptance names.
let private richParameters = [
    flat "dataset" "string" "Dataset id." true
    ToolSchema.parameter
        "filter"
        "Row filter."
        true
        (ToolSchema.objectOf [
            ToolSchema.field "metric" "Metric to compute." true (ToolSchema.enumOf [ "revenue"; "units"; "margin" ])
            ToolSchema.field "weeks" "Weeks of history." false ToolSchema.integer
            ToolSchema.field
                "region"
                "Optional region restriction."
                false
                (ToolSchema.objectOf [
                    ToolSchema.field "country" "ISO-3166 alpha-2." true ToolSchema.text
                    ToolSchema.field "includeSubregions" "" false ToolSchema.boolean
                ])
        ])
    ToolSchema.parameter
        "units"
        "Units to report in."
        false
        (ToolSchema.arrayOf (ToolSchema.enumOf [ "metric"; "imperial" ]))
]

let private richSchemaNode =
    ToolSchema.objectOf [
        ToolSchema.field "metric" "Metric to compute." true (ToolSchema.enumOf [ "revenue"; "units"; "margin" ])
        ToolSchema.field "weeks" "Weeks of history." false ToolSchema.integer
    ]

// ─── GP 11 — flat declarations emit exactly what they always did ─────

let private flatUnchangedTests =
    testList "flat declarations are byte-for-byte unchanged (GP 11)" [

        test "the canonical flat tool matches the pre-508 formula" {
            let def =
                defWith "my_module.analyse" [
                    flat "item_ids" "array" "List of item IDs." true
                    flat "metric" "string" "Metric to compute." true
                    flat "weeks" "number" "Weeks of history." false
                ]

            Expect.equal (toProviderDef def).InputSchema (pre508InputSchema def) "flat emission is unchanged"
        }

        test "a flat tool with no parameters is unchanged" {
            let def = defWith "my_module.ping" []

            Expect.equal (toProviderDef def).InputSchema (pre508InputSchema def) "empty parameter list is unchanged"
        }

        test "flat emission is unchanged for names, types and descriptions carrying JSON metacharacters" {
            // Same adversarial set the rich path is held to below, so a
            // divergence between the two escapers shows up here first.
            let nasty = [
                "quote\"inside"
                "back\\slash"
                "new\nline"
                "carriage\rreturn"
                "tab\there"
                "brace{but not a schema"
            ]

            for s in nasty do
                let def = defWith "t" [ flat s s s true ]

                Expect.equal
                    (toProviderDef def).InputSchema
                    (pre508InputSchema def)
                    (sprintf "flat emission unchanged for %A" s)
        }

        test "no bare JSON-Schema type name is mistaken for a rendered schema" {
            for typeName in [ "string"; "number"; "integer"; "boolean"; "object"; "array"; "null"; "" ] do
                Expect.isFalse (ToolSchema.isRendered typeName) $"'{typeName}' is a bare type name"

            Expect.isFalse (ToolSchema.isRendered null) "a null Type is not a rendered schema"
        }
    ]

// ─── The rendered schema ─────────────────────────────────────────────

let private rendererTests =
    testList "the recursive schema renders to JSON Schema" [

        test "a rendered schema is recognised as one" {
            Expect.isTrue (ToolSchema.isRendered (ToolSchema.render "" richSchemaNode)) "rendered schemas are rendered"
        }

        test "a nested object and an enum array reach the provider definition intact" {
            let schema =
                (toProviderDef (defWith "my_module.analyse" richParameters)).InputSchema

            use doc = JsonDocument.Parse schema
            let props = doc.RootElement.GetProperty "properties"

            // The flat parameter beside the rich ones still renders flat.
            let dataset = props.GetProperty "dataset"
            Expect.equal (dataset.GetProperty("type").GetString()) "string" "flat parameter keeps its type name"

            let filter = props.GetProperty "filter"
            Expect.equal (filter.GetProperty("type").GetString()) "object" "nested parameter is an object"
            Expect.equal (filter.GetProperty("description").GetString()) "Row filter." "carries its description"

            let metric = filter.GetProperty("properties").GetProperty "metric"

            let allowed =
                metric.GetProperty("enum").EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq

            Expect.equal allowed [ "revenue"; "units"; "margin" ] "the enum reaches the wire in declaration order"

            let filterRequired =
                filter.GetProperty("required").EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq

            Expect.equal filterRequired [ "metric" ] "only the required member is listed as required"

            // Two levels down, to prove the recursion is not one deep.
            let country =
                filter.GetProperty("properties").GetProperty("region").GetProperty("properties").GetProperty "country"

            Expect.equal (country.GetProperty("type").GetString()) "string" "a member of a nested object"

            let units = props.GetProperty "units"
            Expect.equal (units.GetProperty("type").GetString()) "array" "array parameter"

            let itemValues =
                units.GetProperty("items").GetProperty("enum").EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq

            Expect.equal itemValues [ "metric"; "imperial" ] "an array of enum values"
        }

        test "the top-level required list still reads a rich parameter's Required flag" {
            let schema =
                (toProviderDef (defWith "my_module.analyse" richParameters)).InputSchema

            use doc = JsonDocument.Parse schema

            let required =
                doc.RootElement.GetProperty("required").EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq

            // `units` is optional, `dataset` (flat) and `filter` (rich)
            // are required — the rich parameter is an ordinary
            // `ToolParameterSchema` to every reader but the renderer.
            Expect.equal required [ "dataset"; "filter" ] "rich parameters participate in the required list"
        }

        test "an empty description omits the keyword rather than emitting a blank one" {
            let rendered = ToolSchema.render "" ToolSchema.text
            Expect.equal rendered "{\"type\":\"string\"}" "no empty description keyword"
        }

        test "the renderer produces the exact schema the provider wire goldens are pinned to" {
            // `src/ToolUp.AI.Wire.Tests/WireFixtures.fs` hand-authors this
            // string and each mapper's golden embeds it, proving Claude,
            // OpenAI (and so the Copilot companion, which compiles the
            // OpenAI mapper source verbatim) and Gemini all carry a nested
            // schema to the wire byte for byte. That pack cannot reference
            // this tier, so this is the join: if the renderer's output
            // moves, the goldens over there are pinning a schema the SDK
            // no longer emits, and this case says so.
            let wireGolden =
                """{"type":"object","properties":{"filter":{"type":"object","description":"Row filter.","properties":{"metric":{"type":"string","description":"Metric.","enum":["revenue","units"]},"weeks":{"type":"integer"}},"required":["metric"]},"units":{"type":"array","description":"Units.","items":{"type":"string","enum":["metric","imperial"]}}},"required":["filter"]}"""

            let def =
                defWith "analyse" [
                    ToolSchema.parameter
                        "filter"
                        "Row filter."
                        true
                        (ToolSchema.objectOf [
                            ToolSchema.field "metric" "Metric." true (ToolSchema.enumOf [ "revenue"; "units" ])
                            ToolSchema.field "weeks" "" false ToolSchema.integer
                        ])
                    ToolSchema.parameter
                        "units"
                        "Units."
                        false
                        (ToolSchema.arrayOf (ToolSchema.enumOf [ "metric"; "imperial" ]))
                ]

            Expect.equal
                (toProviderDef def).InputSchema
                wireGolden
                "the wire packs' nestedToolInputSchema fixture is still what the registry emits"
        }

        test "the emission is stable — the same declaration renders the same bytes" {
            let once = (toProviderDef (defWith "t" richParameters)).InputSchema
            let twice = (toProviderDef (defWith "t" richParameters)).InputSchema
            Expect.equal once twice "rendering is deterministic"
        }
    ]

// ─── Escaping, as a property ─────────────────────────────────────────

let private escapingTests =
    testList "nested names, descriptions and enum values are escaped" [

        test "adversarial strings in every nested position still yield parseable JSON" {
            // Enumerated rather than randomly generated so a failure is
            // reproducible, but exhaustive over the positions: the flat
            // path only ever had three places to get this wrong, and a
            // recursive schema has one per member and one per value.
            let nasty = [
                "quote\"inside"
                "back\\slash"
                "new\nline"
                "carriage\rreturn"
                "tab\there"
                "\"},\"injected\":{\"type\":\"string\""
                "\\"
                "\"\"\""
            ]

            for s in nasty do
                let def =
                    defWith "t" [
                        ToolSchema.parameter
                            s
                            s
                            true
                            (ToolSchema.objectOf [
                                ToolSchema.field s s true (ToolSchema.enumOf [ s; "plain" ])
                                ToolSchema.field "arr" s false (ToolSchema.arrayOf (ToolSchema.enumOf [ s ]))
                            ])
                    ]

                let schema = (toProviderDef def).InputSchema

                use doc =
                    try
                        JsonDocument.Parse schema
                    with ex ->
                        failtestf "emission for %A is not parseable JSON (%s): %s" s ex.Message schema

                let outer = doc.RootElement.GetProperty("properties").GetProperty s
                let inner = outer.GetProperty("properties").GetProperty s

                Expect.equal
                    (inner.GetProperty("description").GetString())
                    s
                    (sprintf "description round-trips for %A" s)

                let values =
                    inner.GetProperty("enum").EnumerateArray()
                    |> Seq.map _.GetString()
                    |> List.ofSeq

                Expect.equal values [ s; "plain" ] (sprintf "enum values round-trip for %A" s)
        }

        test "an injection-shaped member name cannot add a sibling property" {
            let injected = "\"},\"escaped\":{\"type\":\"string\""

            let def =
                defWith "t" [
                    ToolSchema.parameter
                        "p"
                        ""
                        true
                        (ToolSchema.objectOf [ ToolSchema.field injected "" true ToolSchema.text ])
                ]

            use doc = JsonDocument.Parse (toProviderDef def).InputSchema

            let props =
                doc.RootElement.GetProperty("properties").GetProperty("p").GetProperty "properties"

            let names = props.EnumerateObject() |> Seq.map _.Name |> List.ofSeq

            Expect.equal names [ injected ] "the name is one property, not two"
        }

        test "the two escapers are one implementation" {
            // `AIToolRegistry`'s escaper delegates to the Core one; this
            // is the assertion that keeps them from drifting apart if
            // somebody re-inlines it.
            let probe = "a\"b\\c\nd\re\tf"
            let def = defWith "t" [ flat probe "string" probe true ]

            use doc = JsonDocument.Parse (toProviderDef def).InputSchema

            let name = doc.RootElement.GetProperty("properties").EnumerateObject() |> Seq.head

            Expect.equal name.Name probe "the flat path escapes exactly as ToolSchema.jsonEscape does"
            Expect.equal (ToolSchema.jsonEscape probe) "a\\\"b\\\\c\\nd\\re\\tf" "the Core escaper's own output"
        }
    ]

// ─── A hand-written fragment is a composition error ──────────────────

let private malformedTests =
    testList "a malformed rendered schema is refused at compose time" [

        test "the refusal names the tool and the parameter" {
            let def =
                defWith "my_module.broken" [ flat "arg" "{\"type\":\"object\"" "hand-written" true ]

            Expect.throwsC (fun () -> toProviderDef def |> ignore) (fun ex ->
                Expect.stringContains ex.Message "my_module.broken" "the refusal names the tool"
                Expect.stringContains ex.Message "arg" "the refusal names the parameter"
                Expect.stringContains ex.Message "ToolSchema.parameter" "the refusal names the remedy")
        }

        test "the refusal lands at createTool, beside the budget refusal" {
            let def = defWith "my_module.broken" [ flat "arg" "{not json}" "hand-written" true ]

            let executor: Microsoft.AspNetCore.Http.HttpContext -> string -> Async<string> =
                fun _ _ -> async { return "{}" }

            Expect.throws (fun () -> createTool def executor |> ignore) "compose-time, not turn-time"
        }
    ]

// ─── The same declaration decodes the arguments ──────────────────────

type Filter = { metric: string; weeks: int option }

let private decodeTests =
    testList "schema-validated argument decoding" [

        test "conforming arguments decode into the executor's record" {
            use doc = JsonDocument.Parse """{"filter":{"metric":"units","weeks":8}}"""

            match decodeArg<Filter> doc.RootElement "filter" richSchemaNode with
            | Ok value ->
                Expect.equal value.metric "units" "the enum member"
                Expect.equal value.weeks (Some 8) "the optional integer"
            | Error refusal -> failtestf "expected Ok; got %s" (ToolArgumentRefusal.toMessage refusal)
        }

        test "a value outside the enum is refused, naming the path and the allowed set" {
            use doc = JsonDocument.Parse """{"filter":{"metric":"furlongs"}}"""

            match decodeArg<Filter> doc.RootElement "filter" richSchemaNode with
            | Ok _ ->
                failtest
                    "an out-of-enum value was accepted — the deserialiser alone cannot catch this, which is why validation runs first"
            | Error refusal ->
                Expect.equal refusal.Path "filter.metric" "the refusal names the member, not just the argument"
                Expect.stringContains refusal.Expected "revenue" "the refusal quotes the allowed set"
                Expect.stringContains (ToolArgumentRefusal.toMessage refusal) "furlongs" "and what arrived"
        }

        test "a missing required member is refused before deserialisation" {
            use doc = JsonDocument.Parse """{"filter":{"weeks":4}}"""

            match decodeArg<Filter> doc.RootElement "filter" richSchemaNode with
            | Ok _ -> failtest "a missing required member was accepted"
            | Error refusal ->
                Expect.equal refusal.Path "filter.metric" "names the missing member"
                Expect.equal refusal.Received "" "nothing arrived to quote"
        }

        test "an absent optional member is not a refusal" {
            use doc = JsonDocument.Parse """{"filter":{"metric":"margin"}}"""

            match decodeArg<Filter> doc.RootElement "filter" richSchemaNode with
            | Ok value -> Expect.equal value.weeks None "optional members stay optional"
            | Error refusal -> failtestf "expected Ok; got %s" (ToolArgumentRefusal.toMessage refusal)
        }

        test "a JSON null on an optional member reads as absent" {
            use doc = JsonDocument.Parse """{"filter":{"metric":"margin","weeks":null}}"""

            match validateSchema richSchemaNode (doc.RootElement.GetProperty "filter") with
            | Ok() -> ()
            | Error refusal -> failtestf "null on an optional member should read as absent; got %A" refusal
        }

        test "a wrong element type inside an array names its index" {
            let schema = ToolSchema.arrayOf (ToolSchema.enumOf [ "metric"; "imperial" ])
            use doc = JsonDocument.Parse """{"units":["metric",3]}"""

            match decodeArg<string list> doc.RootElement "units" schema with
            | Ok _ -> failtest "a non-string array element was accepted"
            | Error refusal -> Expect.equal refusal.Path "units[1]" "the refusal names the offending index"
        }

        test "a fractional value is refused against an integer member" {
            use doc = JsonDocument.Parse """{"filter":{"metric":"units","weeks":1.5}}"""

            match decodeArg<Filter> doc.RootElement "filter" richSchemaNode with
            | Ok _ -> failtest "a fractional value was accepted against an integer declaration"
            | Error refusal -> Expect.equal refusal.Path "filter.weeks" "names the member"
        }

        test "a missing argument is refused" {
            use doc = JsonDocument.Parse """{}"""

            match decodeArg<Filter> doc.RootElement "filter" richSchemaNode with
            | Ok _ -> failtest "a missing argument was accepted"
            | Error refusal -> Expect.equal refusal.Path "filter" "names the argument"
        }

        test "decodeOptionalArg returns None for an absent argument and still validates a present one" {
            use present = JsonDocument.Parse """{"filter":{"metric":"nope"}}"""
            use absent = JsonDocument.Parse """{}"""

            match decodeOptionalArg<Filter> absent.RootElement "filter" richSchemaNode with
            | Ok None -> ()
            | other -> failtestf "absent should be Ok None; got %A" other

            match decodeOptionalArg<Filter> present.RootElement "filter" richSchemaNode with
            | Error refusal ->
                Expect.equal refusal.Path "filter.metric" "a present optional argument is still validated"
            | Ok _ -> failtest "an optional argument that is present must still match its schema"
        }

        test "requireDecoded raises the ToolArgumentError the agent loop classifies as InvalidArguments" {
            use doc = JsonDocument.Parse """{"filter":{"metric":"furlongs"}}"""

            // The field is read rather than `Message`, so the assertion
            // is about what was raised and not about how F# renders it.
            Expect.throwsC
                (fun () -> requireDecoded<Filter> doc.RootElement "filter" richSchemaNode |> ignore)
                (fun ex ->
                    match ex with
                    | ToolArgumentError message ->
                        Expect.stringContains message "filter.metric" "the raised message carries the path"
                    | other -> failtestf "expected ToolArgumentError; got %s" (other.GetType().Name))
        }
    ]

let tests =
    testList "Phase 508 rich AI tool parameter schemas" [
        flatUnchangedTests
        rendererTests
        escapingTests
        malformedTests
        decodeTests
    ]
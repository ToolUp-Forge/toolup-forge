module ToolUp.Platform.Tests.InProcess.AIToolResultBudgetTests

open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.AI.AIToolRegistry

// ─── Phase 709 — AI tool-result context budget ───────────────────────
//
// The guard's whole value is that an oversized tool result stops
// flooding model context WITHOUT becoming invisible, and without
// becoming confusable with either of the two other things that can
// remove content from a tool result: a disclosure-policy withhold
// (Phase 706's `WithheldByPolicy`) and a tool invocation error.
//
// So the pack is organised around those three properties rather than
// around the three tasks: the pass-through has to be byte-identical
// (GP 11), the elision has to be legible JSON that names its own cause,
// and the marker has to be distinguishable from a withhold and from an
// error by inspection.

let private mkDef (name: string) (budget: AIToolResultBudget) : AIToolDefinition = {
    Name = name
    Description = "budget test tool"
    Parameters = []
    SourceModule = "test"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = budget
}

/// A JSON-ish payload of exactly `n` characters — stands in for the
/// 10⁵-record encoding the guard exists to stop.
let private payloadOf (n: int) = System.String('x', n)

let private applyTo (budget: AIToolResultBudget) (result: string) =
    ToolResultBudget.apply "list_metric_coverage" budget result

// ── 709.A — the knob ────────────────────────────────────────────────

let private knobTests =
    testList "budget knob" [

        test "the default resolves to the SDK ceiling, and the ceiling is generous" {
            Expect.equal
                (ToolResultBudget.limitFor DefaultResultBudget)
                (Some DefaultToolResultBudgetChars)
                "the value every pre-709 tool carries resolves to the SDK-wide ceiling"

            // The number itself is the GP-11 promise: a guard, not a
            // tuning knob. If someone tightens it into the range real
            // tool results occupy, this is the case that says so.
            Expect.isGreaterThanOrEqual
                DefaultToolResultBudgetChars
                100_000
                "the default must stay far above any well-behaved tool result — it is a flood guard"
        }

        test "a per-tool override replaces the ceiling, and NoResultBudget removes it" {
            Expect.equal (ToolResultBudget.limitFor (ResultBudgetChars 512)) (Some 512) "the tool's own ceiling"
            Expect.equal (ToolResultBudget.limitFor NoResultBudget) None "the escape for a legitimately large contract"
        }

        test "a non-positive override is refused at createTool, not silently read as unbounded" {
            // The failure mode this guards: an author reaching for a
            // TIGHTER budget writes 0 (or a computed value that came out
            // negative) and gets an UNBOUNDED tool — the exact opposite
            // of what they asked for, silently. createTool is the single
            // funnel every RegisteredTool passes through and it runs at
            // compose time, so the refusal lands at startup.
            let exec _ _ = async { return "{}" }

            Expect.throws
                (fun () -> createTool (mkDef "zero_budget" (ResultBudgetChars 0)) exec |> ignore)
                "a zero budget is a composition error"

            Expect.throws
                (fun () -> createTool (mkDef "negative_budget" (ResultBudgetChars -1)) exec |> ignore)
                "a negative budget is a composition error"

            // …and the valid declarations still compose.
            let ok = createTool (mkDef "fine" (ResultBudgetChars 1)) exec
            Expect.equal ok.Definition.ResultBudget (ResultBudgetChars 1) "a positive override composes"

            let unbounded = createTool (mkDef "big" NoResultBudget) exec
            Expect.equal unbounded.Definition.ResultBudget NoResultBudget "NoResultBudget composes"
        }

        test "every tool the SDK itself registers stays on the default" {
            // The acceptance clause: all in-repo tools pass under the
            // default budget unchanged. Asserted over the two built-in
            // families rather than by reading the source, so a new
            // built-in that quietly declares a tight budget is caught.
            let builtIns =
                ToolUp.AI.NarrativeTools.builtInTools @ ToolUp.AI.PlatformAITools.builtIn

            Expect.isNonEmpty builtIns "the built-in tool families are non-empty"

            for t in builtIns do
                Expect.equal
                    t.Definition.ResultBudget
                    DefaultResultBudget
                    $"built-in tool '{t.Definition.Name}' declares the default budget"
        }
    ]

// ── 709.B — enforcement ─────────────────────────────────────────────

let private passThroughTests =
    testList "under-budget pass-through" [

        test "an under-budget result is returned byte-identical" {
            let result = """{"rows":[{"metric":"revenue","coverage":0.92}]}"""
            let payload, elided = applyTo DefaultResultBudget result

            Expect.equal payload result "byte-identical — no re-encode, no normalisation"
            Expect.isNone elided "nothing was elided"
        }

        test "a result exactly at the ceiling passes — the bound is inclusive" {
            // Off-by-one here would elide a result that fits, which is a
            // worse failure than letting one extra character through.
            let payload, elided = applyTo (ResultBudgetChars 100) (payloadOf 100)

            Expect.equal payload.Length 100 "the exact-fit result passed through"
            Expect.isNone elided "at the ceiling is not over it"
        }

        test "NoResultBudget passes an arbitrarily large result through untouched" {
            let huge = payloadOf 5_000_000
            let payload, elided = applyTo NoResultBudget huge

            Expect.equal payload huge "the escape hatch means what it says"
            Expect.isNone elided "no elision recorded"
        }

        test "a null result is not the budget's problem" {
            let payload, elided = applyTo DefaultResultBudget null

            Expect.isNull payload "passed through for the caller's existing handling"
            Expect.isNone elided "and not reported as an elision"
        }
    ]

let private elisionTests =
    testList "over-budget elision" [

        test "an over-budget result is replaced, and the elided size is reported" {
            let payload, elided = applyTo (ResultBudgetChars 100) (payloadOf 5_000)

            Expect.equal elided (Some 5_000) "the caller is told what was elided, so telemetry can name it"
            Expect.isLessThan payload.Length 5_000 "the marker is small"
            Expect.notEqual payload (payloadOf 5_000) "the payload was replaced"
        }

        test "the marker is valid JSON the model can act on" {
            let payload, _ = applyTo (ResultBudgetChars 100) (payloadOf 5_000)

            // Parse rather than substring-match: the acceptance is that a
            // MODEL can read it, and a marker that only a regex can read
            // would pass a looser assertion.
            use doc = JsonDocument.Parse payload
            let root = doc.RootElement

            Expect.isTrue (root.GetProperty("toolResultElided").GetBoolean()) "the discriminator"
            Expect.equal (root.GetProperty("tool").GetString()) "list_metric_coverage" "names the offending tool"
            Expect.equal (root.GetProperty("resultChars").GetInt32()) 5_000 "names the elided size"
            Expect.equal (root.GetProperty("budgetChars").GetInt32()) 100 "names the ceiling it broke"

            let steer = root.GetProperty("steer").GetString()

            Expect.stringContains steer "Narrow the request" "the steer tells the model what to do next"
            Expect.stringContains steer "aggregate" "…including the aggregate/paginated alternative"
        }

        test "the result is withheld IN FULL, never truncated mid-value" {
            // Two failures a mid-value truncation causes, both silent:
            // the payload stops being valid JSON, and a shortened ranking
            // reads to the model as a complete one.
            let ranking =
                let rows =
                    [ 1..500 ]
                    |> List.map (fun i -> $"{{\"rank\":{i},\"subject\":\"s{i}\"}}")
                    |> String.concat ","

                $"{{\"rows\":[{rows}]}}"

            let payload, elided = applyTo (ResultBudgetChars 200) ranking

            Expect.isSome elided "elided"
            Expect.isFalse (payload.Contains "\"rank\":1,") "no prefix of the real ranking survives into context"
            Expect.isFalse (payload.StartsWith(ranking.Substring(0, 50))) "the marker is not a truncation of the result"
        }

        test "a tool name containing JSON metacharacters still yields parseable JSON" {
            // The marker is hand-built by interpolation, the same shape
            // `toProviderDef` uses — and the same escaping hazard.
            let payload, _ =
                ToolResultBudget.apply "weird\"tool\\name" (ResultBudgetChars 10) (payloadOf 100)

            use doc = JsonDocument.Parse payload

            Expect.equal
                (doc.RootElement.GetProperty("tool").GetString())
                "weird\"tool\\name"
                "the name round-trips through the escaping"
        }
    ]

let private distinguishabilityTests =
    testList "not a withhold, not an error" [

        test "the marker carries no policy-withhold vocabulary" {
            // Phase 706's disclosure gate reports absence as
            // `WithheldCount` / `WithheldByPolicy`. A budget drop means
            // "ask me something narrower"; a withhold means "you may not
            // have this". A model that conflates them either stops asking
            // for data it is entitled to, or keeps re-asking for data it
            // is not. Keeping the two vocabularies disjoint is the whole
            // defence, and it is a property of the marker text.
            let payload, _ = applyTo (ResultBudgetChars 100) (payloadOf 5_000)
            let lowered = payload.ToLowerInvariant()

            Expect.isFalse (lowered.Contains "withheldbypolicy") "no policy-withhold key"
            Expect.isFalse (lowered.Contains "withheldcount") "no policy-withhold key"
            Expect.isFalse (lowered.Contains "policy") "the word 'policy' never appears in a budget drop"
            Expect.stringContains payload "over-context-budget" "the reason is stated as what it is"
        }

        test "the marker does not read as a tool invocation error" {
            // `AIAgentEngine.isErrorToolResult` classifies by the prose
            // prefixes our own error rendering produces. If the marker
            // matched one, an elided result would count toward the
            // two-turn early-stop and could end a conversation the model
            // can recover from in one turn by narrowing. The predicate is
            // private, so its shape is mirrored here — the comment on it
            // says to update both.
            let payload, _ = applyTo (ResultBudgetChars 100) (payloadOf 5_000)

            Expect.isFalse (payload.StartsWith "Unknown tool:") "not the unknown-tool rendering"
            Expect.isFalse (payload.StartsWith "Tool '") "not the failed/invalid-args/denied rendering"
            Expect.stringStarts payload "{" "it is a JSON document, which no error rendering is"
        }
    ]

let private telemetryTests =
    testList "operator signal" [

        test "the elision counter is registered, so emissions flow rather than drop" {
            // An unregistered series fails by silently dropping every
            // emission — the one failure mode a metric has.
            let reg =
                ToolUp.AI.AILatencyMetrics.registrations
                |> List.tryFind (fun r -> r.Definition.Name = ToolUp.AI.AILatencyMetrics.ToolResultElided)

            match reg with
            | None -> failtest "the tool-result elision counter is not in the AI metric registrations"
            | Some r ->
                Expect.equal r.Definition.Kind ToolUp.Platform.Metrics.Counter "one increment per elision"

                Expect.equal
                    r.Definition.Tags
                    [ "tool" ]
                    "tagged by tool — the operator's question is which tool needs an aggregate shape"
        }

        test "the Warn is claimed once per tool, and per registry rather than globally" {
            let registry = ToolUp.AI.AIToolRegistry.AIToolRegistry()

            Expect.isTrue (registry.TryClaimBudgetWarning "noisy_tool") "the first elision logs"
            Expect.isFalse (registry.TryClaimBudgetWarning "noisy_tool") "the second does not"
            Expect.isFalse (registry.TryClaimBudgetWarning "noisy_tool") "nor any later one"
            Expect.isTrue (registry.TryClaimBudgetWarning "other_tool") "a different tool gets its own line"

            // Two composed servers in one process (which the test suite
            // itself does) must not share a suppression set — otherwise
            // the first test to elide silences the operator signal for
            // every later deployment in the process.
            let second = ToolUp.AI.AIToolRegistry.AIToolRegistry()

            Expect.isTrue (second.TryClaimBudgetWarning "noisy_tool") "a fresh registry starts with a fresh claim set"
        }
    ]

let tests =
    testList "Phase 709 AI tool-result budget guard" [
        knobTests
        passThroughTests
        elisionTests
        distinguishabilityTests
        telemetryTests
    ]
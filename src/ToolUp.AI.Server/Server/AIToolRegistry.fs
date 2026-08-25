module ToolUp.AI.AIToolRegistry

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

/// A registered tool: metadata + executable function.
/// The Execute function receives JSON arguments and HttpContext
/// (for scope resolution / DI access), returns a JSON result.
type RegisteredTool = {
    /// Shared metadata visible to both server and client
    Definition: AIToolDefinition
    /// Tool definition in the format the AI provider expects
    ProviderDef: AIProviderToolDef
    /// Execute the tool: HttpContext -> JSON args -> Async<JSON result>
    Execute: HttpContext -> string -> Async<string>
}

/// Sanitize a tool name to match AI provider naming constraints.
/// Claude requires ^[a-zA-Z0-9_-]{1,128}$
let private sanitizeToolName (name: string) = name.Replace(".", "_")

/// JSON-escape a string value so it can be embedded inside a
/// double-quoted JSON string literal. Required because `toProviderDef`
/// hand-builds the `InputSchema` JSON via string interpolation; a raw
/// `"` or `\` in any tool's `Name` / `Type` / `Description` would
/// otherwise terminate the string early and produce malformed JSON
/// that the AI provider's `JsonDocument.Parse` rejects at request
/// time. Order matters: backslashes first so they don't double-escape
/// the replacements that follow.
let private jsonEscape (s: string) : string =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

/// Phase 6g.A: surface gate. Returns true when the tool's `Surface`
/// declaration permits the current request's surface. The agent loop
/// applies this filter when constructing the per-turn tool list passed
/// to the provider, so the model only sees tools that make sense for
/// the user's chat surface.
let isToolVisibleOnSurface (surface: AISurface) (def: AIToolDefinition) : bool =
    match def.Surface, surface with
    | Both, _
    | SidePanelOnly, SidePanel
    | FullPageOnly, FullPage -> true
    | SidePanelOnly, FullPage
    | FullPageOnly, SidePanel -> false

/// Generate an AIProviderToolDef (JSON Schema format) from an AIToolDefinition.
/// Eliminates the need to hand-write both Definition and ProviderDef.
/// Tool names are sanitized to meet provider naming constraints.
///
/// `Name` / `Type` / `Description` are JSON-escaped before
/// interpolation — descriptions routinely contain quoted examples
/// like `e.g. "country"`, which would otherwise terminate the JSON
/// string mid-stream and trip the provider's `JsonDocument.Parse`.
let toProviderDef (def: AIToolDefinition) : AIProviderToolDef =
    let properties =
        def.Parameters
        |> List.map (fun p ->
            $"\"{jsonEscape p.Name}\":{{\"type\":\"{jsonEscape p.Type}\",\"description\":\"{jsonEscape p.Description}\"}}")
        |> String.concat ","

    let required =
        def.Parameters
        |> List.filter _.Required
        |> List.map (fun p -> $"\"{jsonEscape p.Name}\"")
        |> String.concat ","

    {
        Name = sanitizeToolName def.Name
        Description = def.Description
        InputSchema = $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{required}]}}"
    }

// ─── Phase 709 — per-tool result context budget ──────────────────────
//
// A tool result is serialised straight into model context. Any module
// tool can therefore flood the conversation with an unbounded payload —
// observed in a consumer composition whose analysis tool returns the
// complete subject key list on every call, which at high cardinality is
// 10⁵+ records JSON-encoded into the prompt. The agent-loop dispatch is
// the one choke point every tool result passes through, so the guard
// belongs there.
//
// Three properties the design is deliberately built around:
//
//   * **Visible, never silent.** An over-budget result is replaced by a
//     typed JSON marker naming the tool, the elided size, the ceiling
//     and the steer. It is NOT truncated mid-value: a JSON document cut
//     at character N is invalid JSON that the model will nonetheless try
//     to read, and a *silently* shortened ranking reads as complete.
//     Same reasoning as the population disclosure's `WithheldCount` —
//     an absence has to be reported to be reasoned about.
//
//   * **Distinct from a policy withhold.** The marker's discriminator is
//     `toolResultElided`, never a `withheld*` key. A budget drop and a
//     disclosure-policy withhold must not be confusable: one is "ask me
//     something narrower", the other is "you may not have this". The
//     budget also applies strictly AFTER the tool has returned — every
//     disclosure gate inside the tool has already run — so freed budget
//     can never surface a substitute for something a policy withheld.
//
//   * **Not an error.** `isErrorToolResult` in the agent loop matches the
//     invocation-error prose prefixes, and the marker deliberately does
//     not. The tool SUCCEEDED; the result was too large to carry. Having
//     it read as an error would spend the loop's two-turn early-stop
//     budget on a situation the model can recover from in one turn by
//     narrowing the query.

/// The SDK-wide default result ceiling, in characters of returned JSON.
///
/// Deliberately generous (GP 11 / GP 13): it is a flood guard, not a
/// tuning knob. 200 000 characters is roughly 50k tokens — an order of
/// magnitude above any well-behaved tool result in this repo and above
/// anything a deployment would want a *single* tool return to occupy,
/// while still well inside a modern frontier context window, so a
/// deployment that trips it was already broken rather than newly
/// constrained.
[<Literal>]
let DefaultToolResultBudgetChars = 200_000

/// Resolution + marker rendering for `AIToolResultBudget`. Pure — the
/// dispatch site in `AIAgentEngine` supplies the telemetry and logging.
module ToolResultBudget =

    /// The effective ceiling in characters, or `None` when this tool's
    /// result is not bounded.
    let limitFor (budget: AIToolResultBudget) : int option =
        match budget with
        | NoResultBudget -> None
        | DefaultResultBudget -> Some DefaultToolResultBudgetChars
        | ResultBudgetChars n -> Some n

    /// The typed replacement payload for an over-budget result. Valid
    /// JSON the model can read and act on: it names the tool, both
    /// sizes, and what to do next.
    let elisionMarker (toolName: string) (resultChars: int) (limitChars: int) : string =
        let steer =
            $"The result from '{toolName}' was {resultChars} characters, over this tool's "
            + $"{limitChars}-character context budget, and was withheld IN FULL rather than "
            + "truncated mid-value — a partial result would read as a complete one. "
            + "Narrow the request (fewer subjects, a smaller limit, a tighter filter), or call "
            + "an aggregate or paginated tool if one is available for this data."

        $"{{\"toolResultElided\":true,\"tool\":\"{jsonEscape toolName}\","
        + $"\"resultChars\":{resultChars},\"budgetChars\":{limitChars},"
        + $"\"reason\":\"over-context-budget\",\"steer\":\"{jsonEscape steer}\"}}"

    /// Apply a tool's budget to its returned JSON. Returns the payload
    /// the model should see, plus `Some elidedChars` when the result was
    /// replaced by the marker (`None` when it passed through).
    ///
    /// Under-budget results are returned **byte-identical** — the same
    /// string instance, no re-encode, no normalisation.
    let apply (toolName: string) (budget: AIToolResultBudget) (result: string) : string * int option =
        if isNull result then
            result, None
        else
            match limitFor budget with
            | None -> result, None
            | Some limit when result.Length <= limit -> result, None
            | Some limit -> elisionMarker toolName result.Length limit, Some result.Length

/// Create a RegisteredTool from an AIToolDefinition and an Execute function.
/// The ProviderDef is auto-generated from the Definition.
///
/// Phase 709: this is the single funnel every `RegisteredTool` passes
/// through, so it is where a nonsensical budget declaration is refused.
/// A non-positive `ResultBudgetChars` is a composition error, not a
/// silent "unbounded" — reading it as `NoResultBudget` would disable
/// exactly the guard the author was reaching for. `createTool` runs at
/// compose time (built-ins at module init, module tools inside
/// `composeAI`), so the failure lands at startup, beside the tool-name
/// collision check, and never mid-turn.
let createTool (def: AIToolDefinition) (execute: HttpContext -> string -> Async<string>) : RegisteredTool =
    match def.ResultBudget with
    | ResultBudgetChars n when n <= 0 ->
        failwithf
            "AI tool '%s' declares ResultBudgetChars %d. A result budget must be positive — use NoResultBudget to declare a tool whose contract is legitimately large, or DefaultResultBudget for the SDK ceiling (%d characters)."
            def.Name
            n
            DefaultToolResultBudgetChars
    | _ -> ()

    {
        Definition = def
        ProviderDef = toProviderDef def
        Execute = execute
    }

/// Mutable registry of AI-callable tools, populated at startup.
/// Modules register their tools via compose; the registry is immutable after startup.
type AIToolRegistry() =
    let mutable tools: RegisteredTool list = []

    /// Phase 709 — tools that have already logged a budget-elision Warn
    /// on this registry. Instance state rather than a module-level
    /// `mutable` so two composed servers in one process (tests do this)
    /// do not share a suppression set.
    let budgetWarned =
        System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

    /// Register a list of tools (called during server startup)
    member _.RegisterAll(newTools: RegisteredTool list) = tools <- newTools @ tools

    /// Get all registered tools
    member _.GetAll() = tools

    /// Find a tool by name (matches against both Definition.Name and ProviderDef.Name)
    member _.FindByName(name: string) =
        tools
        |> List.tryFind (fun t -> t.Definition.Name = name || t.ProviderDef.Name = name)

    /// Phase 709 — claim the one budget-elision Warn allowed for this
    /// tool. Returns `true` exactly once per tool name per registry;
    /// every later elision by the same tool emits the telemetry counter
    /// but stays out of the log.
    ///
    /// The signal an operator needs is "this tool needs an aggregate
    /// shape", which is a property of the TOOL and does not become truer
    /// by being logged on every turn. A model that keeps calling an
    /// oversized tool would otherwise fill the operator log with the one
    /// line that mattered the first time — and the counter, which is
    /// where per-occurrence volume belongs, still records every one.
    member _.TryClaimBudgetWarning(toolName: string) : bool = budgetWarned.TryAdd(toolName, true)
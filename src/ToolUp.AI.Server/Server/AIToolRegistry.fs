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

/// Create a RegisteredTool from an AIToolDefinition and an Execute function.
/// The ProviderDef is auto-generated from the Definition.
let createTool (def: AIToolDefinition) (execute: HttpContext -> string -> Async<string>) : RegisteredTool = {
    Definition = def
    ProviderDef = toProviderDef def
    Execute = execute
}

/// Mutable registry of AI-callable tools, populated at startup.
/// Modules register their tools via compose; the registry is immutable after startup.
type AIToolRegistry() =
    let mutable tools: RegisteredTool list = []

    /// Register a list of tools (called during server startup)
    member _.RegisterAll(newTools: RegisteredTool list) = tools <- newTools @ tools

    /// Get all registered tools
    member _.GetAll() = tools

    /// Find a tool by name (matches against both Definition.Name and ProviderDef.Name)
    member _.FindByName(name: string) =
        tools
        |> List.tryFind (fun t -> t.Definition.Name = name || t.ProviderDef.Name = name)
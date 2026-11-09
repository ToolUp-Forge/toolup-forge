module internal KnowledgeBase.ServerJsonHelpers

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── JSON helpers ─────────────────────────────────────────────────

let jsonOptions = PlatformJson.options

let toJson o =
    JsonSerializer.Serialize(o, jsonOptions)

let fromJson<'T> (s: string) =
    JsonSerializer.Deserialize<'T>(s, jsonOptions)
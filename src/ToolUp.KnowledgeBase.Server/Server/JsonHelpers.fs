module KnowledgeBase.ServerJsonHelpers

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── JSON helpers ─────────────────────────────────────────────────

let jsonOptions = FableConverters.create ()

let toJson o =
    JsonSerializer.Serialize(o, jsonOptions)

let fromJson<'T> (s: string) =
    JsonSerializer.Deserialize<'T>(s, jsonOptions)
module KnowledgeBase.ServerJsonHelpers

open Newtonsoft.Json

// ─── JSON helpers ─────────────────────────────────────────────────

let jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(ToolUp.Remoting.Json.FableJsonConverter())
    s

let toJson o =
    JsonConvert.SerializeObject(o, jsonSettings)

let fromJson<'T> (s: string) =
    JsonConvert.DeserializeObject<'T>(s, jsonSettings)
module ToolUp.AI.NarrativeTools

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.AI
open ToolUp.AI.AIToolRegistry

/// Built-in AI tools for surfacing narrative output produced on other
/// pages or earlier in the session. The assistant can list the user's
/// recent narratives (`list_narratives`) and fetch the full markdown
/// body of any one of them by id (`get_narrative`). Both are scoped by
/// the request's `StorageScope` — a user in one team only sees their
/// own scope's narratives (GP 4).
///
/// Wired automatically by `composeWithAI`; apps do not need to register
/// these explicitly.

// ─── JSON helpers ────────────────────────────────────────────────

let private fableJsonSettings =
    let s = Newtonsoft.Json.JsonSerializerSettings()
    s.Converters.Add(Fable.Remoting.Json.FableJsonConverter())
    s

let private fableSerialize (value: obj) : string =
    Newtonsoft.Json.JsonConvert.SerializeObject(value, fableJsonSettings)

// ─── list_narratives ─────────────────────────────────────────────

let private listDefinition: AIToolDefinition = {
    Name = "list_narratives"
    Description =
        "List narrative outputs the user has generated on other pages in this session (or earlier sessions for persistent deployments). Returns an id, module, page, title, optional subtitle and publication timestamp for each entry. Use this before `get_narrative` to discover what is available. Results are scoped to the current user/team — the assistant cannot see other users' narratives."
    Parameters = [
        {
            Name = "limit"
            Type = "number"
            Description =
                "Maximum number of entries to return (newest first). Omit for a default of 20; the store caps its own upper bound."
            Required = false
            Default = Some "20"
        }
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
}

let private executeList (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let limit =
        try
            let doc = JsonDocument.Parse(argsJson)

            match doc.RootElement.TryGetProperty("limit") with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 20
        with _ ->
            20

    let! entries = NarrativePublisher.list ctx limit
    return fableSerialize entries
}

// ─── get_narrative ───────────────────────────────────────────────

let private getDefinition: AIToolDefinition = {
    Name = "get_narrative"
    Description =
        "Fetch the full body of a single narrative entry by id (discovered via `list_narratives`). Returns the narrative rendered as Markdown — section headings, prose, lists, callouts and metrics — ready to quote or summarise. Returns an error object if the id is unknown or not visible to the current scope."
    Parameters = [
        {
            Name = "id"
            Type = "string"
            Description = "NarrativeId (GUID string) returned by `list_narratives`."
            Required = true
            Default = None
        }
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
}

let private executeGet (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let idText =
        let doc = JsonDocument.Parse(argsJson)

        match doc.RootElement.TryGetProperty("id") with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    match idText with
    | None ->
        return
            fableSerialize {|
                error = "Required argument 'id' is missing. Call list_narratives first to get valid ids."
            |}
    | Some idText ->

        match Guid.TryParse idText with
        | false, _ ->
            return
                fableSerialize {|
                    error = "Argument 'id' is not a valid GUID."
                    id = idText
                |}
        | true, id ->
            let! entry = NarrativePublisher.get ctx id

            match entry with
            | None ->
                return
                    fableSerialize {|
                        error =
                            "No narrative with that id is visible to this scope. It may belong to a different user or team, or have been evicted."
                        id = idText
                    |}
            | Some e ->
                let markdown = NarrativeMarkdown.render e.Document

                let payload = {|
                    id = e.Id
                    moduleId = e.ModuleId
                    pageRoute = e.PageRoute
                    title = e.Title
                    subtitle = e.Subtitle
                    publishedAt = e.PublishedAt
                    markdown = markdown
                |}

                return fableSerialize payload
}

// ─── Registration ────────────────────────────────────────────────

/// Built-in narrative tools — auto-registered by `composeWithAI`.
let builtInTools: RegisteredTool list = [ createTool listDefinition executeList; createTool getDefinition executeGet ]
module KnowledgeBase.ServerAIContext

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AI.SystemPromptBuilder
open SharedTypes
open KnowledgeBase.ServerJsonHelpers

// ─── AI context blob storage ─────────────────────────────────────

let private aiContextBlobName = "knowledge/_ai-context.json"

let loadAIContext (storage: IBlobStorage) (container: string) : Async<AIContextEntry option> = async {
    match! storage.Download(container, aiContextBlobName) with
    | Ok bytes ->
        try
            return Some(fromJson<AIContextEntry> (Encoding.UTF8.GetString bytes))
        with _ ->
            return None
    | Error _ -> return None
}

let saveAIContext (storage: IBlobStorage) (container: string) (entry: AIContextEntry) = async {
    let bytes = (toJson entry: string) |> Encoding.UTF8.GetBytes
    let! _ = storage.Upload(container, aiContextBlobName, bytes)
    ()
}

// ─── Inventory summary publish ────────────────────────────────────

/// Reads the current inventory state and publishes a `CustomNotification`
/// keyed by `InventoryUpdatedNotificationKey` to the user's notification
/// scope. Subscribers in other companion packages (e.g. the AI Assistant
/// side panel's KB-presence badge) receive the snapshot without having
/// to import KB types — they parse the JSON payload by shape.
///
/// Failures are swallowed and logged: a flaky inventory snapshot must
/// never block the inventory-mutating operation that triggered it.

// ─── Standing AI context system-prompt builder ───────────────────

/// Reads the team's standing AI context from `knowledge/_ai-context.json`
/// and returns its body for inclusion in the system prompt. Returns `""`
/// on absence, empty body, Anonymous mode, or read failure so
/// `SystemPromptBuilder.compose` drops the contribution silently.
///
/// Composed by the deployment alongside the platform prefix, active-
/// module context, and current-page narrative — KB exports the builder
/// here so the dependency direction stays correct (AI must not depend
/// on KB; the deployment's composition root is the only place that
/// sees both).
let standingContextBuilder (storage: IBlobStorage) (logger: ILogger option) : SystemPromptBuilder =
    fun ctx -> async {
        match AccessContext.configScope ctx.Access with
        | None -> return ""
        | Some scope ->
            try
                let! entry = loadAIContext storage scope.Container

                match entry with
                | None -> return ""
                | Some e ->
                    if String.IsNullOrWhiteSpace e.Body then
                        return ""
                    else
                        return e.Body.Trim()
            with ex ->
                logger
                |> Option.iter (fun l ->
                    l.Warn(sprintf "[KnowledgeBase] standingContextBuilder read failed: %s" ex.Message))

                return ""
    }
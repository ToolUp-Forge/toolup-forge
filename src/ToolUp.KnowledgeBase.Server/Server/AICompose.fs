module ToolUp.KnowledgeBase.Server.AICompose

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AI.AICompose
open SharedTypes
open KnowledgeBase.ServerIndexStorage

// Built-in AI tools that surface the KnowledgeBase to the assistant
// directly — separate from the `list_narratives` / `get_narrative`
// tools which query `INarrativeStore`. Documents committed via
// narrative-commit, uploads, and notes all flow into the KB; this
// gives the assistant a way to enumerate them by name and fetch the
// raw content without going through vector retrieval.
//
// RAG retrieval (the default `RAGPromptBuilder.withRetrieval` path)
// still injects relevant chunks into the system prompt every turn —
// but it ranks by cosine similarity over the full deployment-scoped
// corpus and can miss documents the user is asking about by name in
// a noisy corpus. These tools complement RAG: the assistant can call
// `list_kb_documents` to discover what's there, then `get_kb_document`
// to fetch a specific document's full content when it knows the
// document is what the user wants.
//
// Both tools are scoped to the request's `StorageScope`. A user in
// one team / session only sees their own scope's KB content (GP 4).

// ─── JSON helpers ────────────────────────────────────────────────

let private fableJsonOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

let private fableSerialize (value: obj) : string =
    JsonSerializer.Serialize(value, fableJsonOptions)

// ─── Scope / storage resolution ──────────────────────────────────

let private resolveScope (ctx: HttpContext) : StorageScope =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        {
            ScopeId = userId
            Container = sprintf "user-%s" userId
            Persist = true
        }

let private resolveStorage (ctx: HttpContext) : IBlobStorage option =
    match ctx.RequestServices.GetService(typeof<IBlobStorage>) with
    | :? IBlobStorage as s -> Some s
    | _ -> None

// ─── list_kb_documents ───────────────────────────────────────────

let private listDefinition: AIToolDefinition = {
    Name = "list_kb_documents"
    Description =
        "List documents currently in the user's knowledge base — uploaded files, narrative commits from analysis pages, and free-form notes. Returns one summary per document with id, filename, file type, source kind (UploadedFile / FromNarrative / Note), upload timestamp, chunk count, and size in bytes. Use this BEFORE answering questions like 'what documents do I have?' or 'compare X and Y' — the knowledge base is the source of truth for what the user committed; vector retrieval may miss documents in a noisy corpus. Pass the resulting ids into `get_kb_document` to fetch full content. Results are scoped to the current user/team — the assistant cannot see other users' KB content."
    Parameters = []
    SourceModule = "ToolUp.KnowledgeBase"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}

let private executeList (ctx: HttpContext) (_argsJson: string) : Async<string> = async {
    match resolveStorage ctx with
    | None ->
        return
            fableSerialize {|
                documents = ([]: KnowledgeDocument list)
                error = "No IBlobStorage registered — KnowledgeBase tools are unavailable in this deployment."
            |}
    | Some storage ->
        let scope = resolveScope ctx
        let! docs = loadIndex storage scope.Container

        let summaries =
            docs
            |> List.map (fun d -> {|
                id = d.Id
                fileName = d.FileName
                fileType = d.FileType
                uploadedAt = d.UploadedAt
                chunkCount = d.ChunkCount
                sizeBytes = d.SizeBytes
                source = d.Source
                status = d.Status
            |})

        return fableSerialize {| documents = summaries |}
}

// ─── get_kb_document ─────────────────────────────────────────────

let private getDefinition: AIToolDefinition = {
    Name = "get_kb_document"
    Description =
        "Fetch the full raw content of a single knowledge-base document by id (discovered via `list_kb_documents`). Returns the document's filename, source kind, file type, chunk count, and `content` (the raw text — markdown for narratives and notes, extracted text for uploaded files). Use this when the user asks about a specific document by name, or when comparing two named documents that vector retrieval may not have surfaced both of. Returns an error object if the id is unknown or not visible to the current scope."
    Parameters = [
        {
            Name = "id"
            Type = "string"
            Description =
                "Document id (string) returned by `list_kb_documents`. Typically a GUID for narrative / upload sources."
            Required = true
            Default = None
        }
    ]
    SourceModule = "ToolUp.KnowledgeBase"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}

let private executeGet (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let id =
        try
            let doc = JsonDocument.Parse(argsJson)

            match doc.RootElement.TryGetProperty("id") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
        with _ ->
            ""

    if String.IsNullOrWhiteSpace id then
        return
            fableSerialize {|
                error = "Required argument 'id' is missing. Call list_kb_documents first to get valid ids."
            |}
    else
        match resolveStorage ctx with
        | None ->
            return
                fableSerialize {|
                    error = "No IBlobStorage registered."
                    id = id
                |}
        | Some storage ->
            let scope = resolveScope ctx
            let! docs = loadIndex storage scope.Container

            match docs |> List.tryFind (fun d -> d.Id = id) with
            | None ->
                return
                    fableSerialize {|
                        error =
                            "No document with that id is visible to this scope. It may belong to a different user or team, or have been deleted."
                        id = id
                    |}
            | Some doc ->
                // Mirrors KB's internal storage convention from
                // `Documents.fs` and `Narrative.fs` under
                // `src/ToolUp.KnowledgeBase.Server/Server/Api/`.
                let rawBlobName = sprintf "knowledge/%s/%s" doc.Id doc.FileName
                let! result = storage.Download(scope.Container, rawBlobName)

                match result with
                | Ok bytes ->
                    let content = Encoding.UTF8.GetString bytes

                    return
                        fableSerialize {|
                            id = doc.Id
                            fileName = doc.FileName
                            fileType = doc.FileType
                            source = doc.Source
                            chunkCount = doc.ChunkCount
                            content = content
                        |}
                | Error msg ->
                    return
                        fableSerialize {|
                            error = sprintf "Could not read document content: %s" msg
                            id = id
                        |}
}

// ─── Registration ────────────────────────────────────────────────

let private builtInTools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list = [
    (listDefinition, executeList)
    (getDefinition, executeGet)
]

/// Register KnowledgeBase's built-in AI tools (`list_kb_documents`,
/// `get_kb_document`) with an `AIServerApp`. Same `AICompose.register`
/// shape as `ToolUp.AI` itself — call once during composition root
/// assembly, before `AIServerApp.run` (or `RAGServerApp.run` via the
/// `rag.AI` lift).
///
///     AIServerApp.create factory configStore
///     |> AIServerApp.withConfig config
///     |> AIServerApp.addModules modules
///     |> ToolUp.KnowledgeBase.Server.AICompose.register
///     |> AIServerApp.run
///
/// Or for `RAGServerApp`:
///
///     |> (fun rag -> {
///         rag with AI = ToolUp.KnowledgeBase.Server.AICompose.register rag.AI
///     })
/// Warn when the Knowledge Base is composed on a declared multi-instance
/// deployment (`ReplicaCount > 1`). KB index read-modify-write
/// (`upsertIndexEntry` / `updateIndexStatus` / `updateIndexChunkCount` in
/// `IndexStorage`) is serialised by a per-process `SemaphoreSlim` — it
/// serialises within one process but NOT across replicas, so two pods
/// ingesting concurrently can lose an index entry or mismatch chunk
/// counts (the blob + chunks persist, but the `index.json` write is
/// clobbered). The cross-replica fix is ETag-conditional-write CAS on the
/// index blob (deferred). `Warning`, not `Error`: many KB deployments
/// funnel ingestion through a single writer in practice.
type private KnowledgeBaseIndexInstanceValidator(serverConfig: ServerConfig) =
    interface ConfigValidation.IConfigValidator with
        member _.Name = "knowledge-base:index-instance"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            if serverConfig.ReplicaCount > 1 then
                return
                    ConfigValidation.ValidationResult.Warning(
                        sprintf
                            "Knowledge Base is composed with ServerConfig.ReplicaCount = %d. KB index read-modify-write (upsertIndexEntry / updateIndexStatus / updateIndexChunkCount) is serialised by a per-process lock only — two replicas ingesting concurrently can lose an index entry or mismatch chunk counts (the blob + chunks persist, but the index.json write is clobbered). Until cross-replica ETag-CAS lands, funnel ingestion through a single writer replica for multi-instance KB deployments."
                            serverConfig.ReplicaCount
                    )
            else
                return ConfigValidation.ValidationResult.Ok
        }

let register (app: AIServerApp) : AIServerApp =
    let baseWithTools = {
        app.Base with
            AITools = app.Base.AITools @ builtInTools
    }

    // Investigate-gaps #4 — surface the per-process index-lock hazard at
    // startup on a declared multi-instance KB deployment.
    let baseWithValidator =
        ServerApp.withConfigValidator
            (KnowledgeBaseIndexInstanceValidator(baseWithTools.Config) :> ConfigValidation.IConfigValidator)
            baseWithTools

    // Phase 54d — register the KB offboard-purge hook when the deployment
    // opted into the tenant-lifecycle substrate. Threaded through the
    // shared `Extensions.ServiceConfig` seam (the same idiom
    // `ServerApp.withCspContributor` uses), gated on the same
    // `TenantLifecycle = EnabledTenantLifecycle` switch the core
    // `ComposeTenantLifecycle` gates on. The hook self-`Skipped`s when no
    // `IBlobStorage` is composed, so the gate is the only condition.
    let registerLifecycleHook (s: IServiceCollection) : IServiceCollection =
        match baseWithValidator.Config.TenantLifecycle with
        | EnabledTenantLifecycle ->
            s.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> KnowledgeBaseLifecycle.create sp)
        | NoTenantLifecycle -> s

    // Phase 707 — the request-free narrative-commit door. Registered
    // unconditionally with the knowledge base, on the same argument the
    // fact companion registers its disclosure gate by: a deployment
    // cannot compose the knowledge base and then find the programmatic
    // door missing. It is a lazy factory over the container and holds no
    // state, so a deployment where nothing ever commits programmatically
    // pays one unused singleton and no call (GP 13). Its ABSENCE is what
    // makes every programmatic producer dormant on a deployment with no
    // knowledge base — which is the other half of that double gate, and
    // it is enforced by there being no other registration site.
    let registerNarrativeIngestor (s: IServiceCollection) : IServiceCollection =
        s.AddSingleton<INarrativeIngestor>(fun (sp: IServiceProvider) ->
            KnowledgeBase.ServerApiNarrativeIngestor.create sp)

    let baseWithLifecycle = {
        baseWithValidator with
            Extensions = {
                baseWithValidator.Extensions with
                    ServiceConfig =
                        let register (s: IServiceCollection) =
                            registerNarrativeIngestor (registerLifecycleHook s)

                        match baseWithValidator.Extensions.ServiceConfig with
                        | None -> Some register
                        | Some baseFn -> Some(fun s -> register (baseFn s))
            }
    }

    { app with Base = baseWithLifecycle }
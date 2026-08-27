module KnowledgeBase.ServerInventory

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AI.SystemPromptBuilder
open SharedTypes
open KnowledgeBase.ServerJsonHelpers
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerAIContext

// ─── Inventory partition helper ──────────────────────────────────

/// Split an index into uploaded-or-narrative documents vs notes.
/// Both `publishInventoryUpdate` and `formatInventory` need this split,
/// so it lives here as a single helper rather than being repeated.
///
/// Both of them had gone back to repeating it inline anyway — four copies
/// of the same predicate across two functions — and neither called this.
/// Phase 626's unreferenced-code report is what surfaced that, which is
/// the useful shape of that tool's output: a helper with no callers whose
/// doc comment says it has two is not dead code, it is a helper that lost
/// its call sites. Rewired 2026-08-26 (tidy-drain).
let private partitionInventory (docs: KnowledgeDocument list) =
    docs
    |> List.partition (fun d ->
        match d.Source with
        | Note _ -> false
        | UploadedFile
        | FromNarrative _ -> true)

// ─── Inventory notification publisher ────────────────────────────

let publishInventoryUpdate
    (storage: IBlobStorage)
    (channel: INotificationChannel)
    (logger: ILogger)
    (userId: string)
    (container: string)
    : Async<unit> =
    async {
        try
            let! docs = loadIndex storage container
            let! aiContext = loadAIContext storage container

            // Heuristic-only suggested questions (WS4.4) — sample doc
            // names + notes to seed the AI panel's zero-state. Server-
            // side AI-generated suggestions are a follow-up. Carrying
            // them in the inventory payload keeps the AI client free
            // of any KB compile-time dependency.
            let documents, notes = partitionInventory docs
            let documentCount = documents.Length
            let noteCount = notes.Length

            let topDocs = documents |> List.sortByDescending _.UploadedAt |> List.truncate 3

            let suggestedQuestions =
                [
                    for doc in topDocs do
                        sprintf "What's in \"%s\"?" doc.FileName

                    if not notes.IsEmpty then
                        "Summarise the team's notes."

                    if documents.Length >= 2 then
                        "What themes do these documents share?"
                ]
                |> List.distinct
                |> List.truncate 5

            let payload: InventorySummary = {
                DocumentCount = documentCount
                NoteCount = noteCount
                HasAIContext = Option.isSome aiContext
                LastUpdated = DateTime.UtcNow
                SuggestedQuestions = suggestedQuestions
            }

            let payloadJson = toJson payload
            let notification = CustomNotification(InventoryUpdatedNotificationKey, payloadJson)
            do! channel.Publish(userId, notification)
        with ex ->
            logger.Warn(sprintf "[KnowledgeBase] Failed to publish InventoryUpdated notification: %s" ex.Message)
    }

// ─── Inventory cache + formatter + system-prompt builder ────────

/// Per-scope cache for `kbInventoryBuilder` — value is `(expiresAt, payload)`.
/// 30-second TTL keeps the index read off the prompt-build hot path while
/// staying short enough that newly uploaded documents appear in the inventory
/// almost immediately. Keyed by container so each team / user / deployment
/// scope has its own entry.
let private inventoryCache = ConcurrentDictionary<string, DateTimeOffset * string>()

let private inventoryCacheTtl = TimeSpan.FromSeconds 30.0

/// Drop the cached inventory payload for a container. Called from
/// document mutation paths (delete / reset / re-ingest) so the next
/// system-prompt build re-reads the index instead of returning a
/// stale "X documents" string for up to 30 s after the change.
let invalidateInventoryCache (container: string) : unit =
    inventoryCache.TryRemove(container) |> ignore

let private formatInventory (docs: KnowledgeDocument list) : string =
    let documents, notes = partitionInventory docs

    let docCount = documents.Length
    let noteCount = notes.Length

    if docCount = 0 && noteCount = 0 then
        ""
    else
        let mostRecent =
            documents
            |> List.sortByDescending _.UploadedAt
            |> List.tryHead
            |> Option.map _.FileName

        let docsClause =
            if docCount = 0 then ""
            elif docCount = 1 then "1 document"
            else sprintf "%d documents" docCount

        let notesClause =
            if noteCount = 0 then ""
            elif noteCount = 1 then "1 note"
            else sprintf "%d notes" noteCount

        let counts =
            [ docsClause; notesClause ]
            |> List.filter (fun s -> s <> "")
            |> String.concat " and "

        let recentClause =
            match mostRecent with
            | Some name when docCount > 0 -> sprintf " The most recently uploaded document is \"%s\"." name
            | _ -> ""

        sprintf "The team's knowledge base currently contains %s.%s" counts recentClause

/// Reads the team's KB document index from `knowledge/index.json` and emits
/// a one-paragraph summary of its contents — total documents, total notes,
/// and the most recently uploaded document. Cached per-scope with a 30-second
/// TTL so prompt building doesn't hit blob storage on every turn.
///
/// Pairs with the RAG-awareness preamble RAG composition contributes:
/// the preamble tells the model what the KB is and that it's been searched;
/// this builder tells the model how much is in there. Together they prevent
/// the model from guessing the corpus is empty when it isn't, or claiming
/// rich data when there's only a single note.
///
/// Returns `""` on Anonymous mode, no scope, an empty inventory, or a read
/// failure — `SystemPromptBuilder.compose` drops empty contributions silently.
/// Composed by the deployment alongside the platform prefix, active-module
/// context, and current-page narrative; KB exports the builder here so AI
/// stays free of any compile-time dependency on KB.
let kbInventoryBuilder (storage: IBlobStorage) (logger: ILogger option) : SystemPromptBuilder =
    fun ctx -> async {
        match AccessContext.configScope ctx.Access with
        | None -> return ""
        | Some scope ->
            try
                let now = DateTimeOffset.UtcNow

                match inventoryCache.TryGetValue scope.Container with
                | true, (expiresAt, payload) when expiresAt > now -> return payload
                | _ ->
                    let! docs = loadIndex storage scope.Container
                    let payload = formatInventory docs

                    inventoryCache.AddOrUpdate(
                        scope.Container,
                        (now + inventoryCacheTtl, payload),
                        fun _ _ -> (now + inventoryCacheTtl, payload)
                    )
                    |> ignore

                    return payload
            with ex ->
                logger
                |> Option.iter (fun l ->
                    l.Warn(sprintf "[KnowledgeBase] kbInventoryBuilder read failed: %s" ex.Message))

                return ""
    }

// ─── Convenience: KB system-prompt builders ──────────────────────

/// Returns the canonical pair of KB system-prompt builders the deployment
/// composition root drops into composeWithAI's prompt list — kbInventoryBuilder
/// then standingContextBuilder. The order matches the recommended layering:
/// inventory comes first so the model knows the corpus shape before it reads
/// any standing context.
let knowledgeBasePromptBuilders (storage: IBlobStorage) (logger: ILogger option) : SystemPromptBuilder list = [
    kbInventoryBuilder storage logger
    standingContextBuilder storage logger
]
module ToolUp.AI.AIAssistantHandler

open ToolUp.Platform.Transport
open System
open System.Text
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.AIAgentEngine
open ToolUp.AI.SSEHandler

// ─── Every conversation-addressed entry point, and its rule ──────
//
// Checked against the code below; `docs/ai/conversation-visibility.md`
// carries the same table for readers of the docs.
//
//   GetConversation            `canSee`; an elevated open is
//                              audited (`ConversationVisibilityAudit.elevatedRead`).
//   ListConversations,         every row through `canSee` (`visibleRows`).
//   ListConversationsPage
//   DeleteConversation,        `authoriseWrite`: invisible is a no-op,
//   SetConversationOverride    otherwise the author or `canModify`.
//   GetTaskStatus              keyed by the submitter; another member's
//                              task is `None` at every level.
//   SubmitMessage,             the ownership gate (`checkAppend`):
//   StreamChatV2               owner-only, and an ownerless
//                              legacy conversation under a narrowed level
//                              needs `canModify`. A refusal ends the turn —
//                              nothing of the conversation is read but the
//                              gate's own read, nothing is written. The
//                              opt-in substrate's prompt history is read
//                              only when `canSee` admits.
//   fast-path beacon           the same gate, `403` on refusal
//                              (`FastPathBeaconHandler`).
//
// There is no rename endpoint on `AIAssistantApi`. The data-subject export
// is `ConversationExporter.Export`, an operator-initiated read of the
// subject's own conversations, pinned with that reason in a fitness case.

// ─── JSON helpers ────────────────────────────────────────────────
// Uses FableConverters (from ToolUp.Remoting.Json.SystemTextJson) so
// that persisted conversations with F# DUs serialize in Fable-
// compatible format.

let private jsonOptions = FableConverters.create ()

let private toJson obj =
    JsonSerializer.Serialize(obj, jsonOptions)

// ─── Bounded waits on the chat path ──────────────────────────────
//
// Provider resolution, history loads and conversation loads each touch
// I/O (blob storage, secret store, config store). Each is bounded to 10 s,
// so a wedged backend fails the turn inline — its `TimeoutException` is the
// turn's `TurnFailure.Faulted` — instead of presenting as the client's
// silent 60-second watchdog.

[<Literal>]
let private ChatPathIoTimeoutMs = 10_000

/// Run `work` with a wall-clock cap. The work runs under a token linked to
/// the caller's, which the cap cancels; the wait ends at the cap whether or
/// not the work observes it, raising `TimeoutException` carrying `label`.
let private withTimeout (label: string) (timeoutMs: int) (work: Async<'T>) : Async<'T> = async {
    let! outer = Async.CancellationToken
    let cap = CancellationTokenSource.CreateLinkedTokenSource outer
    let running = Async.StartAsTask(work, cancellationToken = cap.Token)
    // Released once the work settles, which may be after the cap.
    running.ContinueWith(fun (_: Task<'T>) -> cap.Dispose()) |> ignore
    let! winner = Task.WhenAny(running :> Task, Task.Delay timeoutMs) |> Async.AwaitTask

    if winner = (running :> Task) then
        return! running |> Async.AwaitTask
    else
        cap.Cancel()
        return raise (TimeoutException $"{label} timed out after {timeoutMs / 1000}s")
}

// ─── Conversation persistence ────────────────────────────────────
//
// A conversation is four sibling blobs, read and written through the
// conversation record (`ConversationBlobs`), shared with the fast-path
// beacon handler (the other writer of an exchange):
//
//   ai-conversations/{id}.json          UI-facing `ConversationMessage list`
//                                        (user/assistant text only — what the
//                                        chat panel renders).
//   ai-conversations/{id}.history.json  Full `AIProviderMessage list` — every
//                                        tool_use / tool_result pair intact,
//                                        round-trippable back to the AI
//                                        provider on the next turn.
//   ai-conversations/{id}.meta.json     Server-only fields — the provider
//                                        override and the generated title.
//   ai-conversations/{id}.consent.json  The cross-module consent record.
//
// Why the UI and the history are two blobs:
// The provider round-trip requires the full history (tool_use in assistant
// messages paired with tool_result in user messages — Claude rejects
// requests that break pairing with HTTP 400). The UI does not display
// those intermediate turns and the shared `ConversationMessage` type does
// not carry tool_use / tool_result shapes. Keeping the two stores parallel
// means:
//   - The UI contract (`GetConversation` → `ConversationMessage list`) is
//     unchanged; clients see the same shape they always have.
//   - The agent loop gets the complete history every turn; no more orphaned
//     tool_result blocks.
//   - A conversation with no history blob starts from its UI blob.
//
// A sibling that is present but cannot be read is a typed error in the
// record, never an empty value: this handler refuses the turn (or the
// write) rather than write over what it could not read.

/// The turn's failure for an unreadable conversation blob. The operator
/// detail (blob, reason) goes to the log; the client is shown
/// `TurnFailure.message`, and the stored history is left as it was.
let private unreadable (logger: ILogger) (conversationId: Guid) (err: ConversationBlobs.ConversationBlobError) =
    let detail = ConversationBlobs.ConversationBlobError.describe err
    logger.Warn $"AI conversation {conversationId}: {detail}."
    TurnFailure.RecordUnreadable(conversationId, detail)

/// The turn's failure for an exchange that could not be written; a failed
/// save is never reported as a completed turn.
let private unwritten (logger: ILogger) (conversationId: Guid) (err: ConversationBlobs.ConversationBlobError) =
    let detail = ConversationBlobs.ConversationBlobError.describe err
    logger.Error($"AI conversation {conversationId}: {detail}.", None)
    TurnFailure.RecordUnwritten(conversationId, detail)

// ─── Task status for the polling fallback ────────────────────────
//
// `GetTaskStatus` used to return `None` unconditionally, so a client that
// missed the terminal SSE event had nothing to poll. Every status a turn
// reaches is already emitted exactly once, through the `emit` sink the
// turn is built over; the registry records what went through it.
//
// Process-local by design, and for the same reason the SSE stream and
// the chat worker are: a task runs on the process that accepted it, and
// that process is the only one that can say where it got to. Bounded two
// ways so a long-lived process never accumulates history — a terminal
// task is forgotten `retention` after it finished, and past `capacity`
// the oldest entries go first. Erasure reaches it too: `deleteSiblings`
// forgets a deleted or purged conversation's tasks, because an `AITask`
// carries the prompt the user typed.

type private TrackedTask = {
    Container: string
    Owner: string
    Task: AITask
}

/// The per-process record of each chat task's latest status.
/// `AITaskStatusRegistry.Shared` is the instance the handler uses;
/// construct one directly only in a test.
type AITaskStatusRegistry(capacity: int, retention: TimeSpan) =
    let tasks = System.Collections.Concurrent.ConcurrentDictionary<Guid, TrackedTask>()

    let mutable registrations = 0L

    let isTerminal (status: AITaskStatus) =
        match status with
        | AITaskCompleted
        | AITaskFailed _ -> true
        | Queued
        | InProgress -> false

    let isExpired (now: DateTime) (tracked: TrackedTask) =
        match tracked.Task.CompletedAt with
        | Some finished -> now - finished > retention
        | None -> false

    let evict (now: DateTime) =
        for KeyValue(id, tracked) in tasks do
            if isExpired now tracked then
                tasks.TryRemove id |> ignore

        let excess = tasks.Count - capacity

        if excess > 0 then
            tasks.Values
            |> Seq.sortBy (fun t -> t.Task.CreatedAt)
            |> Seq.truncate excess
            |> Seq.toList
            |> List.iter (fun t -> tasks.TryRemove t.Task.TaskId |> ignore)

    /// The process-wide instance: 10 000 tasks, terminal ones kept for an
    /// hour — ample for a client that reconnects after a dropped stream.
    static member val Shared = AITaskStatusRegistry(10_000, TimeSpan.FromHours 1.0)

    /// Record a task the moment it is accepted. `container` and `owner`
    /// are the storage container and user id that submitted it; a read
    /// must present both.
    member _.Register(container: string, owner: string, task: AITask, now: DateTime) =
        tasks[task.TaskId] <- {
            Container = container
            Owner = owner
            Task = task
        }

        let n = System.Threading.Interlocked.Increment(&registrations)

        if tasks.Count > capacity || n % 256L = 0L then
            evict now

    /// Apply a status the turn emitted. A terminal status stamps
    /// `CompletedAt`; once a task is terminal it stays so, because the
    /// first terminal event is the one the stream delivered.
    member _.Transition(taskId: Guid, status: AITaskStatus, now: DateTime) =
        match tasks.TryGetValue taskId with
        | true, tracked when not (isTerminal tracked.Task.Status) ->
            let completedAt = if isTerminal status then Some now else None

            tasks[taskId] <- {
                tracked with
                    Task = {
                        tracked.Task with
                            Status = status
                            CompletedAt = completedAt
                    }
            }
        | _ -> ()

    /// The task's latest status, for the caller that submitted it. `None`
    /// for an unknown id, an expired entry, or a different container or
    /// user — a task id is not a capability.
    member _.TryGet(container: string, owner: string, taskId: Guid, now: DateTime) : AITask option =
        match tasks.TryGetValue taskId with
        | true, tracked when isExpired now tracked ->
            tasks.TryRemove taskId |> ignore
            None
        | true, tracked when tracked.Container = container && tracked.Owner = owner -> Some tracked.Task
        | _ -> None

    /// Forget every task of a conversation — called when the conversation
    /// is erased, so no prompt outlives it here.
    member _.ForgetConversation(container: string, conversationId: Guid) =
        for KeyValue(id, tracked) in tasks do
            if tracked.Container = container && tracked.Task.ConversationId = conversationId then
                tasks.TryRemove id |> ignore

    /// How many tasks are held (diagnostics and tests).
    member _.Count = tasks.Count

// ─── Conversation retention sweep ────────────────────────────────
//
// The scope-level purge behind `ConversationRetentionPolicy`. The sibling
// set is the conversation record's (`ConversationRecord.siblingNames`), so
// a purge cannot drift from the record the first time a sibling is added.
// `DeleteConversation` and the sweep share ONE deletion
// (`ConversationRetention.deleteSiblings`), so an erasure and a retention
// purge remove exactly the same set.

/// Outcome of one retention sweep over one scope.
type ConversationPurgeReport = {
    /// Scope id the sweep was asked to visit.
    ScopeId: string
    /// Container it resolved to.
    Container: string
    /// Conversations enumerated (every `{id}.json` under the prefix).
    Examined: int
    /// Conversations whose every sibling blob was removed.
    Purged: Guid list
    /// Conversations the policy selected but a sibling delete failed
    /// for — left in place, retried by the next sweep. Each entry is
    /// `"{id}: {reason}"`.
    Failures: string list
}

module ConversationPurgeReport =
    let noOp (scopeId: string) (container: string) (examined: int) : ConversationPurgeReport = {
        ScopeId = scopeId
        Container = container
        Examined = examined
        Purged = []
        Failures = []
    }

    /// `true` when nothing selected was left behind.
    let isClean (report: ConversationPurgeReport) : bool = report.Failures.IsEmpty

    let summarise (report: ConversationPurgeReport) : string =
        sprintf
            "scope %s (%s): examined %d, purged %d, failed %d%s"
            report.ScopeId
            report.Container
            report.Examined
            report.Purged.Length
            report.Failures.Length
            (if report.Failures.IsEmpty then
                 ""
             else
                 " — " + String.concat "; " report.Failures)

module ConversationRetention =
    /// Every blob a conversation owns, in the order they are deleted — the
    /// conversation record's sibling set. The consent record is a sibling
    /// too: a per-conversation record of what the user agreed to is that
    /// user's data and goes with the conversation.
    let siblingBlobNames (conversationId: Guid) : string list =
        ConversationBlobs.ConversationRecord.siblingNames conversationId

    /// Delete EVERY sibling blob of a conversation, and any copy of its UI
    /// blob the record quarantined. `IBlobStorage.Delete` is idempotent
    /// (missing blob ⇒ `Ok`), so deleting all of them unconditionally is
    /// safe; the FIRST failure is surfaced so an incomplete erasure is
    /// retryable rather than silently partial. Shared by
    /// `DeleteConversation` (right-to-erasure) and the retention sweep —
    /// one deletion, one sibling set.
    let deleteSiblings
        (storage: IBlobStorage)
        (container: string)
        (conversationId: Guid)
        : Async<Result<unit, string>> =
        async {
            let! quarantined =
                storage.List(container, ConversationBlobs.ConversationRecord.quarantinePrefix conversationId)

            let! results =
                siblingBlobNames conversationId @ quarantined
                |> List.map (fun name -> storage.Delete(container, name))
                |> Async.Sequential

            let outcome =
                results
                |> Array.tryPick (function
                    | Error e -> Some(Error e)
                    | Ok() -> None)
                |> Option.defaultValue (Ok())

            // The task-status registry holds each task's prompt; an erased
            // conversation takes those entries with it.
            if Result.isOk outcome then
                AITaskStatusRegistry.Shared.ForgetConversation(container, conversationId)

            return outcome
        }

    /// Resolve the blob container for a scope id, mirroring the
    /// `team-{id}` / `user-{id}` convention the storage scope resolver
    /// uses: an already-prefixed id is taken verbatim; a bare id is a
    /// team id (the tenant unit). The job scheduler hands the sweep a
    /// scope id, not a container, so this is the same translation the
    /// Knowledge Base sweep performs.
    let containerOf (scopeId: string) : string =
        if
            scopeId.StartsWith("user-", StringComparison.Ordinal)
            || scopeId.StartsWith("team-", StringComparison.Ordinal)
        then
            scopeId
        else
            "team-" + scopeId

    /// Enumerate the conversation ids in a container — every
    /// `ai-conversations/{guid}.json`. Sibling blobs strip to
    /// `"{id}.history"` / `"{id}.meta"` / `"{id}.consent"`, which
    /// `Guid.TryParse` rejects, so they fall through (the same rule
    /// `ListConversations` applies).
    let listConversationIds (storage: IBlobStorage) (container: string) : Async<Guid list> = async {
        let! blobs = storage.List(container, "ai-conversations/")

        return
            blobs
            |> List.choose (fun blobName ->
                let fileName = blobName.Replace("ai-conversations/", "").Replace(".json", "")

                match Guid.TryParse(fileName) with
                | true, id -> Some id
                | _ -> None)
    }

    /// When a conversation was last active: the newest message
    /// timestamp in its UI blob, else the blob's own `LastModified`
    /// (an empty or unparseable conversation still has a write time).
    /// `None` when neither can be read — such a conversation is never
    /// selected for purge, because an undated row is a repair job, not
    /// an expired one.
    let lastActivity
        (logger: ILogger)
        (storage: IBlobStorage)
        (container: string)
        (conversationId: Guid)
        : Async<DateTime option> =
        async {
            let blobName = ConversationBlobs.conversationBlobName conversationId

            let fromMetadata () = async {
                match! storage.GetMetadata(container, blobName) with
                | Ok meta -> return Some meta.LastModified
                | Error _ -> return None
            }

            match! storage.Download(container, blobName) with
            | Ok bytes ->
                let parsed =
                    match ConversationBlobs.decodeConversation blobName bytes with
                    | Ok messages -> Some messages
                    | Error err ->
                        logger.Warn
                            $"AI conversation {conversationId}: {ConversationBlobs.ConversationBlobError.describe err}; dating it by its write time for retention."

                        None

                match parsed with
                | Some(_ :: _ as messages) -> return Some(messages |> List.map _.Timestamp |> List.max)
                | _ -> return! fromMetadata ()
            | Error _ -> return! fromMetadata ()
        }

    /// Sweep one container under `policy` at `now`: enumerate, date,
    /// select, delete every sibling of each expired conversation, and
    /// write ONE `ConversationsPurged` audit row when anything went.
    ///
    /// GP 11 / GP 13 — an inert policy reads nothing: there is no code
    /// path from `retainForever` to a delete. A run that expires
    /// nothing writes no audit row. A conversation whose sibling delete
    /// fails is reported in `Failures` and left for the next sweep.
    let sweepContainer
        (storage: IBlobStorage)
        (auditLog: IAuditLog option)
        (logger: ILogger)
        (now: DateTime)
        (policy: ConversationRetentionPolicy)
        (scopeId: string)
        (container: string)
        : Async<ConversationPurgeReport> =
        async {
            if ConversationRetentionPolicy.isInert policy then
                return ConversationPurgeReport.noOp scopeId container 0
            else
                let! ids = listConversationIds storage container

                let! dated =
                    ids
                    |> List.map (fun id -> async {
                        let! at = lastActivity logger storage container id
                        return at |> Option.map (fun at -> id, at)
                    })
                    |> Async.Sequential

                let conversations = dated |> Array.toList |> List.choose id
                let expired = ConversationRetentionPolicy.selectExpired now policy conversations

                if expired.IsEmpty then
                    return ConversationPurgeReport.noOp scopeId container ids.Length
                else
                    let! outcomes =
                        expired
                        |> List.map (fun id -> async {
                            match! deleteSiblings storage container id with
                            | Ok() -> return Ok id
                            | Error e -> return Error(sprintf "%O: %s" id e)
                        })
                        |> Async.Sequential

                    let purged = outcomes |> Array.toList |> List.choose Result.toOption

                    let failures =
                        outcomes
                        |> Array.toList
                        |> List.choose (function
                            | Error e -> Some e
                            | Ok _ -> None)

                    if not purged.IsEmpty then
                        match auditLog with
                        | Some audit ->
                            do!
                                audit.Record(
                                    scopeId,
                                    ConversationsPurged {
                                        ScopeId = scopeId
                                        ConversationIds = purged |> List.map string
                                        PurgedCount = purged.Length
                                        MaxAgeSeconds = policy.MaxAge |> Option.map (fun a -> int64 a.TotalSeconds)
                                        MaxCount = policy.MaxCount
                                        FailedCount = failures.Length
                                    }
                                )
                        | None -> ()

                        logger.Info(
                            sprintf
                                "[AI] Conversation retention sweep purged %d conversation(s) from scope %s (%d failed)."
                                purged.Length
                                scopeId
                                failures.Length
                        )

                    for failure in failures do
                        logger.Warn(
                            sprintf
                                "[AI] Conversation retention sweep left %s in scope %s; the next sweep retries it."
                                failure
                                scopeId
                        )

                    return {
                        ScopeId = scopeId
                        Container = container
                        Examined = ids.Length
                        Purged = purged
                        Failures = failures
                    }
        }

    /// Handler name the sweep job registers under. Namespaced against
    /// the AI module so it cannot clash with a consumer's own handlers.
    [<Literal>]
    let SweepHandlerName = "ai.conversation-retention-sweep"

/// The `IJobHandler` `withConversationRetention` registers. Resolves
/// its substrate from the provider on every `Execute` — nothing is
/// cached between invocations (GP 12 rule 4), so a distributed
/// scheduler may deactivate and rehydrate it freely. Sweeps
/// `ctx.ScopeId` only.
///
/// A sweep that left a selected conversation behind returns
/// `TransientFailure`: the shape is retryable by construction (the
/// next attempt re-selects the same rows), which is what
/// `JobRetryPolicy` backoff exists for.
type ConversationRetentionSweepJobHandler(services: IServiceProvider, policy: ConversationRetentionPolicy) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            let logger =
                match services.GetService typeof<ILogger> with
                | :? ILogger as l -> l
                | _ ->
                    { new ILogger with
                        member _.Debug _ = ()
                        member _.Info _ = ()
                        member _.Warn _ = ()
                        member _.Error(_, _) = ()
                    }

            match services.GetService typeof<IBlobStorage> with
            | :? IBlobStorage as storage ->
                let auditLog =
                    match services.GetService typeof<IAuditLog> with
                    | :? IAuditLog as a -> Some a
                    | _ -> None

                try
                    let! report =
                        ConversationRetention.sweepContainer
                            storage
                            auditLog
                            logger
                            DateTime.UtcNow
                            policy
                            ctx.ScopeId
                            (ConversationRetention.containerOf ctx.ScopeId)

                    if ConversationPurgeReport.isClean report then
                        return Success
                    else
                        return TransientFailure(ConversationPurgeReport.summarise report)
                with ex ->
                    logger.Error(sprintf "[AI] Conversation retention sweep failed for scope %s" ctx.ScopeId, Some ex)

                    return TransientFailure ex.Message
            | _ ->
                // No blob storage composed — conversations are written
                // through `IBlobStorage`, so there is nothing to sweep.
                // Permanent: a retry cannot conjure a store.
                return
                    PermanentFailure
                        "No IBlobStorage is registered; the AI conversation retention sweep has nothing to sweep."
        }

// ─── Real listing metadata, paging and search ────────────────────
//
// `ListConversations` used to return `Title = None`, `MessageCount = 0`
// and `DateTime.MinValue` for every row. The metadata is read from the
// same UI blob `GetConversation` serves and the retention sweep dates,
// so the list, the conversation view and the purge agree on what a
// conversation is: a conversation whose `{id}.json` is gone — deleted,
// or purged by `deleteSiblings` — is not listed, even while a sibling it
// used to own is still being removed.
//
// The listing does NOT read the `IConversationStore`: that substrate
// is opt-in (`NoConversationStore` is the default), while
// every conversation has its blob. Reading the blob is what makes the
// listing real in every composition.

/// One conversation as the listing reads it: the row the client sees,
/// plus the text a search is matched against (never returned).
type ConversationListingRow = {
    Conversation: Conversation
    /// The title and every message's text.
    SearchText: string list
}

module ConversationListing =
    /// Collapse runs of whitespace and cut `text` to at most `maxChars`
    /// characters, an ellipsis included. `None` for blank text.
    let truncateTitle (maxChars: int) (text: string) : string option =
        if String.IsNullOrWhiteSpace text then
            None
        else
            let collapsed =
                String.Join(" ", text.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries))

            let limit = max 1 maxChars

            if collapsed.Length <= limit then
                Some collapsed
            else
                Some(collapsed.Substring(0, limit - 1).TrimEnd() + "…")

    /// The title a conversation has when none was generated: its first
    /// user message, truncated.
    let fallbackTitle (maxChars: int) (messages: ConversationMessage list) : string option =
        messages
        |> List.tryFind (fun m -> m.Participant = User && not (String.IsNullOrWhiteSpace m.Content))
        |> Option.bind (fun m -> truncateTitle maxChars m.Content)

    let private idKey (id: Guid) = id.ToString("N")

    /// The listing order: newest activity first, ties broken by id so
    /// two reads of the same data page identically.
    let compareRows (a: Conversation) (b: Conversation) : int =
        match compare b.UpdatedAt.Ticks a.UpdatedAt.Ticks with
        | 0 -> String.CompareOrdinal(idKey a.Id, idKey b.Id)
        | c -> c

    /// The keyset cursor naming `row` as the last one a page returned.
    let encodeCursor (row: Conversation) : string =
        sprintf "%d.%s" row.UpdatedAt.Ticks (idKey row.Id)

    let private tryDecodeCursor (cursor: string) : (int64 * string) option =
        match cursor.Split('.') with
        | [| ticks; id |] ->
            match Int64.TryParse ticks, Guid.TryParseExact(id, "N") with
            | (true, t), (true, g) -> Some(t, idKey g)
            | _ -> None
        | _ -> None

    let private isAfter (ticks: int64, key: string) (row: Conversation) =
        row.UpdatedAt.Ticks < ticks
        || (row.UpdatedAt.Ticks = ticks && String.CompareOrdinal(idKey row.Id, key) > 0)

    /// Whether `row` matches `search`: a case-insensitive substring of
    /// its title or of any message's text. A blank search matches all.
    let matches (search: string option) (row: ConversationListingRow) : bool =
        match search with
        | Some term when not (String.IsNullOrWhiteSpace term) ->
            let term = term.Trim()

            row.SearchText
            |> List.exists (fun text -> not (isNull text) && text.Contains(term, StringComparison.OrdinalIgnoreCase))
        | _ -> true

    /// One page of `rows` under `query`. Pure — the handler and the
    /// tests share it. A cursor that does not decode yields an empty
    /// last page rather than restarting at the first, so a client
    /// holding a corrupt cursor cannot loop over the same rows forever.
    let page (query: ConversationListQuery) (rows: ConversationListingRow list) : ConversationPage =
        let size = query.PageSize |> max 1 |> min ConversationListQuery.MaxPageSize

        let matching =
            rows
            |> List.filter (matches query.Search)
            |> List.sortWith (fun a b -> compareRows a.Conversation b.Conversation)

        let remaining =
            match query.Cursor with
            | None -> Some matching
            | Some cursor when String.IsNullOrWhiteSpace cursor -> Some matching
            | Some cursor ->
                tryDecodeCursor cursor
                |> Option.map (fun key -> matching |> List.filter (fun r -> isAfter key r.Conversation))

        match remaining with
        | None -> {
            Items = []
            NextCursor = None
            TotalCount = matching.Length
          }
        | Some rest ->
            let items = rest |> List.truncate size

            {
                Items = items |> List.map _.Conversation
                NextCursor =
                    if rest.Length > size then
                        items |> List.tryLast |> Option.map (fun r -> encodeCursor r.Conversation)
                    else
                        None
                TotalCount = matching.Length
            }

    /// A conversation's stored author: the `CreatedBy` its first persisted
    /// message recorded, `""` when none was recorded. Always read from the
    /// stored conversation, never from a request.
    let ownerOf (messages: ConversationMessage list) : string =
        match messages with
        | first :: _ when not (String.IsNullOrEmpty first.CreatedBy) -> first.CreatedBy
        | _ -> ""

    /// Read one conversation's listing row together with its stored author
    /// (the visibility filter needs both). `None` when its UI blob can
    /// no longer be read.
    let readOwnedRow
        (logger: ILogger)
        (storage: IBlobStorage)
        (container: string)
        (maxTitleChars: int)
        (conversationId: Guid)
        : Async<(ConversationListingRow * string) option> =
        async {
            let blobName = ConversationBlobs.conversationBlobName conversationId

            match! storage.Download(container, blobName) with
            | Error _ -> return None
            | Ok bytes ->
                // Read-only: an undecodable conversation is still LISTED
                // (with no messages) so it stays visible to its owner; the
                // listing writes nothing, so nothing is overwritten.
                let messages =
                    match ConversationBlobs.decodeConversation blobName bytes with
                    | Ok messages -> messages
                    | Error err ->
                        logger.Warn
                            $"AI conversation {conversationId}: {ConversationBlobs.ConversationBlobError.describe err}; listing it with no messages."

                        []

                // Read-only too: unreadable metadata lists the conversation
                // under its first-message title.
                let! meta = async {
                    match! ConversationBlobs.loadMeta storage container conversationId with
                    | Ok meta -> return meta
                    | Error err ->
                        logger.Warn
                            $"AI conversation {conversationId}: {ConversationBlobs.ConversationBlobError.describe err}; listing it under its first message."

                        return ConversationBlobs.ConversationMeta.empty
                }

                let! createdAt, updatedAt =
                    match messages with
                    | _ :: _ ->
                        let stamps = messages |> List.map _.Timestamp
                        async.Return(List.min stamps, List.max stamps)
                    | [] -> async {
                        match! storage.GetMetadata(container, blobName) with
                        | Ok m -> return m.LastModified, m.LastModified
                        | Error _ -> return DateTime.MinValue, DateTime.MinValue
                      }

                let title =
                    match meta.Title with
                    | Some t when not (String.IsNullOrWhiteSpace t) -> Some t
                    | _ -> fallbackTitle maxTitleChars messages

                let row = {
                    Conversation = {
                        Id = conversationId
                        Title = title
                        CreatedAt = createdAt
                        UpdatedAt = updatedAt
                        MessageCount = messages.Length
                        OverrideProviderLabel = meta.OverrideProviderLabel
                    }
                    SearchText = [
                        yield! Option.toList title
                        for m in messages do
                            if not (String.IsNullOrEmpty m.Content) then
                                m.Content
                    ]
                }

                return Some(row, ownerOf messages)
        }

    /// Read one conversation's listing row. `None` when its UI blob can
    /// no longer be read — the conversation was deleted or purged after
    /// the container was enumerated, and must not reappear in the list.
    let readRow
        (logger: ILogger)
        (storage: IBlobStorage)
        (container: string)
        (maxTitleChars: int)
        (conversationId: Guid)
        : Async<ConversationListingRow option> =
        async {
            let! owned = readOwnedRow logger storage container maxTitleChars conversationId
            return owned |> Option.map fst
        }

    /// Every conversation in `container` with its stored author, in
    /// listing order. At most 16 reads are in flight, so a large scope
    /// does not open one blob read per conversation at once.
    let readAllOwned
        (logger: ILogger)
        (storage: IBlobStorage)
        (container: string)
        (maxTitleChars: int)
        : Async<(ConversationListingRow * string) list> =
        async {
            let! ids = ConversationRetention.listConversationIds storage container

            let! rows =
                Async.Parallel(
                    ids |> List.map (readOwnedRow logger storage container maxTitleChars),
                    maxDegreeOfParallelism = 16
                )

            return
                rows
                |> Array.toList
                |> List.choose id
                |> List.sortWith (fun (a, _) (b, _) -> compareRows a.Conversation b.Conversation)
        }

    /// Every conversation in `container`, in listing order. At most 16
    /// reads are in flight, so a large scope does not open one blob read
    /// per conversation at once.
    let readAll
        (logger: ILogger)
        (storage: IBlobStorage)
        (container: string)
        (maxTitleChars: int)
        : Async<ConversationListingRow list> =
        async {
            let! rows = readAllOwned logger storage container maxTitleChars
            return rows |> List.map fst
        }

// ─── Conversation titling ────────────────────────────────────────

/// How conversations get their list titles. Composed with
/// `AICompose.withConversationTitling`; a deployment that composes
/// nothing gets `firstMessage` — titles derived from the first user
/// message, and no provider call it did not ask for.
type ConversationTitlingPolicy = {
    /// When true, a conversation's first completed turn is followed by
    /// ONE provider call asking for a short title, made after the turn's
    /// terminal event so it never delays the reply. The call runs on the
    /// provider that served the turn, so it is metered, budgeted and
    /// failed-over like every other call of that turn. On any failure
    /// the title stays derived from the first message: titling never
    /// blocks or empties the list.
    Generate: bool
    /// The model the titling call asks for through the per-call override
    /// — name a small, cheap model. `None` titles on the conversation's
    /// own model. A provider that cannot serve the id
    /// serves the call on its configured model rather than failing it.
    Model: string option
    /// Characters of the first message sent to the titling call — the
    /// input half of the budget cap.
    MaxInputChars: int
    /// Longest title kept, generated or derived — the output half.
    MaxTitleChars: int
    /// Wall-clock bound on the titling call. One attempt, no retries.
    Timeout: TimeSpan
}

module ConversationTitlingPolicy =
    /// No titling call: every title is the first user message, truncated.
    let firstMessage: ConversationTitlingPolicy = {
        Generate = false
        Model = None
        MaxInputChars = 1000
        MaxTitleChars = 60
        Timeout = TimeSpan.FromSeconds 10.0
    }

    /// Generate titles on the conversation's own model.
    let generated: ConversationTitlingPolicy = { firstMessage with Generate = true }

    /// Generate titles on `model` (a small, cheap one).
    let generatedOn (model: string) : ConversationTitlingPolicy = { generated with Model = Some model }

module ConversationTitling =
    /// The titling call's system prompt.
    [<Literal>]
    let Instruction =
        "Write a short title of at most eight words for a conversation that begins with the user message you are given. Reply with the title only: no quotation marks, no preamble, no trailing punctuation."

    /// One attempt, bounded by the policy's timeout.
    let retryPolicy (policy: ConversationTitlingPolicy) : RetryPolicy = {
        MaxAttempts = 1
        InitialBackoff = TimeSpan.Zero
        MaxBackoff = TimeSpan.Zero
        Timeout = Some policy.Timeout
    }

    let private decoration = [| '"'; '\''; '*'; '#'; '`'; '“'; '”' |]

    /// The first non-blank line of a model's reply, stripped of the
    /// quoting and label a model adds despite being asked not to, then
    /// truncated. `None` when nothing is left.
    let cleanTitle (maxChars: int) (raw: string) : string option =
        if isNull raw then
            None
        else
            raw.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun line ->
                let line = line.Trim()

                let unlabelled =
                    if line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase) then
                        line.Substring(6)
                    else
                        line

                unlabelled.Trim().Trim(decoration).Trim().TrimEnd('.').Trim())
            |> Array.tryFind (fun line -> line <> "")
            |> Option.bind (ConversationListing.truncateTitle maxChars)

    /// Ask `provider` for a title for a conversation opening with
    /// `firstMessage`. `Error` carries the reason for a log line — it
    /// never carries message content — and is what the caller degrades
    /// on; nothing here throws.
    let generate
        (policy: ConversationTitlingPolicy)
        (provider: IAIProvider)
        (firstMessage: string)
        : Async<Result<string, string>> =
        async {
            match ConversationListing.truncateTitle policy.MaxInputChars firstMessage with
            | None -> return Error "the first message has no text to title"
            | Some input ->
                try
                    let options =
                        policy.Model
                        |> Option.map AIProviderCallOptions.forModel
                        |> Option.defaultValue AIProviderCallOptions.none

                    let message: AIProviderMessage = {
                        Role = "user"
                        Content = input
                        ToolCalls = []
                        ToolResults = []
                        Parts = []
                    }

                    let! result =
                        provider.SendMessageWith(options, [ message ], [], Some Instruction, None, retryPolicy policy)
                        |> withTimeout "Conversation titling" (int policy.Timeout.TotalMilliseconds + 1000)

                    match result with
                    | Ok served ->
                        match cleanTitle policy.MaxTitleChars served.Response.Content with
                        | Some title -> return Ok title
                        | None -> return Error "the provider returned an empty title"
                    | Error err -> return Error(AIProviderError.toMessage err)
                with ex ->
                    return Error ex.Message
        }

    /// The title a conversation opening with `firstMessage` shows under
    /// `policy`: generated when the policy asks and the call succeeds,
    /// else the first message truncated.
    let titleOrFallback
        (policy: ConversationTitlingPolicy)
        (provider: IAIProvider)
        (firstMessage: string)
        : Async<string option> =
        async {
            if policy.Generate then
                match! generate policy provider firstMessage with
                | Ok title -> return Some title
                | Error _ -> return ConversationListing.truncateTitle policy.MaxTitleChars firstMessage
            else
                return ConversationListing.truncateTitle policy.MaxTitleChars firstMessage
        }

    /// Title a conversation after its first turn and persist the result
    /// on its meta sibling, preserving the provider override stored
    /// there. A failed call persists nothing, so the listing keeps the
    /// first-message title; a no-op unless `policy.Generate`. Never
    /// throws — it runs after the turn's terminal event, where an
    /// exception would reach the turn's failure handler.
    let titleConversation
        (logger: ILogger)
        (storage: IBlobStorage)
        (container: string)
        (conversationId: Guid)
        (policy: ConversationTitlingPolicy)
        (provider: IAIProvider)
        (firstMessage: string)
        : Async<unit> =
        async {
            if policy.Generate then
                try
                    match! generate policy provider firstMessage with
                    | Ok title ->
                        match!
                            ConversationBlobs.ConversationRecord.updateMeta
                                storage
                                container
                                conversationId
                                (fun meta -> { meta with Title = Some title })
                        with
                        | Ok() -> ()
                        | Error err ->
                            logger.Warn
                                $"AI conversation {conversationId} title could not be saved ({ConversationBlobs.ConversationBlobError.describe err}); the list shows its first message instead."
                    | Error reason ->
                        logger.Warn
                            $"AI conversation {conversationId} titling failed ({reason}); the list shows its first message instead."
                with ex ->
                    logger.Warn
                        $"AI conversation {conversationId} title could not be saved ({ex.Message}); the list shows its first message instead."
        }

// ─── Provider message conversion ─────────────────────────────────

let private toProviderMessage (msg: ConversationMessage) : AIProviderMessage = {
    Role =
        match msg.Participant with
        | User -> "user"
        | AIAssistant -> "assistant"
        | System -> "system"
    Content = msg.Content
    ToolCalls =
        msg.ToolCalls
        |> List.map (fun tc -> {
            AIProviderToolCall.Id = string tc.ToolCallId
            Name = tc.ToolName
            Arguments = tc.Arguments
        })
    ToolResults = []
    // Round-trip multipart content through history replay. A
    // multipart user turn persisted on a prior request reaches the
    // provider as the same content-block array on the next turn;
    // plain-text turns continue to carry `Parts = []` and the
    // provider emits the legacy string shape.
    Parts = msg.Parts
}

let private toConversationMessage
    (conversationId: Guid)
    (participant: ParticipantType)
    (content: string)
    (toolCalls: ToolCallRecord list)
    (retrievedSources: RetrievedSource list)
    (parts: AIContentPart list)
    (createdBy: string)
    (verification: AnswerVerification option)
    : ConversationMessage =
    {
        Id = Guid.NewGuid()
        ConversationId = conversationId
        Participant = participant
        Content = content
        Timestamp = DateTime.UtcNow
        ToolCalls = toolCalls
        RetrievedSources = retrievedSources
        Parts = parts
        // Populated for the first persisted user message of
        // a conversation; subsequent appends are gated against it by the
        // ownership check inside `bgWork`. Empty for assistant turns
        // (only the first message's `CreatedBy` is authoritative).
        CreatedBy = createdBy
        // Beacon idempotency key. Always empty here: this is
        // the ordinary agent-loop persistence path, which no client
        // retries against a key. Only `FastPathBeaconHandler` stamps a
        // non-empty value, and only it scans for one.
        BeaconId = ""
        // Numeric-fidelity verdict; `None` for user turns and
        // for assistant turns produced with the gate `Off` (the default).
        Verification = verification
    }

// ─── Background context ──────────────────────────────────────────

/// Create a synthetic HttpContext with a new DI scope for background work.
/// The original request HttpContext is disposed after the response is sent,
/// so background work (the agent loop) needs its own scope.
///
/// Copies the pre-resolved scope / user / permission items from the
/// original request into the background context's `Items` dictionary so
/// any tool that reads them (`ToolUp.StorageScope` in particular) hits
/// the same store the Data Manager API used on the originating request.
/// Without this copy the agent loop falls back to `getStoreForUser`
/// which resolves a different container in every mode EXCEPT the one
/// where it happens to match — in Anonymous mode the user's files live
/// in `session-{sessionId}` and the fallback looks in `user-{userId}`,
/// which is always empty, so tools see no uploaded files at all.
let private createBackgroundContext (ctx: HttpContext) (userId: string) =
    let scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>()

    let scope = scopeFactory.CreateScope()
    let bgCtx = DefaultHttpContext()
    bgCtx.RequestServices <- scope.ServiceProvider
    // Preserve user identity headers so scope resolvers work
    bgCtx.Request.Headers.Append("X-User-Id", userId)

    // Carry forward the resolved scope / user / permission state from the
    // middleware-decorated original context. Keys are the exact strings
    // populated by `ScopeResolutionMiddleware` in `SDK.Server.fs`; keeping
    // the list in lockstep with that middleware is a stringly-typed
    // coupling a typed `RequestContext` would remove.
    //
    // The list carries all six items `reconstructAccessContext` reads.
    // Dropping any of them would make the agent loop rebuild `Subject` from
    // the scope-container prefix rather than carry the one the request
    // resolved, or leave `ModuleExposure` / `PlatformRole` empty — every
    // authorization row the loop emits would then attribute a subject it
    // inferred, and the whole point of resolving a subject once is that
    // nothing downstream has to guess.
    //
    // The two grant stamps are load-bearing for the tool gate. Both are
    // resolved PER REQUEST by the middleware — the consent verdicts
    // especially, because a revocation must bite at the next call rather
    // than the next sweep. A
    // background context that did not carry them would leave the tool gate
    // reading `Map.empty`, which reads as "no grant, no consent" and would
    // therefore fail CLOSED rather than open. Safe, but wrong in the other
    // direction: every governed module would vanish from the model's tool
    // list even for a subject who legitimately holds it.
    let itemsToCopy = [
        "ToolUp.StorageScope"
        "ToolUp.UserId"
        "ToolUp.ModulePermissions"
        "ToolUp.Subject"
        "ToolUp.ModuleExposure"
        "ToolUp.PlatformRole"
        // The grant + consent stamps. Named through the constants that
        // DEFINE them, not re-spelled, so a rename moves both ends together.
        GrantPolicyGuard.ModuleGrantsItemsKey
        GrantConsentStore.ModuleGrantConsentsItemsKey
    ]

    for key in itemsToCopy do
        match ctx.Items.TryGetValue key with
        | true, value -> bgCtx.Items[key] <- value
        | _ -> ()

    // The scope the platform resolved for the request. The fact tools read
    // only that (`ScopeResolution.forRequest`), and the list above does not
    // carry it, so without this every fact tool in a chat turn would read the
    // anonymous shard. `carry` records the request's own resolution and mints
    // nothing: a request the platform resolved no scope for stays anonymous.
    ScopeResolution.carry bgCtx (ScopeResolution.forRequest ctx)

    bgCtx, scope

// ─── Typed streaming surface ─────────────────────────────────────
//
// Server-only companion to `AIAssistantApi`. A streaming method returns
// `IAsyncEnumerable<'T>`, which the ToolUp.Remoting dispatcher auto-frames
// as Server-Sent Events. This record is deliberately
// NOT in `ToolUp.AI.Core` (Fable-shared): `IAsyncEnumerable` has no Fable
// client representation, so the typed-streaming wire is consumed as a plain
// SSE source (fetch / NotificationClient), not via the ToolUp.Remoting proxy.
// Streaming methods may carry only `[<AllowAnonymous>]` / `[<PublicEndpoint>]`
// (the dispatcher refuses to start if a streaming method carries an
// unenforceable pre-flight attribute); the turn is gated inside the handler
// exactly as the legacy SSE path is.

/// Typed streaming chat surface. `StreamChatV2` is the versioned
/// typed analogue of the legacy `SubmitMessage` + `/api/ai/events`
/// SSE pair; the legacy path stays mounted unchanged.
type AIStreamingApi = {
    [<AllowAnonymous>]
    StreamChatV2: AIMessageRequest -> IAsyncEnumerable<AIStreamEvent>
}

/// Terminal-event predicate for the typed chat stream: the turn
/// ends when its task reaches a terminal status. Used by the
/// `AsyncStream.fromCallback` bridge to close the stream after the terminal
/// `TaskStatusChanged` is yielded.
let private isTerminalStreamEvent =
    function
    | TaskStatusChanged(_, AITaskCompleted)
    | TaskStatusChanged(_, AITaskFailed _) -> true
    | _ -> false

// ─── API implementation ──────────────────────────────────────────

/// Build the AIAssistantApi ToolUp.Remoting handler + its typed streaming
/// companion. Both records are built in one closure over ONE turn
/// implementation (`makeAssistantApi`): the legacy record takes the
/// SSE-broadcast sink, the streaming method takes the stream's channel
/// sink, and nothing else differs.
/// Mirrors the pattern from fileManagementApi in FileManagement.fs.
/// What `SubmitMessage` resolves in the foreground and hands the turn.
type private TurnRequest = {
    TaskId: Guid
    Request: AIMessageRequest
    /// The authoritative surface (`AISurfaceCapability`).
    Surface: AISurface
    /// What the prompt builder contributed.
    Contribution: SystemPromptBuilder.PromptContribution
    /// The turn's model input, built from the contribution.
    Input: ModelInput
    /// The background context the turn runs on, with its own DI scope.
    Background: HttpContext
    /// The request's access, for the ownership gate.
    GateAccess: AccessContext option
    /// The turn's stream.
    Emit: AIStreamEvent -> unit
}

/// The turn's model input: the composed prompt verbatim as one block, so
/// rendering reproduces it byte for byte, and each retrieved source as a
/// chunk carrying `NotGated` until a gate result is supplied.
let private modelInputOf (contribution: SystemPromptBuilder.PromptContribution) : ModelInput =
    contribution.RetrievedSources
    |> List.fold
        (fun input (s: RetrievedSource) ->
            ModelInput.addChunk
                {
                    ChunkId = s.DocumentId
                    Text = s.Snippet
                    GateResult = NotGated
                    Source = Some s.DocumentName
                }
                input)
        (ModelInput.ofSystemPrompt "SystemPromptBuilder" contribution.SystemPrompt [])

let aiAssistantApi
    (config: SystemPromptBuilder.AIAssistantServerConfig option)
    (moduleContexts: Map<string, ModuleAIContext>)
    (sseManager: SSEConnectionManager)
    (ctx: HttpContext)
    : AIAssistantApi * AIStreamingApi =
    let providerFactory =
        ctx.RequestServices.GetService(typeof<IAIProviderFactory>) :?> IAIProviderFactory

    let registry =
        ctx.RequestServices.GetService(typeof<AIToolRegistry>) :?> AIToolRegistry

    // Per-process registry of pending client-resident tool calls. The
    // agent loop suspends on a TCS keyed by toolCallId; the
    // /api/ai/tool-result POST handler completes it. Singleton — same
    // instance everywhere it's resolved.
    let dispatchRegistry =
        ctx.RequestServices.GetService(typeof<ClientToolDispatch.ClientToolDispatchRegistry>)
        :?> ClientToolDispatch.ClientToolDispatchRegistry

    // Per-task `CancellationTokenSource` registry for cancel-mid-stream.
    // The handler registers a CTS before kicking off the agent loop;
    // /api/ai/cancel/{taskId} cancels it; the handler's try/finally
    // unregisters on natural completion.
    let cancellationRegistry =
        ctx.RequestServices.GetService(typeof<AICancellationRegistry.AICancellationRegistry>)
        :?> AICancellationRegistry.AICancellationRegistry

    // The supervised agent-loop worker. `GetService`, not
    // `GetRequiredService`: a composition that never registered one is
    // a supported shape (a host that gates every `IHostedService` off,
    // a test standing this handler up without `compose`), and the
    // enqueue site falls back to a detached start rather than refusing
    // to serve chat at all.
    let chatWorker =
        match ctx.RequestServices.GetService(typeof<AIChatWorker.AIChatWorker>) with
        | :? AIChatWorker.AIChatWorker as w -> Some w
        | _ -> None

    let storage = ctx.RequestServices.GetService(typeof<IBlobStorage>) :?> IBlobStorage

    // Observational logger; silent no-op when DI has none (tests
    // bypassing `compose`). Must not log prompt content or API keys.
    let logger: ILogger =
        match ctx.RequestServices.GetService(typeof<ILogger>) with
        | :? ILogger as l -> l
        | _ ->
            { new ILogger with
                member _.Debug _ = ()
                member _.Info _ = ()
                member _.Warn _ = ()
                member _.Error(_, _) = ()
            }

    // `IAuditLog` for the BeaconRejected emission on the symmetric
    // ownership-gate path. `None` in test compositions that bypass
    // `compose` (no DI registration); the gate stays effective — only
    // the audit-trail row is skipped.
    let auditLogOpt =
        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as a -> Some a
        | _ -> None

    // Opt-in conversation persistence. Resolves `IConversationStore`
    // once per handler invocation; `null` (the default,
    // `ServerConfig.ConversationStore = NoConversationStore`) gates
    // every substrate-hook closure below to a no-op. The `ConvBridge`
    // closures stringify the existing `Guid`-typed `conversationId` at
    // the seam — see `ConversationTypes.fs` for the identity-by-value
    // rationale.
    let conversationStore =
        match ctx.RequestServices.GetService(typeof<IConversationStore>) with
        | :? IConversationStore as s -> Some s
        | _ -> None

    let convId (id: Guid) : ConversationId = id.ToString("N")

    // The titling policy `withConversationTitling` composed,
    // else titles derived from the first message and no titling call.
    let titlingPolicy =
        match ctx.RequestServices.GetService(typeof<ConversationTitlingPolicy>) with
        | :? ConversationTitlingPolicy as p -> p
        | _ -> ConversationTitlingPolicy.firstMessage

    // Where every task's latest status is recorded.
    let taskRegistry = AITaskStatusRegistry.Shared

    let sha256OfText (text: string) : string =
        let bytes = System.Text.Encoding.UTF8.GetBytes text
        let hash = System.Security.Cryptography.SHA256.HashData bytes
        Convert.ToHexString(hash).ToLowerInvariant()

    let digestOfContent (content: AIMessageContent) : string =
        ConversationStore.computeContentDigest content

    /// Begin the substrate's conversation record on first turn. No-op
    /// when the store is absent OR the conversation already exists
    /// (idempotent — the store's `BeginConversation` returns Ok in
    /// either case so we never double-emit `ConversationStarted`).
    let convBeginIfNeeded
        (conversationId: Guid)
        (userIdLocal: string)
        (scopeIdLocal: string)
        (providerName: string)
        (modelName: string)
        (systemPrompt: string option)
        : Async<unit> =
        async {
            match conversationStore with
            | None -> return ()
            | Some store ->
                let id = convId conversationId

                let writer = store :> IConversationWriter
                let reader = store :> IConversationReader

                // Read-then-create. The store's BeginConversation
                // is idempotent, but we avoid the BeginConversation
                // audit emit on a re-open by gating on existence
                // first.
                let! existing = reader.GetConversation(scopeIdLocal, id)

                match existing with
                | Ok _ -> return ()
                | Error _ ->
                    let conversation: ToolUp.Platform.Conversation = {
                        ConversationId = id
                        SchemaVersion = 1
                        CreatedAt = DateTime.UtcNow
                        CreatedBy = userIdLocal
                        ScopeId = scopeIdLocal
                        Provider = providerName
                        ModelName = modelName
                        SystemPromptDigest =
                            match systemPrompt with
                            | Some text -> sha256OfText text
                            | None -> ""
                        SdkVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()
                    }

                    let! result = writer.BeginConversation(scopeIdLocal, conversation)

                    match result with
                    | Ok() -> return ()
                    | Error err ->
                        logger.Warn
                            $"Conversation substrate BeginConversation failed: {ConversationError.toMessage err}"

                        return ()
        }

    let convAppendTurn
        (scopeIdLocal: string)
        (conversationId: Guid)
        (role: string)
        (content: AIMessageContent)
        (tokensIn: int option)
        (tokensOut: int option)
        : Async<unit> =
        async {
            match conversationStore with
            | None -> return ()
            | Some store ->
                let writer = store :> IConversationWriter
                let id = convId conversationId

                let turn: ConversationTurn = {
                    TurnId = "" // canonical id assigned by the store
                    ConversationId = id
                    SchemaVersion = 1
                    Role = role
                    Content = content
                    Timestamp = DateTime.UtcNow
                    TokensIn = tokensIn
                    TokensOut = tokensOut
                    ContentDigest = digestOfContent content
                }

                let! result = writer.AppendTurn(scopeIdLocal, id, turn)

                match result with
                | Ok() -> return ()
                | Error err ->
                    logger.Warn $"Conversation substrate AppendTurn failed: {ConversationError.toMessage err}"
                    return ()
        }

    let convMarkStatus (scopeIdLocal: string) (conversationId: Guid) (status: ConversationStatus) : Async<unit> = async {
        match conversationStore with
        | None -> return ()
        | Some store ->
            let writer = store :> IConversationWriter
            let id = convId conversationId
            let! result = writer.MarkStatus(scopeIdLocal, id, status)

            match result with
            | Ok() -> return ()
            | Error err ->
                logger.Warn $"Conversation substrate MarkStatus failed: {ConversationError.toMessage err}"
                return ()
    }

    /// Prior turns handed to `PromptContext.ConversationHistory`
    /// so the RAG query-rewrite stage can resolve a follow-up against what
    /// was said before. Bounded to the trailing window: an anaphor's referent
    /// is within a turn or two, and the rewriter's own prompt budget is
    /// small, so replaying a long conversation buys nothing and costs a
    /// larger read plus a larger rewrite prompt.
    let promptHistoryTurns = 6

    /// Read the trailing conversation turns for this request as plain text,
    /// oldest first.
    ///
    /// Gated on the `IConversationStore` being composed: with no store
    /// this returns `[]` without any I/O, so a deployment that never
    /// opted into conversation persistence pays nothing and its prompt
    /// context carries no history (GP 11 / GP 13).
    ///
    /// Best-effort by construction — a store error, a not-yet-existing
    /// conversation (the first turn of every conversation) and an empty
    /// history are all the same `[]`. History is an enhancement to
    /// retrieval; failing the user's turn because we could not read it
    /// would be a far worse outcome than retrieving on the raw query.
    ///
    /// The conversation id is the REQUEST's, so the history is
    /// read only when `canSeeStored` (the team's visibility rule,
    /// `ConversationVisibility.canSee`, over the stored conversation's
    /// owner and creation time) admits the caller; otherwise it is `[]`,
    /// exactly as a missing conversation is.
    let loadPromptHistory
        (canSeeStored: string -> DateTime -> Async<bool>)
        (scopeIdLocal: string)
        (conversationId: Guid)
        : Async<string list> =
        async {
            match conversationStore with
            | None -> return []
            | Some store ->
                try
                    let reader = store :> IConversationReader
                    let! result = reader.GetConversation(scopeIdLocal, convId conversationId)

                    match result with
                    | Error _ -> return []
                    | Ok(header, turns, _) ->
                        let! visible = canSeeStored header.CreatedBy header.CreatedAt

                        if not visible then
                            return []
                        else
                            let texts =
                                turns
                                |> List.map (fun t -> t.Content.Content)
                                |> List.filter (String.IsNullOrWhiteSpace >> not)

                            let drop = max 0 (List.length texts - promptHistoryTurns)
                            return texts |> List.skip drop
                with ex ->
                    logger.Warn $"Conversation history read for prompt context failed: {ex.Message}"
                    return []
        }

    // Scope and user are pre-resolved asynchronously by
    // `ScopeResolutionMiddleware`. If the middleware did not run or scope
    // resolution failed (Error case), fall back to an anonymous user-scoped
    // container.
    let scope =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as s) -> s
        | _ ->
            let fallback =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            {
                ScopeId = fallback
                Container = $"user-{fallback}"
                Persist = true
            }

    let userId =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback: construct from Items. Only used when the AccessContext
            // DI factory didn't run (tests bypassing compose).
            AccessContext.unrestricted (AnonymousSession userId)

    let promptBuilder = config |> Option.bind _.SystemPrompt

    // The team's conversation visibility policy, read per request
    // in the team's scope (`None` outside one: a personal container
    // is owner-only by construction). Every read path below filters through
    // `ConversationVisibility.canSee`, every write through `canModify`, and
    // the owner is always the stored conversation's, never the request's.
    let visibilityState () =
        TeamConversationPolicyStore.ConversationVisibility.resolveState ctx storage scope.Container

    let viewerIn (state: TeamConversationPolicyStore.TeamVisibilityState) =
        TeamConversationPolicyStore.ConversationVisibility.resolveViewer ctx userId state.TeamId

    let createdAtOf (messages: ConversationMessage list) =
        match messages with
        | [] -> DateTime.MinValue
        | _ -> messages |> List.map _.Timestamp |> List.min

    /// Whether the caller may see a conversation the opt-in substrate
    /// holds, by its stored owner and creation time: the rule every other
    /// read path applies (`ConversationVisibility.canSee`).
    let canSeeStoredConversation (owner: string) (createdAt: DateTime) : Async<bool> = async {
        match! visibilityState () with
        | Some state when not (TeamConversationPolicyStore.ConversationVisibility.isOpen state) ->
            let! viewer = viewerIn state
            return TeamConversationPolicyStore.ConversationVisibility.canSee state viewer owner createdAt
        | _ -> return true
    }

    /// The listing rows the caller may see. A team that never chose a level
    /// under a `TeamVisible` default is not filtered at all (no role
    /// lookup), so it lists exactly what it listed before.
    let visibleRows (rows: (ConversationListingRow * string) list) : Async<ConversationListingRow list> = async {
        match! visibilityState () with
        | Some state when not (TeamConversationPolicyStore.ConversationVisibility.isOpen state) ->
            let! viewer = viewerIn state

            return
                rows
                |> List.filter (fun (row, owner) ->
                    TeamConversationPolicyStore.ConversationVisibility.canSee
                        state
                        viewer
                        owner
                        row.Conversation.CreatedAt)
                |> List.map fst
        | _ -> return rows |> List.map fst
    }

    /// Whether the caller may write to (delete, or change) a conversation.
    /// `Ok true` proceed; `Ok false` the caller cannot see it, so the write
    /// behaves exactly as it does for an id that does not exist (a no-op),
    /// and its existence does not leak; `Error` refused, naming the rule.
    let authoriseWrite (conversationId: Guid) : Async<Result<bool, string>> = async {
        match! visibilityState () with
        | None -> return Ok true
        | Some state ->
            match! ConversationBlobs.loadConversation storage scope.Container conversationId with
            | Error err ->
                // A conversation whose blob cannot be read has no readable
                // owner, so nobody's authority over it can be established;
                // refuse rather than read it as the empty (ownerless,
                // writable-by-anyone) conversation.
                return Error(TurnFailure.message (unreadable logger conversationId err))
            | Ok messages ->
                let owner = ConversationListing.ownerOf messages

                match messages with
                | [] -> return Ok true
                | _ when owner = userId -> return Ok true
                | _ ->
                    let! viewer = viewerIn state
                    let createdAt = createdAtOf messages

                    if not (TeamConversationPolicyStore.ConversationVisibility.canSee state viewer owner createdAt) then
                        return Ok false
                    elif TeamConversationPolicyStore.ConversationVisibility.canModify state viewer owner createdAt then
                        return Ok true
                    else
                        let elevated =
                            match TeamConversationPolicyStore.ConversationVisibility.levels state createdAt with
                            | Some(_, PlatformAdmins) -> "a platform admin"
                            | _ -> "a team owner or admin"

                        return
                            Error
                                $"Only the conversation's author or {elevated} can delete or change another member's conversation."
    }

    // ONE turn implementation, the event sink injected at construction.
    // `makeAssistantApi emit` builds the api record whose turn emits every
    // `AIStreamEvent` through `emit`; the two mounted surfaces differ ONLY in
    // the sink they pass:
    //
    //   * the legacy `SubmitMessage` + `/api/ai/events` pair is
    //     `makeAssistantApi (sendEvent sseManager userId)` — the SSE-manager
    //     broadcast, byte-for-byte what it always wrote (pinned by
    //     `AIStreamFramingPinTests`);
    //   * the typed `StreamChatV2` builds the same record over the
    //     `AsyncStream.fromCallback` channel sink and drives the turn through
    //     it, so its `event: chunk` payloads are the same serialised events.
    //
    // Every status a turn reaches passes through the sink its api record is
    // built over, so wrapping BOTH sinks below keeps `GetTaskStatus` in step
    // with the stream without touching an emit site.
    let trackTaskStatus (sink: AIStreamEvent -> unit) (event: AIStreamEvent) =
        match event with
        | TaskStatusChanged(taskId, status) -> taskRegistry.Transition(taskId, status, DateTime.UtcNow)
        | _ -> ()

        sink event

    // ─── One turn: one request in, one outcome out ───────────────────

    let limits = SystemPromptBuilder.AIAssistantServerConfig.limits config

    /// The prompt builder's contribution for `request`. The builder sees the
    /// resolved access context plus the active module the client reported,
    /// so platform, team and module layers all contribute per request.
    let contributePrompt (request: AIMessageRequest) : Async<SystemPromptBuilder.PromptContribution> = async {
        // Prior turns for the query-rewrite stage, read only when a builder
        // will run; no I/O on the request path otherwise.
        let! history =
            match promptBuilder with
            | Some _ -> loadPromptHistory canSeeStoredConversation scope.ScopeId request.ConversationId
            | None -> async.Return []

        return!
            SystemPromptBuilder.PromptContribution.collect promptBuilder {
                Access = accessContext
                ActiveModule = request.ActiveModule
                ActivePage = request.ActivePage
                ActivePageNarrative = request.ActivePageNarrative
                ModuleContexts = moduleContexts
                CurrentMessage = Some request.Content
                ConversationHistory = history
                // The client's per-turn retrieval scope, forwarded verbatim:
                // a filter only narrows, AND-combined on top of the scopes the
                // builder derives from `accessContext`.
                RetrievalFilters = request.RetrievalFilters
                RetrievedSources = ref []
                ShortCircuit = ref None
                PlannedAnswerId = ref None
                Scope = ScopeResolution.forRequest ctx
            }
    }

    /// Record a refused append on the audit trail: one `BeaconRejected` row,
    /// `Surface = "submit"`, whichever surface fired the gate.
    let auditRefusal (conversationId: Guid) (ownerOfRecord: string) = async {
        logger.Warn
            $"AI SubmitMessage rejected: caller '{userId}' is not the owner of conversation {conversationId} (owner '{ownerOfRecord}', scope '{scope.ScopeId}')."

        match auditLogOpt with
        | Some auditLog ->
            let payload: BeaconRejectedPayload = {
                ConversationId = conversationId
                Caller = userId
                Owner = ownerOfRecord
                Surface = "submit"
            }

            try
                do! auditLog.Record(scope.ScopeId, BeaconRejected payload)
            with ex ->
                logger.Warn $"BeaconRejected audit write failed (submit; conversation {conversationId}): {ex.Message}"
        | None -> ()
    }

    /// The symmetric ownership gate, shared with the beacon handler. In a
    /// shared container a member could address another member's
    /// conversation; the gate refuses an append to one whose first message
    /// recorded a different owner, and admits an ownerless legacy one under
    /// a narrowed team level only for a caller `canModify` admits.
    ///
    /// This is the turn's ONLY read of the addressed conversation before the
    /// caller is admitted, and it runs before `InProgress`, so a refused turn
    /// never flashes as in flight.
    let admitTurn (turn: TurnRequest) : Async<Result<unit, TurnFailure>> = async {
        let conversationId = turn.Request.ConversationId

        let! existing =
            ConversationBlobs.loadConversation storage scope.Container conversationId
            |> withTimeout "Conversation load (ownership gate)" ChatPathIoTimeoutMs

        match existing with
        | Error err -> return Error(unreadable logger conversationId err)
        | Ok messages ->
            // The team state and the caller's role are read on the background
            // scope (the request's own is gone), and only for an ownerless
            // conversation.
            let! admission =
                ConversationBlobs.checkAppend
                    (fun () ->
                        TeamConversationPolicyStore.ConversationVisibility.resolveState
                            turn.Background
                            storage
                            scope.Container)
                    (fun state ->
                        ToolUp.Platform.TeamPolicyStore.TeamPolicyViewer.resolve
                            turn.Background.RequestServices
                            turn.GateAccess
                            userId
                            state.TeamId)
                    messages
                    userId

            match admission with
            | Ok() -> return Ok()
            | Error ownerOfRecord ->
                do! auditRefusal conversationId ownerOfRecord
                return Error(TurnFailure.Refused ownerOfRecord)
    }

    /// Resolve the provider once per message, so the user's configuration
    /// cannot shift mid-loop. Priority: the request's one-off override, then
    /// the conversation's stored override (the defensive fallback for a tab
    /// that has not loaded the picker, and the truth on reload), then the
    /// factory's active-label policy. Unreadable metadata fails the turn: its
    /// override may be the provider the user chose, and the turn must not
    /// quietly run on another.
    let resolveProvider (turn: TurnRequest) : Async<Result<IAIProvider, TurnFailure>> = async {
        let conversationId = turn.Request.ConversationId

        let! meta =
            ConversationBlobs.loadMeta storage scope.Container conversationId
            |> withTimeout "Conversation metadata load" ChatPathIoTimeoutMs

        match meta with
        | Error err -> return Error(unreadable logger conversationId err)
        | Ok meta ->
            let label =
                turn.Request.OverrideProviderLabel |> Option.orElse meta.OverrideProviderLabel

            let! resolved =
                match label with
                | Some label -> providerFactory.TryResolveByLabel(accessContext, label)
                | None -> providerFactory.Resolve accessContext
                |> withTimeout "AI provider resolution" ChatPathIoTimeoutMs

            match resolved with
            | Ok provider ->
                // No key material is ever included.
                logger.Debug
                    $"AI provider resolved for user {accessContext.UserId} (conversation={conversationId}, override={label}): {provider.Capabilities.ProviderName}/{provider.Capabilities.Model}"

                return Ok provider
            | Error err ->
                logger.Warn
                    $"AI provider resolution failed for user {accessContext.UserId} (conversation={conversationId}, override={label}): {ProviderResolutionError.toMessage err}"

                return Error(TurnFailure.ProviderUnresolved err)
    }

    /// The round-trippable provider history, every tool_use / tool_result
    /// pair intact. A conversation with no history sibling (written before
    /// it existed) is rebuilt from its UI blob, without tool context, for
    /// one turn.
    let loadHistory (conversationId: Guid) : Async<Result<AIProviderMessage list, TurnFailure>> = async {
        let! history =
            ConversationBlobs.loadProviderHistory storage scope.Container conversationId
            |> withTimeout "Conversation history load" ChatPathIoTimeoutMs

        match history with
        | Error err -> return Error(unreadable logger conversationId err)
        | Ok [] ->
            let! existing =
                ConversationBlobs.loadConversation storage scope.Container conversationId
                |> withTimeout "Conversation load" ChatPathIoTimeoutMs

            return
                existing
                |> Result.map (List.map toProviderMessage)
                |> Result.mapError (unreadable logger conversationId)
        | Ok history -> return Ok history
    }

    /// Bound the replayed history to the limits' window, keeping the most
    /// recent messages. Replaying everything grows per-turn cost without
    /// limit and ends in an opaque context-overflow error.
    let windowed (conversationId: Guid) (messages: AIProviderMessage list) =
        let window = SystemPromptBuilder.AgentLoopLimits.historyWindow limits
        let total = List.length messages

        if total > window then
            logger.Warn(
                sprintf
                    "AI conversation %O: provider history is %d messages; replaying only the most recent %d (dropping %d oldest). Tune via AIAssistantServerConfig.Limits.HistoryWindow."
                    conversationId
                    total
                    window
                    (total - window)
            )

            List.skip (total - window) messages
        else
            messages

    /// Post-stream citation normalisation, when `ICitationNormaliser` is
    /// composed (RAG with a non-Off policy) and the turn produced both
    /// retrieved sources and assistant text. One audit event per citation
    /// event, best-effort: a wedged event store never fails the turn.
    let normaliseCitations (turn: TurnRequest) (services: TurnServices) (provider: IAIProvider) (content: string) = async {
        let sources = turn.Contribution.RetrievedSources

        match services.CitationNormaliser, services.EventStore with
        | Some normaliser, store when not (List.isEmpty sources) && not (String.IsNullOrEmpty content) ->
            let providerName = provider.Capabilities.ProviderName
            let providerModel = provider.Capabilities.Model
            let result = normaliser.Normalise(sources, content, providerName, providerModel)

            match store with
            | Some store ->
                for evt in result.Events do
                    let sourceModule, eventType, sourceIndex =
                        match evt.Action with
                        | NormalisedToCanonical idx -> "_platform.ai.citation.normalised", "Normalised", Some idx
                        | StrippedPhantom -> "_platform.ai.citation.stripped", "Stripped", None
                        | UnverifiedTagged -> "_platform.ai.citation.stripped", "UnverifiedTagged", None

                    let payload = {|
                        TaskId = turn.TaskId
                        ConversationId = turn.Request.ConversationId
                        ProviderName = providerName
                        ProviderModel = providerModel
                        Variant = evt.Variant
                        Digit = evt.Digit
                        Action = eventType
                        SourceIndex = sourceIndex
                    |}

                    try
                        do!
                            store.Write {
                                Id = Guid.NewGuid()
                                OccurredAt = DateTime.UtcNow
                                ScopeId = scope.ScopeId
                                SourceModule = sourceModule
                                EventType = eventType
                                Payload = toJson payload
                            }
                    with ex ->
                        logger.Warn
                            $"AI citation-audit write failed (taskId={turn.TaskId}, action={eventType}): {ex.Message}. Record dropped; conversation unaffected."
            | None -> ()

            return result.Text
        | _ -> return content
    }

    /// The numeric-fidelity answer gate. Absent (the default) it returns the
    /// content verbatim with no verdict, event, audit or metric (GP 11 /
    /// GP 13); in `Annotate` / `Strict` it verifies every number against the
    /// turn's retrieved facts and returns the verdict the message carries.
    let verifyAnswer (turn: TurnRequest) (services: TurnServices) (provider: IAIProvider) (content: string) =
        AnswerVerifier.runVerificationStageWithJoin
            services.AnswerGate
            services.MetricRegistry
            turn.Contribution.RetrievedSources
            content
            services.MetricsSink
            services.EventStore
            {
                AuditLog = services.AuditLog
                Anchors = services.AnswerAnchors
            }
            scope.ScopeId
            turn.TaskId
            turn.Request.ConversationId
            provider.Capabilities.ProviderName
            provider.Capabilities.Model
            logger

    /// Finish an answered turn: the reply's final text, the exchange written
    /// through the record (the full history first, then the user prompt and
    /// final text appended to the UI blob), the assistant turn on the
    /// conversation substrate, and the conversation marked completed. `Some`
    /// provider when this was the conversation's first exchange, to title it.
    let completeTurn (turn: TurnRequest) (context: TurnContext) (finalMessages: AIProviderMessage list) = async {
        let provider = context.Provider
        let conversationId = turn.Request.ConversationId

        let lastAssistant =
            finalMessages |> List.tryFindBack (fun m -> m.Role = "assistant")

        let emitted = lastAssistant |> Option.map _.Content |> Option.defaultValue ""
        let! normalised = normaliseCitations turn context.Services provider emitted
        let! content, verification = verifyAnswer turn context.Services provider normalised

        verification
        |> Option.iter (fun v -> turn.Emit(AnswerVerified(conversationId, v)))

        let exchange = [
            toConversationMessage conversationId User turn.Request.Content [] [] [] userId None
            toConversationMessage
                conversationId
                AIAssistant
                content
                []
                turn.Contribution.RetrievedSources
                []
                ""
                verification
        ]

        match!
            ConversationBlobs.ConversationRecord.appendExchange
                storage
                scope.Container
                conversationId
                finalMessages
                exchange
        with
        | Error err -> return Error(unwritten logger conversationId err)
        | Ok priorUi ->
            do!
                convAppendTurn
                    scope.ScopeId
                    conversationId
                    "assistant"
                    {
                        Role = "assistant"
                        Content = content
                        ToolCalls = lastAssistant |> Option.map _.ToolCalls |> Option.defaultValue []
                        ToolResults = []
                        Parts = []
                    }
                    None
                    None

            do! convMarkStatus scope.ScopeId conversationId ConversationStatus.Completed
            return Ok(if List.isEmpty priorUi then Some provider else None)
    }

    /// Run an admitted turn on `provider`: the history, the user's turn on
    /// the conversation substrate, the plan, and the completion.
    let runAdmitted (turn: TurnRequest) (provider: IAIProvider) (cancel: CancellationToken) = async {
        let conversationId = turn.Request.ConversationId

        match! loadHistory conversationId with
        | Error failure -> return Error failure
        | Ok history ->
            let userMessage = AIProviderMessage.text "user" turn.Request.Content

            // Both no-ops without a composed `IConversationStore`.
            do!
                convBeginIfNeeded
                    conversationId
                    userId
                    scope.ScopeId
                    provider.Capabilities.ProviderName
                    provider.Capabilities.Model
                    turn.Contribution.SystemPrompt

            do!
                convAppendTurn
                    scope.ScopeId
                    conversationId
                    "user"
                    {
                        Role = "user"
                        Content = turn.Request.Content
                        ToolCalls = []
                        ToolResults = []
                        Parts = []
                    }
                    None
                    None

            let context =
                TurnContext.create
                    provider
                    registry
                    dispatchRegistry
                    turn.Background
                    turn.TaskId
                    conversationId
                    turn.Surface
                    turn.Request.ActiveModule
                    turn.Request.ActivePage
                    turn.Input
                    (AgentCallBudget.ofLimits limits)
                    limits
                    turn.Emit
                    cancel

            let plan = TurnPlan.ofContribution turn.Contribution

            match! AIAgentEngine.run context plan (windowed conversationId history @ [ userMessage ]) with
            | Error stop -> return Error(TurnFailure.Stopped stop)
            | Ok state -> return! completeTurn turn context state.Messages
    }

    /// The turn's stages, each refusing with a typed failure.
    let attemptTurn (turn: TurnRequest) (cancel: CancellationToken) = async {
        match! admitTurn turn with
        | Error failure -> return Error failure
        | Ok() ->
            turn.Emit(TaskStatusChanged(turn.TaskId, InProgress))

            match! resolveProvider turn with
            | Error failure -> return Error failure
            | Ok provider -> return! runAdmitted turn provider cancel
    }

    /// Run one turn to its outcome, and render the outcome once.
    ///
    /// The turn's cancellation joins the user's cancel (the per-task token
    /// `/api/ai/cancel` flips) and the worker's shutdown token, and reaches
    /// the provider call and the tool body. Which of the two fired decides
    /// the outcome: a user's cancel completes the task, a shutdown fails it
    /// with the reason, so the client is never left on an open stream.
    let runTurn (turn: TurnRequest) (shutdown: CancellationToken) = async {
        let conversationId = turn.Request.ConversationId

        // The turn runs on the chat worker, where the request's ambient
        // context does not flow: re-establish its viewer from the items
        // carried forward, so the disclosure gate decides for this user.
        use _viewer =
            let viewer = RequestViewer.ofItems turn.Background.Items
            RequestViewerContext.establish (fun () -> viewer)

        Logger.trace
            logger
            "ai.agent"
            $"turn start (userId={userId}, taskId={turn.TaskId}, conversation={conversationId})"

        let userCancel = cancellationRegistry.Register turn.TaskId
        use linked = CancellationTokenSource.CreateLinkedTokenSource(userCancel, shutdown)
        let! attempted = attemptTurn turn linked.Token |> Async.Catch

        let interrupted (otherwise: TurnFailure) =
            if userCancel.IsCancellationRequested then
                TurnOutcome.CancelledByUser
            elif shutdown.IsCancellationRequested then
                TurnOutcome.Failed TurnFailure.ShutDown
            else
                TurnOutcome.Failed otherwise

        let outcome =
            match attempted with
            | Choice1Of2(Ok _) -> TurnOutcome.Completed
            | Choice1Of2(Error(TurnFailure.Stopped LoopStop.Cancelled as failure)) -> interrupted failure
            | Choice1Of2(Error failure) -> TurnOutcome.Failed failure
            | Choice2Of2(:? OperationCanceledException as ex) -> interrupted (TurnFailure.Faulted ex)
            | Choice2Of2 ex -> TurnOutcome.Failed(TurnFailure.Faulted ex)

        match outcome with
        | TurnOutcome.Completed
        // Another member's conversation: nothing of it is touched.
        | TurnOutcome.Failed(TurnFailure.Refused _) -> ()
        | TurnOutcome.CancelledByUser
        | TurnOutcome.Failed TurnFailure.ShutDown ->
            convMarkStatus scope.ScopeId conversationId ConversationStatus.Cancelled
            |> Async.Start
        | TurnOutcome.Failed failure ->
            convMarkStatus scope.ScopeId conversationId (ConversationStatus.Errored(TurnFailure.message failure))
            |> Async.Start

        turn.Emit(TaskStatusChanged(turn.TaskId, TurnOutcome.toStatus outcome))
        cancellationRegistry.Unregister turn.TaskId

        // Title a new conversation after the terminal event, so the reply is
        // never kept waiting on it. A no-op unless a generating policy is
        // composed; never throws.
        match attempted with
        | Choice1Of2(Ok(Some provider)) ->
            do!
                ConversationTitling.titleConversation
                    logger
                    storage
                    scope.Container
                    conversationId
                    titlingPolicy
                    provider
                    turn.Request.Content
        | _ -> ()
    }

    /// Hand a turn to the supervised worker rather than `Async.Start`. The
    /// background scope is disposed exactly once, on the worker boundary,
    /// whichever way the turn ended.
    let enqueue (workItem: AIChatWorker.AIChatWorkItem) =
        match chatWorker with
        | Some worker ->
            match worker.TryEnqueue workItem with
            | AIChatWorker.Accepted -> ()
            | AIChatWorker.QueueFull ->
                // Admission control, surfaced, rather than a provider
                // timeout several seconds further from the cause.
                Logger.trace logger "ai.agent" $"chat worker queue full; refusing turn (taskId={workItem.TaskId})"
                workItem.Reject AIChatWorker.QueueFullReason
                workItem.Dispose()
            | AIChatWorker.NotRunning ->
                Logger.trace
                    logger
                    "ai.agent"
                    $"chat worker registered but not running; detached start (taskId={workItem.TaskId})"

                AIChatWorker.runDetached workItem
        | None -> AIChatWorker.runDetached workItem

    let makeAssistantApi (emit: AIStreamEvent -> unit) : AIAssistantApi = {
        SubmitMessage =
            fun (request: AIMessageRequest) -> async {
                let taskId = Guid.NewGuid()

                // The authoritative surface. Under `TrustClient` (default)
                // this is `request.Surface`; under `DeriveFromCookie` it comes
                // from the signed capability cookie on the ORIGINAL request
                // (the background context carries no cookies), so a client
                // lying about `Surface = FullPage` is demoted to SidePanel.
                let surfaceMode =
                    config |> Option.map _.AISurfaceDerivation |> Option.defaultValue TrustClient

                let surface, surfaceWarning =
                    AISurfaceCapability.resolveSurfaceFromRequest surfaceMode DateTimeOffset.UtcNow ctx request.Surface

                surfaceWarning |> Option.iter logger.Warn

                let task: AITask = {
                    TaskId = taskId
                    ConversationId = request.ConversationId
                    Prompt = request.Content
                    Status = Queued
                    CreatedAt = DateTime.UtcNow
                    CompletedAt = None
                }

                taskRegistry.Register(scope.Container, userId, task, task.CreatedAt)
                let! contribution = contributePrompt request

                // A background context with its own DI scope: the request's
                // HttpContext is disposed after this response.
                let background, backgroundScope = createBackgroundContext ctx userId

                // The "from" half of "cross-module". The active module
                // arrives on the request body, not among the items the
                // background context copies forward. Absent when the user was
                // on no module's page; the audit row then carries `None`.
                request.ActiveModule
                |> Option.iter (fun activeModule ->
                    background.Items[AICrossModuleAudit.ItemsKeys.ActiveModule] <- box activeModule)

                let turn: TurnRequest = {
                    TaskId = taskId
                    Request = request
                    Surface = surface
                    Contribution = contribution
                    Input = modelInputOf contribution
                    Background = background
                    // Captured now: the gate runs on the background scope,
                    // after the request's own services are gone.
                    GateAccess = TeamConversationPolicyStore.ConversationVisibility.accessOf ctx
                    Emit = emit
                }

                enqueue {
                    TaskId = taskId
                    Fingerprint = $"userId={userId}, conversation={request.ConversationId}"
                    Run = runTurn turn
                    Reject = fun reason -> emit (TaskStatusChanged(taskId, AITaskFailed reason))
                    Dispose = fun () -> backgroundScope.Dispose()
                }

                return task
            }

        // A conversation the caller may not see reads as empty, exactly as
        // a missing id does; opening another member's conversation under a
        // narrower level than `TeamVisible` is audited. A blob that is
        // present but cannot be read raises rather than reading as an empty
        // conversation.
        GetConversation =
            fun conversationId -> async {
                let! messages = async {
                    match! ConversationBlobs.loadConversation storage scope.Container conversationId with
                    | Ok messages -> return messages
                    | Error err -> return failwith (TurnFailure.message (unreadable logger conversationId err))
                }

                match messages with
                | [] -> return []
                | _ ->
                    match! visibilityState () with
                    | Some state when not (TeamConversationPolicyStore.ConversationVisibility.isOpen state) ->
                        let owner = ConversationListing.ownerOf messages
                        let createdAt = createdAtOf messages
                        let! viewer = viewerIn state

                        if
                            not (TeamConversationPolicyStore.ConversationVisibility.canSee state viewer owner createdAt)
                        then
                            return []
                        else
                            match TeamConversationPolicyStore.ConversationVisibility.levels state createdAt with
                            | Some(_, current) when
                                TeamConversationPolicyStore.ConversationVisibility.isElevatedRead
                                    state
                                    viewer
                                    owner
                                    createdAt
                                ->
                                do!
                                    TeamConversationPolicyStore.ConversationVisibilityAudit.elevatedRead
                                        auditLogOpt
                                        scope.ScopeId
                                        viewer.UserId
                                        owner
                                        conversationId
                                        current
                            | _ -> ()

                            return messages
                    | _ -> return messages
            }

        // Real titles, counts and timestamps, newest activity first, read
        // by the same `ConversationListing` the paged endpoint uses.
        ListConversations =
            fun () -> async {
                let! owned = ConversationListing.readAllOwned logger storage scope.Container titlingPolicy.MaxTitleChars
                let! rows = visibleRows owned
                return rows |> List.map _.Conversation
            }

        // The client-visible tool listing is filtered by the
        // caller's per-module `Read` permission, the same predicate the
        // agent loop's per-turn list and its dispatch re-check use — a
        // user must not be able to enumerate the tool surface of a module
        // they cannot read. Unrestricted deployments (empty
        // `ModulePermissions`) see the full registry as before (GP 11).
        //
        // Resolved through the items-backed reconstruction rather than the
        // `accessContext` above, whose fall-through is
        // `AccessContext.unrestricted` — a silent unrestricted fallback is
        // the one thing a permission filter must not have. Both read the
        // same middleware-populated request; the reconstruction keeps
        // `ModulePermissions` when the DI factory did not run.
        GetAvailableTools =
            fun () -> async {
                let toolAccess =
                    match ctx.RequestServices.GetService(typeof<AccessContext>) with
                    | :? AccessContext as ac -> ac
                    | _ -> AIToolRegistry.reconstructAccessContext ctx

                // Grant-gated for the same reason the listing
                // is permission-gated: enumerating the tool surface of a
                // module whose grant is pending or consent-revoked tells
                // the caller what they would have had, which is the fact
                // the grant policy is withholding.
                return
                    registry.ListAccessible(toolAccess, AIToolRegistry.moduleGrantGate ctx)
                    |> List.map _.Definition
            }

        // The polling fallback: the last status the task's turn emitted,
        // for the caller that submitted it. A task is readable by its
        // submitter only, at every level, which is never wider than `canSee`
        // allows (the author always sees their own), so another member's
        // task is `None` under every level without consulting the policy.
        GetTaskStatus =
            fun taskId -> async { return taskRegistry.TryGet(scope.Container, userId, taskId, DateTime.UtcNow) }

        DeleteConversation =
            fun conversationId -> async {
                // Delete EVERY sibling blob. Deleting only `.json` would
                // leave `.history.json` (the full provider round-trip incl.
                // tool inputs/outputs) and `.meta.json` behind — a
                // right-to-erasure / privacy gap. The sibling set lives in
                // ONE place (`ConversationRetention.siblingBlobNames`, which also
                // names the consent sidecar) and is shared with the
                // retention sweep, so an erasure and a purge remove
                // exactly the same blobs. The first failure is surfaced
                // so an incomplete erasure is retryable rather than
                // silently partial.
                //
                // Another member's conversation only by the level's
                // elevated role; one the caller cannot see is a no-op, as
                // a missing id is.
                match! authoriseWrite conversationId with
                | Ok true -> return! ConversationRetention.deleteSiblings storage scope.Container conversationId
                | Ok false -> return Ok()
                | Error refusal -> return Error refusal
            }

        SetConversationOverride =
            fun (conversationId, label) -> async {
                // The one other write a caller can aim at a conversation:
                // the same rule as `DeleteConversation`.
                match! authoriseWrite conversationId with
                | Error refusal -> return Error refusal
                | Ok false -> return Ok()
                | Ok true ->
                    // Read-modify-write: the meta sibling also carries the
                    // generated title, which an override change must not
                    // erase — and metadata that cannot be read is refused
                    // rather than written over.
                    match!
                        ConversationBlobs.ConversationRecord.updateMeta
                            storage
                            scope.Container
                            conversationId
                            (fun meta -> {
                                meta with
                                    OverrideProviderLabel = label
                            })
                    with
                    | Ok() -> return Ok()
                    | Error err ->
                        logger.Warn
                            $"AI conversation {conversationId}: {ConversationBlobs.ConversationBlobError.describe err}; the provider override was not changed."

                        return Error $"The provider override of conversation {conversationId} could not be changed."
            }

        // One page, filtered by title/content search.
        //
        // Filtered BEFORE the search and the page, so a hit
        // never surfaces a conversation the caller could not open, and the
        // count and cursor are over what the caller can see.
        ListConversationsPage =
            fun query -> async {
                let! owned = ConversationListing.readAllOwned logger storage scope.Container titlingPolicy.MaxTitleChars
                let! rows = visibleRows owned
                return ConversationListing.page query rows
            }
    }

    // The legacy surface: the turn over the per-user SSE broadcast.
    let assistantApi: AIAssistantApi =
        makeAssistantApi (trackTaskStatus (sendEvent sseManager userId))

    // The typed streaming surface: the SAME turn over the
    // stream's channel sink. `SubmitMessage`'s foreground returns once the
    // agent loop is kicked off (the detached-tail shape `fromCallback` is
    // built for); the loop emits the terminal `TaskStatusChanged` that ends
    // the stream, and a foreground fault completes the stream as
    // `event: error`. No SSE broadcast happens on this path.
    let streamingApi: AIStreamingApi = {
        StreamChatV2 =
            fun (request: AIMessageRequest) ->
                ToolUp.Remoting.Server.AsyncStream.fromCallback isTerminalStreamEvent (fun emitTyped ->
                    (makeAssistantApi (trackTaskStatus emitTyped)).SubmitMessage request
                    |> Async.Ignore)
    }

    assistantApi, streamingApi
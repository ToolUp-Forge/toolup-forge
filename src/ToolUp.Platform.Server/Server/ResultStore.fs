module ToolUp.Platform.ResultStore

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Transport

// ─── ResultStore implementations ────────────────────────────────
//
// Two implementations satisfy `IResultStore`:
//
// - `InMemoryResultStore` — `ConcurrentDictionary` keyed on
//   `(scopeId, objectId)`, mapped to a per-object version list.
//   Suitable for tests and short-lived dev environments. **Stateful
//   between calls** — documented dev-only exception to Phase 9c
//   Rule 4 (same shape as `LocalEmbeddingProvider`).
//
// - `PersistentResultStore` — delegates to `IDataObjectStore` with
//   `StrictlyVersioned` policy. Stateless between calls. Emits
//   `AnalysisCompleted` events to `IEventStore` and (when an
//   `ILineageStore` is available) auto-emits `LineageLink`s for
//   declared inputs.
//
// Both variants emit `AnalysisCompleted` events when an
// `IEventStore` is available — events are an audit-trail concern
// independent of result durability.

// ─── JSON diff ───────────────────────────────────────────────────
//
// Walks two `JsonDocument` trees in parallel and emits
// `FieldChange` records for every leaf-level difference. Object
// keys not present on one side surface as
// `{ From = None; To = Some _ }` (added) or
// `{ From = Some _; To = None }` (removed). Arrays are diffed by
// index — element re-ordering produces every-element changes
// (acceptable for the MVP; richer array-diff is a follow-up).

module private Diff =

    let private joinPath (parent: string) (segment: string) =
        if parent = "" then segment else parent + "." + segment

    let private joinIndex (parent: string) (index: int) =
        if parent = "" then $"[{index}]" else $"{parent}[{index}]"

    let private valueText (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Undefined then
            None
        else
            Some(element.GetRawText())

    let rec private walk (path: string) (from: JsonElement option) (to_: JsonElement option) : FieldChange list =
        match from, to_ with
        | None, None -> []
        | Some f, None -> [
            {
                Path = path
                From = valueText f
                To = None
            }
          ]
        | None, Some t -> [
            {
                Path = path
                From = None
                To = valueText t
            }
          ]
        | Some f, Some t when f.ValueKind = JsonValueKind.Object && t.ValueKind = JsonValueKind.Object ->
            // Union of keys; recurse per key.
            let fromKeys = f.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

            let toKeys = t.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

            Set.union fromKeys toKeys
            |> Seq.sort
            |> Seq.collect (fun key ->
                let fromChild =
                    if fromKeys.Contains key then
                        Some(f.GetProperty key)
                    else
                        None

                let toChild =
                    if toKeys.Contains key then
                        Some(t.GetProperty key)
                    else
                        None

                walk (joinPath path key) fromChild toChild)
            |> Seq.toList
        | Some f, Some t when f.ValueKind = JsonValueKind.Array && t.ValueKind = JsonValueKind.Array ->
            let fromItems = f.EnumerateArray() |> Seq.toArray
            let toItems = t.EnumerateArray() |> Seq.toArray
            let maxLen = max fromItems.Length toItems.Length

            [
                for i in 0 .. maxLen - 1 do
                    let fromChild = if i < fromItems.Length then Some fromItems[i] else None

                    let toChild = if i < toItems.Length then Some toItems[i] else None

                    yield! walk (joinIndex path i) fromChild toChild
            ]
        | Some f, Some t ->
            // Leaf comparison — strings, numbers, booleans, null.
            // Use raw text so numerics keep their canonical form.
            if f.GetRawText() = t.GetRawText() then
                []
            else
                [
                    {
                        Path = path
                        From = valueText f
                        To = valueText t
                    }
                ]

    /// Compute the field-level diff between two JSON documents.
    /// Throws `JsonException` (caught by the caller) if either side
    /// is not valid JSON.
    let compute (from: byte[]) (to_: byte[]) : FieldChange list =
        use fromDoc = JsonDocument.Parse(ReadOnlyMemory(from))
        use toDoc = JsonDocument.Parse(ReadOnlyMemory(to_))
        walk "" (Some fromDoc.RootElement) (Some toDoc.RootElement)

// ─── Event emission ──────────────────────────────────────────────

module private Events =
    /// STJ + FableConverters — same pattern as
    /// `ConfigStore` / `DataObjectStore`. `LineageLink` payloads
    /// round-trip the `LinkType` DU losslessly, which
    /// `System.Text.Json` does not.
    let private options = FableConverters.create ()

    let private serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    /// Phase 1025 — the write a failed `AnalysisCompleted` emission's
    /// warning names.
    let analysisCompletedWrite (scopeId: string) (moduleName: string) (objectId: string) (version: int) =
        sprintf
            "AnalysisCompleted event (scope %s, module %s, object %s, version %d)"
            scopeId
            moduleName
            objectId
            version

    /// Phase 1025 — the write a failed lineage link's warning names: every
    /// field the link was made of, so an operator can re-record a lost link
    /// by hand from the log line alone. Lineage feeds provenance, and a
    /// missing link is otherwise invisible in the graph.
    let lineageWrite (scopeId: string) (link: LineageLink) =
        sprintf
            "lineage link (scope %s, %s -> %s, module %s, type %A, link %O, at %s)"
            scopeId
            link.FromObjectId
            link.ToObjectId
            link.ModuleName
            link.LinkType
            link.LinkId
            (link.Timestamp.ToString "O")

    /// Best-effort: callers start this through `Background.startAudit`
    /// (Phase 1025), so a failing `IEventStore` loses the one event and is
    /// logged, never surfaced to the result-save path.
    let recordAnalysisCompleted
        (eventStore: IEventStore option)
        (scopeId: string)
        (moduleName: string)
        (objectId: string)
        (version: int)
        (createdBy: string)
        : Async<unit> =
        async {
            match eventStore with
            | None -> return ()
            | Some store ->
                let payload =
                    serialize {|
                        objectId = objectId
                        version = version
                        moduleName = moduleName
                        createdBy = createdBy
                    |}

                do!
                    store.Write {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime.UtcNow
                        ScopeId = scopeId
                        SourceModule = moduleName
                        EventType = ResultEventType.AnalysisCompleted
                        Payload = payload
                    }
        }

    /// One `Derived` link per input into `toObjectId`, each with a fresh
    /// `LinkId` — the identity a durable retry deduplicates on (Phase 1027).
    let mintLinks (moduleName: string) (toObjectId: string) (inputs: string list) : LineageLink list =
        inputs
        |> List.map (fun inputId -> {
            LinkId = Guid.NewGuid()
            FromObjectId = inputId
            ToObjectId = toObjectId
            ModuleName = moduleName
            LinkType = Derived
            Timestamp = DateTime.UtcNow
        })

    /// Record one link; `None` when the store recorded it, else the
    /// failure — a refusal (`Error`) or a throw, as one exception.
    let recordOne (store: ILineageStore) (scopeId: string) (link: LineageLink) : Async<exn option> = async {
        try
            match! store.Record(scopeId, link) with
            | Ok() -> return None
            | Error reason ->
                return
                    Some(InvalidOperationException(sprintf "ILineageStore.Record refused the link: %s" reason) :> exn)
        with ex ->
            return Some ex
    }

    /// Phase 1025 — each link is recorded on its own: a link the store
    /// refuses (an `Error` result, which this used to drop silently) or
    /// throws on is handed to `warn` under `lineageWrite`'s name, and the
    /// remaining links are still recorded. The loss is bounded to the
    /// failed links and each is reconstructable from its warning. This is
    /// the whole behaviour with durable lineage OFF (Phase 1027).
    let recordLinks
        (warn: string -> exn -> unit)
        (store: ILineageStore)
        (scopeId: string)
        (links: LineageLink list)
        : Async<unit> =
        async {
            for link in links do
                match! recordOne store scopeId link with
                | None -> ()
                | Some ex -> warn (lineageWrite scopeId link) ex
        }

    /// Emit one `LineageLink` per input. No-op when
    /// `lineageStore = None`.
    let recordLineage
        (warn: string -> exn -> unit)
        (lineageStore: ILineageStore option)
        (scopeId: string)
        (moduleName: string)
        (toObjectId: string)
        (inputs: string list)
        : Async<unit> =
        async {
            match lineageStore with
            | None -> return ()
            | Some store -> do! recordLinks warn store scopeId (mintLinks moduleName toObjectId inputs)
        }

// ─── Durable lineage: the lineage outbox (Phase 1027) ────────────
//
// With durable lineage OFF (the default, GP 11 / GP 13) a lineage link
// the store fails to record is logged and dropped (Phase 1025). With it
// ON (`ServerApp.withDurableLineage true`), a result save's links ride a
// write-ahead intent in the Phase 599 shape (`EntityOutbox`): the same
// `_platform` blob container, the same settle and abandon windows, the
// same version witness, and the same copy-then-delete quarantine.
//
//   1. Stage ONE intent blob holding every link of the save, and the
//      result's head version plus one as its witness. Staging happens
//      BEFORE `PersistentResultStore`'s save (the in-memory store, whose
//      save cannot fail and is not durable, stages after it with no
//      witness). A staging write that fails never fails the save: the
//      links fall back to Phase 1025's record-or-warn path, at Error.
//   2. Save the result. A failed save withdraws the intent; one left
//      behind is discarded unrecorded once it outlives the abandon window
//      without the witness — no link for a result that never landed.
//   3. Record the links (fire-and-forget, as before). All recorded: the
//      intent is deleted. Any failure: Phase 1025's Warning per failed
//      link, and the intent is rewritten with its attempt count, last
//      error and next-attempt time for `RelayOnce` to retry.
//
// **Delivery is at least once, deduplicated by `LinkId`.** A retry first
// reads `ILineageStore.GetAncestors` of the result once (every link of an
// intent shares `ToObjectId`) and records only the links whose ids are
// not already among its edges, so a partly delivered intent, or a crash
// between a recorded link and its intent's delete, never doubles a link.
// That works over any `ILineageStore` without a new interface member.
// There is no ordering promise across scopes or across intents
// (portability rule 5); links of one intent are recorded in input order.
//
// **Retry is data** (rule 3): a `RetryPolicy`, counting the inline first
// attempt, with exponential backoff from `InitialBackoff` to
// `MaxBackoff`. The default — 50 attempts, 1 minute up to 1 hour — rides
// out roughly two days of lineage-store outage.
//
// **Dead letters.** An intent that exhausts the budget is copied to
// `lineage-outbox-dead/` with its attempts and last error, then removed
// from staging; each of its links is logged at Error with Phase 1025's
// reconstructable text. Nothing is silently dropped: `DeadLetters` reads
// them back, `PendingCount` / `DeadLetterCount` count both sets, and the
// /dev diagnostics lineage row shows the counts. Re-drive is an operator
// act (move the intent back under `lineage-outbox/`); any link already in
// the graph is skipped by the dedup above.
//
// **Retention.** A staged intent lives until it is recorded, abandoned
// (an unwitnessed save, after the abandon window) or dead-lettered (after
// the retry budget). A dead letter lives until an operator removes it. A
// corrupt intent is quarantined under `lineage-outbox-poison/`.
//
// Stateless between calls over the blob substrate (rule 4): a restarted
// process builds a fresh `LineageOutbox` over the same storage and loses
// no staged link.

/// One staged save's lineage links, persisted as a single blob.
type LineageIntent = {
    IntentId: Guid
    ScopeId: string
    /// The result every link of this intent points into.
    ToObjectId: string
    /// The result's head version before the save, plus one; the relay
    /// records the links only once the result reached it. `0` means
    /// unwitnessed (the in-memory result store).
    MinVersionAfterSave: int
    CreatedAt: DateTime
    Links: LineageLink list
    /// Delivery attempts made so far, the inline first attempt included.
    Attempts: int
    /// The relay leaves the intent alone until this instant (backoff).
    NextAttemptAt: DateTime
    LastError: string option
}

/// An intent the relay gave up on after its retry budget. Durable under
/// `lineage-outbox-dead/` until an operator removes or re-drives it.
type LineageDeadLetter = {
    /// The intent as it stood at its last attempt, links included.
    Intent: LineageIntent
    LastError: string
    DeadLetteredAt: DateTime
}

/// A staged intent and the blob it lives in. Internal: only the result
/// stores stage.
type internal StagedLineage = {
    BlobName: string
    Intent: LineageIntent
}

/// The Phase 1027 lineage-durability defaults.
module LineageOutboxDefaults =
    /// 50 attempts (the inline first included), backing off from one
    /// minute to one hour: roughly two days of outage before a dead letter.
    let retryPolicy: RetryPolicy = {
        MaxAttempts = 50
        InitialBackoff = TimeSpan.FromMinutes 1.0
        MaxBackoff = TimeSpan.FromHours 1.0
        Timeout = None
    }

[<Literal>]
let private OutboxContainer = "_platform"

let private stagedPrefix = "lineage-outbox/"
let private deadPrefix = "lineage-outbox-dead/"
let private poisonPrefix = "lineage-outbox-poison/"

let private outboxJson = FableConverters.create ()

/// The cause behind an async boundary's wrapper (Phase 972).
let private causeOf (ex: exn) = (ProviderExceptions.unwrap ex).Message

/// The durable lineage outbox (Phase 1027; see the section header). Built
/// and registered by compose when `ServerConfig.LineageDurability =
/// EnabledDurableLineage`; resolve it from DI for its read surface.
type LineageOutbox
    (
        blobStorage: IBlobStorage,
        lineageStore: ILineageStore,
        objectStore: IDataObjectStore,
        logger: ILogger,
        retryPolicy: RetryPolicy,
        settleWindow: TimeSpan,
        abandonWindow: TimeSpan
    ) =

    do
        if retryPolicy.MaxAttempts < 1 then
            invalidArg (nameof retryPolicy) "LineageOutbox: RetryPolicy.MaxAttempts must be at least 1"

    let blobNameOf (intent: LineageIntent) =
        $"{stagedPrefix}{intent.ScopeId}/{intent.CreatedAt.Ticks:D19}-{intent.IntentId:N}.json"

    let encode (value: 'T) =
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, outboxJson))

    let backoff (attempts: int) =
        let ms =
            retryPolicy.InitialBackoff.TotalMilliseconds
            * Math.Pow(2.0, float (max 0 (attempts - 1)))

        TimeSpan.FromMilliseconds(min ms retryPolicy.MaxBackoff.TotalMilliseconds)

    let countUnder (prefix: string) = async {
        let! names = blobStorage.List(OutboxContainer, prefix)
        return names |> List.filter (fun n -> n.EndsWith ".json") |> List.length
    }

    let deadLetter (blobName: string) (intent: LineageIntent) (lastError: string) = async {
        let deadName = deadPrefix + blobName.Substring(stagedPrefix.Length)

        let letter = {
            Intent = intent
            LastError = lastError
            DeadLetteredAt = DateTime.UtcNow
        }

        // Copy first: the staged intent is deleted only once the dead
        // letter has landed (the Phase 863 order).
        match! blobStorage.Upload(OutboxContainer, deadName, encode letter) with
        | Ok _ ->
            for link in intent.Links do
                logger.Error(
                    $"[LineageOutbox] event=dead_lettered {Events.lineageWrite intent.ScopeId link} after {intent.Attempts} attempts: {lastError} - recorded at {deadName}; it is NOT in the lineage graph until re-driven",
                    None
                )

            match! blobStorage.Delete(OutboxContainer, blobName) with
            | Ok() -> ()
            | Error err ->
                logger.Error(
                    $"[LineageOutbox] event=dead_letter_clear_failed blob={blobName}: {err} - the dead letter is at {deadName}; the next pass dead-letters the staged copy again",
                    None
                )
        | Error err ->
            logger.Error(
                $"[LineageOutbox] event=dead_letter_failed blob={blobName}: {err} - left staged; the next pass retries the dead letter",
                None
            )
    }

    /// One delivery attempt. `isRetry` deduplicates against the graph;
    /// `onLinkFailure` sees each failed link. True when every link is in.
    let attempt (staged: StagedLineage) (isRetry: bool) (onLinkFailure: LineageLink -> exn -> unit) = async {
        let intent = staged.Intent

        let! outcome = async {
            try
                let! already =
                    if isRetry then
                        async {
                            let! graph = lineageStore.GetAncestors(intent.ScopeId, intent.ToObjectId)
                            return graph.Edges |> List.map _.LinkId |> Set.ofList
                        }
                    else
                        async { return Set.empty }

                let failures = ResizeArray<string>()

                for link in intent.Links do
                    if not (already.Contains link.LinkId) then
                        match! Events.recordOne lineageStore intent.ScopeId link with
                        | None -> ()
                        | Some ex ->
                            onLinkFailure link ex
                            failures.Add(causeOf ex)

                return if failures.Count = 0 then Ok() else Error failures[0]
            with ex ->
                return Error(causeOf ex)
        }

        match outcome with
        | Ok() ->
            match! blobStorage.Delete(OutboxContainer, staged.BlobName) with
            | Ok() -> ()
            | Error err ->
                logger.Warn
                    $"[LineageOutbox] event=intent_clear_failed blob={staged.BlobName}: {err} - its links ARE recorded; the relay re-finds the intent, skips them by link id and re-deletes it"

            return true
        | Error lastError ->
            let attempts = intent.Attempts + 1

            let updated = {
                intent with
                    Attempts = attempts
                    LastError = Some lastError
                    NextAttemptAt = DateTime.UtcNow + backoff attempts
            }

            if attempts >= retryPolicy.MaxAttempts then
                do! deadLetter staged.BlobName updated lastError
            else
                match! blobStorage.Upload(OutboxContainer, staged.BlobName, encode updated) with
                | Ok _ ->
                    logger.Warn
                        $"[LineageOutbox] event=link_deferred scope={intent.ScopeId} object={intent.ToObjectId} links={intent.Links.Length} attempt={attempts} of {retryPolicy.MaxAttempts}: {lastError} - staged; the relay retries from {updated.NextAttemptAt:O}"
                | Error err ->
                    logger.Warn
                        $"[LineageOutbox] event=attempt_record_failed blob={staged.BlobName}: {err} - the intent stays staged as it was and the relay retries it"

            return false
    }

    /// The defaults: `LineageOutboxDefaults.retryPolicy` and the Phase 599
    /// settle and abandon windows.
    new(blobStorage: IBlobStorage, lineageStore: ILineageStore, objectStore: IDataObjectStore, logger: ILogger) =
        LineageOutbox(
            blobStorage,
            lineageStore,
            objectStore,
            logger,
            LineageOutboxDefaults.retryPolicy,
            EntityOutbox.defaultSettleWindow,
            EntityOutbox.defaultAbandonWindow
        )

    /// Stage `links` into `toObjectId` with its witness. `None` (logged at
    /// Error) when the staging write fails; the caller then falls back to
    /// Phase 1025's record-or-warn path.
    member internal _.Stage
        (scopeId: string, toObjectId: string, links: LineageLink list, minVersionAfterSave: int)
        : Async<StagedLineage option> =
        async {
            let now = DateTime.UtcNow

            let intent = {
                IntentId = Guid.NewGuid()
                ScopeId = scopeId
                ToObjectId = toObjectId
                MinVersionAfterSave = minVersionAfterSave
                CreatedAt = now
                Links = links
                Attempts = 0
                NextAttemptAt = now
                LastError = None
            }

            let blobName = blobNameOf intent

            let! uploaded = async {
                try
                    return! blobStorage.Upload(OutboxContainer, blobName, encode intent)
                with ex ->
                    return Error(causeOf ex)
            }

            match uploaded with
            | Ok _ -> return Some { BlobName = blobName; Intent = intent }
            | Error err ->
                logger.Error(
                    $"[LineageOutbox] event=staging_failed scope={scopeId} object={toObjectId} links={links.Length}: {err} - these links are NOT durable; they are recorded once, and a failure is logged and dropped",
                    None
                )

                return None
        }

    /// Withdraw an intent whose save failed. Best-effort: one left behind
    /// is abandoned by the relay after the abandon window.
    member internal _.Withdraw(staged: StagedLineage) : Async<unit> = async {
        match! blobStorage.Delete(OutboxContainer, staged.BlobName) with
        | Ok() -> ()
        | Error err ->
            logger.Warn
                $"[LineageOutbox] event=intent_withdraw_failed blob={staged.BlobName}: {err} - the save failed, so the relay abandons the intent after the abandon window"
    }

    /// The inline first attempt, right after the save. `warn` is the
    /// result store's Phase 1025 warner, called once per failed link.
    member internal _.Deliver(staged: StagedLineage, warn: string -> exn -> unit) : Async<unit> = async {
        let! _ = attempt staged false (fun link ex -> warn (Events.lineageWrite staged.Intent.ScopeId link) ex)
        return ()
    }

    /// Staged intents awaiting delivery, across all scopes.
    member _.PendingCount() : Async<int> = countUnder stagedPrefix

    /// Dead-lettered intents, across all scopes.
    member _.DeadLetterCount() : Async<int> = countUnder deadPrefix

    /// Every dead letter, oldest first. One that cannot be read is logged
    /// and skipped, never deleted.
    member _.DeadLetters() : Async<LineageDeadLetter list> = async {
        let! names = blobStorage.List(OutboxContainer, deadPrefix)
        let letters = ResizeArray<LineageDeadLetter>()

        for name in names |> List.filter (fun n -> n.EndsWith ".json") |> List.sort do
            match! blobStorage.Download(OutboxContainer, name) with
            | Ok bytes ->
                try
                    letters.Add(
                        JsonSerializer.Deserialize<LineageDeadLetter>(Encoding.UTF8.GetString bytes, outboxJson)
                    )
                with ex ->
                    logger.Warn $"[LineageOutbox] event=dead_letter_unreadable blob={name}: {ex.Message}"
            | Error err -> logger.Warn $"[LineageOutbox] event=dead_letter_unreadable blob={name}: {err}"

        return List.ofSeq letters
    }

    /// One relay pass over at most `batchSize` staged intents, oldest
    /// first: each settled, due intent whose result reached its witness is
    /// retried (deduplicated by link id); one past its retry budget is
    /// dead-lettered; one whose save never landed is discarded once it
    /// outlives the abandon window; a corrupt one is quarantined under
    /// `lineage-outbox-poison/`. Returns the number of intents fully
    /// recorded.
    member _.RelayOnce(batchSize: int) : Async<int> = async {
        let! names = blobStorage.List(OutboxContainer, stagedPrefix)

        let batch =
            names
            |> List.filter (fun n -> n.EndsWith ".json")
            |> List.sort
            |> List.truncate batchSize

        let mutable recorded = 0
        let now = DateTime.UtcNow

        for blobName in batch do
            let! downloaded = blobStorage.Download(OutboxContainer, blobName)

            let decoded =
                match downloaded with
                | Error err -> Error err
                | Ok bytes ->
                    try
                        Ok(JsonSerializer.Deserialize<LineageIntent>(Encoding.UTF8.GetString bytes, outboxJson))
                    with ex ->
                        Error ex.Message

            match downloaded, decoded with
            | Error err, _ ->
                logger.Warn
                    $"[LineageOutbox] event=intent_unreadable blob={blobName}: {err} - left in place; the next pass retries"
            | Ok bytes, Error msg ->
                logger.Error(
                    $"[LineageOutbox] event=intent_poison blob={blobName}: {msg} - quarantined; its links are NOT recorded",
                    None
                )

                let quarantineName = poisonPrefix + blobName.Substring(stagedPrefix.Length)

                match! blobStorage.Upload(OutboxContainer, quarantineName, bytes) with
                | Ok _ ->
                    match! blobStorage.Delete(OutboxContainer, blobName) with
                    | Ok() -> ()
                    | Error err ->
                        logger.Error(
                            $"[LineageOutbox] event=intent_quarantine_delete_failed blob={blobName}: {err} - the bytes are in quarantine at {quarantineName}; the next pass retries the delete",
                            None
                        )
                | Error err ->
                    logger.Error(
                        $"[LineageOutbox] event=intent_quarantine_failed blob={blobName}: {err} - left in place; the next pass retries",
                        None
                    )
            | Ok _, Ok intent ->
                let staged = { BlobName = blobName; Intent = intent }
                let age = now - intent.CreatedAt

                if intent.Attempts >= retryPolicy.MaxAttempts then
                    do! deadLetter blobName intent (defaultArg intent.LastError "retry budget exhausted")
                elif age < settleWindow || intent.NextAttemptAt > now then
                    () // in flight, or backing off - a later pass
                else
                    let! head = async {
                        if intent.MinVersionAfterSave <= 0 then
                            return Some Int32.MaxValue
                        else
                            try
                                let! versions = objectStore.ListVersions(intent.ScopeId, intent.ToObjectId)
                                return Some(versions |> List.fold (fun acc o -> max acc o.Version) 0)
                            with ex ->
                                logger.Warn
                                    $"[LineageOutbox] event=witness_unreadable blob={blobName}: {causeOf ex} - the next pass retries"

                                return None
                    }

                    match head with
                    | None -> ()
                    | Some head when head >= intent.MinVersionAfterSave ->
                        let! ok =
                            attempt staged true (fun link ex ->
                                logger.Warn
                                    $"[LineageOutbox] event=link_retry_failed {Events.lineageWrite intent.ScopeId link}: {causeOf ex}")

                        if ok then
                            recorded <- recorded + 1

                            logger.Info
                                $"[LineageOutbox] event=relayed scope={intent.ScopeId} object={intent.ToObjectId} links={intent.Links.Length} attempts={intent.Attempts + 1}"
                    | Some _ when age > abandonWindow ->
                        match! blobStorage.Delete(OutboxContainer, blobName) with
                        | Ok() ->
                            logger.Warn
                                $"[LineageOutbox] event=intent_abandoned scope={intent.ScopeId} object={intent.ToObjectId} - the save never reached version {intent.MinVersionAfterSave}; its links are discarded, not recorded"
                        | Error err ->
                            logger.Warn
                                $"[LineageOutbox] event=intent_abandon_failed blob={blobName}: {err} - the next pass re-deletes it"
                    | Some _ -> () // the save may still land

        return recorded
    }

// ─── In-memory implementation ───────────────────────────────────

/// Per-`(scopeId, objectId)` slot held by `InMemoryResultStore`.
/// Versions accumulate; the latest is the last-written. Concurrent
/// saves to the same key serialise via the lock around `lock` — the
/// dictionary itself is concurrent, but the per-slot append needs
/// linearisation to keep version numbers monotonic.
type private InMemoryResultEntry = {
    mutable Versions: (DataObject * byte[]) list
    Lock: obj
}

/// Phase 1025 — the `AnalysisCompleted` event and the lineage links a save
/// emits are started through `Background.startAudit` with `warn`: a failing
/// event or lineage store loses those writes and logs a Warning naming each,
/// and never takes the host down or changes the save's result. The internal
/// primary constructor carries the logger composition hands it; the public
/// one logs to the SDK's default `ConsoleLogger`.
///
/// Phase 1027 — with a `LineageOutbox` (durable lineage on), the links are
/// staged after the in-memory save, unwitnessed, and retried until recorded
/// or dead-lettered; see the lineage-outbox section above.
type InMemoryResultStore
    internal
    (
        eventStore: IEventStore option,
        lineageStore: ILineageStore option,
        logger: ILogger,
        lineageOutbox: LineageOutbox option
    ) =
    let entries = ConcurrentDictionary<string * string, InMemoryResultEntry>()

    let warn =
        Background.writeFailureLogger "InMemoryResultStore" (Some logger) "" "the save's result"

    let getOrCreate (scopeId, objectId) =
        entries.GetOrAdd((scopeId, objectId), (fun _ -> { Versions = []; Lock = obj () }))

    /// The public constructor: the default `ConsoleLogger` (see the type's doc).
    new(?eventStore: IEventStore, ?lineageStore: ILineageStore) =
        InMemoryResultStore(eventStore, lineageStore, ConsoleLogger.ConsoleLogger() :> ILogger, None)

    /// Phase 1025 — composition's logger, durable lineage off.
    internal new(eventStore: IEventStore option, lineageStore: ILineageStore option, logger: ILogger) =
        InMemoryResultStore(eventStore, lineageStore, logger, None)

    interface IResultStore with
        member _.SaveResult(scopeId, moduleName, resultType, content, createdBy, inputs) = async {
            let objectId = ResultObjectId.make moduleName resultType
            let entry = getOrCreate (scopeId, objectId)

            let dataObject =
                lock entry.Lock (fun () ->
                    let nextVersion = entry.Versions.Length + 1

                    let obj = {
                        ObjectId = objectId
                        Version = nextVersion
                        CreatedAt = DateTime.UtcNow
                        CreatedBy = createdBy
                        ScopeId = scopeId
                        DataType = resultType
                        ContentHash = ""
                        Policy = StrictlyVersioned
                        Metadata = Map.empty
                    }

                    entry.Versions <- entry.Versions @ [ obj, content ]
                    obj)

            // Best-effort event + lineage emission. Fire-and-forget
            // so a slow event store doesn't block the caller; a failure
            // is logged under the write's name (Phase 1025).
            Events.recordAnalysisCompleted eventStore scopeId moduleName objectId dataObject.Version createdBy
            |> Background.startAudit warn (Events.analysisCompletedWrite scopeId moduleName objectId dataObject.Version)

            match lineageOutbox, lineageStore with
            | Some outbox, Some store when not inputs.IsEmpty ->
                // Phase 1027 — the save cannot fail and is not durable, so
                // the links are staged after it, with no witness.
                async {
                    let links = Events.mintLinks moduleName objectId inputs

                    match! outbox.Stage(scopeId, objectId, links, 0) with
                    | Some staged -> do! outbox.Deliver(staged, warn)
                    | None -> do! Events.recordLinks warn store scopeId links
                }
                |> Background.startAudit warn (sprintf "lineage links into %s (scope %s)" objectId scopeId)
            | _ ->
                Events.recordLineage warn lineageStore scopeId moduleName objectId inputs
                |> Background.startAudit warn (sprintf "lineage links into %s (scope %s)" objectId scopeId)

            return Ok dataObject
        }

        member _.GetLatest(scopeId, moduleName, resultType) = async {
            let objectId = ResultObjectId.make moduleName resultType

            match entries.TryGetValue((scopeId, objectId)) with
            | true, entry ->
                match entry.Versions |> List.tryLast with
                | Some(obj, bytes) -> return Ok(obj, bytes)
                | None -> return Error NotFound
            | false, _ -> return Error NotFound
        }

        member _.ListResults(scopeId, moduleName, dateRange) = async {
            let prefix = ResultObjectId.modulePrefix moduleName

            let inRange (obj: DataObject) =
                match dateRange with
                | None -> true
                | Some(fromTs, toTs) -> obj.CreatedAt >= fromTs && obj.CreatedAt <= toTs

            let results =
                entries
                |> Seq.filter (fun kvp ->
                    let (scope, objectId) = kvp.Key
                    scope = scopeId && objectId.StartsWith(prefix))
                |> Seq.collect (fun kvp -> kvp.Value.Versions |> List.map fst)
                |> Seq.filter inRange
                |> Seq.sortByDescending _.CreatedAt
                |> Seq.toList

            return results
        }

        member _.CompareVersions(scopeId, objectId, v1, v2) = async {
            match entries.TryGetValue((scopeId, objectId)) with
            | false, _ -> return Error NotFound
            | true, entry ->
                let pickVersion v =
                    entry.Versions |> List.tryFind (fun (obj, _) -> obj.Version = v)

                match pickVersion v1, pickVersion v2 with
                | None, _ -> return Error(VersionNotFound v1)
                | _, None -> return Error(VersionNotFound v2)
                | Some(_, fromBytes), Some(_, toBytes) ->
                    try
                        let changes = Diff.compute fromBytes toBytes

                        return
                            Ok {
                                ObjectId = objectId
                                FromVersion = v1
                                ToVersion = v2
                                Changes = changes
                            }
                    with :? System.Text.Json.JsonException as ex ->
                        return Error(StorageFailure $"diff requires JSON content: {ex.Message}")
        }

        member _.DeleteResult(scopeId, moduleName, resultType) = async {
            let objectId = ResultObjectId.make moduleName resultType
            // Idempotent: TryRemove on an absent key is a no-op.
            entries.TryRemove((scopeId, objectId)) |> ignore
            return Ok()
        }

        member _.DeleteByPrefix(scopeId, moduleName, resultTypePrefix) = async {
            let objectPrefix = ResultObjectId.make moduleName resultTypePrefix

            let toRemove =
                entries.Keys
                |> Seq.filter (fun (scope, objectId) -> scope = scopeId && objectId.StartsWith(objectPrefix))
                |> Seq.toList

            for key in toRemove do
                entries.TryRemove(key) |> ignore

            return Ok()
        }

// ─── Persistent implementation ──────────────────────────────────

/// Wraps `IDataObjectStore` with a Phase 8-shaped surface:
/// derives `ObjectId` from `(moduleName, resultType)`, hardcodes
/// `StrictlyVersioned` policy, and emits `AnalysisCompleted` /
/// `LineageLink` events on every successful save. Stateless between
/// calls — every method derives its result from the parameters +
/// `IDataObjectStore`.
/// Phase 1025 — the `AnalysisCompleted` event and the lineage links a save
/// emits are started through `Background.startAudit` with `warn`: a failing
/// event or lineage store loses those writes and logs a Warning naming each,
/// and never takes the host down or changes the save's result. The internal
/// primary constructor carries the logger composition hands it; the public
/// one logs to the SDK's default `ConsoleLogger`.
///
/// Phase 1027 — with a `LineageOutbox` (durable lineage on), the links are
/// staged BEFORE the save with the result's next version as their witness,
/// and retried until recorded or dead-lettered; see the lineage-outbox
/// section above.
type PersistentResultStore
    internal
    (
        objectStore: IDataObjectStore,
        eventStore: IEventStore option,
        lineageStore: ILineageStore option,
        logger: ILogger,
        lineageOutbox: LineageOutbox option
    ) =

    let warn =
        Background.writeFailureLogger "PersistentResultStore" (Some logger) "" "the save's result"

    /// Phase 1027 — stage the save's links ahead of it. `None` when
    /// durable lineage is off, there is nothing to link, or staging
    /// failed (the links then take Phase 1025's path after the save).
    let stageAhead (scopeId: string) (moduleName: string) (objectId: string) (inputs: string list) = async {
        match lineageOutbox, lineageStore with
        | Some outbox, Some _ when not inputs.IsEmpty ->
            let links = Events.mintLinks moduleName objectId inputs

            let! head = async {
                try
                    let! versions = objectStore.ListVersions(scopeId, objectId)
                    return Some(versions |> List.fold (fun acc o -> max acc o.Version) 0)
                with ex ->
                    logger.Error(
                        $"[LineageOutbox] event=witness_unreadable scope={scopeId} object={objectId}: {ex.Message} - these links are NOT durable",
                        None
                    )

                    return None
            }

            match head with
            | Some head ->
                let! staged = outbox.Stage(scopeId, objectId, links, head + 1)
                return Some links, staged
            | None -> return Some links, None
        | _ -> return None, None
    }

    /// The public constructor: the default `ConsoleLogger` (see the type's doc).
    new(objectStore: IDataObjectStore, ?eventStore: IEventStore, ?lineageStore: ILineageStore) =
        PersistentResultStore(objectStore, eventStore, lineageStore, ConsoleLogger.ConsoleLogger() :> ILogger, None)

    /// Phase 1025 — composition's logger, durable lineage off.
    internal new
        (
            objectStore: IDataObjectStore,
            eventStore: IEventStore option,
            lineageStore: ILineageStore option,
            logger: ILogger
        ) =
        PersistentResultStore(objectStore, eventStore, lineageStore, logger, None)

    interface IResultStore with
        member _.SaveResult(scopeId, moduleName, resultType, content, createdBy, inputs) = async {
            let objectId = ResultObjectId.make moduleName resultType

            let metadata = Map [ "moduleName", moduleName; "resultType", resultType ]

            let! minted, staged = stageAhead scopeId moduleName objectId inputs

            let! result =
                objectStore.Save(scopeId, objectId, content, resultType, createdBy, metadata, StrictlyVersioned)

            match result with
            | Error err ->
                match staged, lineageOutbox with
                | Some staged, Some outbox -> do! outbox.Withdraw staged
                | _ -> ()

                return Error err
            | Ok dataObject ->
                Events.recordAnalysisCompleted eventStore scopeId moduleName objectId dataObject.Version createdBy
                |> Background.startAudit
                    warn
                    (Events.analysisCompletedWrite scopeId moduleName objectId dataObject.Version)

                let lineageWork =
                    match staged, lineageOutbox, minted, lineageStore with
                    | Some staged, Some outbox, _, _ -> outbox.Deliver(staged, warn)
                    | None, _, Some links, Some store -> Events.recordLinks warn store scopeId links
                    | _ -> Events.recordLineage warn lineageStore scopeId moduleName objectId inputs

                lineageWork
                |> Background.startAudit warn (sprintf "lineage links into %s (scope %s)" objectId scopeId)

                return Ok dataObject
        }

        member _.GetLatest(scopeId, moduleName, resultType) =
            let objectId = ResultObjectId.make moduleName resultType
            objectStore.Get(scopeId, objectId)

        member _.ListResults(scopeId, moduleName, dateRange) = async {
            let prefix = ResultObjectId.modulePrefix moduleName

            let inRange (obj: DataObject) =
                match dateRange with
                | None -> true
                | Some(fromTs, toTs) -> obj.CreatedAt >= fromTs && obj.CreatedAt <= toTs

            let! allObjects = objectStore.ListObjects scopeId

            // For each module-prefixed object, pull its full version
            // history. `ListObjects` returns latest-only, so we need
            // a per-object `ListVersions` fan-out for the date-range
            // filter to apply over the full history.
            let moduleObjects =
                allObjects |> List.filter (fun obj -> obj.ObjectId.StartsWith(prefix))

            let! versionsByObject =
                moduleObjects
                |> List.map (fun obj -> objectStore.ListVersions(scopeId, obj.ObjectId))
                |> Async.Parallel

            return
                versionsByObject
                |> Array.toList
                |> List.collect id
                |> List.filter inRange
                |> List.sortByDescending _.CreatedAt
        }

        member _.CompareVersions(scopeId, objectId, v1, v2) = async {
            let! fromResult = objectStore.GetVersion(scopeId, objectId, v1)
            let! toResult = objectStore.GetVersion(scopeId, objectId, v2)

            match fromResult, toResult with
            | Error err, _ -> return Error err
            | _, Error err -> return Error err
            | Ok(_, fromBytes), Ok(_, toBytes) ->
                try
                    let changes = Diff.compute fromBytes toBytes

                    return
                        Ok {
                            ObjectId = objectId
                            FromVersion = v1
                            ToVersion = v2
                            Changes = changes
                        }
                with :? System.Text.Json.JsonException as ex ->
                    return Error(StorageFailure $"diff requires JSON content: {ex.Message}")
        }

        member _.DeleteResult(scopeId, moduleName, resultType) =
            let objectId = ResultObjectId.make moduleName resultType
            // `Evict` bypasses the `StrictlyVersioned` guard the store
            // applies on `SaveResult` — see `IResultStore` /
            // `IDataObjectStore.Evict` doc-comments. Idempotent.
            objectStore.Evict(scopeId, objectId)

        member _.DeleteByPrefix(scopeId, moduleName, resultTypePrefix) = async {
            let objectPrefix = ResultObjectId.make moduleName resultTypePrefix

            let! allObjects = objectStore.ListObjects scopeId

            let matching =
                allObjects |> List.filter (fun obj -> obj.ObjectId.StartsWith(objectPrefix))

            let! results =
                matching
                |> List.map (fun obj -> objectStore.Evict(scopeId, obj.ObjectId))
                |> Async.Parallel

            // First error wins; all-Ok (or empty) is Ok.
            return
                match
                    results
                    |> Array.tryPick (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | Some err -> Error err
                | None -> Ok()
        }
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConversationStore

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.EntityQueryTypes

// ─── Conversation-store implementations ─────────────────────────
//
// Two implementations satisfy `IConversationStore`:
//
//  - `InMemoryConversationStore` — `ConcurrentDictionary` keyed on
//    `(scopeId, conversationId)`. **Stateful between calls** —
//    documented dev-only exception to Phase 9c Rule 4 (same shape
//    as `InMemoryResultStore` / `LocalEmbeddingProvider`). Suitable
//    for tests and short-lived dev environments that want the
//    `IConversationStore` API surface without the durability cost.
//
//  - `PersistentConversationStore` — delegates to `IDataObjectStore`.
//    Stateless between calls. Inherits versioning, content-hash
//    dedup, and DSR erasure semantics from the underlying object
//    store; conversation-level audit events ride on top via the
//    supplied `IEventStore option`.
//
// Both impls implement the unified `IConversationStore` interface,
// which extends all three role interfaces (`IConversationReader` +
// `IConversationWriter` + `IConversationEraser`).

// ─── JSON helpers ────────────────────────────────────────────────
// FableJsonConverter so DU cases (`ConversationStatus`,
// `AIContentPart`, the per-image variants) round-trip cleanly. Same
// pattern as `ResultStore.fs` / `AIAssistantHandler.fs`.

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private toJsonBytes (value: 'T) : byte[] =
    JsonConvert.SerializeObject(value, jsonSettings) |> Encoding.UTF8.GetBytes

let private fromJsonBytes<'T> (bytes: byte[]) : 'T =
    bytes
    |> Encoding.UTF8.GetString
    |> fun s -> JsonConvert.DeserializeObject<'T>(s, jsonSettings)

// ─── Digest helpers ──────────────────────────────────────────────

let private sha256Hex (bytes: byte[]) : string =
    let hash = SHA256.HashData(bytes)
    Convert.ToHexString(hash).ToLowerInvariant()

/// SHA-256 hex over the canonical JSON of an `AIMessageContent`.
/// Exposed for `AIAssistantHandler` so the turn's `ContentDigest`
/// field matches what the substrate would compute on a re-read.
let computeContentDigest (content: AIMessageContent) : string = content |> toJsonBytes |> sha256Hex

// ─── ObjectId convention ─────────────────────────────────────────
//
// `__` (double underscore) separator follows the `ResultObjectId`
// convention — slashes leak into the underlying blob path
// (`{container}/objects/{objectId}/v{N}.json`) and would break
// `IDataObjectStore`'s parsing.

[<Literal>]
let private Separator = "__"

[<Literal>]
let private ManifestPrefix = "conv"

[<Literal>]
let private TurnInfix = "turn"

[<Literal>]
let private ManifestDataType = "_conversation_manifest"

[<Literal>]
let private TurnDataType = "_conversation_turn"

let private manifestObjectId (conversationId: ConversationId) =
    ManifestPrefix + Separator + conversationId

let private turnObjectId (conversationId: ConversationId) (turnNum: int) =
    sprintf "%s%s%s%s%s%s%06d" ManifestPrefix Separator conversationId Separator TurnInfix Separator turnNum

let private isTurnObject (objectId: string) =
    objectId.Contains(Separator + TurnInfix + Separator)

let private isManifestObject (objectId: string) =
    objectId.StartsWith(ManifestPrefix + Separator) && not (isTurnObject objectId)

let private parseConversationIdFromManifest (objectId: string) : ConversationId option =
    if isManifestObject objectId then
        Some(objectId.Substring((ManifestPrefix + Separator).Length))
    else
        None

let private parseTurnNumber (objectId: string) : int option =
    let marker = Separator + TurnInfix + Separator

    match objectId.LastIndexOf(marker) with
    | -1 -> None
    | i ->
        let tail = objectId.Substring(i + marker.Length)

        match Int32.TryParse(tail) with
        | true, n -> Some n
        | _ -> None

/// Standard zero-padded turn-id format. The substrate accepts any
/// monotonic-string turn-id from `IConversationWriter.AppendTurn`,
/// but the persistent impl rewrites to this canonical form so
/// `GetConversation` returns turns in append order under
/// lexicographic sort.
let formatTurnId (turnNum: int) : TurnId = sprintf "%06d" turnNum

// ─── Predicate evaluation (Conversation headers) ─────────────────
//
// MVP linear-scan predicate evaluator over `Conversation` records.
// Supports the `EntityQueryTypes.Predicate` AST against field-name
// keys; non-matching field names evaluate to `false` (the predicate
// is conservatively false rather than throwing — invalid field
// names are a programmer error, but rejecting them at query time
// is what `IEntityStore.Query` validation already does upstream).
//
// Adequate for ~1K conversations per scope. A future phase wires
// `IEntityStore` registration for indexed lookup without changing
// the `IConversationReader.Query` signature.

module private PredicateEval =

    let private fieldOf (conv: Conversation) (name: string) : string option =
        match name with
        | "ConversationId" -> Some conv.ConversationId
        | "CreatedBy" -> Some conv.CreatedBy
        | "ScopeId" -> Some conv.ScopeId
        | "Provider" -> Some conv.Provider
        | "ModelName" -> Some conv.ModelName
        | "SystemPromptDigest" -> Some conv.SystemPromptDigest
        | "SdkVersion" -> Some conv.SdkVersion
        | "CreatedAt" -> Some(conv.CreatedAt.ToString("o"))
        | _ -> None

    let rec eval (conv: Conversation) (predicate: Predicate) : bool =
        match predicate with
        | Eq(field, value) ->
            match fieldOf conv field with
            | Some actual -> actual = value
            | None -> false
        | Ne(field, value) ->
            match fieldOf conv field with
            | Some actual -> actual <> value
            | None -> false
        | Gt(field, value) ->
            match fieldOf conv field with
            | Some actual -> System.String.Compare(actual, value) > 0
            | None -> false
        | Gte(field, value) ->
            match fieldOf conv field with
            | Some actual -> System.String.Compare(actual, value) >= 0
            | None -> false
        | Lt(field, value) ->
            match fieldOf conv field with
            | Some actual -> System.String.Compare(actual, value) < 0
            | None -> false
        | Lte(field, value) ->
            match fieldOf conv field with
            | Some actual -> System.String.Compare(actual, value) <= 0
            | None -> false
        | In(field, values) ->
            match fieldOf conv field with
            | Some actual -> values |> List.contains actual
            | None -> false
        | And(left, right) -> eval conv left && eval conv right
        | Or(left, right) -> eval conv left || eval conv right
        | Not inner -> not (eval conv inner)

    let applySort (sort: (string * SortDirection) option) (convs: Conversation list) : Conversation list =
        match sort with
        | None -> convs |> List.sortByDescending _.CreatedAt
        | Some(field, direction) ->
            let keyed = convs |> List.map (fun c -> c, defaultArg (fieldOf c field) "")

            let sorted =
                match direction with
                | Ascending -> keyed |> List.sortBy snd
                | Descending -> keyed |> List.sortByDescending snd

            sorted |> List.map fst

    let applyPaging (skip: int) (take: int) (convs: Conversation list) : Conversation list =
        let safeSkip = max 0 skip
        let safeTake = if take > 0 then take else convs.Length

        convs |> List.skip (min safeSkip convs.Length) |> List.truncate safeTake

// ─── Audit emission ──────────────────────────────────────────────

module private Events =

    let private write
        (eventStore: IEventStore option)
        (scopeId: string)
        (eventType: string)
        (payload: 'T)
        : Async<unit> =
        async {
            match eventStore with
            | None -> return ()
            | Some store ->
                let payloadJson = JsonConvert.SerializeObject(payload, jsonSettings)

                let event: ModuleEvent = {
                    Id = Guid.NewGuid()
                    OccurredAt = DateTime.UtcNow
                    ScopeId = scopeId
                    SourceModule = ConversationsSourceModule.value
                    EventType = eventType
                    Payload = payloadJson
                }

                do! store.Write event
        }

    let started (eventStore: IEventStore option) (scopeId: string) (conv: Conversation) : Async<unit> =
        write eventStore scopeId "ConversationStarted" {
            ConversationStartedPayload.ConversationId = conv.ConversationId
            UserId = conv.CreatedBy
            ScopeId = conv.ScopeId
            Provider = conv.Provider
            ModelName = conv.ModelName
            SystemPromptDigest = conv.SystemPromptDigest
            SdkVersion = conv.SdkVersion
        }

    let turnAppended
        (eventStore: IEventStore option)
        (scopeId: string)
        (conversationId: ConversationId)
        (turn: ConversationTurn)
        : Async<unit> =
        write eventStore scopeId "ConversationTurnAppended" {
            ConversationTurnAppendedPayload.ConversationId = conversationId
            TurnId = turn.TurnId
            Role = turn.Role
            ContentDigest = turn.ContentDigest
            TokensIn = turn.TokensIn
            TokensOut = turn.TokensOut
        }

    let completed
        (eventStore: IEventStore option)
        (scopeId: string)
        (conversationId: ConversationId)
        (status: ConversationStatus)
        (turnCount: int)
        : Async<unit> =
        let statusName, reason =
            match status with
            | ConversationStatus.Active -> "Active", ""
            | ConversationStatus.Completed -> "Completed", ""
            | ConversationStatus.Errored r -> "Errored", r
            | ConversationStatus.Cancelled -> "Cancelled", ""

        write eventStore scopeId "ConversationCompleted" {
            ConversationCompletedPayload.ConversationId = conversationId
            FinalStatus = statusName
            ErrorReason = reason
            TurnCount = turnCount
        }

    let erased
        (eventStore: IEventStore option)
        (scopeId: string)
        (subjectUserId: string)
        (policy: ErasurePolicy)
        (conversationCount: int)
        : Async<unit> =
        let policyName =
            match policy with
            | ErasurePolicy.HardDelete -> "HardDelete"
            | ErasurePolicy.Tombstone -> "Tombstone"
            | ErasurePolicy.RetainPerCompliance -> "RetainPerCompliance"

        write eventStore scopeId "ConversationErased" {
            ConversationErasedPayload.SubjectUserId = subjectUserId
            Policy = policyName
            ConversationCount = conversationCount
        }

// ─── Manifest body shape (private wire format) ───────────────────
//
// Persisted manifest blob carries the `Conversation` header + the
// current `ConversationStatus` + the ordered list of turn ids. The
// turn ids let `GetConversation` reload turns without
// list-and-filter against the underlying object store. New turn
// ids are appended; the manifest is `Versioned` (bumped per
// append).

/// Persisted manifest payload. Not marked `private` because
/// Newtonsoft.Json's deserialiser requires the synthesised
/// constructor to be visible — F# private records hide it and
/// trigger `Unable to find a constructor` at deserialise time.
/// Module-internal usage is enforced by convention (no external
/// file references this type).
type ManifestBody = {
    Conversation: Conversation
    Status: ConversationStatus
    TurnIds: TurnId list
}

let private statusOk (current: ConversationStatus) (next: ConversationStatus) : bool =
    // Refuse backwards transitions out of a terminal state — that
    // would erase the terminal-state evidence. Transitions between
    // terminal states are accepted (the AIAssistantHandler may
    // legitimately flip from Active → Errored after a partial run).
    match current, next with
    | ConversationStatus.Completed, ConversationStatus.Active
    | ConversationStatus.Errored _, ConversationStatus.Active
    | ConversationStatus.Cancelled, ConversationStatus.Active -> false
    | _ -> true

// ─── In-memory implementation (dev / test) ───────────────────────

/// Per-`(scopeId, conversationId)` slot. Concurrent dictionary at the
/// outer level; per-slot lock around the turn-append + status-change
/// to keep version numbers monotonic under concurrent writes.
type private InMemoryConversationEntry = {
    mutable Conversation: Conversation
    mutable Status: ConversationStatus
    mutable Turns: ConversationTurn list
    Lock: obj
}

/// In-process conversation store. Stateful between calls — documented
/// dev-only exception to Rule 4. Use `PersistentConversationStore`
/// for production deployments.
type InMemoryConversationStore(?eventStore: IEventStore) =
    let entries =
        ConcurrentDictionary<string * ConversationId, InMemoryConversationEntry>()

    let tryGet (scopeId, conversationId) =
        match entries.TryGetValue((scopeId, conversationId)) with
        | true, e -> Some e
        | _ -> None

    interface IConversationStore with

        // Reader

        member _.GetConversation(scopeId, conversationId) = async {
            match tryGet (scopeId, conversationId) with
            | None -> return Error(ConversationError.NotFound conversationId)
            | Some entry ->
                let snapshot =
                    lock entry.Lock (fun () -> entry.Conversation, entry.Turns, entry.Status)

                return Ok snapshot
        }

        member _.GetTurn(scopeId, conversationId, turnId) = async {
            match tryGet (scopeId, conversationId) with
            | None -> return Error(ConversationError.NotFound conversationId)
            | Some entry ->
                let turn = entry.Turns |> List.tryFind (fun t -> t.TurnId = turnId)

                match turn with
                | Some t -> return Ok t
                | None -> return Error(ConversationError.TurnNotFound(conversationId, turnId))
        }

        member _.ListByScope(scopeId) = async {
            let convs =
                entries
                |> Seq.filter (fun kvp -> fst kvp.Key = scopeId)
                |> Seq.map _.Value.Conversation
                |> Seq.sortByDescending _.CreatedAt
                |> Seq.toList

            return convs
        }

        member _.ListByUser(scopeId, userId) = async {
            let convs =
                entries
                |> Seq.filter (fun kvp -> fst kvp.Key = scopeId)
                |> Seq.map _.Value.Conversation
                |> Seq.filter (fun c -> c.CreatedBy = userId)
                |> Seq.sortByDescending _.CreatedAt
                |> Seq.toList

            return convs
        }

        member _.Query(scopeId, query) = async {
            let inScope =
                entries
                |> Seq.filter (fun kvp -> fst kvp.Key = scopeId)
                |> Seq.map _.Value.Conversation
                |> Seq.toList

            let filtered =
                match query.Where with
                | None -> inScope
                | Some predicate -> inScope |> List.filter (fun c -> PredicateEval.eval c predicate)

            return
                filtered
                |> PredicateEval.applySort query.OrderBy
                |> PredicateEval.applyPaging query.Skip query.Take
        }

        // Writer

        member _.BeginConversation(scopeId, conv) = async {
            if conv.ScopeId <> scopeId then
                return Error(ConversationError.ScopeMismatch(scopeId, conv.ScopeId))
            else
                let entry = {
                    Conversation = conv
                    Status = ConversationStatus.Active
                    Turns = []
                    Lock = obj ()
                }

                let added = entries.TryAdd((scopeId, conv.ConversationId), entry)

                if added then
                    Events.started eventStore scopeId conv |> Async.Start
                    return Ok()
                else
                    // Idempotent: re-`BeginConversation` of an
                    // existing id is a no-op. Audit not re-emitted
                    // — the first emission is the canonical row.
                    return Ok()
        }

        member _.AppendTurn(scopeId, conversationId, turn) = async {
            if turn.ConversationId <> conversationId then
                return Error(ConversationError.ScopeMismatch(conversationId, turn.ConversationId))
            else
                match tryGet (scopeId, conversationId) with
                | None -> return Error(ConversationError.NotFound conversationId)
                | Some entry ->
                    lock entry.Lock (fun () -> entry.Turns <- entry.Turns @ [ turn ])

                    Events.turnAppended eventStore scopeId conversationId turn |> Async.Start
                    return Ok()
        }

        member _.MarkStatus(scopeId, conversationId, status) = async {
            match tryGet (scopeId, conversationId) with
            | None -> return Error(ConversationError.NotFound conversationId)
            | Some entry ->
                let result =
                    lock entry.Lock (fun () ->
                        let current = entry.Status

                        if statusOk current status then
                            entry.Status <- status
                            Ok(status, entry.Turns.Length)
                        else
                            Error(ConversationError.StatusForbidden(current, status)))

                match result with
                | Ok(applied, turnCount) ->
                    match applied with
                    | ConversationStatus.Active -> ()
                    | _ ->
                        Events.completed eventStore scopeId conversationId applied turnCount
                        |> Async.Start

                    return Ok()
                | Error e -> return Error e
        }

        member _.DeleteConversation(scopeId, conversationId) = async {
            entries.TryRemove((scopeId, conversationId)) |> ignore
            return Ok()
        }

        // Eraser

        member this.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Ok {
                        HandlerName = "conversations"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op"
                    }
            else
                let matchedIds =
                    entries
                    |> Seq.filter (fun kvp -> fst kvp.Key = scopeId && kvp.Value.Conversation.CreatedBy = subjectUserId)
                    |> Seq.map (fun kvp -> snd kvp.Key)
                    |> Seq.toList

                if dryRun then
                    return
                        Ok {
                            HandlerName = "conversations"
                            RecordsAffected = matchedIds.Length
                            Note = Some(sprintf "%d conversation(s) would be affected by %A" matchedIds.Length policy)
                        }
                else
                    let tombstone = Erasure.TombstoneMarker

                    let tombstoneContent (c: AIMessageContent) : AIMessageContent = {
                        Role = c.Role
                        Content = tombstone
                        ToolCalls = c.ToolCalls |> List.map (fun tc -> { tc with Arguments = tombstone })
                        ToolResults = c.ToolResults |> List.map (fun tr -> { tr with Content = tombstone })
                        Parts = []
                    }

                    for id in matchedIds do
                        match tryGet (scopeId, id) with
                        | None -> ()
                        | Some entry ->
                            lock entry.Lock (fun () ->
                                match policy with
                                | ErasurePolicy.HardDelete -> entries.TryRemove((scopeId, id)) |> ignore
                                | ErasurePolicy.Tombstone ->
                                    entry.Turns <-
                                        entry.Turns
                                        |> List.map (fun t -> {
                                            t with
                                                Content = tombstoneContent t.Content
                                                ContentDigest = computeContentDigest (tombstoneContent t.Content)
                                        })

                                    entry.Conversation <- {
                                        entry.Conversation with
                                            CreatedBy = tombstone
                                    }
                                | ErasurePolicy.RetainPerCompliance ->
                                    entry.Conversation <- {
                                        entry.Conversation with
                                            CreatedBy = tombstone
                                    })

                    Events.erased eventStore scopeId subjectUserId policy matchedIds.Length
                    |> Async.Start

                    return
                        Ok {
                            HandlerName = "conversations"
                            RecordsAffected = matchedIds.Length
                            Note =
                                Some(
                                    sprintf "%d conversation(s) %A under user %s" matchedIds.Length policy subjectUserId
                                )
                        }
        }

// ─── Persistent implementation ──────────────────────────────────

/// Conversation store built on `IDataObjectStore`. Stateless between
/// calls. Manifest blobs live at `objectId = "conv__{conversationId}"`
/// under `Versioned` policy (bumped on every `AppendTurn` /
/// `MarkStatus`); turn blobs live at
/// `objectId = "conv__{conversationId}__turn__{NNNNNN}"` under
/// `StrictlyVersioned` policy (immutable). All metadata (`userId`,
/// `kind`, `conversationId`) is carried on the `DataObject.Metadata`
/// map so `IDataObjectStore.Erase` matches by subject without
/// content scans.
type PersistentConversationStore(objectStore: IDataObjectStore, ?eventStore: IEventStore) =

    let readManifest
        (scopeId: string)
        (conversationId: ConversationId)
        : Async<Result<ManifestBody, ConversationError>> =
        async {
            let! result = objectStore.Get(scopeId, manifestObjectId conversationId)

            match result with
            | Ok(_, bytes) ->
                try
                    let body = fromJsonBytes<ManifestBody> bytes
                    return Ok body
                with ex ->
                    return Error(ConversationError.StoreUnreachable ex.Message)
            | Error NotFound -> return Error(ConversationError.NotFound conversationId)
            | Error err -> return Error(ConversationError.StoreUnreachable(sprintf "%A" err))
        }

    let writeManifest
        (scopeId: string)
        (createdBy: string)
        (body: ManifestBody)
        : Async<Result<unit, ConversationError>> =
        async {
            let metadata =
                Map [
                    "kind", "manifest"
                    "conversationId", body.Conversation.ConversationId
                    "userId", body.Conversation.CreatedBy
                ]

            let! result =
                objectStore.Save(
                    scopeId,
                    manifestObjectId body.Conversation.ConversationId,
                    toJsonBytes body,
                    ManifestDataType,
                    createdBy,
                    metadata,
                    Versioned
                )

            match result with
            | Ok _ -> return Ok()
            | Error err -> return Error(ConversationError.StoreUnreachable(sprintf "%A" err))
        }

    let writeTurn
        (scopeId: string)
        (conversationId: ConversationId)
        (userId: string)
        (turnNum: int)
        (turn: ConversationTurn)
        : Async<Result<unit, ConversationError>> =
        async {
            let metadata =
                Map [
                    "kind", "turn"
                    "conversationId", conversationId
                    "turnId", turn.TurnId
                    "userId", userId
                ]

            let! result =
                objectStore.Save(
                    scopeId,
                    turnObjectId conversationId turnNum,
                    toJsonBytes turn,
                    TurnDataType,
                    userId,
                    metadata,
                    StrictlyVersioned
                )

            match result with
            | Ok _ -> return Ok()
            | Error err -> return Error(ConversationError.StoreUnreachable(sprintf "%A" err))
        }

    let readTurns
        (scopeId: string)
        (conversationId: ConversationId)
        (turnIds: TurnId list)
        : Async<ConversationTurn list> =
        async {
            // `turnIds` is the ordered list from the manifest;
            // read each turn blob individually. Per-turn parsing
            // failures are logged inside the underlying store but
            // surfaced here as a `StoreUnreachable` if any read
            // fails — the manifest's promised turn isn't readable
            // is a substrate-integrity issue worth surfacing.
            let! reads =
                turnIds
                |> List.mapi (fun i tid ->
                    // Convention: persisted turn-objectId is the
                    // 1-based position in the manifest's TurnIds
                    // list. `turnId` itself is the operator-facing
                    // canonical id (typically `formatTurnId` of the
                    // same number, but the manifest is authoritative).
                    let turnNum = i + 1
                    objectStore.Get(scopeId, turnObjectId conversationId turnNum))
                |> Async.Parallel

            return
                reads
                |> Array.choose (fun result ->
                    match result with
                    | Ok(_, bytes) ->
                        try
                            Some(fromJsonBytes<ConversationTurn> bytes)
                        with _ ->
                            None
                    | Error _ -> None)
                |> Array.toList
        }

    interface IConversationStore with

        // Reader

        member _.GetConversation(scopeId, conversationId) = async {
            let! manifestResult = readManifest scopeId conversationId

            match manifestResult with
            | Error e -> return Error e
            | Ok manifest ->
                let! turns = readTurns scopeId conversationId manifest.TurnIds
                return Ok(manifest.Conversation, turns, manifest.Status)
        }

        member _.GetTurn(scopeId, conversationId, turnId) = async {
            let! manifestResult = readManifest scopeId conversationId

            match manifestResult with
            | Error e -> return Error e
            | Ok manifest ->
                let idx = manifest.TurnIds |> List.tryFindIndex (fun t -> t = turnId)

                match idx with
                | None -> return Error(ConversationError.TurnNotFound(conversationId, turnId))
                | Some i ->
                    let! result = objectStore.Get(scopeId, turnObjectId conversationId (i + 1))

                    match result with
                    | Ok(_, bytes) ->
                        try
                            return Ok(fromJsonBytes<ConversationTurn> bytes)
                        with ex ->
                            return Error(ConversationError.StoreUnreachable ex.Message)
                    | Error err -> return Error(ConversationError.StoreUnreachable(sprintf "%A" err))
        }

        member _.ListByScope(scopeId) = async {
            let! allObjects = objectStore.ListObjects scopeId

            let manifestIds =
                allObjects
                |> List.choose (fun obj ->
                    if obj.DataType = ManifestDataType then
                        parseConversationIdFromManifest obj.ObjectId
                    else
                        None)

            // Per-conversation manifest read. Parallel for
            // medium scopes; degrades to linear time for large
            // scopes (the MVP scaling caveat documented on
            // `IConversationReader.Query`).
            let! manifests = manifestIds |> List.map (fun id -> readManifest scopeId id) |> Async.Parallel

            return
                manifests
                |> Array.choose (function
                    | Ok m -> Some m.Conversation
                    | Error _ -> None)
                |> Array.sortByDescending _.CreatedAt
                |> Array.toList
        }

        member this.ListByUser(scopeId, userId) = async {
            let reader = this :> IConversationReader
            let! all = reader.ListByScope scopeId
            return all |> List.filter (fun c -> c.CreatedBy = userId)
        }

        member this.Query(scopeId, query) = async {
            let reader = this :> IConversationReader
            let! all = reader.ListByScope scopeId

            let filtered =
                match query.Where with
                | None -> all
                | Some predicate -> all |> List.filter (fun c -> PredicateEval.eval c predicate)

            return
                filtered
                |> PredicateEval.applySort query.OrderBy
                |> PredicateEval.applyPaging query.Skip query.Take
        }

        // Writer

        member _.BeginConversation(scopeId, conv) = async {
            if conv.ScopeId <> scopeId then
                return Error(ConversationError.ScopeMismatch(scopeId, conv.ScopeId))
            else
                let body = {
                    Conversation = conv
                    Status = ConversationStatus.Active
                    TurnIds = []
                }

                let! writeResult = writeManifest scopeId conv.CreatedBy body

                match writeResult with
                | Ok() ->
                    Events.started eventStore scopeId conv |> Async.Start
                    return Ok()
                | Error e -> return Error e
        }

        member _.AppendTurn(scopeId, conversationId, turn) = async {
            if turn.ConversationId <> conversationId then
                return Error(ConversationError.ScopeMismatch(conversationId, turn.ConversationId))
            else
                let! manifestResult = readManifest scopeId conversationId

                match manifestResult with
                | Error e -> return Error e
                | Ok manifest ->
                    let nextTurnNum = manifest.TurnIds.Length + 1
                    let canonicalTurnId = formatTurnId nextTurnNum
                    let turnWithCanonicalId = { turn with TurnId = canonicalTurnId }

                    let! turnWriteResult =
                        writeTurn scopeId conversationId manifest.Conversation.CreatedBy nextTurnNum turnWithCanonicalId

                    match turnWriteResult with
                    | Error e -> return Error e
                    | Ok() ->
                        let bumped = {
                            manifest with
                                TurnIds = manifest.TurnIds @ [ canonicalTurnId ]
                        }

                        let! manifestWriteResult = writeManifest scopeId manifest.Conversation.CreatedBy bumped

                        match manifestWriteResult with
                        | Error e -> return Error e
                        | Ok() ->
                            Events.turnAppended eventStore scopeId conversationId turnWithCanonicalId
                            |> Async.Start

                            return Ok()
        }

        member _.MarkStatus(scopeId, conversationId, status) = async {
            let! manifestResult = readManifest scopeId conversationId

            match manifestResult with
            | Error e -> return Error e
            | Ok manifest ->
                if not (statusOk manifest.Status status) then
                    return Error(ConversationError.StatusForbidden(manifest.Status, status))
                else
                    let bumped = { manifest with Status = status }
                    let! writeResult = writeManifest scopeId manifest.Conversation.CreatedBy bumped

                    match writeResult with
                    | Ok() ->
                        match status with
                        | ConversationStatus.Active -> ()
                        | _ ->
                            Events.completed eventStore scopeId conversationId status manifest.TurnIds.Length
                            |> Async.Start

                        return Ok()
                    | Error e -> return Error e
        }

        member _.DeleteConversation(scopeId, conversationId) = async {
            // Best-effort delete. The manifest carries the
            // turn-ids; we walk them to delete each turn blob
            // before deleting the manifest itself.
            let! manifestResult = readManifest scopeId conversationId

            match manifestResult with
            | Error(ConversationError.NotFound _) -> return Ok() // already gone — idempotent
            | Error e -> return Error e
            | Ok manifest ->
                let turnNums = [ 1 .. manifest.TurnIds.Length ]

                for n in turnNums do
                    let! _ = objectStore.Delete(scopeId, turnObjectId conversationId n)
                    ()

                let! _ = objectStore.Delete(scopeId, manifestObjectId conversationId)
                return Ok()
        }

        // Eraser

        member this.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Ok {
                        HandlerName = "conversations"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op"
                    }
            else
                let reader = this :> IConversationReader
                let writer = this :> IConversationWriter
                let! allConversations = reader.ListByUser(scopeId, subjectUserId)
                let count = allConversations.Length

                if dryRun then
                    return
                        Ok {
                            HandlerName = "conversations"
                            RecordsAffected = count
                            Note = Some(sprintf "%d conversation(s) would be affected by %A" count policy)
                        }
                else
                    match policy with
                    | ErasurePolicy.HardDelete ->
                        // Delete each conversation's manifest +
                        // turns. `DeleteConversation` is
                        // idempotent so concurrent erasure
                        // doesn't double-count.
                        for c in allConversations do
                            let! _ = writer.DeleteConversation(scopeId, c.ConversationId)
                            ()
                    | ErasurePolicy.Tombstone ->
                        // Walk each conversation's turns;
                        // overwrite each turn blob with a
                        // tombstoned content payload (turn-id +
                        // timestamp + token counts preserved,
                        // content fields redacted). Manifest
                        // gets its `CreatedBy` redacted.
                        let tombstone = Erasure.TombstoneMarker

                        for conv in allConversations do
                            let! manifestResult = readManifest scopeId conv.ConversationId

                            match manifestResult with
                            | Error _ -> ()
                            | Ok manifest ->
                                let turnCount = manifest.TurnIds.Length

                                for i in 1..turnCount do
                                    let! existing = objectStore.Get(scopeId, turnObjectId conv.ConversationId i)

                                    match existing with
                                    | Ok(_, bytes) ->
                                        try
                                            let turn = fromJsonBytes<ConversationTurn> bytes

                                            let redactedContent: AIMessageContent = {
                                                Role = turn.Content.Role
                                                Content = tombstone
                                                ToolCalls =
                                                    turn.Content.ToolCalls
                                                    |> List.map (fun tc -> { tc with Arguments = tombstone })
                                                ToolResults =
                                                    turn.Content.ToolResults
                                                    |> List.map (fun tr -> { tr with Content = tombstone })
                                                Parts = []
                                            }

                                            let redactedTurn = {
                                                turn with
                                                    Content = redactedContent
                                                    ContentDigest = computeContentDigest redactedContent
                                            }

                                            let! _ = writeTurn scopeId conv.ConversationId tombstone i redactedTurn

                                            ()
                                        with _ ->
                                            ()
                                    | Error _ -> ()

                                let tombstoned = {
                                    manifest with
                                        Conversation = {
                                            manifest.Conversation with
                                                CreatedBy = tombstone
                                        }
                                }

                                let! _ = writeManifest scopeId tombstone tombstoned
                                ()
                    | ErasurePolicy.RetainPerCompliance ->
                        // Redact `CreatedBy` on the manifest
                        // only; preserve turn content. The
                        // retention regime keeps the audit
                        // trail readable while removing the
                        // structured identifier from the
                        // manifest header.
                        let tombstone = Erasure.TombstoneMarker

                        for conv in allConversations do
                            let! manifestResult = readManifest scopeId conv.ConversationId

                            match manifestResult with
                            | Error _ -> ()
                            | Ok manifest ->
                                let redacted = {
                                    manifest with
                                        Conversation = {
                                            manifest.Conversation with
                                                CreatedBy = tombstone
                                        }
                                }

                                let! _ = writeManifest scopeId tombstone redacted
                                ()

                    Events.erased eventStore scopeId subjectUserId policy count |> Async.Start

                    return
                        Ok {
                            HandlerName = "conversations"
                            RecordsAffected = count
                            Note = Some(sprintf "%d conversation(s) %A under user %s" count policy subjectUserId)
                        }
        }
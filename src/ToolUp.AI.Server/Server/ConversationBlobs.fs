module internal ToolUp.AI.ConversationBlobs

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage

// ─── Phase 862 — the conversation blobs, read and written in one place ──
//
// Each conversation has two sibling blobs, and two handlers write them —
// the assistant handler after a full turn, the fast-path beacon handler
// after a Tier-1 resolution:
//
//   ai-conversations/{id}.json          UI-facing `ConversationMessage list`
//   ai-conversations/{id}.history.json  provider history, `AIProviderMessage list`
//
// Both handlers read and write them through this module, over the
// canonical types (`ConversationMessage` in Platform.Core,
// `AIProviderMessage` in AI.Wire). Before Phase 862 the beacon carried a
// private shadow of `AIProviderMessage` whose `ToolResults` was a tuple
// list and which had no `Parts`: a history holding a tool turn failed to
// decode, the failure read as an empty history, and the beacon wrote
// `[] @ [user; assistant]` over the whole conversation (GP 10: a type that
// crosses a persistence boundary has one definition).
//
// Three rules the module enforces, so no caller can forget them:
//
//   1. **An absent blob is an empty history; a blob that is present and
//      cannot be read or decoded is an ERROR** (GP 9). The caller refuses
//      the write rather than replacing the stored history with what it
//      could not read, and the blob is left byte-identical.
//   2. **Absent additive fields are coerced at the read path.** The STJ
//      path yields `null` for a list member the JSON omits — entries the
//      pre-862 beacon wrote carry no `Parts` — and a null F# list NREs on
//      the first list operation (`isMultimodal`, on the next full turn).
//   3. **A save returns its `Upload` result.** A failed write is not
//      reported as success.
//
// `internal`: the persisted blob format is the contract, not these
// helpers, so the module adds no public surface.

/// Why a conversation blob could not be read or written.
type ConversationBlobError =
    /// The blob is present and its bytes do not decode to the expected type.
    | Undecodable of blobName: string * reason: string
    /// The store failed the read and the blob is present, so it cannot be
    /// read as absent.
    | Unreadable of blobName: string * reason: string
    /// The store refused the write.
    | WriteFailed of blobName: string * reason: string

module ConversationBlobError =
    /// Operator-facing detail: names the blob and the underlying reason.
    let describe (error: ConversationBlobError) : string =
        match error with
        | Undecodable(blob, reason) -> $"conversation blob '{blob}' is present but cannot be decoded ({reason})"
        | Unreadable(blob, reason) -> $"conversation blob '{blob}' is present but could not be read ({reason})"
        | WriteFailed(blob, reason) -> $"conversation blob '{blob}' could not be written ({reason})"

// Same settings as every other persisted AI blob, so the bytes match what
// the Fable client and the listing / retention readers expect.
let private jsonOptions = FableConverters.create ()

let conversationBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.json"

let providerHistoryBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.history.json"

// ─── Read-path coercion of absent additive fields ─────────────────

let private orEmpty (xs: 'T list) : 'T list = if isNull (box xs) then [] else xs

let private normaliseConversationMessage (m: ConversationMessage) : ConversationMessage =
    if
        isNull (box m.ToolCalls)
        || isNull (box m.RetrievedSources)
        || isNull (box m.Parts)
    then
        {
            m with
                ToolCalls = orEmpty m.ToolCalls
                RetrievedSources = orEmpty m.RetrievedSources
                Parts = orEmpty m.Parts
        }
    else
        m

let private normaliseProviderMessage (m: AIProviderMessage) : AIProviderMessage =
    if isNull (box m.ToolCalls) || isNull (box m.ToolResults) || isNull (box m.Parts) then
        {
            m with
                ToolCalls = orEmpty m.ToolCalls
                ToolResults = orEmpty m.ToolResults
                Parts = orEmpty m.Parts
        }
    else
        m

let private decodeList<'T>
    (normalise: 'T -> 'T)
    (blobName: string)
    (bytes: byte[])
    : Result<'T list, ConversationBlobError> =
    try
        let decoded =
            JsonSerializer.Deserialize<'T list>(Encoding.UTF8.GetString bytes, jsonOptions)

        if isNull (box decoded) then
            Error(Undecodable(blobName, "the blob decodes to null"))
        elif decoded |> List.exists (fun m -> isNull (box m)) then
            Error(Undecodable(blobName, "the blob holds a null message"))
        else
            Ok(decoded |> List.map normalise)
    with ex ->
        Error(Undecodable(blobName, ex.Message))

/// Decode conversation-blob bytes. Read-only callers that deliberately
/// degrade (the listing, the retention sweep) match on the error; nothing
/// that WRITES may treat an error as an empty conversation.
let decodeConversation (blobName: string) (bytes: byte[]) : Result<ConversationMessage list, ConversationBlobError> =
    decodeList normaliseConversationMessage blobName bytes

let decodeProviderHistory (blobName: string) (bytes: byte[]) : Result<AIProviderMessage list, ConversationBlobError> =
    decodeList normaliseProviderMessage blobName bytes

// ─── Load / save ──────────────────────────────────────────────────

let private load
    (decode: string -> byte[] -> Result<'T list, ConversationBlobError>)
    (storage: IBlobStorage)
    (container: string)
    (blobName: string)
    : Async<Result<'T list, ConversationBlobError>> =
    async {
        match! storage.Download(container, blobName) with
        | Ok bytes -> return decode blobName bytes
        | Error reason ->
            // `Download` does not type "not found" apart from a store
            // failure, so presence decides: only a blob that is not there
            // is an empty history.
            let! present = storage.Exists(container, blobName)

            if present then
                return Error(Unreadable(blobName, reason))
            else
                return Ok []
    }

let private save
    (storage: IBlobStorage)
    (container: string)
    (blobName: string)
    (value: 'T)
    : Async<Result<unit, ConversationBlobError>> =
    async {
        let bytes = JsonSerializer.Serialize(value, jsonOptions) |> Encoding.UTF8.GetBytes

        match! storage.Upload(container, blobName, bytes) with
        | Ok _ -> return Ok()
        | Error reason -> return Error(WriteFailed(blobName, reason))
    }

/// The UI-facing conversation. `Ok []` only when the blob is absent.
let loadConversation (storage: IBlobStorage) (container: string) (conversationId: Guid) =
    load decodeConversation storage container (conversationBlobName conversationId)

let saveConversation
    (storage: IBlobStorage)
    (container: string)
    (conversationId: Guid)
    (messages: ConversationMessage list)
    =
    save storage container (conversationBlobName conversationId) messages

/// The round-trippable provider history. `Ok []` only when the blob is absent.
let loadProviderHistory (storage: IBlobStorage) (container: string) (conversationId: Guid) =
    load decodeProviderHistory storage container (providerHistoryBlobName conversationId)

let saveProviderHistory
    (storage: IBlobStorage)
    (container: string)
    (conversationId: Guid)
    (messages: AIProviderMessage list)
    =
    save storage container (providerHistoryBlobName conversationId) messages

// ─── Phase 6j.D — conversation-ownership gate ─────────────────────
//
// Both append paths (the beacon and `SubmitMessage`) cross-check the caller
// against the conversation's `CreatedBy` — the first persisted message's
// owner field — so a member of a shared container (`Team` / `MultiTeam`)
// cannot append to another member's conversation. Semantics:
//   1. `existing = []` — new conversation; the caller becomes its creator.
//   2. `existing[0].CreatedBy = ""` — legacy (pre-6j.D) blob with no
//      recorded owner; accepted, since the field was added without a
//      backfill and locking these out would break them on the upgrade.
//   3. `existing[0].CreatedBy = caller` — same user; accepted.
//   4. otherwise — cross-user; refused with the owner of record, which the
//      `BeaconRejected` audit row records.
// Pure: the handler resolves the inputs and applies the result.

let checkOwnership (existing: ConversationMessage list) (callerUserId: string) : Result<unit, string> =
    match existing with
    | [] -> Ok()
    | first :: _ ->
        let owner = first.CreatedBy

        if String.IsNullOrEmpty owner then Ok()
        elif owner = callerUserId then Ok()
        else Error owner
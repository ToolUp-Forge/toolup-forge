module internal ToolUp.AI.ConversationBlobs

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage

// ─── The conversation record, read and written in one place ──────────
//
// Each conversation is four sibling blobs:
//
//   ai-conversations/{id}.json          UI-facing `ConversationMessage list`
//   ai-conversations/{id}.history.json  provider history, `AIProviderMessage list`
//   ai-conversations/{id}.meta.json     `ConversationMeta` — provider override, title
//   ai-conversations/{id}.consent.json  `AIConsentState` — cross-module decisions
//
// Every reader and writer goes through this module, over the canonical
// types; `ConversationRecord` names the four together and holds the
// writers. The readers are per sibling (`loadConversation`,
// `loadProviderHistory`, `loadMeta`, `loadConsent`) because no caller may
// read all four at once: a turn reads the UI blob alone to learn the owner,
// and the rest only after its gate admits the caller. Three rules hold for
// every sibling, so no caller can forget them:
//
//   1. **An absent blob is empty; a blob that is present and cannot be
//      read or decoded is an ERROR** (GP 9). The caller refuses the write
//      rather than replacing the stored blob with what it could not read,
//      and the blob is left byte-identical.
//   2. **Absent additive fields are coerced at the read path.** The STJ
//      path yields `null` for a list or map member the JSON omits, and a
//      null F# collection NREs on its first use.
//   3. **A save returns its `Upload` result.** A failed write is not
//      reported as success.
//
// An exchange — a full turn, or a fast-path beacon — is written by ONE
// function, `ConversationRecord.appendExchange`, in ONE order: the
// history, then the UI. The UI append is a guarded read-modify-write
// (`BlobMapStore`, conditional where the backend supports it), so two
// writers appending at once do not lose one another's messages. The only
// half-written shape a reader can meet is therefore a history one exchange
// AHEAD of the UI: the next turn reads that history and carries the
// exchange forward, and the chat shows it as missing until then.
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

/// Per-conversation metadata: server-only fields the UI does not render
/// directly. Kept apart from the UI blob so list-page and chat-page reads
/// do not pull either side's payload unnecessarily.
type ConversationMeta = {
    OverrideProviderLabel: string option
    /// The model-generated title, when titling is composed in `Generate`
    /// mode and the call succeeded. `None` otherwise, and for every meta
    /// blob written before this field existed (a missing option field
    /// deserialises to `None`); the listing then derives a title from the
    /// first user message.
    Title: string option
}

module ConversationMeta =
    let empty = {
        OverrideProviderLabel = None
        Title = None
    }

// Same settings as every other persisted AI blob, so the bytes match what
// the Fable client and the listing / retention readers expect.
let private jsonOptions = FableConverters.create ()

let conversationBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.json"

let providerHistoryBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.history.json"

let metaBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.meta.json"

let consentBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.consent.json"

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

let private normaliseConsent (state: AIConsentState) : AIConsentState =
    if isNull (box state.CrossModuleAllowlist) then
        AIConsentState.empty
    else
        state

let private decodeValue<'T> (blobName: string) (bytes: byte[]) : Result<'T, ConversationBlobError> =
    try
        let decoded =
            JsonSerializer.Deserialize<'T>(Encoding.UTF8.GetString bytes, jsonOptions)

        if isNull (box decoded) then
            Error(Undecodable(blobName, "the blob decodes to null"))
        else
            Ok decoded
    with ex ->
        Error(Undecodable(blobName, ex.Message))

let private decodeList<'T>
    (normalise: 'T -> 'T)
    (blobName: string)
    (bytes: byte[])
    : Result<'T list, ConversationBlobError> =
    decodeValue<'T list> blobName bytes
    |> Result.bind (fun decoded ->
        if decoded |> List.exists (fun m -> isNull (box m)) then
            Error(Undecodable(blobName, "the blob holds a null message"))
        else
            Ok(decoded |> List.map normalise))

/// Decode conversation-blob bytes. Read-only callers that deliberately
/// degrade (the listing, the retention sweep) match on the error; nothing
/// that WRITES may treat an error as an empty conversation.
let decodeConversation (blobName: string) (bytes: byte[]) : Result<ConversationMessage list, ConversationBlobError> =
    decodeList normaliseConversationMessage blobName bytes

let decodeProviderHistory (blobName: string) (bytes: byte[]) : Result<AIProviderMessage list, ConversationBlobError> =
    decodeList normaliseProviderMessage blobName bytes

let private decodeMeta (blobName: string) (bytes: byte[]) : Result<ConversationMeta, ConversationBlobError> =
    decodeValue<ConversationMeta> blobName bytes

let private decodeConsent (blobName: string) (bytes: byte[]) : Result<AIConsentState, ConversationBlobError> =
    decodeValue<AIConsentState> blobName bytes |> Result.map normaliseConsent

// ─── Load / save ──────────────────────────────────────────────────

let private load
    (decode: string -> byte[] -> Result<'T, ConversationBlobError>)
    (empty: 'T)
    (storage: IBlobStorage)
    (container: string)
    (blobName: string)
    : Async<Result<'T, ConversationBlobError>> =
    async {
        match! storage.Download(container, blobName) with
        | Ok bytes -> return decode blobName bytes
        | Error reason ->
            // `Download` does not type "not found" apart from a store
            // failure, so presence decides: only a blob that is not there
            // is empty.
            let! present = storage.Exists(container, blobName)

            if present then
                return Error(Unreadable(blobName, reason))
            else
                return Ok empty
    }

let private encode (value: 'T) : byte[] =
    JsonSerializer.Serialize(value, jsonOptions) |> Encoding.UTF8.GetBytes

let private save
    (storage: IBlobStorage)
    (container: string)
    (blobName: string)
    (value: 'T)
    : Async<Result<unit, ConversationBlobError>> =
    async {
        match! storage.Upload(container, blobName, encode value) with
        | Ok _ -> return Ok()
        | Error reason -> return Error(WriteFailed(blobName, reason))
    }

/// The UI-facing conversation. `Ok []` only when the blob is absent.
let loadConversation (storage: IBlobStorage) (container: string) (conversationId: Guid) =
    load decodeConversation [] storage container (conversationBlobName conversationId)

/// The round-trippable provider history. `Ok []` only when the blob is absent.
let loadProviderHistory (storage: IBlobStorage) (container: string) (conversationId: Guid) =
    load decodeProviderHistory [] storage container (providerHistoryBlobName conversationId)

/// The metadata. `Ok ConversationMeta.empty` only when the blob is absent.
let loadMeta (storage: IBlobStorage) (container: string) (conversationId: Guid) =
    load decodeMeta ConversationMeta.empty storage container (metaBlobName conversationId)

/// The consent record. `Ok AIConsentState.empty` only when the blob is absent.
let loadConsent (storage: IBlobStorage) (container: string) (conversationId: Guid) =
    load decodeConsent AIConsentState.empty storage container (consentBlobName conversationId)

// ─── The record ───────────────────────────────────────────────────

let private uiCodec: BlobCodec<ConversationMessage list> = {
    Encode = encode
    Decode =
        fun bytes ->
            decodeConversation "ui" bytes
            |> Result.mapError (function
                | Undecodable(_, reason)
                | Unreadable(_, reason)
                | WriteFailed(_, reason) -> reason)
}

let private uiStoreLogger = ConsoleLogger.ConsoleLogger() :> ILogger

// One `BlobMapStore` per storage instance, so its capability probe and its
// one-time fallback warning are paid once per backend, not once per append.
let private uiStores =
    System.Runtime.CompilerServices.ConditionalWeakTable<IBlobStorage, BlobMapStore<ConversationMessage list>>()

let private uiStoreFor (storage: IBlobStorage) =
    uiStores.GetValue(storage, fun s -> BlobMapStore<ConversationMessage list>(s, uiCodec, uiStoreLogger))

let private ofStoreError (error: BlobMapStoreError) : ConversationBlobError =
    match error with
    | BlobMapStoreError.Unreadable(blob, reason) -> Unreadable(blob, reason)
    | BlobMapStoreError.Quarantined(blob, copy, reason) ->
        Undecodable(blob, $"{reason}; its bytes were copied to '{copy}'")
    | BlobMapStoreError.WriteFailed(blob, reason) -> WriteFailed(blob, reason)
    | BlobMapStoreError.Contended(blob, attempts) ->
        WriteFailed(blob, $"it changed under every one of {attempts} attempts")

module ConversationRecord =

    /// Every blob a conversation owns, in the order an erasure deletes them.
    let siblingNames (conversationId: Guid) : string list = [
        conversationBlobName conversationId
        providerHistoryBlobName conversationId
        metaBlobName conversationId
        consentBlobName conversationId
    ]

    /// The prefix of the copies `appendExchange` quarantines an undecodable
    /// UI blob to — the conversation's data too, erased with it.
    let quarantinePrefix (conversationId: Guid) : string =
        conversationBlobName conversationId + ".corrupt-"

    /// Read-modify-write of the metadata. Refuses, writing nothing, when the
    /// stored metadata cannot be read.
    let updateMeta
        (storage: IBlobStorage)
        (container: string)
        (conversationId: Guid)
        (change: ConversationMeta -> ConversationMeta)
        : Async<Result<unit, ConversationBlobError>> =
        async {
            match! loadMeta storage container conversationId with
            | Error e -> return Error e
            | Ok meta -> return! save storage container (metaBlobName conversationId) (change meta)
        }

    /// Write the consent record whole.
    let saveConsent
        (storage: IBlobStorage)
        (container: string)
        (conversationId: Guid)
        (state: AIConsentState)
        : Async<Result<unit, ConversationBlobError>> =
        save storage container (consentBlobName conversationId) state

    /// Read-modify-write of the consent record. Refuses, writing nothing,
    /// when the stored record cannot be read.
    let updateConsent
        (storage: IBlobStorage)
        (container: string)
        (conversationId: Guid)
        (change: AIConsentState -> AIConsentState)
        : Async<Result<unit, ConversationBlobError>> =
        async {
            match! loadConsent storage container conversationId with
            | Error e -> return Error e
            | Ok state -> return! saveConsent storage container conversationId (change state)
        }

    /// Write one exchange: `history` (the whole provider history after it)
    /// first, then `exchange` appended to the UI messages under a guarded
    /// read-modify-write. A failed history write leaves both blobs as they
    /// were. Returns the UI messages the exchange was appended to.
    let appendExchange
        (storage: IBlobStorage)
        (container: string)
        (conversationId: Guid)
        (history: AIProviderMessage list)
        (exchange: ConversationMessage list)
        : Async<Result<ConversationMessage list, ConversationBlobError>> =
        async {
            match! save storage container (providerHistoryBlobName conversationId) history with
            | Error e -> return Error e
            | Ok() ->
                let! appended =
                    (uiStoreFor storage)
                        .Update(
                            container,
                            conversationBlobName conversationId,
                            fun current ->
                                let prior = defaultArg current []
                                BlobUpdate.Write(prior @ exchange, prior)
                        )

                return appended |> Result.mapError ofStoreError
        }

// ─── Conversation-ownership gate ──────────────────────────────────
//
// Both append paths (the beacon and `SubmitMessage`) cross-check the caller
// against the conversation's `CreatedBy` — the first persisted message's
// owner field — so a member of a shared container (`Team` / `MultiTeam`)
// cannot append to another member's conversation. Semantics:
//   1. `existing = []` — new conversation; the caller becomes its creator.
//   2. `existing[0].CreatedBy = ""` — legacy blob with no recorded owner;
//      accepted, since the field was added without a backfill and locking
//      these out would break them on the upgrade. A team scope narrows
//      this case — see `checkAppend`.
//   3. `existing[0].CreatedBy = caller` — same user; accepted.
//   4. otherwise — cross-user; refused with the owner of record, which the
//      `BeaconRejected` audit row records.
// Pure: the handler resolves the inputs and applies the result.
//
// The team conversation policy: appending a turn is AUTHORSHIP, and the
// owner gate stays owner-only at every level. Case 4 is refused even where
// `ConversationVisibility.canModify` would admit the caller, because the
// policy grants a level's elevated role delete and the provider override,
// never the right to write turns into another member's conversation. What
// the policy does change is case 2: under a level narrower than
// `TeamVisible` (at the conversation's creation or now, or when the team's
// record cannot be read) an ownerless legacy conversation is no longer open
// to every member, so the append is admitted only for a caller `canModify`
// admits. Under `TeamVisible` at both instants, and outside a team scope,
// case 2 is accepted exactly as before (GP 11). `checkAppend` applies both
// halves, and both append paths call it.

let checkOwnership (existing: ConversationMessage list) (callerUserId: string) : Result<unit, string> =
    match existing with
    | [] -> Ok()
    | first :: _ ->
        let owner = first.CreatedBy

        if String.IsNullOrEmpty owner then Ok()
        elif owner = callerUserId then Ok()
        else Error owner

/// Whether an append to an OWNERLESS legacy conversation needs
/// `canModify`: a level narrower than `TeamVisible` governed it at creation
/// or governs it now, or the team's record cannot be read (fail closed, as
/// every other check over an unreadable record does).
let private ownerlessAppendNeedsModify (state: TeamConversationPolicyStore.TeamVisibilityState) (createdAt: DateTime) =
    match TeamConversationPolicyStore.ConversationVisibility.levels state createdAt with
    | Some(TeamVisible, TeamVisible) -> false
    | _ -> true

/// The append gate with the team policy applied: `checkOwnership`
/// (owner-only), and for an ownerless legacy conversation under a narrowed
/// level, `canModify`. `Error owner` refuses with the owner of record (`""`
/// for an ownerless conversation). The team state and the caller's role are
/// read only when case 2 meets a team scope, so every other append costs
/// what it cost before.
let checkAppend
    (resolveState: unit -> Async<TeamConversationPolicyStore.TeamVisibilityState option>)
    (resolveViewer:
        TeamConversationPolicyStore.TeamVisibilityState -> Async<TeamConversationPolicyStore.ConversationViewer>)
    (existing: ConversationMessage list)
    (callerUserId: string)
    : Async<Result<unit, string>> =
    async {
        match checkOwnership existing callerUserId, existing with
        | Error owner, _ -> return Error owner
        | Ok(), first :: _ when String.IsNullOrEmpty first.CreatedBy ->
            match! resolveState () with
            | None -> return Ok()
            | Some state ->
                let createdAt = existing |> List.map _.Timestamp |> List.min

                if not (ownerlessAppendNeedsModify state createdAt) then
                    return Ok()
                else
                    let! viewer = resolveViewer state

                    if TeamConversationPolicyStore.ConversationVisibility.canModify state viewer "" createdAt then
                        return Ok()
                    else
                        return Error ""
        | Ok(), _ -> return Ok()
    }
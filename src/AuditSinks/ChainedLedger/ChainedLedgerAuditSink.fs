module ToolUp.Platform.AuditSinks.ChainedLedger

open System
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.LedgerChain
open ToolUp.Platform.BlobStorage

// ─── Public surface ──────────────────────────────────────────────
//
// A tamper-evident `IAuditSink`: every appended record carries the
// SHA-256 digest of its predecessor over a canonical serialisation, and
// the chain head is signable through an injected signer. A verification
// pass walks the stored chain and names the first tampered, dropped, or
// reordered record BY POSITION.
//
// **Composition cost when unused is zero (GP 11 / GP 13).** This is a
// companion package. A deployment that never adds the project reference
// and never constructs the sink is byte-for-byte what it was — no
// hosted service, no middleware, no allocation, no configuration file.
// A deployment that composes it but injects no signer gets the full
// chain and an unsigned head, with no key management to stand up.
//
// **Storage is the abstract `IBlobStorage`, per the archive-sink
// precedent.** The ledger writes through the interface, so a deployment
// points it at whichever backing store it already runs — local files in
// development, object storage with a write-once retention policy in
// production. Retention and immutability are configured at the
// destination; the sink writes the blob, the destination owns the
// promise.
//
// **Layout.**
//   `{prefix}/records/{firstSequence:D20}-{contentDigest}.jsonl` — one
//       line per record. The zero-padded leading sequence makes lexical
//       blob order equal chain order, so read-back needs a `List` and a
//       sort rather than an index; the content digest makes a retried
//       batch overwrite rather than duplicate.
//   `{prefix}/head.json` — the head pointer: chain length, head digest,
//       and the head signature when one has been taken.

/// Where the ledger lives.
type ChainedLedgerSettings = {
    /// Destination container. With object storage this is the bucket or
    /// container name; with local-file storage it is a directory under
    /// the storage root. Configuring write-once retention on it is the
    /// operator's act, not the sink's.
    Container: string
    /// Optional path prefix, so one container can host several
    /// deployments' ledgers without their chains interleaving.
    PathPrefix: string option
}

module ChainedLedgerSettings =
    /// Container `audit-ledger`, no prefix. Enough for a development rig
    /// to boot with no per-environment configuration; most deployments
    /// override `Container`.
    let defaults: ChainedLedgerSettings = {
        Container = "audit-ledger"
        PathPrefix = None
    }

/// Produces a detached signature over the chain head.
///
/// **Deliberately generic, and deliberately not depending on any
/// particular signing implementation.** The ledger needs three things
/// from a signer — a key identifier to record, an algorithm name to
/// record, and bytes-to-signature — and asking for more would couple the
/// ledger to whichever key-management substrate happened to exist when
/// it was written. A deployment implements this against its own signing
/// substrate; the ledger stays a consumer of the shape.
type ILedgerHeadSigner =
    /// Stable identifier for the key material, recorded in the head so a
    /// verifier can select the matching public key. Rotating a key means
    /// a new `KeyId`, so heads signed under the old key stay verifiable.
    abstract KeyId: string
    /// Algorithm name recorded alongside the signature, so a verifier
    /// can refuse rather than guess.
    abstract Algorithm: string
    /// Sign the canonical head bytes. `Error` is surfaced to the caller
    /// rather than swallowed — a signature that silently did not happen
    /// is worse than no signature at all.
    abstract Sign: headBytes: byte[] -> Async<Result<byte[], string>>

/// Checks a head signature. Needs only PUBLIC key material, which is the
/// property that makes a cold verification possible: an auditor with the
/// ledger blobs and the public key can confirm the head without any
/// access to the signing environment.
type ILedgerHeadVerifier =
    /// `Ok true` when the signature is valid for the key and algorithm
    /// the head recorded; `Ok false` when it is well-formed but wrong;
    /// `Error` when the verifier cannot answer at all (unknown key,
    /// unsupported algorithm) — a distinction the ledger preserves
    /// rather than collapsing into "invalid".
    abstract Verify:
        keyId: string * algorithm: string * headBytes: byte[] * signature: byte[] -> Async<Result<bool, string>>

/// The persisted head pointer.
type LedgerHead = {
    /// Number of records in the chain — signed alongside the digest, so
    /// a truncated ledger cannot be re-presented as a shorter valid one.
    RecordCount: int64
    /// Digest of the last record.
    HeadDigest: string
    /// Key that signed this head, when signed.
    KeyId: string option
    /// Algorithm the signature was taken under, when signed.
    Algorithm: string option
    /// Base64 detached signature over `LedgerChain.headBytes`.
    Signature: string option
    /// When the signature was taken, as an invariant round-trip string.
    SignedAt: string option
}

/// Where the head signature stands after a verification pass.
type HeadSignatureStatus =
    /// No signature was taken. The chain is still tamper-evident against
    /// an editor who cannot rewrite the tail; it is not pinned against
    /// one who can.
    | HeadUnsigned
    /// Signature present and valid for the recorded key.
    | HeadSignatureValid of keyId: string * algorithm: string
    /// Signature present and WRONG for the recorded key.
    | HeadSignatureInvalid of keyId: string * algorithm: string
    /// Signature present but no verifier could answer — no verifier was
    /// supplied, the key is unknown, or the algorithm is unsupported.
    /// Reported as its own case so "I could not check" is never rendered
    /// as "I checked and it was fine".
    | HeadSignatureUnverifiable of algorithm: string * reason: string

/// The outcome of verifying a stored ledger.
///
/// Three cases rather than a boolean-plus-details, so that no caller can
/// read a success case while an unverified signature sits in a field
/// beside it.
type LedgerVerification =
    /// The chain walks cleanly AND the head signature is acceptable —
    /// either validly signed, or unsigned with no verifier expected.
    | LedgerVerified of recordCount: int64 * headDigest: string * signature: HeadSignatureStatus
    /// The chain walks cleanly but the head signature did not verify.
    /// The records are internally consistent; what is missing is the
    /// proof that this is the chain the ledger actually wrote.
    | LedgerHeadUntrusted of recordCount: int64 * headDigest: string * signature: HeadSignatureStatus
    /// The chain is broken. Carries the FIRST break, positioned.
    | LedgerBroken of LedgerBreak

let private ledgerJsonOptions = FableConverters.create ()

/// Body carried in each record's `Payload`, canonicalised before it is
/// framed into the digest.
type LedgerPayload = {
    Subject: AuditSubject
    Event: AuditEvent
}

[<Literal>]
let private recordsSegment = "records"

[<Literal>]
let private headBlobName = "head.json"

let private root (settings: ChainedLedgerSettings) =
    match settings.PathPrefix with
    | Some prefix when not (String.IsNullOrWhiteSpace prefix) -> (prefix.Trim '/') + "/"
    | _ -> ""

/// Blob name for a batch starting at `firstSequence`, addressed by the
/// digest of its own content.
///
/// **Content-addressing is what makes the sink batch-idempotent**, which
/// `IAuditSink` requires: the dispatcher re-delivers a batch after a
/// transient failure, and the failure mode that matters here is a
/// segment that was written before the head pointer write failed. The
/// retry recomputes the same chain — same head, same envelopes, same
/// deterministic serialisation — so it produces the same bytes, the same
/// digest, and therefore the same blob name, and the write lands on top
/// of the first one instead of beside it. A random name would leave two
/// segments claiming the same sequence range, and the duplicate would
/// surface later as a spurious chain break.
///
/// The zero-padded sequence prefix is kept so lexical blob order is
/// numeric order for every `int64`.
let buildSegmentName (settings: ChainedLedgerSettings) (firstSequence: int64) (contentDigest: string) : string =
    sprintf "%s%s/%020d-%s.jsonl" (root settings) recordsSegment firstSequence contentDigest

/// Blob name of the head pointer.
let buildHeadName (settings: ChainedLedgerSettings) : string = root settings + headBlobName

/// Phase 677 — decides which scope facets an envelope is recorded under.
///
/// **Called at APPEND time, once, by the writer**, and the facets it
/// returns are framed into the record's digest. That placement is the
/// whole design: a per-party export then filters on a committed claim
/// rather than re-deriving entitlement from record content at export
/// time, which would make every export's filter a function of whatever
/// code ran that day.
///
/// A plain function rather than an interface: it is pure classification
/// over a value the caller already holds, with no lifecycle, no state
/// between calls and nothing to dispose. The default — `fun _ -> []` —
/// tags nothing, which is the shipped behaviour and is fail-closed at
/// the far end (an untagged record is visible to no party).
type LedgerScopeTagger = AuditEnvelope -> string list

/// Project one audit envelope into an unchained record, tagged with the
/// scope facets `tagger` assigns it. `chain` fills in the sequence and
/// the links.
let recordOfEnvelopeTagged (tagger: LedgerScopeTagger) (envelope: AuditEnvelope) : LedgerRecord =
    let payload: LedgerPayload = {
        Subject = envelope.Subject
        Event = envelope.Event
    }

    let payloadJson =
        JsonSerializer.Serialize(payload, ledgerJsonOptions) |> canonicaliseJson

    {
        Sequence = 0L
        PreviousDigest = genesisDigest
        Digest = ""
        SchemaVersion = AuditSchemaVersion.current
        OccurredAt = envelope.OccurredAt.ToString("o", Globalization.CultureInfo.InvariantCulture)
        ScopeId = envelope.ScopeId
        SubjectKind = AuditEnvelope.subjectKindString envelope
        EventType = AuditEvent.eventTypeName envelope.Event
        Payload = payloadJson
        ScopeFacets = envelope |> tagger |> LedgerRecord.normaliseFacets
    }

/// Project one audit envelope into an unchained, untagged record — the
/// pre-Phase-677 projection, unchanged and byte-identical.
let recordOfEnvelope (envelope: AuditEnvelope) : LedgerRecord =
    recordOfEnvelopeTagged (fun _ -> []) envelope

/// Chain a batch of envelopes onto an existing head, tagging each with
/// the scope facets `tagger` assigns, and returning the linked records
/// and the new head digest. Pure — the sink calls this under its append
/// lock, and tests call it directly to assert that the same envelopes
/// always produce the same chain.
let chainBatchTagged
    (tagger: LedgerScopeTagger)
    (startSequence: int64)
    (previousDigest: string)
    (batch: AuditEnvelope list)
    : LedgerRecord list * string =
    let records, headDigest =
        batch
        |> List.fold
            (fun (acc, previous) envelope ->
                let sequence = startSequence + int64 (List.length acc)

                let linked = envelope |> recordOfEnvelopeTagged tagger |> chain sequence previous

                acc @ [ linked ], linked.Digest)
            ([], previousDigest)

    records, headDigest

/// Chain a batch with no scope tagging — the pre-Phase-677 shape,
/// unchanged.
let chainBatch
    (startSequence: int64)
    (previousDigest: string)
    (batch: AuditEnvelope list)
    : LedgerRecord list * string =
    chainBatchTagged (fun _ -> []) startSequence previousDigest batch

let private serialiseRecords (records: LedgerRecord list) : byte[] =
    let lines =
        records
        |> List.map (fun record -> JsonSerializer.Serialize(record, ledgerJsonOptions))

    Encoding.UTF8.GetBytes(String.Join("\n", lines))

let private readHead (settings: ChainedLedgerSettings) (storage: IBlobStorage) = async {
    let name = buildHeadName settings
    let! exists = storage.Exists(settings.Container, name)

    if not exists then
        return Ok None
    else
        match! storage.Download(settings.Container, name) with
        | Error message -> return Error(sprintf "chained ledger head read failed: %s" message)
        | Ok bytes ->
            try
                let json = Encoding.UTF8.GetString bytes
                return Ok(Some(JsonSerializer.Deserialize<LedgerHead>(json, ledgerJsonOptions)))
            with ex ->
                return Error(sprintf "chained ledger head is unreadable: %s" ex.Message)
}

let private writeHead (settings: ChainedLedgerSettings) (storage: IBlobStorage) (head: LedgerHead) = async {
    let json = JsonSerializer.Serialize(head, ledgerJsonOptions)

    match! storage.Upload(settings.Container, buildHeadName settings, Encoding.UTF8.GetBytes json) with
    | Ok _ -> return Ok()
    | Error message -> return Error(sprintf "chained ledger head write failed: %s" message)
}

/// Read every stored record in chain order, plus a torn-tail diagnostic
/// when the final line could not be parsed.
///
/// A parse failure anywhere other than the very last line is itself a
/// torn tail as far as this reader is concerned — it stops at the first
/// unreadable line and reports its position, rather than skipping it and
/// producing a chain with an invisible hole.
let private readRecords (settings: ChainedLedgerSettings) (storage: IBlobStorage) = async {
    let prefix = root settings + recordsSegment + "/"
    let! names = storage.List(settings.Container, prefix)
    let ordered = names |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    // `ref` rather than `let mutable`: the async computation expression
    // compiles its body into closures, and a mutable local cannot be
    // captured by one.
    let records = ResizeArray<LedgerRecord>()
    let torn = ref None
    let failure = ref None

    for name in ordered do
        if Option.isNone torn.Value && Option.isNone failure.Value then
            match! storage.Download(settings.Container, name) with
            | Error message -> failure.Value <- Some(sprintf "chained ledger segment read failed: %s" message)
            | Ok bytes ->
                let text = Encoding.UTF8.GetString bytes

                let lines = if String.IsNullOrEmpty text then [||] else text.Split '\n'

                for line in lines do
                    if Option.isNone torn.Value && not (String.IsNullOrWhiteSpace line) then
                        try
                            // Coerced on the way in: a record written
                            // before scope facets existed omits the
                            // field, which this converter set reads as
                            // `null`. Every consumer downstream of here
                            // sees the empty list it claims to be.
                            records.Add(
                                JsonSerializer.Deserialize<LedgerRecord>(line, ledgerJsonOptions)
                                |> LedgerRecord.coerceFacets
                            )
                        with ex ->
                            torn.Value <- Some(sprintf "unreadable ledger line in segment %s: %s" name ex.Message)

    match failure.Value with
    | Some message -> return Error message
    | None -> return Ok(List.ofSeq records, torn.Value)
}

/// Everything a reader gets out of stored ledger blobs, before any
/// judgement is passed on it.
///
/// Exposed (Phase 677) because the scoped exporter needs exactly what
/// `verify` needs and must not re-implement the read: two readers of one
/// on-disk layout drift, and the one that drifts is the one a
/// counterparty is holding.
type StoredLedger = {
    /// Records in chain order, facets coerced.
    Records: LedgerRecord list
    /// A read failure after the records that parsed — a truncated final
    /// line. `None` when the ledger read cleanly to its end.
    TornTail: string option
    /// The head pointer, absent on a ledger that has never been written.
    Head: LedgerHead option
}

/// Read the stored records and the head pointer. No verification — the
/// caller decides what the bytes mean.
let read (settings: ChainedLedgerSettings) (storage: IBlobStorage) : Async<Result<StoredLedger, string>> = async {
    match! readRecords settings storage with
    | Error message -> return Error message
    | Ok(records, torn) ->
        match! readHead settings storage with
        | Error message -> return Error message
        | Ok head ->
            return
                Ok {
                    Records = records
                    TornTail = torn
                    Head = head
                }
}

/// Verify a stored ledger: walk the chain, then check the head pointer
/// and its signature.
///
/// Pass `None` for the verifier when there is no key material to hand —
/// a signed head then reports `HeadSignatureUnverifiable` and the result
/// is `LedgerHeadUntrusted`, never a quiet pass.
let verify
    (settings: ChainedLedgerSettings)
    (storage: IBlobStorage)
    (verifier: ILedgerHeadVerifier option)
    : Async<Result<LedgerVerification, string>> =
    async {
        match! readRecords settings storage with
        | Error message -> return Error message
        | Ok(records, torn) ->
            match verifyRecords records torn with
            | Error breakage -> return Ok(LedgerBroken breakage)
            | Ok(recordCount, headDigest) ->
                match! readHead settings storage with
                | Error message -> return Error message
                | Ok None ->
                    // No head pointer. Legitimate only for an empty
                    // ledger; on a non-empty one it means the pointer
                    // was lost or never written, which is a head we
                    // cannot trust even though the chain is sound.
                    if recordCount = 0L then
                        return Ok(LedgerVerified(0L, headDigest, HeadUnsigned))
                    else
                        return
                            Ok(
                                LedgerHeadUntrusted(
                                    recordCount,
                                    headDigest,
                                    HeadSignatureUnverifiable("none", "ledger head pointer is missing")
                                )
                            )
                | Ok(Some head) ->
                    if head.HeadDigest <> headDigest || head.RecordCount <> recordCount then
                        // The chain is internally sound but is not the
                        // chain the head claims. Records appended after
                        // the pointer was last written land here, as
                        // does a deliberately rolled-back pointer.
                        return
                            Ok(
                                LedgerHeadUntrusted(
                                    recordCount,
                                    headDigest,
                                    HeadSignatureUnverifiable(
                                        head.Algorithm |> Option.defaultValue "none",
                                        sprintf
                                            "head pointer records %d/%s, chain walks to %d/%s"
                                            head.RecordCount
                                            head.HeadDigest
                                            recordCount
                                            headDigest
                                    )
                                )
                            )
                    else
                        match head.Signature, head.KeyId, head.Algorithm with
                        | None, _, _ -> return Ok(LedgerVerified(recordCount, headDigest, HeadUnsigned))
                        | Some signature, Some keyId, Some algorithm ->
                            match verifier with
                            | None ->
                                return
                                    Ok(
                                        LedgerHeadUntrusted(
                                            recordCount,
                                            headDigest,
                                            HeadSignatureUnverifiable(
                                                algorithm,
                                                "head is signed but no verifier was supplied"
                                            )
                                        )
                                    )
                            | Some verifier ->
                                let bytes = headBytes recordCount headDigest

                                match! verifier.Verify(keyId, algorithm, bytes, Convert.FromBase64String signature) with
                                | Ok true ->
                                    return
                                        Ok(
                                            LedgerVerified(
                                                recordCount,
                                                headDigest,
                                                HeadSignatureValid(keyId, algorithm)
                                            )
                                        )
                                | Ok false ->
                                    return
                                        Ok(
                                            LedgerHeadUntrusted(
                                                recordCount,
                                                headDigest,
                                                HeadSignatureInvalid(keyId, algorithm)
                                            )
                                        )
                                | Error reason ->
                                    return
                                        Ok(
                                            LedgerHeadUntrusted(
                                                recordCount,
                                                headDigest,
                                                HeadSignatureUnverifiable(algorithm, reason)
                                            )
                                        )
                        | Some _, _, _ ->
                            return
                                Ok(
                                    LedgerHeadUntrusted(
                                        recordCount,
                                        headDigest,
                                        HeadSignatureUnverifiable(
                                            "unknown",
                                            "head carries a signature without a key id or algorithm"
                                        )
                                    )
                                )
    }

/// Tamper-evident `IAuditSink`. One segment blob per delivered batch;
/// each record chains to its predecessor's digest.
///
/// **Append ordering under concurrent writers — the guarantee, stated.**
/// A digest chain is inherently serial: record N+1's digest is a
/// function of record N's. The sink therefore SERIALISES appends through
/// a single in-process semaphore. Concurrent `Deliver` calls are
/// linearised in semaphore-acquisition order; each batch is chained as
/// one contiguous run, so a batch's records are never interleaved with
/// another batch's. No ordering is promised BETWEEN concurrent batches
/// beyond "some serial order", because none can be — the dispatcher's
/// per-scope `OccurredAt` ordering is the temporal source of truth, and
/// each record carries its own `OccurredAt` for that reason.
///
/// **Single-writer per ledger — detected, not prevented.** The semaphore
/// is in-process. Two processes writing the same container and prefix
/// would fork the chain. The sink guards against this optimistically:
/// every append re-reads the head pointer and refuses if it has moved
/// beneath it, naming the conflict. That is DETECTION, not exclusion —
/// the blob interface has no compare-and-set, so a genuine race can
/// still interleave two writes. Deployments needing multiple writers run
/// one ledger per writer and verify each chain independently.
type ChainedLedgerAuditSink
    (
        name: string,
        settings: ChainedLedgerSettings,
        blobStorage: IBlobStorage,
        signer: ILedgerHeadSigner option,
        tagger: LedgerScopeTagger
    ) =

    let appendLock = new SemaphoreSlim(1, 1)

    /// In-process view of the head, adopted from storage on first append
    /// and advanced locally thereafter. `None` means "not yet read".
    let mutable known: (int64 * string) option = None

    /// Take a head signature when a signer is composed. A signing failure
    /// fails the delivery: the dispatcher retries the batch, and the
    /// alternative — reporting success on a head that was not signed —
    /// would make the signature meaningless precisely when it matters.
    let signHead (recordCount: int64) (headDigest: string) = async {
        match signer with
        | None ->
            return
                Ok {
                    RecordCount = recordCount
                    HeadDigest = headDigest
                    KeyId = None
                    Algorithm = None
                    Signature = None
                    SignedAt = None
                }
        | Some signer ->
            match! signer.Sign(headBytes recordCount headDigest) with
            | Error message -> return Error(sprintf "chained ledger head signing failed: %s" message)
            | Ok signature ->
                return
                    Ok {
                        RecordCount = recordCount
                        HeadDigest = headDigest
                        KeyId = Some signer.KeyId
                        Algorithm = Some signer.Algorithm
                        Signature = Some(Convert.ToBase64String signature)
                        SignedAt = Some(DateTime.UtcNow.ToString("o", Globalization.CultureInfo.InvariantCulture))
                    }
    }

    let append (batch: AuditEnvelope list) = async {
        match! readHead settings blobStorage with
        | Error message -> return Error message
        | Ok stored ->
            let storedPosition =
                stored |> Option.map (fun head -> head.RecordCount, head.HeadDigest)

            match known, storedPosition with
            | Some(localCount, localDigest), Some(storedCount, storedDigest) when
                localCount <> storedCount || localDigest <> storedDigest
                ->
                // The head moved without us. Refusing is the honest
                // answer: appending anyway would fork the chain, and a
                // forked chain fails verification later, further from
                // the cause.
                return
                    Error(
                        sprintf
                            "chained ledger head moved beneath this writer (expected %d/%s, found %d/%s) — another writer is appending to the same ledger"
                            localCount
                            localDigest
                            storedCount
                            storedDigest
                    )
            | _ ->
                let startSequence, previousDigest =
                    match storedPosition with
                    | Some(count, digest) -> count, digest
                    | None -> 0L, genesisDigest

                let records, headDigest = chainBatchTagged tagger startSequence previousDigest batch
                let content = serialiseRecords records
                let segment = buildSegmentName settings startSequence (digestBytes content)

                match! blobStorage.Upload(settings.Container, segment, content) with
                | Error message -> return Error(sprintf "chained ledger segment write failed: %s" message)
                | Ok _ ->
                    let recordCount = startSequence + int64 (List.length records)

                    match! signHead recordCount headDigest with
                    | Error message -> return Error message
                    | Ok head ->
                        match! writeHead settings blobStorage head with
                        | Error message -> return Error message
                        | Ok() ->
                            known <- Some(recordCount, headDigest)
                            return Ok()
    }

    /// The pre-Phase-677 shape, preserved as an explicit secondary
    /// constructor rather than by defaulting the parameter. An optional
    /// parameter would fold both arities into ONE widened constructor,
    /// and the four-argument token would disappear from the public
    /// surface — a genuine break for every existing caller, not a
    /// baseline artefact.
    new(name: string, settings: ChainedLedgerSettings, blobStorage: IBlobStorage, signer: ILedgerHeadSigner option) =
        ChainedLedgerAuditSink(name, settings, blobStorage, signer, (fun _ -> []))

    interface IAuditSink with
        member _.Name = name

        member _.SchemaVersion = AuditSchemaVersion.current

        member _.Deliver(batch) = async {
            if List.isEmpty batch then
                return Ok()
            else
                do! appendLock.WaitAsync() |> Async.AwaitTask

                try
                    try
                        return! append batch
                    with ex ->
                        return Error(sprintf "chained ledger sink threw: %s" ex.Message)
                finally
                    appendLock.Release() |> ignore
        }

/// Construct an UNSIGNED chained ledger. Zero configuration beyond the
/// destination: the chain is built and verifiable, and no key material
/// is required to stand it up. This is the default a deployment should
/// reach for first — an unsigned chain is a large improvement on no
/// chain, and adding a signer later changes only this call.
let create (name: string) (settings: ChainedLedgerSettings) (blobStorage: IBlobStorage) : IAuditSink =
    ChainedLedgerAuditSink(name, settings, blobStorage, None) :> _

/// Construct a chained ledger whose head is signed by `signer` after
/// every append.
let createSigned
    (name: string)
    (settings: ChainedLedgerSettings)
    (blobStorage: IBlobStorage)
    (signer: ILedgerHeadSigner)
    : IAuditSink =
    ChainedLedgerAuditSink(name, settings, blobStorage, Some signer) :> _

/// Construct an UNSIGNED chained ledger whose records are tagged with
/// scope facets at append time, so a per-party scoped export can be
/// taken from it later (Phase 677).
///
/// **Tagging is a decision taken once, at the writer.** A deployment
/// that adds a tagger later does not re-tag what is already written —
/// records keep the facets they were appended with, and they must, since
/// the facets are inside the digest. The consequence is worth stating
/// plainly: earlier records are visible to no party scope, and an
/// exporter says so by withholding them rather than by omitting them.
let createScoped
    (name: string)
    (settings: ChainedLedgerSettings)
    (blobStorage: IBlobStorage)
    (tagger: LedgerScopeTagger)
    : IAuditSink =
    ChainedLedgerAuditSink(name, settings, blobStorage, None, tagger) :> _

/// Construct a scope-tagging chained ledger whose head is signed by
/// `signer` after every append — the full shape a multi-party
/// deployment composes: a chain a counterparty can verify, a head it
/// cannot forge, and facets it can filter on.
let createSignedScoped
    (name: string)
    (settings: ChainedLedgerSettings)
    (blobStorage: IBlobStorage)
    (signer: ILedgerHeadSigner)
    (tagger: LedgerScopeTagger)
    : IAuditSink =
    ChainedLedgerAuditSink(name, settings, blobStorage, Some signer, tagger) :> _

// ─── Phase 686 — the deployment verification report's ledger source ──
//
// The report composes five verifiers and reaches none of them by
// package reference: it sits in `ToolUp.Platform.Server`, upstream of
// this assembly, so a reference in that direction would invert the
// graph and nail every deployment composing the report to this ledger
// (GP 1). The seam is a thunk, and this is the adapter that fills it.
//
// It performs NO verification of its own — it calls `verify` above and
// re-labels the answer. The single discrimination it adds is the one
// the report's section verdict needs and `LedgerVerification` does not
// happen to draw: an untrusted head splits into one that was REJECTED
// (a signature present and not valid; a head pointer disagreeing with
// the chain) and one that could not be JUDGED (signed with no verifier
// supplied; head pointer missing). Folding those together would let a
// deployment silence a bad head signature simply by withholding the
// verifier, which would be the cheapest possible attack on the report.

let private breakKindLabel (kind: LedgerBreakKind) : string =
    match kind with
    | TamperedRecord -> "tampered-record"
    | DroppedRecord -> "dropped-record"
    | ReorderedRecord -> "reordered-record"
    | BrokenLink -> "broken-link"
    | TornTail -> "torn-tail"

let private signatureLabel (status: HeadSignatureStatus) : string =
    match status with
    | HeadUnsigned -> "unsigned"
    | HeadSignatureValid(keyId, algorithm) -> sprintf "valid (%s / %s)" keyId algorithm
    | HeadSignatureInvalid(keyId, algorithm) -> sprintf "INVALID (%s / %s)" keyId algorithm
    | HeadSignatureUnverifiable(algorithm, reason) -> sprintf "unverifiable (%s): %s" algorithm reason

/// Map one `LedgerVerification` onto the report's tier-neutral mirror.
/// Exposed separately from the thunk below so a test can assert the
/// mapping without standing up storage.
let toLedgerIntegrity (verification: LedgerVerification) : LedgerIntegrity =
    match verification with
    | LedgerVerified(records, headDigest, signature) ->
        LedgerChainVerified(records, headDigest, signatureLabel signature)
    | LedgerHeadUntrusted(records, headDigest, HeadSignatureUnverifiable(algorithm, reason)) ->
        // The head's trust could not be ESTABLISHED. Not a finding
        // against the ledger, and emphatically not a pass: the read is
        // incomplete, and the report exits non-zero on it.
        LedgerHeadUnverifiable(records, headDigest, sprintf "%s (%s)" reason algorithm)
    | LedgerHeadUntrusted(records, headDigest, signature) ->
        // Everything else that reaches `LedgerHeadUntrusted` is a
        // positive finding: a signature that is present and does not
        // verify, or a head pointer that disagrees with the chain the
        // walk actually built.
        LedgerHeadRejected(records, headDigest, signatureLabel signature)
    | LedgerBroken ledgerBreak ->
        LedgerChainBroken(ledgerBreak.Position, breakKindLabel ledgerBreak.Kind, ledgerBreak.Detail)

/// The deployment verification report's ledger source, over the settings
/// and storage the composition root already holds.
///
/// The composition root must retain `settings` and `blobStorage` itself —
/// `IAuditSink` exposes neither, deliberately, so there is no way to
/// recover them from a registered sink. Close over the same values passed
/// to `create` / `createSigned`.
///
/// **Pass the verifier whenever the head is signed.** Omitting it against
/// a signed head does not quietly pass: `verify` reports the head as
/// unverifiable and the report's section reads `Unreadable` and exits
/// non-zero.
let deploymentVerificationSource
    (settings: ChainedLedgerSettings)
    (blobStorage: IBlobStorage)
    (headVerifier: ILedgerHeadVerifier option)
    : unit -> Async<Result<LedgerIntegrity, string>> =
    fun () -> async {
        let! outcome = verify settings blobStorage headVerifier

        return outcome |> Result.map toLedgerIntegrity
    }
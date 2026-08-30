module ToolUp.Platform.AuditSinks.LedgerChain

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

// ─── Chain primitives ────────────────────────────────────────────
//
// The pure, IO-free half of the chained audit ledger: the canonical
// serialisation every digest is taken over, the digest chain itself,
// and the verification walk that reports the first break by position.
// Nothing here touches storage — `ChainedLedgerAuditSink.fs` owns the
// `IBlobStorage` layout and composes these functions.
//
// **Why the chain is worth having.** An append-reliable audit trail
// answers "what did the application record?"; it does not answer
// "is this still what the application recorded?". A record that chains
// to its predecessor's digest turns any post-hoc edit, reorder, or
// deletion into an arithmetic contradiction that a verifier can find
// and *position*, rather than a difference nobody can see.
//
// **What the chain does and does not prove.** The chain alone is
// tamper-EVIDENT against an editor who cannot recompute the tail: mutate
// one record and its own digest stops matching; drop or reorder one and
// the link breaks at a nameable position. It is NOT tamper-PROOF against
// an attacker with write access to the whole ledger, who can rewrite
// every record from the edit forward and produce a self-consistent
// chain. That residual is exactly what signing the head closes: a head
// signature the attacker cannot forge pins the chain the ledger actually
// wrote. Stated plainly because a reader who believes the chain alone is
// sufficient is worse off than one who knows its bound.

/// The digest a genesis record chains to — 64 hex zeros, i.e. the
/// all-zero SHA-256 width. A record claiming this predecessor asserts
/// it is the first in its chain, so a truncated-from-the-front ledger
/// cannot pass verification by simply starting later.
[<Literal>]
let genesisDigest =
    "0000000000000000000000000000000000000000000000000000000000000000"

/// One persisted ledger line.
///
/// **Every field is a primitive, deliberately.** The audit event body
/// travels as `Payload`, an already-serialised canonical JSON *string*,
/// rather than as the typed event. Two consequences, both wanted:
/// the digest is taken over exactly the bytes that are stored (so
/// verification is a text-and-bytes operation that can never disagree
/// with the writer about how a type serialises), and reading the ledger
/// back never depends on a converter round-tripping a wide domain union
/// faithfully. Consumers that want the typed body call
/// `LedgerRecord.decodePayload`; verification never does.
type LedgerRecord = {
    /// Zero-based position in the chain. Contiguous by construction —
    /// a gap is a dropped record and verification says so.
    Sequence: int64
    /// The digest of the record at `Sequence - 1`, or `genesisDigest`
    /// at position 0. This is the link.
    PreviousDigest: string
    /// SHA-256, lowercase hex, over this record's canonical bytes —
    /// which include `PreviousDigest`, so the digest commits to the
    /// entire prefix of the chain, not just to this record.
    Digest: string
    /// Audit envelope wire version the record was written under.
    SchemaVersion: int
    /// `OccurredAt` as an invariant round-trip string. Stored as text,
    /// not as a `DateTime`, so the digest never depends on a parse.
    OccurredAt: string
    /// Scope the event was recorded under.
    ScopeId: string
    /// Subject kind, denormalised so an auditor can filter without
    /// decoding `Payload`.
    SubjectKind: string
    /// Event type name, denormalised for the same reason.
    EventType: string
    /// Canonical JSON of the subject-and-event body.
    Payload: string
    /// Phase 677 — the scope facets this record was tagged with AT APPEND
    /// TIME, so a per-party export filters on data the chain committed to
    /// rather than by inspecting `Payload` after the fact. Content
    /// inspection at export time is a filter nobody can check: it depends
    /// on the exporter's code at the moment of export, and two exports of
    /// the same ledger can disagree without either being detectable. A
    /// facet decided by the writer and framed into the digest is a claim
    /// the chain itself carries.
    ///
    /// **The empty list is the shipped default and costs nothing.** It
    /// contributes ZERO bytes to `canonicalBytes` (see the facet block
    /// there), so every chain written before facets existed recomputes to
    /// exactly the digests it already stores and keeps verifying — the
    /// backward-compatible default GP 11 asks for, obtained structurally
    /// rather than by a version switch. A record carrying no facet is
    /// visible to NO party scope: fail-closed, so tagging that never
    /// happened cannot read as universal entitlement.
    ScopeFacets: string list
}

/// What a verification walk found wrong. One value per class, so a
/// caller can branch on the *kind* of failure rather than pattern-match
/// a diagnostic string.
type LedgerBreakKind =
    /// A record's stored digest disagrees with its recomputed digest —
    /// its content was edited in place.
    | TamperedRecord
    /// The sequence expected at this position is absent from the whole
    /// ledger — a record was deleted.
    | DroppedRecord
    /// The sequence expected at this position is present elsewhere in
    /// the ledger — records were permuted.
    | ReorderedRecord
    /// Sequence and self-digest are both internally consistent, but the
    /// record does not chain to its predecessor — e.g. a record spliced
    /// in from another ledger, or a rewritten tail whose join is wrong.
    | BrokenLink
    /// The ledger ends in a record that could not be read at all — a
    /// crash part-way through an append. Detected, never absorbed.
    | TornTail

/// The FIRST break, positioned. Verification stops here: everything
/// after a break is unverifiable in principle, and reporting a hundred
/// downstream consequences of one edit hides the edit.
type LedgerBreak = {
    /// Zero-based index in read order where the break was observed.
    Position: int64
    /// The sequence the offending record claims, when it has one.
    /// `None` for a torn tail, which by definition did not parse.
    Sequence: int64 option
    Kind: LedgerBreakKind
    /// Human-readable specifics — the two digests that disagreed, the
    /// expected-versus-found sequence, or the read error.
    Detail: string
}

module private Canonical =
    /// Frame one field as `<utf8-byte-length>:<bytes>`.
    ///
    /// Length-prefixed rather than delimiter-separated because a
    /// delimiter can appear inside a field and an escape scheme is one
    /// more thing to get wrong. With the length in front, no field
    /// value can be re-cut into a different field sequence, so two
    /// distinct records cannot canonicalise to the same bytes by
    /// smuggling a separator through a scope id or a JSON payload.
    let frame (buffer: MemoryStream) (field: string) =
        let bytes = Encoding.UTF8.GetBytes field
        let header = Encoding.UTF8.GetBytes(string bytes.Length + ":")
        buffer.Write(header, 0, header.Length)
        buffer.Write(bytes, 0, bytes.Length)

    let rec write (writer: Utf8JsonWriter) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            writer.WriteStartObject()

            let properties = List<JsonProperty>()

            for property in element.EnumerateObject() do
                properties.Add property

            properties.Sort(fun a b -> String.CompareOrdinal(a.Name, b.Name))

            for property in properties do
                writer.WritePropertyName property.Name
                write writer property.Value

            writer.WriteEndObject()
        | JsonValueKind.Array ->
            writer.WriteStartArray()

            for item in element.EnumerateArray() do
                write writer item

            writer.WriteEndArray()
        | _ -> element.WriteTo writer

/// Rewrite JSON into a canonical form: object properties sorted by
/// ordinal name at every depth, array order preserved, no insignificant
/// whitespace.
///
/// **Why not just trust the serialiser's output.** Property emission
/// order is an implementation detail of whichever converter produced
/// the JSON, and a digest that silently depends on it is a digest that
/// changes when a library does. Sorting makes the canonical form a
/// function of the *value*. Array order is meaningful data and is
/// deliberately left alone.
///
/// Honest bound: this canonicalises structure, not number spelling —
/// `1.0` and `1` remain distinct — which is sufficient here because
/// both sides of every comparison come from the same serialiser.
let canonicaliseJson (json: string) : string =
    use document = JsonDocument.Parse json
    use buffer = new MemoryStream()

    do
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        Canonical.write writer document.RootElement

    Encoding.UTF8.GetString(buffer.ToArray())

/// A record's facets, with the `null` an absent JSON field deserialises
/// to coerced to the empty list.
///
/// **Not defensiveness — the documented shape of an older record.** The
/// converter set this ledger serialises through initialises an absent
/// reference-typed field to `null`, and a null F# list throws on every
/// list operation (`[]` is the `Empty` singleton, not null). Every
/// record written before Phase 677 omits the field, so the read path and
/// the digest path both meet it.
let facetsOf (record: LedgerRecord) : string list =
    if obj.ReferenceEquals(record.ScopeFacets, null) then
        []
    else
        record.ScopeFacets

/// The exact bytes a record's digest is taken over: every field except
/// `Digest` itself, each length-framed, in a fixed order.
///
/// `PreviousDigest` is inside the framing, which is what makes the
/// digest commit to the whole prefix rather than to this record alone.
///
/// **The facet block is appended only when the record carries facets**,
/// and that conditional is load-bearing rather than an optimisation: a
/// record with no facets frames byte-for-byte what it framed before
/// facets existed, so an existing ledger's stored digests stay correct
/// and an existing head signature stays valid. The block frames the
/// COUNT before the facets, so `["ab"]` and `["a"; "b"]` cannot
/// canonicalise alike, and a facet list can never be re-cut into a
/// different one — the same argument length-framing makes for the
/// fields above.
let canonicalBytes (record: LedgerRecord) : byte[] =
    use buffer = new MemoryStream()
    let frame = Canonical.frame buffer

    frame (string record.SchemaVersion)
    frame (string record.Sequence)
    frame record.PreviousDigest
    frame record.OccurredAt
    frame record.ScopeId
    frame record.SubjectKind
    frame record.EventType
    frame record.Payload

    let facets = facetsOf record

    if not (List.isEmpty facets) then
        frame (string (List.length facets))

        for facet in facets do
            frame facet

    buffer.ToArray()

/// Lowercase-hex SHA-256 of arbitrary bytes.
let digestBytes (bytes: byte[]) : string =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

/// Recompute a record's digest from its content. Deterministic: the
/// same record always yields the same digest, on any machine, in any
/// process, at any time — which is what makes the chain reproducible
/// rather than merely self-consistent.
let computeDigest (record: LedgerRecord) : string = record |> canonicalBytes |> digestBytes

/// Chain a record onto a predecessor digest, filling in `Sequence`,
/// `PreviousDigest` and `Digest`. The single place a link is formed.
let chain (sequence: int64) (previousDigest: string) (record: LedgerRecord) : LedgerRecord =
    let linked = {
        record with
            Sequence = sequence
            PreviousDigest = previousDigest
            Digest = ""
    }

    {
        linked with
            Digest = computeDigest linked
    }

/// The bytes a head signature is taken over: the chain length and the
/// digest of its last record, length-framed like everything else.
///
/// Signing this pair — rather than the last record alone — is what
/// makes a truncated ledger detectable. An attacker who deletes the
/// final forty records and re-signs cannot, without the key, produce a
/// signature over the shorter count.
let headBytes (recordCount: int64) (headDigest: string) : byte[] =
    use buffer = new MemoryStream()
    let frame = Canonical.frame buffer

    frame (string recordCount)
    frame headDigest

    buffer.ToArray()

/// Walk a ledger in read order and return either the verified
/// `(recordCount, headDigest)` or the first break.
///
/// `tornTail` carries a read failure the caller hit *after* the records
/// it managed to parse — a truncated final line. A torn tail is only
/// reported once the intact prefix has been walked, so a ledger that
/// was both edited early and torn late reports the edit, which is the
/// more serious finding and the earlier position.
let verifyRecords (records: LedgerRecord list) (tornTail: string option) : Result<int64 * string, LedgerBreak> =
    let observed = records |> List.map _.Sequence |> Set.ofList

    let rec walk (index: int64) (previousDigest: string) (remaining: LedgerRecord list) =
        match remaining with
        | [] ->
            match tornTail with
            | Some detail ->
                Error {
                    Position = index
                    Sequence = None
                    Kind = TornTail
                    Detail = detail
                }
            | None -> Ok(index, previousDigest)
        | record :: rest ->
            if record.Sequence <> index then
                // A sequence that turns up somewhere else in the ledger
                // is a permutation; one that turns up nowhere is a
                // deletion. Both are visible here because the whole
                // sequence set was collected before the walk began.
                let kind =
                    if observed.Contains index then
                        ReorderedRecord
                    else
                        DroppedRecord

                Error {
                    Position = index
                    Sequence = Some record.Sequence
                    Kind = kind
                    Detail = sprintf "expected sequence %d at position %d, found %d" index index record.Sequence
                }
            else
                let recomputed = computeDigest record

                if recomputed <> record.Digest then
                    Error {
                        Position = index
                        Sequence = Some record.Sequence
                        Kind = TamperedRecord
                        Detail = sprintf "stored digest %s, recomputed %s" record.Digest recomputed
                    }
                elif record.PreviousDigest <> previousDigest then
                    Error {
                        Position = index
                        Sequence = Some record.Sequence
                        Kind = BrokenLink
                        Detail =
                            sprintf "record chains to %s, predecessor digest is %s" record.PreviousDigest previousDigest
                    }
                else
                    walk (index + 1L) record.Digest rest

    walk 0L genesisDigest records

module LedgerRecord =
    /// The record with its facets coerced away from `null` — what a
    /// reader applies immediately after deserialising a stored line, so
    /// nothing downstream has to know that an older record omits the
    /// field.
    let coerceFacets (record: LedgerRecord) : LedgerRecord = {
        record with
            ScopeFacets = facetsOf record
    }

    /// A facet set in canonical order: distinct, ordinally sorted.
    ///
    /// Applied by the PRODUCER, before the digest is taken, because the
    /// digest is over the list as stored. Two taggers that name the same
    /// set in different orders must not produce different chains, and
    /// this is where that is settled — verification recomputes over
    /// exactly what it reads and never re-sorts, so it can never
    /// disagree with the writer about what was framed.
    let normaliseFacets (facets: string list) : string list =
        facets
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> List.distinct
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    /// The subject-and-event body a record carries, decoded from
    /// `Payload`. Provided for consumers reading the ledger back;
    /// verification deliberately never calls it, so a decode failure
    /// can never be mistaken for a chain failure.
    let decodePayload<'T> (options: JsonSerializerOptions) (record: LedgerRecord) : Result<'T, string> =
        try
            Ok(JsonSerializer.Deserialize<'T>(record.Payload, options))
        with ex ->
            Error(sprintf "ledger payload decode failed at sequence %d: %s" record.Sequence ex.Message)
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.DeliveredEgress

open System
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Usage

// ─── Phase 742 — delivered-egress reconciliation ──────────────────────
//
// Phase 473 counts ORIGIN egress and says so deliberately. Phase 472 put
// a CDN in front, and an edge hit never reaches this process — so
// delivered egress diverges from origin egress by exactly the edge hit
// rate, and the gap is unmeasurable from inside the deployment. For
// metered or monetised media the delivered figure is the billable one.
//
// This module closes the loop the only way it can be closed honestly:
// by reading the edge's own account of what it served. The deployment
// supplies parsed access-log records; this module resolves each to a
// `(media, scope)` pair, drops what it cannot attribute, and folds the
// rest into the Phase 473 rollup under a distinct `delivered` series.
//
// ─── Why the seam takes PARSED records and no vendor SDK ─────────────
//
// The same `ICloudTranscodeProvider` / `IDelegatedUrlSigner` reasoning
// applies, and the 2026-08-28 survey of the two edges Phase 472 shaped
// its adapter for made it concrete rather than assumed. A single in-SDK
// log parser is not merely undesirable, it is not well defined:
//
//   * The FIELD SET is per-deployment configuration, not a format. Each
//     platform's log delivery names the fields it emits explicitly, so
//     two deployments on the SAME vendor emit different records. There
//     is no fixed schema for the SDK to bind to.
//   * The OUTPUT FORMAT is chosen per delivery — JSON, plain, W3C, raw
//     or Parquet on one; newline-delimited JSON on the other — with a
//     configurable field delimiter.
//   * The field NAMES and SEMANTICS differ. One reports the bytes it
//     sent as the total response INCLUDING headers; the other reports
//     the bytes returned to the client. Vocabularies for the cache
//     disposition differ too (`Hit`/`RefreshHit`/`Miss`/`Error` against
//     `hit`/`miss`/`expired`/`bypass`/`dynamic`). Phase 743 turned the
//     first of those — what a byte count MEANS — from a caveat into a
//     `ByteSemantics` declaration the source must make, because it is
//     the one difference that changes the ARITHMETIC rather than the
//     parsing.
//
// So the vendor-shaped half is a FIELD MAPPING, which is data, and the
// generic half is everything below. `ToolUp.Hosts.DeliveredEgress`
// carries two field-mapped reference parsers proving the seam from
// outside (GP 12); neither names a vendor in code, because there is
// nothing vendor-specific left once the field names are a parameter.
//
// ─── What the survey settled about DEDUP, and why there is no store ──
//
// Redelivery is real: one edge retries a failed push several times over
// several minutes, so a partially-consumed push arrives again; the other
// delivers a period's records across several files, can delay entries by
// up to 24 hours, and recommends combining every file for a period — so
// re-reading is the normal operating mode, not an error path.
//
// The unit of redelivery is therefore the BATCH, and the defence is
// idempotent WRITES rather than a remembered set of what has been seen.
// `UsageRecord.RecordId` is already the ledger's own dedup key — the
// blob-backed `IUsageLog` merges incoming rows into the existing
// per-`(scope, day)` rollup by that id — so ingestion is idempotent
// exactly when the id is a deterministic function of the record. It is
// (`dedupKey` below), and this phase adds no store, no watermark and no
// sweep (GP 9).
//
// ─── The honest limits, stated once here and again in the docs ───────
//
//   1. Attribution is only as complete as the deployment's own topology
//      allows. `MediaId` is recoverable from every route this SDK mints,
//      but the SCOPE is in the URL only on the `/media/signed/` form —
//      the other routes established it from ambient request context that
//      an access log never saw. `IMediaLibrary` has no `MediaId -> scope`
//      lookup to fall back on, and that is not an oversight: every one
//      of its members takes the scope as a parameter precisely so a
//      cross-scope read is structurally impossible (GP 4). A deployment
//      that wants the ambient-scope routes attributed composes an
//      `IDeliveredScopeResolver` declaring the mapping it already knows.
//   2. Without a per-request id from the edge, two byte-identical
//      responses logged in the same second collapse to one row. The
//      resulting error is an UNDERCOUNT and it is bounded by that
//      collision rate. Every edge surveyed does emit such an id, so the
//      lossy path is the fallback, not the expectation.
//   3. Nothing here reaches a network or a vendor API, and nothing
//      backfills. If the deployment's log pipeline drops a period, the
//      delivered series is short for that period permanently — one of
//      the surveyed edges states outright that it cannot backfill.

// ─── Vocabulary ───────────────────────────────────────────────────────

/// How the edge disposed of one logged response.
///
/// Three cases rather than the union of two vendors' vocabularies: the
/// MAPPING from `Hit`/`RefreshHit`/`hit`/`expired`/... onto these is the
/// deployment's, because it is exactly the vendor-shaped knowledge the
/// SDK must not hold. What the arithmetic needs is only this
/// distinction, and `DeliveredOutcomeUnknown` exists so a parser that
/// cannot classify a record says so instead of guessing.
type DeliveredCacheOutcome =
    /// The edge answered from its own cache. These are the bytes Phase
    /// 473 structurally could not see.
    | ServedFromEdge
    /// The edge went to the origin. Counted here AND by Phase 473's
    /// origin series, from two vantage points.
    | ServedFromOrigin
    /// Not classifiable from the record. Counted in delivered bytes,
    /// excluded from the hit-rate numerator.
    | DeliveredOutcomeUnknown

module DeliveredCacheOutcome =
    /// The `PlaybackTelemetry.OutcomeKey` token for a ledger row.
    let token (outcome: DeliveredCacheOutcome) : string =
        match outcome with
        | ServedFromEdge -> PlaybackTelemetry.OutcomeEdge
        | ServedFromOrigin -> PlaybackTelemetry.OutcomeOrigin
        | DeliveredOutcomeUnknown -> PlaybackTelemetry.OutcomeUnknown

/// Phase 743 — what a source's byte count MEANS.
///
/// **Why this is a type and not a doc comment.** Phase 742 discovered
/// that a delivered byte count's definition is per-deployment
/// configuration — one surveyed edge documents its byte field as the
/// total response INCLUDING headers, another as the bytes returned to
/// the client, and neither equals the origin's body-bytes count — and
/// recorded that in three places of prose. Prose leaves two holes a
/// billing consumer falls into: `DeliveredEgressBytes` can be read
/// without ever meeting the caveat, and two sources with different
/// definitions fold into one number with nothing recording that they
/// did. Making it a declaration closes both — it is the same move
/// `DeliveryLag` makes for a fact that also varies per deployment and
/// also cannot be inferred (portability rule 6).
///
/// **A source cannot decline: `UnknownByteSemantics` IS the answer.**
/// There is no `option` here and no default. A deployment whose log
/// documentation genuinely does not say has an honest thing to declare,
/// and the arithmetic treats it as what it is — counted in the delivered
/// total, excluded from every semantics-specific figure, never quietly
/// billed as one definition or the other.
type ByteSemantics =
    /// The response BODY only — the same quantity Phase 473's origin
    /// accounting counts. (Still not summable with it: a cache miss is
    /// counted by both, from two vantage points.)
    | BodyOnly
    /// The whole HTTP response, response headers included. Exceeds a
    /// body-only count of the same bytes by the header size, on every
    /// single response.
    | IncludesHeaders
    /// Not stated. Spelled out rather than left as a bare `Unknown` for
    /// the reason `DeliveredOutcomeUnknown` is: this module is `open`ed
    /// by its consumers, and two unrelated unions each contributing an
    /// `Unknown` case makes the shorter name a coin toss at every use
    /// site.
    | UnknownByteSemantics

module ByteSemantics =
    /// The `PlaybackTelemetry.ByteSemanticsKey` token for a ledger row.
    let token (semantics: ByteSemantics) : string =
        match semantics with
        | BodyOnly -> PlaybackTelemetry.SemanticsBodyOnly
        | IncludesHeaders -> PlaybackTelemetry.SemanticsIncludesHeaders
        | UnknownByteSemantics -> PlaybackTelemetry.SemanticsUnknown

    let all: ByteSemantics list = [ BodyOnly; IncludesHeaders; UnknownByteSemantics ]

/// One access-log record, already parsed out of whatever shape the
/// deployment's edge emits.
///
/// Deliberately the smallest record the reconciliation can work from —
/// five facts and an optional id. Widening it would push vendor
/// vocabulary across the seam, which is the thing this shape exists to
/// prevent.
type DeliveredRecord = {
    /// The requested URL. An absolute URL, an origin-relative path, or a
    /// path with its query string — all three are accepted, because
    /// which one an edge logs is its own choice (one splits the path and
    /// query into separate fields; the other offers both joined and
    /// split). Only the path and a `token` query parameter are read.
    Url: string
    /// Bytes the edge reported returning for this response. Whether that
    /// includes response headers is the edge's definition, not this
    /// SDK's — see the rollup field docs on why the delivered and origin
    /// series are not reconciled term-by-term.
    Bytes: int64
    /// When the edge served the response.
    At: DateTimeOffset
    /// The HTTP status the edge returned to the viewer.
    Status: int
    Outcome: DeliveredCacheOutcome
    /// The edge's own unique identifier for the request, when the
    /// deployment selected that field. Every edge surveyed emits one.
    /// Supplying it makes dedup exact; omitting it falls back to a
    /// content-derived key that is lossy in the undercount direction.
    RequestId: string option
}

/// A named group of records ingested together — typically one delivered
/// log file, which is the unit an edge actually redelivers.
///
/// The `BatchId` is carried for diagnostics and for the ingestion
/// outcome, NOT for dedup: keying dedup on it would mean the same
/// response re-listed under a different object name counted twice, which
/// is precisely the case redelivery produces.
type DeliveredBatch = {
    BatchId: string
    Records: DeliveredRecord list
    /// Phase 743 — what the byte counts in `Records` mean.
    ///
    /// **On the BATCH rather than on the record, and required rather than
    /// optional.** It sits here because a batch is one delivery's worth
    /// of one source's output, so the declaration is uniform across it
    /// and repeating it per record would invite rows within one file
    /// disagreeing about something a file cannot disagree about. It is
    /// required because `IngestBatch` is public and a PUSH topology calls
    /// it with no `IDeliveredLogSource` in sight — an optional field
    /// would let exactly that path decline, which is the path a billing
    /// integration is most likely to take.
    ///
    /// A batch fetched through a source is stamped with that source's
    /// declaration by `DeliveredEgressJobHandler`, so the two cannot
    /// drift into disagreeing.
    ByteSemantics: ByteSemantics
}

/// Why a record was not attributed. Each is a counted, named outcome —
/// never an error, because a log batch that is 3% unattributable must
/// still contribute the other 97%.
type DeliveredDropReason =
    /// The path is not one this SDK mints for a media item.
    | UnrecognisedPath
    /// The media id resolved but no scope could be established: the URL
    /// carried no verifiable signed token and no `IDeliveredScopeResolver`
    /// answered.
    | ScopeUnresolved
    /// A non-2xx response. A 404 or a 403 delivered an error body, not
    /// media bytes, and metering it would inflate the billable figure.
    | NonSuccessStatus
    /// Zero or negative bytes — nothing was delivered to attribute.
    | NonPositiveBytes

module DeliveredDropReason =
    /// Stable metric-tag token. Lower-case and hyphenated to match the
    /// tag vocabulary the rest of the media metrics use.
    let token (reason: DeliveredDropReason) : string =
        match reason with
        | UnrecognisedPath -> "unrecognised-path"
        | ScopeUnresolved -> "scope-unresolved"
        | NonSuccessStatus -> "non-success-status"
        | NonPositiveBytes -> "non-positive-bytes"

    let all: DeliveredDropReason list = [ UnrecognisedPath; ScopeUnresolved; NonSuccessStatus; NonPositiveBytes ]

/// What one ingestion did. Returned rather than logged so a deployment
/// can alert on its own shortfall — a drop rate that climbs is how a
/// changed field mapping or a new route announces itself.
type DeliveredIngestOutcome = {
    BatchId: string
    /// Phase 743 — the semantics the ingested batch declared, echoed so a
    /// caller reading only the outcome can partition its own accounting
    /// the same way the rollup does.
    ByteSemantics: ByteSemantics
    /// Records attributed to a `(media, scope)` and written.
    Attributed: int
    /// Delivered bytes across the attributed records.
    AttributedBytes: int64
    /// Of `AttributedBytes`, the part the edge served itself.
    EdgeServedBytes: int64
    /// Per-reason drop counts. Ordered by `DeliveredDropReason.all`, so
    /// two readers render the same table.
    Dropped: (DeliveredDropReason * int) list
}

module DeliveredIngestOutcome =
    /// Total records dropped, across every reason.
    let droppedTotal (outcome: DeliveredIngestOutcome) : int = outcome.Dropped |> List.sumBy snd

// ─── Path resolution — the inverse of `MediaEdgePaths` ────────────────

/// Which media artefact a logged path names.
type DeliveredTargetClass =
    /// The stored original, from either of its two routes.
    | DeliveredOriginal
    /// A derived blob, carrying its path relative to the item's derived
    /// directory — which is what decides the manifest / segment / poster
    /// response class.
    | DeliveredDerived of relativePath: string

/// What a logged URL resolves to before any scope is established.
type ParsedDeliveredPath = {
    Media: MediaId
    Class: DeliveredTargetClass
    /// The `token` query value, present only on the `/media/signed/{id}`
    /// form. Unverified at this point — it is a string off a log line.
    SignedToken: string option
}

/// The pure inverse of `MediaEdgePaths`, and deliberately nothing more.
///
/// It recognises exactly the paths this SDK mints and refuses everything
/// else, rather than being a general URL parser that guesses. A
/// deployment whose public URLs are rewritten by the edge (a vanity
/// prefix, a rewritten path) is not served by widening this — it is
/// served by normalising the URL in its own parser, where it knows its
/// own rewrite rules and this module cannot.
module DeliveredPath =

    [<Literal>]
    let private derivedPrefix = "/api/media/hls/"

    [<Literal>]
    let private streamPrefix = "/api/media/stream/"

    [<Literal>]
    let private signedPrefix = "/media/signed/"

    /// Split a URL into its path and raw query, tolerating an absolute
    /// URL, an origin-relative path, or either with a fragment. A
    /// delegated signer emits absolute URLs (that is the point of the
    /// seam), so an absolute form is the expected input, not an edge
    /// case.
    let private splitPathAndQuery (url: string) : (string * string) option =
        if String.IsNullOrWhiteSpace url then
            None
        else
            let trimmed = url.Trim()

            // Strip scheme + authority when present. `Uri` is not used:
            // it throws on the relative forms, and its exception cost on
            // a per-record path in a batch of millions is worth avoiding
            // for a test this simple.
            let afterAuthority =
                let schemeAt = trimmed.IndexOf "://"

                if schemeAt < 0 then
                    trimmed
                else
                    let rest = trimmed.Substring(schemeAt + 3)
                    let slashAt = rest.IndexOf '/'
                    if slashAt < 0 then "/" else rest.Substring slashAt

            if not (afterAuthority.StartsWith "/") then
                None
            else
                let withoutFragment =
                    let hashAt = afterAuthority.IndexOf '#'

                    if hashAt < 0 then
                        afterAuthority
                    else
                        afterAuthority.Substring(0, hashAt)

                let questionAt = withoutFragment.IndexOf '?'

                if questionAt < 0 then
                    Some(withoutFragment, "")
                else
                    Some(withoutFragment.Substring(0, questionAt), withoutFragment.Substring(questionAt + 1))

    /// Read one query parameter's value, URL-decoded. Returns the FIRST
    /// occurrence: a repeated parameter is a malformed request, and
    /// taking the first is what a server would have bound.
    let private queryValue (name: string) (query: string) : string option =
        if String.IsNullOrEmpty query then
            None
        else
            query.Split('&')
            |> Array.tryPick (fun pair ->
                let eq = pair.IndexOf '='

                if eq <= 0 then
                    None
                elif pair.Substring(0, eq) = name then
                    Some(Uri.UnescapeDataString(pair.Substring(eq + 1)))
                else
                    None)

    /// Reject a decoded segment that could escape the item's derived
    /// directory. The serving path refuses traversal too (`OpenDerived`
    /// returns `NotFound`), so a traversal attempt in a log is a probe
    /// that was already refused — it must not become an attribution.
    let private isSafeRelativePath (relativePath: string) : bool =
        not (String.IsNullOrWhiteSpace relativePath)
        && not (relativePath.Contains "..")
        && not (relativePath.StartsWith "/")
        && not (relativePath.Contains "\\")

    /// Resolve a logged URL to the media artefact it names, or `None`
    /// when it names none. Pure and total.
    let parse (url: string) : ParsedDeliveredPath option =
        match splitPathAndQuery url with
        | None -> None
        | Some(path, query) ->
            let decoded = Uri.UnescapeDataString path

            let idOf (prefix: string) =
                let raw = decoded.Substring(prefix.Length)

                if String.IsNullOrWhiteSpace raw || raw.Contains "/" then
                    None
                else
                    Some raw

            if decoded.StartsWith derivedPrefix then
                let rest = decoded.Substring(derivedPrefix.Length)
                let slashAt = rest.IndexOf '/'

                if slashAt <= 0 then
                    None
                else
                    let id = rest.Substring(0, slashAt)
                    let relativePath = rest.Substring(slashAt + 1)

                    if String.IsNullOrWhiteSpace id || not (isSafeRelativePath relativePath) then
                        None
                    else
                        Some {
                            Media = MediaId id
                            Class = DeliveredDerived relativePath
                            SignedToken = None
                        }
            elif decoded.StartsWith streamPrefix then
                idOf streamPrefix
                |> Option.map (fun id -> {
                    Media = MediaId id
                    Class = DeliveredOriginal
                    SignedToken = None
                })
            elif decoded.StartsWith signedPrefix then
                idOf signedPrefix
                |> Option.map (fun id -> {
                    Media = MediaId id
                    Class = DeliveredOriginal
                    SignedToken = queryValue "token" query
                })
            else
                None

    /// The `PlaybackTelemetry` response-class token for a resolved
    /// target. Routed through `responseClassForDerived` rather than
    /// re-deriving the extension tests, so a delivered row and an origin
    /// row for the same file can never disagree about its class.
    let responseClass (targetClass: DeliveredTargetClass) : string =
        match targetClass with
        | DeliveredOriginal -> PlaybackTelemetry.ClassOriginal
        | DeliveredDerived relativePath -> PlaybackTelemetry.responseClassForDerived relativePath

// ─── The dedup key ────────────────────────────────────────────────────

/// The deterministic `UsageRecord.RecordId` for one delivered record.
///
/// Derived by SHA-256 over LENGTH-PREFIXED inputs (so no field boundary
/// is ambiguous — `"ab" + "c"` and `"a" + "bc"` must not collide) with
/// the first 16 bytes taken as a GUID. Two properties are load-bearing:
///
///   * **Stable.** The same logged response yields the same id on every
///     machine and every run, which is the whole of the idempotency
///     guarantee — the ledger merges by this id.
///   * **Independent of the batch.** A response re-listed under a
///     different object name is still the same response, so the batch id
///     is deliberately NOT an input.
///
/// When the edge supplied a per-request id, that alone identifies the
/// record and nothing else is mixed in. Otherwise the record's own
/// content stands in, which collapses two byte-identical responses
/// logged in the same second — an undercount, bounded by that collision
/// rate, and preferable to the alternative of double-counting on every
/// redelivery.
let dedupKey (record: DeliveredRecord) : Guid =
    let parts =
        match record.RequestId with
        | Some id when not (String.IsNullOrWhiteSpace id) -> [ "rid"; id ]
        | _ -> [
            "syn"
            record.Url
            string record.Bytes
            string (record.At.ToUnixTimeMilliseconds())
            string record.Status
          ]

    let payload = StringBuilder()

    for part in parts do
        payload.Append(part.Length).Append(':').Append(part).Append('|') |> ignore

    let hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString()))
    Guid(hash[0..15])

// ─── Seams ────────────────────────────────────────────────────────────

/// Supplies the scope a media item belongs to, for the records whose URL
/// never carried one.
///
/// **Why this is the deployment's to answer and not the SDK's.** Only
/// `/media/signed/{id}?token=` puts the scope in the URL; the ambient
/// routes resolved it from request context an access log never captured.
/// `IMediaLibrary` cannot supply it either — every member takes the
/// scope as a parameter so that a cross-scope read is impossible to
/// express (GP 4), and adding a global `MediaId -> scope` lookup to close
/// this gap would dismantle exactly that property for the sake of a
/// reporting figure. A deployment already holds the mapping in whatever
/// index its own catalogue keeps; this seam is it declaring it.
///
/// Six portability rules (GP 12): identity by value (`MediaId` in,
/// `StorageScope` out); async at the boundary; absence is data (`None`,
/// not an exception, and not a callback); stateless between calls; no
/// ordering promise; no timing primitive to declare a precision for.
type IDeliveredScopeResolver =
    /// Stable name for diagnostics. Not an identity the SDK dispatches
    /// on.
    abstract Name: string

    /// The scope owning `id`, or `None` when the deployment does not
    /// know — which is a legitimate answer, counted as
    /// `ScopeUnresolved`, never an error.
    abstract ResolveScope: id: MediaId -> Async<StorageScope option>

/// Supplies batches of parsed access-log records to the reconciliation
/// job.
///
/// The vendor-shaped work — listing an object store, reading a push
/// destination, parsing a field mapping — lives entirely in the
/// deployment's implementation, exactly as `IEdgeCache`'s HTTP half and
/// `IDelegatedUrlSigner`'s signing callback do.
///
/// **Rule 4 (stateless between invocations) is satisfied by making
/// forgetfulness SAFE rather than by forbidding it.** A source is
/// welcome to be dumb — "every object written in the last day" is a
/// perfectly good implementation — because ingestion is idempotent by
/// `dedupKey`, so re-offering a batch costs a ledger merge and changes
/// no number. A source that tracks its own position is also fine; it is
/// an optimisation, not a correctness requirement.
type IDeliveredLogSource =
    /// Stable name for diagnostics.
    abstract Name: string

    /// How far behind real time this source's records are expected to
    /// run — rule 6, declared rather than assumed. The two edges
    /// surveyed differ by three orders of magnitude here: one pushes
    /// batches sub-minute, the other typically delivers within an hour
    /// and can delay entries by up to 24. A rollup read sooner than this
    /// is not missing data; it is early, and only a declared lag lets a
    /// deployment tell those apart.
    abstract DeliveryLag: TimeSpan

    /// Phase 743 — what this source's byte counts MEAN: rule 6 again, for
    /// a second fact that varies per deployment and cannot be inferred.
    ///
    /// The 2026-08-28 survey found the two surveyed edges document their
    /// byte fields differently — one as the whole response including
    /// headers, the other as the bytes returned to the client — and
    /// neither as the body-bytes quantity the origin counts. Which one a
    /// deployment gets depends on which field it selected in its own log
    /// delivery, so only the deployment can say, and there is deliberately
    /// no default: `UnknownByteSemantics` is an available and honest
    /// answer, and it is a different claim from silence.
    abstract ByteSemantics: ByteSemantics

    /// The batches available now. An empty list is success with nothing
    /// to do; failure is data, so a transient source error becomes a
    /// retryable job outcome rather than an exception.
    abstract FetchBatches: unit -> Async<Result<DeliveredBatch list, string>>

// ─── Ingestion ────────────────────────────────────────────────────────

/// Reconciles parsed CDN access-log records into the Phase 473 rollup.
///
/// Constructed by the deployment and called either directly (a push
/// topology — an object-created trigger handing over one file) or by
/// `DeliveredEgressJobHandler` on a schedule (a pull topology). Both are
/// first-class; neither is a new pipeline.
type DeliveredEgressIngestor
    (
        usageLog: IUsageLog,
        metrics: IMetricsSink option,
        signer: SignedUrl.MediaUrlSigner option,
        scopeResolver: IDeliveredScopeResolver option
    ) =

    /// Establish the scope for a resolved path: the URL's own signed
    /// token first, the deployment's resolver second.
    ///
    /// **The token's signature is VERIFIED, and that is a scope-isolation
    /// requirement rather than a formality.** The token arrives as a
    /// string on a log line, and anything a viewer can put in a query
    /// string reaches that line — so trusting the payload unverified
    /// would let anyone who can make one request attribute arbitrary
    /// bytes to any scope they care to name. Expiry is deliberately NOT
    /// checked (see `SignedUrl.verifySignature`): the bytes were served
    /// while the grant was live, and the log arrives long after.
    /// Returns the SCOPE ID rather than a `StorageScope`, because that
    /// is the whole of what an attribution needs and it is all the token
    /// actually proves. Rebuilding a full `StorageScope` here would mean
    /// inventing a `Persist` flag the signed payload never carried — a
    /// fabricated field is worse than an absent one.
    member private _.ResolveScopeId(parsed: ParsedDeliveredPath) : Async<string option> = async {
        let! fromToken =
            match parsed.SignedToken, signer with
            | Some token, Some s -> async {
                let! verified = s.VerifySignatureAsync token

                match verified with
                | Ok payload when payload.MediaId = MediaId.value parsed.Media -> return Some payload.ScopeId
                | _ -> return None
              }
            | _ -> async.Return None

        match fromToken with
        | Some scopeId -> return Some scopeId
        | None ->
            match scopeResolver with
            | Some resolver ->
                let! resolved = resolver.ResolveScope parsed.Media
                return resolved |> Option.map _.ScopeId
            | None -> return None
    }

    /// Classify one record: the `(attribution, bytes)` to write, or the
    /// reason it was dropped. Pure but for the scope lookup.
    member private this.Classify
        (record: DeliveredRecord)
        : Async<Result<PlaybackTelemetry.EgressAttribution * DeliveredRecord, DeliveredDropReason>> =
        async {
            if record.Status < 200 || record.Status > 299 then
                return Error NonSuccessStatus
            elif record.Bytes <= 0L then
                return Error NonPositiveBytes
            else
                match DeliveredPath.parse record.Url with
                | None -> return Error UnrecognisedPath
                | Some parsed ->
                    let! scopeId = this.ResolveScopeId parsed

                    match scopeId with
                    | None -> return Error ScopeUnresolved
                    | Some sid ->
                        let attribution: PlaybackTelemetry.EgressAttribution = {
                            Media = parsed.Media
                            ScopeId = sid
                            Class = DeliveredPath.responseClass parsed.Class
                        }

                        return Ok(attribution, record)
        }

    /// Ingest one batch. Idempotent: re-ingesting a batch already
    /// ingested writes rows the ledger already holds, de-duped by
    /// `RecordId`, and changes no number.
    ///
    /// Never fails on an unattributable record — those are counted into
    /// the returned outcome and into `DeliveredDroppedMetric`. The
    /// `Result` covers only the case where the ledger itself refuses.
    member this.IngestBatch(batch: DeliveredBatch) : Async<Result<DeliveredIngestOutcome, string>> = async {
        // Phase 743 — the batch's declaration, resolved once and written
        // onto every row it produces. Deliberately NOT an input to
        // `dedupKey`: the same logged response re-ingested after a source
        // corrected its declaration must still dedup as the same response,
        // or the correction would double-count every row it touched.
        let semanticsToken = ByteSemantics.token batch.ByteSemantics
        let mutable attributed = 0
        let mutable attributedBytes = 0L
        let mutable edgeServedBytes = 0L
        let drops = System.Collections.Generic.Dictionary<DeliveredDropReason, int>()

        let countDrop reason =
            match drops.TryGetValue reason with
            | true, n -> drops[reason] <- n + 1
            | _ -> drops[reason] <- 1

        // Every sink call is guarded, the same way Phase 473 guards its
        // own: a telemetry sink that throws must not take down a
        // reconciliation that has already written rows to the ledger.
        let emit (f: IMetricsSink -> unit) =
            match metrics with
            | Some sink ->
                try
                    f sink
                with _ ->
                    ()
            | None -> ()

        for record in batch.Records do
            let! classified = this.Classify record

            match classified with
            | Error reason ->
                countDrop reason

                emit (fun sink ->
                    sink.Increment(
                        PlaybackTelemetry.DeliveredDroppedMetric,
                        Map.ofList [ "reason", DeliveredDropReason.token reason ]
                    ))
            | Ok(attribution, rec') ->
                let outcomeToken = DeliveredCacheOutcome.token rec'.Outcome

                let row =
                    PlaybackTelemetry.deliveredRecord
                        attribution
                        outcomeToken
                        semanticsToken
                        rec'.Bytes
                        (dedupKey rec')
                        rec'.At.UtcDateTime

                do! usageLog.Record row

                emit (fun sink ->
                    sink.Record(
                        PlaybackTelemetry.DeliveredEgressMetric,
                        float rec'.Bytes,
                        Map.ofList [
                            "scope", attribution.ScopeId
                            "class", attribution.Class
                            "outcome", outcomeToken
                            "semantics", semanticsToken
                        ]
                    ))

                attributed <- attributed + 1
                attributedBytes <- attributedBytes + rec'.Bytes

                if rec'.Outcome = ServedFromEdge then
                    edgeServedBytes <- edgeServedBytes + rec'.Bytes

        return
            Ok {
                BatchId = batch.BatchId
                ByteSemantics = batch.ByteSemantics
                Attributed = attributed
                AttributedBytes = attributedBytes
                EdgeServedBytes = edgeServedBytes
                Dropped =
                    DeliveredDropReason.all
                    |> List.choose (fun reason ->
                        match drops.TryGetValue reason with
                        | true, n when n > 0 -> Some(reason, n)
                        | _ -> None)
            }
    }

    /// Ingest every batch a source currently offers, in order. Returns
    /// one outcome per batch.
    ///
    /// Stops at the FIRST ledger failure and returns what it has, rather
    /// than pressing on: a ledger that has started refusing will refuse
    /// the rest too, and the batches not yet ingested are safe to retry
    /// precisely because ingestion is idempotent.
    member this.IngestAll(batches: DeliveredBatch list) : Async<Result<DeliveredIngestOutcome list, string>> = async {
        let mutable failure = None
        let outcomes = ResizeArray<DeliveredIngestOutcome>()
        let mutable remaining = batches

        while failure.IsNone && not remaining.IsEmpty do
            let batch = List.head remaining
            remaining <- List.tail remaining
            let! result = this.IngestBatch batch

            match result with
            | Ok outcome -> outcomes.Add outcome
            | Error e -> failure <- Some e

        match failure with
        | Some e -> return Error e
        | None -> return Ok(List.ofSeq outcomes)
    }

// ─── The scheduled job (the Phase 534 composition shape) ──────────────

/// Pulls batches from an `IDeliveredLogSource` and ingests them.
///
/// **No `BackgroundService` and no new pipeline (GP 9/GP 13).** This is
/// an ordinary `IJobHandler` registered against the shipped
/// `IJobScheduler`, exactly as Phase 534's report subscriptions are — so
/// a deployment that composes no delivered-egress job runs nothing, and
/// one that does gets the scheduler's retry policy, run history and
/// admin surface for free rather than reinventing them.
///
/// Stateless between invocations (rule 4): every fact it needs comes
/// from the source and the ledger.
type DeliveredEgressJobHandler(ingestor: DeliveredEgressIngestor, source: IDeliveredLogSource) =

    /// The handler name this job registers under. Namespaced against the
    /// media companion so a cross-module clash is caught at registration
    /// (the `ScheduledJobDeclaration.HandlerName` convention).
    static member val HandlerName = "media.delivered-egress-reconciliation" with get

    interface IJobHandler with
        member _.Execute(_ctx: JobContext) : Async<JobResult> = async {
            let! fetched = source.FetchBatches()

            match fetched with
            // A source that cannot reach its log store right now is the
            // textbook transient failure: the same fetch next tick is
            // likely to succeed, and the scheduler's backoff is exactly
            // the right response.
            | Error e -> return TransientFailure(sprintf "[DeliveredEgress:%s] fetch failed: %s" source.Name e)
            | Ok [] -> return Success
            | Ok batches ->
                // Phase 743 — the SOURCE is authoritative for what its own
                // bytes mean, so every batch it produced is stamped with
                // its declaration rather than trusting whatever the fetch
                // callback happened to put on each batch. Two declarations
                // that could disagree would be worse than one: the batch
                // field exists for the PUSH path, where there is no source
                // to ask, and this is what keeps the pull path from
                // acquiring a second, drifting answer.
                let! ingested =
                    batches
                    |> List.map (fun batch -> {
                        batch with
                            ByteSemantics = source.ByteSemantics
                    })
                    |> ingestor.IngestAll

                match ingested with
                | Ok _ -> return Success
                | Error e -> return TransientFailure(sprintf "[DeliveredEgress:%s] ingest failed: %s" source.Name e)
        }

module DeliveredEgressJob =

    /// The declaration a deployment hands to its existing scheduled-job
    /// composition. Deliberately a VALUE rather than a compose-time
    /// mutation of `MediaLibraryServerApp`: the media composition root is
    /// unchanged by this phase, so a deployment that ingests nothing is
    /// byte-identical to Phase 473 (GP 11) and one that does opts in
    /// through the path it already uses for every other job.
    ///
    /// The default cadence is hourly, which is not arbitrary: the slower
    /// of the two edges surveyed typically delivers a period's log within
    /// an hour of the events. Polling faster than the source's declared
    /// `DeliveryLag` costs list calls and returns nothing new.
    let declaration
        (trigger: Trigger)
        (ingestor: DeliveredEgressIngestor)
        (source: IDeliveredLogSource)
        : ScheduledJobDeclaration =
        ScheduledJobDeclaration.create
            DeliveredEgressJobHandler.HandlerName
            (DeliveredEgressJobHandler(ingestor, source))
            trigger
        |> ScheduledJobDeclaration.withTags (
            Map.ofList [
                "source", source.Name
                "deliveryLag", string source.DeliveryLag
                "byteSemantics", ByteSemantics.token source.ByteSemantics
            ]
        )

    /// `declaration` on the hourly cadence described above.
    let hourly (ingestor: DeliveredEgressIngestor) (source: IDeliveredLogSource) : ScheduledJobDeclaration =
        declaration (CronTrigger "0 * * * *") ingestor source
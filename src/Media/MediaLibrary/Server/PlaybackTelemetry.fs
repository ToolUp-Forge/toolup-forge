// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.PlaybackTelemetry

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Usage

// ─── Phase 473 — playback + delivery telemetry ────────────────────────
//
// Hosting media without knowing plays, completion rates or egress bytes
// is a capability-sized gap. This module closes it with two emissions
// and no new pipeline: both flow into the shipped `IMetricsSink`
// (Phase 9e) and `IUsageLog` (Phase 9d) substrates.
//
//   1. **Egress accounting.** The range handler's single copy loop
//      counts the bytes it ACTUALLY wrote and hands them here. Not
//      `Content-Length` — that is what the server intended to send, and
//      an abandoned scrub is precisely the case where the two differ.
//   2. **Playback beacons.** `POST /api/media/beacon` takes a typed
//      `Started` / `Progress` / `Completed` event from a player.
//
// ─── Three things about this that are decisions, not details ─────────
//
// **What is counted is ORIGIN egress, never DELIVERED egress.** With a
// CDN composed (Phase 472), an edge hit never reaches this process: the
// bytes are served from a POP and nothing here observes them. So the
// `media.egress.bytes` ledger rows are the bytes that left THIS origin,
// and a deployment behind an edge must read them as such. The only
// origin-side signal about the size of the gap is the `s-maxage` the
// deployment itself declared per response class. Estimating delivered
// egress from that would be inventing a number, so this module does not
// — the CDN's own logs are the authority for what an edge served, and
// the honest thing is to label the dimension rather than to guess.
//
// **Phase 742 measures the gap rather than closing it.** A deployment
// that feeds its CDN access logs to `DeliveredEgress` gets a SECOND
// series here — `DeliveredEgressKind`, folded into `PlaybackRollup` as
// `DeliveredEgressBytes` / `EdgeServedBytes` / `EdgeHitRateByBytes` —
// carrying what the edge actually returned. It is a distinct series and
// never a correction: `OriginEgressBytes` still means exactly what it
// meant, measured the same way, and a deployment that ingests nothing
// reads byte-identically to Phase 473. The two are deliberately NOT
// summed anywhere, because a cache miss appears in both and their byte
// definitions differ (a CDN log field is typically the whole response
// including headers; the origin count is body bytes only). Where they
// disagree, the delivered figure is the billable one and the origin
// figure is the one this process can prove — which is why both survive.
//
// **Phase 743 types the second half of that caveat.** "Their byte
// definitions differ" was prose, and prose has two failure modes a
// billing consumer walks straight into: it can read
// `DeliveredEgressBytes` without ever meeting the sentence, and two
// sources with DIFFERENT definitions fold into one number with nothing
// recording that they did. So the definition is now DECLARED by the
// source (`ByteSemantics`, the rule-6 shape beside `DeliveryLag`),
// carried on every ledger row under `ByteSemanticsKey`, and PARTITIONED
// in the rollup as `DeliveredBytesBySemantics`. The total is unchanged
// and the partition sums to it; what changed is that a mixed-semantics
// ingestion is now visible, and an undeclared one is named `unknown`
// rather than silently counted as either definition.
//
// **The two sinks carry different resolutions, on purpose.** The metric
// series are tagged `scope` + `class` only; the per-MEDIA attribution
// lives in the usage ledger's `Metadata`. `IMetricsSink` carries a
// per-metric distinct-tag-set ceiling (default 1 000, overflow routed
// to one `_overflow=true` series), and a media id is exactly the
// unbounded key an operator would blow that ceiling with. The usage
// ledger is row-shaped, partitioned by scope, and is what the rollups
// below read — so the per-`(mediaId, scope)` attribution the phase asks
// for is exact where it is queried and bounded where it is aggregated.
//
// **Off means off (GP 13).** `accountFor` returns `EgressUnmetered` — a
// nullary union case, which F# caches as a singleton — when neither
// sink is live, and `count` / `flush` on that value allocate nothing at
// all. A deployment with `MetricsEndpoint = NoMetricsEndpoint` and
// `UsageMetering = NoUsageMetering` pays two DI lookups per served
// response and not one byte of allocation. The gate is resolved
// per-response rather than cached in a process-wide `mutable` (the
// Phase 69l shape) because this seam is per-RESPONSE rather than
// per-request, two singleton lookups are already cheaper than the
// `IMediaLibrary` + `MediaLibraryOptions` lookups the same handler
// makes, and a cached static would make "metering rows appear only when
// composed" untestable in a shared test process.

// ─── Vocabulary ───────────────────────────────────────────────────────

/// Histogram of per-response ORIGIN egress. The histogram's `_sum` is
/// the deployment's total origin egress; its buckets characterise
/// whether that total is a few large downloads or many small segment
/// fetches, which is the question a CDN decision turns on.
[<Literal>]
let EgressBytesMetric = "toolup.media.egress.bytes"

/// Counter of accepted playback beacons, tagged by event.
[<Literal>]
let PlaybackEventsMetric = "toolup.media.playback.events"

/// Phase 742 — histogram of per-response DELIVERED egress, reconciled
/// from CDN access logs. Same bucket boundaries as `EgressBytesMetric`
/// so the two are readable on one axis.
[<Literal>]
let DeliveredEgressMetric = "toolup.media.egress.delivered.bytes"

/// Phase 742 — counter of access-log records that could not be
/// attributed, tagged by reason.
[<Literal>]
let DeliveredDroppedMetric = "toolup.media.delivered.dropped"

/// Usage-ledger `ResourceKind` for origin egress. `Quantity` is the
/// byte count, `Unit` is `bytes`, and `Metadata` carries the media id
/// and the response class.
[<Literal>]
let EgressBytesKind = "media.egress.bytes"

/// Usage-ledger `ResourceKind` for one accepted playback beacon.
/// `Quantity` is always `1`; the event token is in `Metadata`.
[<Literal>]
let PlaybackEventsKind = "media.playback.events"

/// Phase 742 — usage-ledger `ResourceKind` for DELIVERED egress: bytes
/// a CDN edge returned to a viewer, reconciled from the deployment's own
/// access logs. `Quantity` is the byte count, `Unit` is `bytes`, and
/// `Metadata` carries the media id, the response class, and the
/// `OutcomeKey` distinguishing an edge-served response from one that
/// passed through to this origin.
///
/// **A distinct kind, never a correction of `EgressBytesKind`.** The two
/// measure different things from different vantage points and are not
/// summable — see the delivered-egress note in the module header.
[<Literal>]
let DeliveredEgressKind = "media.egress.delivered.bytes"

[<Literal>]
let MediaIdKey = "mediaId"

[<Literal>]
let ClassKey = "class"

[<Literal>]
let EventKey = "event"

[<Literal>]
let SessionKey = "session"

[<Literal>]
let PercentKey = "percent"

/// The stored original, served by `/api/media/stream/{id}` or
/// `/media/signed/{id}`.
[<Literal>]
let ClassOriginal = "original"

[<Literal>]
let ClassManifest = "manifest"

[<Literal>]
let ClassSegment = "segment"

/// Derived stills and any other derived artefact — the same residual
/// class `MediaLibraryOptions.edgeCacheabilityForDerived` assigns.
[<Literal>]
let ClassPoster = "poster"

[<Literal>]
let EventStarted = "started"

[<Literal>]
let EventProgress = "progress"

[<Literal>]
let EventCompleted = "completed"

/// Phase 742 — `Metadata` key on a `DeliveredEgressKind` row recording
/// whether the edge served the response itself or passed it through to
/// this origin. The whole reconciliation turns on this distinction: a
/// pass-through response is ALSO counted by `EgressBytesKind`, so only
/// the edge-served subset is the gap Phase 473 could not see.
[<Literal>]
let OutcomeKey = "outcome"

/// The edge answered from its own cache. The origin never saw these
/// bytes, so they appear in no `EgressBytesKind` row.
[<Literal>]
let OutcomeEdge = "edge"

/// The edge went to the origin for this response. Counted here AND by
/// `EgressBytesKind`, from two vantage points with two byte definitions.
[<Literal>]
let OutcomeOrigin = "origin"

/// The deployment's parser could not classify the record's cache
/// disposition. Counted in delivered bytes, excluded from the hit-rate
/// numerator — an unknown is not evidence of a miss.
[<Literal>]
let OutcomeUnknown = "unknown"

/// Phase 743 — `Metadata` key on a `DeliveredEgressKind` row recording
/// what the source's byte count MEANS.
///
/// **Why a delivered byte count needs its meaning carried with it.**
/// Phase 742 established that the delivered and origin series are not
/// summable, partly because a CDN's byte field may count the whole HTTP
/// response including headers while the origin counts body bytes only.
/// That was written down as prose in three places, which left two things
/// a billing-grade read cannot do: meet the caveat at all (nothing forced
/// a reader of `DeliveredEgressBytes` past it), and separate two sources
/// with DIFFERENT definitions that had folded into one number. This key
/// is the caveat made structural — the source declares, the declaration
/// rides each row, and the rollup partitions on it.
///
/// It is deliberately NOT an input to the ingestion's dedup key: the same
/// logged response re-ingested after its source's declaration was
/// corrected must still be recognised as the same response, or the
/// correction would double-count everything it touched.
[<Literal>]
let ByteSemanticsKey = "byteSemantics"

/// The source counts the response BODY only — the same quantity Phase
/// 473's `OriginEgressBytes` counts, so the two are at least measuring
/// the same thing (they still must not be summed; a cache miss appears
/// in both).
[<Literal>]
let SemanticsBodyOnly = "body-only"

/// The source's byte field counts the whole HTTP response, response
/// headers included. Larger than the body by the header size on every
/// response, so it exceeds an origin-side count of the same bytes.
[<Literal>]
let SemanticsIncludesHeaders = "includes-headers"

/// The source did not state what its bytes mean — which is an ANSWER,
/// not an omission: a source cannot decline to declare, and a
/// deployment whose log documentation does not say is better served
/// saying so than guessing. Counted in `DeliveredEgressBytes`, and
/// excluded from every semantics-specific figure, so it can never be
/// silently billed as one definition or the other.
[<Literal>]
let SemanticsUnknown = "unknown"

/// The canonical rendering order for a byte-semantics partition, so two
/// readers holding the same rows render the same table. A token outside
/// this list sorts after it, alphabetically — the partition must remain
/// exhaustive (it has to sum to the delivered total) even for a value
/// this SDK does not know.
let byteSemanticsTokens: string list = [ SemanticsBodyOnly; SemanticsIncludesHeaders; SemanticsUnknown ]

/// Metric declarations, wired into `ServerApp.MetricRegistrations` by
/// `MediaCompose.run` so a media-composing deployment has both series
/// declared the moment it composes — the `AILatencyMetrics` /
/// `ActionLedgerMetrics` pattern. An unregistered series is dropped by
/// the sink, so declaring them at compose time is what makes the
/// emissions below reach an exporter.
let registrations: MetricRegistration list = [
    {
        Module = None
        Definition = {
            Name = EgressBytesMetric
            Kind = Histogram [ 65536.0; 262144.0; 1048576.0; 4194304.0; 16777216.0; 67108864.0; 268435456.0 ]
            Description =
                "Bytes written from the media ORIGIN per served response "
                + "(tags: scope + class=original|manifest|segment|poster). "
                + "Excludes anything a CDN edge served without reaching this origin."
            Unit = "bytes"
            Tags = [ "scope"; "class" ]
        }
    }
    {
        Module = None
        Definition = {
            Name = PlaybackEventsMetric
            Kind = Counter
            Description =
                "Accepted media playback beacons "
                + "(tags: scope + event=started|progress|completed)"
            Unit = "1"
            Tags = [ "scope"; "event" ]
        }
    }
    {
        Module = None
        Definition = {
            Name = DeliveredEgressMetric
            Kind = Histogram [ 65536.0; 262144.0; 1048576.0; 4194304.0; 16777216.0; 67108864.0; 268435456.0 ]
            Description =
                "Bytes an edge DELIVERED to a viewer per logged response, reconciled "
                + "from the deployment's CDN access logs (tags: scope + class + "
                + "outcome=edge|origin|unknown + semantics=body-only|includes-headers|"
                + "unknown). Not summable with the origin-egress series: outcome=origin "
                + "responses appear in both. PARTITION ON semantics before billing — the "
                + "tag says what the source's byte count means, and unknown is unbillable."
            Unit = "bytes"
            Tags = [ "scope"; "class"; "outcome"; "semantics" ]
        }
    }
    {
        Module = None
        Definition = {
            Name = DeliveredDroppedMetric
            Kind = Counter
            Description =
                "CDN access-log records the delivered-egress ingestion could not attribute "
                + "and DROPPED (tags: reason). Never an error — an unattributable record is "
                + "counted so the shortfall is visible, not raised."
            Unit = "1"
            Tags = [ "reason" ]
        }
    }
]

/// The response class for a derived blob.
///
/// Deliberately keyed on the SAME extension tests
/// `MediaLibraryOptions.edgeCacheabilityForDerived` uses, so a file
/// cannot be metered as one class and cached as another — the test pack
/// pins the two against one extension set rather than trusting this
/// comment.
let responseClassForDerived (relativePath: string) : string =
    let lower = relativePath.ToLowerInvariant()

    if lower.EndsWith ".m3u8" then
        ClassManifest
    elif lower.EndsWith ".ts" || lower.EndsWith ".m4s" || lower.EndsWith ".mp4" then
        ClassSegment
    else
        ClassPoster

// ─── Sink resolution ──────────────────────────────────────────────────

/// The registered `IMetricsSink`, or `None` when the deployment
/// composed none — which is what `NoOpMetricsSink` means. Matching the
/// no-op explicitly rather than trusting `null` is what makes the gate
/// answer "off" for the SDK default composition, where the service IS
/// registered and does nothing.
let private liveMetrics (ctx: HttpContext) : IMetricsSink option =
    match ctx.RequestServices with
    | null -> None
    | sp ->
        match sp.GetService typeof<IMetricsSink> with
        | :? NoOpMetricsSink -> None
        | :? IMetricsSink as sink -> Some sink
        | _ -> None

/// The registered `IUsageLog`, or `None` for the no-op. Same shape and
/// same reason as `liveMetrics`.
let private liveUsage (ctx: HttpContext) : IUsageLog option =
    match ctx.RequestServices with
    | null -> None
    | sp ->
        match sp.GetService typeof<IUsageLog> with
        | :? NoOpUsageLog -> None
        | :? IUsageLog as log -> Some log
        | _ -> None

// ─── 473.A — egress accounting ────────────────────────────────────────

/// Where a served byte count is attributed. Both coordinates are
/// derived server-side — `ScopeId` from the resolved `StorageScope` or
/// from a verified signature's payload, never from anything the caller
/// supplied (GP 4).
type EgressAttribution = {
    Media: MediaId
    ScopeId: string
    /// One of `ClassOriginal` / `ClassManifest` / `ClassSegment` /
    /// `ClassPoster`.
    Class: string
}

/// The usage-ledger row for `bytes` of origin egress. Pure and
/// explicitly parameterised on the id + timestamp so the row shape is
/// assertable without a clock or a host.
let egressRecord (attribution: EgressAttribution) (bytes: int64) (recordId: Guid) (timestamp: DateTime) : UsageRecord = {
    RecordId = recordId
    ScopeId = attribution.ScopeId
    ResourceKind = EgressBytesKind
    Quantity = decimal bytes
    Unit = "bytes"
    Origin = None
    Metadata = Map.ofList [ MediaIdKey, MediaId.value attribution.Media; ClassKey, attribution.Class ]
    Timestamp = timestamp
}

/// Phase 742 — the usage-ledger row for `bytes` of DELIVERED egress.
/// Pure, and explicitly parameterised on the id + timestamp for the same
/// reason `egressRecord` is: the row shape is assertable without a clock,
/// a host, or a CDN.
///
/// **`recordId` is the ingestion's whole idempotency story.** The
/// blob-backed `IUsageLog` merges incoming rows into the existing
/// per-`(scope, day)` rollup de-duped by `RecordId`, so a batch ingested
/// twice writes rows the ledger already holds and the second write
/// changes nothing. That is why this phase adds no dedup store of its
/// own: the substrate already has the property, and the part worth
/// owning is the DERIVATION of a stable id — see
/// `DeliveredEgress.dedupKey`.
///
/// Phase 743 — `semantics` is the source's `ByteSemanticsKey` declaration
/// travelling with the row it qualifies. It is a REQUIRED parameter and
/// there is no overload without one: a delivered byte count whose meaning
/// is unrecorded is exactly the row this phase exists to make
/// unconstructable, and `SemanticsUnknown` is how a source that cannot
/// say says so.
let deliveredRecord
    (attribution: EgressAttribution)
    (outcome: string)
    (semantics: string)
    (bytes: int64)
    (recordId: Guid)
    (timestamp: DateTime)
    : UsageRecord =
    {
        RecordId = recordId
        ScopeId = attribution.ScopeId
        ResourceKind = DeliveredEgressKind
        Quantity = decimal bytes
        Unit = "bytes"
        Origin = None
        Metadata =
            Map.ofList [
                MediaIdKey, MediaId.value attribution.Media
                ClassKey, attribution.Class
                OutcomeKey, outcome
                ByteSemanticsKey, semantics
            ]
        Timestamp = timestamp
    }

/// The live sinks plus the attribution for ONE metered response, and
/// the running count of what that response actually wrote.
///
/// A class rather than a `let mutable` inside the copy loop: the
/// emission happens in that loop's `finally` — so an aborted scrub
/// still meters what it actually cost — and the F# `task` builder
/// compiles a `finally` body into a closure, which cannot capture a
/// mutable local. An immutable reference to this object can.
type EgressMeter(metrics: IMetricsSink option, usage: IUsageLog option, attribution: EgressAttribution) =
    let mutable written = 0L

    member _.Attribution = attribution

    /// Bytes written so far on this response.
    member _.Written = written

    member _.Count(bytes: int) =
        if bytes > 0 then
            written <- written + int64 bytes

    /// Emit once, at the end of the response. Best-effort in both
    /// directions: a sink that throws must not turn a served body into
    /// a failed request.
    member _.Emit() =
        if written > 0L then
            match metrics with
            | Some sink ->
                try
                    sink.Record(
                        EgressBytesMetric,
                        float written,
                        Map.ofList [ "scope", attribution.ScopeId; "class", attribution.Class ]
                    )
                with _ ->
                    ()
            | None -> ()

            match usage with
            | Some log ->
                try
                    log.Record(egressRecord attribution written (Guid.NewGuid()) DateTime.UtcNow)
                    |> Async.Start
                with _ ->
                    ()
            | None -> ()

/// What a response about to be served will meter. `EgressUnmetered` is
/// a nullary case, so it is a cached singleton and the OFF path
/// allocates nothing (GP 13).
type EgressAccount =
    | EgressUnmetered
    | EgressMetered of EgressMeter

/// Resolve the account for a response. Returns `EgressUnmetered`
/// without allocating when neither sink is live.
let accountFor (ctx: HttpContext) (media: MediaId) (scopeId: string) (responseClass: string) : EgressAccount =
    match liveMetrics ctx, liveUsage ctx with
    | None, None -> EgressUnmetered
    | metrics, usage ->
        EgressMetered(
            EgressMeter(
                metrics,
                usage,
                {
                    Media = media
                    ScopeId = scopeId
                    Class = responseClass
                }
            )
        )

/// Add bytes actually written. A tag test and nothing else when the
/// account is unmetered.
let count (account: EgressAccount) (bytes: int) : unit =
    match account with
    | EgressUnmetered -> ()
    | EgressMetered meter -> meter.Count bytes

/// Emit the response's total, once. A no-op when unmetered.
let flush (account: EgressAccount) : unit =
    match account with
    | EgressUnmetered -> ()
    | EgressMetered meter -> meter.Emit()

// ─── 473.B — playback beacons ─────────────────────────────────────────

/// What a player reports. `Progress` carries whole percent, 0–100
/// inclusive; anything outside that is not a `Progress` this module
/// will construct.
type PlaybackEvent =
    | Started
    | Progress of percent: int
    | Completed

module PlaybackEvent =
    /// Stable wire token. The same vocabulary appears as the metric's
    /// `event` tag and as the ledger row's `event` metadata, so a
    /// dashboard reading either sees one set of names.
    let token =
        function
        | Started -> EventStarted
        | Progress _ -> EventProgress
        | Completed -> EventCompleted

/// A validated beacon. `Session` is already the derived correlator —
/// the raw client value never reaches this type, and therefore never
/// reaches the ledger.
type PlaybackBeacon = {
    Media: MediaId
    Event: PlaybackEvent
    Session: string
}

/// Bodies larger than this are dropped unread past the cap. A beacon is
/// four short fields; anything bigger is not one.
[<Literal>]
let MaxBeaconBytes = 2048

/// Cap on the raw client session id. Long enough for a UUID or an
/// opaque player handle, short enough that the field cannot be used as
/// a smuggling channel into the ledger.
[<Literal>]
let MaxSessionChars = 200

/// Derive the stored session correlator.
///
/// **This is what makes the beacon anonymous-safe, and it is the whole
/// reason the raw value is not stored.** The client's session id is
/// hashed together with the scope and the media id, so the stored
/// correlator is:
///
///   * stable within one `(scope, media)` — which is exactly, and
///     only, what counting unique sessions and completion rates needs;
///   * useless as a cross-media or cross-scope tracking key, because
///     the same viewer watching a second item produces an unrelated
///     value;
///   * not reversible to whatever the client actually minted, so a
///     player that (wrongly) puts a user identifier in the field does
///     not thereby put one in the usage ledger.
///
/// The inputs are length-prefixed rather than delimiter-joined so no
/// two distinct triples can hash to the same string.
let sessionCorrelator (scopeId: string) (mediaId: string) (rawSession: string) : string =
    let material =
        sprintf "%d:%s|%d:%s|%d:%s" scopeId.Length scopeId mediaId.Length mediaId rawSession.Length rawSession

    let hash = SHA256.HashData(Encoding.UTF8.GetBytes material)
    Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant()

/// Parse a beacon body into `(rawMediaId, event, rawSession)`.
///
/// `None` for ANYTHING that is not a well-formed beacon — unparseable
/// JSON, a non-object root, a missing or empty field, an unknown event
/// token, a `Progress` percent outside 0–100, an over-long session.
/// There is no error channel on purpose: the endpoint's contract is
/// that a malformed beacon is dropped, and a parser that could throw
/// would be the one way this route reaches a `500`.
///
/// Hand-parsed with `JsonDocument` rather than deserialised into an F#
/// record: the wire shape here is authored by third-party players, so
/// tolerating an extra field, a numeric string, or a missing optional
/// is the requirement — not something to fight a typed deserialiser
/// over.
let parseBeacon (body: string) : (string * PlaybackEvent * string) option =
    if String.IsNullOrWhiteSpace body then
        None
    else
        try
            use doc = JsonDocument.Parse body

            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                let stringField (name: string) =
                    match doc.RootElement.TryGetProperty name with
                    | true, v when v.ValueKind = JsonValueKind.String ->
                        match v.GetString() with
                        | null -> None
                        | s when String.IsNullOrWhiteSpace s -> None
                        | s -> Some(s.Trim())
                    | _ -> None

                let percentField () =
                    match doc.RootElement.TryGetProperty PercentKey with
                    | true, v when v.ValueKind = JsonValueKind.Number ->
                        match v.TryGetDouble() with
                        | true, d when Double.IsFinite d -> Some(int (Math.Round d))
                        | _ -> None
                    | _ -> None

                match stringField MediaIdKey, stringField EventKey, stringField SessionKey with
                | Some media, Some rawEvent, Some session when session.Length <= MaxSessionChars ->
                    let event =
                        match rawEvent.ToLowerInvariant() with
                        | EventStarted -> Some Started
                        | EventCompleted -> Some Completed
                        | EventProgress ->
                            match percentField () with
                            | Some p when p >= 0 && p <= 100 -> Some(Progress p)
                            | _ -> None
                        | _ -> None

                    event |> Option.map (fun e -> media, e, session)
                | _ -> None
        with _ ->
            None

/// The usage-ledger row for one accepted beacon. Pure, for the same
/// reason `egressRecord` is.
let beaconRecord (scopeId: string) (beacon: PlaybackBeacon) (recordId: Guid) (timestamp: DateTime) : UsageRecord =
    let metadata =
        Map.ofList [
            MediaIdKey, MediaId.value beacon.Media
            EventKey, PlaybackEvent.token beacon.Event
            SessionKey, beacon.Session
        ]

    {
        RecordId = recordId
        ScopeId = scopeId
        ResourceKind = PlaybackEventsKind
        Quantity = 1m
        Unit = "events"
        Origin = None
        Metadata =
            match beacon.Event with
            | Progress p -> metadata |> Map.add PercentKey (string p)
            | _ -> metadata
        Timestamp = timestamp
    }

// ─── The beacon's own rate limit ──────────────────────────────────────

/// Per-partition fixed window for the beacon route. Expressed as the
/// SDK's own `RateLimitPolicy` data, and applied over the SAME
/// partition key the global limiter derives
/// (`RateLimitPolicy.partitionFor` — `token:` / `team:` / `user:` /
/// `ip:`), so a beacon flood is attributed to exactly the identity the
/// rest of the deployment would attribute it to.
///
/// Sizing: a player emits one `Started`, one `Completed`, and a
/// progress ping every few tens of seconds, so a single long viewing
/// is on the order of a hundred beacons an hour. 300/minute leaves an
/// order of magnitude of headroom for a shared `team:` partition while
/// still bounding a flood. `QueueLimit = 0` — a refused beacon is
/// DROPPED, never queued and never rejected to the caller.
let beaconRateLimit: RateLimitPolicy = {
    PermitLimit = 300
    WindowSeconds = 60
    QueueLimit = 0
}

/// A fixed-window admission counter over partition keys.
///
/// **Why this is not the ASP.NET Core limiter.** That limiter's job is
/// to REJECT a request with `429`, and a beacon must never be rejected
/// — a player that gets an error status for telemetry is a player whose
/// vendor files a bug about the video. What is wanted here is an
/// admission decision whose losing branch is "drop the row and answer
/// `204` anyway", which is a different verb. The partition key, and
/// therefore the identity being limited, is shared with the global
/// limiter; the disposition is not.
///
/// A class rather than module state so a test constructs its own and
/// two tests cannot interfere.
type BeaconRateLimiter(policy: RateLimitPolicy) =
    // Bounded so a hostile spread of partition keys cannot grow this
    // without limit. Clearing wholesale rather than evicting LRU is
    // deliberate: the loss is at most one window of beacon fidelity,
    // and an eviction policy here would be more machinery than the
    // thing it protects.
    let maxPartitions = 10_000
    let windows = ConcurrentDictionary<string, struct (int64 * int)>()

    let windowIndex (nowUtc: DateTime) =
        let ticksPerWindow = int64 (max 1 policy.WindowSeconds) * TimeSpan.TicksPerSecond
        nowUtc.Ticks / ticksPerWindow

    member _.Policy = policy

    /// Tracked partition count — for the test pack's bound assertion.
    member _.TrackedPartitions = windows.Count

    /// `true` when this beacon is admitted, `false` when the partition
    /// has spent its window.
    member _.Admit(nowUtc: DateTime, partition: string) : bool =
        if windows.Count >= maxPartitions then
            windows.Clear()

        let current = windowIndex nowUtc

        let struct (_, count) =
            windows.AddOrUpdate(
                partition,
                (fun _ -> struct (current, 1)),
                (fun _ (struct (window, count)) ->
                    if window = current then
                        struct (window, count + 1)
                    else
                        struct (current, 1))
            )

        count <= policy.PermitLimit

/// The limiter the mounted route uses.
let beaconLimiter = BeaconRateLimiter beaconRateLimit

// ─── The endpoint ─────────────────────────────────────────────────────

let private storageScopeOf (ctx: HttpContext) : StorageScope option =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> Some s
    | _ -> None

let private subjectOf (ctx: HttpContext) : Subject =
    match ctx.Items.TryGetValue "ToolUp.Subject" with
    | true, (:? Subject as s) -> s
    | _ -> AnonymousSession ""

let private remoteIp (ctx: HttpContext) =
    match ctx.Connection.RemoteIpAddress with
    | null -> "unknown"
    | ip -> ip.ToString()

/// The partition this request's beacons are counted against.
let beaconPartition (ctx: HttpContext) : string =
    "beacon:" + RateLimitPolicy.partitionFor (remoteIp ctx) (subjectOf ctx)

/// Read at most `MaxBeaconBytes` of request body. `None` when the body
/// is absent or over the cap — the cap is checked by reading one byte
/// past it rather than by trusting `Content-Length`, which a client
/// controls.
let private readBoundedBody (ctx: HttpContext) : Task<string option> = task {
    let contentLength = ctx.Request.ContentLength

    if contentLength.HasValue && contentLength.Value > int64 MaxBeaconBytes then
        return None
    else
        let buffer = Array.zeroCreate<byte> (MaxBeaconBytes + 1)
        let mutable total = 0
        let mutable eof = false

        while not eof && total < buffer.Length do
            let! read =
                ctx.Request.Body.ReadAsync(Memory<byte>(buffer, total, buffer.Length - total), ctx.RequestAborted)

            if read <= 0 then eof <- true else total <- total + read

        if total > MaxBeaconBytes then
            return None
        else
            return Some(Encoding.UTF8.GetString(buffer, 0, total))
}

/// Resolve the scope this beacon is attributed to, admitting on exactly
/// the two credentials the media bytes themselves are reachable by —
/// the same decision `HlsKeyDelivery.decideAccess` makes for the key
/// endpoint, called rather than re-derived so the two cannot drift.
///
/// The media id the token is checked against comes from the BEACON
/// BODY, so a token minted for one item cannot report plays against
/// another.
let private resolveBeaconScope
    (ctx: HttpContext)
    (verification: Result<SignedUrl.MediaSignedPayload, SignedUrlError> option)
    (bodyMediaId: string)
    : (string * string) option =
    let container = storageScopeOf ctx |> Option.map _.Container

    match HlsKeyDelivery.decideAccess container verification bodyMediaId with
    | HlsKeyDelivery.KeyAccessGranted _ ->
        match verification with
        | Some(Ok payload) -> Some(payload.ScopeId, payload.Container)
        | _ -> storageScopeOf ctx |> Option.map (fun s -> s.ScopeId, s.Container)
    | _ -> None

/// Emit one accepted beacon into whichever sinks are live. Best-effort;
/// nothing here can fail the response.
let recordBeacon (ctx: HttpContext) (scopeId: string) (beacon: PlaybackBeacon) : unit =
    match liveMetrics ctx with
    | Some sink ->
        try
            sink.Increment(
                PlaybackEventsMetric,
                Map.ofList [ "scope", scopeId; "event", PlaybackEvent.token beacon.Event ]
            )
        with _ ->
            ()
    | None -> ()

    match liveUsage ctx with
    | Some log ->
        try
            log.Record(beaconRecord scopeId beacon (Guid.NewGuid()) DateTime.UtcNow)
            |> Async.Start
        with _ ->
            ()
    | None -> ()

/// `POST /api/media/beacon` — the playback beacon endpoint.
///
/// **Every outcome is `204 No Content`.** Accepted, malformed, rate-
/// limited, unauthenticated and forbidden are indistinguishable to the
/// caller, which is two properties at once: a beacon can never surface
/// an error to a player (the phase's hard requirement — a `5xx` on
/// telemetry reads to a viewer as a broken video), and the endpoint is
/// not an oracle telling an unauthenticated prober which media ids
/// exist in which scope. What the outcomes DO differ in is whether a
/// row reaches the usage ledger, which is where the test matrix reads
/// them.
///
/// The whole body is wrapped so that even an unforeseen throw lands as
/// a `204`.
let beaconHandler: HttpHandler =
    POST
    >=> route MediaApi.beaconRoute
    >=> fun (_: HttpFunc) (ctx: HttpContext) -> task {
        try
            match! readBoundedBody ctx with
            | None -> ()
            | Some body ->
                match parseBeacon body with
                | None -> ()
                | Some(rawMediaId, event, rawSession) ->
                    if beaconLimiter.Admit(DateTime.UtcNow, beaconPartition ctx) then
                        let token =
                            match ctx.Request.Query.TryGetValue "token" with
                            | true, v -> v.ToString()
                            | _ -> ""

                        let! verification =
                            match ctx.RequestServices with
                            | null -> Task.FromResult None
                            | sp ->
                                match sp.GetService typeof<SignedUrl.MediaUrlSigner> with
                                | :? SignedUrl.MediaUrlSigner as signer when not (String.IsNullOrEmpty token) -> task {
                                    let! r = signer.VerifyAsync(token, DateTimeOffset.UtcNow) |> Async.StartAsTask
                                    return Some r
                                  }
                                | _ -> Task.FromResult None

                        match resolveBeaconScope ctx verification rawMediaId with
                        | None -> ()
                        | Some(scopeId, _container) ->
                            recordBeacon ctx scopeId {
                                Media = MediaId rawMediaId
                                Event = event
                                Session = sessionCorrelator scopeId rawMediaId rawSession
                            }
        with _ ->
            ()

        ctx.SetStatusCode 204
        return Some ctx
    }

// ─── 473.C — the aggregation surface ──────────────────────────────────

/// One `(media, scope, day)` rollup.
///
/// `OriginEgressBytes` is named for what it is: see the module header
/// on why a CDN-fronted deployment must not read it as delivered bytes.
type PlaybackRollup = {
    MediaId: string
    ScopeId: string
    /// UTC date bucket, `yyyy-MM-dd` — the same key shape
    /// `UsageGrouping.ByDay` produces, so the two read alike.
    Day: string
    /// `Started` beacons.
    Plays: int
    /// Distinct session correlators seen on any beacon for this bucket.
    UniqueSessions: int
    /// `Completed` beacons.
    Completions: int
    /// `Completions / Plays`, or `0.0` when nothing started.
    CompletionRate: float
    OriginEgressBytes: int64
    /// Phase 742 — every byte an edge returned to a viewer for this
    /// bucket, as reconciled from the deployment's CDN access logs. `0`
    /// on a deployment that ingests none, which is byte-identical to
    /// Phase 473.
    ///
    /// **Not comparable term-by-term with `OriginEgressBytes`, and not
    /// summable with it.** A cache MISS is counted in both — once by the
    /// origin as it wrote the body, once by the edge as it relayed it —
    /// and the two counts differ even for that one response, because a
    /// CDN log's byte field is typically the whole HTTP response
    /// including headers while `OriginEgressBytes` is body bytes only.
    /// The delivered figure is the billable one; the origin figure is
    /// the one this process can prove.
    DeliveredEgressBytes: int64
    /// The subset of `DeliveredEgressBytes` the edge served from its own
    /// cache — the bytes Phase 473 structurally could not see, because
    /// they never reached this origin. Records whose cache disposition
    /// the deployment's parser could not classify are excluded: an
    /// unknown is not evidence of a hit.
    EdgeServedBytes: int64
    /// `EdgeServedBytes / DeliveredEgressBytes`, or `0.0` when nothing
    /// was delivered. A BY-BYTES hit rate, not by request: the question
    /// the origin-vs-delivered gap poses is about volume, and one large
    /// origin pull outweighs a hundred small edge hits in every way that
    /// matters to a bill.
    EdgeHitRateByBytes: float
    /// Phase 743 — `DeliveredEgressBytes` PARTITIONED by what each
    /// source declared its bytes to mean: `(ByteSemanticsKey token,
    /// bytes)` pairs in `byteSemanticsTokens` order, omitting any token
    /// with no bytes in this bucket.
    ///
    /// **A partition, never an average and never a fold.** The entries
    /// SUM to `DeliveredEgressBytes` exactly — including the
    /// `SemanticsUnknown` share and including a token this SDK does not
    /// recognise — so a mixed-semantics ingestion is VISIBLE here rather
    /// than silently merged into one number whose meaning depends on
    /// which source happened to contribute more of it.
    ///
    /// **Bill on the partition, not on the total.** A billing-grade read
    /// takes the entry whose definition it has priced and refuses the
    /// rest: `SemanticsUnknown` bytes are real bytes that were really
    /// delivered, but nothing here knows whether they include response
    /// headers, so charging for them charges for an unstated quantity.
    /// `deliveredBytesWithKnownSemantics` is that read; `bytesForSemantics`
    /// is the one for a deployment that prices exactly one definition.
    ///
    /// Empty on a deployment that ingests no delivered rows. Rows written
    /// before this field existed carry no declaration and read as
    /// `SemanticsUnknown`, which is the honest reading of a row whose
    /// source never stated anything.
    DeliveredBytesBySemantics: (string * int64) list
}

module PlaybackRollup =

    /// The `ByteSemanticsKey` a delivered row declares, or
    /// `SemanticsUnknown` when it declares none.
    ///
    /// A row written before Phase 743 carries no key at all, and reading
    /// it as unknown rather than assuming one of the two definitions is
    /// the whole point: the earlier row's source may well have been
    /// either, and inferring which would fabricate the fact this field
    /// exists to record.
    let semanticsOf (record: UsageRecord) : string =
        match record.Metadata.TryFind ByteSemanticsKey with
        | Some token when not (String.IsNullOrWhiteSpace token) -> token
        | _ -> SemanticsUnknown

    /// Delivered bytes in one bucket that were declared under exactly
    /// `token`. `0L` when nothing in the bucket carried that declaration.
    let bytesForSemantics (token: string) (rollup: PlaybackRollup) : int64 =
        rollup.DeliveredBytesBySemantics
        |> List.tryFind (fst >> (=) token)
        |> Option.map snd
        |> Option.defaultValue 0L

    /// Delivered bytes whose meaning the source actually stated — the
    /// total less the `SemanticsUnknown` share.
    ///
    /// **This is the billable read, and it is deliberately not called
    /// that.** What it knows is that these bytes have a declared
    /// definition, not that a given deployment's contract prices that
    /// definition; a deployment billing on body bytes alone still wants
    /// `bytesForSemantics SemanticsBodyOnly`. What it rules out is the
    /// one thing no bill should rest on: a quantity nobody defined.
    let deliveredBytesWithKnownSemantics (rollup: PlaybackRollup) : int64 =
        rollup.DeliveredBytesBySemantics
        |> List.sumBy (fun (token, bytes) -> if token = SemanticsUnknown then 0L else bytes)

    /// Fold usage records into per-`(media, scope, day)` rollups.
    ///
    /// **This is the whole of 473.C, and the point is what it is NOT.**
    /// There is no new API, no new store, no dashboard and no module
    /// (GP 9). The input is exactly what the shipped
    /// `IUsageQueryApi.Query` already returns for a scope, so a
    /// deployment reads playback numbers through the read path its
    /// usage dashboard already uses and folds them here. Records of
    /// other kinds, and rows with no media id, are ignored — so passing
    /// a whole scope's ledger is the expected call.
    ///
    /// Pure and total, and the output is ordered `(Day, MediaId,
    /// ScopeId)` so two callers holding the same records render the
    /// same table.
    let ofUsageRecords (records: UsageRecord list) : PlaybackRollup list =
        let relevant =
            records
            |> List.choose (fun r ->
                if
                    r.ResourceKind <> EgressBytesKind
                    && r.ResourceKind <> PlaybackEventsKind
                    && r.ResourceKind <> DeliveredEgressKind
                then
                    None
                else
                    match r.Metadata.TryFind MediaIdKey with
                    | Some media when not (String.IsNullOrWhiteSpace media) ->
                        Some((r.Timestamp.ToUniversalTime().ToString "yyyy-MM-dd", media, r.ScopeId), r)
                    | _ -> None)

        relevant
        |> List.groupBy fst
        |> List.map (fun ((day, media, scope), rows) ->
            let rs = rows |> List.map snd

            let countOf token =
                rs
                |> List.filter (fun r ->
                    r.ResourceKind = PlaybackEventsKind && r.Metadata.TryFind EventKey = Some token)
                |> List.length

            let plays = countOf EventStarted
            let completions = countOf EventCompleted

            let uniqueSessions =
                rs
                |> List.choose (fun r ->
                    if r.ResourceKind = PlaybackEventsKind then
                        r.Metadata.TryFind SessionKey
                    else
                        None)
                |> List.distinct
                |> List.length

            let egress =
                rs
                |> List.sumBy (fun r ->
                    if r.ResourceKind = EgressBytesKind then
                        int64 r.Quantity
                    else
                        0L)

            let deliveredBytesWhere (predicate: UsageRecord -> bool) =
                rs
                |> List.sumBy (fun r ->
                    if r.ResourceKind = DeliveredEgressKind && predicate r then
                        int64 r.Quantity
                    else
                        0L)

            let delivered = deliveredBytesWhere (fun _ -> true)

            let edgeServed =
                deliveredBytesWhere (fun r -> r.Metadata.TryFind OutcomeKey = Some OutcomeEdge)

            // Phase 743 — the partition. Grouped by the token the rows
            // actually carry rather than by the tokens this SDK knows, so
            // a value from a newer or a hand-written source still appears
            // and the partition still sums to `delivered`. A partition
            // that quietly dropped what it did not recognise would report
            // a bill smaller than the bytes, which is the one direction an
            // error here must never take.
            let bySemantics =
                rs
                |> List.filter (fun r -> r.ResourceKind = DeliveredEgressKind)
                |> List.groupBy semanticsOf
                |> List.map (fun (token, rows) -> token, rows |> List.sumBy (fun r -> int64 r.Quantity))
                |> List.filter (fun (_, bytes) -> bytes <> 0L)
                |> List.sortBy (fun (token, _) ->
                    let index =
                        byteSemanticsTokens
                        |> List.tryFindIndex ((=) token)
                        |> Option.defaultValue byteSemanticsTokens.Length

                    index, token)

            {
                MediaId = media
                ScopeId = scope
                Day = day
                Plays = plays
                UniqueSessions = uniqueSessions
                Completions = completions
                CompletionRate = if plays = 0 then 0.0 else float completions / float plays
                OriginEgressBytes = egress
                DeliveredEgressBytes = delivered
                EdgeServedBytes = edgeServed
                EdgeHitRateByBytes =
                    if delivered = 0L then
                        0.0
                    else
                        float edgeServed / float delivered
                DeliveredBytesBySemantics = bySemantics
            })
        |> List.sortBy (fun r -> r.Day, r.MediaId, r.ScopeId)
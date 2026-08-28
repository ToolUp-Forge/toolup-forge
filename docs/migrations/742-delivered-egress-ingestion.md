# Phase 742 — delivered-egress reconciliation (CDN log ingestion)

**What changes for you:** nothing, unless you opt in. This phase is additive and off by default.

A deployment that does not ingest CDN access logs behaves byte-for-byte as it did before, and every
number it already reads means exactly what it meant. The one edit some consumers need is a
one-line record-construction fix — see [Breaking-ish](#breaking-ish-one-widened-record).

## Why you might want it

The media library counts **origin** egress: bytes that left your process. Once a CDN is in front,
an edge cache hit never reaches your origin, so origin egress understates what was actually
delivered by exactly the edge hit rate — and no origin-side signal tells you how large that gap is.

If you meter, bill, or report on egress, delivered bytes are the number you actually want. This
phase lets you feed your edge's own access logs back in, so delivered bytes and an edge hit rate sit
beside origin bytes in the rollup you already read.

## Adopting it

### 1. Reference the adapter (optional but recommended)

```xml
<PackageReference Include="ToolUp.Hosts.DeliveredEgress" Version="..." />
```

It carries field-mapped parsers for delimited and newline-delimited-JSON logs, plus callback
implementations of the two seams. Skip it if you would rather build `DeliveredRecord` values
yourself — the ingestor takes them from anywhere.

### 2. Describe your log's field names

Your CDN's field set is **your** configuration, not a format: both major platforms let you choose
which fields to emit, so there is no preset that would be right for you. Name what you selected:

```fsharp skip=fragment
open ToolUp.Hosts.DeliveredEgress.FieldMappedParser

let map = {
    FieldMap.required "<path-field>" "<bytes-field>" "<status-field>" "<timestamp-field>" with
        QueryField = Some "<query-field>"       // omit if your path field includes the query
        OutcomeField = Some "<cache-status-field>"
        RequestIdField = Some "<request-id-field>"  // name it if you have one — see below
        TimestampFormats = [ "yyyy-MM-dd HH:mm:ss" ]
        EdgeOutcomes = Set.ofList [ (* values meaning "served from cache" *) ]
        OriginOutcomes = Set.ofList [ (* values meaning "went to origin" *) ]
}
```

**Name `RequestIdField` if your delivery has one.** Every major CDN emits a per-request identifier.
With it, re-ingestion dedup is exact. Without it, the fallback key is derived from the record's
content, which collapses two byte-identical responses logged in the same second — an *undercount*,
bounded by that collision rate.

**Enumerate both outcome sets.** A cache-status value in neither reads as *unknown*, which is
counted as delivered but excluded from the hit-rate numerator. That is deliberate — an unenumerated
vocabulary degrades to "I do not know" rather than silently deflating your hit rate — but it does
mean an incomplete `EdgeOutcomes` shows up as a hit rate that looks too low.

### 3. Declare how the ambient-scope routes map to scopes

The media id is in every URL. The scope is only in `/media/signed/{id}?token=`, where it rides
inside the token (whose signature is verified before it is believed). `/api/media/stream/{id}` and
`/api/media/hls/{id}/{file}` resolved their scope from request context that no access log captured,
and `IMediaLibrary` cannot recover it — every member takes the scope as a parameter so a cross-scope
read cannot be expressed at all.

If you serve HLS through a CDN — which is the common case, since HLS segments are the response class
most worth caching — **most of your delivered bytes are on the ambient route**, so this step is not
optional in practice:

```fsharp skip=fragment
open ToolUp.Hosts.DeliveredEgress.CallbackLogSource

let scopeResolver = CallbackScopeResolver("catalogue", fun mediaId -> myCatalogue.ScopeOf mediaId)
```

Without it those records are dropped and counted as `scope-unresolved` — visible in the ingest
outcome and on `toolup.media.delivered.dropped`, never raised as an error.

### 4. Wire a source and a job

```fsharp skip=fragment
open System
open ToolUp.MediaLibrary.DeliveredEgress

let source =
    CallbackLogSource(
        "my-edge",
        TimeSpan.FromHours 1.0, // YOUR delivery's lag — see below
        fun () -> async {
            let! files = myLogStore.FetchNewFiles()

            return
                Ok(
                    files
                    |> List.map (fun (name, content) -> {
                        BatchId = name
                        Records = (parseDelimited map '\t' None content).Records
                    })
                )
        }
    )

let ingestor = DeliveredEgressIngestor(usageLog, Some metricsSink, Some mediaUrlSigner, Some scopeResolver)

DeliveredEgressJob.hourly ingestor source // hand to your scheduled-job composition
```

Reaching your log store — the credential, the listing, the download — is entirely your callback's
job. Nothing in this phase opens a socket.

**Set `deliveryLag` honestly.** It is a declaration, not a setting: one platform pushes batches
sub-minute; the other typically delivers within an hour of the events and *can delay entries by up
to 24 hours*. Polling faster than your delivery's lag costs list calls and returns nothing new, and
a rollup read sooner than the lag is **early**, not incomplete.

**Your source may be forgetful.** Ingestion is idempotent by a deterministic per-record key, so
re-offering a batch you have already ingested changes no number. "Every object written in the last
day" is a perfectly correct implementation — which matters, because redelivery is the normal
operating mode for both platforms (one retries failed pushes; the other spreads a period across
several files and recommends combining them all).

A push topology works too: call `ingestor.IngestBatch` directly from an object-created trigger and
compose no job at all.

## Reading the result

`PlaybackRollup` gains `DeliveredEgressBytes`, `EdgeServedBytes` and `EdgeHitRateByBytes`.
`OriginEgressBytes` is unchanged and still means exactly what it meant.

**Do not sum the delivered and origin figures, and do not expect them to reconcile.** A cache miss
is counted in both — once by the origin writing the body, once by the edge relaying it — and even
for that one response the counts differ, because a CDN's byte field is typically the whole response
*including headers* while the origin count is body bytes only. The delivered figure is the billable
one; the origin figure is the one your process can prove. If you need a single number for a bill,
use `DeliveredEgressBytes`.

New series: ledger kind `media.egress.delivered.bytes`; metrics
`toolup.media.egress.delivered.bytes` (histogram, tagged `scope` / `class` / `outcome`) and
`toolup.media.delivered.dropped` (counter, tagged `reason`). Both are declared automatically when
you compose the media library, so no metric registration is needed. **Watch the drop counter** — a
drop rate that climbs is how a changed field mapping, a new route, or a catalogue gap announces
itself.

## Breaking-ish: one widened record

`PlaybackRollup` gained three fields. If you construct one with a **full record literal** — which
you would only do in a test or a fixture, since the SDK produces them —  that construction now fails
to compile (FS0764):

```fsharp skip=fragment
// Before
{ MediaId = "m"; ScopeId = "s"; Day = "2026-08-28"
  Plays = 1; UniqueSessions = 1; Completions = 1
  CompletionRate = 1.0; OriginEgressBytes = 100L }

// After — add the three fields
{ MediaId = "m"; ScopeId = "s"; Day = "2026-08-28"
  Plays = 1; UniqueSessions = 1; Completions = 1
  CompletionRate = 1.0; OriginEgressBytes = 100L
  DeliveredEgressBytes = 0L; EdgeServedBytes = 0L; EdgeHitRateByBytes = 0.0 }
```

Zeroes are the correct values for a deployment that ingests nothing, and are what the fold produces
for it.

Nothing else changes. `PlaybackRollup.ofUsageRecords` takes the same input and returns the same
values for every pre-742 ledger; `SignedUrl.verify`, every serving route, and every existing metric
are untouched.

## Rollback

Stop composing the job (or stop calling `IngestBatch`). The delivered rows already in the ledger stay
— they are a distinct resource kind and are simply not added to. Downgrading the package restores the
narrower `PlaybackRollup`, at the cost of the three-field literal edit in reverse.

## What this phase does not give you

- **No estimate.** If you ingest nothing, no delivered figure is inferred from `s-maxage` or anything
  else. A number derived from a TTL would be a guess presented as a measurement.
- **No backfill.** If your log pipeline drops a period, the delivered series is short for that period
  permanently — one platform states outright that it cannot re-push historical data.
- **No network access.** Reaching your CDN or its log store is your code's job throughout.

## See also

- [`docs/platform/edge-serving.md`](../platform/edge-serving.md) — the delivered-vs-origin section.
- [`src/Hosts/DeliveredEgress/README.md`](../../src/Hosts/DeliveredEgress/README.md) — the adapter,
  with worked field maps.
- [`docs/migrations/473-playback-delivery-telemetry.md`](473-playback-delivery-telemetry.md) — the
  origin-egress accounting this reconciles against.
- [`docs/migrations/472-edge-cache-seam.md`](472-edge-cache-seam.md) — the edge seams that created
  the gap.

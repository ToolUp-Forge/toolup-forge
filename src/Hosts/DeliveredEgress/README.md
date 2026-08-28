# ToolUp.Hosts.DeliveredEgress

CDN access-log ingestion for `ToolUp.MediaLibrary` — the adapter half of
**delivered-egress reconciliation**.

## The problem this closes

`ToolUp.MediaLibrary` counts **origin** egress: the bytes that left *this*
process. Put a CDN in front of it and that number stops being the whole
story, because an edge cache hit never reaches the origin at all. Origin
egress then understates delivered egress by exactly the edge hit rate, and
the size of that gap is not observable from inside the deployment.

For metered or monetised media, delivered bytes are the billable number.
This package is how a deployment feeds its edge's own account of what it
served back into the media rollup, so `DeliveredEgressBytes`,
`EdgeServedBytes` and `EdgeHitRateByBytes` sit beside `OriginEgressBytes`
in the numbers it already reads.

## What is here, and what deliberately is not

Two things:

- **`FieldMappedParser`** — parsers for the two containers access logs
  actually arrive in: delimited text (with a `#Fields:` header or a
  caller-supplied column list) and newline-delimited JSON. Field names,
  timestamp shapes and cache-status vocabularies are supplied as a
  `FieldMap`.
- **`CallbackLogSource` / `CallbackScopeResolver`** — the two
  delivered-egress seams over plain callbacks.

There is **no vendor-specific code path and no cloud SDK dependency**
(GP 1). That is a finding rather than a preference. A survey of the two
major edge classes found that a single fixed parser is not merely
undesirable but ill-defined, for three reasons that all reduce to
configuration:

- **The field set is chosen per delivery.** Both platforms let the
  operator name exactly which fields to emit, so two deployments on the
  *same* edge produce different records. There is no schema to bind to.
- **The container is chosen per delivery** — delimited text with a
  configurable delimiter, or newline-delimited JSON, among others.
- **Names and vocabularies differ.** One reports bytes sent as the whole
  response *including headers*; the other reports bytes returned to the
  client. Cache dispositions read `Hit` / `RefreshHit` / `Miss` on one and
  `hit` / `miss` / `expired` / `bypass` / `dynamic` on the other.

Once the field names are a parameter, nothing vendor-shaped is left. So
the vendor-specific part of this integration is a `FieldMap` **value you
write**, not a package you pick.

## Building a `FieldMap`

Name the fields your delivery emits, then the vocabulary its cache-status
field uses:

```fsharp skip=fragment
open System
open ToolUp.Hosts.DeliveredEgress.FieldMappedParser

let map = {
    FieldMap.required "cs-uri-stem" "sc-bytes" "sc-status" "date" with
        // This delivery splits the timestamp across two columns and
        // logs the query string separately from the path — the signed
        // URL token lives in the query, so naming it is what makes
        // signed-URL requests attributable without a scope resolver.
        TimestampSecondField = Some "time"
        QueryField = Some "cs-uri-query"
        OutcomeField = Some "x-edge-result-type"
        RequestIdField = Some "x-edge-request-id"
        TimestampFormats = [ "yyyy-MM-dd HH:mm:ss" ]
        EdgeOutcomes = Set.ofList [ "Hit"; "RefreshHit" ]
        OriginOutcomes = Set.ofList [ "Miss" ]
}
```

The equivalent for a JSON-lines delivery names its own fields
(`ClientRequestURI`, `EdgeResponseBytes`, `EdgeResponseStatus`,
`EdgeStartTimestamp`, `CacheCacheStatus`, `RayID`) and needs no
`QueryField`, because that URI field already carries the query.

Three notes on the shape:

- **Name `RequestIdField` if your delivery has one.** Every edge surveyed
  emits a per-request identifier. Supplying it makes re-ingestion dedup
  exact; omitting it falls back to a content-derived key that collapses
  two byte-identical responses logged in the same second — an
  *undercount*, bounded by that collision rate.
- **A value in neither outcome set reads as unknown, not as a miss.** An
  unenumerated vocabulary degrades to "I do not know", which is excluded
  from the hit-rate numerator rather than silently deflating it.
- **Timestamps with no zone are read as UTC**, not as machine-local time.
  Every edge surveyed logs UTC; reading them as local would shift whole
  days of attribution on any host that is not on UTC, and would be
  invisible to a developer whose machine is.

## Wiring it

```fsharp skip=fragment
open System
open ToolUp.MediaLibrary.DeliveredEgress
open ToolUp.Hosts.DeliveredEgress.CallbackLogSource

// Your code reaches your log store with your credential; this package
// never opens a socket.
let source =
    CallbackLogSource(
        "my-edge-logs",
        TimeSpan.FromHours 1.0, // the delivery lag YOUR delivery has
        fun () -> async {
            let! files = fetchUndeliveredLogFiles ()

            return
                files
                |> List.map (fun (name, content) -> {
                    BatchId = name
                    Records = (parseDelimited map '\t' None content).Records
                })
        }
    )

let ingestor = DeliveredEgressIngestor(usageLog, Some metricsSink, Some signer, Some scopeResolver)

// An ordinary scheduled job — no BackgroundService, no new pipeline.
let declaration = DeliveredEgressJob.hourly ingestor source
```

Hand `declaration` to the scheduled-job composition you already use. A
deployment that never does this runs nothing and is byte-identical to the
pre-742 behaviour.

`IngestBatch` is also public, so a push topology — an object-created
trigger handing over one file — calls it directly without composing a job
at all.

## Attribution, and its one real limit

`MediaId` is recoverable from every URL the media library mints. The
**scope** is recoverable from only one of them:

| Route | Media id | Scope |
|---|---|---|
| `/media/signed/{id}?token=` | yes | **yes** — from the token, signature-verified |
| `/api/media/stream/{id}` | yes | no — ambient at the origin |
| `/api/media/hls/{id}/{file}` | yes | no — ambient at the origin |

The signed form carries the scope inside its own token, and the token's
signature is **verified** before the scope is believed — anything a viewer
can put in a query string reaches a log line, so an unverified payload
would let anyone attribute bytes to any scope they cared to name. (Expiry
is deliberately *not* checked: the bytes were served while the grant was
live, and the log arrives hours later.)

The other two routes resolved their scope from request context that no
access log captured, and `IMediaLibrary` cannot recover it either —
every one of its members takes the scope as a parameter precisely so a
cross-scope read cannot be expressed. Attributing those routes therefore
requires a `CallbackScopeResolver` declaring the mapping your own
catalogue already holds. Without one, those records are dropped and
**counted** as `scope-unresolved` — never raised as an error, but visible,
so a shortfall shows up as a number rather than as silence.

## Reading the result

`PlaybackRollup` gains three fields. The important thing about them:

**`DeliveredEgressBytes` and `OriginEgressBytes` are not summable.** A
cache miss is counted in both — once by the origin as it wrote the body,
once by the edge as it relayed it — and even for that single response the
two counts differ, because a CDN's byte field is typically the whole HTTP
response including headers while the origin count is body bytes only. The
delivered figure is the billable one; the origin figure is the one the
process can prove. Both survive for that reason.

`EdgeServedBytes` is the subset the edge served from cache: the bytes the
origin structurally could not see. `EdgeHitRateByBytes` is their ratio —
by bytes rather than by request, because the question a delivery bill
poses is about volume, and one large origin pull outweighs a hundred small
edge hits.

## What this package does not do

- **It opens no socket.** Reaching your log store is your callback's job.
- **It does not backfill.** If your log pipeline drops a period, the
  delivered series is short for that period permanently — one of the
  surveyed platforms states outright that it cannot re-push historical
  data.
- **It does not reconcile the two series to each other**, for the reason
  above. Reporting a "corrected" origin figure would be inventing a
  number, which is exactly what the origin-vs-delivered labelling exists
  to avoid.

## Licence

Apache-2.0. See [LICENSE](../../../LICENSE).

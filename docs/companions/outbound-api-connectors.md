# Outbound API connector companions

A connector that syncs a third-party REST API — a CRM, an analytics product, a billing system, an
ad platform — needs the same machinery every time: resolve a credential, make the call, back off
when the provider is struggling, stay inside its quota, follow its pagination, and remember where
the last sync got to. The **outbound API connector kit** in `ToolUp.DataSources.Common` ships that
machinery once, tested, so a provider companion is a **thin provider mapping over shared
machinery**: two pure functions and two lists.

This page sits beside [`data-sources.md`](data-sources.md) (the `IDataSource` contract the
ingestor drives), [`ai-providers.md`](ai-providers.md) and [`storage-providers.md`](storage-providers.md).
The kit is BCL-only: it carries no vendor SDK (GP 1), and a deployment that composes no connector
pays nothing for it (GP 13).

## What a connector writes, and what it does not

| A connector supplies | The kit supplies |
|---|---|
| `Provider` — a stable id and a display name | credential resolution (`ExternalApi.prepare` → `Credentials.resolve`) |
| `Endpoints` — the resources it reads, each `FullRefresh` or `Incremental` | the HTTP call over the injected `IHttpTransport` |
| `PageRequest` — the request for one page at a `PageCursor` | retry and backoff, as a `RetryPolicy` record |
| `DecodePage` — a 2xx response → typed rows + the next cursor | quota: requests-per-window, concurrency, `Retry-After` (`OutboundRateBudget`) |
| `Watermark` — a row's high-water mark (incremental endpoints) | the paging loop, its stall guard and page cap (`PagedFetch`) |
| | resumable sync cursors over `IEntityStore` and a stateless job handler (`SyncCursor`, `IncrementalSync`) |

Everything on the left is pure — no I/O, no state — which is what makes a connector portable by
construction (GP 12 rule 4) and testable without a network.

## The pieces

### `IExternalApiConnector<'Row>`

Five members, all pure:

- `Provider: ExternalApiProvider` — who the provider is.
- `Endpoints: ExternalApiEndpoint list` — the resources it reads.
- `PageRequest: ExternalApiCall * PageCursor -> Result<HttpRequest, string>` — the request for one
  page, credential header included; `Error` for a configuration problem, and nothing is sent.
- `DecodePage: ExternalApiCall * HttpResponse -> Result<Page<'Row>, string>` — a 2xx response into
  typed rows and the next cursor; `Error` on a shape mismatch, never a throw.
- `Watermark: 'Row -> string option` — the row's high-water mark, for incremental endpoints.

`ExternalApiCall` is everything one page call needs, by value: the endpoint, the tenant scope, the
data source's `ConnectionScope`, the resolved credential and the pass's watermark. A connector that
reads several resources with different shapes uses a DU as its row type.

`Provider.Id` is load-bearing: it is the `RateLimitDescriptor.Provider` the connector's budget draws
from, and the connector half of every sync-cursor key. Never change it once a deployment has synced.

### Credentials — composed, not re-implemented

`ExternalApi.prepare connector secretStore ctx endpoint watermark` resolves the credential through
`ConnectorSupport.Credentials.resolve`, the same thunk every `IDataSource` uses: the ingestor's
pre-resolved credential first, otherwise `ISecretStore` re-read on every call so a rotated secret
takes effect without rebuilding anything. For an OAuth connection the access token lives in that
store: the Authorization-Code flow (`IOAuthCredentialFlow`) puts it there and the token refresher
keeps it current, so a connector never sees a refresh flow. An endpoint the connector does not
declare is a `SchemaMismatch`; an absent credential is `CredentialMissing` — both before anything
is sent.

### Transport, classification and retry — `ToolUp.Platform.Transport`

The kit calls through `IHttpTransport` (in `ToolUp.AI.Wire`, namespace `ToolUp.Platform.AI` — the
seam carries no AI semantics; it is the one portable egress seam the SDK has). On the server host
`ConnectorTransport.ofHttpClient client timeout` maps it onto a BCL `HttpClient`; give that client a
handler chain that includes the platform's egress-policy handler, so a connector's destinations are
governed like every other outbound call.

Failures classify into the connector-neutral `TransportError`:

| Case | When | Retried? |
|---|---|---|
| `Network` | no response (refused, reset, DNS, client timeout) | yes |
| `Transient (status, body, retryAfter)` | 429 or 5xx, with the provider's `Retry-After` when sent | yes — not before `retryAfter` |
| `Permanent (status, body)` | every other non-2xx | no |
| `Refused reason` | the caller's own budget said no, before sending | no |
| `Exhausted (attempts, last)` | the policy ran out | — |

`HttpCall.send policy transport request` is one call under a `RetryPolicy`. Retry is data
(GP 12 rule 3): there is no `OnFailure` hook anywhere. A `Retry-After` longer than the policy's
`MaxBackoff` ends the loop at once rather than parking the caller. The AI providers' classifier and
retry runner are projections of this same rule and loop, so the two cannot drift.

### Paging — `PagedFetch`

`PageCursor` covers the continuation styles in the wild: `Token` (an opaque continuation),
`Offset`, and `NextUrl` (a provider-supplied next-page link), plus `First`. `PagedFetch.all` reads a
resource into typed rows; `PagedFetch.fold` hands each page to a consumer in order. Either way the
result is a `PagedOutcome`: the rows (or folded state) of every page taken, the page count, the
failing page **located** (`PageIndex`, `Cursor`, `PageFailure`) and `ResumeFrom` — `None` when the
resource is exhausted, otherwise where the next run starts. A failure on page 7 never throws away
pages 1–6. A provider that hands back the cursor it was asked for is a `Stalled` failure rather than
an infinite loop, and `PagedFetchOptions.MaxPages` caps one run.

### Quota — `OutboundRateBudget`

```fsharp skip=signature
type OutboundRateBudget = {
    Window: RateLimitDescriptor        // requests per window (+ optional long window)
    MaxConcurrency: int option         // in-flight cap
    RetryAfter: RetryAfterHandling     // Ignore | HoldUpTo maxWait
}
```

`OutboundRateBudget.decorate limiter clock budget scopeId subKey transport` wraps any
`IHttpTransport`. Requests-per-window is **not** re-implemented: `Window` is the outbound
`RateLimitDescriptor` the composed `IRateLimiter` already enforces (register the same value with
`ServerApp.withRateLimitDescriptor`), so the soft ceiling, long-window refusal, per-scope or
per-provider fairness and the distributed limiter swap all apply. The decorator adds the two things
that substrate does not model: a concurrency cap, and the provider's own pause — after a 429 with
`Retry-After`, following calls through the same decorated transport wait it out, or, when it is
longer than `HoldUpTo`, are refused. Refusals are answered locally as data
(`TransportError.Refused`), never sent and never retried.

The decorator's slots and pause deadline belong to the decorated instance — share one per
connector and scope.

### Incremental sync — `SyncCursor` and `IncrementalSync`

A `SyncCursor` is stored in `IEntityStore` by value, keyed by (tenant scope, connector, data source,
endpoint). It holds `Resume` — the page cursor inside an unfinished pass — and `Watermark` — the
high-water mark of the last **completed** pass; the largest mark seen inside an unfinished pass is
promoted only when the pass completes, so a pass that dies half-way never skips rows it had not
reached. Register `SyncCursor.registration` with the entity registry at composition.

`IncrementalSync.run deps scopeId request` runs one pass: read the cursor, resolve the call from its
watermark, page from its resume point, hand each page to `deps.Ingest`, then advance the cursor
with a compare-and-set write. Two runs racing on one cursor resolve to one advancing and one
stopping. The residual window is a crash between `Ingest` taking a page and the cursor write: that
page is offered again on the next run — at-least-once, at most one page — so a sink that keys rows
by the provider's id makes the replay a no-op.

`IncrementalSync.handler deps` is the `IJobHandler` for the background-job scheduler: stateless,
every invocation re-reads the cursor, and everything else rides the payload
(`IncrementalSync.payload { Source = …; Endpoint = …; ScheduledBy = … }`). A page-capped pass is a
`Success` (the next scheduled run continues); a failure a later run could get past is a
`TransientFailure`; one that would repeat is a `PermanentFailure`.

## Worked skeleton

A connector for a hypothetical contacts API that pages with an opaque `after` token, authenticates
with a bearer token, and supports `updated_since`:

```fsharp
open System
open ToolUp.Platform.AI
open ToolUp.DataSources.Common

type Contact = { Id: string; Email: string; UpdatedAt: string }

type ContactsConnector() =
    interface IExternalApiConnector<Contact> with
        member _.Provider = { Id = "contacts-api"; DisplayName = "Contacts API" }

        member _.Endpoints = [
            {
                Name = "contacts"
                Description = "Contacts, synced incrementally by update time."
                Sync = SyncMode.Incremental
            }
        ]

        member _.PageRequest(call, cursor) =
            let query =
                [
                    match cursor with
                    | PageCursor.Token after -> "after=" + Uri.EscapeDataString after
                    | _ -> ()
                    match call.Watermark with
                    | Some since -> "updated_since=" + Uri.EscapeDataString since
                    | None -> ()
                ]

            let path =
                if query.IsEmpty then "/v1/contacts"
                else "/v1/contacts?" + String.concat "&" query

            match cursor with
            | PageCursor.First
            | PageCursor.Token _ -> Ok(HttpRequest.get path [ "Authorization", "Bearer " + call.Credential ])
            | other -> Error $"the contacts API pages by token, not %A{other}"

        member _.DecodePage(_, response) =
            // Parse response.Body with the JSON reader of your choice; return
            // Error on a shape mismatch — never throw.
            Ok { Rows = []; Next = None }

        member _.Watermark contact = Some contact.UpdatedAt
```

Composition, on the server host:

```fsharp skip=fragment
let window: RateLimitDescriptor = {
    Provider = "contacts-api"
    ShortWindow = 100, TimeSpan.FromSeconds 10.0
    LongWindow = Some(250_000, TimeSpan.FromDays 1.0)
    FairnessMode = PerScope
}

let transport =
    ConnectorTransport.ofHttpClient contactsHttpClient None   // BaseAddress = the provider
    |> OutboundRateBudget.decorate limiter TimeProvider.System
        { OutboundRateBudget.ofWindow window with MaxConcurrency = Some 4 }
        scopeId None

let deps: IncrementalSyncDeps<Contact> = {
    Connector = ContactsConnector()
    Transport = transport
    Cursors = entityStore
    SecretStore = Some secretStore
    Options = PagedFetchOptions.defaults
    Ingest = fun call rows -> upsertContacts call.ScopeId rows   // keyed by Contact.Id
}

scheduler.RegisterHandler("contacts-api.sync", IncrementalSync.handler deps)
```

The validation bar a new connector meets is the contract pack in
`src/ToolUp.Platform.Tests/Contracts/IExternalApiConnectorContract.fs`: bind
`connectorTests "<name>" factory expectedRows` with a transport serving a known resource across
more than one page.

## Six-rule portability audit

| Rule | How the kit meets it |
|---|---|
| 1. Identity by value | provider, endpoint, call and cursor are records of strings; `PageCursor` round-trips through `PageCursor.encode` |
| 2. Async at every boundary | every effect — credential read, HTTP, cursor store — returns `Async<_>` |
| 3. Retry + supervision as data | `RetryPolicy`, `OutboundRateBudget`, `RetryClass`; failures are `TransportError` / `PageFailure` values |
| 4. Stateless handlers | connector members are pure; `IncrementalSync.handler` re-reads its cursor per invocation |
| 5. No cross-shard ordering | pages of one resource in order; nothing promised across resources |
| 6. Precision at the lower bound | rate windows inherit the limiter's 1-second floor; watermarks are compared ordinally, so emit a sortable form (ISO-8601 UTC, zero-padded ids) |

## Adopting the transport in an existing companion

Many companions hand-roll their HTTP: an `HttpClient`, an inline status check, sometimes a retry
loop. They do not have to adopt the whole kit to benefit — the transport layer stands alone:

1. Build requests as `HttpRequest` records and send them through an injected `IHttpTransport`
   instead of calling `HttpClient` directly (server host: `ConnectorTransport.ofHttpClient` over the
   client you already have; AI providers already have `HttpClientTransport`).
2. Replace the inline status check and retry loop with `HttpCall.send policy transport request`,
   carrying the `RetryPolicy` your companion already accepts; branch on `TransportError`.
3. If the provider publishes a quota, declare a `RateLimitDescriptor` and wrap the transport with
   `OutboundRateBudget.decorate`.

Adoption is per companion, on its next touch, and never forced. A companion whose vendor SDK owns
its paging (the GA4 connector pages through the Google client library) keeps that SDK; the kit is
for APIs reached over plain HTTP.

**Adopters:** none yet — the first provider companion built on the kit lands here.

## Out of scope

Concrete provider companions; inbound rate limiting (the inbound limiter); push / webhook-driven
ingestion, which is a different shape from pull sync; and federation across connectors.

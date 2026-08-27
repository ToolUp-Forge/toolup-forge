# ToolUp.DataSources.GoogleAnalytics

A Google Analytics 4 connector for ToolUp.Platform: an `IDataSource` over the GA4 Data API
(reports) and the Analytics Admin API (property discovery), the paired `IOAuthCredentialFlow`
for Google's Authorization Code with offline access, and a three-step Feliz credential form for
the built-in data-ingestion admin module.

Two packages, because they are two tiers:

| Package | Tier | Contents |
|---|---|---|
| `ToolUp.DataSources.GoogleAnalytics` | Server | `IDataSource` + `IOAuthCredentialFlow` + the dimension/metric catalogue |
| `ToolUp.DataSources.GoogleAnalytics.Client` | Client (Fable) | the credential form |

The Google client libraries are referenced by the **server** package alone. No
`ToolUp.Platform.*` project gains a Google dependency, and nothing Google-shaped reaches a
Fable-compiled tier.

---

## What you get

```fsharp skip=fragment
open ToolUp.DataSources

// The connector.
let transport = GoogleAnalyticsLiveTransport.create "My Deployment"

let dataSource =
    GoogleAnalyticsDataSource.create
        secretStore
        transport
        (Some tokenRefresher)          // Phase 10h refresher, or None
        GoogleAnalyticsSourceConfig.standard

// The credential flow.
let oauthFlow =
    GoogleOAuthFlow.create
        httpClient
        secretStore
        (Some tokenRefresher)          // same instance, or None
        GoogleOAuthFlow.GoogleOAuthFlowConfig.analyticsReadonly
```

Register both as singletons on the server:

```fsharp skip=fragment
ServerApp.withExtensions {
    ComposeExtensions.empty with
        ServiceConfig =
            Some(fun services ->
                services
                    .AddSingleton<IDataSource>(dataSource)
                    .AddSingleton<IOAuthCredentialFlow>(oauthFlow))
}
```

and the credential form on the client:

```fsharp skip=fragment
Handlers = {
    ClientHandlerRegistry.empty with
        DataSourceCredentialHandlers = [
            GoogleAnalyticsCredentialUI.handler
                (GoogleAnalyticsCredentialUIConfig.create saveClientCredentials)
        ]
}
```

`saveClientCredentials` is yours to supply — see
[Supplying the client-credential save path](#supplying-the-client-credential-save-path).

A data source routes here when its `DataSourceConfig.Kind` is `"GoogleAnalytics"`.

---

## Google Cloud setup

All of this happens once, in the Google Cloud project that will own the OAuth client. None of it
can be done from the deployment.

### 1. Enable the two APIs

In the project, enable both:

- **Google Analytics Data API** — reports (`RunReport`).
- **Google Analytics Admin API** — property discovery (`ListTables`).

Enabling only the first is the common mistake: reports work, the property picker stays empty, and
the Admin API answers `403` with a message about the API not being enabled rather than about
permissions.

### 2. Configure the OAuth consent screen

- **User type**: *Internal* if every user is in your Google Workspace organisation, *External*
  otherwise. Internal skips verification entirely; External does not (see
  [Sensitive-scope verification](#sensitive-scope-verification)).
- **Scopes**: add `https://www.googleapis.com/auth/analytics.readonly`. This is a *sensitive*
  scope.
- **Test users**: while the app is unverified, only listed test users can consent, and their
  refresh tokens expire after **seven days**. That expiry is the single most common "it worked
  yesterday" report against a new GA4 connector.

### 3. Create the OAuth client

Credentials → Create credentials → OAuth client ID → **Web application**.

Register the deployment's callback as an **Authorised redirect URI**. It is:

```
{TOOLUP_OAUTH_REDIRECT_BASE}/api/oauth/google-analytics/callback
```

Google compares redirect URIs for **exact string equality** — scheme, host, port, path, and
trailing slash all count. A mismatch fails at the authorize step with `redirect_uri_mismatch`
before the user sees a consent screen.

Register every environment you will connect from (local development, staging, production) as a
separate URI on the same client, or use a separate client per environment.

### 4. Grant the connecting account access to the property

Consent grants the *app* access to whatever the *consenting user* can see. A user with no role on
a property will complete consent successfully and then see an empty property list. Grant at least
**Viewer** on the properties you intend to report on, in Analytics' own admin, not in Cloud
Console.

---

## Configuration

### Environment

| Variable | Read by | Purpose |
|---|---|---|
| `TOOLUP_OAUTH_REDIRECT_BASE` | the SDK's OAuth substrate | Public base URL of the deployment. The callback URI is derived from it, and must match what you registered in Cloud Console byte for byte. |

The connector itself reads no environment variables — every credential arrives through
`ISecretStore` (see below), and everything else through `create`.

### Secrets

Three keys per data source, all under the caller's resolved scope:

| Key | Written by | Read by |
|---|---|---|
| `google-analytics-client-id-{dataSourceId}` | your `saveClientCredentials` | the flow and the connector |
| `google-analytics-client-secret-{dataSourceId}` | your `saveClientCredentials` | the flow and the connector |
| `google-analytics-refresh-{dataSourceId}` | the SDK's OAuth callback handler | the connector, and the Phase 10h refresher |

Refresh tokens are credential-grade and long-lived. Compose the encrypted secret-store decorator
in production; the connector reads through whatever `ISecretStore` it is handed, so this costs
nothing at this layer.

### Connection scope

| Key | Meaning |
|---|---|
| `property_id` | The property this source reports on. Used when a report request omits `property`. Either `123456789` or `properties/123456789` — both normalise. |

---

## Supplying the client-credential save path

The SDK ships no wire method for writing a data source's OAuth client id and secret:
`IDataIngestionApi` has none, and `DataSourceConfig.ConnectionScope` is documented as never
carrying credentials — correctly, since it is persisted as an ordinary config blob.

So the credential form takes the persistence path as a parameter. Point it at whatever endpoint
your deployment already uses for bring-your-own-key settings, ending in two `SetSecret` calls:

```fsharp skip=fragment
// Server side, behind your own authenticated endpoint:
do! secretStore.SetSecret(scopeId, $"google-analytics-client-id-{dataSourceId}", clientId)     |> Async.Ignore
do! secretStore.SetSecret(scopeId, $"google-analytics-client-secret-{dataSourceId}", secret)   |> Async.Ignore
```

Gate it the way you gate every other credential write — the form is rendered inside the admin
module, but the form is not the authority on who may write a secret.

---

## Querying

`IDataSource.Query`'s `sql` parameter is connector-specific syntax, and GA4 has no SQL surface.
This connector's dialect is the Data API's own **`RunReportRequest` JSON**:

```json
{
  "property": "properties/123456789",
  "dateRanges": [{ "startDate": "2026-08-01", "endDate": "2026-08-27" }],
  "dimensions": [{ "name": "date" }, { "name": "sessionSource" }],
  "metrics": [{ "name": "activeUsers" }, { "name": "sessions" }],
  "limit": "500"
}
```

Two conveniences, both documented rather than magical:

- **`property` may be omitted** — the source's `ConnectionScope` `property_id` fills it. A request
  that names neither is an error; the connector will not guess, because a plausible report about
  the wrong site is worse than a refusal.
- **A bare property id or resource name** (`"123456789"`, `"properties/123456789"`) expands to a
  default report — last 28 days, `date` × `activeUsers`. Configurable on
  `GoogleAnalyticsSourceConfig`.

Everything else in the request is passed through untouched: filters, ordering, pagination, metric
aggregations, `keepEmptyRows`.

### What comes back

```json
{
  "property": "properties/123456789",
  "dimensionHeaders": ["date"],
  "metricHeaders": ["activeUsers"],
  "rowCount": 2,
  "rows": [
    { "date": "20260801", "activeUsers": "17" },
    { "date": "20260802", "activeUsers": "23" }
  ]
}
```

A flat envelope rather than the Data API's positionally-correlated header/value arrays, so a
consuming module parses rows directly instead of re-implementing the same zip.

**Every cell is a string, metrics included.** That is the Data API's own wire shape, and
preserving it is deliberate: GA4 reports integers, floats, currency and durations through one
field, so a connector that guessed which to parse each as would be wrong for somebody. Parse
according to the metrics you asked for.

`rowCount` is the total matching rows Google reports, which can exceed `rows.length` when the
request paginates — comparing the two tells you whether you saw everything.

### Schema

`GetSchema` answers from a static catalogue of GA4's common, property-independent dimension and
metric API names — no credential, no network. Custom dimensions and metrics, and item-scoped
ecommerce fields, vary per property and are deliberately absent; query the Data API's metadata
endpoint directly if you need the exact per-property set. The catalogue is the picker default, not
an authority on what a given property will accept.

---

## Disconnecting and revocation

The admin module's **Disconnect** runs the SDK's own path: it calls the flow's `Revoke`, then
deletes the refresh-token secret, then emits an `OAuthDisconnected` audit event.

`Revoke` does two things, in this order:

1. **Unregisters the Phase 10h refresh descriptor** — unconditionally, and before the network
   call. A Google outage must not leave a live refresh job behind for a connector the operator has
   disconnected.
2. **POSTs the refresh token to Google's revocation endpoint**, invalidating it and every access
   token minted from it. A `400 invalid_token` for an already-revoked token is treated as success
   — that is the desired end state, not a failure.

Afterwards the source reports `NeedsAuthorization`, the admin UI shows "Reconnect required", and a
subsequent query returns `IngestionError.CredentialMissing`.

To revoke from the other side, the user visits their Google account's third-party-access page. The
deployment finds out on its next refresh: Google answers `invalid_grant`, which the substrate maps
to `NeedsReauthorization`.

---

## Sensitive-scope verification

`analytics.readonly` is a **sensitive** scope. For an **External** consent screen this means:

- **Testing mode**: only listed test users may consent, and their refresh tokens **expire after
  seven days**. Fine for development; not a deployment.
- **In production**: Google requires app verification before an unlisted user can consent. Expect
  to supply a homepage, a privacy policy, a scope justification, and a demonstration video showing
  the consent flow and what the data is used for. Turnaround is measured in days to weeks.

An **Internal** consent screen (Google Workspace organisations only) skips all of this, and is the
right choice whenever every user is in your organisation.

Connecting a *customer's* Analytics account — rather than your own — is a different and larger
problem: it is third-party consent, needs the verification above, and is a production milestone
rather than a configuration step.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `redirect_uri_mismatch` at the authorize step | The registered URI is not byte-identical to `{TOOLUP_OAUTH_REDIRECT_BASE}/api/oauth/google-analytics/callback`. Check scheme, port and trailing slash. |
| "Google returned no refresh_token" | A re-consent without `prompt=consent`, or a grant that already exists. The flow always sends `access_type=offline` + `prompt=consent`, so in practice this means the authorize URL was not the one this flow built. |
| `invalid_grant` on refresh | The user revoked access, changed their password, or the seven-day unverified-app window elapsed. All three need fresh consent, not a retry. |
| Property picker is empty | The Admin API is not enabled on the project, or the consenting account has no role on any property. |
| Quota exhausted | GA4 enforces per-property and per-project token buckets that refill hourly and daily. Surfaces as `SourceUnreachable` naming the quota; back off for the window rather than retrying immediately. |
| Report rejected naming a field | A dimension/metric that does not exist or cannot be combined with the others. Surfaces as `SchemaMismatch` carrying Google's own message, which names the offending field. |

---

## Testing

The connector's network sits behind a three-function `GoogleAnalyticsTransport` seam, so the
`IDataSource` contract pack runs against the **real** connector with only the network faked —
credential resolution, request interpretation, property normalisation, descriptor registration and
error mapping are all under test. `GoogleAnalyticsLiveTransport.create` is the real implementation.

A live-API arm runs when `TOOLUP_GA4_CLIENT_ID`, `TOOLUP_GA4_CLIENT_SECRET`,
`TOOLUP_GA4_REFRESH_TOKEN` and `TOOLUP_GA4_PROPERTY_ID` are all set; with any of them unset it
reports as skipped rather than passing silently.

---

## A note on the package pins

`Google.Analytics.Data.V1Beta` has no stable release and never has had one — the GA4 Data API is
itself a `v1beta` surface and Google has shipped only prereleases since 2020. The `-beta` pin is
the newest thing that exists, not a choice to run ahead of a stable line.

## Licence

Apache-2.0. See the repository `LICENSE`.

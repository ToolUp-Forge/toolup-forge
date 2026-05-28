# SendGrid transactional notification sink (Phase 6f)

Email backend over the [SendGrid v3 REST API](https://docs.sendgrid.com/api-reference/mail-send/mail-send).
Implements `INotificationSink`. Pure HTTP — no SendGrid NuGet SDK
dependency (the wire format is small and the SDK pulls in transitive
deps we'd rather avoid; mirrors the `src/AIProviders/Claude/`
pattern).

## Activation

```bash
TOOLUP_TRANSACTIONAL_EMAIL=sendgrid
TOOLUP_SENDGRID_FROM=noreply@example.com
TOOLUP_SENDGRID_FROM_NAME=ExampleCo  # optional
```

The API key comes from `ISecretStore`:
```bash
# whatever your secret store implements; for FileSecretStore:
# echo -n "$SENDGRID_API_KEY" > data/secrets/_platform/SENDGRID_API_KEY
```

Or in F# directly:

```fsharp
let sendGridSink =
    SendGridNotificationSink.fromEnv addressBook secretStore (Some logger)

ServerApp.empty
|> ServerApp.withStorage blobStorage
|> ServerApp.withTransactionalSink sendGridSink
|> ServerApp.run
```

## Templated email

`TemplatedEmail (templateId, variables)` maps to SendGrid's
`dynamic_template_data`. The vendor's template id is whatever
SendGrid's UI / API calls "Template ID" (starts with `d-`).

## Failure classification

| HTTP response | `SinkResult` |
|---|---|
| 200 / 201 / 202 | `Delivered` (vendor message id from `X-Message-Id` header when present) |
| 429 | `TransientFailure` (rate limit — retried) |
| 5xx | `TransientFailure` |
| Other 4xx | `PermanentFailure` |
| Network / timeout | `TransientFailure` |

## API key rotation

Read fresh from `ISecretStore` on every send — rotating the value in
the store takes effect at the next dispatch without server restart.

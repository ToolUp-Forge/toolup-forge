# ToolUp.Webhooks.Server

Giraffe / ASP.NET Core wiring for [`ToolUp.Webhooks.Core`](../ToolUp.Webhooks.Core/README.md):
the per-`Kind` handler registry, an in-memory dedup store, and the
verify → dedup → dispatch inbound route. **Fail-closed** — a bad-signature or
out-of-window delivery is rejected before any handler runs.

## What's here

- **`WebhookRegistry`** — `ofList` / `tryOfList` build a per-`Kind` registry, rejecting
  duplicate kinds at compose time.
- **`InMemoryWebhookDedupStore`** — dev / single-instance `IWebhookDedupStore`. Mark and
  swap for a durable store under multiple replicas.
- **`WebhookRoutes.routes`** — mounts `POST /webhooks/{kind}`: looks the handler up by
  kind, resolves the signing secret (via a caller-supplied `resolveSecret` over
  `ISecretStore` — this module never reads secrets directly), verifies, dedups, dispatches.

## Example

```fsharp
open ToolUp.Webhooks
open ToolUp.Webhooks.Server

let registry =
    WebhookRegistry.ofList [
        { new IInboundWebhookHandler with
            member _.Kind = "github"
            member _.Handle(w) = async { return Acknowledged } }
    ]

let dedup = InMemoryWebhookDedupStore() :> IWebhookDedupStore

let resolveSecret kind =
    secretStore.GetSecret("_platform", sprintf "WEBHOOK_SECRET_%s" kind)

let app = WebhookRoutes.routes WebhookScheme.gitHubStyle resolveSecret dedup registry
```

Status mapping: `200` handled (or replay / ignored); `400` missing/malformed/mismatch;
`404` no handler for the kind; `408` timestamp outside the freshness window.

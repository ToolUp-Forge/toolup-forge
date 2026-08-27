# ToolUp.ExternalCompute.Http

Generic HTTP/REST `IExternalComputeDispatcher` for ToolUp Platform: hand a unit of work to **any**
HTTP compute service — a self-hosted training server, an inference endpoint, a container/batch API
with a REST facade, a Flask wrapper around a fit script — through config-driven submit / status /
cancel requests.

- **No vendor SDK, no paid dependency.** BCL `HttpClient` + `System.Text.Json`, nothing else.
- **Credentials read per call** from `ISecretStore`, so a rotation is picked up on the next request
  with no restart.
- **Push completion where the service supports it**: the per-handle callback credential the platform
  mints is delivered to the service, so a run resolves the moment the work finishes instead of on the
  next poll. A service that cannot call back is reconciled by polling, which is the behaviour it
  already had.

## Composing it

```fsharp skip=fragment
open System.Net.Http
open ToolUp.Platform
open ToolUp.Platform.ExternalCompute.Http

let httpClient = new HttpClient()

let config =
    HttpComputeConfig.create
        "gpu-training"                              // handle Backend label
        "https://training.internal/api/jobs"        // POST here to submit
        ("https://training.internal/api/jobs/" + HttpComputeConfig.JobIdPlaceholder)
        (JsonPath.ofString "job.id")                // job id, in the submit response
        (JsonPath.ofString "job.state")             // status, in the status response
    |> HttpComputeConfig.withAuth (HttpComputeAuth.bearer "training-api-token")
    |> HttpComputeConfig.withResultRef (JsonPath.ofString "job.artifact.uri")
    |> HttpComputeConfig.withProgress 100.0 (JsonPath.ofString "job.percentComplete")
    |> HttpComputeConfig.withCancel "DELETE" ("https://training.internal/api/jobs/" + HttpComputeConfig.JobIdPlaceholder)
    |> HttpComputeConfig.withHealthUrl "https://training.internal/healthz"

ServerApp.empty
|> HttpComputeCompose.withHttpCompute config secretStore httpClient logger
|> ServerApp.run
```

`withHttpCompute` folds in the dispatcher singleton, a readiness probe (when a health URL is
configured), the startup preflight, and `ServerConfig.ExternalCompute = CustomExternalCompute`. A
deployment that never calls it keeps the `NoExternalCompute` default and pays nothing.

Environment-bound alternative: `HttpComputeConfig.fromEnv ()` returns `None` unless
`TOOLUP_EXTERNAL_COMPUTE=http`, and otherwise `Ok config` or `Error problems` read from
`TOOLUP_EXTERNAL_COMPUTE_HTTP_*` variables (`SUBMIT_URL`, `STATUS_URL`, `CANCEL_URL`,
`JOBID_SELECTOR`, `STATUS_SELECTOR`, `PROGRESS_SELECTOR`, `RESULTREF_SELECTOR`, `ERROR_SELECTOR`,
`RETRIABLE_SELECTOR`, `AUTH_HEADER`, `AUTH_SECRET_KEY`, `AUTH_VALUE_FORMAT`, `STATUS_PENDING` …
`STATUS_CANCELLED`, `CALLBACK_BASE_URL`, `CALLBACK_REGISTRATION_URL`, `HEALTH_URL`,
`TIMEOUT_SECONDS`, `PROGRESS_SCALE`, `BACKEND`).

## Selectors

A selector is a **dotted path**: a `.`-separated sequence of property names, each optionally followed
by one or more `[n]` array indices. `state`, `job.status`, `items[0].phase`, `result.refs[1]`.

That is the whole grammar, deliberately. No wildcards, no filters, no expressions — each of those
buys a rarer response shape at the price of a second language inside the config, with its own parser,
error messages and semantics to document. A response shape a dotted path cannot describe is a signal
to write a companion that knows the service, not to grow the grammar: `IExternalComputeDispatcher` is
twenty lines.

## Status vocabulary

`HttpComputeStatusMap` maps the service's own status labels onto the five `ExternalOutcome` states,
case-insensitively. The defaults already cover most REST compute services (`queued` / `running` /
`succeeded` / `failed` / `cancelled` and the usual synonyms); a service that says `WORKING` adds it.

A status label the map does not declare is reported as a **terminal failure naming the label** — it
is never guessed, because every available guess is a claim about whether the work finished. A label
declared under two classes is refused at compose.

## Error classification

The `Retriable` flag on `ExternalComputeError` is the retry decision, so it is explicit:

| Condition | Retriable |
|---|---|
| transport failure, or the per-request budget expired | yes — the request was never answered, so nothing was learned about the work |
| `5xx` | yes |
| `408 Request Timeout`, `429 Too Many Requests` | yes — the two `4xx` codes that mean "ask again"; treating them as terminal abandons good work exactly when a queue is deepest |
| any other non-`2xx` | no — a statement about the request, which re-sending cannot change |
| `404` on a status read | no — the service has forgotten the unit; reported as `Failed`, never as a fabricated `Cancelled` |

`Poll` returns `ExternalOutcome`, which has no error channel, so a transport failure is reported as
`Failed (retriable …)`: terminal in shape so the poller stops, with retriability as data so the
scheduler can decide. It never answers `Running` (which would keep a dead handle alive forever) or
`Cancelled` (a fabricated terminal state).

## What this dispatcher does not claim

- **It does not honour `ExternalWorkSpec.Idempotency` itself.** The key is forwarded when the config
  names a field for it, but the handle id is platform-minted per `Submit`, so a service that dedupes
  returns the same native ref under a new handle id. Phase 318 words idempotent resubmit as a
  *should* for exactly this reason; the platform-side memoization decorator is the portable answer.
- **It does not validate the presented handle's scope.** `Poll` / `Cancel` address the service by the
  opaque native ref, which is all the service gave us. Tenant isolation is enforced a layer up, where
  it is structural: the handle store is scope-partitioned and the callback ingress takes the scope
  from the platform's own stored record, never from the request.
- **It declares no isolation posture**, so a spec requiring an isolated execution profile is refused
  rather than handed to a service that has made no such guarantee. A generic HTTP endpoint cannot
  honestly assert no-egress.

## Health + preflight

The readiness probe exists only when the config names a **dedicated health URL** — probing the submit
URL would submit work on every readiness poll, and probing a status URL needs a job id there is no
safe value for. An unreachable compute service reports `Degraded`, not `Unhealthy`: the deployment
still serves every request path, and draining the rotation would turn a partial outage into a total
one.

The startup validator checks the two things only a running deployment can answer — whether the
configured credential is actually in `ISecretStore` (the most common deployment miss, which otherwise
surfaces as a `401` on the first submission hours later), and whether the service is reachable. Both
report rather than abort: a compute service that is briefly down must not take the whole deployment
with it.

## Conformance

Passes the `IExternalComputeDispatcher` contract pack unmodified against an in-process stub HTTP
server, declaring `HonoursIdempotency = false` and `ValidatesHandleScope = false` per the section
above.

## Licence

Apache-2.0.

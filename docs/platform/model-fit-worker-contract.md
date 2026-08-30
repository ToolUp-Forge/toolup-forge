# The model-fit worker contract (`modelfit/v1`)

A **fit worker** is a program that trains a model. It reads a dataset, runs whatever numerics it likes, writes an artifact, and reports some diagnostics. This document is everything you need to write one.

The contract is deliberately small, and the reason is a design constraint rather than a taste: **a fit worker must not have to be a .NET program.** The workloads this seam exists for are Python and R fits on machines the web server never touches, and the moment a worker has to import an SDK versioned in this repository, every one of those fits is coupled to this repository's release cadence. So the worker is an HTTP contract: a JSON envelope in, a JSON descriptor out, one authenticated POST to say it is done.

You do not need any package from this repository to write a worker. If you find yourself needing one, that is a defect in this document.

## Conformance

The wire face described here conforms to the **model-execution wire specification** — `MODEL_EXECUTION_WIRE.md` and its conformance corpus, published at <https://github.com/Fuaran-Core/fuaran-model-execution-spec> (Apache-2.0).

This repository *conforms to* that specification; it does not define the format. **Where anything in this document disagrees with the specification, the specification wins**, and the disagreement is a defect here rather than a local variant. The certification lives in `src/ToolUp.Platform.Tests/Conformance/ModelExecutionSpecConformance.fs`, and the rules that make an external corpus worth certifying against are in [`testing-conventions.md`](testing-conventions.md#certifying-against-an-external-conformance-corpus-phase-602).

## The shape of one fit

```
platform                          worker
   |                                 |
   |--- POST submit (envelope) ----->|   accepted, worker mints its own job id
   |<-- { job id } ------------------|
   |                                 |
   |--- POST webhook (credential) -->|   the per-handle callback URL + secret
   |                                 |
   |--- GET status ----------------->|   polled; may report a progress fraction
   |<-- { running, 0.4 } ------------|
   |                                 |
   |<-- POST completion callback ----|   the artifact descriptor, authenticated
   |                                 |
```

Four exchanges, and only the first and last are load-bearing. Status polling is how a worker that *cannot* call back is still resolved; the callback is how a worker that can avoids a poll interval of latency. A fit is correct either way — the callback is a latency optimisation, never a correctness dependency.

## 1. The work envelope

The platform submits a unit of work whose **kind is `modelfit/v1`**. The version lives in the kind, not in a field: a future `modelfit/v2` is a *different kind* that a worker either routes or refuses, never the same kind carrying a version some workers read and others ignore.

The submit request's shape (URLs, field names, auth header) is your service's business — it is configured deployment-side. What is fixed is the **payload**, a JSON object:

```json
{
  "envelope": "modelfit/v1",
  "scopeId": "team-acme",
  "specRef": "{\"family\":\"glm\",\"link\":\"log\"}",
  "specHash": "9f2c…64 lowercase hex…",
  "specHashAlgorithm": "",
  "datasetParquetRef": {
    "scopeId": "team-acme",
    "datasetId": "observations",
    "version": 3,
    "contentHash": "deadbeef…",
    "format": "parquet",
    "rowCount": 1200
  },
  "seed": 4242,
  "gates": [
    { "name": "rmse", "threshold": 0.5, "direction": "AtMost" }
  ],
  "resourceHints": { "gpu": "1" }
}
```

| Field | Meaning |
|---|---|
| `envelope` | Always `"modelfit/v1"`. Echoed so a payload you persist alongside your own job record is self-describing when you re-read it. |
| `scopeId` | The tenant the fit runs under. Partition your own records by it. |
| `specRef` | **Your** model specification, verbatim, as a string. The platform never parses it — it is whatever your worker's own spec language is. |
| `specHash` | Lowercase hex SHA-256 of `specRef`'s UTF-8 bytes. Verify it if you want to be sure you received what was hashed. |
| `specHashAlgorithm` | The submitter's name for the rule that produced `specHash`, or `""`. Carried verbatim, never acted on. |
| `datasetParquetRef` | Where the training data lives. See §2. |
| `seed` | The fit's seed. It is part of the fit's identity, so a worker that ignores it does not merely vary — it breaks reproducibility. |
| `gates` | Diagnostics the platform will threshold *after* you return. **You do not evaluate these.** See §4. |
| `resourceHints` | Advisory. Ignore a hint you do not understand; refuse the work outright if you understand one and cannot honour it. |

`direction` is `"AtLeast"` or `"AtMost"` — no other value is valid, and an unrecognised one is refused rather than guessed at.

### Refusing an envelope you do not speak

A worker declares which envelope versions it accepts. If a deployment points a `modelfit/v1` platform at a worker that only accepts `modelfit/v2`, the submission is **refused before the payload leaves the platform** — you will never see it. That check is deployment-side configuration (`ExternalFitOptions.AcceptedEnvelopes`), so tell your operator what your worker accepts.

If a submission somehow reaches you under an envelope you cannot read, refuse it as a **terminal** failure, not a retriable one. Being asked twice does not teach you a new envelope.

## 2. Reading the dataset

`datasetParquetRef` is a **reference to an immutable dataset vintage** — coordinates, not rows. A fit that trains on ten million rows must not have those rows pushed through a submit request.

**Read `format` before you read the bytes.** The field is not decorative and the type name is deliberately not a promise: a deployment that has composed a Parquet codec ships `"parquet"` and the blob is native Parquet, readable by `pandas.read_parquet` or `arrow::read_parquet` directly. A deployment that has **not** ships `"toolup-frame-v1"`, which is a JSON frame, not Parquet. A worker that assumes Parquet because the field is called `datasetParquetRef` will hand a Parquet reader a JSON document. Read the tag; refuse a format you do not implement, terminally, naming what you got and what you can read.

`contentHash` addresses the content. In the default storage layout the bytes live in the blob container named for `scopeId`, under `objects/_content/{contentHash}.data`; the content is deduplicated by hash, so two vintages with identical bytes share one blob.

**How your worker is granted read access is a deployment decision, and the envelope does not currently carry a credential.** Be clear-eyed about this: `datasetParquetRef` is coordinates, and coordinates are not authorisation. Two arrangements are in use:

- **Scoped store access.** The worker is given credentials to the object store, scoped to the container(s) it may read. This is what a worker running inside the same trust boundary normally has, and it is the arrangement the default composition assumes.
- **A time-boxed signed URL, minted per fit.** Blob backends that implement `ISignedUrlBlobStorage` can mint one (`BlobStorage.trySignedUrl`), and a backend that cannot declines cleanly rather than failing. **The `modelfit/v1` envelope does not yet carry such a URL** — a deployment that wants this hands it to the worker out of band today. Carrying a signed, TTL-bounded read URL as an envelope field is the obvious successor: it would be an additive field and therefore a `modelfit/v2` concern, since a worker that ignored it would be reading via credentials the field exists to remove.

Do not attempt to read any dataset other than the one you were handed, and do not attempt to write to the store. A worker under the `Isolated` execution profile is additionally required to have no network egress beyond its completion callback; if your deployment declares that posture, the two rules above are enforced rather than requested.

## 3. Reporting progress

There is no progress ingress to POST to. Progress travels the same way status does: **the platform polls your status endpoint, and whatever fraction it reads there is what surfaces.**

Report a fraction if you can compute one honestly — `0.0` to `1.0`, or a percentage if your operator configured the scale. Report nothing if you cannot: a worker that invents a number to fill the field is worse than one that reports none, because a progress bar that moves is read as evidence the fit is alive.

The platform turns each observation into a checkpoint on the job's progress sink, attributed to the dispatching job and scope. Two things follow that are worth knowing:

- **Intermediate frames may be coalesced.** A chatty status endpoint does not flood anything; the platform sheds frames under a rate limit. The frame that says *done* is never the one shed.
- **The poll interval is the resolution.** Reporting progress that changes faster than the platform polls buys nothing.

## 4. Returning the artifact

When the fit finishes, the result you return is a **JSON artifact descriptor**, as a string, in the `resultRef` position of your completion:

```json
{
  "envelope": "modelfit/v1",
  "artifactId": "blob://fits/glm-4242.bin",
  "contentHash": "aaaa…64 lowercase hex…",
  "byteLength": 20480,
  "diagnostics": { "rmse": 0.25, "converged": 1.0 },
  "durationMs": 91000,
  "costUnits": 4.5
}
```

| Field | Rules |
|---|---|
| `envelope` | `"modelfit/v1"`. A descriptor answering under an envelope the platform does not know is **refused, not read** — a worker that upgraded ahead of its deployment gets an error, not a mis-parse. |
| `artifactId` | Your own identity for the stored artifact — a blob key, a URI, a content-addressed name. Opaque: the platform records it and never dereferences it. Must be non-empty. |
| `contentHash` | **Lowercase** hex SHA-256 of the artifact bytes, exactly 64 characters. Uppercase is refused rather than normalised: the digest is carried as text, so two casings would name one artifact. |
| `byteLength` | Artifact size in bytes. Non-negative. |
| `diagnostics` | A flat object of `name → number`. This is where every measurement goes. |
| `durationMs` | Your own compute duration, as an integer. Report *your* time, not elapsed wall-clock since submission — the platform must not have queue latency folded into a fit's cost. |
| `costUnits` | Your own cost self-report. `0` is a fine answer. |

**You do not evaluate the gates, and you must not return verdicts.** The platform thresholds `diagnostics` against the `gates` it sent, and it does so on its own side for a reason that has nothing to do with distrusting your arithmetic: a gate is the deployment's acceptance criterion, and a worker grading its own homework makes the criterion unfalsifiable.

The corollary is the one to remember: **a gate whose diagnostic you did not report fails closed.** If the request asked for a gate on `auc` and your `diagnostics` has no `auc` key, that gate fails, with no observed value invented for it. Silence is never a pass. Report every diagnostic the `gates` array names, or expect the fit to be reported as gate-failing.

If the artifact descriptor cannot be read, the fit is refused as **malformed** even though your worker reported success. This is the most common integration mistake: a worker written against the generic external-compute prose returns a bare blob key (`"blob://fits/glm-4242.bin"`) because a `resultRef` is documented there as "a blob key, an artefact URI, a content hash". For `modelfit/v1` it is the descriptor above. A fit's outcome is irreducibly more than one string — an artifact needs a hash and a length to be checkable at all, and the gates cannot be evaluated without diagnostics.

## 5. Completing the fit

Immediately after your submit is accepted, the platform delivers a **per-handle credential** to whatever registration endpoint your service exposes:

```json
{
  "callbackUrl": "https://app.example.com/_platform/external-compute/callback",
  "callbackSecret": "…256 bits of hex…",
  "handleId": "3f9c8a12-…"
}
```

Store all three against your own job record. **Never log `callbackSecret`.** The platform holds only its hash; this delivery is the one and only time the cleartext exists outside your service.

The credential arrives *after* acceptance rather than in the submit request, and that ordering is forced rather than chosen: the credential is keyed by a handle that does not exist until the submission has been accepted. A worker fast enough to finish before the credential lands simply cannot call back, and that fit resolves by poll instead. Nothing is lost but latency.

When the fit terminates, POST to `callbackUrl`:

```
POST /_platform/external-compute/callback
X-ToolUp-External-Callback-Secret: <callbackSecret>
Content-Type: application/json

{
  "handleId": "3f9c8a12-…",
  "status": "succeeded",
  "resultRef": "{\"envelope\":\"modelfit/v1\",\"artifactId\":\"…\",…}"
}
```

Five primitive fields, emittable with `curl`:

| Field | When |
|---|---|
| `handleId` | Always. The **only** routing input the platform accepts from you — the scope, the run and the backend all come from its own stored record. |
| `status` | `"succeeded"`, `"failed"` or `"cancelled"`. Case-insensitive. Terminal only: `"running"` is not an outcome and is refused. |
| `resultRef` | Required for `"succeeded"` — the artifact descriptor from §4, as a string. |
| `error` | Required for `"failed"` — a human-readable description. Never embed a credential in it. |
| `retriable` | Read only for `"failed"`. Absent means `false`: a worker that does not say is not asserting the work is worth re-running, and the other default re-submits a malformed payload forever. |

The route sits outside the session-auth and CSRF envelope precisely so a service with no browser and no cookie jar can reach it. It is paid for by the per-handle secret, a uniform refusal, a per-source throttle, and an audit row on every outcome.

### Idempotency: re-delivering the same handle

**Send the callback again if you did not see a `200`.** Delivering the same completion twice is not merely tolerated, it is expected — that is what a webhook system retrying on a lost response does.

- The **first** delivery to win resolves the fit.
- Every **later** delivery for the same handle answers `200`, with a body whose resolution reads `already-resolved`. It is a success from your point of view and nothing is written.
- The same is true when the platform's own poll got there first: the fit is resolved once, whichever path won.

A duplicate is answered `200` and not `409` deliberately. A backend that retries on any non-2xx would otherwise retry a *correct* duplicate forever.

Refusals are uniform: a forged secret, an unknown handle, a malformed body and a non-terminal status all answer `403` with the same text, and the platform's log and audit trail — not the response — record which. If you are getting `403` on a callback you believe is correct, the two things to check are that you are sending the secret in `X-ToolUp-External-Callback-Secret` (a header, never a query parameter) and that `handleId` is the one the platform gave you, not your own job id.

## A minimal worker

Everything above, in one file. No SDK, no client library — `flask`, `requests` and whatever numerics you were going to use anyway.

```python
import hashlib, json, threading, requests
from flask import Flask, request, jsonify

app = Flask(__name__)
FITS = {}                       # job id -> {"state","progress","hook"}
ACCEPTED = {"modelfit/v1"}

@app.post("/fits")
def submit():
    body = request.get_json()
    payload = body["payload"]

    if payload.get("envelope") not in ACCEPTED:                      # §1
        return jsonify(error=f"unsupported envelope {payload.get('envelope')}"), 400
    if payload["datasetParquetRef"]["format"] != "parquet":          # §2 — read the tag
        return jsonify(error="this worker reads parquet only"), 400

    job_id = "fit-%d" % (len(FITS) + 1)
    FITS[job_id] = {"state": "running", "progress": 0.0, "hook": None}
    threading.Thread(target=run, args=(job_id, payload), daemon=True).start()
    return jsonify(fit={"id": job_id})

@app.get("/fits/<job_id>")                                           # §3 — progress is polled
def status(job_id):
    fit = FITS[job_id]
    return jsonify(fit={"state": fit["state"], "percentComplete": fit["progress"] * 100})

@app.post("/fits/<job_id>/webhook")                                  # §5 — store, never log
def webhook(job_id):
    FITS[job_id]["hook"] = request.get_json()
    return "", 204

def run(job_id, payload):
    ref = payload["datasetParquetRef"]
    frame = read_dataset(ref["scopeId"], ref["contentHash"])         # your store access
    FITS[job_id]["progress"] = 0.4

    model, diagnostics = fit_model(frame, payload["specRef"], payload["seed"])
    blob = serialise(model)

    descriptor = {                                                   # §4
        "envelope": "modelfit/v1",
        "artifactId": store_artifact(job_id, blob),
        "contentHash": hashlib.sha256(blob).hexdigest(),             # lowercase hex
        "byteLength": len(blob),
        "diagnostics": diagnostics,   # every name the request's gates ask for
        "durationMs": 91000,
        "costUnits": 0,
    }

    FITS[job_id]["state"] = "succeeded"
    hook = FITS[job_id]["hook"]
    if hook:                                                         # §5 — else it resolves by poll
        requests.post(
            hook["callbackUrl"],
            headers={"X-ToolUp-External-Callback-Secret": hook["callbackSecret"]},
            json={"handleId": hook["handleId"],
                  "status": "succeeded",
                  "resultRef": json.dumps(descriptor)},
            timeout=30,
        )
```

The stub worker the contract test binds against — `src/ToolUp.Platform.Tests/InProcess/ExternalModelFitTests.fs` — is this program with the numerics removed. It is worth reading beside this document, because it is the executable form of everything above and it is held to it on every build.

## Deployment side, briefly

For completeness, the composition a deployment writes. A worker author does not need this.

```fsharp skip=fragment
let registry = ExternalFitCompletionRegistry()

let provider =
    ExternalModelFitProvider(
        dispatcher,                       // any IExternalComputeDispatcher
        datasetStore,
        registry,
        ExternalFitOptions.create "python-fitter" "2.1.0"
        |> ExternalFitOptions.withDeclaredGates [ "rmse" ]
        |> ExternalFitOptions.withTimeout (TimeSpan.FromHours 6.0),
        Some handleStore,                 // enables the push path
        Some logger
    )
```

Register `provider` as an `IModelFitProvider` singleton, and register `ExternalFitCompletionSink(registry, inner)` as the deployment's `IExternalCompletionSink` — passing the scheduler's own sink as `inner` if the deployment also runs external-compute *jobs*, so callbacks for those keep resolving.

Nothing here is composed by default. A deployment that does not want external fits builds none of it and pays nothing.

## See also

- [`external-compute.md`](external-compute.md) — the dispatcher seam, the handle, and the outcome vocabulary this contract rides on.
- [`jobs.md`](jobs.md) — the scheduler, progress checkpoints, and how a long-running run reports itself.
- [`testing-conventions.md`](testing-conventions.md) — the external-conformance-corpus rules, including the specification cited above.

# Which store for what

The platform now offers several server-side surfaces that all, loosely, "store
outputs". They are **not** interchangeable — each has a different lifecycle, a
different key, and a different consumer. Reaching for the wrong one produces
subtle problems: outputs that should be governed leak into a cache with no
lifecycle, or provenance that should be queryable is buried in a module's
result history. This page draws the lines.

## The one-line answer

| Surface | Stores | Keyed by | Lifecycle |
|---|---|---|---|
| **`IDataObjectStore`** | Opaque versioned byte content (the substrate every other surface rides) | `(scopeId, objectId)` you choose | Versioning policy (`Unversioned` / `Versioned` / `StrictlyVersioned`) |
| **`IDatasetStore`** | Rectangular typed **datasets** — immutable vintages | `(scopeId, datasetId, version)` | Immutable versions ("vintages") |
| **`IResultStore`** | An analytical **module's outputs** — a result cache/history | `(scopeId, moduleName, resultType)` | `StrictlyVersioned` history of the latest computed result |
| **`IModelRegistry`** | A fitted **model artifact** — governed, provenance-complete | The fit **composite key** `(specHash, datasetVersion, seed, providerId, providerVersion)` | Lifecycle status (`Draft` → `Fitted` → `Approved` → `Retired`) |
| **Knowledge Base** (`ToolUp.KnowledgeBase`) | User **documents** for retrieval (RAG) | `(scopeId, documentId)` | Ingested → chunked → embedded; erasable |

If you can answer "what is the natural key of the thing?", the surface follows:

- Keyed by **content you chose** → `IDataObjectStore`.
- Keyed by **a dataset + its version** → `IDatasetStore`.
- Keyed by **which module produced it** → `IResultStore`.
- Keyed by **the reproducible identity of a fit** → `IModelRegistry`.
- Keyed by **a document a user uploaded to ask questions about** → Knowledge Base.

## `IModelRegistry` vs `IResultStore` — the decision that trips people up

Both persist "an analytical output" as `StrictlyVersioned` data objects. The
plan draws the line deliberately (decision **D11**):

**`IResultStore` is a result cache.** A module runs, produces its `resultType`
output, and saves it under `(moduleName, resultType)`. The *latest* version is
what consumers read; the version history is an audit trail of "the last time
this module ran, here is what it produced". There is **no lifecycle** — a
result is not "approved" or "retired", it is simply the current output of a
module. The key names the *producer*, not the identity of the computation.

**`IModelRegistry` is a governed artifact catalogue.** A fitted model is keyed
by its **composite identity** — `(specHash, datasetVersion, seed, providerId,
providerVersion)` — so two fits differing in *any* component (a re-seed, a new
data vintage, a provider upgrade) are **different artifacts you can query side
by side**. That is the whole point: "which model version produced the number in
front of the CFO, trained on which data" is one query (`QueryBySpecHash` returns
every vintage of one modelling decision). An artifact carries a **lifecycle**
(`Draft` → `Fitted` → `Approved` → `Retired`) with gated, audited transitions
(promotion to `Approved` requires Owner/Admin — GP 4/GP 6). The registry stores
the artifact *record* (identity + diagnostics + gate verdicts + lifecycle +
provenance); the fitted parameters themselves are an opaque blob in
`IDataObjectStore` (dedup + immutability inherited).

> **The registry never stores a module output, and the result store never
> governs a lifecycle.** If you are caching "the last thing my module computed",
> that is `IResultStore`. If you are cataloguing "a reproducible, approvable,
> provenance-complete fitted model", that is `IModelRegistry`.

## Where the bytes actually live

Every surface above is layered over `IDataObjectStore`, which is itself layered
over `IBlobStorage`. So:

- A **dataset vintage's** rows are an `IDataObjectStore` object (content =
  encoded frame; dedup by content hash).
- A **model artifact's** fitted parameters are an `IDataObjectStore` object
  (the `ArtifactRef` the provider returned); the registry stores a *separate*
  record object holding the artifact's identity + lifecycle.
- A **result** is an `IDataObjectStore` object under the `moduleName__resultType`
  id convention.

Content-addressable dedup means identical bytes across any of these share one
`_content/{hash}.data` blob within a scope — you never pay twice for the same
payload, regardless of which surface referenced it.

## Dataset wire formats — the codec seam

A dataset vintage's content bytes are encoded through the pluggable
`IDatasetCodec` seam, and the composition chooses the wire format an external
compute worker will find behind a `DatasetContentRef`:

| Composition | Codec | `Format` tag | Worker expectation |
|---|---|---|---|
| `BlobDatasetStore.create` (default) | `JsonFrameDatasetCodec` (BCL-only) | `"toolup-frame-v1"` | A self-describing JSON frame; not Parquet |
| `BlobDatasetStore.createWithCodec … (ParquetDatasetCodec())` | `ToolUp.DataSources.Parquet` companion | `"parquet"` | Native Parquet — readable by any Python / R Parquet reader with no ToolUp code |

The format tag on `DatasetContentRef` is **honest by construction**: a worker
inspects `Format` before parsing, so a non-Parquet default composition can
never silently hand Parquet-expecting workers the wrong bytes. Deployments
that fit through external compute workers should compose the Parquet codec;
everything else can stay on the dependency-free default. The declared
`DatasetSchema` (dtypes, nullability, panel roles) travels inside the Parquet
file's custom metadata, and the codec verifies it against the physical schema
on every decode.

## `SpecHash` is submitter-minted and opaque

The fit composite key `(specHash, datasetVersion, seed, providerId,
providerVersion)` starts from a hash **the submitter mints under its own
canonicalisation rule**. Forge stores, keys, and compares `SpecHash` as an
opaque string and **never re-derives, normalises, or validates it against
the spec payload** — the payload is equally opaque (GP 1). This is what
keeps artifact identities stable across every consumer that minted hashes
under the spec's rule: a server-side re-hash or payload canonicalisation
would silently fork identities and corrupt the cross-record join between
submissions, outcomes, and registry queries. The posture is executable —
the model-fit contract pack fits requests whose declared hash deliberately
matches no canonical hash of their payload and proves they are keyed and
retrievable by exactly the declared value. (In-process helpers like
`ModelSpecRef.ofPayload` are conveniences for callers that *choose* forge's
hashing rule; nothing server-side applies them to submitted specs.)

## Provenance ties them together

Model-artifact registration emits a **lineage edge** (`ILineageStore`, Phase 8a)
from the artifact to the dataset version its fit read. Combined with the
dataset-assembly provenance (dataset version → assembly spec → source objects),
the walk **artifact → dataset version → assembly spec → sources** is a single
lineage traversal. Lineage is the connective tissue; the stores above are the
nodes it connects.

## See also

- `IDatasetStore` — immutable typed dataset vintages (Phase 448).
- The model-fit envelope — `IModelFitProvider` / `FitOutcome` (Phase 449); a
  `FitOutcome` is what `IModelRegistry.Register` consumes.
- `IResultStore` — analytical module output persistence (Phase 8 / 21c).
- `ILineageStore` — the lineage query surface (Phase 8a).

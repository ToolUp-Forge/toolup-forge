# ToolUp.Facts

A bitemporal, content-addressed **fact store** for ToolUp.Platform — the
typed *memory* tier that numeric answers quote verbatim. Companion package
(Phase 520); a deployment that composes no fact store pays zero runtime
cost.

## What this is

A **fact** is a verified assertion about a `(subject, metric, period)`: a
value, with provenance, produced by deterministic computation, asserted by
a human, or imported from a peer. The model is deliberately small and every
guarantee is *decidable*:

- **Content-addressed** — `FactId = hash(subject, metric, period, method,
  inputHashes)`. Re-asserting an identical tuple is idempotent (the store
  converges under replay); a changed input yields a *new* fact whose
  supersession edge is **derived**, never declared.
- **Bitemporal** — `Period` is valid time, `AsOf` is transaction time.
  "The fact base as of May 3" is a first-class query. A **forecast** is
  simply a fact whose valid time extends beyond its transaction time — no
  flag; the timestamps carry it.
- **Append-only** — a correction is a supersession (a new fact with a
  later `AsOf`), never a mutation.
- **Disclosure-classified at birth** — every fact carries a `Disclosure`
  (`Surfaceable` / `Internal` / `Restricted policy`) from its first
  assertion. Egress enforcement is a later phase; the field lands here.

| Concern | File | Types |
|---|---|---|
| Fact model + laws | [`Shared/FactTypes.fs`](../ToolUp.Facts.Core/Shared/FactTypes.fs) | `Fact`, `FactValue`, `MethodRef`, `Disclosure`, `TemporalExtent`, `Fact.compute` / `lineageKey` / `Freshness.derive` |
| Store contract | [`Server/IFactStore.fs`](../ToolUp.Facts.Server/Server/IFactStore.fs) | `IFactStore`, `FactDraft`, `FactQuery` |
| Blob-backed default | [`Server/BlobFactStore.fs`](../ToolUp.Facts.Server/Server/BlobFactStore.fs) | `BlobFactStore.create` |

## Why a companion, not core SDK

A fact base is a *grounding capability*, not platform substrate. Nothing
else in the SDK depends on `IFactStore`; analytics-only or CRUD-only
deployments never need it. Same rationale as `ToolUp.AI` / `ToolUp.RAG` not
living in `ToolUp.Platform`.

The companion is a *consumer* of substrate (`IBlobStorage` for storage,
`IEventStore` for the GP 6 audit trail under the reserved `_facts` source
module), not substrate itself.

## How to use

```fsharp
open ToolUp.Facts

let store = BlobFactStore.create blobStorage eventStore

let! asserted =
    store.Assert(
        scopeId,
        { Subject = { Hierarchy = "geography"; Path = [ "uk" ] }
          Metric = MetricRef "revenue"
          Value = Scalar 1_250_000m
          Period = { From = q2Start; To = q2End; Label = Some "Q2-2026" }
          Method = Computed("sales.rollup", "1", paramHash)
          Evidence = { ResultRef = Some resultId; InputHashes = [ dataHash ]; TriggerRef = None }
          Confidence = None
          Disclosure = Surfaceable }
    )

// Reconstruct what we knew at a past transaction time (law L4):
let! asOfMay3 = store.Query(scopeId, FactQuery.forSubjectMetric subject metric |> FactQuery.asOf may3)
```

`ServerConfig.FactStore` (default `NoFactStore`) is the composition slot the
introspection manifest reports; `BlobFactStore.create` composes the store
directly today.

## See also

- [`docs/platform/facts.md`](../../docs/platform/facts.md) — the fact model,
  and what belongs in a fact vs a result vs a model artifact.
- [`docs/platform/metric-registry.md`](../../docs/platform/metric-registry.md)
  — the metric & subject registry a fact references (Phase 519).

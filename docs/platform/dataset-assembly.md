# Dataset assembly (transforms-as-data)

`DatasetAssemblyExecutor` produces model-ready dataset versions (Phase 448
vintages) from platform stores **declaratively** — a `DatasetAssemblySpec` is
serialisable data (source bindings + a closed transform list), and the executor
materialises it into a new immutable version whose metadata records the spec and
every source identity it read. Replaying the same spec against moved sources is
the mechanical "new vintage" path the out-of-time evaluation story depends on:
the spec hash is unchanged, only the source identities move, and the provenance
shows exactly what moved.

## What assembly does

- **Sources.** A `DatasetVersion` (an existing vintage), a `TimeSeriesRange`
  (Phase 439, optionally downsampled to a grain — the `Downsample` surface feeds
  `Resample`), or an `ExternalTable` (entity query / ingested table) resolved by
  a caller-supplied resolver wired into the executor.
- **Transforms (closed DU).** `Join` (declared keys, inner / left-outer),
  `Resample` (period grain + `Sum`/`Mean`/`Min`/`Max`/`Last`/`Count`), `Lag` /
  `Window` (shift & rolling over the `PanelPeriod` role, partitioned by the panel
  unit), `Filter` (the typed `DatasetFilter`), and `Split` (period cutoff or a
  deterministic unit-hash → named subsets, each landing as its own version).
- **Provenance.** Every produced version carries `assembly.specHash`,
  `assembly.subset`, and `assembly.sources` in its metadata. Because
  `DatasetVersion.Metadata` is an existing field, provenance is
  queryable/joinable with zero new machinery.

## Scope guard — assembly is plumbing, not features (plan risk #3, GP 1)

**Assembly rearranges and folds; it never computes a new derived value outside
the closed aggregate list.** The transform DU is deliberately small and has **no
case** for:

- interaction terms, polynomial / basis expansions;
- decays, adstocks, saturation / response curves;
- categorical encodings (one-hot, target, hashing beyond the split key);
- imputation strategies beyond structural nulls;
- any domain feature engineering.

These are a **provider or consumer concern**, not a forge-substrate concern
(plan decisions D1 / D10, GP 1 — forge owns contracts, data plumbing, and
lifecycle; algorithms live in provider companions, consumer apps, or external
workers). The boundary is enforced *by construction*: there is no expression
language and no user-supplied function anywhere in a spec, so a derived-value
computation simply cannot be expressed. A caller that needs one runs it in a fit
/ score provider (Phase 449 / 454) or an external worker over the assembled
vintage, and lands the result as its own dataset version.

This keeps assembly:

- **deterministic** — identical sources always yield identical output, so a
  replay is a faithful re-materialisation;
- **auditable** — the whole recipe is data recorded on the output;
- **portable** — any executor implementation honours the same spec, because the
  spec carries no code.

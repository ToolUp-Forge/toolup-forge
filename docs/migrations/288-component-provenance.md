# Phase 288 — component provenance in the manifest (`ComponentProvenance`) (consumer migration)

**What changes.** A new read-only accessor `ComponentProvenance.forApp : ServerApp -> Map<ComponentId,
ComponentProvenance>` records, per Phase 279 `ComponentId`, **which package / assembly + version** a
composed companion came from. It reuses the assembly-metadata introspection the Phase 9q
`ConfigDriftDetector` already performs and attaches that provenance to the composed surface — so the
Phase 280 `CompositionManifest` can answer "which nupkg provides this companion, at what version"
(supply-chain visibility, SBOM-adjacent, upgrade auditing).

**Read-only enrichment by id-join (no manifest-shape change).** The provenance map is keyed by the
*same* `ComponentId` the manifest's companion-slot entries carry, so it **attaches to** the manifest
by id-join without widening the `CompositionManifest` / `ComponentEntry` shape — a manifest read
without provenance is byte-for-byte unchanged (GP 11), and a deployment that never calls `forApp`
builds nothing (GP 13).

**Total (GP 4).** Resolution never throws: a type whose assembly metadata is missing resolves to
`ComponentProvenance.unknown`, so provenance can always be reported for every entry.

## The shape

```fsharp
type ComponentProvenance = { Package: string; Version: string; Assembly: string }
```

## Reading provenance

```fsharp
let provenance = ComponentProvenance.forApp app
// keyed by companion:<iface>/<sub-id> (multi-impl) or companion:<iface> (single-impl) —
// the same ids CompositionManifest.CompanionSlots carry.

ComponentProvenance.tryForComponent (ComponentId.forCompanionImpl "IAuditSink" "splunk") app
// -> Some { Package = "ToolUp.AuditSinks.SplunkHec"; Version = "0.9.4.0"; Assembly = "…" }

ComponentProvenance.forType  (impl.GetType())   // the resolution primitive (total)
ComponentProvenance.forInstance impl            // its instance form
```

## Verification

- `InProcess/ComponentProvenanceTests.fs`: a first-party companion reports the platform assembly + a
  real version; the provenance key id-joins the manifest companion-slot entry; `forType` / `forInstance`
  are total (null → `unknown`); an app that composed no companions yields an empty map (GP 13).

## Rollback

Stop calling `ComponentProvenance.forApp` — nothing else references it and no behaviour changes when
unused. Or revert the Phase 288 forge commit; no persisted state is involved.

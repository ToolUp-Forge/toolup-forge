# Phase 187 — Compliance evidence-pack generator

**Status:** additive, opt-in. Zero action required for an existing
deployment — the default is `NoEvidencePackGenerator` (registers nothing,
mounts no route), so boot path and wire are byte-for-byte unchanged until a
deployment composes a real generator.

## What changed

Phases 40 (`IArtefactSigner`) / 41 (`IFieldClassifier`) / 9 (`IAuditLog` +
`IEventStore`) / 9h.A (DSR pipeline) each shipped a *part* of a compliance
audit pack; no phase composed them into the single artefact an MRM /
compliance reviewer reads. Phase 187 adds `IEvidencePackGenerator` — pure
orchestration over those seams, introducing **no new infrastructure**.

New surface:

| Symbol | Tier | Notes |
|---|---|---|
| `EvidencePackRequest` / `EvidencePackManifest` / `EvidencePackEntry` / `EvidencePackClassification` / `EvidencePackResult` / `EvidencePackError` | Core (`EvidencePackTypes.fs`) | Request / signed-manifest / result value types. |
| `IEvidencePackGenerator` + `NoEvidencePackGenerator` | Core | Seam + disabled default. |
| `DefaultEvidencePackGenerator` (+ `create` / `createWithClock`) | Server (`EvidencePackGenerator.fs`) | Composes audit + classification + DSR + signing. |
| `EvidencePackGenerator.register` | Server | DI opt-in (mirrors `SignedExportBundle.register`). |
| `EvidencePackHandler.routes` | Server | `POST /_platform/evidence-pack`, admin-gated, opt-in. |

## How it composes

`DefaultEvidencePackGenerator.Generate` assembles a pack by reading
already-shipped seams:

1. **Audit slices** — `IEventStore.ReadBySource(scopeId, sourceModule)` for
   each `AuditSourceModules` entry → one `audit/<module>.json` segment.
2. **DSR records** — when `SubjectUserId` is `Some`, each registered
   `IDataExporter.Export(scopeId, subjectId)` → `dsr/<name>` segments
   (Article-15 shape).
3. **Classification sidecar** — `IFieldClassifier.Classify(entityName)` for
   each `EntityNames` entry → a `classification/sidecar.json` segment and the
   manifest's `Classifications` list.
4. **Manifest** — content-addresses every segment (`Name` + SHA-256 + size),
   sorted by name for deterministic bytes.
5. **Signature** — when an `IExportEnvelopeSigner` is composed, the manifest
   bytes are signed (detached JWS) through that neutral seam, yielding an
   `ExportSignature`. **The SDK core carries no signer dependency (GP 1)** —
   the signer is the same `IExportEnvelopeSigner` Phase 162 introduced, filled
   by `ToolUp.ArtefactSigning.SignedExportBundle.adapter` over an
   `IArtefactSigner`.

## Determinism + signing layering

The manifest is deterministic for a fixed request + fixed clock + fixed
underlying data (entries + classifications are sorted; the clock is injected).
`ManifestBytes` are the exact bytes the signature covers, so a verifier
re-checks the detached JWS against the public key the Phase 40
`/_platform/signing-key/{keyId}` endpoint serves.

**Test layering note.** `IEvidencePackGeneratorContract` (in
`ToolUp.Platform.Tests`) verifies the *composition* contract — determinism,
that the manifest (and only the manifest) is the signed payload, sidecar
fidelity, and content-addressing — using a recording fake signer.
`ToolUp.Platform.Tests` does not reference `ToolUp.ArtefactSigning`, and the
**crypto round-trip** (sign → `IArtefactVerifier.Verify`) is `IArtefactSigner`'s
own contract, already proven by `IArtefactSignerContract` in
`ToolUp.ArtefactSigning.Tests`. The two together cover the acceptance: a
configured generator signs the canonical manifest, and that signature verifies
under the active key.

## Opting in

```fsharp
// 1. Build the generator over composed substrate (signer optional).
let signer = Some (SignedExportBundle.adapter artefactSigner)   // or None
let generator =
    DefaultEvidencePackGenerator.create eventStore classifier exporters signer

// 2. Register it (mirrors SignedExportBundle.register).
EvidencePackGenerator.register services generator |> ignore

// 3. Mount the admin route alongside your other _platform admin endpoints.
//    choose [ EvidencePackHandler.routes; ...existing... ]
//    POST /_platform/evidence-pack  (body: EvidencePackRequest JSON)
```

The route resolves the generator from DI; when it is `NoEvidencePackGenerator`
or absent it returns `404 not enabled`, and it is gated in-handler on
`canModifyPlatformConfig` (platform admin) — the pack carries audit +
classification evidence and is never anonymous.

## Verification

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
runs `IEvidencePackGeneratorContract` — determinism, signature-over-manifest,
sidecar fidelity, content-addressing, and the disabled default.

## Rollback

Remove the `EvidencePackGenerator.register` call and drop
`EvidencePackHandler.routes` from the route group. The types are additive and
inert; the default `NoEvidencePackGenerator` produces nothing.

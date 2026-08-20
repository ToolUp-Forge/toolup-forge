# 656 — build transcript + signed deploy record

**What changes:** the deploy plane gains a way to bind *what was deployed* to *how it was built*.
Three new pieces of surface, all additive; `DeployManifest`, `DeploySummary`, `IDeployPipeline` and
every existing pipeline implementation are **unchanged**. A deployment that composes no sealer seals
nothing, stores nothing extra, and runs byte-for-byte as it did (GP 11, GP 13).

| Type / module | Tier | Purpose |
|---|---|---|
| `BuildTranscript` (+ `BuildToolchain`, `BuildDependency`, `BuildEntryPoint`) | `Platform.Core` | content-addressed record of a build's declared inputs |
| `DeployProvenance` (+ `DeployArtifactDigest`) | `Platform.Core` | deployed artifact digests, transcript digest, opaque upstream slot |
| `DeployRecord`, `DeployRecordSeal`, `SealedDeployRecord`, `IDeployRecordSealer` | `Platform.Core` | the deploy record and the sealing seam |
| `DeployRecords` (`transcriptDigest`, `canonicalBytes`, `artifactsUnder`, `verify`, …) | `Platform.Server` | digests + the verification walk |
| `DeployRecordSealer.overApplicationSigner` / `.ofProvider` | `ToolUp.ArtefactSigning` | the sealer over the application signing seam |

## What a transcript claims — and what it does not

A `BuildTranscript` records the inputs a build was **given**: toolchain identity and version, the
resolved dependency closure as an id + version + content-digest set, and the entry point with its own
content digest. `BuildTranscript.canonicalForm` sorts and de-duplicates the closure and length-frames
every field, so the same inputs canonicalise to the same text on any machine — the digest is a
function of the value, not of the resolver's ordering or the host's culture.

It does **not** claim the build is reproducible. Re-running the recorded inputs may still produce
different output bytes; bit-for-bit reproducibility is a strictly stronger property this record does
not establish. It does not claim the recorded inputs are true either — a transcript is a statement by
whoever produced it. Sealing makes that statement attributable and tamper-evident; it does not make
it correct.

## The opaque upstream-provenance slot

`DeployProvenance.UpstreamProvenanceDigest` is a nullable digest a deployment MAY fill to name
whatever upstream input produced the sources it was built from. **The platform stores it, covers it
with the seal, and reports it back verbatim — it never interprets it.** Nothing in this SDK branches
on its content, and nothing here encodes what produced it or which algorithm minted it. A consumer
that fills the slot owns its meaning end to end. Because the slot sits inside the canonical form, a
filled value is tamper-evident once the record is sealed; that is the whole of what the platform
offers, and a seal says nothing about whether the value was right in the first place.

## Why a new record rather than fields on `DeployManifest`

Three more fields on the manifest would have retyped its constructor and broken every consumer that
builds one literally, for the benefit of consumers that fill them — which, on the day this shipped,
was none. `DeployRecord` **embeds** the manifest instead, so the existing type is untouched.

## Adopting it

Nothing is required. To opt in:

```fsharp
open ToolUp.Platform
open ToolUp.ArtefactSigning

// 1. Record the build's inputs where the build happens.
let transcript =
    BuildTranscript.create
        { Name = "example-sdk"; Version = "10.0.203" }
        [ { Id = "Some.Package"; Version = "1.2.3"; ContentDigest = "…" } ]
        { Path = "src/Program.fs"; ContentDigest = "…" }

// 2. Record what was deployed.
let provenance =
    DeployProvenance.none
    |> DeployProvenance.withArtifacts (DeployRecords.artifactsUnder deployRoot)
    |> DeployProvenance.withTranscriptDigest (DeployRecords.transcriptDigest transcript)
    // optional, opaque, never interpreted by the platform:
    |> DeployProvenance.withUpstreamProvenanceDigest upstreamDigest

// 3. Compose the sealer over the application signer and hand it to the pipeline.
let sealer = DeployRecordSealer.ofProvider signingProvider

let pipeline =
    DefaultDeployPipeline.createSealed
        buildOrchestrator
        containerScheduler
        eventStore
        logger
        {
            Sealer = sealer
            Provenance = fun _summary -> async { return provenance }
        }
```

The substrate seals the record when a deploy succeeds and persists it beside the deploy's state
events. `pipeline.TryGetSealedRecord deployId` reads it back.

## Verifying, from outside

```fsharp
let! outcome =
    DeployRecords.verify
        sealer
        (DeployRecords.locateUnder deployRoot)
        (Some transcript)          // None skips the transcript question rather than answering it
        sealedRecord
```

`verify` answers exactly three questions and accumulates every failure it finds: the seal covers the
record's canonical bytes; every recorded artifact is present and hashes to its recorded digest (a
mismatch **names the file**); and the recorded transcript digest is the digest of the transcript
supplied. `DeployRecordVerificationFailure.describe` renders any failure for an operator.

## Verification steps

1. `dotnet build ToolUp.Forge.sln`
2. `dotnet run --project Build.fsproj -- VerifyAll` — the `Platform` pack covers determinism in both
   directions, the tamper walk, and the null-coercion read path; the `ArtefactSigning` pack covers
   sealing over a real signer, the purpose binding, and key revocation.

## Rollback

Delete the sealer from the composition. The pipeline's four-argument constructor and
`DefaultDeployPipeline.create` are unchanged and seal nothing; persisted seal events are inert to a
deployment that stops reading them.

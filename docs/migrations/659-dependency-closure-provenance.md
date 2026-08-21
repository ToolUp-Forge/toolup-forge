# 659 — the dependency-closure upstream-provenance join

**What changes:** the build-transcript substrate (656) gains a structured answer to *"which upstream
releases does this build stand on?"* All additive; every 656 type, the pipeline's constructors and
`DeploySealingOptions` are **unchanged**. A deployment that captures no closure and registers no
provider behaves byte-for-byte as it did (GP 11, GP 13).

| Type / module | Tier | Purpose |
|---|---|---|
| `DependencyClosure` (+ `DependencyClosureEntry`, `ClosureAttestation`, `UnattestedReason`, `UpstreamReleaseReference`) | `Platform.Core` | content-addressed record of the resolved dependency closure, with per-entry upstream attestation |
| `IUpstreamReleaseProvider` (+ `UpstreamReleaseCoverage`) | `Platform.Core` | the seam an upstream release ledger implements to answer per-entry coverage |
| `RestoreClosures.readAssetsFile` | `Platform.Server` | capture the closure from the restore's own assets output — observed, never re-derived |
| `DeployRecords.closureDigest` / `.withClosure` / `.verifyClosure` | `Platform.Server` | the closure's digest, its binding into the sealed record, and the checker's join question |

## The shape of the join

Each closure entry carries the package id, the exact resolved version, the **source it resolved
from** (as the restore's own output recorded it), the content hash the restore recorded, and an
attestation: `AttestedBy { OpId; ActDigest }` — a typed reference to a release act in an upstream
ledger — or `Unattested` with a **distinguishable reason**: no provider was registered, the ledger
does not track the package (an external package), no release act covers this version, or the
provider failed (with its own reason). No entry is ever silently dropped: a closure that listed only
its attested members would read as complete and would not be.

The provider is a seam, not a dependency. With no provider registered, every entry is honestly
unattested; when an upstream ledger exists, it activates the join by implementing
`IUpstreamReleaseProvider` — no change to this SDK.

## How the closure joins the sealed record

`DeployRecords.withClosure` fills the deploy record's existing upstream-provenance slot with the
closure's digest — a digest in a slot that already existed, no new record shape. Because the slot is
inside the sealed canonical form, a deploy whose build resolved a different closure is a different
record, and the seal refuses the substitution. The platform still never interprets the stored value;
`DeployRecords.verifyClosure` answers the join question only when a checker supplies the closure to
ask it with, and filling the slot with something else remains as legitimate as it was.

## Adopting it

Nothing is required. To bind a build's closure:

```fsharp
open ToolUp.Platform

// 1. Capture — from the restore's own output, observed not re-derived.
let closure =
    match RestoreClosures.readAssetsFile "obj/project.assets.json" with
    | Ok closure -> closure
    | Error reason -> failwith reason   // a missing/malformed restore output is an error, never "no dependencies"

// 2. Attest — against a registered provider, or none (honestly unattested).
let! attested = DependencyClosure.attest upstreamProvider closure   // upstreamProvider: IUpstreamReleaseProvider option

// 3. Record — the transcript carries the same resolved set; the closure's digest joins the sealed record.
let transcript =
    BuildTranscript.create toolchain (DependencyClosure.toBuildDependencies attested) entryPoint

let provenance =
    DeployProvenance.none
    |> DeployProvenance.withArtifacts (DeployRecords.artifactsUnder deployRoot)
    |> DeployProvenance.withTranscriptDigest (DeployRecords.transcriptDigest transcript)
    |> DeployRecords.withClosure attested
```

Hand `provenance` to the sealing pipeline exactly as in 656 (`DefaultDeployPipeline.createSealed`).
`ClosureAttestation.describe` / `UnattestedReason.describe` render each entry's standing for an
operator.

## Verifying, from outside

```fsharp
DeployRecords.verifyClosure closure sealedRecord.Record.Provenance
```

`Ok` means the record's upstream-provenance slot carries this closure's digest. `ClosureNotRecorded`
and `ClosureDigestMismatch` are reported as themselves; a mismatch does not distinguish "the slot was
filled with something else" from "a different closure" — the record cannot say which.

## Verification steps

1. `dotnet build ToolUp.Forge.sln`
2. `dotnet run --project Build.fsproj -- VerifyAll` — the `Platform` pack probes the closure's
   canonical form in both directions, the restore-output capture (including the missing/malformed
   error paths), the attest seam provider-absent and against a stub ledger, and the seal's refusal
   of a substituted closure.

## Rollback

Stop calling the three new steps; the 656 flow is untouched. A persisted record whose slot carries a
closure digest stays verifiable by anyone holding the closure, and inert to everyone else.

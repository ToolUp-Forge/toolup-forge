# Migration — the joined evidence chain as a signed statement

**Phase 714.** Additive and opt-in. Nothing is composed, registered, mounted or started by this
phase; a deployment that never exports a bundle is byte-for-byte unchanged (GP 11 / GP 13), and
there is nothing to migrate unless you want the artefact.

## What changes

Phase 713 walks a deployment's evidence chain and returns it as a value. That value is useful to
whoever is holding the deployment and useless to anybody else: nothing addresses it, nothing states
what it does and does not claim, and the only thing that could check it is the deployment that
produced it — which is the one party a counterparty is trying not to have to take on trust.

This phase turns the walk into an artefact:

```
EvidenceChain  →  EvidenceBundle  →  in-toto Statement  →  DSSE envelope
                  (content-addressed)  (predicate)          (one signature)
```

and ships a verifier for it that needs no platform at all.

## The predicate type

```
https://toolup-forge.io/attestations/evidence-chain-bundle/v1
```

**Its own type, deliberately.** A bundle is a different claim from a grounding certificate and a
different claim from an audit-ledger segment, even where the three quote overlapping records: a
certificate says what a value was grounded in, a segment says what a party's slice of a chain
contains, and a bundle says which records this deployment observed and how they join. A verifier
keys on `predicateType` to decide what shape it is about to read, so publishing two shapes under one
URI would make that key meaningless — which is what `EnvelopePredicateTypeMismatch` exists to
report, and what stock tooling reports too (see the demonstration below).

## Exporting

```fsharp
open ToolUp.Platform

// A walked chain, from the Phase 713 seam.
let! chain = EvidenceChainWalker.run services mode request

// Package it. `observer` is this deployment's own opaque id.
let bundle = EvidenceBundleExport.bundleOf "deployment-id" DateTime.UtcNow chain

// Sign it through the envelope seam the SDK already ships. The signing
// companion fills `IStatementEnvelopeSigner` from the same key material,
// key id and algorithm the deployment's artefact signer uses.
let signer = DsseEnvelopeSigning.fromSecretStore secrets keyId EcdsaP256
let! envelope = EvidenceBundleExport.export signer bundle

// Two files: the envelope, and the canonical bytes its subject addresses.
File.WriteAllText("bundle.dsse.json", DsseEnvelope.toJson envelope)
File.WriteAllBytes("bundle.canonical.txt", EvidenceBundleExport.canonicalBytes bundle)
```

`EvidenceBundleExport.bundleFor services mode request observer` does the walk and the packaging in
one call, and records the audited read exactly as any other walk does — producing evidence leaves
evidence, and exporting it does not become a way to walk the chain unobserved.

## The claim boundary is carried, not documented

A bundle states what it does not prove **as data, on every bundle, clean ones included**. Five
statements ship, each able to name the substrate that narrows it in this deployment:

| Id | Bound |
|---|---|
| `records-not-truth` | the bundle attests observation of records, never their truth |
| `work-quality-not-claimed` | a resolved work record names the work, not its quality |
| `uncomposed-substrate-is-silent` | an absent hop is a bound on the bundle, not a clean bill |
| `code-never-composed-behaved` | recorded artefacts are compared to each other; no running behaviour is observed |
| `signature-binds-the-document` | the outer signature binds this document, not the inner attestations it quotes |

**Narrowing is never closing.** A chain whose hops all resolve shrinks the first statement to name
what the joins do cover and leaves it standing; the statement count is the same on a complete chain
as on an empty one. A caveat that appears only on failures is a caveat nobody reads, so it prints on
a pass too — including in the verify command's own output.

## The ruling: nested attestations are **carried verbatim**, never re-signed

Several of the chain's hops name an artefact that already carries somebody's signature — a deploy
record's seal, a signed evidence-pack manifest, a signed ledger head. A bundle that nests those has
two options and must pick one, because a verifier must not have to know which producer wrote the
document in order to know what it means.

**The ruling is: transcode.** Each inner attestation is carried exactly as the walk recorded it and
the bundle adds **one** outer signature over the whole. The bundle declares this in the document as
`NestedAttestationDisposition = "carried-verbatim"`, and the verifier **refuses** a document
declaring anything else, naming what it read. The choice is therefore readable from the artefact
rather than known about the producer, and adopting the other option later would be a new
disposition and a new verifier leg — never a silent change of meaning under the same shape.

`EvidenceBundleExportTests` pins it: the disposition is on the wire, the three inner references
reach the predicate byte-identically, the export carries exactly one signature, and a bundle
declaring `re-signed` is refused at `bundle/nestedAttestationDisposition`.

### Pricing the rejected option

Re-signing — extract the inner content, drop its signature, assert it afresh under the bundle key —
is not obviously worse, and it buys three real things:

- **One key for the holder.** A counterparty resolves a single public key and is done, instead of
  resolving the bundle key plus whichever keys the inner attestations were minted under.
- **A smaller document**, since inner signature material need not travel.
- **A uniform verification path**, with no per-inner-artefact scheme for a holder to implement.

It costs more than it buys, in three ways that are not recoverable afterwards:

- **It converts an observation into an origin claim, in the one act that should preserve the
  difference.** The surviving signature would say *this deployment asserts these upstream facts*
  where the record said *this deployment observed that somebody else asserted them*. No later reader
  can recover which was meant, because the evidence that distinguished them is the thing that was
  discarded. That is precisely the claim boundary this artefact exists to hold.
- **It makes a compromised bundle key sufficient to manufacture upstream attestations.** Under
  transcode, forging an inner attestation needs the inner key; under re-signing, the bundle key
  alone produces a document indistinguishable from a genuine one.
- **It makes the bundle unreconcilable with upstream after the fact.** A holder who later obtains
  the upstream artefact can, under transcode, check the two against each other. Under re-signing
  there is nothing to check against — the digests would be the bundle's own.

The convenience the rejected option buys is real and is addressed differently: the outer signature
is a standard DSSE one, so a holder who wants a single-key answer gets one for the *document*, and
the inner attestations remain separately checkable by anyone who cares to.

## Verifying — two checks, and they answer different questions

**Structural, offline, pure.** `EvidenceBundle.verifyWith : (string -> string) -> EvidenceBundle ->
BundleIntegrity` lives in `ToolUp.Platform.Core` and takes its digest function as an argument, so it
carries no cryptography dependency and runs on any host. It establishes that the schema is one it
knows, the nested-attestation ruling is the recognised one, the chain carries the full hop set in
order and correctly ordinalled, the chain's outcome is the fold of its own hops, the chain's verdict
digest is the digest of its own canonical form, the claim boundary is present, and the content id is
the digest of the whole. Every failure is `BrokenAt (position, reason)` naming a stable structural
coordinate — `bundle/chain/hops[3]`, `bundle/contentId`, `document/subject`.

`BrokenAt` also covers *I cannot check this*: a bundle written under a later schema is reported
broken with a reason saying exactly that, never `Intact`.

**It establishes nothing about the signature, and nothing about whether the records are true.** A
wholly fabricated bundle passes the structural check, and passes it honestly — *this is a well-formed
bundle* and *this bundle is yours* are different questions and only the second needs a key. The
verify command prints that sentence on every run, pass included.

**Signature: use any DSSE verifier.** The envelope is a standard DSSE-wrapped in-toto Statement, so
there is nothing bespoke for a counterparty to install or trust.

## The one command

```fsharp
let run = EvidenceBundleExport.verifyCommand (File.ReadAllText "bundle.dsse.json")
Console.Out.Write run.Report
exit run.ExitCode          // 0 intact, 1 otherwise
```

The whole input is the document — no deployment, no store, no network, no configuration, no key.
A composition root, a CI job and a counterparty all invoke the same function.

## Cold verification, without the SDK at all

`probes/evidence-bundle-cold-verify.fsx` is the same check performed by a party who has none of
this built:

```powershell
$env:NUGET_PACKAGES = "<a fresh empty directory>"
dotnet fsi probes/evidence-bundle-cold-verify.fsx bundle.dsse.json
```

It `#load`s three shared-tier source files, reads the wire format with the BCL, and re-derives the
document-level checks from the format rather than deserialising through this SDK's converter set —
so a defect in that converter cannot make the probe agree with the exporter. Its output is
**byte-identical** to the shipped verify command's, because the report is rendered by
`EvidenceBundle.verificationReport` in the shared tier and nothing in it reads a clock, a store or a
key.

Measured 2026-08-26 on Windows: an empty package cache (0 entries) with no deployment running,
`dotnet fsi` over a fixture bundle → exit 0, 3,147 bytes, SHA-256
`b370d66f5daf51a6e1aeb0945f185458c98efee0b4deda5b403d43f7f6e4a479` — identical to the warm
in-process run. The same probe over a tampered document exits 1 and names
`bundle/nestedAttestationDisposition`, so the green was earned rather than vacuous.

## Stock tooling — demonstrated, not asserted

Verified 2026-08-26 with an **unmodified cosign v2.6.5** (go1.26.5, windows/amd64) against a fixture
emitted by the shipped test pack, over an ECDSA P-256 key:

```
cosign verify-blob-attestation --key bundle.pub.pem --signature bundle.dsse.json \
  --type https://toolup-forge.io/attestations/evidence-chain-bundle/v1 \
  --insecure-ignore-tlog=true bundle.canonical.txt
```

| Case | cosign result | exit |
|---|---|---|
| well-formed bundle | `Verified OK` | 0 |
| signed payload altered | `accepted signatures do not match threshold, Found: 0, Expected 1` | 1 |
| canonical blob altered | `no matching subject digest found` | 1 |
| wrong `--type` | `invalid predicate type, expected … got …` | 1 |

The third row is why the canonical bytes are written beside the envelope: the content id **is** the
SHA-256 of those bytes, so a stock verifier claim-checks the subject as well as the signature with
nothing that understands this SDK. The fourth row is the predicate-identity ruling being load-bearing
to a tool nobody here controls.

`--insecure-ignore-tlog=true` is required because these bundles are not logged to a transparency
service; that is a deployment's choice to make and outside this substrate.

Re-emit the fixture with:

```powershell
$env:TOOLUP_BUNDLE_FIXTURE_DIR = "<a directory>"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter "ToolUp.Platform.Tests.Phase 714"
```

## Extending the claim

`BundleClaimQualifier` is the additive slot for a later typed verdict about the walk. Qualifiers
render **last** in the canonical form, so adding one appends lines and moves nothing before them: a
reader diffing two canonical forms across an upgrade can tell a growth from a re-statement. Use it
rather than widening the chain or the not-proved list.

## Rollback

Nothing is composed, so there is nothing to roll back. Stop calling the exporter and the deployment
is byte-for-byte what it was. Bundles already issued remain verifiable — the verifier's inputs are
the document and a hash.

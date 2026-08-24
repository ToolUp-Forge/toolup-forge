# Migration — certificate-verified fact import (Phase 683)

**Status:** net-new, opt-in. Nothing is composed unless you call `FactsCompose.withFactImport`, and a deployment that does not is byte-for-byte unchanged (GP 11 / GP 13). **No consumer action is required to upgrade.**

## Why

`MethodRef.Imported of certificateRef` has existed since the fact store shipped, and grounding certificates have embedded imported-cert refs since they shipped — but nothing verified a certificate **at import**. An imported fact was therefore testimony wearing a provenance field: the ref named a document nobody had checked, asserted by whoever called `Assert`. Since the weakest fact in a chain is what the chain is worth, a fact base that accepts unverified imports has no grounding claim left.

`FactImport` is the door. It verifies a peer's certificate offline against key material held **for that peer**, re-derives the content-addressed fact id from the offered identity tuple and refuses on mismatch, maps the peer's disclosure stance conservatively, and only then asserts with `Imported` provenance carrying the verified certificate's ref. Every other outcome is a typed refusal, audited, with nothing written to the store.

## What is new

All in `ToolUp.Facts.Server` (`Server/FactImport.fs`), plus two `AuditEvent` cases in `ToolUp.Platform.Core`.

| Type | Purpose |
|---|---|
| `PeerTrustAnchor` | One peer's `PublicKeyMetadata` plus an optional `DisclosureCeiling`. Built with `PeerTrustAnchor.create` / `withCeiling`. |
| `ImportedFactOffer` | The identity tuple + value a peer offers. `ImportedFactOffer.ofFact` is the exporting side's projection; `derivedFactId` is the id the door re-derives. |
| `FactImportRefusal` | Seven distinct refusals + `describe`. |
| `IFactImportDoor` | `Import(scopeId, peerId, offer, certificateJson) : Async<Result<Fact, FactImportRefusal>>`. |
| `FactImport.create` / `certificateRef` | Construction, and the content-addressed ref recorded as provenance. |
| `Disclosure.tryParse` / `Disclosure.floor` | The exact inverse of `Disclosure.toString`, and the conservative meet. (`ToolUp.Facts.Core`.) |
| `AuditEvent.FactImportAccepted` / `FactImportRefused` | One `FactImportPayload` row per attempt, either verdict. |

## Wiring

```fsharp
open ToolUp.Facts

// The peer's public key — the whole of what offline verification needs.
// It arrives as data (a published key document, an operator-installed
// file); nothing here resolves a key from a store.
let partner: PeerTrustAnchor =
    PeerTrustAnchor.create "partner-a" partnerPublicKey
    |> PeerTrustAnchor.withCeiling (Restricted "policy:third-party")   // optional

ServerApp.empty
|> ServerApp.withStorage blob
|> FactsCompose.withFactStore
|> FactsCompose.withFactImport [ partner ]
|> ServerApp.run
```

Then resolve `IFactImportDoor` from DI and hand it the offer plus the certificate document:

```fsharp
match! door.Import(scopeId, "partner-a", offer, certificateJson) with
| Ok fact -> ()                                   // asserted, with Imported provenance
| Error refusal -> log.Warn(FactImportRefusal.describe refusal)   // nothing was stored
```

The exporting side needs no new code: it issues a certificate as it already does and exports it with `CertificateEnvelope.export`, then `DsseEnvelope.toJson`.

## Four things worth knowing before you compose it

1. **Key material is per-peer and explicit.** The certificate's own detached-JWS seal verifies through `IArtefactVerifier`, which resolves keys out of *this* deployment's secret store — exactly the ambient trust the door exists to avoid. So the door takes the DSSE-wrapped statement form and a `PublicKeyMetadata` per peer. No key is discoverable, so no key is implicitly trusted, and which peers you accept facts from is readable off your composition root.

2. **The id check is what makes any of this checkable.** A `FactId` is `hash(subject, metric, period, method, inputHashes)` and therefore deployment-independent. The door recomputes it from the offer and compares it to the certificate's root, so a peer cannot hand over a fact whose identity tuple differs from the one their certificate covers. Altering any single field of the offer lands as `ImportContentIdMismatch`, not in the store.

3. **The imported fact gets a NEW local id.** The method is part of the content address, and the local assertion's method is `Imported ref` rather than the peer's. That is deliberate: this deployment is asserting *that a peer computed something*, not that it computed it. The certificate ref is the join between the two ids, and a re-issued certificate's chain names it.

4. **An import can narrow disclosure and can never widen it.** The peer's stance is read out of the **signed** certificate, not out of the offer beside it, and the effective stance is `Disclosure.floor declared ceiling`. A ceiling of `Surfaceable` is the identity, so an anchor that declares none leaves the peer's stance exactly as sealed. Two `Restricted` stances under different policy refs meet at `Internal`: they are incomparable, and picking either would assert a permission neither side granted. A fact the peer's **own** export door withheld is refused outright (`ImportWithheldByPeer`) — importing it would be the widest widening available.

## Audit

Every attempt records one row through the composed `IAuditLog`, on the accepted verdict too — a trail carrying only refusals cannot distinguish a deployment whose imports were all sound from one whose door was never composed. `FactImportPayload` carries the peer, its key id, the certificate root and ref, the derived and imported ids, the subject and metric, and **both** stances. The pair is the claim: a row whose `EffectiveDisclosure` is more permissive than its `DeclaredDisclosure` is a defect visible from the trail alone. PII-free — the fact's value rides neither the row nor the certificate.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — 0 failures. New coverage in `ToolUp.Platform.Tests` (`InProcess/FactImportTests.fs`, 19 cases): two real deployments exchanging a fact and re-certifying so the chain names the imported ref; the disclosure floor in both directions, plus its lattice laws; one distinct refusal per failure class (unknown peer, wrong peer's key, edited certificate, genuine certificate over another fact, every single-field offer mutation, a peer-withheld fact, a certificate over a non-fact, an unreadable stance, an unreadable document) — each asserting the importing store is still **empty**. The last three are reached with **genuinely sealed** hand-built bodies, so they prove the structural guards rather than the signature check firing twice.

## Rollback

Entirely additive. To revert, drop the `FactsCompose.withFactImport` call from your composition root; the door, its types and the two audit cases become unreachable and cost nothing. No existing signature changed, and no default path was touched.

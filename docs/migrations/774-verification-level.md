# Migration — Phase 774: the verification level on the boot seal

**Status:** a schema bump on the sealed deploy record (`DeployRecord.SchemaVersion` 1 → 2), plus
fields and a verdict case on the boot preflight's records and unions. Every record sealed before this
phase still verifies, byte for byte. No consumer action is needed to keep booting; a few constructions
need one more field to keep compiling (below).

## What changed

- **`VerificationLevel` (Core).** A ladder: `Attributed` (a signature attributes the record to a key
  holder, and nothing more), `IsolatedSigner` (the signing key never left an isolated signing
  environment), `RuntimeQuoted` (a runtime quote from the host covers the running binary).
- **`DeployRecord.VerificationLevel`, inside the signed bytes.** `DeployRecord.create` stamps schema 2
  at `Attributed`; `DeployRecord.withVerificationLevel` raises it, before sealing. Changing the level
  after sealing, or re-labelling the record to schema 1 to drop it, breaks the seal.
- **Old records verify as before.** A schema-1 record canonicalises to exactly the bytes it was sealed
  over, and `DeployRecord.verificationLevel` reads it as `Attributed`, *inferred*: the report says
  `attributed (inferred: pre-level schema)`, never `declared by the record`.
- **`IRuntimeAttestor` (Core)** is the seam for the top rung. It returns a level and an opaque evidence
  digest, never a quote payload. The only in-tree provider, `RuntimeAttestor.unattested`, observes
  nothing. A record on its own is credited with at most `IsolatedSigner`, because a deploy-time record
  cannot carry a quote of the binary now running.
- **The boot preflight's fifth question.** `BootVerificationOptions` gains `MinimumLevel` (a floor,
  `None` for none) and `Attestor`. `BootVerificationResult.Level` reports the level the boot is credited
  with and where it came from: declared by the record, inferred from an old schema, or observed by the
  attestor. A floor the level does not reach turns an affirmative verdict into the new
  `BootVerificationVerdict.BelowVerificationFloor`, naming both. Under `CompositionProfile.Verified` that
  refuses the start; under `Standard` the policy decides, as for any other finding.
- **The report's boot-seal section renders the level and its provenance** (`BootSealVerified` and
  `BootSealVerifiedUnrecorded` carry it). A below-floor boot is a rejection, and its finding names both.

## What can break

- A full-literal `BootVerificationOptions` needs `MinimumLevel = None` and
  `Attestor = RuntimeAttestor.unattested`. A full-literal `BootVerificationResult` needs `Level`, and a
  full-literal `DeployRecord` needs `VerificationLevel`. Each is FS0764 until then.
- An exhaustive `match` on `BootVerificationVerdict` must handle `BelowVerificationFloor`.
  `label`, `findings` and `describe` already do.
- A pattern on `BootSealVerified` / `BootSealVerifiedUnrecorded` gains one more element (`level`).
- Code that pinned the canonical bytes or digest of a NEWLY created record will see them move once.
  Bytes of records already sealed do not move.

## Re-sealing existing deployments

Nothing forces a re-seal: an old seal still verifies, at `Attributed` (inferred). To have the record
*declare* its level, rebuild the record at schema 2 and seal it again, then re-mint the composition
binding (it names the record's digest):

```fsharp skip=fragment
let record =
    DeployRecord.create deployId tenantId buildId manifest provenance
    |> DeployRecord.withVerificationLevel VerificationLevel.IsolatedSigner // only if it is true
let! sealedRecord = sealer.Seal(DeployRecords.canonicalBytes record)
let! sealedBinding = BootVerificationPreflight.sealBinding sealer (BootVerificationPreflight.bindingFor record composition)
```

## The honest reading of `Attributed`

`Attributed` says who signed. It does not say the key was protected, that the host was trustworthy,
or that the binary running is the one the record names. A developer keyring reaches exactly this rung,
and so does every record sealed before this phase. With the in-tree provider every deployment reads
`Unattested` at runtime and `Attributed` from its record. That is the correct claim, not a gap in it:
the stronger rungs exist only when a sealer or an attestor can actually stand behind them.

## Rollback

Leave `MinimumLevel = None`: the level is read and reported and refuses nothing. A schema-2 record
cannot be read by a build older than this phase, which would compute schema-1 bytes for it and fail the
seal, so roll the sealing side back before the verifying side.

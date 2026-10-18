// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text

// ─── Deploy record ───────────────────────────────────────────────────
//
// A `DeployManifest` says what a deployment ASKED FOR. A `DeploySummary`
// says how far the pipeline GOT. Neither says what was actually
// deployed, or what it was built from — so nothing today binds a
// running deployment to the inputs that produced it.
//
// A **deploy record** is that binding: the manifest as deployed,
// carried verbatim, plus a `DeployProvenance` recording the deployed
// artifact digests, the build-transcript digest, and the opaque
// upstream-provenance slot. Sealed (below), it becomes a statement a
// relying party can check without trusting the deployment that made it.
//
// **A superset, not a widening — deliberately.** The obvious shape
// would have been three more fields on `DeployManifest`. That would
// have retyped the manifest's constructor and broken every consumer
// that builds one literally, for the benefit of consumers that fill the
// new fields — which, on the day this shipped, was none of them. As a
// separate record that EMBEDS the manifest, the existing type is
// untouched, an existing consumer keeps compiling, an existing
// deployment keeps serialising byte-for-byte identical manifests, and
// nothing pays for a feature it has not opted into (GP 11 / GP 13).
//
// **Sealing is a seam, not a dependency.** `IDeployRecordSealer` takes
// bytes and returns an opaque seal; the signing implementation lives in
// the signing companion, above this tier. That keeps the substrate free
// of any crypto surface, and keeps the deploy plane composable with a
// sealer this SDK never shipped.

// ─── Phase 774 — the verification level ──────────────────────────────
//
// A sealed record says WHAT was deployed, and the boot preflight says the
// running composition MATCHES it. Neither said under what environment
// guarantee either statement was made, so a reader could not tell a seal
// minted with a developer's keyring from one minted by a key that never
// left an isolated signer, and had no place to record that a runtime quote
// covered the running binary. The level below is that place.
//
// **A ladder, read conservatively.** Each rung includes the ones below it.
// A record can declare only what was true when it was SEALED, so the
// strongest thing a record can be credited with is `IsolatedSigner`:
// `RuntimeQuoted` is a statement about the binary now running, and only a
// runtime attestor observing it at boot can make that statement. The level
// a boot reports is therefore never more than the record's credit or the
// attestor's observation, whichever is higher.
//
// **The level is inside the signed bytes** (`DeployRecord.canonicalForm`),
// so a record cannot be re-labelled after sealing: changing the level, or
// the schema version that says whether a level is recorded at all, breaks
// the seal.

/// How strong an environment guarantee stands behind a sealed deploy
/// record and the boot that verified it. Ordered: each case includes the
/// ones before it.
[<RequireQualifiedAccess>]
type VerificationLevel =
    /// A signature attributes the record to a key holder, and nothing more.
    /// What a developer keyring gives, and what every record sealed before
    /// the level existed is read as.
    | Attributed
    /// The signing key never left an isolated signing environment.
    | IsolatedSigner
    /// A runtime quote from the host covers the running binary. Only a
    /// runtime attestor can establish this, at boot.
    | RuntimeQuoted

/// What a runtime attestor reported about the process it runs in.
///
/// **Never a quote payload.** The seam carries a level and an opaque
/// evidence digest; a verifier that wants the quote itself fetches it from
/// wherever the attestor keeps it, by that digest.
[<RequireQualifiedAccess>]
type RuntimeObservation =
    /// The attestor observed nothing it can vouch for. `reason` says why.
    | Unattested of reason: string
    /// The attestor observed `level`, evidenced by the opaque
    /// `evidenceDigest`.
    | Observed of level: VerificationLevel * evidenceDigest: string

/// The seam a deployment composes to have the running binary vouched for
/// at boot.
///
/// The first and only in-tree provider is `RuntimeAttestor.unattested`,
/// which honestly observes nothing. A provider backed by a real runtime
/// quote implements this and is composed through
/// `BootVerificationOptions.Attestor`.
///
/// GP 12: identity by value (a digest string in, a value out), async at
/// the boundary, failure as data (`RuntimeObservation.Unattested`, never
/// an exception), stateless between calls.
type IRuntimeAttestor =
    /// The attestor's own name, recorded beside what it observed.
    abstract Name: unit -> string

    /// Observe the running process. `deployRecordDigest` is the digest of
    /// the sealed record's canonical bytes, so a provider can bind its
    /// quote to the record it is vouching for.
    abstract Observe: deployRecordDigest: string -> Async<RuntimeObservation>

/// Where the level a boot reports came from.
[<RequireQualifiedAccess>]
type VerificationLevelProvenance =
    /// The sealed record declares it, inside its signed bytes.
    | DeclaredByRecord
    /// The record was sealed under a schema that carried no level, so the
    /// level is inferred, never read. `schemaVersion` names that schema.
    | InferredFromSchema of schemaVersion: int
    /// A runtime attestor observed it at boot.
    | ObservedByAttestor of attestor: string * evidenceDigest: string

/// The level a boot reports, with where it came from and what the runtime
/// attestor said.
type VerificationLevelReading = {
    /// The level this boot is credited with.
    Level: VerificationLevel
    /// Where `Level` came from.
    Provenance: VerificationLevelProvenance
    /// What the sealed record declares (or is inferred to declare).
    Declared: VerificationLevel
    /// The runtime attestor consulted.
    Attestor: string
    /// What it observed, recorded whether or not it raised the level.
    Observation: RuntimeObservation
}

[<RequireQualifiedAccess>]
module VerificationLevel =

    /// Position on the ladder. `Attributed` is 0.
    let rank =
        function
        | VerificationLevel.Attributed -> 0
        | VerificationLevel.IsolatedSigner -> 1
        | VerificationLevel.RuntimeQuoted -> 2

    /// Whether `level` is at or above `floor`.
    let meets (floor: VerificationLevel) (level: VerificationLevel) : bool = rank level >= rank floor

    /// Stable lowercase label. Part of a sealed record's canonical bytes,
    /// so never change one.
    let label =
        function
        | VerificationLevel.Attributed -> "attributed"
        | VerificationLevel.IsolatedSigner -> "isolated-signer"
        | VerificationLevel.RuntimeQuoted -> "runtime-quoted"

    /// The most a sealed record can be credited with on its own say-so.
    /// A record is written before the binary it describes runs, so it
    /// cannot carry a runtime quote of it; a record declaring
    /// `RuntimeQuoted` is credited as `IsolatedSigner`, the strongest
    /// statement a sealing environment can make.
    let recordCredit (declared: VerificationLevel) : VerificationLevel =
        match declared with
        | VerificationLevel.RuntimeQuoted -> VerificationLevel.IsolatedSigner
        | other -> other

[<RequireQualifiedAccess>]
module RuntimeAttestor =

    /// Name of the in-tree provider.
    [<Literal>]
    let UnattestedName = "unattested"

    /// The in-tree provider: observes nothing, and says so. Composing it
    /// is the honest default: a boot under it reports exactly what the
    /// sealed record is credited with, never more.
    let unattested: IRuntimeAttestor =
        { new IRuntimeAttestor with
            member _.Name() = UnattestedName

            member _.Observe _ = async {
                return
                    RuntimeObservation.Unattested
                        "no runtime attestor is composed, so nothing vouches for the running binary"
            }
        }

[<RequireQualifiedAccess>]
module VerificationLevelReading =

    /// Read the level a boot is credited with: the record's credit, raised
    /// only by what the attestor actually observed.
    let read
        (declared: VerificationLevel)
        (declaredProvenance: VerificationLevelProvenance)
        (attestor: string)
        (observation: RuntimeObservation)
        : VerificationLevelReading =
        let credit = VerificationLevel.recordCredit declared

        let level, provenance =
            match observation with
            | RuntimeObservation.Observed(observed, digest) when
                VerificationLevel.rank observed > VerificationLevel.rank credit
                ->
                observed, VerificationLevelProvenance.ObservedByAttestor(attestor, digest)
            | _ -> credit, declaredProvenance

        {
            Level = level
            Provenance = provenance
            Declared = declared
            Attestor = attestor
            Observation = observation
        }

    /// The provenance as a short phrase, e.g. `inferred: pre-level schema`.
    let describeProvenance =
        function
        | VerificationLevelProvenance.DeclaredByRecord -> "declared by the record"
        | VerificationLevelProvenance.InferredFromSchema _ -> "inferred: pre-level schema"
        | VerificationLevelProvenance.ObservedByAttestor(attestor, digest) ->
            $"observed by runtime attestor '{attestor}', evidence {digest}"

    /// One line naming the level and its provenance, e.g.
    /// `attributed (inferred: pre-level schema)`, followed by what the
    /// attestor said and any credit cap applied to the record's claim.
    let describe (reading: VerificationLevelReading) : string =
        let head =
            $"{VerificationLevel.label reading.Level} ({describeProvenance reading.Provenance})"

        let capped =
            if VerificationLevel.recordCredit reading.Declared <> reading.Declared then
                $"; the record declares {VerificationLevel.label reading.Declared}, which a sealed record cannot evidence on its own"
            else
                ""

        let observation =
            match reading.Observation with
            | RuntimeObservation.Unattested reason ->
                $"; runtime attestor '{reading.Attestor}' observed nothing: {reason}"
            | RuntimeObservation.Observed(level, digest) ->
                $"; runtime attestor '{reading.Attestor}' observed {VerificationLevel.label level}, evidence {digest}"

        head + capped + observation

/// A deploy record: what was deployed, and what it was built from.
///
/// Identity by value throughout (GP 12 rule 1) — every field is a
/// string, an int, or a record of the same, so the whole record crosses
/// any boundary and round-trips through any serialiser.
type DeployRecord = {
    /// Schema version of the record shape. Bumped only when the
    /// canonical form changes, which by construction invalidates every
    /// existing seal — so bump deliberately, with a migration note.
    SchemaVersion: int
    /// The deploy this record describes.
    DeployId: DeployId
    /// The tenant the deploy belonged to.
    TenantId: TenantId
    /// The build the deploy ran from.
    BuildId: BuildId
    /// The manifest as deployed, carried verbatim so the record is
    /// self-contained: a checker holding the record needs nothing else
    /// to see what was asked for.
    Manifest: DeployManifest
    /// What was deployed, and what it was built from. `DeployProvenance.none`
    /// for a deploy that recorded nothing.
    Provenance: DeployProvenance
    /// Phase 774 — the environment guarantee the record was sealed under.
    /// Inside the signed bytes from `DeployRecord.LevelSchemaVersion` on.
    ///
    /// **Read it through `DeployRecord.verificationLevel`, never directly.**
    /// A record sealed before the field existed carries no level, so its
    /// value here is meaningless (a deserialiser yields `null`); the
    /// accessor reads that record as `Attributed`, inferred from its
    /// schema, and says so.
    VerificationLevel: VerificationLevel
}

/// An opaque seal over a deploy record's canonical bytes.
///
/// The substrate mints none of this itself: it hands the canonical
/// bytes to an `IDeployRecordSealer` and stores whatever comes back.
/// `Token` in particular is scheme-defined and **never parsed by the
/// deploy plane** — only the sealer that produced it, or another
/// implementation of the same scheme, can read it. That is what lets a
/// deployment seal with a scheme this SDK has never heard of.
type DeployRecordSeal = {
    /// The sealing scheme's own name, so a verifier can refuse a seal
    /// it does not implement rather than mis-parse it.
    Scheme: string
    /// Key identifier the seal was minted under. Carried for diagnostics
    /// and for operator-facing display; the sealer, not the substrate,
    /// decides what it means.
    KeyId: string
    /// What the seal claims about the environment that produced it, as
    /// the sealer's own label. Opaque to the substrate — a label is not
    /// evidence for the claim the label names, and no code here treats
    /// it as such.
    Claim: string
    /// The scheme-defined encoding of the signature. Opaque.
    Token: string
    /// When the seal was minted, as the sealer reported it.
    SealedAt: DateTimeOffset
}

/// A deploy record together with its seal. What an external checker is
/// handed, and the only shape `DeployRecords.verify` accepts.
type SealedDeployRecord = {
    Record: DeployRecord
    Seal: DeployRecordSeal
}

/// The seam a deployment composes to seal and verify its deploy
/// records.
///
/// Deliberately expressed over BYTES rather than over the record type:
/// the substrate computes the canonical bytes (so producer and verifier
/// can never disagree about what was covered) and the sealer decides
/// only how to sign them. An implementation therefore needs no
/// knowledge of the deploy plane, and the deploy plane needs no
/// knowledge of any signing library.
///
/// **Nothing composes this by default.** A deployment that never
/// supplies a sealer seals nothing, stores nothing extra, and runs the
/// pipeline exactly as it did before this surface existed (GP 11 / GP 13).
///
/// **Six portability rules (GP 12).** Identity by value — bytes in,
/// a record of strings out. Async at every boundary. Failure as data
/// (`Result` over a reason string, never an exception). Stateless
/// between invocations, so key rotation takes effect on the next call
/// with no restart and no cache to invalidate. No cross-shard ordering
/// promise; no timing-precision boundary.
type IDeployRecordSealer =
    /// The scheme name this sealer mints and verifies.
    abstract Scheme: unit -> string

    /// Seal a deploy record's canonical bytes.
    abstract Seal: canonicalBytes: byte[] -> Async<Result<DeployRecordSeal, string>>

    /// Verify that `seal` covers `canonicalBytes`. An implementation
    /// MUST refuse a seal whose `Scheme` it does not own, rather than
    /// attempt to parse a token it does not understand.
    abstract VerifySeal: canonicalBytes: byte[] * seal: DeployRecordSeal -> Async<Result<unit, string>>

[<RequireQualifiedAccess>]
module DeployRecord =

    /// Schema version this build of the substrate emits.
    ///
    /// Phase 774 moved it from 1 to 2: the canonical form gained the
    /// verification level. A record at schema 1 still canonicalises to the
    /// exact bytes it was sealed over (see `canonicalForm`), so its seal
    /// still verifies.
    [<Literal>]
    let SchemaVersion = 2

    /// The schema of every record sealed before Phase 774 — no
    /// verification level in its canonical bytes.
    [<Literal>]
    let PreLevelSchemaVersion = 1

    /// The schema at which the verification level joined the canonical
    /// form. Separate from `SchemaVersion` deliberately (the Phase 694
    /// shape): the next field to join advances `SchemaVersion` and must
    /// NOT move this, or every record sealed between the two would be read
    /// as carrying no level.
    [<Literal>]
    let LevelSchemaVersion = 2

    /// Framing version, prefixed to the record's canonical form.
    /// Bumping it invalidates every existing seal by construction.
    [<Literal>]
    let FramingVersion = "toolup.deployrecord.v1"

    /// Framing version of the Phase 774 level block. Part of the framed
    /// bytes, so a level framed under a future scheme can never collide
    /// with one framed under this.
    [<Literal>]
    let LevelFramingVersion = "toolup.deployrecord.level.v1"

    /// Build a deploy record at the current schema version, at
    /// `VerificationLevel.Attributed` — the claim every signature can
    /// honestly make. A producer sealing under a stronger guarantee raises
    /// it with `withVerificationLevel` BEFORE sealing.
    let create
        (deployId: DeployId)
        (tenantId: TenantId)
        (buildId: BuildId)
        (manifest: DeployManifest)
        (provenance: DeployProvenance)
        : DeployRecord =
        {
            SchemaVersion = SchemaVersion
            DeployId = deployId
            TenantId = tenantId
            BuildId = buildId
            Manifest = manifest
            Provenance = DeployProvenance.coerce provenance
            VerificationLevel = VerificationLevel.Attributed
        }

    /// The schema a record was sealed under, reading an absent or
    /// non-positive field as the pre-level shape.
    let effectiveSchemaVersion (record: DeployRecord) : int =
        if record.SchemaVersion <= 0 then
            PreLevelSchemaVersion
        else
            record.SchemaVersion

    /// Whether the record's canonical bytes carry a verification level.
    let recordsVerificationLevel (record: DeployRecord) : bool =
        effectiveSchemaVersion record >= LevelSchemaVersion

    /// The level field with the null coercion a read path needs: a record
    /// deserialised without the field carries `null`, read as the floor.
    let private levelField (record: DeployRecord) : VerificationLevel =
        if isNull (box record.VerificationLevel) then
            VerificationLevel.Attributed
        else
            record.VerificationLevel

    /// The level the record declares, and where that answer came from.
    /// A pre-level record is `Attributed`, inferred from its schema — never
    /// read from a field it did not seal.
    let verificationLevel (record: DeployRecord) : VerificationLevel * VerificationLevelProvenance =
        if recordsVerificationLevel record then
            levelField record, VerificationLevelProvenance.DeclaredByRecord
        else
            VerificationLevel.Attributed, VerificationLevelProvenance.InferredFromSchema(effectiveSchemaVersion record)

    /// Set the level a record declares. Must be applied BEFORE sealing:
    /// the level is inside the signed bytes, so changing it afterwards
    /// breaks the seal, which is the point. Stamps the record at the
    /// current schema, since only that schema carries a level.
    let withVerificationLevel (level: VerificationLevel) (record: DeployRecord) : DeployRecord = {
        record with
            SchemaVersion = max record.SchemaVersion SchemaVersion
            VerificationLevel = level
    }

    /// The canonical text for a manifest, framed field by field.
    ///
    /// The WHOLE manifest is covered, not a summary of it: editing a
    /// secret source, a domain, or a pinned module version after the
    /// fact changes these bytes and breaks the seal. A canonical form
    /// that covered only the app identity would let the interesting
    /// half of the manifest be rewritten under a valid signature, which
    /// is worse than not sealing at all — it would look verified.
    ///
    /// List-valued sections are sorted so that authoring order never
    /// reaches the seal; `ExtensionFields` is sorted by key for the same
    /// reason. `TimeSpan`s are framed as tick counts, so no culture or
    /// format string can change the bytes.
    let manifestCanonicalForm (manifest: DeployManifest) : string =
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder
        let frameOptional = ProvenanceFraming.frameOptional builder

        frame (string manifest.SchemaVersion)
        frame manifest.App.Name
        frame manifest.App.Slug
        frame manifest.App.Region
        frame manifest.Runtime.Framework
        frameOptional manifest.Runtime.Image
        frame manifest.Runtime.Healthcheck.Path
        frameOptional (manifest.Runtime.Healthcheck.Port |> Option.map string)
        frame (string manifest.Runtime.Healthcheck.InitialDelay.Ticks)
        frame (string manifest.Runtime.Healthcheck.Interval.Ticks)

        let secrets =
            manifest.Secrets
            |> List.sortWith (fun a b ->
                let byName = String.CompareOrdinal(a.Name, b.Name)

                if byName <> 0 then
                    byName
                else
                    String.CompareOrdinal(a.Source, b.Source))

        frame (string secrets.Length)

        for secret in secrets do
            frame secret.Name
            frame secret.Source

        let domains =
            manifest.Domains
            |> List.sortWith (fun a b ->
                let byHost = String.CompareOrdinal(a.Hostname, b.Hostname)

                if byHost <> 0 then
                    byHost
                else
                    String.CompareOrdinal(a.TlsMode, b.TlsMode))

        frame (string domains.Length)

        for domain in domains do
            frame domain.Hostname
            frame domain.TlsMode

        let modules =
            manifest.Modules
            |> List.sortWith (fun a b ->
                let byId = String.CompareOrdinal(a.PackageId, b.PackageId)

                if byId <> 0 then
                    byId
                else
                    String.CompareOrdinal(a.Version, b.Version))

        frame (string modules.Length)

        for moduleRef in modules do
            frame moduleRef.PackageId
            frame moduleRef.Version

        let dependencies =
            manifest.Dependencies
            |> List.sortWith (fun a b ->
                let byName = String.CompareOrdinal(a.Name, b.Name)

                if byName <> 0 then
                    byName
                else
                    String.CompareOrdinal(a.Kind, b.Kind))

        frame (string dependencies.Length)

        for dependency in dependencies do
            frame dependency.Name
            frame dependency.Kind
            frameOptional dependency.ConnectionTemplate

        let extensions =
            manifest.ExtensionFields
            |> Map.toList
            |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

        frame (string extensions.Length)

        for (key, value) in extensions do
            frame key
            frame value

        builder.ToString()

    /// The canonical text a deploy record's seal is taken over.
    ///
    /// Deterministic in the same strong sense as
    /// `BuildTranscript.canonicalForm`: nothing about the ambient
    /// machine reaches the output, every field is length-framed, and
    /// every list-valued section is sorted. Two records carrying the
    /// same facts produce identical text; two carrying different facts
    /// cannot.
    let canonicalForm (record: DeployRecord) : string =
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder

        frame FramingVersion
        frame (string record.SchemaVersion)
        frame record.DeployId
        frame record.TenantId
        frame record.BuildId
        frame (manifestCanonicalForm record.Manifest)
        frame (DeployProvenance.canonicalForm record.Provenance)

        // Phase 774 — the level block is emitted only for a record whose
        // schema carries it, and that gate is load-bearing: a record sealed
        // before the level existed must canonicalise to the exact bytes its
        // seal was minted over, for ever. The schema version is framed
        // above, so stripping the level by re-labelling a record to the old
        // schema changes the bytes too.
        if recordsVerificationLevel record then
            frame LevelFramingVersion
            frame (VerificationLevel.label (levelField record))

        builder.ToString()

    /// Coerce a record read back from a store written before this
    /// surface existed, or by a producer that omitted the provenance
    /// section. See `DeployProvenance.coerce` for why this is a read-path
    /// obligation rather than a deserialiser detail.
    let coerce (record: DeployRecord) : DeployRecord = {
        record with
            Provenance = DeployProvenance.coerce record.Provenance
            VerificationLevel = levelField record
    }

// ─── The upstream work half, as an optional structured companion ─────
//
// `DeployProvenance.UpstreamProvenanceDigest` is an opaque digest: a
// deployment may fill it, the platform stores it, seals it and reports
// it verbatim, and never interprets it. That slot is authoritative and
// is not changed here — nothing below rewrites it, widens it, or adds a
// second one.
//
// What it cannot do is answer WHICH upstream work stands behind the
// sources, because a digest can be compared and cannot be traversed. The
// companion below is the structured half of that answer: a reference to
// the head upstream work record (walkable through the work provenance
// seam), or the honest reason there is none.
//
// **A companion, not a widening — for the reason this file already
// records.** Three more fields on `DeployRecord` would retype its
// constructor and break every consumer that builds one literally, for
// the benefit of consumers that fill them — and on the day this shipped,
// that was none of them. As a separate record that EMBEDS the deploy
// record, the sealed type is untouched, an existing consumer keeps
// compiling, and an existing deployment keeps producing byte-for-byte
// identical canonical bytes and therefore byte-for-byte identical seals
// (GP 11 / GP 13).
//
// **The companion is NOT inside the seal, and says so.** It is derived
// after the fact by asking a source system a question, so it is a
// reading of the record rather than part of it. `UpstreamReference`
// echoes the slot the reading was taken against precisely so a reader
// can confirm the companion describes THIS record — a cross-check
// against the authoritative digest, never a substitute for it.

/// The structured upstream-work companion to a deploy record.
///
/// Purely additive: a deployment that never asks produces
/// `DeployWorkAttestation.none`, which records the honest reason
/// (`SourceAbsent`) rather than a blank that could be mistaken for
/// "asked, and clean".
type DeployWorkAttestation = {
    /// Schema version of the companion shape.
    SchemaVersion: int
    /// Echo of the deploy record's opaque upstream-provenance slot as it
    /// stood when this reading was taken. The slot stays authoritative;
    /// this is how a reader confirms the companion belongs to the record
    /// in front of it, and nothing here parses the value.
    UpstreamReference: string option
    /// The head upstream work record, or the reason there is none.
    /// Never silent.
    Head: WorkAttestation
    /// The source system consulted, as it named itself. `""` when none
    /// was consulted.
    SourceSystem: string
}

/// A deploy record together with its upstream-work reading. What a
/// reader is handed when it wants both halves; the sealed record inside
/// is unchanged and verifies exactly as it did before.
type WorkAttestedDeployRecord = {
    Record: DeployRecord
    Work: DeployWorkAttestation
}

[<RequireQualifiedAccess>]
module DeployWorkAttestation =

    /// Schema version this build of the substrate emits.
    [<Literal>]
    let SchemaVersion = 1

    /// The identity value: nothing asked, and it says so. What a
    /// deployment that composes no work provenance source contributes,
    /// so "no reading" is not a special case anyone has to handle
    /// (GP 11).
    let none: DeployWorkAttestation = {
        SchemaVersion = SchemaVersion
        UpstreamReference = None
        Head = WorkAttestation.Unattested WorkUnattestedReason.SourceAbsent
        SourceSystem = ""
    }

    /// Whether this reading references a work record at all.
    let isAttested (attestation: DeployWorkAttestation) : bool =
        match attestation.Head with
        | WorkAttestation.AttestedBy _ -> true
        | WorkAttestation.Unattested _ -> false

    /// Render the reading for an operator-facing surface, naming the
    /// source system when one was consulted.
    let describe (attestation: DeployWorkAttestation) : string =
        let head = WorkAttestation.describe attestation.Head

        if attestation.SourceSystem = "" then
            head
        else
            $"{head} [source system: {attestation.SourceSystem}]"
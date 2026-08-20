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
    [<Literal>]
    let SchemaVersion = 1

    /// Framing version, prefixed to the record's canonical form.
    /// Bumping it invalidates every existing seal by construction.
    [<Literal>]
    let FramingVersion = "toolup.deployrecord.v1"

    /// Build a deploy record at the current schema version.
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

        builder.ToString()

    /// Coerce a record read back from a store written before this
    /// surface existed, or by a producer that omitted the provenance
    /// section. See `DeployProvenance.coerce` for why this is a read-path
    /// obligation rather than a deserialiser detail.
    let coerce (record: DeployRecord) : DeployRecord = {
        record with
            Provenance = DeployProvenance.coerce record.Provenance
    }
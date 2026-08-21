// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text

// ─── Build transcript + deploy provenance ────────────────────────────
//
// Nothing in the deploy plane binds WHAT was deployed to HOW it was
// built. A deploy records an image reference and a manifest; neither
// says which toolchain compiled the code, which dependency versions it
// resolved, or which entry point it started from. Two deploys naming
// the same image are indistinguishable even when the second was built
// from different inputs.
//
// A **build transcript** closes that gap at the honest floor: a
// content-addressed record of the inputs a build was given. It is
// deterministic — the same inputs canonicalise to the same text and
// therefore to the same digest, on any machine, in any process, at any
// time — so a transcript can be referenced by its digest, and any party
// holding the transcript can confirm the reference.
//
// **What a transcript claims, and what it does not.** Written down
// here rather than left to be inferred, because a reader who over-reads
// it is worse off than one who knows its bound:
//
//   * It DOES record the inputs a builder declared: toolchain identity
//     and version, the resolved dependency closure as an
//     id + version + content-digest set, and the build entry point with
//     its own content digest.
//   * It DOES make those recorded inputs tamper-evident. Edit any one
//     of them and the transcript's digest changes, so a deploy record
//     referencing the old digest stops matching — visibly, at a named
//     field, rather than silently.
//   * It does NOT claim the build is REPRODUCIBLE. Re-running the same
//     recorded inputs may still produce different output bytes:
//     timestamps, file ordering, embedded absolute paths and
//     non-deterministic compilers all remain in play. Bit-for-bit
//     reproducibility is a strictly stronger property, and this record
//     does not establish it. Treat the transcript as *what the build
//     was given*, never as *what the build must therefore produce*.
//   * It does NOT claim the recorded inputs are TRUE. A transcript is
//     a statement by whoever produced it. Sealing it (see
//     `DeployRecordSeal`) makes that statement attributable and
//     tamper-evident; it does not make it correct.
//
// Purely additive (GP 11 / GP 13): every type here is new, nothing
// existing is retyped, and a deployment that never builds a transcript
// allocates nothing and behaves byte-for-byte as it did before this
// surface existed.
//
// **Fable-safe by construction.** These are records plus pure string
// building, so they compile on the client tier with the rest of
// `ToolUp.Platform.Core` (GP 10). The digest itself is deliberately NOT
// here: hashing needs `System.Security.Cryptography`, which does not
// compile under Fable, so it lives beside the deploy pipeline in
// `ToolUp.Platform.Server`. The canonical form — the part that must be
// agreed between producer and verifier — is shared; the hash over it is
// a server-side detail.

/// Length-prefixed framing, shared by every canonical form in this
/// substrate.
///
/// **Length prefixes rather than delimiters**, because a delimiter can
/// occur inside a field value and an escape scheme is one more thing to
/// get wrong. With the length in front, no field value can be re-cut
/// into a different field sequence, so two distinct records cannot
/// canonicalise to the same text by smuggling a separator through a
/// package id or a free-form label.
///
/// The prefix counts UTF-16 code units — the same measure
/// `String.Length` reports — so the framing is a pure string operation
/// with no encoding step. That keeps it identical on every host that
/// compiles this file, which is the property that matters; the choice
/// of measure is arbitrary as long as producer and verifier share it.
[<RequireQualifiedAccess>]
module ProvenanceFraming =

    /// Append `<length>:<field>` to `builder`.
    let frame (builder: StringBuilder) (field: string) : unit =
        let value = if isNull field then "" else field

        builder.Append(value.Length).Append(':').Append(value) |> ignore

    /// Frame an optional field as a presence marker plus its value, so
    /// `None` and `Some ""` cannot canonicalise to the same text.
    let frameOptional (builder: StringBuilder) (field: string option) : unit =
        match field with
        | Some value ->
            frame builder "1"
            frame builder value
        | None ->
            frame builder "0"
            frame builder ""

/// The toolchain a build ran under. Both fields are free-form: the
/// substrate does not enumerate toolchains, and a consumer recording
/// something this SDK has never heard of is the expected case.
type BuildToolchain = {
    /// Toolchain identity, e.g. a compiler or SDK name.
    Name: string
    /// Toolchain version, exactly as the toolchain reports it.
    Version: string
}

/// One member of the resolved dependency closure.
///
/// The closure is recorded as a SET: `BuildTranscript.canonicalForm`
/// sorts and de-duplicates, so two builds that resolved the same
/// dependencies in a different order produce the same transcript. That
/// is deliberate — resolution order is an artefact of the resolver, not
/// a fact about the build.
type BuildDependency = {
    /// Package identifier as the resolver names it.
    Id: string
    /// Exact resolved version. Never a range — a range is what was
    /// asked for, and a transcript records what was resolved.
    Version: string
    /// Content digest of the resolved artefact, lowercase hex, or `""`
    /// when the resolver exposed no artefact to hash. An empty digest
    /// is honest; a fabricated one is not.
    ContentDigest: string
}

/// The entry point a build started from, and its content digest.
type BuildEntryPoint = {
    /// Path of the entry point, relative to the build root. Relative so
    /// that two builds of the same sources on different machines are
    /// comparable — an absolute path records the machine, not the build.
    Path: string
    /// Content digest of the entry point, lowercase hex.
    ContentDigest: string
}

/// A content-addressed record of the inputs a build was given.
///
/// See the file header for what this does and does not claim. In one
/// line: it records inputs and makes them tamper-evident; it does not
/// assert reproducibility, and it does not assert truth.
type BuildTranscript = {
    /// Schema version of the transcript shape. Bumped only when the
    /// canonical form changes, which by construction changes every
    /// transcript digest — so bump deliberately, with a migration note.
    SchemaVersion: int
    /// Toolchain identity and version.
    Toolchain: BuildToolchain
    /// The resolved dependency closure, as a set (see
    /// `BuildDependency`).
    Dependencies: BuildDependency list
    /// The build entry point and its digest.
    EntryPoint: BuildEntryPoint
}

[<RequireQualifiedAccess>]
module BuildTranscript =

    /// Schema version this build of the substrate emits.
    [<Literal>]
    let SchemaVersion = 1

    /// Framing version, prefixed to every canonical form. Bumping it
    /// invalidates every existing transcript digest by construction,
    /// which is the intended behaviour for a framing change.
    [<Literal>]
    let FramingVersion = "toolup.buildtranscript.v1"

    /// A transcript with nothing recorded, at the current schema
    /// version. A starting point for builder-style construction; a
    /// transcript that stays empty records nothing and says so.
    let empty: BuildTranscript = {
        SchemaVersion = SchemaVersion
        Toolchain = { Name = ""; Version = "" }
        Dependencies = []
        EntryPoint = { Path = ""; ContentDigest = "" }
    }

    /// Build a transcript at the current schema version.
    let create
        (toolchain: BuildToolchain)
        (dependencies: BuildDependency list)
        (entryPoint: BuildEntryPoint)
        : BuildTranscript =
        {
            SchemaVersion = SchemaVersion
            Toolchain = toolchain
            Dependencies = dependencies
            EntryPoint = entryPoint
        }

    /// The dependency closure in canonical order: de-duplicated, then
    /// sorted by (id, version, digest) ordinally. Exposed because a
    /// consumer rendering a transcript should show the same order the
    /// digest was taken over — a display that disagrees with the
    /// canonical form invites a reader to conclude the digest is wrong.
    let canonicalDependencies (transcript: BuildTranscript) : BuildDependency list =
        transcript.Dependencies
        |> List.distinct
        |> List.sortWith (fun a b ->
            let byId = String.CompareOrdinal(a.Id, b.Id)

            if byId <> 0 then
                byId
            else
                let byVersion = String.CompareOrdinal(a.Version, b.Version)

                if byVersion <> 0 then
                    byVersion
                else
                    String.CompareOrdinal(a.ContentDigest, b.ContentDigest))

    /// The canonical text a transcript's digest is taken over.
    ///
    /// Deterministic in the strong sense the file header claims: the
    /// dependency closure is sorted and de-duplicated, every field is
    /// length-framed, and nothing about the ambient machine — clock,
    /// culture, file system, resolver order — reaches the output. Two
    /// transcripts carrying the same inputs produce identical text; two
    /// carrying different inputs cannot.
    let canonicalForm (transcript: BuildTranscript) : string =
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder

        frame FramingVersion
        frame (string transcript.SchemaVersion)
        frame transcript.Toolchain.Name
        frame transcript.Toolchain.Version

        let dependencies = canonicalDependencies transcript

        frame (string dependencies.Length)

        for dependency in dependencies do
            frame dependency.Id
            frame dependency.Version
            frame dependency.ContentDigest

        frame transcript.EntryPoint.Path
        frame transcript.EntryPoint.ContentDigest

        builder.ToString()

/// One deployed artifact, recorded by path and content digest.
type DeployArtifactDigest = {
    /// Path of the artifact, relative to the deployment root. Relative
    /// for the same reason `BuildEntryPoint.Path` is: an absolute path
    /// records the machine that happened to run the build.
    Path: string
    /// Content digest of the artifact's bytes, lowercase hex.
    ContentDigest: string
}

/// What a deploy was, and what it was built from.
///
/// Every field is optional or empty by default, and
/// `DeployProvenance.none` is the value a deployment that records
/// nothing contributes — so an unfilled provenance is not a special
/// case anyone has to handle, it is the identity (GP 11).
type DeployProvenance = {
    /// The deployed artifacts, by path and content digest. Recorded as
    /// a set: the canonical form sorts and de-duplicates, so file-system
    /// enumeration order never reaches the digest.
    ArtifactDigests: DeployArtifactDigest list
    /// Digest of the `BuildTranscript` this deploy was built under, or
    /// `None` when no transcript was produced.
    TranscriptDigest: string option
    /// **An opaque digest naming whatever upstream input produced the
    /// sources this deploy was built from.**
    ///
    /// The platform STORES this value, carries it into the sealed
    /// bytes, and REPORTS it back verbatim. It never interprets it. The
    /// substrate does not know — and deliberately does not encode —
    /// what produced the value, which algorithm minted it, or what it
    /// refers to; a consumer that fills the slot owns its meaning end
    /// to end, and no code in this SDK will ever branch on its content.
    ///
    /// Treat it as an uninterpreted correlation handle. Because the
    /// slot is inside the canonical form, a filled value is
    /// tamper-evident once the record is sealed — which is the whole of
    /// what the platform offers here. A seal proves the value was not
    /// edited after sealing; it says nothing about whether the value
    /// was right in the first place, and nothing in this substrate can.
    ///
    /// The substrate does define ONE structured value a deployment MAY
    /// choose to fill it with: the digest of its build's
    /// `DependencyClosure` (the dependency-closure section at the end
    /// of this file; bound via `DeployRecords.withClosure`). The slot
    /// stays uninterpreted here either way — a checker that holds the
    /// closure asks the join question explicitly, and any other filling
    /// remains as legitimate as it was.
    UpstreamProvenanceDigest: string option
}

[<RequireQualifiedAccess>]
module DeployProvenance =

    /// Framing version for the provenance section of a canonical form.
    [<Literal>]
    let FramingVersion = "toolup.deployprovenance.v1"

    /// The identity value: nothing recorded. What a deployment that
    /// never fills a slot contributes, so "unfilled" needs no special
    /// handling anywhere downstream.
    let none: DeployProvenance = {
        ArtifactDigests = []
        TranscriptDigest = None
        UpstreamProvenanceDigest = None
    }

    /// Whether this provenance records anything at all.
    let isEmpty (provenance: DeployProvenance) : bool =
        List.isEmpty provenance.ArtifactDigests
        && provenance.TranscriptDigest.IsNone
        && provenance.UpstreamProvenanceDigest.IsNone

    /// Coerce a value read back from a record persisted before this
    /// field existed.
    ///
    /// The platform's JSON path initialises an absent reference-typed
    /// field to `null`, and a null F# list throws on the first list
    /// operation — `[]` is a singleton, not null. A store that has been
    /// running since before this surface landed will hand back exactly
    /// that shape, so every read path coerces rather than trusting the
    /// deserialiser to have produced an empty list.
    let coerce (provenance: DeployProvenance) : DeployProvenance =
        if isNull (box provenance) then
            none
        else
            {
                provenance with
                    ArtifactDigests =
                        if isNull (box provenance.ArtifactDigests) then
                            []
                        else
                            provenance.ArtifactDigests
            }

    /// Record the deployed artifact set.
    let withArtifacts (artifacts: DeployArtifactDigest list) (provenance: DeployProvenance) : DeployProvenance = {
        provenance with
            ArtifactDigests = artifacts
    }

    /// Record the digest of the build transcript this deploy was built
    /// under.
    let withTranscriptDigest (digest: string) (provenance: DeployProvenance) : DeployProvenance = {
        provenance with
            TranscriptDigest = Some digest
    }

    /// Fill the opaque upstream-provenance slot. The platform records
    /// the value and never reads it — see the field's own
    /// documentation before deciding what to put here.
    let withUpstreamProvenanceDigest (digest: string) (provenance: DeployProvenance) : DeployProvenance = {
        provenance with
            UpstreamProvenanceDigest = Some digest
    }

    /// The artifact set in canonical order: de-duplicated, then sorted
    /// by (path, digest) ordinally.
    let canonicalArtifacts (provenance: DeployProvenance) : DeployArtifactDigest list =
        provenance.ArtifactDigests
        |> List.distinct
        |> List.sortWith (fun a b ->
            let byPath = String.CompareOrdinal(a.Path, b.Path)

            if byPath <> 0 then
                byPath
            else
                String.CompareOrdinal(a.ContentDigest, b.ContentDigest))

    /// The canonical text for a provenance value, framed like
    /// everything else. Not digested on its own — it is a section of a
    /// deploy record's canonical form.
    let canonicalForm (provenance: DeployProvenance) : string =
        let coerced = coerce provenance
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder

        frame FramingVersion

        let artifacts = canonicalArtifacts coerced

        frame (string artifacts.Length)

        for artifact in artifacts do
            frame artifact.Path
            frame artifact.ContentDigest

        ProvenanceFraming.frameOptional builder coerced.TranscriptDigest
        ProvenanceFraming.frameOptional builder coerced.UpstreamProvenanceDigest

        builder.ToString()

// ─── Dependency-closure provenance (the upstream join) ───────────────
//
// The transcript above records a build's resolved dependency closure
// as an id + version + content-digest SET, and the deploy record's
// upstream-provenance slot can carry ONE opaque digest. Neither can
// say, per dependency, WHERE it resolved from or WHICH upstream
// release stands behind it — so "which attested release does this
// build stand on" is an investigation, not a lookup.
//
// A **dependency closure** answers it with structure: one entry per
// resolved package, carrying the source the package resolved from (as
// the restore itself recorded it — observed, never re-derived) and an
// attestation that either references an upstream release act BY ID or
// says WHY there is no reference. The reasons are load-bearing: a
// closure that listed only its attested members would read as complete
// and would not be, so no entry is ever silently dropped — an external
// package, a version no release act covers, and the absence of any
// reference provider are each recorded as themselves.
//
// The reference leg is a SEAM (`IUpstreamReleaseProvider`), not a
// dependency: the substrate defines the shape of the answer and asks a
// registered provider per entry. With no provider registered every
// entry is honestly unattested and nothing else changes (GP 11 /
// GP 13); an upstream ledger this SDK has never heard of activates the
// join by implementing one interface, with no substrate change.
//
// Like the transcript, a closure is content-addressed — sorted,
// de-duplicated, length-framed; the digest itself lives server-side
// beside the other digests (`DeployRecords.closureDigest`). A
// deployment binds the closure into its sealed deploy record by
// filling the upstream-provenance slot with the closure's digest
// (`DeployRecords.withClosure`), so a deploy whose build resolved a
// different closure is a different record and the seal refuses the
// substitution.

/// A typed reference to one attested release act in an upstream
/// release ledger.
type UpstreamReleaseReference = {
    /// Identifier of the release act, exactly as the upstream ledger
    /// names it. A join key, not a value anything here parses.
    OpId: string
    /// Content digest of the release act, lowercase hex, or `""` when
    /// the ledger exposes none. An empty digest is honest; a
    /// fabricated one is not.
    ActDigest: string
}

/// Why a closure entry carries no upstream release reference.
///
/// Distinguishable on purpose: "nobody was asked", "asked, and the
/// ledger does not track this package", "asked, and no release act
/// covers this version" and "asked, and the answer never arrived" are
/// four different facts, and a reader deciding what a build stands on
/// needs to know which one they are looking at.
type UnattestedReason =
    /// No upstream release provider was registered — nothing was asked.
    | ProviderAbsent
    /// The upstream ledger does not track this package at all — an
    /// external package, outside the ledger's scope.
    | ExternalPackage
    /// The ledger tracks the package, but no release act covers this
    /// version — e.g. a version released before the ledger existed.
    | NoCoveringAct
    /// The provider was asked and failed; the failure's own reason is
    /// carried. Recorded on the entry rather than aborting the closure,
    /// because losing the whole closure to one failed lookup would be
    /// the silence this type exists to prevent.
    | ResolutionFailed of reason: string

[<RequireQualifiedAccess>]
module UnattestedReason =

    /// Render a reason for an operator-facing surface.
    let describe (reason: UnattestedReason) : string =
        match reason with
        | ProviderAbsent -> "no upstream release provider was registered"
        | ExternalPackage -> "the upstream ledger does not track this package"
        | NoCoveringAct -> "no release act in the upstream ledger covers this version"
        | ResolutionFailed failure -> $"the upstream release provider failed: {failure}"

/// Whether a closure entry stands on an attested upstream release.
/// Never silent: an entry is attested by reference, or unattested with
/// the reason.
type ClosureAttestation =
    | AttestedBy of UpstreamReleaseReference
    | Unattested of UnattestedReason

[<RequireQualifiedAccess>]
module ClosureAttestation =

    /// Render an attestation for an operator-facing surface — wherever
    /// a transcript or closure renders, this is its vocabulary.
    let describe (attestation: ClosureAttestation) : string =
        match attestation with
        | AttestedBy reference -> $"attested by upstream release act {reference.OpId}"
        | Unattested reason -> $"unattested — {UnattestedReason.describe reason}"

/// One resolved package in a build's dependency closure.
type DependencyClosureEntry = {
    /// Package identifier as the resolver names it.
    Id: string
    /// Exact resolved version — what was resolved, never what was
    /// asked for.
    Version: string
    /// The source the package resolved from, exactly as the restore's
    /// own output recorded it, or `""` when the restore exposed none.
    /// Observed, never re-derived: an empty source is honest, a
    /// guessed one is not.
    Source: string
    /// Content digest of the resolved artefact, lowercase hex, or `""`
    /// when the restore exposed none.
    ContentDigest: string
    /// The upstream join for this entry: attested by reference, or
    /// unattested with the reason.
    Attestation: ClosureAttestation
}

/// A build's resolved dependency closure, with per-entry upstream
/// provenance. The structured answer to "which attested releases does
/// this build stand on" — and, for the entries with no answer, the
/// honest reason why not.
type DependencyClosure = {
    /// Schema version of the closure shape. Bumped only when the
    /// canonical form changes, which by construction changes every
    /// closure digest — so bump deliberately, with a migration note.
    SchemaVersion: int
    /// The resolved entries, as a set (the canonical form sorts and
    /// de-duplicates).
    Entries: DependencyClosureEntry list
}

/// How an upstream release ledger answers for one closure entry.
[<RequireQualifiedAccess>]
type UpstreamReleaseCoverage =
    /// A release act covers this exact package version.
    | Covered of UpstreamReleaseReference
    /// The ledger does not track this package at all.
    | NotTracked
    /// The ledger tracks the package, but no release act covers this
    /// version.
    | NotCovered

/// The seam through which a dependency closure references upstream
/// release acts.
///
/// **Nothing composes this by default.** A build that registers no
/// provider records every entry as honestly unattested and behaves
/// byte-for-byte as it did before this seam existed (GP 11 / GP 13).
/// The upstream ledger the references point into is not this SDK's to
/// ship — a provider implements this interface over whatever ledger it
/// answers from, and the join activates with no substrate change.
///
/// **Six portability rules (GP 12).** Identity by value — strings in,
/// a record out. Async at the boundary. Failure as data (`Result` over
/// a reason string, never an exception). Stateless between calls, so a
/// ledger refresh takes effect on the next resolve with nothing to
/// invalidate. No cross-shard ordering promise; no timing-precision
/// boundary.
type IUpstreamReleaseProvider =
    /// The name of the ledger this provider answers from, for
    /// diagnostics and operator-facing display.
    abstract Ledger: unit -> string

    /// Answer for one resolved package: covered (with the reference),
    /// not tracked at all, or tracked but not covered at this version.
    abstract Resolve: packageId: string * version: string -> Async<Result<UpstreamReleaseCoverage, string>>

[<RequireQualifiedAccess>]
module DependencyClosure =

    /// Schema version this build of the substrate emits.
    [<Literal>]
    let SchemaVersion = 1

    /// Framing version, prefixed to every canonical form. Bumping it
    /// invalidates every existing closure digest by construction,
    /// which is the intended behaviour for a framing change.
    [<Literal>]
    let FramingVersion = "toolup.dependencyclosure.v1"

    /// A closure with nothing recorded, at the current schema version.
    let empty: DependencyClosure = {
        SchemaVersion = SchemaVersion
        Entries = []
    }

    /// Build a closure at the current schema version.
    let create (entries: DependencyClosureEntry list) : DependencyClosure = {
        SchemaVersion = SchemaVersion
        Entries = entries
    }

    /// An entry as capture observes it: resolved facts recorded, no
    /// upstream reference resolved yet — which is exactly the
    /// provider-absent state, and is recorded as such rather than as a
    /// blank that could be mistaken for "checked and clean".
    let unattestedEntry
        (id: string)
        (version: string)
        (source: string)
        (contentDigest: string)
        : DependencyClosureEntry =
        {
            Id = id
            Version = version
            Source = source
            ContentDigest = contentDigest
            Attestation = Unattested ProviderAbsent
        }

    let private frameEntry (builder: StringBuilder) (entry: DependencyClosureEntry) : unit =
        let frame = ProvenanceFraming.frame builder

        frame entry.Id
        frame entry.Version
        frame entry.Source
        frame entry.ContentDigest

        match entry.Attestation with
        | AttestedBy reference ->
            frame "attested"
            frame reference.OpId
            frame reference.ActDigest
        | Unattested ProviderAbsent ->
            frame "unattested"
            frame "provider-absent"
            frame ""
        | Unattested ExternalPackage ->
            frame "unattested"
            frame "external-package"
            frame ""
        | Unattested NoCoveringAct ->
            frame "unattested"
            frame "no-covering-act"
            frame ""
        | Unattested(ResolutionFailed reason) ->
            frame "unattested"
            frame "resolution-failed"
            frame reason

    let private entryCanonicalForm (entry: DependencyClosureEntry) : string =
        let builder = StringBuilder()
        frameEntry builder entry
        builder.ToString()

    /// The entries in canonical order: de-duplicated, then sorted by
    /// each entry's framed text ordinally — a total order, because the
    /// framing is injective. Exposed for the same reason
    /// `BuildTranscript.canonicalDependencies` is: a consumer rendering
    /// a closure should show the order the digest was taken over.
    let canonicalEntries (closure: DependencyClosure) : DependencyClosureEntry list =
        closure.Entries
        |> List.distinct
        |> List.sortWith (fun a b -> String.CompareOrdinal(entryCanonicalForm a, entryCanonicalForm b))

    /// The canonical text a closure's digest is taken over.
    /// Deterministic in the same strong sense as
    /// `BuildTranscript.canonicalForm`, for the same reasons.
    let canonicalForm (closure: DependencyClosure) : string =
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder

        frame FramingVersion
        frame (string closure.SchemaVersion)

        let entries = canonicalEntries closure

        frame (string entries.Length)

        for entry in entries do
            frameEntry builder entry

        builder.ToString()

    /// Project the closure into the transcript's dependency shape, so a
    /// build's transcript records the same resolved set its closure
    /// annotates — one observation, two projections, no way for the two
    /// to disagree about what was resolved.
    let toBuildDependencies (closure: DependencyClosure) : BuildDependency list =
        closure.Entries
        |> List.map (fun entry -> {
            Id = entry.Id
            Version = entry.Version
            ContentDigest = entry.ContentDigest
        })

    /// Resolve every entry's upstream reference through a provider.
    ///
    /// `None` re-states every entry as honestly unattested
    /// (`ProviderAbsent`) — for a freshly captured closure that is the
    /// identity, and for any closure it is the truthful description of
    /// an attestation pass that asked nobody. With a provider, each
    /// entry's answer maps to its attestation and a per-entry failure
    /// is recorded on that entry (`ResolutionFailed`) rather than
    /// aborting the closure.
    let attest (provider: IUpstreamReleaseProvider option) (closure: DependencyClosure) : Async<DependencyClosure> = async {
        match provider with
        | None ->
            return {
                closure with
                    Entries =
                        closure.Entries
                        |> List.map (fun entry -> {
                            entry with
                                Attestation = Unattested ProviderAbsent
                        })
            }
        | Some provider ->
            let mutable attested = []

            for entry in closure.Entries do
                let! answer = provider.Resolve(entry.Id, entry.Version)

                let attestation =
                    match answer with
                    | Ok(UpstreamReleaseCoverage.Covered reference) -> AttestedBy reference
                    | Ok UpstreamReleaseCoverage.NotTracked -> Unattested ExternalPackage
                    | Ok UpstreamReleaseCoverage.NotCovered -> Unattested NoCoveringAct
                    | Error reason -> Unattested(ResolutionFailed reason)

                attested <- { entry with Attestation = attestation } :: attested

            return {
                closure with
                    Entries = List.rev attested
            }
    }
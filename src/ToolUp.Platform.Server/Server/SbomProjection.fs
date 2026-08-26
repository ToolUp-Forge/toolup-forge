// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Giraffe

// ─── The dependency SBOM as a projection, not a scan ─────────────────
//
// A build already records its resolved dependency closure — one entry
// per package, with the source it resolved from and whether an upstream
// release act covers it. That record IS an SBOM's dependency half in
// everything but format. This module emits the format: SPDX 2.3 and
// CycloneDX 1.6 documents PROJECTED from the recorded closure.
//
// **Why a projection rather than a scanner.** Every scanner produces a
// file; the property worth having is that this one cannot drift from
// what was built. It is derived from the record the build itself wrote,
// not from a later scan of a tree that may have moved since. So this
// module performs NO I/O, runs NO restore, queries NO feed, opens NO
// network connection and hashes NOTHING: it is a total function from a
// recorded closure to two strings. A projection that went looking for
// facts of its own would be a second observation, free to disagree with
// the first — which is the one thing a provenance artefact must not do.
//
// **Unattested entries are IN the document, marked.** A bill of
// materials listing only the dependencies whose provenance we like
// reads as complete and is not. Every recorded entry appears, and the
// marking is a documented field in each format rather than prose a
// reader has to interpret:
//
//   * SPDX — an `externalRefs` entry of `referenceType`
//     `toolup-upstream-attestation`, whose `referenceLocator` is the
//     machine-readable code (`attested`, `unattested-provider-absent`,
//     `unattested-external-package`, `unattested-no-covering-act`,
//     `unattested-resolution-failed`), plus the rendered sentence on
//     the package `comment`.
//   * CycloneDX — a `properties` entry named
//     `toolup:upstream:attestation` carrying the same code, plus
//     `toolup:upstream:unattested-reason` or
//     `toolup:upstream:release-act` as applicable.
//
// The code is computed ONCE, in the projection, so the two serialisers
// cannot disagree about what an entry's standing is.
//
// **Deterministic.** Same closure and same options produce
// byte-identical documents on any machine, in any process, at any time.
// Components come out in the closure's own canonical order
// (`DependencyClosure.canonicalEntries` — de-duplicated and totally
// ordered), the JSON writer's indentation and newline are pinned rather
// than inherited from the host OS, and NOTHING is read from the ambient
// machine: the created-at instant, the document identity and the
// serial number are INPUTS. A wall-clock read or a fresh GUID would
// make two projections of one build differ, which would defeat the
// point of projecting rather than scanning.
//
// **Honest about what it cannot say.** Three facts the closure does not
// record are therefore never asserted:
//
//   * WHICH PACKAGE ECOSYSTEM names these ids. `SbomOptions.PurlType`
//     is declared by the caller; undeclared, no `purl` is emitted at
//     all rather than a guessed `pkg:nuget/…`.
//   * WHICH ALGORITHM produced a content digest. The closure records
//     the value, not the algorithm, so
//     `SbomOptions.ContentDigestAlgorithm` is declared by the caller
//     and a digest is emitted only when the declared algorithm is one
//     both formats name AND the value is the hex length that algorithm
//     produces. An unrecognised algorithm, or a length that contradicts
//     it, omits the digest rather than asserting a mismatch.
//   * WHAT THE DOCUMENT DOES NOT COVER. `SbomScope` states that in the
//     emitted document itself (SPDX document `comment`; CycloneDX
//     `metadata.properties`), not only in this comment — a reader
//     holding the file is the one who needs it.
//
// **Opt-in surface (GP 13).** The projection is a library function.
// `SbomAdminEndpoint.route` is a platform-admin HTTP surface a
// deployment mounts EXPLICITLY, over an `ISbomSource` it supplies;
// nothing here is registered, scheduled, hosted or allocated by the
// default composition, and a deployment that never mounts it has no
// such path at all.

/// Whether a bill-of-materials component stands on an attested upstream
/// release act. Projected once from `ClosureAttestation` so both
/// serialisers emit the same standing for the same entry.
type SbomAttestation = {
    /// Machine-readable code — the value of the documented marking
    /// field in each format. Never empty.
    Code: string
    /// The rendered sentence, for a human reading the document.
    Detail: string
    /// Identifier of the upstream release act, when one covers this
    /// component; `""` otherwise.
    ReleaseActId: string
    /// Content digest of that release act, lowercase hex, or `""` when
    /// the ledger exposed none or no act covers the component.
    ReleaseActDigest: string
}

/// One component of a projected bill of materials.
type SbomComponent = {
    /// Package identifier, as the resolver named it.
    Id: string
    /// Exact resolved version.
    Version: string
    /// Package URL, or `""` when the caller declared no ecosystem.
    Purl: string
    /// The source the package resolved from, as the restore recorded
    /// it, or `""` when the restore exposed none.
    Source: string
    /// Content digest, lowercase hex, or `""` when none was recorded or
    /// none can be asserted honestly (see the file header).
    ContentDigest: string
    /// Whether the component stands on an attested upstream release.
    Attestation: SbomAttestation
}

/// What the emitted document covers, and what it does not. Rendered
/// INTO the document, because a reader holding the file is the one who
/// needs to know its bound.
type SbomScope = {
    /// What the document does cover, one claim per entry.
    Covers: string list
    /// What the document does NOT cover, one exclusion per entry.
    Excludes: string list
}

/// The application the bill of materials is about.
type SbomSubject = {
    /// Name of the deployed application.
    Name: string
    /// Version of the deployed application, or `""` when unversioned.
    Version: string
}

/// Everything the projection needs that the closure does not record.
///
/// Every field is an INPUT rather than something read from the ambient
/// machine — that is what makes two projections of one build identical
/// (see the file header). `SbomOptions.defaults` is the value a caller
/// that declares nothing contributes: it projects a document that is
/// honest about knowing nothing, rather than one that guesses.
type SbomOptions = {
    /// The application the document is about.
    Subject: SbomSubject
    /// The document's created-at instant, ISO-8601 UTC, supplied by the
    /// caller. Derive it from the build being described, never from the
    /// clock: a wall-clock read makes two projections of one build
    /// differ.
    Created: string
    /// Who or what produced the document, e.g. a tool name and version.
    Creator: string
    /// SPDX `documentNamespace`, a URI. `""` omits the field.
    DocumentNamespace: string
    /// CycloneDX `serialNumber`, of the form `urn:uuid:<uuid>`. `""`
    /// omits the field — as does any value the format would reject,
    /// because an invalid serial number is worse than none.
    SerialNumber: string
    /// The package ecosystem naming the closure's ids, as a package-URL
    /// type (e.g. `nuget`). `""` — the default — emits no `purl`.
    PurlType: string
    /// The algorithm that produced the closure's content digests (e.g.
    /// `SHA-512`). `None` — the default — emits no digests.
    ContentDigestAlgorithm: string option
}

/// The seam through which a deployment supplies the recorded closure a
/// bill of materials is projected from.
///
/// **Nothing composes this by default.** The substrate does not know
/// where a deployment keeps its build record — beside the deployed
/// artefacts, in a store, fetched from a build system — and will not
/// guess. A deployment that wants the endpoint implements this over
/// whatever holds its closure and mounts `SbomAdminEndpoint.route`; one
/// that does not is byte-for-byte unchanged (GP 11 / GP 13).
///
/// **Six portability rules (GP 12).** Identity by value — records in
/// and out, no live handles. Async at the boundary. Failure as data
/// (`Result` over a reason string, never an exception). Stateless
/// between calls, so a rebuilt closure is served on the next request
/// with nothing to invalidate. No cross-shard ordering promise; no
/// timing-precision boundary.
type ISbomSource =
    /// Where this source reads its closure from, for diagnostics and
    /// operator-facing display.
    abstract Describe: unit -> string

    /// The projection inputs the closure does not record.
    abstract Options: unit -> SbomOptions

    /// The recorded closure, or the reason it could not be read.
    abstract Closure: unit -> Async<Result<DependencyClosure, string>>

/// A bill of materials, projected and ready to serialise. Format-neutral
/// by construction: everything a serialiser needs has already been
/// decided here, so SPDX and CycloneDX render the same facts.
type SoftwareBillOfMaterials = {
    /// The application the document is about.
    Subject: SbomSubject
    /// The document's created-at instant, ISO-8601 UTC.
    Created: string
    /// Who or what produced the document.
    Creator: string
    /// SPDX `documentNamespace`, or `""`.
    DocumentNamespace: string
    /// CycloneDX `serialNumber`, or `""`.
    SerialNumber: string
    /// The content-digest algorithm, in each format's own spelling, or
    /// `None` when no digest can be asserted honestly.
    ContentDigestAlgorithm: (string * string) option
    /// The components, in the closure's canonical order.
    Components: SbomComponent list
    /// What the document covers and what it does not.
    Scope: SbomScope
}

[<RequireQualifiedAccess>]
module SbomProjection =

    /// SPDX version emitted.
    [<Literal>]
    let SpdxVersion = "SPDX-2.3"

    /// CycloneDX specification version emitted.
    [<Literal>]
    let CycloneDxSpecVersion = "1.6"

    /// SPDX `referenceType` carrying a component's attestation code.
    [<Literal>]
    let SpdxAttestationRefType = "toolup-upstream-attestation"

    /// SPDX `referenceType` carrying the covering release act's id.
    [<Literal>]
    let SpdxReleaseActRefType = "toolup-upstream-release-act"

    /// CycloneDX property name carrying a component's attestation code.
    [<Literal>]
    let CycloneDxAttestationProperty = "toolup:upstream:attestation"

    /// CycloneDX property name carrying the covering release act's id.
    [<Literal>]
    let CycloneDxReleaseActProperty = "toolup:upstream:release-act"

    /// CycloneDX property name carrying an unattested entry's reason.
    [<Literal>]
    let CycloneDxUnattestedReasonProperty = "toolup:upstream:unattested-reason"

    /// CycloneDX property name carrying the scope statement.
    [<Literal>]
    let CycloneDxScopeProperty = "toolup:scope"

    /// Attestation code for a component a release act covers.
    [<Literal>]
    let AttestedCode = "attested"

    // ─── The scope statement ─────────────────────────────────────────
    //
    // Standing text, emitted into every document. It is a constant
    // rather than a caller-supplied string on purpose: the bound is a
    // property of what a build transcript IS, not of any one
    // deployment's opinion about it, and a bound a producer can edit is
    // not a bound.

    /// What a projected document covers, and what it does not.
    let scope: SbomScope = {
        Covers = [
            "The compile-time dependency closure the build recorded, as its own restore output reported it: \
             one component per resolved package, with the source it resolved from."
            "Every recorded entry, including entries no upstream release act covers. Unattested entries are \
             present and marked, never filtered out."
            "The record as observed at build time. This document is a projection of that record, so it cannot \
             disagree with what the build was given."
        ]
        Excludes = [
            "Components loaded at runtime rather than resolved at compile time: plugins, dynamically probed \
             assemblies, anything the restore never saw."
            "Native artefacts outside the recorded closure, including binaries vendored into an image or \
             installed by the host."
            "Anything the build record never observed. A projection cannot contain what its source does not."
            "Any claim that the recorded inputs are true, or that the build is reproducible. The record is a \
             statement by whoever produced it."
        ]
    }

    /// The scope statement as one block of text — the identical string
    /// both formats carry, so a reader comparing the two documents sees
    /// one bound rather than two paraphrases.
    let scopeText (value: SbomScope) : string =
        let builder = StringBuilder()

        builder.Append "This document covers:" |> ignore

        for claim in value.Covers do
            builder.Append("\n  - ").Append(claim) |> ignore

        builder.Append "\nThis document does NOT cover:" |> ignore

        for exclusion in value.Excludes do
            builder.Append("\n  - ").Append(exclusion) |> ignore

        builder.ToString()

    // ─── Content-digest algorithms ───────────────────────────────────
    //
    // The two formats spell the same algorithms differently and each
    // closes its enumeration, so an algorithm neither names cannot be
    // emitted at all. The hex length is carried alongside because a
    // digest whose length contradicts its declared algorithm is not a
    // digest we should assert — CycloneDX rejects it outright, and SPDX
    // would accept a value that is simply wrong.

    /// Canonical lookup key: case- and separator-insensitive, so
    /// `SHA-512`, `sha512` and `SHA512` are the same declaration.
    let private algorithmKey (name: string) =
        if isNull name then
            ""
        else
            name.Replace("-", "").Trim().ToUpperInvariant()

    /// `key, spdx spelling, cyclonedx spelling, hex length`.
    let private digestAlgorithms = [
        "MD5", "MD5", "MD5", 32
        "SHA1", "SHA1", "SHA-1", 40
        "SHA256", "SHA256", "SHA-256", 64
        "SHA384", "SHA384", "SHA-384", 96
        "SHA512", "SHA512", "SHA-512", 128
        "SHA3256", "SHA3-256", "SHA3-256", 64
        "SHA3384", "SHA3-384", "SHA3-384", 96
        "SHA3512", "SHA3-512", "SHA3-512", 128
        "BLAKE2B256", "BLAKE2b-256", "BLAKE2b-256", 64
        "BLAKE2B384", "BLAKE2b-384", "BLAKE2b-384", 96
        "BLAKE2B512", "BLAKE2b-512", "BLAKE2b-512", 128
        "BLAKE3", "BLAKE3", "BLAKE3", 64
    ]

    let private resolveAlgorithm (declared: string option) =
        match declared with
        | None -> None
        | Some name ->
            let key = algorithmKey name

            digestAlgorithms
            |> List.tryFind (fun (candidate, _, _, _) -> candidate = key)
            |> Option.map (fun (_, spdx, cyclonedx, length) -> (spdx, cyclonedx, length))

    let private isLowerHex (value: string) =
        value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    /// A CycloneDX serial number must be `urn:uuid:<uuid>`; an invalid
    /// one is dropped rather than emitted, because a document the
    /// format rejects helps nobody.
    let private isSerialNumber (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.StartsWith("urn:uuid:", StringComparison.Ordinal)
        && (match Guid.TryParse(value.Substring "urn:uuid:".Length) with
            | true, _ -> true
            | _ -> false)

    // ─── Projection ──────────────────────────────────────────────────

    /// The documented marking for one closure entry's standing. The
    /// codes are stable wire values — a consumer filtering a document
    /// for unattested components matches on these, not on the prose.
    let attestation (value: ClosureAttestation) : SbomAttestation =
        match value with
        | AttestedBy reference -> {
            Code = AttestedCode
            Detail = ClosureAttestation.describe value
            ReleaseActId = reference.OpId
            ReleaseActDigest = reference.ActDigest
          }
        | Unattested reason ->
            let code =
                match reason with
                | ProviderAbsent -> "unattested-provider-absent"
                | ExternalPackage -> "unattested-external-package"
                | NoCoveringAct -> "unattested-no-covering-act"
                | ResolutionFailed _ -> "unattested-resolution-failed"

            {
                Code = code
                Detail = ClosureAttestation.describe value
                ReleaseActId = ""
                ReleaseActDigest = ""
            }

    let private purlOf (purlType: string) (id: string) (version: string) =
        if String.IsNullOrWhiteSpace purlType then
            ""
        else
            $"pkg:{purlType.Trim()}/{id}@{version}"

    /// Project a recorded dependency closure into a bill of materials.
    ///
    /// Pure and total: no I/O, no scan, no clock, no randomness. The
    /// components come out in the closure's canonical order, so two
    /// calls over closures that differ only in resolution order produce
    /// the same value — and therefore the same document bytes.
    let project (options: SbomOptions) (closure: DependencyClosure) : SoftwareBillOfMaterials =
        let algorithm = resolveAlgorithm options.ContentDigestAlgorithm

        let digestOf (recorded: string) =
            match algorithm with
            | Some(_, _, length) when
                not (String.IsNullOrWhiteSpace recorded)
                && recorded.Length = length
                && isLowerHex recorded
                ->
                recorded
            | _ -> ""

        {
            Subject = options.Subject
            Created = options.Created
            Creator = options.Creator
            DocumentNamespace = options.DocumentNamespace
            SerialNumber =
                if isSerialNumber options.SerialNumber then
                    options.SerialNumber
                else
                    ""
            ContentDigestAlgorithm = algorithm |> Option.map (fun (spdx, cyclonedx, _) -> (spdx, cyclonedx))
            Components =
                DependencyClosure.canonicalEntries closure
                |> List.map (fun entry -> {
                    Id = entry.Id
                    Version = entry.Version
                    Purl = purlOf options.PurlType entry.Id entry.Version
                    Source = entry.Source
                    ContentDigest = digestOf entry.ContentDigest
                    Attestation = attestation entry.Attestation
                })
            Scope = scope
        }

    /// Project a build transcript's recorded dependency set.
    ///
    /// The transcript records what was resolved but carries no source
    /// and no upstream reference, so every component comes out honestly
    /// unattested with the provider-absent reason — which is exactly
    /// what "nobody was asked" means. A deployment that has the
    /// annotated closure should project THAT (`project`); this exists so
    /// a deployment holding only a transcript still emits a truthful
    /// document rather than none.
    let ofTranscript (options: SbomOptions) (transcript: BuildTranscript) : SoftwareBillOfMaterials =
        BuildTranscript.canonicalDependencies transcript
        |> List.map (fun dependency ->
            DependencyClosure.unattestedEntry dependency.Id dependency.Version "" dependency.ContentDigest)
        |> DependencyClosure.create
        |> project options

    // ─── Serialisers ─────────────────────────────────────────────────
    //
    // Indentation, indent character and newline are pinned rather than
    // left to the writer's host defaults, so the bytes are identical on
    // a Windows box and a Linux runner. That is not cosmetic: the
    // determinism claim is about BYTES, and `Environment.NewLine` would
    // quietly break it across hosts while every same-host test passed.

    let private writerOptions =
        JsonWriterOptions(Indented = true, IndentCharacter = ' ', IndentSize = 2, NewLine = "\n")

    let private render (write: Utf8JsonWriter -> unit) : string =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, writerOptions)
        write writer
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    /// SPDX's placeholder for a field whose value is genuinely unknown.
    /// Preferred over omitting a required field, and over inventing one.
    [<Literal>]
    let private NoAssertion = "NOASSERTION"

    let private spdxPackageId (index: int) = $"SPDXRef-Package-{index}"

    [<Literal>]
    let private SpdxRootPackageId = "SPDXRef-RootPackage"

    [<Literal>]
    let private SpdxDocumentId = "SPDXRef-DOCUMENT"

    let private writeSpdxExternalRef
        (writer: Utf8JsonWriter)
        (category: string)
        (refType: string)
        (locator: string)
        (comment: string option)
        =
        writer.WriteStartObject()
        writer.WriteString("referenceCategory", category)
        writer.WriteString("referenceType", refType)
        writer.WriteString("referenceLocator", locator)

        match comment with
        | Some text -> writer.WriteString("comment", text)
        | None -> ()

        writer.WriteEndObject()

    /// Serialise a bill of materials as an SPDX 2.3 JSON document.
    ///
    /// The subject is emitted as its own package so the dependency
    /// relationships have something to hang from: the document
    /// DESCRIBES the subject, and the subject DEPENDS_ON each recorded
    /// component. `filesAnalyzed` is false throughout and the licence
    /// fields are `NOASSERTION` — the closure records neither, and a
    /// projection does not go and find out.
    let toSpdxJson (bom: SoftwareBillOfMaterials) : string =
        render (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("spdxVersion", SpdxVersion)
            writer.WriteString("dataLicense", "CC0-1.0")
            writer.WriteString("SPDXID", SpdxDocumentId)
            writer.WriteString("name", bom.Subject.Name)

            if not (String.IsNullOrWhiteSpace bom.DocumentNamespace) then
                writer.WriteString("documentNamespace", bom.DocumentNamespace)

            writer.WriteString("comment", scopeText bom.Scope)

            writer.WriteStartObject "creationInfo"
            writer.WriteString("created", bom.Created)
            writer.WriteStartArray "creators"
            writer.WriteStringValue($"Tool: {bom.Creator}")
            writer.WriteEndArray()
            writer.WriteEndObject()

            writer.WriteStartArray "documentDescribes"
            writer.WriteStringValue SpdxRootPackageId
            writer.WriteEndArray()

            writer.WriteStartArray "packages"

            writer.WriteStartObject()
            writer.WriteString("SPDXID", SpdxRootPackageId)
            writer.WriteString("name", bom.Subject.Name)

            if not (String.IsNullOrWhiteSpace bom.Subject.Version) then
                writer.WriteString("versionInfo", bom.Subject.Version)

            writer.WriteString("downloadLocation", NoAssertion)
            writer.WriteBoolean("filesAnalyzed", false)
            writer.WriteString("licenseConcluded", NoAssertion)
            writer.WriteString("licenseDeclared", NoAssertion)
            writer.WriteString("copyrightText", NoAssertion)
            writer.WriteString("primaryPackagePurpose", "APPLICATION")
            writer.WriteEndObject()

            bom.Components
            |> List.iteri (fun index component' ->
                writer.WriteStartObject()
                writer.WriteString("SPDXID", spdxPackageId (index + 1))
                writer.WriteString("name", component'.Id)

                if not (String.IsNullOrWhiteSpace component'.Version) then
                    writer.WriteString("versionInfo", component'.Version)

                writer.WriteString(
                    "downloadLocation",
                    if String.IsNullOrWhiteSpace component'.Source then
                        NoAssertion
                    else
                        component'.Source
                )

                writer.WriteBoolean("filesAnalyzed", false)
                writer.WriteString("licenseConcluded", NoAssertion)
                writer.WriteString("licenseDeclared", NoAssertion)
                writer.WriteString("copyrightText", NoAssertion)
                writer.WriteString("primaryPackagePurpose", "LIBRARY")
                writer.WriteString("comment", component'.Attestation.Detail)

                match bom.ContentDigestAlgorithm with
                | Some(spdxAlgorithm, _) when component'.ContentDigest <> "" ->
                    writer.WriteStartArray "checksums"
                    writer.WriteStartObject()
                    writer.WriteString("algorithm", spdxAlgorithm)
                    writer.WriteString("checksumValue", component'.ContentDigest)
                    writer.WriteEndObject()
                    writer.WriteEndArray()
                | _ -> ()

                writer.WriteStartArray "externalRefs"

                if component'.Purl <> "" then
                    writeSpdxExternalRef writer "PACKAGE-MANAGER" "purl" component'.Purl None

                writeSpdxExternalRef
                    writer
                    "OTHER"
                    SpdxAttestationRefType
                    component'.Attestation.Code
                    (Some component'.Attestation.Detail)

                if component'.Attestation.ReleaseActId <> "" then
                    writeSpdxExternalRef
                        writer
                        "OTHER"
                        SpdxReleaseActRefType
                        component'.Attestation.ReleaseActId
                        (if component'.Attestation.ReleaseActDigest = "" then
                             None
                         else
                             Some $"release act digest {component'.Attestation.ReleaseActDigest}")

                writer.WriteEndArray()
                writer.WriteEndObject())

            writer.WriteEndArray()

            writer.WriteStartArray "relationships"

            writer.WriteStartObject()
            writer.WriteString("spdxElementId", SpdxDocumentId)
            writer.WriteString("relatedSpdxElement", SpdxRootPackageId)
            writer.WriteString("relationshipType", "DESCRIBES")
            writer.WriteEndObject()

            bom.Components
            |> List.iteri (fun index _ ->
                writer.WriteStartObject()
                writer.WriteString("spdxElementId", SpdxRootPackageId)
                writer.WriteString("relatedSpdxElement", spdxPackageId (index + 1))
                writer.WriteString("relationshipType", "DEPENDS_ON")
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WriteEndObject())

    let private writeCycloneDxProperty (writer: Utf8JsonWriter) (name: string) (value: string) =
        writer.WriteStartObject()
        writer.WriteString("name", name)
        writer.WriteString("value", value)
        writer.WriteEndObject()

    [<Literal>]
    let private CycloneDxRootRef = "toolup:root"

    /// Serialise a bill of materials as a CycloneDX 1.6 JSON document.
    ///
    /// `bom-ref`s are the component's purl where the caller declared an
    /// ecosystem and an ordinal otherwise — either way derived from the
    /// canonical order, so the refs are stable across projections of
    /// the same closure.
    let toCycloneDxJson (bom: SoftwareBillOfMaterials) : string =
        let refs =
            bom.Components
            |> List.mapi (fun index component' ->
                if component'.Purl <> "" then
                    component'.Purl
                else
                    $"toolup:component:{index + 1}")

        render (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("bomFormat", "CycloneDX")
            writer.WriteString("specVersion", CycloneDxSpecVersion)

            if bom.SerialNumber <> "" then
                writer.WriteString("serialNumber", bom.SerialNumber)

            writer.WriteNumber("version", 1)

            writer.WriteStartObject "metadata"
            writer.WriteString("timestamp", bom.Created)

            writer.WriteStartObject "tools"
            writer.WriteStartArray "components"
            writer.WriteStartObject()
            writer.WriteString("type", "application")
            writer.WriteString("name", bom.Creator)
            writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteEndObject()

            writer.WriteStartObject "component"
            writer.WriteString("type", "application")
            writer.WriteString("bom-ref", CycloneDxRootRef)
            writer.WriteString("name", bom.Subject.Name)

            if not (String.IsNullOrWhiteSpace bom.Subject.Version) then
                writer.WriteString("version", bom.Subject.Version)

            writer.WriteEndObject()

            writer.WriteStartArray "properties"
            writeCycloneDxProperty writer CycloneDxScopeProperty (scopeText bom.Scope)
            writer.WriteEndArray()

            writer.WriteEndObject()

            writer.WriteStartArray "components"

            List.zip bom.Components refs
            |> List.iter (fun (component', reference) ->
                writer.WriteStartObject()
                writer.WriteString("type", "library")
                writer.WriteString("bom-ref", reference)
                writer.WriteString("name", component'.Id)

                if not (String.IsNullOrWhiteSpace component'.Version) then
                    writer.WriteString("version", component'.Version)

                if component'.Purl <> "" then
                    writer.WriteString("purl", component'.Purl)

                match bom.ContentDigestAlgorithm with
                | Some(_, cycloneDxAlgorithm) when component'.ContentDigest <> "" ->
                    writer.WriteStartArray "hashes"
                    writer.WriteStartObject()
                    writer.WriteString("alg", cycloneDxAlgorithm)
                    writer.WriteString("content", component'.ContentDigest)
                    writer.WriteEndObject()
                    writer.WriteEndArray()
                | _ -> ()

                if component'.Source <> "" then
                    writer.WriteStartArray "externalReferences"
                    writer.WriteStartObject()
                    writer.WriteString("type", "distribution")
                    writer.WriteString("url", component'.Source)
                    writer.WriteEndObject()
                    writer.WriteEndArray()

                writer.WriteStartArray "properties"
                writeCycloneDxProperty writer CycloneDxAttestationProperty component'.Attestation.Code

                if component'.Attestation.Code = AttestedCode then
                    writeCycloneDxProperty writer CycloneDxReleaseActProperty component'.Attestation.ReleaseActId
                else
                    writeCycloneDxProperty writer CycloneDxUnattestedReasonProperty component'.Attestation.Detail

                writer.WriteEndArray()
                writer.WriteEndObject())

            writer.WriteEndArray()

            writer.WriteStartArray "dependencies"
            writer.WriteStartObject()
            writer.WriteString("ref", CycloneDxRootRef)
            writer.WriteStartArray "dependsOn"

            for reference in refs do
                writer.WriteStringValue reference

            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.WriteEndArray()

            writer.WriteEndObject())

[<RequireQualifiedAccess>]
module SbomOptions =

    /// The value a caller that declares nothing contributes: a document
    /// honest about knowing nothing — no ecosystem, so no `purl`; no
    /// digest algorithm, so no digests; no namespace or serial number.
    /// Every field is still an input, so a projection over these
    /// defaults is as deterministic as one over a fully-declared set.
    let defaults: SbomOptions = {
        Subject = { Name = ""; Version = "" }
        Created = ""
        Creator = ""
        DocumentNamespace = ""
        SerialNumber = ""
        PurlType = ""
        ContentDigestAlgorithm = None
    }

// ─── The opt-in platform-admin endpoint ──────────────────────────────
//
// A deployment that wants the document over HTTP mounts this route
// itself, over its own `ISbomSource`:
//
//     ServerApp.withHandlers [ SbomAdminEndpoint.route mySource ]
//
// Deliberately NOT mounted by the default composition and NOT gated on
// a `ServerConfig` flag. There is nothing for the substrate to mount:
// the endpoint is meaningless without a source only the deployment can
// supply, so "composed" and "has a source" are the same fact, and a
// second config flag would let them disagree. A deployment that never
// calls this has no route, no handler, no DI registration and no
// allocation — the Giraffe terminal middleware answers a clean 404
// (GP 13).
//
// **Fail-closed twice.** The path sits under `/api/_platform/admin/*`,
// which `PlatformAdminAuthorizationMiddleware` refuses for non-admin
// callers before the router dispatches; the handler ALSO checks
// `AccessContext.canModifyPlatformConfig` itself, so the gate holds in
// a composition that omitted the middleware. Defence in depth, and the
// same shape every other raw `_platform` admin handler uses.

[<RequireQualifiedAccess>]
module SbomAdminEndpoint =

    /// The route this endpoint mounts at.
    [<Literal>]
    let RoutePath = "/api/_platform/admin/sbom"

    /// `?format=` value selecting an SPDX 2.3 document. The default.
    [<Literal>]
    let SpdxFormat = "spdx"

    /// `?format=` value selecting a CycloneDX 1.6 document.
    [<Literal>]
    let CycloneDxFormat = "cyclonedx"

    /// A rendered document: its media type and its bytes-as-text.
    type Rendered = { ContentType: string; Body: string }

    /// Why a render did not produce a document.
    type RenderFailure =
        /// The `?format=` value named no format this endpoint emits.
        /// Carries the value asked for.
        | UnknownFormat of requested: string
        /// The source could not produce a closure. Carries its reason
        /// verbatim — the substrate does not paraphrase a seam's
        /// failure.
        | SourceUnavailable of reason: string

    [<RequireQualifiedAccess>]
    module RenderFailure =

        /// Render a failure for an operator-facing surface.
        let describe (failure: RenderFailure) : string =
            match failure with
            | UnknownFormat requested ->
                $"unknown SBOM format '{requested}' — expected '{SpdxFormat}' or '{CycloneDxFormat}'"
            | SourceUnavailable reason -> $"the dependency closure could not be read: {reason}"

    /// Project and serialise, or say why not. The whole of the
    /// endpoint's behaviour, separated from the HTTP shell so it is
    /// exercised directly rather than through a request.
    let render (source: ISbomSource) (format: string) : Async<Result<Rendered, RenderFailure>> = async {
        let requested =
            if String.IsNullOrWhiteSpace format then
                SpdxFormat
            else
                format.Trim().ToLowerInvariant()

        let serialise =
            match requested with
            | SpdxFormat -> Ok("application/spdx+json; charset=utf-8", SbomProjection.toSpdxJson)
            | CycloneDxFormat -> Ok("application/vnd.cyclonedx+json; charset=utf-8", SbomProjection.toCycloneDxJson)
            | other -> Error(UnknownFormat other)

        match serialise with
        | Error failure -> return Error failure
        | Ok(contentType, write) ->
            let! closure = source.Closure()

            match closure with
            | Error reason -> return Error(SourceUnavailable reason)
            | Ok closure ->
                let bom = SbomProjection.project (source.Options()) closure

                return
                    Ok {
                        ContentType = contentType
                        Body = write bom
                    }
    }

    let private resolveAccessContext (ctx: HttpContext) : AccessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as accessContext -> accessContext
        | _ -> AccessContext.unrestricted (AnonymousSession "anonymous")

    let private writeError (ctx: HttpContext) (status: int) (message: string) =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.WriteAsync(JsonSerializer.Serialize {| error = message |})

    /// The `/api/_platform/admin/sbom` route, over a deployment's own
    /// source. Nothing mounts this — a deployment appends it to its own
    /// handler list (see the section header).
    let route (source: ISbomSource) : HttpHandler =
        Giraffe.Routing.route RoutePath
        >=> fun next ctx -> task {
            if not (AccessContext.canModifyPlatformConfig (resolveAccessContext ctx)) then
                do! writeError ctx 403 "platform admin role required"
                return! next ctx
            else
                let format =
                    match ctx.Request.Query.TryGetValue "format" with
                    | true, value -> string value
                    | _ -> SpdxFormat

                let! rendered = render source format |> Async.StartAsTask

                match rendered with
                | Error(UnknownFormat _ as failure) ->
                    do! writeError ctx 400 (RenderFailure.describe failure)
                    return! next ctx
                | Error(SourceUnavailable _ as failure) ->
                    do! writeError ctx 503 (RenderFailure.describe failure)
                    return! next ctx
                | Ok document ->
                    ctx.Response.ContentType <- document.ContentType
                    ctx.Response.Headers["Cache-Control"] <- "no-store"
                    do! ctx.Response.WriteAsync document.Body
                    return! next ctx
        }
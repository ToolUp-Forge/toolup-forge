module ToolUp.Platform.Tests.InProcess.SbomProjectionTests

// Ahead of `open System.Text.Json`: opened after it, `Json.Schema`
// resolves as the partially-qualified `System.Text.Json.Schema` and the
// compiler refuses it (FS0893).
open Json.Schema
open System
open System.IO
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform

// ─── Phase 717 — the dependency SBOM as a projection ─────────────────
//
// Four properties carry this surface, and each is probed in BOTH
// directions, because the passing direction of every one of them is
// also satisfied by an implementation that does nothing useful:
//
//   * CONFORMANCE. Both documents validate against the format's own
//     published schema (vendored under `fixtures/sbom-schemas/`, so the
//     check is offline and pinned). Probed the other way by validating a
//     deliberately broken document and asserting the validator REFUSES
//     it — a schema check that cannot fail proves nothing about the one
//     that passed, and a mis-wired `$ref` or an evaluator that silently
//     no-ops would otherwise read as a clean green.
//   * COMPLETENESS. Every recorded entry appears, unattested ones
//     included and marked. Probed the other way with a closure whose
//     entries are ALL unattested: a filtering emitter would produce an
//     empty component list there and a plausible-looking document
//     everywhere else.
//   * DETERMINISM. Same closure, byte-identical documents — including
//     across a shuffled and duplicated entry list. Probed the other way
//     by perturbing one field at a time and asserting the bytes MOVE, so
//     a serialiser that dropped a field could not pass.
//   * HONESTY. What the closure does not record is not asserted: no
//     ecosystem, no `purl`; no declared digest algorithm, no digests;
//     a digest whose length contradicts its declared algorithm, no
//     digest. Each probed by presence AND absence.

// ─── Schema fixtures ─────────────────────────────────────────────────

let private schemaDirectory =
    Path.Combine(AppContext.BaseDirectory, "fixtures", "sbom-schemas")

let private loadSchema (name: string) =
    JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, name)))

/// The CycloneDX BOM schema `$ref`s two sibling schemas by relative
/// path. Registering them under the `$id` those relative paths resolve
/// to keeps the whole evaluation offline — an unresolvable `$ref` is a
/// validator error, which would masquerade as a document defect.
let private schemas =
    lazy
        (let spdx = loadSchema "spdx-schema.json"
         let cycloneDx = loadSchema "bom-1.6.schema.json"

         SchemaRegistry.Global.Register(
             Uri "http://cyclonedx.org/schema/spdx.schema.json",
             loadSchema "spdx.schema.json"
         )

         SchemaRegistry.Global.Register(
             Uri "http://cyclonedx.org/schema/jsf-0.82.schema.json",
             loadSchema "jsf-0.82.schema.json"
         )

         spdx, cycloneDx)

/// Validate `json` against `schema`, returning the failing keyword
/// locations so a red test names what the format rejected rather than
/// only that it did.
let private validate (schema: JsonSchema) (json: string) : Result<unit, string list> =
    use document = JsonDocument.Parse json
    let options = EvaluationOptions(OutputFormat = OutputFormat.List)
    let results = schema.Evaluate(document.RootElement, options)

    if results.IsValid then
        Ok()
    else
        Error [
            for detail in results.Details do
                if not (isNull (box detail.Errors)) then
                    for KeyValue(keyword, message) in detail.Errors do
                        $"{detail.InstanceLocation}: {keyword}: {message}"
        ]

let private expectValid (schema: JsonSchema) (label: string) (json: string) =
    match validate schema json with
    | Ok() -> ()
    | Error failures ->
        failtestf "%s did not validate against its published schema:\n  %s" label (String.Join("\n  ", failures))

// ─── Fixtures ────────────────────────────────────────────────────────

let private options: SbomOptions = {
    SbomOptions.defaults with
        Subject = {
            Name = "Example App"
            Version = "3.1.0"
        }
        Created = "2026-01-02T03:04:05Z"
        Creator = "example-emitter 1.0.0"
        DocumentNamespace = "https://example.invalid/sbom/example-app/3.1.0"
        SerialNumber = "urn:uuid:11111111-2222-4333-8444-555555555555"
        PurlType = "nuget"
        ContentDigestAlgorithm = Some "SHA-512"
}

/// A 128-hex-character value — the length SHA-512 produces, so the
/// projection can carry it under the declared algorithm.
let private digest (seed: char) = String(seed, 128)

let private attestedEntry: DependencyClosureEntry = {
    Id = "Alpha.Package"
    Version = "1.2.3"
    Source = "https://example.invalid/feed/v3/index.json"
    ContentDigest = digest 'a'
    Attestation =
        AttestedBy {
            OpId = "release-act-0001"
            ActDigest = "beef"
        }
}

let private externalEntry: DependencyClosureEntry = {
    Id = "Beta.Package"
    Version = "0.9.0"
    Source = "https://example.invalid/public/v3/index.json"
    ContentDigest = digest 'b'
    Attestation = Unattested ExternalPackage
}

let private uncoveredEntry: DependencyClosureEntry = {
    Id = "Gamma.Package"
    Version = "4.0.0"
    Source = ""
    ContentDigest = digest 'c'
    Attestation = Unattested NoCoveringAct
}

let private absentEntry: DependencyClosureEntry = {
    Id = "Delta.Package"
    Version = "2.0.0-rc.1"
    Source = "https://example.invalid/feed/v3/index.json"
    ContentDigest = ""
    Attestation = Unattested ProviderAbsent
}

let private failedEntry: DependencyClosureEntry = {
    Id = "Epsilon.Package"
    Version = "7.7.7"
    Source = "https://example.invalid/feed/v3/index.json"
    ContentDigest = digest 'e'
    Attestation = Unattested(ResolutionFailed "the ledger timed out")
}

let private mixedClosure =
    DependencyClosure.create [ attestedEntry; externalEntry; uncoveredEntry; absentEntry; failedEntry ]

/// Every entry unattested — the shape a filtering emitter would reduce
/// to an empty document while looking correct on the mixed closure.
let private whollyUnattestedClosure =
    DependencyClosure.create [ externalEntry; uncoveredEntry; absentEntry; failedEntry ]

let private projected = SbomProjection.project options mixedClosure

let private spdxOf (closure: DependencyClosure) =
    SbomProjection.project options closure |> SbomProjection.toSpdxJson

let private cycloneDxOf (closure: DependencyClosure) =
    SbomProjection.project options closure |> SbomProjection.toCycloneDxJson

// ─── Conformance ─────────────────────────────────────────────────────

let sbomSchemaConformanceTests =
    testList "SbomProjection — published-schema conformance" [
        test "the SPDX document validates against the published SPDX 2.3 schema" {
            let spdx, _ = schemas.Value
            expectValid spdx "the SPDX document" (spdxOf mixedClosure)
        }

        test "the CycloneDX document validates against the published CycloneDX 1.6 schema" {
            let _, cycloneDx = schemas.Value
            expectValid cycloneDx "the CycloneDX document" (cycloneDxOf mixedClosure)
        }

        test "an empty closure still produces documents both schemas accept" {
            let spdx, cycloneDx = schemas.Value
            expectValid spdx "the SPDX document over an empty closure" (spdxOf DependencyClosure.empty)

            expectValid cycloneDx "the CycloneDX document over an empty closure" (cycloneDxOf DependencyClosure.empty)
        }

        test "a document declaring nothing — no ecosystem, no digest algorithm — still validates" {
            let spdx, cycloneDx = schemas.Value

            let bare =
                SbomProjection.project
                    {
                        SbomOptions.defaults with
                            Subject = { Name = "Bare"; Version = "" }
                            Created = "2026-01-02T03:04:05Z"
                            Creator = "bare-emitter"
                    }
                    mixedClosure

            expectValid spdx "the bare SPDX document" (SbomProjection.toSpdxJson bare)
            expectValid cycloneDx "the bare CycloneDX document" (SbomProjection.toCycloneDxJson bare)
        }

        // Go-red control. Without this, a mis-registered `$ref`, a
        // schema file that failed to copy, or an evaluator returning a
        // vacuous pass would leave every assertion above green while
        // checking nothing at all.
        test "the validator REFUSES a document the format rejects" {
            let spdx, cycloneDx = schemas.Value

            let brokenSpdx = (spdxOf mixedClosure).Replace("\"spdxVersion\": \"SPDX-2.3\",", "")

            let brokenCycloneDx =
                (cycloneDxOf mixedClosure).Replace("\"specVersion\": \"1.6\"", "\"specVersion\": 16")

            Expect.isError (validate spdx brokenSpdx) "an SPDX document missing spdxVersion must be refused"

            Expect.isError
                (validate cycloneDx brokenCycloneDx)
                "a CycloneDX document with a non-string specVersion must be refused"
        }
    ]

// ─── Completeness — unattested entries are present and marked ────────

let sbomUnattestedPresenceTests =
    testList "SbomProjection — unattested entries are present and marked" [
        test "every recorded entry becomes a component" {
            Expect.equal
                (projected.Components |> List.map _.Id |> List.sort)
                ([ attestedEntry; externalEntry; uncoveredEntry; absentEntry; failedEntry ]
                 |> List.map _.Id
                 |> List.sort)
                "the projection drops no recorded entry"
        }

        test "the attestation code is the documented one for each reason" {
            let codes =
                projected.Components
                |> List.map (fun c -> c.Id, c.Attestation.Code)
                |> Map.ofList

            Expect.equal codes["Alpha.Package"] "attested" "a covered entry is marked attested"

            Expect.equal codes["Beta.Package"] "unattested-external-package" "an untracked package is marked external"

            Expect.equal
                codes["Gamma.Package"]
                "unattested-no-covering-act"
                "a tracked-but-uncovered version is marked as such"

            Expect.equal codes["Delta.Package"] "unattested-provider-absent" "an entry nobody was asked about says so"

            Expect.equal
                codes["Epsilon.Package"]
                "unattested-resolution-failed"
                "a failed lookup lands on the entry rather than vanishing"
        }

        test "a wholly-unattested closure yields a document containing every entry" {
            let spdx = spdxOf whollyUnattestedClosure
            let cycloneDx = cycloneDxOf whollyUnattestedClosure

            for entry in [ externalEntry; uncoveredEntry; absentEntry; failedEntry ] do
                Expect.stringContains spdx entry.Id $"the SPDX document lists the unattested {entry.Id}"

                Expect.stringContains cycloneDx entry.Id $"the CycloneDX document lists the unattested {entry.Id}"

            let bom = SbomProjection.project options whollyUnattestedClosure

            Expect.equal
                bom.Components.Length
                4
                "no unattested entry is filtered out — a BOM listing only its attested members reads as complete and is not"
        }

        test "the marking rides a documented field in each format" {
            let spdx = spdxOf mixedClosure
            let cycloneDx = cycloneDxOf mixedClosure

            Expect.stringContains
                spdx
                SbomProjection.SpdxAttestationRefType
                "SPDX carries the marking on an externalRefs entry"

            Expect.stringContains spdx "unattested-no-covering-act" "the SPDX marking carries the machine-readable code"

            Expect.stringContains
                cycloneDx
                SbomProjection.CycloneDxAttestationProperty
                "CycloneDX carries the marking on a properties entry"

            Expect.stringContains
                cycloneDx
                "unattested-resolution-failed"
                "the CycloneDX marking carries the machine-readable code"
        }

        test "an attested entry references its release act in both formats" {
            let spdx = spdxOf mixedClosure
            let cycloneDx = cycloneDxOf mixedClosure

            Expect.stringContains spdx SbomProjection.SpdxReleaseActRefType "SPDX names the release-act reference type"

            Expect.stringContains spdx "release-act-0001" "SPDX carries the release act's id"

            Expect.stringContains
                cycloneDx
                SbomProjection.CycloneDxReleaseActProperty
                "CycloneDX names the release-act property"

            Expect.stringContains cycloneDx "release-act-0001" "CycloneDX carries the release act's id"
        }

        test "an unattested entry carries no release-act reference" {
            let onlyUnattested = SbomProjection.project options whollyUnattestedClosure
            let spdx = SbomProjection.toSpdxJson onlyUnattested
            let cycloneDx = SbomProjection.toCycloneDxJson onlyUnattested

            Expect.isFalse
                (spdx.Contains SbomProjection.SpdxReleaseActRefType)
                "SPDX asserts no release act where none covers the entry"

            Expect.isFalse
                (cycloneDx.Contains SbomProjection.CycloneDxReleaseActProperty)
                "CycloneDX asserts no release act where none covers the entry"
        }
    ]

// ─── Determinism ─────────────────────────────────────────────────────

let sbomDeterminismTests =
    testList "SbomProjection — determinism" [
        test "the same closure produces byte-identical documents" {
            Expect.equal (spdxOf mixedClosure) (spdxOf mixedClosure) "SPDX output is stable across calls"

            Expect.equal (cycloneDxOf mixedClosure) (cycloneDxOf mixedClosure) "CycloneDX output is stable across calls"
        }

        test "resolution order and duplicates do not reach the bytes" {
            let shuffled =
                DependencyClosure.create [
                    failedEntry
                    uncoveredEntry
                    attestedEntry
                    absentEntry
                    externalEntry
                    attestedEntry
                    uncoveredEntry
                ]

            Expect.equal
                (spdxOf shuffled)
                (spdxOf mixedClosure)
                "a shuffled, duplicated closure yields the same SPDX bytes"

            Expect.equal
                (cycloneDxOf shuffled)
                (cycloneDxOf mixedClosure)
                "a shuffled, duplicated closure yields the same CycloneDX bytes"
        }

        test "newlines are pinned, so the bytes do not vary by host" {
            let spdx = spdxOf mixedClosure
            let cycloneDx = cycloneDxOf mixedClosure

            Expect.isFalse (spdx.Contains "\r\n") "the SPDX document carries no host-dependent line ending"

            Expect.isFalse (cycloneDx.Contains "\r\n") "the CycloneDX document carries no host-dependent line ending"

            Expect.stringContains spdx "\n" "the SPDX document is indented, so a line ending is actually present"
        }

        // The direction that makes the pack above mean something: a
        // serialiser that silently dropped a field would satisfy every
        // "equal inputs, equal bytes" assertion perfectly.
        test "a perturbed closure produces different documents, field by field" {
            let baselineSpdx = spdxOf mixedClosure
            let baselineCycloneDx = cycloneDxOf mixedClosure

            let perturbations = [
                "id",
                {
                    attestedEntry with
                        Id = "Alpha.Package.Renamed"
                }
                "version", { attestedEntry with Version = "1.2.4" }
                "source",
                {
                    attestedEntry with
                        Source = "https://example.invalid/elsewhere/index.json"
                }
                "digest",
                {
                    attestedEntry with
                        ContentDigest = digest 'f'
                }
                "attestation",
                {
                    attestedEntry with
                        Attestation = Unattested NoCoveringAct
                }
            ]

            for (label, entry) in perturbations do
                let perturbed =
                    DependencyClosure.create [ entry; externalEntry; uncoveredEntry; absentEntry; failedEntry ]

                Expect.notEqual (spdxOf perturbed) baselineSpdx $"a changed {label} reaches the SPDX bytes"

                Expect.notEqual
                    (cycloneDxOf perturbed)
                    baselineCycloneDx
                    $"a changed {label} reaches the CycloneDX bytes"
        }

        test "the document identity fields reach the bytes too" {
            let renamed = {
                options with
                    Subject = {
                        options.Subject with
                            Name = "Other App"
                    }
            }

            let recreated = {
                options with
                    Created = "2026-06-07T08:09:10Z"
            }

            Expect.notEqual
                (SbomProjection.project renamed mixedClosure |> SbomProjection.toSpdxJson)
                (spdxOf mixedClosure)
                "the subject name reaches the SPDX bytes"

            Expect.notEqual
                (SbomProjection.project recreated mixedClosure |> SbomProjection.toCycloneDxJson)
                (cycloneDxOf mixedClosure)
                "the created-at instant reaches the CycloneDX bytes"
        }
    ]

// ─── Honesty — nothing unrecorded is asserted ────────────────────────

let sbomHonestyTests =
    testList "SbomProjection — nothing unrecorded is asserted" [
        test "no declared ecosystem means no purl" {
            let bare = SbomProjection.project { options with PurlType = "" } mixedClosure

            Expect.isTrue
                (bare.Components |> List.forall (fun c -> c.Purl = ""))
                "an undeclared ecosystem emits no package URL"

            Expect.isFalse
                ((SbomProjection.toCycloneDxJson bare).Contains "pkg:")
                "the CycloneDX document invents no purl"

            Expect.stringContains
                (cycloneDxOf mixedClosure)
                "pkg:nuget/Alpha.Package@1.2.3"
                "a declared ecosystem does emit one — so the absence above is a decision, not a bug"
        }

        test "no declared digest algorithm means no digests" {
            let bare =
                SbomProjection.project
                    {
                        options with
                            ContentDigestAlgorithm = None
                    }
                    mixedClosure

            Expect.isNone bare.ContentDigestAlgorithm "an undeclared algorithm resolves to none"

            Expect.isFalse
                ((SbomProjection.toSpdxJson bare).Contains "checksums")
                "the SPDX document emits no checksum it cannot name an algorithm for"

            Expect.isFalse
                ((SbomProjection.toCycloneDxJson bare).Contains "hashes")
                "the CycloneDX document emits no hash it cannot name an algorithm for"

            Expect.stringContains (spdxOf mixedClosure) "SHA512" "a declared algorithm does emit checksums"
            Expect.stringContains (cycloneDxOf mixedClosure) "SHA-512" "each format gets its own spelling"
        }

        test "an algorithm neither format names emits nothing" {
            let unknown =
                SbomProjection.project
                    {
                        options with
                            ContentDigestAlgorithm = Some "CRC32"
                    }
                    mixedClosure

            Expect.isNone unknown.ContentDigestAlgorithm "an unnameable algorithm resolves to none"

            Expect.isFalse
                ((SbomProjection.toSpdxJson unknown).Contains "checksums")
                "an unnameable algorithm emits no checksum"
        }

        test "a digest whose length contradicts its algorithm is dropped, not asserted" {
            let short =
                DependencyClosure.create [
                    {
                        attestedEntry with
                            ContentDigest = "aa11"
                    }
                ]

            let bom = SbomProjection.project options short

            Expect.equal
                (bom.Components |> List.map _.ContentDigest)
                [ "" ]
                "a four-character value is not a SHA-512 digest and is not emitted as one"

            Expect.isFalse
                ((SbomProjection.toCycloneDxJson bom).Contains "hashes")
                "and no hash block reaches the document"
        }

        test "an absent source becomes NOASSERTION rather than a guess" {
            let spdx = spdxOf mixedClosure
            Expect.stringContains spdx "NOASSERTION" "SPDX says it does not know rather than inventing a location"

            let bom =
                SbomProjection.project options (DependencyClosure.create [ uncoveredEntry ])

            Expect.isFalse
                ((SbomProjection.toCycloneDxJson bom).Contains "externalReferences")
                "CycloneDX emits no distribution reference for a source the restore never recorded"
        }

        test "an invalid serial number is dropped rather than emitted" {
            let bad =
                SbomProjection.project
                    {
                        options with
                            SerialNumber = "not-a-urn"
                    }
                    mixedClosure

            Expect.equal bad.SerialNumber "" "a serial number the format would reject is not carried"

            Expect.isFalse
                ((SbomProjection.toCycloneDxJson bad).Contains "serialNumber")
                "and never reaches the document"

            let _, cycloneDx = schemas.Value

            expectValid
                cycloneDx
                "the CycloneDX document with a dropped serial number"
                (SbomProjection.toCycloneDxJson bad)
        }

        test "a transcript-only projection is honestly provider-absent" {
            let transcript =
                BuildTranscript.create
                    {
                        Name = "example-sdk"
                        Version = "10.0.203"
                    }
                    [
                        {
                            Id = "Alpha.Package"
                            Version = "1.2.3"
                            ContentDigest = digest 'a'
                        }
                    ]
                    {
                        Path = "src/Program.fs"
                        ContentDigest = "dd44"
                    }

            let bom = SbomProjection.ofTranscript options transcript

            Expect.equal
                (bom.Components |> List.map (fun c -> c.Attestation.Code))
                [ "unattested-provider-absent" ]
                "a transcript records no upstream reference, so nobody was asked"

            Expect.equal
                (bom.Components |> List.map _.Source)
                [ "" ]
                "and it records no source either — an empty source is honest, a guessed one is not"
        }
    ]

// ─── The scope statement, in the document ────────────────────────────

let sbomScopeStatementTests =
    testList "SbomProjection — the emitted scope statement" [
        test "both documents carry the identical scope statement" {
            let statement = SbomProjection.scopeText SbomProjection.scope
            let spdx = spdxOf mixedClosure
            let cycloneDx = cycloneDxOf mixedClosure

            // The documents are JSON, so the statement is escaped inside
            // them; compare against the escaped form rather than the raw
            // one, which would silently never match.
            let escaped = JsonSerializer.Serialize statement
            let embedded = escaped.Substring(1, escaped.Length - 2)

            Expect.stringContains spdx embedded "the SPDX document carries the scope statement"

            Expect.stringContains
                cycloneDx
                embedded
                "the CycloneDX document carries the same statement, character for character"
        }

        test "the statement names what the document does NOT cover" {
            let statement = SbomProjection.scopeText SbomProjection.scope

            Expect.stringContains statement "does NOT cover" "the exclusions are stated, not implied"
            Expect.stringContains statement "runtime" "runtime-loaded components are named"
            Expect.stringContains statement "Native artefacts" "native artefacts outside the closure are named"

            Expect.stringContains statement "never observed" "anything the record never observed is named"
        }

        test "the statement rides a documented field in each format" {
            use spdx = JsonDocument.Parse(spdxOf mixedClosure)

            Expect.stringContains
                (spdx.RootElement.GetProperty("comment").GetString())
                "does NOT cover"
                "SPDX carries it on the document comment"

            use cycloneDx = JsonDocument.Parse(cycloneDxOf mixedClosure)

            let scopeProperty =
                cycloneDx.RootElement.GetProperty("metadata").GetProperty("properties").EnumerateArray()
                |> Seq.tryFind (fun p -> p.GetProperty("name").GetString() = SbomProjection.CycloneDxScopeProperty)

            Expect.isSome scopeProperty "CycloneDX carries it as a named metadata property"

            Expect.stringContains
                (scopeProperty.Value.GetProperty("value").GetString())
                "does NOT cover"
                "and the property's value is the statement itself"
        }
    ]

// ─── The opt-in platform-admin endpoint ──────────────────────────────

/// A source over a fixed closure. Stateless between calls (GP 12), as
/// the seam requires.
type private StubSbomSource(closure: Result<DependencyClosure, string>) =
    interface ISbomSource with
        member _.Describe() = "stub source"
        member _.Options() = options
        member _.Closure() = async { return closure }

let private okSource = StubSbomSource(Ok mixedClosure) :> ISbomSource

let private failingSource =
    StubSbomSource(Error "the assets output was unreadable") :> ISbomSource

let private contextFor (accessContext: AccessContext) (query: string) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton(accessContext) |> ignore
    let ctx = DefaultHttpContext(RequestServices = services.BuildServiceProvider())
    ctx.Request.Method <- "GET"
    ctx.Request.Path <- PathString SbomAdminEndpoint.RoutePath

    if query <> "" then
        ctx.Request.QueryString <- QueryString query

    ctx.Response.Body <- new MemoryStream()
    ctx :> HttpContext

let private adminContext = AccessContext.unrestricted (AuthenticatedUser "admin")

let private platformAdmin = {
    adminContext with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private ordinaryUser = AccessContext.unrestricted (AuthenticatedUser "someone")

let private runRoute (source: ISbomSource) (ctx: HttpContext) =
    let next: Giraffe.Core.HttpFunc =
        fun c -> System.Threading.Tasks.Task.FromResult(Some c)

    SbomAdminEndpoint.route source next ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

    ctx.Response.Body.Seek(0L, SeekOrigin.Begin) |> ignore
    use reader = new StreamReader(ctx.Response.Body, Encoding.UTF8)
    ctx.Response.StatusCode, reader.ReadToEnd()

let sbomEndpointTests =
    testList "SbomAdminEndpoint — the opt-in platform-admin surface" [
        test "the default format is SPDX and the CycloneDX format is selectable" {
            let spdx = SbomAdminEndpoint.render okSource "" |> Async.RunSynchronously

            let cycloneDx =
                SbomAdminEndpoint.render okSource "cyclonedx" |> Async.RunSynchronously

            match spdx, cycloneDx with
            | Ok spdx, Ok cycloneDx ->
                Expect.stringContains spdx.ContentType "spdx" "the SPDX media type names the format"
                Expect.stringContains spdx.Body "\"spdxVersion\": \"SPDX-2.3\"" "and the body is an SPDX document"

                Expect.stringContains cycloneDx.ContentType "cyclonedx" "the CycloneDX media type names the format"

                Expect.stringContains
                    cycloneDx.Body
                    "\"bomFormat\": \"CycloneDX\""
                    "and the body is a CycloneDX document"
            | other -> failtestf "expected both renders to succeed, got %A" other
        }

        test "an unknown format is refused by name, and the closure is never read" {
            match SbomAdminEndpoint.render okSource "spdx-lite" |> Async.RunSynchronously with
            | Error(SbomAdminEndpoint.UnknownFormat requested) ->
                Expect.equal requested "spdx-lite" "the refusal names what was asked for"
            | other -> failtestf "expected an unknown-format refusal, got %A" other
        }

        test "a source that cannot read its closure surfaces the reason verbatim" {
            match SbomAdminEndpoint.render failingSource "" |> Async.RunSynchronously with
            | Error(SbomAdminEndpoint.SourceUnavailable reason) ->
                Expect.equal
                    reason
                    "the assets output was unreadable"
                    "the substrate does not paraphrase the seam's failure"
            | other -> failtestf "expected a source-unavailable refusal, got %A" other
        }

        test "the route refuses a caller who is not a platform admin" {
            let status, body = runRoute okSource (contextFor ordinaryUser "")

            Expect.equal status 403 "a non-admin is refused"
            Expect.stringContains body "platform admin role required" "with the same wording every admin surface uses"

            Expect.isFalse (body.Contains "spdxVersion") "and no part of the document reaches an unauthorised caller"
        }

        test "the route serves a platform admin" {
            let status, body = runRoute okSource (contextFor platformAdmin "")

            Expect.equal status 200 "an admin is served"
            Expect.stringContains body "\"spdxVersion\": \"SPDX-2.3\"" "with the SPDX document by default"

            let cycloneStatus, cycloneBody =
                runRoute okSource (contextFor platformAdmin "?format=cyclonedx")

            Expect.equal cycloneStatus 200 "and with CycloneDX on request"
            Expect.stringContains cycloneBody "\"bomFormat\": \"CycloneDX\"" "which is the other document"
        }

        test "the route reports a bad format as 400 and an unreadable source as 503" {
            let badFormat, _ = runRoute okSource (contextFor platformAdmin "?format=nonsense")
            Expect.equal badFormat 400 "an unknown format is the caller's error"

            let unavailable, body = runRoute failingSource (contextFor platformAdmin "")
            Expect.equal unavailable 503 "a closure the deployment cannot read is not the caller's error"

            Expect.stringContains
                body
                "the assets output was unreadable"
                "and the operator is told what actually went wrong"
        }

        test "the endpoint's path sits under the platform-admin backstop prefix" {
            Expect.isTrue
                (SbomAdminEndpoint.RoutePath.StartsWith "/api/_platform/admin/")
                "so the fail-closed path-prefix middleware covers it even if a handler check were dropped"
        }
    ]
module ToolUp.Platform.Tests.InProcess.FederationWireConformanceTests

// ─── Phase 596 — the federation seam certifies against its own spec ───
//
// `docs/interplatform/FEDERATION_WIRE.md` specifies the seam; the corpus
// under `docs/interplatform/wire-fixtures/` is its executable half; this
// is the harness that runs it. The in-tree emitters are the corpus's
// FIRST conformant host — a specification whose own reference emitter is
// not held to it is a document, not a protocol.
//
// Four things are asserted, and the last two are the ones a corpus
// usually lacks:
//
//   1. **Conformance.** Every round-trip vector parses into its
//      specified shape and re-serialises to identical bytes; every hash
//      vector's stamp is reproduced by recomputation; every reject
//      vector is refused with its named refusal class.
//   2. **Forward coupling.** Re-emitting the corpus in memory from the
//      live emitters reproduces the committed bytes exactly. An emitter
//      shape change without a regenerated corpus fails here, in the same
//      commit — which is the whole of the "spec + emitter + fixtures move
//      together" rule.
//   3. **Non-vacuity.** The corpus is non-empty, the manifest enumerates
//      exactly the files on disk (neither an orphan fixture nor a
//      phantom entry), and every profile partition is non-empty.
//   4. **Execution accounting.** The harness records the id of every
//      vector it actually certified and asserts that set equals the
//      manifest's. A conformance suite that silently exercises nothing
//      is the failure mode this family of test is most prone to, so the
//      count is an assertion rather than a hope.

open System
open System.Collections.Generic
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.InProcess.FederationWireCorpus

// Regeneration runs once at module load, before any assertion reads the
// corpus — so `TOOLUP_EMIT_WIRE_FIXTURES=1` rewrites the committed files
// and the same run then certifies against what it just wrote, whatever
// order Expecto executes the cases in.
do
    if emitModeOn () then
        emit ()

// ─── Manifest reading ────────────────────────────────────────────────

/// One manifest entry, read back from the committed file rather than
/// from the emitter — so a manifest that disagrees with the emitter is
/// caught instead of being written twice from one source.
type private ManifestEntry = {
    Id: string
    Family: string
    Profile: string
    Kind: string
    File: string
    Sha256: string
    Reject: string option
    AgreedHash: string option
    Digest: string option
}

let private optionalString (element: JsonElement) (name: string) =
    match element.TryGetProperty name with
    | true, value -> Some(value.GetString())
    | _ -> None

let private requiredString (element: JsonElement) (name: string) =
    match element.TryGetProperty name with
    | true, value -> value.GetString()
    | _ -> failwithf "the wire-fixture manifest entry is missing the required '%s' field" name

let private manifest () =
    use document = JsonDocument.Parse(readCommitted "manifest.json")
    let root = document.RootElement

    let entries =
        root.GetProperty("vectors").EnumerateArray()
        |> Seq.map (fun entry -> {
            Id = requiredString entry "id"
            Family = requiredString entry "family"
            Profile = requiredString entry "profile"
            Kind = requiredString entry "kind"
            File = requiredString entry "file"
            Sha256 = requiredString entry "sha256"
            Reject = optionalString entry "reject"
            AgreedHash = optionalString entry "agreedHash"
            Digest = optionalString entry "digest"
        })
        |> List.ofSeq

    let profiles =
        root.GetProperty("profiles").EnumerateObject()
        |> Seq.map (fun property ->
            property.Name, property.Value.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq)
        |> Map.ofSeq

    entries, profiles

// ─── Certification ───────────────────────────────────────────────────

/// Re-serialise a committed document through the shape the manifest says
/// it carries. The dispatch is by family + id because the family names
/// the shape and, within `contract-invocation`, the leg names it.
let private reserialise (entry: ManifestEntry) (document: string) : string =
    match entry.Family, entry.Id with
    | "peer-surface", _
    | "aggregate-surface", _ -> JsonRpc.serialize (JsonRpc.deserialize<PeerSurfaceExport> document)
    | "pinned-exchange", _ -> JsonRpc.serialize (JsonRpc.deserialize<PinnedPeerSurface> document)
    | "attestation", _ -> JsonRpc.serialize (JsonRpc.deserialize<TemplateApprovalRecord> document)
    | "contract-invocation", "contract-invocation/request" ->
        JsonRpc.serialize (JsonRpc.deserialize<JsonRpcRequest> document)
    | "contract-invocation", "contract-invocation/response" ->
        JsonRpc.serialize (JsonRpc.deserialize<JsonRpcResponse> document)
    | "contract-invocation", "contract-invocation/errors" ->
        JsonRpc.serialize (JsonRpc.deserialize<JsonRpcResponse list> document)
    | "contract-invocation", "contract-invocation/job-poll" ->
        JsonRpc.serialize (JsonRpc.deserialize<PeerJobStatus<string> list> document)
    | "host-envelope", "host-envelope/stamp" -> JsonRpc.serialize (JsonRpc.deserialize<HostEnvelopeStamp> document)
    | "host-envelope", _ -> HostEnvelope.toJson (JsonRpc.deserialize<HostEnvelope> document)
    | family, id -> failwithf "no specified shape is bound to corpus family '%s' (vector '%s')" family id

/// Recompute the stamp a hash vector claims, from the document's own
/// content — the check an independent implementation runs to prove it
/// hashes what the specification says it hashes.
let private verifyStamp (entry: ManifestEntry) (document: string) : unit =
    match entry.Family with
    | "peer-surface"
    | "aggregate-surface" ->
        let export = JsonRpc.deserialize<PeerSurfaceExport> document
        let recomputed = (PeerSurface.export export.Surface).SurfaceHash

        Expect.equal
            recomputed
            export.SurfaceHash
            $"the stamp on '{entry.Id}' must be reproducible from the surface it stamps"
    | "attestation" ->
        let record = JsonRpc.deserialize<TemplateApprovalRecord> document

        let expected =
            Expect.wantSome entry.Digest $"'{entry.Id}' is a hash vector and must declare its digest"

        Expect.equal
            (TemplateCanonical.recordId record)
            expected
            $"the signing-input digest of '{entry.Id}' must be reproducible from the record's fields"
    | "host-envelope" ->
        // The stamp lives in one fixture and the document it stamps in
        // another, so certifying it is a cross-fixture check — which is
        // the point: a pinned stamp is only worth anything if it binds
        // to a document somebody else holds.
        let stamp = JsonRpc.deserialize<HostEnvelopeStamp> document

        let envelope =
            JsonRpc.deserialize<HostEnvelope> (readCommitted "host-envelope/envelope.json")

        Expect.equal
            (HostEnvelope.contentHash envelope)
            stamp.StampContentHash
            $"the stamp on '{entry.Id}' must be reproducible from the envelope document it stamps"
    | family -> failwithf "no stamp rule is bound to corpus family '%s'" family

/// The stable refusal classes a pinned-exchange reject vector may name,
/// mapped to the phrase the refusal carries. Named classes rather than
/// message matching in the vector itself: an implementation in another
/// language will word its refusal differently, and the class is what the
/// specification actually requires of it.
let private refusalPhrase =
    function
    | "pin-unparseable" -> "could not be parsed"
    | "pin-format-version-unreadable" -> "declares FormatVersion"
    | "pin-stamp-mismatch" -> "carries stamp"
    | "pin-hash-not-agreed" -> "expected hash agreed out of band"
    | other -> failwithf "the corpus names refusal class '%s', which this harness does not implement" other

let private verifyRejection (entry: ManifestEntry) (document: string) : unit =
    let reason =
        Expect.wantSome entry.Reject $"'{entry.Id}' is a reject vector and must name its refusal class"

    // The document's own stamp is the agreed hash unless the vector is
    // specifically about the two disagreeing.
    let agreed =
        entry.AgreedHash
        |> Option.defaultWith (fun () ->
            try
                (JsonRpc.deserialize<PeerSurfaceExport> document).SurfaceHash
            with _ ->
                // An unparseable document has no stamp to quote; the
                // refusal happens before the question arises.
                "")

    let outcome =
        FederationPin.ofExportJson "seller-ssp" "corpus" agreed DateTimeOffset.UtcNow document

    match outcome with
    | Ok _ -> failtestf "'%s' must be refused, and was accepted" entry.Id
    | Error message ->
        Expect.stringContains
            message
            (refusalPhrase reason)
            $"'{entry.Id}' must be refused as '{reason}'; the refusal said: {message}"

// ─── Tests ───────────────────────────────────────────────────────────

let private corpusShapeTests =
    testList "corpus shape" [
        test "the manifest enumerates at least one vector in every family" {
            let entries, profiles = manifest ()

            Expect.isGreaterThan entries.Length 0 "an empty corpus certifies nothing"

            let families = entries |> List.map _.Family |> Set.ofList

            for profile in [ "participant"; "gateway"; "module-host" ] do
                let required =
                    Expect.wantSome (Map.tryFind profile profiles) $"profile '{profile}' must be declared"

                Expect.isNonEmpty required $"profile '{profile}' must require at least one family"

                for family in required do
                    Expect.isTrue
                        (Set.contains family families)
                        $"profile '{profile}' requires family '{family}', which no vector covers"
        }

        test "the manifest and the corpus directory agree, in both directions" {
            let entries, _ = manifest ()

            let listed =
                entries |> List.map _.File |> List.append [ "manifest.json" ] |> List.sort

            let onDisk = filesOnDisk ()

            Expect.sequenceEqual
                onDisk
                listed
                "every corpus file is enumerated by the manifest and every enumerated file exists — an orphan fixture is one nothing certifies, and a phantom entry is a count that overstates the corpus"
        }

        test "every fixture's recorded digest matches its bytes" {
            let entries, _ = manifest ()

            for entry in entries do
                let document = readCommitted entry.File

                Expect.equal
                    (sha256Hex (Text.Encoding.UTF8.GetBytes document))
                    entry.Sha256
                    $"the manifest digest for '{entry.Id}' must match the fixture's own bytes"
        }
    ]

let private forwardCouplingTests =
    testList "forward coupling" [
        test "re-emitting the corpus reproduces the committed bytes" {
            let emitted = vectors ()

            for v in emitted do
                Expect.equal
                    (readCommitted v.File)
                    v.Document
                    $"the emitter and the committed fixture '{v.File}' have drifted apart. A shape change lands with its regenerated corpus in the SAME commit: set TOOLUP_EMIT_WIRE_FIXTURES=1 and re-run this pack, then commit docs/interplatform/wire-fixtures/ alongside the source change."
        }

        test "re-rendering the manifest reproduces the committed manifest" {
            Expect.equal
                (readCommitted "manifest.json")
                (renderManifest (vectors ()))
                "the manifest is generated from the same vector list the fixtures are; regenerate with TOOLUP_EMIT_WIRE_FIXTURES=1"
        }
    ]

let private certificationTests =
    testList "certification" [
        test "every corpus vector certifies against the in-tree emitters, and every one is run" {
            let entries, _ = manifest ()
            // Certification and its own accounting in one case, so the
            // count cannot be satisfied by a case that ran earlier and
            // is no longer connected to the work.
            let certified = HashSet<string>()

            for entry in entries do
                let document = readCommitted entry.File

                match entry.Kind with
                | "round-trip" ->
                    Expect.equal
                        (reserialise entry document)
                        document
                        $"'{entry.Id}' must re-serialise to the bytes it was read from"
                | "hash" ->
                    Expect.equal
                        (reserialise entry document)
                        document
                        $"'{entry.Id}' must re-serialise to the bytes it was read from"

                    verifyStamp entry document
                | "reject" -> verifyRejection entry document
                | kind -> failtestf "the corpus names vector kind '%s', which this harness does not implement" kind

                certified.Add entry.Id |> ignore

            Expect.isGreaterThan
                certified.Count
                0
                "no vector was certified — a conformance run that exercises nothing reports success for the same reason it reports anything"

            Expect.equal
                (Set.ofSeq certified)
                (entries |> List.map _.Id |> Set.ofList)
                "the set of certified vectors must equal the manifest's enumeration; a vector the harness skipped is a shape nothing holds the emitters to"
        }
    ]

/// The negative control the whole corpus rests on: a mutated document
/// must FAIL the same checks the committed ones pass. Without it, a
/// harness that had quietly stopped comparing would look identical to a
/// green one.
let private probeTests =
    testList "the corpus can go red" [
        test "a mutated document fails its round-trip digest" {
            let entries, _ = manifest ()

            let entry = entries |> List.find (fun e -> e.Id = "contract-invocation/request")

            let mutated = (readCommitted entry.File).Replace("PlaceOrder", "PlaceOrderX")

            Expect.notEqual
                (sha256Hex (Text.Encoding.UTF8.GetBytes mutated))
                entry.Sha256
                "a one-field mutation must move the fixture digest — otherwise the digest check is decorative"
        }

        test "a mutated surface fails its stamp check" {
            let document = readCommitted "peer-surface/instance.json"
            let export = JsonRpc.deserialize<PeerSurfaceExport> document

            let mutated = {
                export with
                    Surface = {
                        export.Surface with
                            LocalPeerId = Some "impostor"
                    }
            }

            Expect.notEqual
                (PeerSurface.export mutated.Surface).SurfaceHash
                mutated.SurfaceHash
                "an edited surface must no longer match the stamp it was published under"
        }

        test "the control: the same document IS accepted against the hash agreed out of band" {
            let document = readCommitted "peer-surface/instance.json"

            let accepted =
                FederationPin.ofExportJson
                    "seller-ssp"
                    "corpus"
                    (JsonRpc.deserialize<PeerSurfaceExport> document).SurfaceHash
                    DateTimeOffset.UtcNow
                    document

            Expect.isOk accepted "the control: the same document IS accepted against the hash agreed out of band"
        }
    ]

let tests =
    testList "Phase 596 — federation-seam wire conformance" [
        corpusShapeTests
        forwardCouplingTests
        certificationTests
        probeTests
    ]
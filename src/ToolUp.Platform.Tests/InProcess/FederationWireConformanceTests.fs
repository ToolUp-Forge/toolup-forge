module ToolUp.Platform.Tests.InProcess.FederationWireConformanceTests

// ─── Phase 596 — the federation seam certifies against its own spec ───
//
// The federation-seam wire specification specifies the seam; the corpus in
// its own public specification home is its executable half; this
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
    | "model-execution", "model-execution/submission"
    | "model-execution", "model-execution/view-request"
    | "model-execution", "model-execution/transition-request"
    | "model-execution", "model-execution/promotion-request" ->
        JsonRpc.serialize (JsonRpc.deserialize<ModelExecutionPeerRequest> document)
    | "model-execution", "model-execution/outcome"
    | "model-execution", "model-execution/transition"
    | "model-execution", "model-execution/promotion" ->
        JsonRpc.serialize (JsonRpc.deserialize<ModelExecutionPeerAnswer> document)
    | "model-execution", "model-execution/diagnostics"
    | "model-execution", "model-execution/refusals"
    | "model-execution", "model-execution/view" ->
        JsonRpc.serialize (JsonRpc.deserialize<ModelExecutionPeerAnswer list> document)
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

/// The phrase this implementation's pinning refusal carries, per class.
/// The corpus names a CLASS, not a message: an implementation in another
/// language will word its refusal differently, and the class is what the
/// specification actually requires of it. The mapping lives here because
/// it is this host's business, not the corpus's.
let private pinRefusalPhrase =
    function
    | "pin-unparseable" -> "could not be parsed"
    | "pin-format-version-unreadable" -> "declares FormatVersion"
    | "pin-stamp-mismatch" -> "carries stamp"
    | "pin-hash-not-agreed" -> "expected hash agreed out of band"
    | other -> failwithf "the corpus names pinning refusal class '%s', which this harness does not implement" other

/// Take a pin from a published export and require it to be refused.
let private verifyPinRejection (entry: ManifestEntry) (reason: string) (document: string) : unit =
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
            (pinRefusalPhrase reason)
            $"'{entry.Id}' must be refused as '{reason}'; the refusal said: {message}"

/// A malformed request envelope must not decode. The receiver's own
/// pre-dispatch parse collapses exactly this failure to the JSON-RPC
/// parse-error code, so refusing to decode IS the specified behaviour —
/// there is no partial read of a call the receiver cannot understand.
let private verifyInvocationRejection (entry: ManifestEntry) (document: string) : unit =
    let decoded =
        try
            Some(JsonRpc.deserialize<JsonRpcRequest> document)
        with _ ->
            None

    Expect.isNone decoded $"'{entry.Id}' must be refused before dispatch, and it decoded"

/// Feed a model-execution request envelope to the shipped reader and
/// require the named refusal CLASS back. The reader is
/// `ModelExecutionPeerContract.read` — the same function the seam's
/// dispatch runs, not a test-local re-implementation of it, so a
/// certification that passes here is a statement about what the
/// deployment does rather than about what the harness believes.
/// Phase 642 — the admission is per-vector, because an authority
/// refusal is a statement about a GRANT. Three vectors share one
/// document and differ only in the grant they are read against, which is
/// exactly the property the levels have to hold: the same request is
/// answered at one level, refused at another, and refused for a
/// different reason under a narrowing. `admissionFor` maps a vector id
/// to its grant; every pre-642 vector maps to the reference admission
/// and reads as it always did.
///
/// Phase 643 — a bounded-view vector is refused by a SECOND reader, and
/// the harness has to run both in order. `read` admits the envelope (it
/// is well-formed, declared, in-scope and granted — deliberately, so the
/// vector tests the bound and nothing else) and `PeerView.validate` then
/// judges the body against the deployment's declarations. A harness that
/// stopped at the envelope would report these two as ACCEPTED, which is
/// the failure the two-stage branch below exists to make impossible.
let private verifyModelExecutionRejection (entry: ManifestEntry) (reason: string) (document: string) : unit =
    let outcome =
        ModelExecutionPeerContract.read (admissionFor entry.Id) modelExecutionBoundScope document

    match outcome with
    | Ok request when reason.StartsWith "model-execution-view-" ->
        let view = JsonRpc.deserialize<PeerViewRequest> request.Body

        match PeerView.validate referenceViewDeclarations view with
        | Ok _ -> failtestf "'%s' must be refused by the view's declared bounds, and it was answered" entry.Id
        | Error refusal ->
            Expect.equal
                (PeerViewRefusal.className refusal)
                reason
                $"'{entry.Id}' must be refused as '{reason}'; the view reader said: {PeerViewRefusal.describe refusal}"
    // Phase 644 — a THIRD reader, and the harness runs it in order for
    // the same reason it runs the view's. The envelope check accepts a
    // transition invocation deliberately (it is well-formed, declared,
    // in-scope and needs no level above the floor), and
    // `ModelTransition.judge` — the pure author-agnostic judge a local
    // action and a policy verdict also go through — is what refuses.
    | Ok request when reason.StartsWith "model-execution-transition-" ->
        let invocation = JsonRpc.deserialize<PeerTransitionInvocation> request.Body
        let current, grant = transitionStateFor entry.Id

        match PeerTransition.target invocation with
        | None -> failtestf "'%s' must name a lifecycle status the profile defines" entry.Id
        | Some target ->
            let seamRequest = PeerTransition.toRequest "consortium-north" target invocation

            match ModelTransition.judge current grant seamRequest with
            | Ok _ -> failtestf "'%s' must be refused by the transition seam, and it was admitted" entry.Id
            | Error refusal ->
                Expect.equal
                    (PeerTransition.className refusal)
                    reason
                    $"'{entry.Id}' must be refused as '{reason}'; the seam said: {ModelTransitionRefusal.describe refusal}"
    // Phase 646 — a FOURTH reader, run in order for the same reason. The
    // envelope check accepts a transfer deliberately (well-formed,
    // declared, in-scope, and needing no level above the floor since a
    // transfer answers a receipt rather than data); `ModelPromotion.judge`
    // is what refuses, and for a lifecycle refusal it reaches
    // `ModelTransition.judge` — so one graph decides for a transfer, a
    // bare transition, a local action and a policy verdict alike.
    | Ok request when reason.StartsWith "model-execution-promotion-" ->
        let transfer = JsonRpc.deserialize<PeerPromotionTransfer> request.Body
        let existing, limits, grant = promotionStateFor entry.Id

        match PeerPromotion.target transfer with
        | None -> failtestf "'%s' must name a lifecycle status the profile defines" entry.Id
        | Some target ->
            match PeerPromotion.toPromoted "consortium-north" target transfer with
            | Error reason -> failtestf "'%s' must be readable as a transfer; it was not: %s" entry.Id reason
            | Ok promoted ->
                match ModelPromotion.judge existing limits grant promoted with
                | Ok _ -> failtestf "'%s' must be refused by the transfer seam, and it was admitted" entry.Id
                | Error refusal ->
                    Expect.equal
                        (PeerPromotion.className refusal)
                        reason
                        $"'{entry.Id}' must be refused as '{reason}'; the seam said: {ModelPromotionRefusal.describe refusal}"
    | Ok _ -> failtestf "'%s' must be refused, and was accepted" entry.Id
    | Error refusal ->
        Expect.equal
            (ModelExecutionPeerRefusal.className refusal)
            reason
            $"'{entry.Id}' must be refused as '{reason}'; the reader said: {ModelExecutionPeerRefusal.describe refusal}"

let private verifyRejection (entry: ManifestEntry) (document: string) : unit =
    let reason =
        Expect.wantSome entry.Reject $"'{entry.Id}' is a reject vector and must name its refusal class"

    match entry.Family, reason with
    | "pinned-exchange", _ -> verifyPinRejection entry reason document
    | "contract-invocation", "invocation-unparseable" -> verifyInvocationRejection entry document
    | "model-execution", _ -> verifyModelExecutionRejection entry reason document
    | family, cls ->
        failwithf "the corpus names refusal class '%s' in family '%s', which this harness does not implement" cls family

// ─── Tests ───────────────────────────────────────────────────────────

let private corpusShapeTests =
    testList "corpus shape" [
        test "the manifest enumerates at least one vector in every family" {
            let entries, profiles = manifest ()

            Expect.isGreaterThan entries.Length 0 "an empty corpus certifies nothing"

            let families = entries |> List.map _.Family |> Set.ofList

            for profile in
                [
                    "participant"
                    "gateway"
                    "module-host"
                    "participant-data-host"
                    "participant-modeller"
                ] do
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
                    $"the emitter and the committed fixture '{v.File}' have drifted apart. A shape change lands with its regenerated corpus in the SAME commit: set TOOLUP_EMIT_WIRE_FIXTURES=1 and re-run this pack, then commit the regenerated corpus IN THE SPECIFICATION HOME alongside the source change — it is a separate repository, so that is a second commit and a second push, not a file this one tracks."
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

/// The whole conformance leg, or a single test that says out loud why it is
/// not running.
///
/// The specification and its corpus live in their own public repository —
/// this one is an emitter certifying against them — so a checkout may not
/// have them. When they are absent the leg FAILS by default, naming the
/// remedy: a conformance suite that silently does nothing when its corpus is
/// missing is indistinguishable from one that passed, which is the failure
/// mode the whole corpus exists to prevent. Declining it is possible, but it
/// has to be said explicitly, and then it says so in the run output rather
/// than vanishing from it.
let tests =
    if FederationWireCorpus.specLegDeclined () then
        testList "Phase 596 — federation-seam wire conformance" [
            testCase "DECLINED — the specification home is absent and the leg was opted out"
            <| fun () ->
                printfn
                    "federation-seam conformance: DECLINED via %s. Nothing was certified."
                    FederationWireCorpus.SpecOptionalVariable
        ]
    else
        testList "Phase 596 — federation-seam wire conformance" [
            corpusShapeTests
            forwardCouplingTests
            certificationTests
            probeTests
        ]
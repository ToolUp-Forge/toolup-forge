module ToolUp.Platform.Tests.InProcess.SignedShapeSeparatorTests

open System
open System.Security.Cryptography
open System.Text
open FSharp.Reflection
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform

// ─── Phase 654 — the signed-shape separator registry ─────────────────
//
// Before this pack, **nothing in the repo tested a domain separator**,
// and that was verified rather than assumed during the 2026-08-18 rename
// wave: `TemplateApprovalTests` (50 cases), `CohortActivationTests` (34),
// `OutboundSignalFeedTests` (38) and the conformance drift guard (9) all
// passed, and not one of them would have caught an accidental change to
// any separator. The signal-feed test recomputes its key through the same
// function it asserts against, so it is invariant to the separator's
// value BY CONSTRUCTION; the approval and activation tests assert
// mutation-sensitivity and the `sha256:` prefix, not literals; the drift
// guard pins only values that reach the wire, and these do not. A
// separator could drift, or two modules could silently choose the same
// string — defeating domain separation altogether — with every gate
// green.
//
// This pack closes that, in three claims:
//
//   1. **The registry is the whole set.** `SignedShape.all` is checked
//      against the union's cases BY REFLECTION, so a hand-maintained list
//      cannot go stale behind a new case.
//   2. **The separators are pairwise distinct and well formed.**
//      Distinctness is the property domain separation rests on;
//      well-formedness is what makes `render` injective, without which
//      distinct strings would be an accident rather than a consequence.
//   3. **A changed separator fails loudly, per shape.** Every case is
//      derived from the union by reflection and looks its pin up in
//      `pins` — so a NEW shape inherits coverage and FAILS until someone
//      supplies a pin, rather than needing anyone to remember it. The
//      digest is taken through the shape's own canonical encoder, so the
//      pin proves the registry actually reaches the bytes, not merely
//      that the registry agrees with itself.
//
// **Why a digest and not just the separator string.** Pinning the string
// alone would prove the registry's value; pinning a digest THROUGH the
// encoder proves the encoder consumes it. Both are asserted, because they
// fail differently: a wrong string says the registry moved, a wrong
// digest with a right string says the encoding around it moved.

// ─── Fixtures: fixed, minimal, and never derived from a clock ────────

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private fixedGate: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Histogram ]
}

let private fixedTemplate: CleanRoomTemplate = {
    TemplateId = "phase-654-pinned"
    AllowedMethods = Set.ofList [ "EstimateReach" ]
    Floor = fixedGate
}

/// A fixed instant rather than `DateTimeOffset.UtcNow`: the record's
/// canonical encoding carries unix seconds, so a clock-derived fixture
/// would make the pinned digest un-pinnable.
let private fixedInstant = DateTimeOffset.FromUnixTimeSeconds 1_700_000_000L

let private fixedRecord: TemplateApprovalRecord = {
    TemplateId = "phase-654-pinned"
    TemplateVersion = "sha256:" + String('0', 64)
    ActingPeerId = "peer-acting"
    CounterpartyPeerId = "peer-counterparty"
    Action = TemplateApproved
    IssuedAt = fixedInstant
    NotBefore = fixedInstant
    ExpiresAt = None
    // Deliberately not a real signature: `recordBytes` covers every field
    // EXCEPT this one, which is what signs it.
    Signature = "not-covered-by-recordBytes"
}

let private fixedAuthorisation: ActivationAuthorisation = {
    Cohort = {
        CohortId = "phase-654-cohort"
        Definition = CohortMembers [ "member-a"; "member-b" ]
        Constraints = {
            MinCohortSize = 10
            Predicates = [ { Name = "recency"; Value = "P30D" } ]
        }
    }
    Purpose = {
        PurposeId = "reach-measurement"
        Description = "A fixed purpose, pinned so the digest is stable."
    }
    Destination = {
        DestinationId = "phase-654-destination"
        CounterpartyPeerId = "peer-counterparty"
        PermittedShapes = Set.ofList [ ReleaseCount ]
        Floor = fixedGate
    }
}

/// Constructed by hand rather than via `FitCompositeKey.compute`, so the
/// pinned digest does not move when the identity-hash construction does.
/// `Hash` is the only field the promoted-artifact signing input reads
/// from the key beyond the five it names explicitly.
let private fixedFitKey: FitCompositeKey = {
    SpecHash = "spec-" + String('1', 8)
    DatasetVersion = "dataset-2026-08-18"
    Seed = 654L
    ProviderId = "reference"
    ProviderVersion = "0.1.0"
    Hash = String('c', 64)
}

let private fixedHandle = Guid.Parse "00000000-0000-0000-0000-000000000654"

let private fixedEnvelope: WorkerOutcomeSignature = {
    WorkerId = "phase-654-worker"
    KeyId = "phase-654-key"
    SignedAt = "2026-08-18T00:00:00Z"
    ArtifactHash = String('a', 64)
    DiagnosticsHash = String('b', 64)
    // Not part of `signingPayload` — it is what the payload is signed
    // into.
    Signature = "not-covered-by-signingPayload"
}

/// Phase 676 — a fixed payload for the generic subject encoder. Not
/// derived from any live type: the pin's job is to notice the ENCODING
/// moving, so its input must not move with a domain's record shape.
let private fixedSubjectPayload = Encoding.UTF8.GetBytes "phase-676-payload"

let private fixedCountersignatureSubject: CountersignatureSubject =
    CountersignatureSubject.ofCanonicalBytes "phase-676-kind" "phase-676-subject" fixedSubjectPayload

let private fixedCountersignatureRecord: CountersignatureRecord = {
    Subject = fixedCountersignatureSubject
    // Deliberately written UNSORTED: the encoder emits a roster
    // count-first in ordinal order, so if this pin ever moves after a
    // roster is reordered somewhere, the encoder stopped canonicalising.
    Roster = [ "party-c"; "party-a"; "party-b" ]
    ActingPartyId = "party-a"
    Action = SubjectApproved
    IssuedAt = fixedInstant
    NotBefore = fixedInstant
    ExpiresAt = None
    // Deliberately not a real signature: `recordBytes` covers every
    // field EXCEPT this one, which is what signs it.
    Signature = "not-covered-by-recordBytes"
}

// ─── The pins ────────────────────────────────────────────────────────

type private ShapePin = {
    /// The exact separator text this shape must render.
    Separator: string
    /// A digest taken through this shape's OWN canonical encoder over the
    /// fixed input above. Changing the separator changes this; so does
    /// changing anything else in the encoding, which is the point.
    Digest: unit -> string
    /// The pinned value of that digest.
    ExpectedDigest: string
}

/// One entry per `SignedShape`. **Not enumerated here on purpose** — the
/// test cases are derived from the union by reflection and look their pin
/// up in this map, so a shape added without a pin FAILS with a message
/// telling the author what to add.
let private pins: Map<SignedShape, ShapePin> =
    Map.ofList [
        SignedShape.CleanRoomTemplate,
        {
            Separator = "fuaran.federation.cleanroom.template/1"
            Digest = fun () -> sha256Hex (TemplateCanonical.templateBytes fixedTemplate)
            ExpectedDigest = "386de3787fed5aeb823473776c1f2e0a379149f083375a7154d871fc25e8d5a4"
        }

        SignedShape.CleanRoomApprovalRecord,
        {
            Separator = "fuaran.federation.cleanroom.approval/1"
            Digest = fun () -> sha256Hex (TemplateCanonical.recordBytes fixedRecord)
            ExpectedDigest = "5ee11ae7a8fe605e2125482d323c3bd722aaffb95891cf91603cdce2de169de5"
        }

        SignedShape.ActivationAuthorisation,
        {
            Separator = "fuaran.federation.activation.authorisation/1"
            Digest = fun () -> sha256Hex (ActivationCanonical.bytes fixedAuthorisation)
            ExpectedDigest = "2488ef3b169b3880dc7cf9eb50533284169d61669a6fb77fec7f402994f6ee1b"
        }

        SignedShape.SignalFeedDelivery,
        {
            Separator = "fuaran.federation.signalfeed.delivery/1"
            // Already a lowercase-hex SHA-256 — the key IS the digest.
            Digest = fun () -> SignalFeedCanonical.idempotencyKey "phase-654-feed" "phase-654-auth" 654L
            ExpectedDigest = "3fbe2693a96f65b13d9a2fcc8bf38fdb77aaf5aaec0cfce79799462802a34578"
        }

        // The separator the three rename passes never found, because it
        // lived inside a `sprintf` format string rather than a named
        // binding. Its VALUE is unchanged; what changed is that it is now
        // enumerable.
        SignedShape.PromotedArtifact,
        {
            Separator = "fuaran.federation.promoted-artifact/1"
            Digest =
                fun () ->
                    ModelPromotionSigningInput.bytes fixedFitKey ModelArtifactStatus.Approved [ "att-b"; "att-a" ]
                    |> sha256Hex
            ExpectedDigest = "265c39c7be70f383ace4deb4e6abf3db6f2957fb4d4ab62ef113a84ce1eb9ab6"
        }

        // The one shape whose separator VALUE moved at Phase 654:
        // `toolup.signed-outcome.v1` -> `toolup.signed-outcome/1`, so
        // that the version suffix matches the scheme the other four
        // already used. The `toolup` branding is deliberate and
        // unchanged. Pre-654 this fixture digested to
        // b92109145fe2e12119e23cb6f1db9e6527c26cd94c508a01523c05ff37d75b52;
        // the other four shapes digest IDENTICALLY before and after,
        // which is how the refactor was shown to be value-preserving
        // everywhere it claimed to be.
        SignedShape.WorkerSignedOutcome,
        {
            Separator = "toolup.signed-outcome/1"
            Digest =
                fun () ->
                    WorkerOutcomeSignature.signingPayload fixedHandle fixedEnvelope
                    |> Encoding.UTF8.GetBytes
                    |> sha256Hex
            ExpectedDigest = "7c9d3185544e745eba705fcf23299a90c21b8b256001c39c5b28c4c9941a9cfc"
        }

        // Phase 676 — the generic countersignature core's two shapes.
        // Branded `toolup` because they name a platform substrate
        // rather than a cross-deployment wire protocol; see the note
        // beside them in `SignedShape.parts`.
        SignedShape.CountersignatureSubject,
        {
            Separator = "toolup.countersignature.subject/1"
            Digest =
                fun () ->
                    CountersignatureCanonical.subjectBytes "phase-676-kind" "phase-676-subject" fixedSubjectPayload
                    |> sha256Hex
            ExpectedDigest = "76f58313017cfe3214496081b9089ec7f82eef70acea5cdd70524fe457815ae6"
        }

        SignedShape.CountersignatureRecord,
        {
            Separator = "toolup.countersignature.record/1"
            Digest = fun () -> sha256Hex (CountersignatureCanonical.recordBytes fixedCountersignatureRecord)
            ExpectedDigest = "c80d397313ad0a2427b6d269e2b125cf05dccdb2a69d1f34ccc72ed452151a26"
        }
    ]

// ─── Case derivation: reflection, so coverage is inherited ───────────

/// Every case of `SignedShape`, obtained from the union itself rather
/// than from a list anyone maintains. This is the mechanism that makes a
/// new shape inherit coverage.
let private reflectedShapes: SignedShape list =
    FSharpType.GetUnionCases typeof<SignedShape>
    |> Array.toList
    |> List.map (fun case ->
        if case.GetFields().Length > 0 then
            failwithf
                "SignedShape.%s carries fields. This pack constructs each case with no arguments; extend it deliberately rather than loosening the derivation."
                case.Name

        FSharpValue.MakeUnion(case, [||]) :?> SignedShape)

let private shapeName (shape: SignedShape) : string =
    let case, _ = FSharpValue.GetUnionFields(shape, typeof<SignedShape>)
    case.Name

// ─── Registry-level claims ───────────────────────────────────────────

let registryTests =
    testList "Phase 654 - signed-shape separator registry" [
        test "SignedShape.all covers every union case" {
            Expect.equal
                (Set.ofList SignedShape.all)
                (Set.ofList reflectedShapes)
                "SignedShape.all has drifted from the union's cases — a shape was added to the DU without being added to `all`."
        }

        test "SignedShape.all has no duplicates" {
            Expect.equal
                (List.length SignedShape.all)
                (List.length (List.distinct SignedShape.all))
                "SignedShape.all lists a shape twice."
        }

        test "every separator is well formed" {
            for shape in reflectedShapes do
                match SignedShapeSeparator.validate (SignedShape.parts shape) with
                | Ok() -> ()
                | Error reason ->
                    failtestf
                        "SignedShape.%s has a malformed separator: %s. Well-formedness is not tidiness — it is what makes `render` injective, and without it the collision check below asserts nothing."
                        (shapeName shape)
                        reason
        }

        // The failure mode that silently defeats domain separation
        // altogether: two shapes opening their encodings with the same
        // bytes means a signature over one can be replayed as the other.
        test "separators are pairwise distinct" {
            let rendered =
                reflectedShapes |> List.map (fun s -> shapeName s, SignedShape.separator s)

            let collisions =
                rendered
                |> List.groupBy snd
                |> List.filter (fun (_, group) -> List.length group > 1)

            match collisions with
            | [] -> ()
            | _ ->
                let detail =
                    collisions
                    |> List.map (fun (sep, group) ->
                        let names = group |> List.map fst |> String.concat ", "
                        $"'%s{sep}' shared by %s{names}")
                    |> String.concat "; "

                failtestf
                    "domain separation is defeated — %s. A signature minted over one of these shapes can be replayed as the other."
                    detail
        }

        // Distinct PARTS is the stronger statement, and given
        // well-formedness it implies distinct strings. Asserted
        // separately so a future change to `render` that lost injectivity
        // would show up here rather than as a puzzling string collision.
        test "separator parts are pairwise distinct" {
            let parts = reflectedShapes |> List.map SignedShape.parts

            Expect.equal
                (List.length (List.distinct parts))
                (List.length parts)
                "two shapes share the same (vendor, path, version) parts."
        }

        test "no pin names a shape that no longer exists" {
            let live = Set.ofList reflectedShapes

            let stale =
                pins
                |> Map.toList
                |> List.map fst
                |> List.filter (fun s -> not (live.Contains s))

            Expect.isEmpty stale "a pin survives for a shape that has been removed from SignedShape."
        }
    ]

// ─── Per-shape pins, derived from the union ──────────────────────────

let private pinCase (shape: SignedShape) =
    let name = shapeName shape

    test name {
        match Map.tryFind shape pins with
        | None ->
            failtestf
                "SignedShape.%s has no pinned digest. A new signed shape inherits this test rather than needing anyone to remember it — add an entry to `pins` in this file naming its separator and a digest taken through its own canonical encoder over a fixed input."
                name
        | Some pin ->
            // Two assertions that fail differently on purpose: a wrong
            // separator says the REGISTRY moved; a right separator with a
            // wrong digest says the encoding AROUND it moved.
            Expect.equal
                (SignedShape.separator shape)
                pin.Separator
                $"the domain separator for SignedShape.%s{name} has changed. This is a BREAKING WIRE CHANGE: every signature already minted over this shape stops verifying. If it is deliberate, bump the separator's Version, update this pin and write a migration note."

            Expect.equal
                (pin.Digest())
                pin.ExpectedDigest
                $"the canonical encoding of SignedShape.%s{name} has changed over a fixed input. Either its domain separator moved or its field encoding did; both invalidate every signature already minted over this shape."
    }

let pinnedDigestTests =
    testList "Phase 654 - pinned canonical digests" (reflectedShapes |> List.map pinCase)
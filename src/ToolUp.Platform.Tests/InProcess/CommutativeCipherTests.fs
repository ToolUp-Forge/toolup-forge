module ToolUp.Platform.Tests.InProcess.CommutativeCipherTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.InterPlatform

// ─── Phase 18f — commutative cipher (OPRF) + two-party PSI ───────────
//
// The contract pack for `ICommutativeCipher` and the private-set-
// intersection protocol above it.
//
// **The laws are predicates, not assertions, and that is deliberate.**
// A commutativity assertion written directly as an Expecto case would
// pass against a cipher whose `Apply` returns its input unchanged —
// `id ∘ id = id ∘ id` — so it would prove nothing about the shipped
// backends beyond "they are functions". Every law here is therefore a
// `Result<unit, string>` predicate, consumed twice: `laws` binds them to
// a real backend and asserts `Ok`, and `selfTests` binds them to two
// deliberately-broken ciphers and asserts the SPECIFIC laws that must
// catch each one return `Error`. A law that stopped having teeth fails
// the self-test rather than quietly passing everywhere.
//
// The two controls are chosen to fail on different axes:
//   - `NoOpCipher` is perfectly commutative and perfectly reversible, and
//     hides nothing. It is caught by non-degeneracy and key separation.
//   - `OrderDependentCipher` hides its input completely and separates
//     keys, and does not commute. It is caught by commutativity and the
//     peel round-trip.
// Between them, no single law carries the pack, and no law is decorative.

// ── The laws ─────────────────────────────────────────────────────────

let private sample = Text.Encoding.UTF8.GetBytes "contract-pack/sample"
let private other = Text.Encoding.UTF8.GetBytes "contract-pack/other"

let private check (condition: bool) (failure: string) =
    if condition then Ok() else Error failure

/// `Apply ka ∘ Apply kb = Apply kb ∘ Apply ka` — the defining property.
let private commutes (cipher: ICommutativeCipher) =
    let ka = cipher.GenerateKey()
    let kb = cipher.GenerateKey()
    let point = cipher.HashToPoint sample

    match
        cipher.Apply ka point |> Result.bind (cipher.Apply kb), cipher.Apply kb point |> Result.bind (cipher.Apply ka)
    with
    | Ok ab, Ok ba -> check (ab = ba) "applying two keys in the two orders produced different elements"
    | Error e, _
    | _, Error e -> Error $"a double application failed: {e}"

/// `Remove k ∘ Apply k = id` — a key peels back off exactly.
let private peels (cipher: ICommutativeCipher) =
    let key = cipher.GenerateKey()
    let point = cipher.HashToPoint sample

    match cipher.Apply key point |> Result.bind (cipher.Remove key) with
    | Ok recovered -> check (recovered = point) "removing the key did not recover the original element"
    | Error e -> Error $"the peel round-trip failed: {e}"

/// Non-degeneracy: applying a key must actually change the element. This
/// is the law a no-op cipher fails, and the reason commutativity alone is
/// not a contract.
let private blinds (cipher: ICommutativeCipher) =
    let key = cipher.GenerateKey()
    let point = cipher.HashToPoint sample

    match cipher.Apply key point with
    | Ok blinded -> check (blinded <> point) "applying a key left the element unchanged — the cipher hides nothing"
    | Error e -> Error $"apply failed: {e}"

/// Distinct keys must produce distinct ciphertexts for the same element —
/// otherwise the "key" is not a key.
let private separatesKeys (cipher: ICommutativeCipher) =
    let ka = cipher.GenerateKey()
    let kb = cipher.GenerateKey()
    let point = cipher.HashToPoint sample

    match cipher.Apply ka point, cipher.Apply kb point with
    | Ok a, Ok b -> check (a <> b) "two different keys produced the same ciphertext"
    | Error e, _
    | _, Error e -> Error $"apply failed: {e}"

/// `HashToPoint` must be deterministic, or two deployments derive
/// different elements for the same identifier and nothing ever matches.
let private hashesDeterministically (cipher: ICommutativeCipher) =
    check (cipher.HashToPoint sample = cipher.HashToPoint sample) "HashToPoint is not deterministic"

/// Distinct inputs must land on distinct elements, or unrelated
/// identifiers would match.
let private hashesDistinctly (cipher: ICommutativeCipher) =
    check (cipher.HashToPoint sample <> cipher.HashToPoint other) "two different inputs hashed to the same element"

let private allLaws = [
    "commutes across keys", commutes
    "peels a key back off", peels
    "actually blinds (non-degenerate)", blinds
    "separates distinct keys", separatesKeys
    "hashes deterministically", hashesDeterministically
    "hashes distinct inputs distinctly", hashesDistinctly
]

/// Bind every law to a backend and assert it holds.
let private laws (name: string) (cipher: unit -> ICommutativeCipher) =
    testList name [
        for label, law in allLaws do
            testCase label
            <| fun () ->
                match law (cipher ()) with
                | Ok() -> ()
                | Error reason -> failtestf "%s: %s" label reason
    ]

// ── The negative controls ────────────────────────────────────────────

/// Commutative, reversible, and hides nothing. Every law about ORDER
/// passes; the laws about SECRECY must fail.
type private NoOpCipher() =
    interface ICommutativeCipher with
        member _.GenerateKey() = RandomNumberGenerator.GetBytes 32
        member _.HashToPoint(input) = SHA256.HashData input
        member _.Apply _key point = Ok point
        member _.Remove _key point = Ok point

/// Hides its input completely and separates keys, but chains rather than
/// commutes: `SHA256(kb ‖ SHA256(ka ‖ P))` depends on the order. Every law
/// about SECRECY passes; the laws about ORDER must fail.
type private OrderDependentCipher() =
    interface ICommutativeCipher with
        member _.GenerateKey() = RandomNumberGenerator.GetBytes 32
        member _.HashToPoint(input) = SHA256.HashData input

        member _.Apply key point =
            Ok(SHA256.HashData(Array.append key point))

        member _.Remove _key _point = Error InvalidKey

// ── Cross-backend / rejection fixtures ───────────────────────────────

let private reference () = InMemoryCommutativeCipher.create ()
let private curve () = EcCommutativeCipher.create ()

/// P-256's group order, from FIPS 186-4 / SEC 2. Pinned as a published
/// constant because Phase 18f's stated acceptance — matching published
/// Ristretto255 vectors — is not reachable from this backend (see
/// `EcCommutativeCipher`'s header for why Ristretto255 is deferred). This
/// is the equivalent guarantee available: it proves the backend really is
/// on the curve it names, rather than on whatever a library default
/// resolved to.
[<Literal>]
let private P256Order =
    "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"

/// Regression vectors for the curve backend, recorded from the shipped
/// implementation. They pin the encoding and the derivation — a change to
/// the domain-separation string, the try-and-increment loop, or the point
/// encoding moves them, and moving them is a wire-compatibility break
/// between two deployments on different SDK versions.
[<Literal>]
let private P256HashOfAlice =
    "026B5AC7523E7F565BC4BBB60D8F5D4795B90E0FB4089E341B1CFEE61DA854A929"

[<Literal>]
let private P256AppliedToAlice =
    "02DEBC528499943B10989A5B83DA5F415EA333EF3EF4C031A93DDC39E142448AC2"

// ── PSI fixtures ─────────────────────────────────────────────────────

let private utf8 (s: string) = Text.Encoding.UTF8.GetBytes s
let private ofUtf8 (b: byte[]) = Text.Encoding.UTF8.GetString b

/// Wire the responder straight onto the initiator's exchange closure. Two
/// independent keys, two independent element sets — the only thing shared
/// is the cipher's public construction, which is the point.
let private twoParty (cipher: ICommutativeCipher) (theirs: string list) (theirKey: byte[]) =
    let psi = PrivateSetIntersection.create cipher

    fun (request: PsiRequest) -> async {
        match psi.Respond(theirKey, theirs |> List.map utf8, request) with
        | Ok response -> return Ok response
        | Error e -> return Error(PeerHandler $"%A{e}")
    }

let private runIntersect (cipher: ICommutativeCipher) (mine: string list) (myKey: byte[]) exchange =
    let psi = PrivateSetIntersection.create cipher
    psi.Intersect(myKey, mine |> List.map utf8, exchange) |> Async.RunSynchronously

// ── Phase 479 — release modes ────────────────────────────────────────
//
// Two things need proving here, and only one of them is a correctness
// question.
//
// The correctness half is ordinary: a cardinality equals the plaintext
// intersection size, an aggregate equals the plaintext sum, a mode
// mismatch refuses. Those are assertions.
//
// The LEAKAGE half cannot be written as an assertion, because "reveals
// only the cardinality" has no direct positive form — a test asserting
// that the outcome record carries no member list would pass against an
// implementation that leaked every member on the wire and merely dropped
// them at the last step. So leakage is MEASURED instead, by an attacker:
// `positionalMembership` is the strongest thing an initiator can compute
// from a transcript it legitimately holds, and it is run against both
// modes over identical fixtures. Against `Members` it recovers the exact
// membership vector on every trial — the mode is supposed to leak that,
// and a control that failed to leak would mean the attacker was broken
// rather than the shuffle effective. Against `CardinalityOnly` the same
// attacker, with the same knowledge, fails — while the COUNT it extracts
// stays exactly right on every trial. Count preserved, linkage destroyed,
// measured on one transcript against a control that is not a straw man
// but a shipped mode.

/// Blind `elements` under `key` in the given order — the initiator's own
/// first step, performed by hand so the attacker below knows the order it
/// sent in. A real initiator knows its own permutation, so taking the
/// identity here is without loss of generality.
let private blindWith (cipher: ICommutativeCipher) (key: byte[]) (elements: string list) =
    elements
    |> List.map (fun element ->
        match cipher.Apply key (cipher.HashToPoint(utf8 element)) with
        | Ok blinded -> Convert.ToBase64String blinded
        | Error e -> failtestf "apply failed: %A" e)

let private applyToken (cipher: ICommutativeCipher) (key: byte[]) (token: string) =
    match cipher.Apply key (Convert.FromBase64String token) with
    | Ok applied -> Convert.ToBase64String applied
    | Error e -> failtestf "apply failed: %A" e

/// The attacker. Given everything the initiator legitimately holds — its
/// key and the full response — recover, for each POSITION of `A₂`, whether
/// that position matched. In `Members` mode position `i` is element `i`,
/// so this IS the membership vector.
let private positionalMembership (cipher: ICommutativeCipher) (myKey: byte[]) (response: PsiModeResponse) =
    let partner =
        response.PartnerBlinded |> List.map (applyToken cipher myKey) |> Set.ofList

    response.Doubled |> List.map (fun token -> Set.contains token partner)

/// The one quantity `CardinalityOnly` is entitled to release, computed
/// from the same transcript the attacker above works over.
let private transcriptCardinality (cipher: ICommutativeCipher) (myKey: byte[]) (response: PsiModeResponse) =
    positionalMembership cipher myKey response |> List.filter id |> List.length

let private modeResponder
    (cipher: ICommutativeCipher)
    (theirs: string list)
    (theirKey: byte[])
    (policy: PsiResponderPolicy)
    =
    let modes = PrivateSetIntersection.createModes cipher

    fun (request: PsiModeRequest) -> async {
        match modes.RespondAs(theirKey, theirs |> List.map utf8, policy, request) with
        | Ok response -> return Ok response
        | Error e -> return Error(PeerHandler $"%A{e}")
    }

let private payloadResponder
    (cipher: ICommutativeCipher)
    (theirs: (string * byte[]) list)
    (theirKey: byte[])
    (policy: PsiResponderPolicy)
    =
    let modes = PrivateSetIntersection.createModes cipher

    fun (request: PsiModeRequest) -> async {
        let elements =
            theirs
            |> List.map (fun (element, payload) -> PsiPayloadElement.create (utf8 element) payload)

        match modes.RespondWithPayloads(theirKey, elements, policy, request) with
        | Ok response -> return Ok response
        | Error e -> return Error(PeerHandler $"%A{e}")
    }

let private runMode (cipher: ICommutativeCipher) (options: PsiRunOptions) (mine: string list) (myKey: byte[]) exchange =
    let modes = PrivateSetIntersection.createModes cipher

    modes.IntersectAs(options, myKey, mine |> List.map utf8, exchange)
    |> Async.RunSynchronously

/// A responder that serves everything, with no input floor. The floor is
/// exercised on its own below.
let private servesEverything =
    PsiResponderPolicy.create [ Members; CardinalityOnly; AggregatePayload ] 0

// ── Aggregate payload fixtures ───────────────────────────────────────
//
// Payloads are powers of two in the low half of a 16-byte accumulator, so
// the aggregate is a BITMASK naming exactly which elements were summed.
// Any mispairing of a payload with an element — the failure the shared
// permutation exists to prevent — produces a different mask, not merely a
// different total, so the alignment is checked rather than assumed.

let private aggregateWidth = 16

let private payloadOf (value: uint64) =
    let bytes = Array.zeroCreate<byte> aggregateWidth
    let encoded = BitConverter.GetBytes value |> Array.rev
    Array.blit encoded 0 bytes (aggregateWidth - 8) 8
    bytes

let private valueOf (bytes: byte[]) =
    BitConverter.ToUInt64(bytes[aggregateWidth - 8 ..] |> Array.rev, 0)

/// A stand-in for a real additive-homomorphic context: the same additive
/// group, DECLARED concealing. It exists because the shipped reference
/// mechanism is honestly non-concealing, so the permitted-by-default path
/// has nothing else to exercise it with.
type private ConcealingAggregator(inner: IPsiAggregator) =
    interface IPsiAggregator with
        member _.IsConcealing = true
        member _.Zero = inner.Zero
        member _.Combine left right = inner.Combine left right

let private referenceAggregator () =
    InMemoryPsiAggregator() :> IPsiAggregator

let private concealingAggregator () =
    ConcealingAggregator(referenceAggregator ()) :> IPsiAggregator

let private aggregateOptions (aggregator: IPsiAggregator) =
    PsiRunOptions.create AggregatePayload |> PsiRunOptions.withAggregator aggregator

// ── Aggregator laws ──────────────────────────────────────────────────
//
// The same shape the cipher laws take, for the same reason: an assertion
// that `Combine a b` returns *something* would pass against a mechanism
// that discarded its right operand. Each law is a predicate, bound below
// to the reference mechanism (must hold) and to two deliberately-broken
// ones (each must fail the specific laws that exist to catch it).

let private one = payloadOf 1UL
let private two = payloadOf 2UL
let private four = payloadOf 4UL

let private combined (aggregator: IPsiAggregator) left right =
    match aggregator.Combine left right with
    | Ok value -> value
    | Error e -> failwithf "combine failed: %A" e

/// `Combine Zero p = p` — without it the fold's seed corrupts every
/// aggregate.
let private zeroIsIdentity (aggregator: IPsiAggregator) =
    match aggregator.Combine aggregator.Zero one with
    | Ok value -> check (value = one) "combining with Zero did not return the payload unchanged"
    | Error e -> Error $"combine failed: {e}"

/// Commutativity. The intersection arrives through a shuffle, so a
/// mechanism sensitive to order returns a different aggregate per run for
/// the same answer.
let private combinesCommutatively (aggregator: IPsiAggregator) =
    match aggregator.Combine one two, aggregator.Combine two one with
    | Ok ab, Ok ba -> check (ab = ba) "combining two payloads in the two orders produced different aggregates"
    | Error e, _
    | _, Error e -> Error $"combine failed: {e}"

/// Associativity. The fold is left-to-right over a set; a non-associative
/// mechanism makes the answer depend on how the set happened to be listed.
let private combinesAssociatively (aggregator: IPsiAggregator) =
    let left = combined aggregator (combined aggregator one two) four
    let right = combined aggregator one (combined aggregator two four)
    check (left = right) "regrouping the same three payloads produced different aggregates"

let private aggregatorLaws = [
    "zero is the identity", zeroIsIdentity
    "combines commutatively", combinesCommutatively
    "combines associatively", combinesAssociatively
]

/// Order-dependent: hashes its two operands together. Fails every law.
type private HashingAggregator() =
    interface IPsiAggregator with
        member _.IsConcealing = true
        member _.Zero = Array.zeroCreate aggregateWidth

        member _.Combine left right =
            Ok(SHA256.HashData(Array.append left right)[0 .. aggregateWidth - 1])

/// Keeps its left operand. Associative — and neither commutative nor
/// identity-respecting, so it is caught on a different axis from the
/// hashing control and no single law carries the pack.
type private FirstWinsAggregator() =
    interface IPsiAggregator with
        member _.IsConcealing = true
        member _.Zero = Array.zeroCreate aggregateWidth
        member _.Combine left _right = Ok left

// ── Clean-room gate fixtures (479.C) ─────────────────────────────────

let private gateFloor (k: int) (suppression: int) : PrivacyGate = {
    MinCohortSize = k
    SuppressionThreshold = suppression
    PermittedShapes = Set.ofList [ OutputShape.Count; OutputShape.Aggregate ]
}

let private gateTemplate (methods: string list) (floor: PrivacyGate) : CleanRoomTemplate = {
    TemplateId = "psi-479"
    AllowedMethods = Set.ofList methods
    Floor = floor
}

let private releaseGate (template: CleanRoomTemplate) (methodName: string) (requested: PrivacyGate option) = {
    Broker = CleanRoomBroker.create ()
    Template = template
    MethodName = methodName
    Requested = requested
}

// ── The pack ─────────────────────────────────────────────────────────

let tests =
    testList "Phase 18f ICommutativeCipher" [
        laws "reference backend (modular exponentiation)" reference
        laws "production backend (NIST P-256)" curve
        // Cross-curve conformance: the same laws on two further
        // prime-order curves, so the backend is bound to the PROPERTY
        // (cofactor 1) rather than to one hard-coded curve.
        laws "production backend (NIST P-384)" (fun () -> EcCommutativeCipher.onCurve "P-384")
        laws "production backend (secp256k1)" (fun () -> EcCommutativeCipher.onCurve "secp256k1")

        testList "published parameters + regression vectors" [
            testCase "the default backend is on NIST P-256"
            <| fun () ->
                let order = Convert.ToHexString(EcCommutativeCipher().GroupOrder)
                Expect.equal order P256Order "the group order is not P-256's published order"

            testCase "HashToPoint is pinned to its recorded vector"
            <| fun () ->
                let encoded = Convert.ToHexString((curve ()).HashToPoint(utf8 "alice"))
                Expect.equal encoded P256HashOfAlice "the hash-to-curve derivation moved — this is a wire break"

            testCase "Apply is pinned to its recorded vector"
            <| fun () ->
                let key = Array.init 32 (fun i -> byte (i + 1))

                match (curve ()).Apply key ((curve ()).HashToPoint(utf8 "alice")) with
                | Ok applied ->
                    Expect.equal
                        (Convert.ToHexString applied)
                        P256AppliedToAlice
                        "the scalar-multiplication result moved — this is a wire break"
                | Error e -> failtestf "apply failed: %A" e

            testCase "an unknown curve is refused at construction"
            <| fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () -> EcCommutativeCipher.onCurve "definitely-not-a-curve" |> ignore)
                    "an unknown curve name must fail loudly at construction, not at first use"
        ]

        testList "malformed input is data, not an exception" [
            testCase "a zero key is InvalidKey"
            <| fun () ->
                let cipher = curve ()
                let result = cipher.Apply (Array.zeroCreate 32) (cipher.HashToPoint sample)
                Expect.equal result (Error InvalidKey) "a zero scalar is not a usable key"

            testCase "a wrong-length key is LengthMismatch"
            <| fun () ->
                let cipher = curve ()
                let result = cipher.Apply (Array.zeroCreate 31) (cipher.HashToPoint sample)
                Expect.equal result (Error LengthMismatch) "a short key must be rejected on length"

            testCase "a garbage element is InvalidPoint"
            <| fun () ->
                let cipher = curve ()
                let garbage = Array.create 33 0xFFuy
                Expect.equal (cipher.Apply (cipher.GenerateKey()) garbage) (Error InvalidPoint) "not a curve point"

            testCase "the reference backend rejects an element outside its subgroup"
            <| fun () ->
                // The full multiplicative group modulo the safe prime has
                // order 2q; a quadratic NON-residue sits outside the
                // prime-order subgroup the cipher operates in. Feeding one
                // in is the small-subgroup probe, and it must be refused
                // rather than answered.
                let cipher = reference ()
                let key = cipher.GenerateKey()
                let valid = cipher.HashToPoint sample

                // Roughly half the neighbours of a residue are
                // non-residues, so scan a fixed window rather than flip
                // one bit and hope — a single flip would make this case a
                // coin toss.
                let outcomes = [
                    for delta in 1uy .. 32uy do
                        let tampered = Array.copy valid
                        tampered[tampered.Length - 1] <- tampered[tampered.Length - 1] ^^^ delta
                        cipher.Apply key tampered
                ]

                // About half the scan lands back on a legitimate residue
                // and is rightly accepted; what must never be empty is the
                // rejected half.
                Expect.contains
                    outcomes
                    (Error NotOnCurve)
                    "no tampered element in the scan was rejected as outside the prime-order subgroup"

            testCase "a reference-backend element is refused by the curve backend"
            <| fun () ->
                // The magic tag exists so a mixed-backend wiring mistake
                // fails at the first Apply rather than completing a
                // protocol run nobody reviewed.
                let cipher = curve ()
                let foreign = (reference ()).HashToPoint sample

                match cipher.Apply (cipher.GenerateKey()) foreign with
                | Error _ -> ()
                | Ok _ -> failtest "the curve backend accepted a reference-backend element"

            testCase "a curve-backend element is refused by the reference backend"
            <| fun () ->
                let cipher = reference ()
                let foreign = (curve ()).HashToPoint sample

                match cipher.Apply (cipher.GenerateKey()) foreign with
                | Error _ -> ()
                | Ok _ -> failtest "the reference backend accepted a curve-backend element"

            testCase "the reference backend's elements are visibly marked"
            <| fun () ->
                let marked = (reference ()).HashToPoint sample
                let prefix = Text.Encoding.ASCII.GetString(marked, 0, 8)

                Expect.equal
                    prefix
                    "TU!INSEC"
                    "the reference backend's magic tag is what makes accidental production use fail loudly"
        ]

        testList "constant-time comparison" [
            testCase "bytesEqual agrees with structural equality"
            <| fun () ->
                let a = RandomNumberGenerator.GetBytes 32
                Expect.isTrue (CommutativeCipher.bytesEqual a (Array.copy a)) "equal buffers compare equal"
                Expect.isFalse (CommutativeCipher.bytesEqual a (RandomNumberGenerator.GetBytes 32)) "distinct differ"
                Expect.isFalse (CommutativeCipher.bytesEqual a (Array.zeroCreate 31)) "different lengths differ"
                Expect.isFalse (CommutativeCipher.bytesEqual a null) "null is never equal"
        ]

        testList "two-party private set intersection" [
            testCase "the intersection equals the plaintext set intersection"
            <| fun () ->
                let cipher = curve ()
                let alice = [ "ann"; "bob"; "cat"; "dan" ]
                let bob = [ "cat"; "dan"; "eve" ]

                match runIntersect cipher alice (cipher.GenerateKey()) (twoParty cipher bob (cipher.GenerateKey())) with
                | Ok outcome ->
                    Expect.equal
                        (outcome.MatchedElements |> List.map ofUtf8)
                        [ "cat"; "dan" ]
                        "the recovered pre-images must be exactly the plaintext intersection, in input order"

                    Expect.equal
                        (List.length outcome.MatchedTokens)
                        2
                        "one opaque token accompanies each matched pre-image"
                | Error e -> failtestf "PSI failed: %A" e

            testCase "the reference backend reaches the same answer"
            <| fun () ->
                // Cross-backend conformance: the protocol is a property of
                // the seam, not of one implementation.
                let cipher = reference ()
                let alice = [ "ann"; "bob"; "cat" ]
                let bob = [ "bob"; "zoe" ]

                match runIntersect cipher alice (cipher.GenerateKey()) (twoParty cipher bob (cipher.GenerateKey())) with
                | Ok outcome ->
                    Expect.equal
                        (outcome.MatchedElements |> List.map ofUtf8)
                        [ "bob" ]
                        "intersection over the reference backend"
                | Error e -> failtestf "PSI failed: %A" e

            testCase "disjoint sets intersect to nothing"
            <| fun () ->
                let cipher = curve ()

                match
                    runIntersect
                        cipher
                        [ "ann"; "bob" ]
                        (cipher.GenerateKey())
                        (twoParty cipher [ "yan"; "zoe" ] (cipher.GenerateKey()))
                with
                | Ok outcome -> Expect.isEmpty outcome.MatchedElements "disjoint sets must produce no matches"
                | Error e -> failtestf "PSI failed: %A" e

            testCase "no non-matching element is recoverable from the transcript"
            <| fun () ->
                // The acceptance clause: every element that did NOT match
                // must appear on the wire only as an opaque group element.
                // Assert the strongest mechanical form of that — no
                // transcript token equals a raw element, and none equals
                // an UNBLINDED hash-to-point (which would be invertible by
                // dictionary attack over the identifier domain).
                let cipher = curve ()
                let alice = [ "ann"; "bob"; "cat" ]
                let bob = [ "cat"; "zoe" ]
                let transcript = ResizeArray<string>()

                let recordingExchange =
                    let inner = twoParty cipher bob (cipher.GenerateKey())

                    fun (request: PsiRequest) -> async {
                        transcript.AddRange request.Blinded
                        let! answer = inner request

                        match answer with
                        | Ok response ->
                            transcript.AddRange response.PartnerBlinded
                            transcript.AddRange response.Doubled
                        | Error _ -> ()

                        return answer
                    }

                match runIntersect cipher alice (cipher.GenerateKey()) recordingExchange with
                | Error e -> failtestf "PSI failed: %A" e
                | Ok _ ->
                    Expect.isGreaterThan transcript.Count 0 "the transcript must not be empty"

                    let decoded = transcript |> Seq.map Convert.FromBase64String |> List.ofSeq

                    let forbidden = [
                        for element in alice @ bob do
                            utf8 element
                            cipher.HashToPoint(utf8 element)
                    ]

                    for token in decoded do
                        for leak in forbidden do
                            Expect.isFalse
                                (CommutativeCipher.bytesEqual token leak)
                                "a transcript token equalled a raw element or an unblinded hash — invertible by dictionary attack"

            testCase "a counterparty answering a different session is refused"
            <| fun () ->
                let cipher = curve ()

                let liar =
                    let inner = twoParty cipher [ "cat" ] (cipher.GenerateKey())

                    fun request -> async {
                        let! answer = inner request

                        return
                            answer
                            |> Result.map (fun r -> {
                                r with
                                    SessionId = Guid.NewGuid().ToString "N"
                            })
                    }

                match runIntersect cipher [ "cat" ] (cipher.GenerateKey()) liar with
                | Error(PsiProtocol reason) ->
                    Expect.stringContains reason "different session" "session binding is enforced"
                | other -> failtestf "expected a session-mismatch protocol error, got %A" other

            testCase "a counterparty dropping tokens is refused rather than answered short"
            <| fun () ->
                // Silently intersecting a truncated echo would report a
                // SMALLER intersection than the truth — a wrong answer
                // wearing a success, which is the outcome a privacy
                // primitive must never produce quietly.
                let cipher = curve ()

                let truncating =
                    let inner = twoParty cipher [ "cat"; "dan" ] (cipher.GenerateKey())

                    fun request -> async {
                        let! answer = inner request

                        return
                            answer
                            |> Result.map (fun r -> {
                                r with
                                    Doubled = r.Doubled |> List.truncate 1
                            })
                    }

                match runIntersect cipher [ "cat"; "dan" ] (cipher.GenerateKey()) truncating with
                | Error(PsiProtocol reason) ->
                    Expect.stringContains reason "request order" "the count mismatch is reported, not absorbed"
                | other -> failtestf "expected a token-count protocol error, got %A" other

            testCase "a transport failure surfaces as PsiTransport"
            <| fun () ->
                let cipher = curve ()
                let failing _ = async { return Error(PeerTransport "connection reset") }

                match runIntersect cipher [ "ann" ] (cipher.GenerateKey()) failing with
                | Error(PsiTransport(PeerTransport message)) ->
                    Expect.stringContains message "connection reset" "the transport error is carried through"
                | other -> failtestf "expected PsiTransport, got %A" other

            testCase "the responder shuffles its own list"
            <| fun () ->
                // The shuffle is protocol, not hygiene: unshuffled, the
                // position of a responder token leaks the responder's
                // element ordering. Assert the default permutation is
                // actually applied by observing that a long list comes
                // back in a different order than the responder's own
                // blinding produced.
                let cipher = reference ()
                let psi = PrivateSetIntersection.create cipher
                let key = cipher.GenerateKey()
                let elements = [ for i in 1..16 -> utf8 $"element-{i}" ]

                let blindInOrder =
                    elements
                    |> List.map (fun e ->
                        match cipher.Apply key (cipher.HashToPoint e) with
                        | Ok blinded -> Convert.ToBase64String blinded
                        | Error e -> failtestf "apply failed: %A" e)

                let empty: PsiRequest = { SessionId = "s"; Blinded = [] }

                match psi.Respond(key, elements, empty) with
                | Ok response ->
                    Expect.equal
                        (List.sort response.PartnerBlinded)
                        (List.sort blindInOrder)
                        "the shuffle must permute, never alter, the responder's token set"

                    Expect.notEqual
                        response.PartnerBlinded
                        blindInOrder
                        "the responder's list came back in blinding order — the shuffle did not run"
                | Error e -> failtestf "respond failed: %A" e
        ]

        testList "Phase 479 mode negotiation" [
            testCase "a responder refuses a mode it does not accept"
            <| fun () ->
                let cipher = curve ()
                let modes = PrivateSetIntersection.createModes cipher

                let request = {
                    SessionId = "s"
                    Mode = CardinalityOnly
                    Blinded = blindWith cipher (cipher.GenerateKey()) [ "ann"; "bob" ]
                }

                match
                    modes.RespondAs(cipher.GenerateKey(), [ utf8 "ann" ], PsiResponderPolicy.membersOnly, request)
                with
                | Error(PsiProtocol reason) ->
                    Expect.stringContains reason "does not accept" "the refusal names the unaccepted mode"
                | other -> failtestf "expected a mode refusal, got %A" other

            testCase "a responder refuses a LESS revealing mode it did not agree to"
            <| fun () ->
                // The negotiation is not an ordering. A responder that
                // signed up to answer cardinalities has not thereby agreed
                // to hand over the member set, and one that signed up to
                // hand over members has not agreed to be counted either —
                // "safer" is not the same as "reviewed".
                let cipher = curve ()
                let modes = PrivateSetIntersection.createModes cipher

                let request = {
                    SessionId = "s"
                    Mode = Members
                    Blinded = blindWith cipher (cipher.GenerateKey()) [ "ann" ]
                }

                let cardinalityOnly = PsiResponderPolicy.create [ CardinalityOnly ] 0

                match modes.RespondAs(cipher.GenerateKey(), [ utf8 "ann" ], cardinalityOnly, request) with
                | Error(PsiProtocol reason) -> Expect.stringContains reason "does not accept" "the refusal is symmetric"
                | other -> failtestf "expected a mode refusal, got %A" other

            testCase "an answer echoing a different mode is refused by the initiator"
            <| fun () ->
                // The other half of the negotiation. Without this check a
                // responder could answer a cardinality request with the
                // order-preserving Members transcript and the initiator
                // would read membership out of it believing it had asked
                // for a count.
                let cipher = curve ()

                let lying =
                    let inner = modeResponder cipher [ "ann" ] (cipher.GenerateKey()) servesEverything

                    fun (request: PsiModeRequest) -> async {
                        let! answer = inner request
                        return answer |> Result.map (fun response -> { response with Mode = Members })
                    }

                match runMode cipher (PsiRunOptions.create CardinalityOnly) [ "ann" ] (cipher.GenerateKey()) lying with
                | Error(PsiProtocol reason) ->
                    Expect.stringContains reason "not negotiable" "the mode echo is checked, not trusted"
                | other -> failtestf "expected a mode-echo protocol error, got %A" other

            testCase "a payload-free responder refuses AggregatePayload rather than aggregating nothing"
            <| fun () ->
                let cipher = curve ()
                let modes = PrivateSetIntersection.createModes cipher

                let request = {
                    SessionId = "s"
                    Mode = AggregatePayload
                    Blinded = blindWith cipher (cipher.GenerateKey()) [ "ann" ]
                }

                match modes.RespondAs(cipher.GenerateKey(), [ utf8 "ann" ], servesEverything, request) with
                | Error(PsiProtocol reason) ->
                    Expect.stringContains reason "carries no payloads" "the refusal names the missing payloads"
                | other -> failtestf "expected a payload refusal, got %A" other

            testCase "the responder's input floor refuses a near-singleton probe"
            <| fun () ->
                // The singleton probe is the whole differencing attack in
                // one call: ask for |{x} ∩ Y| and read membership off a
                // number that is 0 or 1. The floor is checked against the
                // token count the responder can see, which is the one
                // quantity the initiator cannot understate.
                let cipher = curve ()
                let modes = PrivateSetIntersection.createModes cipher
                let key = cipher.GenerateKey()
                let policy = PsiResponderPolicy.create [ CardinalityOnly ] 5

                let probe = {
                    SessionId = "s"
                    Mode = CardinalityOnly
                    Blinded = blindWith cipher key [ "ann" ]
                }

                match modes.RespondAs(cipher.GenerateKey(), [ utf8 "ann" ], policy, probe) with
                | Error(PsiProtocol reason) ->
                    Expect.stringContains reason "minimum input cohort" "the floor names itself"
                | other -> failtestf "expected an input-cohort refusal, got %A" other

                let cohort = {
                    SessionId = "s"
                    Mode = CardinalityOnly
                    Blinded = blindWith cipher key [ "ann"; "bob"; "cat"; "dan"; "eve" ]
                }

                match modes.RespondAs(cipher.GenerateKey(), [ utf8 "ann" ], policy, cohort) with
                | Ok _ -> ()
                | other -> failtestf "a set at the floor must be served, got %A" other
        ]

        testList "Phase 479 cardinality-only" [
            testCase "the cardinality equals the plaintext intersection size"
            <| fun () ->
                let cipher = curve ()
                let alice = [ "ann"; "bob"; "cat"; "dan" ]
                let bob = [ "cat"; "dan"; "eve" ]

                match
                    runMode
                        cipher
                        (PsiRunOptions.create CardinalityOnly)
                        alice
                        (cipher.GenerateKey())
                        (modeResponder cipher bob (cipher.GenerateKey()) servesEverything)
                with
                | Ok(CardinalityReleased matched) -> Expect.equal matched 2 "the overlap is {cat, dan}"
                | other -> failtestf "expected a cardinality, got %A" other

            testCase "the reference backend reaches the same count"
            <| fun () ->
                let cipher = reference ()

                match
                    runMode
                        cipher
                        (PsiRunOptions.create CardinalityOnly)
                        [ "ann"; "bob"; "cat" ]
                        (cipher.GenerateKey())
                        (modeResponder cipher [ "bob"; "zoe" ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(CardinalityReleased matched) -> Expect.equal matched 1 "cross-backend conformance"
                | other -> failtestf "expected a cardinality, got %A" other

            testCase "disjoint sets release zero"
            <| fun () ->
                let cipher = curve ()

                match
                    runMode
                        cipher
                        (PsiRunOptions.create CardinalityOnly)
                        [ "ann"; "bob" ]
                        (cipher.GenerateKey())
                        (modeResponder cipher [ "yan"; "zoe" ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(CardinalityReleased matched) -> Expect.equal matched 0 "no overlap"
                | other -> failtestf "expected a cardinality, got %A" other

            testCase "the shuffle destroys linkage while preserving the count"
            <| fun () ->
                // The leakage measurement. See the section header above:
                // one attacker, two transcripts, and the control is a
                // SHIPPED mode rather than a straw man.
                let cipher = curve ()
                let modes = PrivateSetIntersection.createModes cipher
                let mine = [ for i in 1..12 -> $"element-{i}" ]
                let theirs = [ "element-2"; "element-5"; "element-7"; "element-11"; "outsider" ]
                let truth = mine |> List.map (fun element -> List.contains element theirs)
                let expected = truth |> List.filter id |> List.length
                let trials = 40

                let sweep mode = [
                    for _ in 1..trials do
                        let ka = cipher.GenerateKey()
                        let kb = cipher.GenerateKey()

                        let request = {
                            SessionId = "s"
                            Mode = mode
                            Blinded = blindWith cipher ka mine
                        }

                        match modes.RespondAs(kb, theirs |> List.map utf8, servesEverything, request) with
                        | Ok response ->
                            positionalMembership cipher ka response, transcriptCardinality cipher ka response
                        | Error e -> failtestf "respond failed: %A" e
                ]

                let membersRuns = sweep Members
                let cardinalityRuns = sweep CardinalityOnly

                // If the control did NOT leak, the attacker would be
                // broken and the comparison below would prove nothing.
                Expect.equal
                    (membersRuns
                     |> List.filter (fun (recovered, _) -> recovered = truth)
                     |> List.length)
                    trials
                    "the Members transcript must reveal the exact membership vector — it is the control that leaks"

                // A random permutation reproduces this 4-of-12 pattern
                // with probability 1/495, so a sweep that never failed
                // would be a permutation that is not permuting.
                Expect.isLessThan
                    (cardinalityRuns
                     |> List.filter (fun (recovered, _) -> recovered = truth)
                     |> List.length)
                    trials
                    "positional membership survived every CardinalityOnly trial — A₂ is not being permuted"

                // …and the one quantity the mode IS entitled to release
                // comes off the same shuffled transcript exactly right,
                // every time.
                Expect.equal
                    (cardinalityRuns |> List.map snd |> List.distinct)
                    [ expected ]
                    "the count read off the shuffled transcript must be the plaintext intersection size, every time"

            testCase "the initiator shuffles its own request in the non-Members modes"
            <| fun () ->
                // `Members` cannot shuffle `A₁` — it needs the echo mapped
                // back. The other modes can, and do, which denies the
                // responder the ordering oracle over the INITIATOR's set
                // that `B₁`'s shuffle denies the initiator over the
                // responder's.
                let cipher = reference ()
                let key = cipher.GenerateKey()
                let mine = [ for i in 1..16 -> $"element-{i}" ]
                let inOrder = blindWith cipher key mine
                let seen = ResizeArray<string list>()

                let recording (request: PsiModeRequest) = async {
                    seen.Add request.Blinded

                    let inner =
                        modeResponder cipher [ "outsider" ] (cipher.GenerateKey()) servesEverything

                    return! inner request
                }

                runMode cipher (PsiRunOptions.create Members) mine key recording |> ignore

                runMode cipher (PsiRunOptions.create CardinalityOnly) mine key recording
                |> ignore

                Expect.equal (List.sort seen[1]) (List.sort inOrder) "the shuffle permutes, never alters, the token set"

                Expect.notEqual
                    seen[1]
                    seen[0]
                    "the CardinalityOnly request went out in the Members order — A₁ was not shuffled"

            testCase "no non-matching element is recoverable from a cardinality transcript"
            <| fun () ->
                let cipher = curve ()
                let alice = [ "ann"; "bob"; "cat" ]
                let bob = [ "cat"; "zoe" ]
                let transcript = ResizeArray<string>()

                let recording =
                    let inner = modeResponder cipher bob (cipher.GenerateKey()) servesEverything

                    fun (request: PsiModeRequest) -> async {
                        transcript.AddRange request.Blinded
                        let! answer = inner request

                        match answer with
                        | Ok response ->
                            transcript.AddRange response.PartnerBlinded
                            transcript.AddRange response.Doubled
                        | Error _ -> ()

                        return answer
                    }

                match runMode cipher (PsiRunOptions.create CardinalityOnly) alice (cipher.GenerateKey()) recording with
                | Error e -> failtestf "PSI failed: %A" e
                | Ok _ ->
                    Expect.isGreaterThan transcript.Count 0 "the transcript must not be empty"

                    let decoded = transcript |> Seq.map Convert.FromBase64String |> List.ofSeq

                    let forbidden = [
                        for element in alice @ bob do
                            utf8 element
                            cipher.HashToPoint(utf8 element)
                    ]

                    for token in decoded do
                        for leak in forbidden do
                            Expect.isFalse
                                (CommutativeCipher.bytesEqual token leak)
                                "a transcript token equalled a raw element or an unblinded hash"

            testCase "the mode path's Members answer agrees with the Phase 18f seam"
            <| fun () ->
                let cipher = curve ()
                let alice = [ "ann"; "bob"; "cat"; "dan" ]
                let bob = [ "bob"; "dan"; "eve" ]

                match
                    runMode
                        cipher
                        (PsiRunOptions.create Members)
                        alice
                        (cipher.GenerateKey())
                        (modeResponder cipher bob (cipher.GenerateKey()) servesEverything)
                with
                | Ok(MembersReleased outcome) ->
                    Expect.equal
                        (outcome.MatchedElements |> List.map ofUtf8)
                        [ "bob"; "dan" ]
                        "the two seams must not drift — they decide membership through the same code"
                | other -> failtestf "expected a members release, got %A" other
        ]

        testList "Phase 479 aggregate payload" [
            testCase "the aggregate is the plaintext sum over the intersection"
            <| fun () ->
                let cipher = curve ()
                let alice = [ "ann"; "bob"; "cat"; "dan" ]

                let bob = [ "bob", payloadOf 2UL; "cat", payloadOf 4UL; "eve", payloadOf 16UL ]

                match
                    runMode
                        cipher
                        (aggregateOptions (concealingAggregator ()))
                        alice
                        (cipher.GenerateKey())
                        (payloadResponder cipher bob (cipher.GenerateKey()) servesEverything)
                with
                | Ok(AggregateReleased(matched, aggregate)) ->
                    Expect.equal matched 2 "the overlap is {bob, cat}"
                    Expect.equal (valueOf aggregate) 6UL "the mask names exactly bob and cat"
                | other -> failtestf "expected an aggregate release, got %A" other

            testCase "each payload stays attached to its own element through the shuffle"
            <| fun () ->
                // Powers of two, so the aggregate is a bitmask rather than
                // a total: a payload paired with the wrong element after
                // the shuffle produces a DIFFERENT mask, where a plain sum
                // over a symmetric fixture could coincide.
                let cipher = curve ()
                let alice = [ "e1"; "e3"; "e5"; "e7" ]

                let bob = [ for i in 1..8 -> $"e{i}", payloadOf (1UL <<< (i - 1)) ]

                match
                    runMode
                        cipher
                        (aggregateOptions (concealingAggregator ()))
                        alice
                        (cipher.GenerateKey())
                        (payloadResponder cipher bob (cipher.GenerateKey()) servesEverything)
                with
                | Ok(AggregateReleased(matched, aggregate)) ->
                    Expect.equal matched 4 "four of the eight overlap"

                    Expect.equal
                        (valueOf aggregate)
                        0b01010101UL
                        "the mask must name e1, e3, e5 and e7 — any other value is a payload paired with the wrong element"
                | other -> failtestf "expected an aggregate release, got %A" other

            testCase "the reference backend reaches the same aggregate"
            <| fun () ->
                let cipher = reference ()
                let alice = [ "ann"; "bob" ]
                let bob = [ "bob", payloadOf 8UL; "zoe", payloadOf 1UL ]

                match
                    runMode
                        cipher
                        (aggregateOptions (concealingAggregator ()))
                        alice
                        (cipher.GenerateKey())
                        (payloadResponder cipher bob (cipher.GenerateKey()) servesEverything)
                with
                | Ok(AggregateReleased(matched, aggregate)) ->
                    Expect.equal matched 1 "cross-backend conformance"
                    Expect.equal (valueOf aggregate) 8UL "cross-backend conformance"
                | other -> failtestf "expected an aggregate release, got %A" other

            testCase "an aggregate run with no mechanism is refused before anything is sent"
            <| fun () ->
                let cipher = curve ()
                let reached = ref false

                let observing =
                    fun (_: PsiModeRequest) -> async {
                        reached.Value <- true
                        return Error(PeerTransport "should never be reached")
                    }

                match
                    runMode cipher (PsiRunOptions.create AggregatePayload) [ "ann" ] (cipher.GenerateKey()) observing
                with
                | Error(PsiConfiguration reason) ->
                    Expect.stringContains reason "IPsiAggregator" "the refusal names the missing mechanism"

                    Expect.isFalse
                        reached.Value
                        "the run reached the wire before refusing — it had already told the counterparty its set size"
                | other -> failtestf "expected a configuration refusal, got %A" other

            testCase "a revealing mechanism is refused unless the deployment says otherwise"
            <| fun () ->
                // The mode's central caveat, enforced rather than
                // documented: aggregation happens initiator-side, so a
                // readable payload space turns an "aggregate" into
                // per-element disclosure of the responder's values.
                let cipher = curve ()
                let alice = [ "ann"; "bob" ]
                let bob = [ "bob", payloadOf 2UL ]

                let exchange () =
                    payloadResponder cipher bob (cipher.GenerateKey()) servesEverything

                Expect.isFalse
                    (referenceAggregator ()).IsConcealing
                    "the shipped reference mechanism must declare itself revealing — it performs no encryption at all"

                match
                    runMode
                        cipher
                        (aggregateOptions (referenceAggregator ()))
                        alice
                        (cipher.GenerateKey())
                        (exchange ())
                with
                | Error(PsiConfiguration reason) ->
                    Expect.stringContains reason "IsConcealing" "the refusal names the declared property"
                | other -> failtestf "expected a configuration refusal, got %A" other

                match
                    runMode
                        cipher
                        (aggregateOptions (referenceAggregator ())
                         |> PsiRunOptions.allowingRevealingAggregator)
                        alice
                        (cipher.GenerateKey())
                        (exchange ())
                with
                | Ok(AggregateReleased(matched, aggregate)) ->
                    Expect.equal matched 1 "the reviewed opt-in runs"
                    Expect.equal (valueOf aggregate) 2UL "the reviewed opt-in runs"
                | other -> failtestf "expected an aggregate release under the opt-in, got %A" other

            testCase "misaligned payloads fail the run rather than aggregating the wrong values"
            <| fun () ->
                let cipher = curve ()

                let truncating =
                    let inner =
                        payloadResponder
                            cipher
                            [ "bob", payloadOf 2UL; "cat", payloadOf 4UL ]
                            (cipher.GenerateKey())
                            servesEverything

                    fun (request: PsiModeRequest) -> async {
                        let! answer = inner request

                        return
                            answer
                            |> Result.map (fun response -> {
                                response with
                                    Payloads = response.Payloads |> List.truncate 1
                            })
                    }

                match
                    runMode
                        cipher
                        (aggregateOptions (concealingAggregator ()))
                        [ "bob"; "cat" ]
                        (cipher.GenerateKey())
                        truncating
                with
                | Error(PsiProtocol reason) ->
                    Expect.stringContains reason "alignment" "the count mismatch is reported, not absorbed"
                | other -> failtestf "expected an alignment protocol error, got %A" other

            testList "the aggregate mechanism's laws" [
                for label, law in aggregatorLaws do
                    testCase label
                    <| fun () ->
                        match law (referenceAggregator ()) with
                        | Ok() -> ()
                        | Error reason -> failtestf "%s: %s" label reason

                testCase "a width mismatch is data, not an exception"
                <| fun () ->
                    let aggregator = referenceAggregator ()

                    Expect.equal
                        (aggregator.Combine (Array.zeroCreate 8) (Array.zeroCreate 16))
                        (Error AggregateWidthMismatch)
                        "a short ciphertext must be refused on width"

                    Expect.equal
                        (aggregator.Combine null (Array.zeroCreate 16))
                        (Error AggregateMalformed)
                        "a null ciphertext must be refused as malformed"

                testCase "the accumulator wraps rather than growing"
                <| fun () ->
                    // Closure is what makes the combine associative in a
                    // fixed width, so wrapping is the contract rather than
                    // an overflow bug. A caller whose values can wrap has
                    // chosen the width wrongly, and that is a caller
                    // decision (GP 1).
                    let aggregator = referenceAggregator ()
                    let top = Array.create aggregateWidth 0xFFuy

                    match aggregator.Combine top (payloadOf 1UL) with
                    | Ok wrapped ->
                        Expect.equal wrapped (Array.zeroCreate aggregateWidth) "2^128 - 1 plus one wraps to zero"
                    | Error e -> failtestf "combine failed: %A" e
            ]
        ]

        testList "Phase 479 clean-room gate composition" [
            testCase "a sub-k cardinality is withheld with an audit reason"
            <| fun () ->
                let cipher = curve ()
                let template = gateTemplate [ "overlap" ] (gateFloor 5 0)

                let options =
                    PsiRunOptions.create CardinalityOnly
                    |> PsiRunOptions.withRelease (releaseGate template "overlap" None)

                match
                    runMode
                        cipher
                        options
                        [ "ann"; "bob"; "cat" ]
                        (cipher.GenerateKey())
                        (modeResponder cipher [ "cat"; "zoe" ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(ReleaseWithheld reason) ->
                    Expect.stringContains reason "k-anonymity" "the withhold carries the broker's own reason"
                | other -> failtestf "expected a withheld release, got %A" other

            testCase "a cardinality at the floor is released"
            <| fun () ->
                let cipher = curve ()
                let template = gateTemplate [ "overlap" ] (gateFloor 2 0)

                let options =
                    PsiRunOptions.create CardinalityOnly
                    |> PsiRunOptions.withRelease (releaseGate template "overlap" None)

                match
                    runMode
                        cipher
                        options
                        [ "ann"; "bob"; "cat" ]
                        (cipher.GenerateKey())
                        (modeResponder cipher [ "bob"; "cat"; "zoe" ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(CardinalityReleased matched) -> Expect.equal matched 2 "at the floor, the answer is released"
                | other -> failtestf "expected a released cardinality, got %A" other

            testCase "an ungated run releases the same sub-k answer"
            <| fun () ->
                // The control for the two cases above: without the gate
                // the identical fixture releases, so the withhold is the
                // gate binding rather than the protocol failing.
                let cipher = curve ()

                match
                    runMode
                        cipher
                        (PsiRunOptions.create CardinalityOnly)
                        [ "ann"; "bob"; "cat" ]
                        (cipher.GenerateKey())
                        (modeResponder cipher [ "cat"; "zoe" ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(CardinalityReleased matched) -> Expect.equal matched 1 "ungated, the sub-k answer is released"
                | other -> failtestf "expected a released cardinality, got %A" other

            testCase "a method off the template surface is withheld"
            <| fun () ->
                let cipher = curve ()
                let template = gateTemplate [ "overlap" ] (gateFloor 0 0)

                let options =
                    PsiRunOptions.create CardinalityOnly
                    |> PsiRunOptions.withRelease (releaseGate template "something-else" None)

                match
                    runMode
                        cipher
                        options
                        [ "ann" ]
                        (cipher.GenerateKey())
                        (modeResponder cipher [ "ann" ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(ReleaseWithheld reason) ->
                    Expect.stringContains reason "not on clean-room template" "surface enforcement carries through"
                | other -> failtestf "expected a withheld release, got %A" other

            testCase "a caller-requested gate may only tighten the floor"
            <| fun () ->
                let cipher = curve ()
                let template = gateTemplate [ "overlap" ] (gateFloor 1 0)

                let options =
                    PsiRunOptions.create CardinalityOnly
                    |> PsiRunOptions.withRelease (releaseGate template "overlap" (Some(gateFloor 10 0)))

                match
                    runMode
                        cipher
                        options
                        [ "ann"; "bob" ]
                        (cipher.GenerateKey())
                        (modeResponder cipher [ "bob" ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(ReleaseWithheld _) -> ()
                | other -> failtestf "the composed gate must be the stricter of the two, got %A" other

            testCase "a partially-suppressed cohort is withheld, never released as a smaller number"
            <| fun () ->
                // A suppression threshold above the released cohort drops
                // the cell, leaving the broker's cleared cohort smaller
                // than the intersection actually computed. Releasing that
                // number as "the intersection size" would be a wrong
                // answer wearing a success.
                let cipher = curve ()
                let template = gateTemplate [ "overlap" ] (gateFloor 0 10)

                let options =
                    PsiRunOptions.create CardinalityOnly
                    |> PsiRunOptions.withRelease (releaseGate template "overlap" None)

                match
                    runMode
                        cipher
                        options
                        [ "ann"; "bob" ]
                        (cipher.GenerateKey())
                        (modeResponder cipher [ "bob" ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(ReleaseWithheld reason) ->
                    Expect.stringContains reason "partial cardinality" "the partial release is refused explicitly"
                | other -> failtestf "expected a withheld release, got %A" other

            testCase "an aggregate over a sub-k intersection is withheld"
            <| fun () ->
                let cipher = curve ()
                let template = gateTemplate [ "spend" ] (gateFloor 5 0)

                let options =
                    aggregateOptions (concealingAggregator ())
                    |> PsiRunOptions.withRelease (releaseGate template "spend" None)

                match
                    runMode
                        cipher
                        options
                        [ "ann"; "bob" ]
                        (cipher.GenerateKey())
                        (payloadResponder cipher [ "bob", payloadOf 2UL ] (cipher.GenerateKey()) servesEverything)
                with
                | Ok(ReleaseWithheld reason) ->
                    Expect.stringContains reason "k-anonymity" "the aggregate clears the same floor a count does"
                | other -> failtestf "expected a withheld release, got %A" other
        ]
    ]

/// The negative controls. Each broken cipher is asserted to FAIL the laws
/// that exist to catch it and to PASS the ones it genuinely satisfies —
/// the second half matters as much as the first, because a law that
/// rejected everything would also "catch" both controls while proving
/// nothing.
let selfTests =
    let expectFailure (name: string) (label: string) (law: ICommutativeCipher -> Result<unit, string>) cipher =
        testCase $"{name}: '{label}' catches it"
        <| fun () ->
            match law cipher with
            | Error _ -> ()
            | Ok() -> failtestf "'%s' passed against %s — the law has no teeth" label name

    let expectPass (name: string) (label: string) (law: ICommutativeCipher -> Result<unit, string>) cipher =
        testCase $"{name}: '{label}' does not fire"
        <| fun () ->
            match law cipher with
            | Ok() -> ()
            | Error reason -> failtestf "'%s' fired against %s, which genuinely satisfies it: %s" label name reason

    testList "Phase 18f ICommutativeCipher (negative controls)" [
        // A no-op cipher: commutative and reversible, hides nothing.
        expectFailure "the no-op cipher" "actually blinds (non-degenerate)" blinds (NoOpCipher() :> ICommutativeCipher)
        expectFailure "the no-op cipher" "separates distinct keys" separatesKeys (NoOpCipher() :> ICommutativeCipher)
        expectPass "the no-op cipher" "commutes across keys" commutes (NoOpCipher() :> ICommutativeCipher)
        expectPass "the no-op cipher" "peels a key back off" peels (NoOpCipher() :> ICommutativeCipher)

        // An order-dependent chain: hides everything, commutes with nothing.
        expectFailure
            "the order-dependent cipher"
            "commutes across keys"
            commutes
            (OrderDependentCipher() :> ICommutativeCipher)
        expectFailure
            "the order-dependent cipher"
            "peels a key back off"
            peels
            (OrderDependentCipher() :> ICommutativeCipher)
        expectPass
            "the order-dependent cipher"
            "actually blinds (non-degenerate)"
            blinds
            (OrderDependentCipher() :> ICommutativeCipher)
        expectPass
            "the order-dependent cipher"
            "separates distinct keys"
            separatesKeys
            (OrderDependentCipher() :> ICommutativeCipher)

        // The whole pack, not just individual laws: a broken cipher must
        // fail SOMEWHERE in the law set, so `laws` bound to it could never
        // report green.
        testCase "no broken cipher passes the whole law set"
        <| fun () ->
            for name, cipher in
                [
                    "the no-op cipher", NoOpCipher() :> ICommutativeCipher
                    "the order-dependent cipher", OrderDependentCipher() :> ICommutativeCipher
                ] do
                let failures = allLaws |> List.filter (fun (_, law) -> law cipher |> Result.isError)

                Expect.isNonEmpty failures $"{name} passed every law — the contract pack is vacuous"

        // The PSI protocol is only correct because the cipher commutes.
        // Run the end-to-end protocol over the order-dependent control and
        // assert it finds NOTHING: proof that the earlier PSI cases are
        // measuring commutativity rather than the plumbing.
        testCase "PSI over a non-commutative cipher finds no intersection"
        <| fun () ->
            let cipher = OrderDependentCipher() :> ICommutativeCipher
            let shared = [ "cat"; "dan" ]

            match runIntersect cipher shared (cipher.GenerateKey()) (twoParty cipher shared (cipher.GenerateKey())) with
            | Ok outcome ->
                Expect.isEmpty
                    outcome.MatchedElements
                    "a non-commutative cipher must not produce matches — if it does, the PSI test is not testing PSI"
            | Error _ -> ()

        // ── Phase 479 ────────────────────────────────────────────────
        //
        // The aggregate mechanism gets the same treatment the cipher
        // does: two broken mechanisms, each caught on a different axis,
        // so no aggregator law is decorative and none carries the pack
        // alone.
        //   - `HashingAggregator` hides everything and respects nothing:
        //     caught by all three laws.
        //   - `FirstWinsAggregator` is genuinely associative and neither
        //     commutative nor identity-respecting: it PASSES the
        //     associativity law, which is what stops that law being
        //     written in a form that rejects everything.

        for name, aggregator in
            [
                "the hashing aggregator", HashingAggregator() :> IPsiAggregator
                "the first-wins aggregator", FirstWinsAggregator() :> IPsiAggregator
            ] do
            for label, law in aggregatorLaws do
                if not (name = "the first-wins aggregator" && label = "combines associatively") then
                    testCase $"{name}: '{label}' catches it"
                    <| fun () ->
                        match law aggregator with
                        | Error _ -> ()
                        | Ok() -> failtestf "'%s' passed against %s — the law has no teeth" label name

        testCase "the first-wins aggregator: 'combines associatively' does not fire"
        <| fun () ->
            match combinesAssociatively (FirstWinsAggregator() :> IPsiAggregator) with
            | Ok() -> ()
            | Error reason ->
                failtestf "associativity fired against an aggregator that genuinely satisfies it: %s" reason

        testCase "no broken aggregator passes the whole law set"
        <| fun () ->
            for name, aggregator in
                [
                    "the hashing aggregator", HashingAggregator() :> IPsiAggregator
                    "the first-wins aggregator", FirstWinsAggregator() :> IPsiAggregator
                ] do
                let failures =
                    aggregatorLaws |> List.filter (fun (_, law) -> law aggregator |> Result.isError)

                Expect.isNonEmpty failures $"{name} passed every law — the aggregator laws are vacuous"

        // The cardinality path is only correct because the cipher
        // commutes. Run it over the order-dependent control and assert it
        // counts NOTHING — proof that the cardinality cases above measure
        // commutativity rather than list plumbing.
        testCase "cardinality over a non-commutative cipher counts nothing"
        <| fun () ->
            let cipher = OrderDependentCipher() :> ICommutativeCipher
            let shared = [ "cat"; "dan" ]

            match
                runMode
                    cipher
                    (PsiRunOptions.create CardinalityOnly)
                    shared
                    (cipher.GenerateKey())
                    (modeResponder cipher shared (cipher.GenerateKey()) servesEverything)
            with
            | Ok(CardinalityReleased matched) ->
                Expect.equal
                    matched
                    0
                    "a non-commutative cipher must not produce matches — if it does, the cardinality test is not testing PSI"
            | Ok other -> failtestf "expected a cardinality, got %A" other
            | Error _ -> ()
    ]
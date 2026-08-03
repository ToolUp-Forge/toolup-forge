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

                match psi.Respond(key, elements, { SessionId = "s"; Blinded = [] }) with
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
    ]
module ToolUp.Platform.Tests.InProcess.ChainedAuditLedgerTests

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.ChainedLedger
open ToolUp.Platform.AuditSinks.LedgerChain
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

/// Tamper-evidence tests for the chained audit ledger sink.
///
/// The three tamper classes the ledger promises to position — edited,
/// dropped, reordered — are each PROBED here rather than argued: a
/// ledger is written through the sink, the stored bytes are perturbed in
/// exactly one way, and verification is asserted to name the class and
/// the position. A verification pass that cannot be made to fail proves
/// nothing, so each probe also asserts the ledger verified cleanly
/// BEFORE the perturbation.

let private uniqueDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-chained-ledger-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private settings: ChainedLedgerSettings = {
    Container = "audit-ledger"
    PathPrefix = Some "test"
}

let private jsonOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

/// Parse and re-render a stored line. Probes mutate records THROUGH
/// these rather than by string-replacing inside the JSON, so a probe
/// cannot quietly stop perturbing anything because a serialiser changed
/// how it spells a property name.
let private parseRecord (line: string) : LedgerRecord =
    System.Text.Json.JsonSerializer.Deserialize<LedgerRecord>(line, jsonOptions)

let private renderRecord (record: LedgerRecord) : string =
    System.Text.Json.JsonSerializer.Serialize(record, jsonOptions)

let private baseTime = DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc)

let private makeEnvelope (offset: float) : AuditEnvelope =
    AuditEnvelope.fromScopeId
        "team-ledger"
        (baseTime.AddSeconds offset)
        (UserLoggedIn {
            UserId = sprintf "user-%d" (int offset)
            AuthProvider = "Header"
        })

/// A fresh ledger over its own directory.
let private newLedger () =
    let dir = uniqueDir ()
    let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    let sink = create "ledger-test" settings storage
    storage, sink

// ─── Test signing substrate ──────────────────────────────────────
//
// The package ships NO crypto of its own — signing is an injected seam
// (`ILedgerHeadSigner`) precisely so the ledger carries no key-management
// dependency. These are test doubles standing in for a deployment's own
// signing substrate, built on BCL ECDSA.

[<Literal>]
let private testAlgorithm = "ECDSA-P256-SHA256"

type private EcdsaHeadSigner(keyId: string, key: ECDsa) =
    interface ILedgerHeadSigner with
        member _.KeyId = keyId
        member _.Algorithm = testAlgorithm

        member _.Sign(headBytes) = async { return Ok(key.SignData(headBytes, HashAlgorithmName.SHA256)) }

type private EcdsaHeadVerifier(expectedKeyId: string, publicKey: ECDsa) =
    interface ILedgerHeadVerifier with
        member _.Verify(keyId, algorithm, headBytes, signature) = async {
            if keyId <> expectedKeyId then
                return Error(sprintf "no public key held for key id %s" keyId)
            elif algorithm <> testAlgorithm then
                return Error(sprintf "unsupported algorithm %s" algorithm)
            else
                return Ok(publicKey.VerifyData(headBytes, signature, HashAlgorithmName.SHA256))
        }

/// Storage that fails the first `failures` writes to the head pointer
/// and otherwise delegates. Reproduces the one partial-append shape that
/// actually matters: the segment landed, the head write did not, and the
/// dispatcher is about to retry the whole batch.
type private HeadWriteFailingStorage(inner: IBlobStorage, failures: int ref) =
    interface IBlobStorage with
        member _.Upload(container, blobName, content) = async {
            if blobName.EndsWith "head.json" && failures.Value > 0 then
                failures.Value <- failures.Value - 1
                return Error "simulated head write failure"
            else
                return! inner.Upload(container, blobName, content)
        }

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// Export the signer's PUBLIC key and import it into a fresh key object,
/// so a verifier built from it demonstrably holds no private material.
let private coldPublicKeyOf (key: ECDsa) =
    let publicKey = ECDsa.Create()

    publicKey.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), ref 0)
    |> ignore

    publicKey

// ─── Storage-level perturbation helpers ──────────────────────────
//
// Probes perturb through `IBlobStorage` rather than through filesystem
// paths, so they exercise the same surface the sink writes through and
// make no assumption about how a backing store lays bytes out on disk.

let private segmentNames (storage: IBlobStorage) = async {
    let! names = storage.List(settings.Container, "test/records/")
    return names |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
}

let private readLines (storage: IBlobStorage) (name: string) = async {
    match! storage.Download(settings.Container, name) with
    | Error message -> return failwithf "segment read failed: %s" message
    | Ok bytes ->
        return
            Encoding.UTF8.GetString bytes
            |> fun text -> text.Split '\n'
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.toList
}

let private writeLines (storage: IBlobStorage) (name: string) (lines: string list) = async {
    match! storage.Upload(settings.Container, name, Encoding.UTF8.GetBytes(String.Join("\n", lines))) with
    | Ok _ -> return ()
    | Error message -> return failwithf "segment write failed: %s" message
}

/// Rewrite the ledger's single segment through `transform`.
let private perturb (storage: IBlobStorage) (transform: string list -> string list) = async {
    let! names = segmentNames storage
    let name = List.exactlyOne names
    let! lines = readLines storage name
    do! writeLines storage name (transform lines)
}

let private expectVerified (storage: IBlobStorage) message = async {
    match! verify settings storage None with
    | Ok(LedgerVerified(count, _, signature)) -> return count, signature
    | Ok other -> return failtestf "%s: expected LedgerVerified, got %A" message other
    | Error error -> return failtestf "%s: ledger unreadable — %s" message error
}

let private expectBroken (storage: IBlobStorage) message = async {
    match! verify settings storage None with
    | Ok(LedgerBroken breakage) -> return breakage
    | Ok other -> return failtestf "%s: expected LedgerBroken, got %A" message other
    | Error error -> return failtestf "%s: ledger unreadable — %s" message error
}

/// Write `count` records through the sink as a single batch.
let private seed (sink: IAuditSink) (count: int) = async {
    let batch = [ for i in 0 .. count - 1 -> makeEnvelope (float i) ]

    match! sink.Deliver batch with
    | Ok() -> return ()
    | Error message -> return failwithf "seed delivery failed: %s" message
}

// ─── Contract binding ────────────────────────────────────────────

/// Per-sink storage map, so the contract pack's `verifyDelivered` can
/// find the ledger a given sink instance wrote.
let private ledgerStorage = ConcurrentDictionary<obj, IBlobStorage>()

let contractTests =
    let factory () =
        let dir = uniqueDir ()
        let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
        let sink = create "ledger-contract" settings storage
        ledgerStorage[box sink] <- storage
        sink

    let verifyDelivered (sink: IAuditSink) (batches: AuditEnvelope list list) =
        let storage = ledgerStorage[box sink]
        let expected = batches |> List.sumBy List.length |> int64

        match verify settings storage None |> Async.RunSynchronously with
        | Ok(LedgerVerified(count, _, _)) ->
            Expect.equal count expected "Every delivered envelope must appear in the chain exactly once"
        | Ok other -> failtestf "Ledger did not verify after delivery: %A" other
        | Error message -> failtestf "Ledger unreadable after delivery: %s" message

    IAuditSinkContract.tests "ChainedLedger" factory verifyDelivered

// ─── Chain + canonical-serialisation unit tests ──────────────────

let private chainTests =
    testList "chained audit ledger — chain primitives" [
        test "canonical JSON sorts object properties at every depth" {
            let canonical = canonicaliseJson """{"b":1,"a":{"z":true,"y":[3,1,2]}}"""

            Expect.equal
                canonical
                """{"a":{"y":[3,1,2],"z":true},"b":1}"""
                "Properties must sort by ordinal name; array order is data and must be preserved"
        }

        test "canonical JSON is insensitive to input property order" {
            Expect.equal
                (canonicaliseJson """{"a":1,"b":2}""")
                (canonicaliseJson """{"b":2,"a":1}""")
                "Two spellings of the same value must canonicalise identically"
        }

        test "length framing cannot be forged by smuggling a separator" {
            // Two records whose fields differ only in where a colon-ish
            // boundary falls must not collide. Length prefixes make the
            // field cut unambiguous.
            let template = {
                Sequence = 0L
                PreviousDigest = genesisDigest
                Digest = ""
                SchemaVersion = 2
                OccurredAt = "2026-05-28T12:00:00.0000000Z"
                ScopeId = "a"
                SubjectKind = "bc"
                EventType = "d"
                Payload = "{}"
            }

            let shifted = {
                template with
                    ScopeId = "ab"
                    SubjectKind = "c"
            }

            Expect.notEqual
                (computeDigest template)
                (computeDigest shifted)
                "Moving a character across a field boundary must change the digest"
        }

        test "a record's digest commits to its predecessor" {
            let record = {
                Sequence = 1L
                PreviousDigest = genesisDigest
                Digest = ""
                SchemaVersion = 2
                OccurredAt = "2026-05-28T12:00:00.0000000Z"
                ScopeId = "team"
                SubjectKind = "team"
                EventType = "UserLoggedIn"
                Payload = "{}"
            }

            let a = chain 1L (String('a', 64)) record
            let b = chain 1L (String('b', 64)) record

            Expect.notEqual a.Digest b.Digest "Changing only the predecessor digest must change this record's digest"
        }

        test "head bytes commit to the chain LENGTH, not only the head digest" {
            Expect.notEqual
                (headBytes 10L "deadbeef")
                (headBytes 9L "deadbeef")
                "A truncated ledger must not be re-presentable under the same head signature"
        }
    ]

// ─── Ledger behaviour + tamper probes ────────────────────────────

let private ledgerTests =
    testList "chained audit ledger — sink" [
        testCaseAsync "an unsigned ledger composes with zero configuration and verifies"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 5

            let! count, signature = expectVerified storage "fresh ledger"
            Expect.equal count 5L "All five records must be chained"
            Expect.equal signature HeadUnsigned "The default composition takes no signature and needs no key material"
        }

        testCaseAsync "the chain is deterministic — same records produce the same digests"
        <| async {
            let storageA, sinkA = newLedger ()
            let storageB, sinkB = newLedger ()

            do! seed sinkA 6
            do! seed sinkB 6

            let! namesA = segmentNames storageA
            let! namesB = segmentNames storageB
            let! linesA = readLines storageA (List.exactlyOne namesA)
            let! linesB = readLines storageB (List.exactlyOne namesB)

            Expect.equal (List.length linesA) (List.length linesB) "Both ledgers must hold the same number of records"

            // Compare the digests, not the raw lines: the lines are
            // identical here too, but the digest is the claim the ledger
            // actually makes, and asserting it directly is what makes
            // this a determinism probe rather than a byte-equality one.
            let digestsOf lines =
                lines |> List.map (parseRecord >> _.Digest)

            Expect.equal
                (digestsOf linesA)
                (digestsOf linesB)
                "Two independently-written ledgers over identical records must produce an identical chain"
        }

        testCaseAsync "records chain contiguously from genesis"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 4

            let! names = segmentNames storage
            let! lines = readLines storage (List.exactlyOne names)

            let records = lines |> List.map parseRecord

            Expect.equal (records |> List.map _.Sequence) [ 0L; 1L; 2L; 3L ] "Sequences must be contiguous from zero"

            Expect.equal
                (List.head records).PreviousDigest
                genesisDigest
                "The first record must chain to the genesis digest, so a front-truncated ledger cannot pass"
        }

        testCaseAsync "the stored payload decodes back to the original event"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 1

            let! names = segmentNames storage
            let! lines = readLines storage (List.exactlyOne names)
            let record = parseRecord (List.exactlyOne lines)

            match LedgerRecord.decodePayload<LedgerPayload> jsonOptions record with
            | Ok payload ->
                Expect.equal
                    (AuditEvent.eventTypeName payload.Event)
                    "UserLoggedIn"
                    "The audit event must survive the round trip"
            | Error message -> failtestf "Payload decode failed: %s" message
        }

        // ── The three tamper classes ──

        testCaseAsync "PROBE: a tampered record is detected and positioned"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 5

            let! count, _ = expectVerified storage "before tampering"
            Expect.equal count 5L "Precondition: the ledger verifies before it is perturbed"

            // Edit record 2's scope in place, leaving its stored digest
            // untouched — the shape of an after-the-fact edit.
            do!
                perturb storage (fun lines ->
                    lines
                    |> List.mapi (fun i line ->
                        if i = 2 then
                            let record = parseRecord line
                            renderRecord { record with ScopeId = "team-forged" }
                        else
                            line))

            let! breakage = expectBroken storage "after tampering"

            Expect.equal breakage.Kind TamperedRecord "An in-place edit must be reported as a tampered record"
            Expect.equal breakage.Position 2L "The break must be positioned at the edited record"
            Expect.equal breakage.Sequence (Some 2L) "The offending record's own sequence must be reported"
        }

        testCaseAsync "PROBE: a dropped record is detected and positioned"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 5

            let! count, _ = expectVerified storage "before the drop"
            Expect.equal count 5L "Precondition: the ledger verifies before it is perturbed"

            // Delete record 3 outright.
            do! perturb storage (List.mapi (fun i line -> i, line) >> List.filter (fst >> (<>) 3) >> List.map snd)

            let! breakage = expectBroken storage "after the drop"

            Expect.equal
                breakage.Kind
                DroppedRecord
                "A deletion must be reported as a dropped record, not a generic link break"

            Expect.equal breakage.Position 3L "The break must be positioned where the missing record should have been"
            Expect.equal breakage.Sequence (Some 4L) "The record actually found at that position must be named"
        }

        testCaseAsync "PROBE: reordered records are detected and positioned"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 5

            let! count, _ = expectVerified storage "before the reorder"
            Expect.equal count 5L "Precondition: the ledger verifies before it is perturbed"

            // Swap records 1 and 2 — every record still present, only
            // the order changed. This is the class a naive "are all the
            // records here?" check would miss entirely.
            do!
                perturb storage (fun lines ->
                    let array = List.toArray lines
                    let held = array[1]
                    array[1] <- array[2]
                    array[2] <- held
                    Array.toList array)

            let! breakage = expectBroken storage "after the reorder"

            Expect.equal
                breakage.Kind
                ReorderedRecord
                "A permutation must be distinguished from a deletion — every sequence is still present"

            Expect.equal breakage.Position 1L "The break must be positioned at the first displaced record"
            Expect.equal breakage.Sequence (Some 2L) "The record actually found at that position must be named"
        }

        testCaseAsync "PROBE: a torn tail is detected, not absorbed"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 4

            let! count, _ = expectVerified storage "before the tear"
            Expect.equal count 4L "Precondition: the ledger verifies before it is perturbed"

            // Truncate the final line mid-way, the shape a crash
            // part-way through an append leaves behind.
            do!
                perturb storage (fun lines ->
                    let array = List.toArray lines
                    let last = array.Length - 1
                    array[last] <- array[last].Substring(0, array[last].Length / 2)
                    Array.toList array)

            let! breakage = expectBroken storage "after the tear"

            Expect.equal breakage.Kind TornTail "An unreadable trailing record must be reported, never skipped"
            Expect.equal breakage.Position 3L "The tear must be positioned after the intact prefix"
            Expect.isNone breakage.Sequence "A torn record has no readable sequence to report"
        }

        testCaseAsync "a record spliced in from another chain breaks the link"
        <| async {
            let storageA, sinkA = newLedger ()
            let storageB, sinkB = newLedger ()

            do! seed sinkA 4
            // A different scope, so B's record 2 is a genuinely
            // different record that is nonetheless internally valid.
            match!
                sinkB.Deliver [
                    for i in 0..3 ->
                        AuditEnvelope.fromScopeId
                            "team-other"
                            (baseTime.AddSeconds(float i))
                            (UserLoggedIn {
                                UserId = sprintf "other-%d" i
                                AuthProvider = "Header"
                            })
                ]
            with
            | Ok() -> ()
            | Error message -> failwithf "seed delivery failed: %s" message

            let! namesB = segmentNames storageB
            let! linesB = readLines storageB (List.exactlyOne namesB)

            do! perturb storageA (fun lines -> lines |> List.mapi (fun i line -> if i = 2 then linesB[2] else line))

            let! breakage = expectBroken storageA "after the splice"

            Expect.equal
                breakage.Kind
                BrokenLink
                "A self-consistent record from a foreign chain must be caught by the link, not the self-digest"

            Expect.equal breakage.Position 2L "The break must be positioned at the spliced record"
        }

        // ── Concurrency ──

        testCaseAsync "concurrent deliveries are serialised into one contiguous chain"
        <| async {
            let storage, sink = newLedger ()

            // Eight writers, three records each, all in flight at once.
            let deliveries = [
                for writer in 0..7 ->
                    async {
                        let batch = [ for i in 0..2 -> makeEnvelope (float (writer * 3 + i)) ]
                        return! sink.Deliver batch
                    }
            ]

            let! results = Async.Parallel deliveries

            for result in results do
                match result with
                | Ok() -> ()
                | Error message -> failtestf "A concurrent delivery failed: %s" message

            let! count, _ = expectVerified storage "after concurrent delivery"

            Expect.equal
                count
                24L
                "Every record from every concurrent writer must appear exactly once — appends are serialised, not dropped or interleaved"
        }

        testCaseAsync "a second writer moving the head beneath the first is refused, not silently forked"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            let first = create "ledger-first" settings storage
            let second = create "ledger-second" settings storage

            do! seed first 2
            // The second writer adopts the stored head and appends
            // legitimately — it has no stale view yet.
            do! seed second 2

            // The first writer's cached head is now stale.
            match! first.Deliver [ makeEnvelope 99.0 ] with
            | Ok() -> failtest "A writer whose head moved beneath it must refuse rather than fork the chain"
            | Error message ->
                Expect.stringContains
                    message
                    "another writer"
                    "The refusal must name the cause so an operator can act on it"

            // The refusal must leave the ledger intact, not half-written.
            let! count, _ = expectVerified storage "after the refused append"
            Expect.equal count 4L "A refused append must not have written a record"
        }

        testCaseAsync "a retried batch after a failed head write does not duplicate records"
        <| async {
            let dir = uniqueDir ()
            let inner = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            let failures = ref 1
            let storage = HeadWriteFailingStorage(inner, failures) :> IBlobStorage
            let sink = create "ledger-retry" settings storage

            let batch = [ for i in 0..2 -> makeEnvelope (float i) ]

            // First attempt: the segment lands, the head write fails,
            // and the sink reports Error so the dispatcher will retry.
            match! sink.Deliver batch with
            | Ok() -> failtest "Precondition: the seeded head-write failure must fail this delivery"
            | Error _ -> ()

            // The dispatcher retries the identical batch.
            match! sink.Deliver batch with
            | Ok() -> ()
            | Error message -> failtestf "The retry must succeed once the head write recovers: %s" message

            let! names = segmentNames storage

            Expect.equal
                (List.length names)
                1
                "The retry must land on the same content-addressed segment, not beside it"

            let! count, _ = expectVerified storage "after the retried delivery"
            Expect.equal count 3L "A retried batch must appear once, not twice"
        }

        // ── Head signing ──

        testCaseAsync "a signed head verifies COLD, from public key material alone"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)

            let sink =
                createSigned "ledger-signed" settings storage (EcdsaHeadSigner("key-1", key))

            do! seed sink 5

            // A verifier built from the exported PUBLIC key only —
            // no access to the signing key, and a fresh key object.
            use publicKey = coldPublicKeyOf key
            let verifier = EcdsaHeadVerifier("key-1", publicKey)

            match! verify settings storage (Some verifier) with
            | Ok(LedgerVerified(count, _, HeadSignatureValid(keyId, algorithm))) ->
                Expect.equal count 5L "The signed chain must carry all five records"
                Expect.equal keyId "key-1" "The head must record which key signed it"

                Expect.equal
                    algorithm
                    testAlgorithm
                    "The head must record the algorithm so a verifier can refuse rather than guess"
            | Ok other -> failtestf "Expected a validly-signed head, got %A" other
            | Error message -> failtestf "Ledger unreadable: %s" message
        }

        testCaseAsync "a head signed by a DIFFERENT key is reported invalid, not valid"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            use signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            use otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)

            let sink =
                createSigned "ledger-signed" settings storage (EcdsaHeadSigner("key-1", signingKey))

            do! seed sink 3

            use wrongPublicKey = coldPublicKeyOf otherKey
            let verifier = EcdsaHeadVerifier("key-1", wrongPublicKey)

            match! verify settings storage (Some verifier) with
            | Ok(LedgerHeadUntrusted(_, _, HeadSignatureInvalid(keyId, _))) ->
                Expect.equal keyId "key-1" "The rejected key must be named"
            | Ok other -> failtestf "A signature from the wrong key must not verify; got %A" other
            | Error message -> failtestf "Ledger unreadable: %s" message
        }

        testCaseAsync "a signed head with no verifier is UNVERIFIABLE, never a quiet pass"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)

            let sink =
                createSigned "ledger-signed" settings storage (EcdsaHeadSigner("key-1", key))

            do! seed sink 3

            match! verify settings storage None with
            | Ok(LedgerHeadUntrusted(_, _, HeadSignatureUnverifiable(algorithm, reason))) ->
                Expect.equal algorithm testAlgorithm "The unchecked algorithm must be named"
                Expect.stringContains reason "no verifier" "The reason must say WHY it could not be checked"
            | Ok other -> failtestf "'Could not check' must never render as 'checked and fine'; got %A" other
            | Error message -> failtestf "Ledger unreadable: %s" message
        }

        testCaseAsync "a signing failure fails the delivery rather than reporting an unsigned head as signed"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let failingSigner =
                { new ILedgerHeadSigner with
                    member _.KeyId = "key-1"
                    member _.Algorithm = testAlgorithm
                    member _.Sign(_) = async { return Error "signing substrate unavailable" }
                }

            let sink = createSigned "ledger-signed" settings storage failingSigner

            match! sink.Deliver [ makeEnvelope 0.0 ] with
            | Ok() -> failtest "A delivery whose head could not be signed must not report success"
            | Error message ->
                Expect.stringContains
                    message
                    "signing"
                    "The failure must name the signing step so the dispatcher's retry is diagnosable"
        }

        testCaseAsync "a rolled-back head pointer is caught even though the chain itself is sound"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 3

            // Replay an older head over the current one — the records
            // are untouched and chain perfectly; only the claim about
            // where the chain ends has been rewound.
            let stale =
                """{"RecordCount":2,"HeadDigest":"%s","KeyId":null,"Algorithm":null,"Signature":null,"SignedAt":null}"""

            let payload = stale.Replace("%s", String('0', 64))

            match! storage.Upload(settings.Container, "test/head.json", Encoding.UTF8.GetBytes payload) with
            | Ok _ -> ()
            | Error message -> failwithf "head write failed: %s" message

            match! verify settings storage None with
            | Ok(LedgerHeadUntrusted(count, _, HeadSignatureUnverifiable(_, reason))) ->
                Expect.equal count 3L "The chain still walks to its true length"
                Expect.stringContains reason "head pointer" "The mismatch between pointer and chain must be reported"
            | Ok other -> failtestf "A rewound head pointer must not verify; got %A" other
            | Error message -> failtestf "Ledger unreadable: %s" message
        }
    ]

let tests =
    testList "ChainedLedger audit sink" [ contractTests; chainTests; ledgerTests ]
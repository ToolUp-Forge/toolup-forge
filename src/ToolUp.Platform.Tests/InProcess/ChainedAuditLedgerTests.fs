module ToolUp.Platform.Tests.InProcess.ChainedAuditLedgerTests

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuditSinks
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
        // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

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
                ScopeFacets = []
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
                ScopeFacets = []
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

// ─── Phase 677 — per-party scoped export ─────────────────────────
//
// The property under test is not "the filter works". It is that a party
// holding ONLY its own segment can tell three things apart: a chain that
// was tampered with, an export that was under-disclosed to it, and one
// that is sound. Each is probed by producing the state and asserting the
// verdict names it — and every probe first asserts the unperturbed
// export verifies, so a verifier that cannot fail proves nothing.

let private alphaScopeId = "team-alpha"
let private betaScopeId = "team-beta"

/// Tags each record with the facet naming the scope it was recorded
/// under — the shape a deployment configures, kept trivial here so the
/// tests are about the export rather than about the tagging policy.
let private facetTagger: LedgerScopeTagger =
    fun envelope -> [ sprintf "scope:%s" envelope.ScopeId ]

let private partyEnvelope (scopeId: string) (offset: float) : AuditEnvelope =
    AuditEnvelope.fromScopeId
        scopeId
        (baseTime.AddSeconds offset)
        (UserLoggedIn {
            UserId = sprintf "%s-user-%d" scopeId (int offset)
            AuthProvider = "Header"
        })

let private alphaScope =
    LedgerScopedExport.PartyScope.create "alpha" [ sprintf "scope:%s" alphaScopeId ]

/// A ledger over its own directory whose records are facet-tagged, and
/// which alternates between the two parties' scopes.
let private newTaggedLedger () = async {
    let dir = uniqueDir ()
    let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    let sink = createScoped "ledger-scoped" settings storage facetTagger

    let batch = [
        partyEnvelope alphaScopeId 0.0
        partyEnvelope betaScopeId 1.0
        partyEnvelope alphaScopeId 2.0
        partyEnvelope betaScopeId 3.0
        partyEnvelope alphaScopeId 4.0
    ]

    match! sink.Deliver batch with
    | Ok() -> ()
    | Error message -> failwithf "tagged ledger seed failed: %s" message

    return storage
}

let private exportForAlpha (storage: IBlobStorage) = async {
    match! LedgerScopedExport.exportFor settings storage alphaScope with
    | Ok export -> return export
    | Error message -> return failwithf "scoped export failed: %s" message
}

/// Rewrite one entry of an export. Perturbations are applied to the
/// EXPORT rather than to the stored ledger, because the party holding an
/// export is exactly the party who cannot see the ledger — the threat is
/// a document handed over already altered.
let private mapEntry
    (position: int64)
    (transform: LedgerScopedExport.ScopedExportEntry -> LedgerScopedExport.ScopedExportEntry)
    (export: LedgerScopedExport.ScopedLedgerExport)
    =
    {
        export with
            Entries =
                export.Entries
                |> List.map (fun entry ->
                    if LedgerScopedExport.ScopedExportEntry.sequence entry = position then
                        transform entry
                    else
                        entry)
    }

type private EcdsaStatementSigner(keyId: string, key: ECDsa) =
    interface IStatementEnvelopeSigner with
        member _.KeyId() = keyId

        member _.SignPreAuthenticated(pae: byte[]) = async {
            return
                Ok {
                    KeyId = keyId
                    Signature = key.SignData(pae, HashAlgorithmName.SHA256)
                }
        }

let private scopedExportTests =
    testList "scoped export" [

        testCaseAsync "a party's export discloses its own records and withholds every other party's"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            Expect.equal
                (List.length export.Entries)
                5
                "Every position in the source chain must appear — the skeleton is what proves completeness"

            let disclosed = LedgerScopedExport.disclosedRecords export

            Expect.equal (List.length disclosed) 3 "The three alpha records are disclosed"

            Expect.all
                disclosed
                (fun record -> record.ScopeId = alphaScopeId)
                "Nothing outside the party's scope may be disclosed"

            Expect.isEmpty
                (LedgerScopedExport.disclosedOutOfScope alphaScope export)
                "The structural leakage check must find nothing"

            // The strongest available statement about content leakage:
            // the serialised document a counterparty receives does not
            // contain the other party's record bodies anywhere.
            let document = LedgerScopedExport.canonicalForm export

            Expect.isFalse
                (document.Contains(sprintf "%s-user-1" betaScopeId))
                "A withheld record's payload must not appear anywhere in the exported document"

            Expect.isFalse
                (document.Contains(sprintf "%s-user-3" betaScopeId))
                "A withheld record's payload must not appear anywhere in the exported document"

            match LedgerScopedExport.verifyExport alphaScope export with
            | LedgerScopedExport.ExportIntact(partyId, disclosedCount, withheldCount, recordCount, _) ->
                Expect.equal partyId "alpha" "The verdict names the party the export was checked as"
                Expect.equal disclosedCount 3 "Three disclosed"
                Expect.equal withheldCount 2 "Two withheld"
                Expect.equal recordCount 5L "The full chain length is recovered from the export alone"
            | other -> failtestf "An untouched export must verify; got %A" other
        }

        testCaseAsync "a tampered disclosed record is reported as tampered, at its position"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            Expect.isTrue
                (LedgerScopedExport.verifyExport alphaScope export
                 |> LedgerScopedExport.ScopedExportVerification.isIntact)
                "The export must verify BEFORE it is perturbed"

            // Edit a disclosed record's body, leaving its stored digest
            // as it was — the edit-in-place an exporter or a recipient
            // could attempt on a document in hand.
            let tampered =
                export
                |> mapEntry 2L (function
                    | LedgerScopedExport.DisclosedRecord record ->
                        LedgerScopedExport.DisclosedRecord {
                            record with
                                Payload = record.Payload.Replace("alpha-user-2", "alpha-user-9")
                        }
                    | entry -> entry)

            match LedgerScopedExport.verifyExport alphaScope tampered with
            | LedgerScopedExport.ExportBrokenAt breakage ->
                Expect.equal breakage.Kind TamperedRecord "The class must be named"
                Expect.equal breakage.Position 2L "The break must be positioned where the edit was made"
            | other -> failtestf "A tampered export must not verify; got %A" other
        }

        testCaseAsync "an in-scope record downgraded to a withheld witness is detected"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            // The selective-omission attack the facet labels exist to
            // stop: the record is replaced by a witness carrying its own
            // true digest, so the chain still walks perfectly. Only the
            // facet it declares gives it away.
            let omitted =
                export
                |> mapEntry 2L (function
                    | LedgerScopedExport.DisclosedRecord record ->
                        LedgerScopedExport.WithheldRecord(record.Sequence, record.Digest, facetsOf record)
                    | entry -> entry)

            match LedgerScopedExport.verifyExport alphaScope omitted with
            | LedgerScopedExport.ExportScopeViolation(position, detail) ->
                Expect.equal position (Some 2L) "The omission must be positioned"

                Expect.stringContains
                    detail
                    "incomplete within its own scope"
                    "The verdict must say the export is under-disclosed rather than broken"
            | other -> failtestf "A downgraded in-scope record must be detected; got %A" other
        }

        testCaseAsync "an in-scope record removed outright leaves a gap the walk names"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            let removed = {
                export with
                    Entries =
                        export.Entries
                        |> List.filter (fun entry -> LedgerScopedExport.ScopedExportEntry.sequence entry <> 2L)
            }

            match LedgerScopedExport.verifyExport alphaScope removed with
            | LedgerScopedExport.ExportBrokenAt breakage ->
                Expect.equal breakage.Kind DroppedRecord "A vanished position is a deletion"
                Expect.equal breakage.Position 2L "Positioned where the record should have been"
            | other -> failtestf "A removed position must not verify; got %A" other
        }

        testCaseAsync "a truncated export contradicts the head's signed record count"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            // Every remaining entry chains perfectly; what is missing is
            // the tail. Only the record count inside the head bytes —
            // which the exporter cannot re-sign — catches this.
            let truncated = {
                export with
                    Entries =
                        export.Entries
                        |> List.filter (fun entry -> LedgerScopedExport.ScopedExportEntry.sequence entry < 4L)
            }

            match LedgerScopedExport.verifyExport alphaScope truncated with
            | LedgerScopedExport.ExportBrokenAt breakage ->
                Expect.equal breakage.Kind DroppedRecord "A lopped tail is a deletion"
                Expect.stringContains breakage.Detail "the head records" "The head's count must be what catches it"
            | other -> failtestf "A truncated export must not verify; got %A" other
        }

        testCaseAsync "an over-disclosed record is refused even though the chain is sound"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            // The other direction: a beta record handed to alpha in
            // full. The chain walks — it is the real record — and the
            // scope check is the only thing between the counterparty and
            // data it is not entitled to.
            let! stored = ChainedLedger.read settings storage

            let betaRecord =
                match stored with
                | Ok ledger -> ledger.Records |> List.find (fun record -> record.Sequence = 1L)
                | Error message -> failwithf "ledger read failed: %s" message

            let leaked =
                export |> mapEntry 1L (fun _ -> LedgerScopedExport.DisclosedRecord betaRecord)

            match LedgerScopedExport.verifyExport alphaScope leaked with
            | LedgerScopedExport.ExportScopeViolation(position, detail) ->
                Expect.equal position (Some 1L) "The leak must be positioned"
                Expect.stringContains detail "disclosed to a scope" "The verdict must name over-disclosure"
            | other -> failtestf "An over-disclosed record must be refused; got %A" other
        }

        testCaseAsync "an export is refused when checked as a different party"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            let betaScope =
                LedgerScopedExport.PartyScope.create "beta" [ sprintf "scope:%s" betaScopeId ]

            match LedgerScopedExport.verifyExport betaScope export with
            | LedgerScopedExport.ExportScopeViolation(None, detail) ->
                Expect.stringContains detail "alpha" "The verdict must name the party the export was taken for"
            | other -> failtestf "An export must not verify under another party's scope; got %A" other
        }

        testCaseAsync "an untagged ledger discloses nothing to anybody"
        <| async {
            // Fail-closed, end to end: a deployment that composes the
            // ledger without a tagger produces records no scope reaches.
            // The export is honest about it — every position withheld,
            // none omitted.
            let storage, sink = newLedger ()
            do! seed sink 3

            match! LedgerScopedExport.exportFor settings storage alphaScope with
            | Error message -> failtestf "An untagged ledger must still export: %s" message
            | Ok export ->
                Expect.isEmpty (LedgerScopedExport.disclosedRecords export) "An untagged record is visible to no scope"

                match LedgerScopedExport.verifyExport alphaScope export with
                | LedgerScopedExport.ExportIntact(_, disclosed, withheld, _, _) ->
                    Expect.equal disclosed 0 "Nothing disclosed"
                    Expect.equal withheld 3 "Everything withheld, and nothing silently dropped"
                | other -> failtestf "A wholly-withheld export is still a valid export; got %A" other
        }

        testCaseAsync "a record written before scope facets existed verifies unchanged"
        <| async {
            // The GP 11 claim made testable: strip the facet property
            // from the stored JSON, which is exactly the shape a
            // pre-Phase-677 ledger has on disk, and assert both the
            // chain digests and the export still hold.
            let storage, sink = newLedger ()
            do! seed sink 3

            let! names = segmentNames storage
            let name = List.exactlyOne names
            let! lines = readLines storage name

            let stripped =
                lines
                |> List.map (fun line ->
                    let node =
                        System.Text.Json.Nodes.JsonNode.Parse line :?> System.Text.Json.Nodes.JsonObject

                    node.Remove "ScopeFacets" |> ignore
                    node.ToJsonString())

            Expect.all
                stripped
                (fun line -> not (line.Contains "ScopeFacets"))
                "The probe must actually have removed the field, or it proves nothing"

            do! writeLines storage name stripped

            match! verify settings storage None with
            | Ok(LedgerVerified(count, _, HeadUnsigned)) ->
                Expect.equal count 3L "A ledger with no facet field still walks to its full length"
            | Ok other -> failtestf "A pre-facet ledger must verify unchanged; got %A" other
            | Error message -> failtestf "Ledger unreadable: %s" message
        }

        testCaseAsync "a broken source chain is refused rather than exported"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 3

            do!
                perturb storage (fun lines ->
                    lines
                    |> List.mapi (fun index line ->
                        if index = 1 then
                            let record = parseRecord line
                            renderRecord { record with EventType = "Rewritten" }
                        else
                            line))

            match! LedgerScopedExport.exportFor settings storage alphaScope with
            | Ok _ -> failtest "An export must not be taken from a chain that does not verify"
            | Error message ->
                Expect.stringContains message "refusing to export" "The refusal must say what it refused"
                Expect.stringContains message "position 1" "The refusal must name the break"
        }

        testCaseAsync "a scoped export round-trips through the stock DSSE path"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)

            let signer =
                EcdsaStatementSigner("statement-key-1", key) :> IStatementEnvelopeSigner

            match! LedgerScopedExport.sign signer export with
            | Error message -> failtestf "Signing the export failed: %s" message
            | Ok envelope ->
                let json = DsseEnvelope.toJson envelope

                // Stock path: the signature covers the PAE of the
                // payload, and nothing about this SDK is needed to check
                // it beyond the DSSE encoding itself.
                use publicKey = coldPublicKeyOf key

                let pae =
                    match DsseEnvelope.paeOf envelope with
                    | Ok bytes -> bytes
                    | Error message -> failwithf "PAE unreadable: %s" message

                let signatureBytes =
                    match DsseEnvelope.signatureFor "statement-key-1" envelope with
                    | Some signature ->
                        match DsseEnvelope.signatureBytes signature with
                        | Ok bytes -> bytes
                        | Error message -> failwithf "signature unreadable: %s" message
                    | None -> failwith "the envelope carries no signature for the signing key"

                Expect.isTrue
                    (publicKey.VerifyData(pae, signatureBytes, HashAlgorithmName.SHA256))
                    "A stock DSSE verifier holding only the public key must accept the envelope"

                // And the document reader recovers the export and
                // reaches the same verdict the in-memory value did.
                match LedgerScopedExport.verifyDocument alphaScope json with
                | LedgerScopedExport.ExportIntact(partyId, disclosed, withheld, count, _) ->
                    Expect.equal partyId "alpha" "The party survives the round trip"
                    Expect.equal disclosed 3 "Three disclosed"
                    Expect.equal withheld 2 "Two withheld"
                    Expect.equal count 5L "The full chain length survives the round trip"
                | other -> failtestf "A signed export must read back intact; got %A" other

                Expect.equal
                    (LedgerScopedExport.contentId export)
                    (DsseEnvelope.sha256Hex (LedgerScopedExport.canonicalBytes export))
                    "The subject digest is a plain SHA-256 over the canonical bytes, checkable without this SDK"
        }

        testCaseAsync "a document of another predicate type is refused rather than read hopefully"
        <| async {
            let! storage = newTaggedLedger ()
            let! export = exportForAlpha storage

            use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)

            let signer =
                EcdsaStatementSigner("statement-key-1", key) :> IStatementEnvelopeSigner

            match!
                DsseEnvelope.sign
                    signer
                    [ LedgerScopedExport.subjectFor export ]
                    "https://toolup-forge.io/attestations/something-else/v1"
                    (LedgerScopedExport.predicateJson export)
            with
            | Error message -> failtestf "Signing failed: %s" message
            | Ok envelope ->
                match LedgerScopedExport.verifyDocument alphaScope (DsseEnvelope.toJson envelope) with
                | LedgerScopedExport.ExportUnreadable(position, reason) ->
                    Expect.equal position "document/predicateType" "The refusal must name where it stopped"
                    Expect.stringContains reason "scoped-audit-ledger-export" "The expected type must be named"
                | other -> failtestf "A foreign predicate type must be refused; got %A" other
        }
    ]

// ─── Phase 678 — decommission: closure, refusal, certificate ─────
//
// Four claims, each probed against the state that would falsify it:
//
//   * A closed ledger refuses appends, and the refusal is NOT a chain
//     break — asserted by showing the closed ledger still VERIFIES. The
//     two conditions are the ones an operator responds to oppositely,
//     so a test that only checked "the append failed" would leave the
//     distinction unproven.
//   * A retired record refuses to boot — that half lives in
//     `BootVerificationPreflightTests`, where the preflight is.
//   * The certificate verifies COLD: from the document and a public key
//     alone, with no ledger, no storage and no private material in
//     reach. Probed by exporting the key's public half into a fresh key
//     object, exactly as the head-signature probes do.
//   * A deployment that never decommissions is wholly unaffected
//     (GP 13) — no closure blob, no refusal, and a chain byte-identical
//     to the one the same envelopes produced before this phase existed.

/// The closure request a decommissioning deployment hands over.
let private closureRequest: LedgerClosureRequest = {
    DeployRecordDigest = "9f0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8"
    DeployId = "deploy-1"
    ClosedBy = "ops@example.com"
    Reason = "engagement complete — contained clean room torn down"
}

/// A seeded ledger whose head is signed, plus the signing key: the shape
/// a deployment that intends to decommission actually runs.
let private newSignedLedger () = async {
    let dir = uniqueDir ()
    let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    let key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let signer = EcdsaHeadSigner("ledger-key-1", key) :> ILedgerHeadSigner
    let sink = createSigned "ledger-decommission" settings storage signer

    do! seed sink 3

    return storage, signer, key, sink
}

/// `expectVerified` with key material to hand.
///
/// The plain helper passes no verifier, which is right for the unsigned
/// ledgers the tamper probes use and reports `LedgerHeadUntrusted` for a
/// signed one — correctly, since "signed and unchecked" is not verified.
/// These probes sign, so they bring the public half.
let private expectVerifiedWith (storage: IBlobStorage) (key: ECDsa) message = async {
    let verifier =
        EcdsaHeadVerifier("ledger-key-1", coldPublicKeyOf key) :> ILedgerHeadVerifier

    match! verify settings storage (Some verifier) with
    | Ok(LedgerVerified(count, _, signature)) -> return count, signature
    | Ok other -> return failtestf "%s: expected LedgerVerified, got %A" message other
    | Error error -> return failtestf "%s: ledger unreadable — %s" message error
}

let private expectClosed (storage: IBlobStorage) (signer: ILedgerHeadSigner option) = async {
    match! ChainedLedger.close settings storage signer closureRequest with
    | Ok op -> return op
    | Error message -> return failwithf "closing the ledger failed: %s" message
}

let private decommissionTests =
    testList "decommission" [

        testCaseAsync "the terminal op binds the head it closed, and is signed under the head-signing seam"
        <| async {
            let! storage, signer, key, _sink = newSignedLedger ()
            let! count, _ = expectVerifiedWith storage key "before closing"
            let! op = expectClosed storage (Some signer)

            Expect.equal op.RecordCount count "The op closes the chain at the length the walk recovered"

            Expect.isTrue
                (LedgerTerminalOp.digestHolds op)
                "The op's stored digest must be the digest of its own content"

            Expect.isTrue (LedgerTerminalOp.isSigned op) "A signed deployment's closure carries a signature"

            Expect.equal op.KeyId (Some "ledger-key-1") "The signing key is recorded so a verifier can select it"

            Expect.equal op.ClosedBy "ops@example.com" "The decommissioning actor is bound into the op"

            Expect.equal op.DeployRecordDigest closureRequest.DeployRecordDigest "The deploy record identity is bound"

            // The binding is only worth having if it is falsifiable: an
            // op edited to name a different actor no longer digests to
            // what it stores.
            let edited = { op with ClosedBy = "someone-else" }

            Expect.isFalse
                (LedgerTerminalOp.digestHolds edited)
                "Editing the actor after the fact must break the op's own digest"
        }

        testCaseAsync "a closed ledger refuses every later append, and the refusal is not a chain break"
        <| async {
            let! storage, signer, key, sink = newSignedLedger ()

            match! ChainedLedger.appendRefusal settings storage with
            | Ok None -> ()
            | other -> failtestf "An open ledger must refuse nothing; got %A" other

            let! op = expectClosed storage (Some signer)

            match! ChainedLedger.appendRefusal settings storage with
            | Ok(Some refusal) ->
                Expect.isTrue (LedgerAppendRefusal.isClosed refusal) "The refusal is a closure, typed as one"

                match refusal with
                | LedgerClosed found -> Expect.equal found.Digest op.Digest "It names the terminal op that closed it"
                | other -> failtestf "Expected a closure refusal; got %A" other
            | other -> failtestf "A closed ledger must refuse; got %A" other

            match! sink.Deliver [ makeEnvelope 99.0 ] with
            | Ok() ->
                failtest "An append after closure must be refused — a terminal op that can be bypassed is not terminal"
            | Error message ->
                Expect.stringContains message "chained ledger is closed" "The refusal says which refusal it is"
                Expect.stringContains message op.Digest "and names the terminal op"

            // The claim this whole test exists for: refusal and breakage
            // are different states, and the closed ledger is in the
            // FIRST. An operator reading 'broken' hunts for an attacker.
            let! count, signature = expectVerifiedWith storage key "after closing"
            Expect.equal count 3L "Closing appends no record and removes none"

            match signature with
            | HeadSignatureValid _ -> ()
            | other -> failtestf "The head was validly signed before closing and must stay so; got %A" other

            let! names = segmentNames storage
            Expect.equal (List.length names) 1 "Closing writes no segment"
        }

        testCaseAsync "a ledger cannot be closed twice, and a broken chain cannot be closed at all"
        <| async {
            let! storage, signer, _key, _sink = newSignedLedger ()
            let! _ = expectClosed storage (Some signer)

            match! ChainedLedger.close settings storage (Some signer) closureRequest with
            | Ok _ ->
                failtest
                    "A second closure would leave two signed end-markers and no way to say which one a relying party should believe"
            | Error message -> Expect.stringContains message "close the ledger twice" "The refusal says why"

            // The other refusal: a chain that does not walk. Closing it
            // would sign a head nobody can reproduce.
            let dir = uniqueDir ()
            let broken = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            let brokenSink = create "ledger-broken" settings broken
            do! seed brokenSink 3

            do!
                perturb broken (fun lines ->
                    lines
                    |> List.map (fun line ->
                        let record = parseRecord line

                        if record.Sequence = 1L then
                            renderRecord {
                                record with
                                    Payload = record.Payload.Replace("user-1", "user-9")
                            }
                        else
                            line))

            match! ChainedLedger.close settings broken None closureRequest with
            | Ok _ -> failtest "Closing a broken chain would hand a counterparty a document nobody can make sound"
            | Error message -> Expect.stringContains message "the chain breaks at position" "The break is named"
        }

        testCaseAsync "the certificate verifies cold, from the document and a public key alone"
        <| async {
            let! storage, signer, key, _sink = newSignedLedger ()
            let! op = expectClosed storage (Some signer)

            let! certificate = async {
                match! LedgerDecommission.certificateFor settings storage with
                | Ok certificate -> return certificate
                | Error message -> return failwithf "certificate issue failed: %s" message
            }

            Expect.equal
                certificate.TerminalOp.Digest
                op.Digest
                "The certificate carries the terminal op that closed it"

            Expect.equal
                certificate.Retirement.LedgerHeadDigest
                op.HeadDigest
                "The retirement is bound to the head the op closed"

            Expect.isTrue
                (DeployRetirement.bindsRecord closureRequest.DeployRecordDigest certificate.Retirement)
                "and to the deploy record the deployment named"

            // Cold: a verifier built from the EXPORTED PUBLIC half, so it
            // demonstrably holds no private material.
            let coldVerifier =
                EcdsaHeadVerifier("ledger-key-1", coldPublicKeyOf key) :> ILedgerHeadVerifier

            let statementSigner =
                EcdsaStatementSigner("statement-key-1", key) :> IStatementEnvelopeSigner

            match! LedgerDecommission.sign statementSigner certificate with
            | Error message -> failtestf "Signing the certificate failed: %s" message
            | Ok envelope ->
                let document = DsseEnvelope.toJson envelope

                match! LedgerDecommission.verifyDocument (Some coldVerifier) document with
                | LedgerDecommission.DecommissionVerified(deployId, recordCount, headDigest, keyId) ->
                    Expect.equal deployId "deploy-1" "The verdict names the deploy that ended"
                    Expect.equal recordCount 3L "and the length of the chain it closed"
                    Expect.equal headDigest op.HeadDigest "and the head it closed at"
                    Expect.equal keyId "ledger-key-1" "and the key that proved it"
                | other -> failtestf "An untouched certificate must verify cold; got %A" other

                // Without key material the same document is UNPROVEN —
                // never verified. A verifier that cannot fail proves
                // nothing, and one that passes without a key proves less.
                match! LedgerDecommission.verifyDocument None document with
                | LedgerDecommission.DecommissionUnproven _ -> ()
                | other -> failtestf "With no verifier the answer must be unproven; got %A" other
        }

        testCaseAsync "a certificate whose retirement was edited is reported unbound, not unproven"
        <| async {
            let! storage, signer, key, _sink = newSignedLedger ()
            let! _op = expectClosed storage (Some signer)

            let! certificate = async {
                match! LedgerDecommission.certificateFor settings storage with
                | Ok certificate -> return certificate
                | Error message -> return failwithf "certificate issue failed: %s" message
            }

            let coldVerifier =
                EcdsaHeadVerifier("ledger-key-1", coldPublicKeyOf key) :> ILedgerHeadVerifier

            match! LedgerDecommission.verifyCertificate (Some coldVerifier) certificate with
            | LedgerDecommission.DecommissionVerified _ -> ()
            | other -> failtestf "The certificate must verify BEFORE it is perturbed; got %A" other

            // Retarget the retirement at another deploy record — the
            // swap the binding exists to stop.
            let swapped = {
                certificate with
                    Retirement = {
                        certificate.Retirement with
                            DeployRecordDigest = "0000000000000000000000000000000000000000000000000000000000000000"
                    }
            }

            match! LedgerDecommission.verifyCertificate (Some coldVerifier) swapped with
            | LedgerDecommission.DecommissionUnbound _ -> ()
            | other -> failtestf "A retirement pointing at another record must be reported unbound; got %A" other

            // And a terminal op that does not close the head it travels
            // with is NOT CLOSED, which is a different finding again.
            let mismatched = {
                certificate with
                    Head = {
                        certificate.Head with
                            RecordCount = certificate.Head.RecordCount + 1L
                    }
            }

            match! LedgerDecommission.verifyCertificate (Some coldVerifier) mismatched with
            | LedgerDecommission.DecommissionNotClosed _ -> ()
            | other -> failtestf "A head the op does not close must be reported not-closed; got %A" other
        }

        testCaseAsync "an unsigned closure is unproven rather than verified"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            let sink = create "ledger-unsigned" settings storage
            do! seed sink 2

            let! op = expectClosed storage None

            Expect.isFalse (LedgerTerminalOp.isSigned op) "An unsigned deployment may still mark its ledger closed"

            match! LedgerDecommission.certificateFor settings storage with
            | Error message -> failtestf "an unsigned ledger must still issue a certificate: %s" message
            | Ok certificate ->
                match! LedgerDecommission.verifyCertificate None certificate with
                | LedgerDecommission.DecommissionUnproven(headSignature, opSignature) ->
                    Expect.equal headSignature HeadUnsigned "The head carries no signature and the verdict says so"
                    Expect.equal opSignature HeadUnsigned "and neither does the closure"
                | other -> failtestf "An unsigned closure must never read as proof of decommission; got %A" other
        }

        testCaseAsync "a deployment that never decommissions is wholly unaffected"
        <| async {
            let storage, sink = newLedger ()
            do! seed sink 3

            let! namesBefore = segmentNames storage
            let! countBefore, _ = expectVerified storage "an open ledger"

            match! ChainedLedger.readTerminalOp settings storage with
            | Ok None -> ()
            | other -> failtestf "A ledger nobody closed must carry no terminal op; got %A" other

            match! ChainedLedger.appendRefusal settings storage with
            | Ok None -> ()
            | other -> failtestf "and must refuse nothing; got %A" other

            match! sink.Deliver [ makeEnvelope 3.0 ] with
            | Ok() -> ()
            | Error message -> failtestf "an open ledger must keep accepting appends: %s" message

            let! countAfter, _ = expectVerified storage "after a further append"
            Expect.equal countAfter (countBefore + 1L) "The append landed"

            // The chain itself is what Phase 678 must not have moved: the
            // same envelopes through a ledger that knows nothing of
            // closure must produce the same digests, so an existing
            // deployment's stored ledger and head signature stay valid.
            let control, controlSink = newLedger ()
            do! seed controlSink 3

            let! controlNames = segmentNames control

            Expect.equal
                (namesBefore |> List.map (fun name -> name.Substring(name.LastIndexOf '/' + 1)))
                (controlNames |> List.map (fun name -> name.Substring(name.LastIndexOf '/' + 1)))
                "Segment names are content-addressed, so equal names are equal bytes — the chain is unchanged by this phase"

            let! lines = readLines storage (List.exactlyOne namesBefore)
            let! controlLines = readLines control (List.exactlyOne controlNames)
            Expect.equal (List.truncate 3 lines) controlLines "and the records themselves are identical"

            match! LedgerDecommission.certificateFor settings storage with
            | Error message ->
                Expect.stringContains message "this ledger is open" "An open ledger issues no certificate, and says why"
            | Ok _ -> failtest "A certificate for an open ledger would attest to a decommission that never happened"
        }
    ]

let tests =
    testList "ChainedLedger audit sink" [ contractTests; chainTests; ledgerTests; scopedExportTests; decommissionTests ]
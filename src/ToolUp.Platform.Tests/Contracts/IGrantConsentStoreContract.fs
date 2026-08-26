// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IGrantConsentStoreContract

// ─── Phase 552 — IGrantConsentStore conformance ──────────────────────
//
// Every shipped implementation is held to the SAME bar, and an external
// one can be too — the point of a contract pack (Phase 3a shape).
//
// The properties below are the ones an authorization artifact store must
// have, and each is here because getting it wrong is silent:
//
//   * **Round-trip fidelity, INCLUDING the optional fields.** A store
//     that drops `ExpiresAtUtc` or `Supersedes` on the way through
//     serialisation turns an expiring consent into a permanent one and a
//     revocation into an orphan. Neither shows up as an error.
//   * **Subject isolation.** `ListForSubject` returns records for that
//     subject and no other. A store that leaked across subjects would let
//     one user's consent authorise another's grant.
//   * **Append-only accumulation.** Lodging a revocation does not remove
//     the approval it supersedes. The interface has no `Remove` at all;
//     this pins that the implementations do not achieve one by other
//     means.
//   * **Path-traversal refusal.** A record whose id is not a safe blob
//     segment is refused BEFORE any write. On the blob store an unchecked
//     `../` would write outside the team's keyspace; the in-memory store
//     cannot be hurt by it, and is held to the identical rule anyway so
//     the two cannot drift into different validation.
//   * **Absence is `Ok None`, not an error.** A missing record is an
//     ordinary answer; conflating it with a read failure is how a storage
//     blip becomes "no consent was ever given".

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.GrantConsentStore

let private subject = ConsentSubject.create "team-a" "alice" "SkuAnalysis"
let private otherSubject = ConsentSubject.create "team-a" "bob" "SkuAnalysis"
let private party = PartyRef.create "acme-dpo"

/// A record with every optional field populated, so a store that silently
/// drops one fails here rather than in production.
let private recordWith (subj: ConsentSubject) (id: string) (status: ConsentStatus) (supersedes: string option) = {
    ConsentId = id
    Subject = subj
    Party = party
    Status = status
    IssuedAtUtc = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)
    ExpiresAtUtc = Some(DateTimeOffset(2027, 8, 26, 12, 0, 0, TimeSpan.Zero))
    Signature = {
        KeyId = "party-key-1"
        DeclaredAlgorithm = "HmacSha256"
        Value = "AAAA"
        SignedAtUtc = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)
    }
    Supersedes = supersedes
    RecordedBy = "admin-a"
}

let private expectOk label (r: Result<'a, string>) =
    match r with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got Error %s" label e

/// `do! putOk "label" (store.Put r)` — asserts the write succeeded and
/// discards the unit. Written out rather than reaching for an `Async.map`
/// the codebase does not define.
let private putOk label (work: Async<Result<unit, string>>) = async {
    let! r = work
    expectOk label r
}

/// The conformance suite, parameterised over a factory so a companion
/// implementation runs the identical cases.
let contractTests (name: string) (factory: unit -> IGrantConsentStore) =
    testList $"IGrantConsentStore contract — {name}" [
        testCaseAsync
            "round-trips every field, optionals included"
            (async {
                let store = factory ()
                let record = recordWith subject "c1" ConsentStatus.Approved (Some "c0")

                do! putOk "put" (store.Put record)

                let! fetched = store.TryGet(subject.TeamId, "c1")

                match expectOk "tryGet" fetched with
                | None -> failtest "expected the record to be readable after Put"
                | Some r ->
                    Expect.equal r.Subject subject "subject survived the round trip"
                    Expect.equal r.Party party "party survived the round trip"
                    Expect.equal r.Status ConsentStatus.Approved "status survived the round trip"
                    Expect.equal r.ExpiresAtUtc record.ExpiresAtUtc "an expiry that vanishes makes a consent permanent"
                    Expect.equal r.Supersedes (Some "c0") "a supersession edge that vanishes orphans a revocation"
                    Expect.equal r.Signature.Value "AAAA" "the signature is the integrity control; it must survive"

                    Expect.equal
                        r.Signature.DeclaredAlgorithm
                        "HmacSha256"
                        "the declared algorithm is evidence of a downgrade attempt; it must survive"
            })

        testCaseAsync
            "absent record reads as Ok None, never as an error"
            (async {
                let store = factory ()
                let! fetched = store.TryGet("team-a", "nope")
                Expect.equal (expectOk "tryGet" fetched) None "absence is an ordinary answer"
            })

        testCaseAsync
            "ListForSubject isolates subjects"
            (async {
                let store = factory ()
                do! putOk "put a" (store.Put(recordWith subject "c1" ConsentStatus.Approved None))
                do! putOk "put b" (store.Put(recordWith otherSubject "c2" ConsentStatus.Approved None))

                let! mine = store.ListForSubject subject
                let listed = expectOk "list" mine

                Expect.equal (List.length listed) 1 "exactly this subject's records"
                Expect.equal listed.Head.ConsentId "c1" "and it is the right one"
            })

        testCaseAsync
            "a revocation ACCUMULATES — the approval it supersedes stays"
            (async {
                let store = factory ()
                do! putOk "put" (store.Put(recordWith subject "c1" ConsentStatus.Approved None))

                do! putOk "revoke" (store.Put(recordWith subject "c2" ConsentStatus.Revoked (Some "c1")))

                let! listed = store.ListForSubject subject
                let all = expectOk "list" listed |> List.map _.ConsentId |> List.sort

                Expect.equal all [ "c1"; "c2" ] "revocation appends; it never deletes the evidence"
            })

        testCaseAsync
            "refuses an unsafe consent id before writing anything"
            (async {
                let store = factory ()
                let! result = store.Put(recordWith subject "../../escape" ConsentStatus.Approved None)

                Expect.isError result "a path-shaped id must be refused, not encoded and hoped over"

                let! listed = store.ListForSubject subject
                Expect.isEmpty (expectOk "list" listed) "and nothing was written"
            })

        testCaseAsync
            "refuses an incomplete subject"
            (async {
                let store = factory ()
                let incomplete = ConsentSubject.create "team-a" "" "SkuAnalysis"
                let! result = store.Put(recordWith incomplete "c9" ConsentStatus.Approved None)

                Expect.isError result "an unnameable subject is fail-closed, never a wildcard"
            })

        testCaseAsync
            "Put is idempotent on the same id"
            (async {
                let store = factory ()
                do! putOk "put" (store.Put(recordWith subject "c1" ConsentStatus.Proposed None))
                do! putOk "re-put" (store.Put(recordWith subject "c1" ConsentStatus.Proposed None))

                let! listed = store.ListForSubject subject
                Expect.equal (List.length (expectOk "list" listed)) 1 "a retried write does not duplicate"
            })
    ]

let private freshBlobStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-grantconsent-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    BlobGrantConsentStore(LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage) :> IGrantConsentStore

let tests =
    testList "IGrantConsentStore contract" [
        contractTests "InMemoryGrantConsentStore" (fun () -> InMemoryGrantConsentStore() :> IGrantConsentStore)
        // The blob store runs over a REAL LocalFileStorage rather than a
        // stub, so the JSON round trip — where an optional field actually
        // goes missing — is exercised rather than assumed.
        contractTests "BlobGrantConsentStore" freshBlobStore
    ]
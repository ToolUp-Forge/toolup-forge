module ToolUp.Platform.Tests.Contracts.IPendingInviteStoreContract

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.BlobStorage

// ─── IPendingInviteStore contract pack ──────────────────────────────
//
// Parametrised tests for any `IPendingInviteStore` implementation. Each
// test asks the factory for a fresh store so concurrent runs against
// shared substrate state cannot interfere. The InMemory binding resets
// the module-level cache + uses a fresh `InMemoryBlobStorage` per call;
// the future `BlobPendingInviteStore` binding will use a unique blob
// path per call. Bindings that share process-wide state (the InMemory
// impl's writeLock + cache) wrap the produced testList in
// `testSequenced` so module-level state doesn't leak across runs.
//
// Coverage targets the interface contract — `Upsert` / `Remove` /
// `TryConsumeForEmail` / `ListAll` / `SweepExpired`. Failure-shape
// behaviour (Conflict on the future ETag impl, StorageFailed on
// underlying-blob errors) is implementation-specific and out of scope
// for the contract; bindings that exercise those add their own tests.

let private freshPending teamId : PendingInviteByEmail = {
    TeamId = teamId
    Role = Member
    ExpiresAt = DateTime.UtcNow.AddDays 7.0
    InviterUserId = "alice@example.com"
    IssuedAt = DateTime.UtcNow
}

let private expiredPending teamId : PendingInviteByEmail = {
    TeamId = teamId
    Role = Member
    ExpiresAt = DateTime.UtcNow.AddSeconds -60.0
    InviterUserId = "alice@example.com"
    IssuedAt = DateTime.UtcNow.AddDays -7.0
}

let private okOrFail label result =
    match result with
    | Ok v -> v
    | Error err -> failtestf "%s: expected Ok, got %A" label err

let private upsertOrFail label (store: IPendingInviteStore) email pending = async {
    let! result = store.Upsert(email, pending)

    match result with
    | Ok() -> ()
    | Error err -> failtestf "%s: expected Ok from Upsert, got %A" label err
}

let tests (name: string) (factory: unit -> IPendingInviteStore) =

    testList $"{name} — IPendingInviteStore contract" [

        // ─── Upsert → ListAll round-trip ─────────────────────────

        testCaseAsync "Upsert persists; ListAll surfaces the entry"
        <| async {
            let store = factory ()
            let pending = freshPending "team-list"

            do! upsertOrFail "Upsert" store "alice@example.com" pending

            let! listed = store.ListAll()
            let entries = okOrFail "ListAll" listed

            Expect.equal entries.Length 1 "one entry persisted"

            let email, entry = entries.Head
            Expect.equal email "alice@example.com" "email key is lower-case"
            Expect.equal entry.TeamId "team-list" "TeamId echoed"
            Expect.equal entry.Role Member "Role echoed"
            Expect.equal entry.InviterUserId "alice@example.com" "InviterUserId echoed"
        }

        // ─── Email is case-insensitive (lower-cased on store) ────

        testCaseAsync "Upsert lower-cases the email key — same address two cases collapse"
        <| async {
            let store = factory ()
            let pending = freshPending "team-case"
            do! upsertOrFail "Upsert upper" store "Carol@Example.com" pending
            do! upsertOrFail "Upsert lower" store "carol@example.com" pending

            let! listed = store.ListAll()
            let entries = okOrFail "ListAll" listed
            Expect.equal entries.Length 1 "case-variant emails collapsed to a single entry"
        }

        // ─── Upsert replaces an existing entry ───────────────────

        testCaseAsync "Upsert replaces an existing entry for the same email"
        <| async {
            let store = factory ()

            let first = {
                freshPending "team-A" with
                    Role = Member
            }

            let second = {
                freshPending "team-B" with
                    Role = Admin
            }

            do! upsertOrFail "first Upsert" store "dan@example.com" first
            do! upsertOrFail "second Upsert" store "dan@example.com" second

            let! listed = store.ListAll()
            let entries = okOrFail "ListAll" listed
            Expect.equal entries.Length 1 "still one entry after replace"

            let _, entry = entries.Head
            Expect.equal entry.TeamId "team-B" "second TeamId wins"
            Expect.equal entry.Role Admin "second Role wins"
        }

        // ─── Remove ──────────────────────────────────────────────

        testCaseAsync "Remove deletes an existing entry; ListAll empty after"
        <| async {
            let store = factory ()
            let pending = freshPending "team-remove"
            do! upsertOrFail "Upsert" store "erin@example.com" pending

            let! removed = store.Remove "erin@example.com"
            okOrFail "Remove" removed

            let! listed = store.ListAll()
            let entries = okOrFail "ListAll" listed
            Expect.equal entries.Length 0 "entry gone after Remove"
        }

        testCaseAsync "Remove returns NotFound for an email with no entry"
        <| async {
            let store = factory ()

            let! result = store.Remove "ghost@example.com"

            match result with
            | Error PendingInviteStoreError.NotFound -> ()
            | other -> failtestf "Expected Error NotFound, got %A" other
        }

        testCaseAsync "Remove is case-insensitive — uppercase removes the lowercased key"
        <| async {
            let store = factory ()
            let pending = freshPending "team-case-rm"
            do! upsertOrFail "Upsert" store "Frank@Example.com" pending

            let! removed = store.Remove "FRANK@example.com"
            okOrFail "Remove uppercase" removed

            let! listed = store.ListAll()
            Expect.equal (okOrFail "ListAll" listed).Length 0 "entry gone"
        }

        // ─── TryConsumeForEmail ──────────────────────────────────

        testCaseAsync "TryConsumeForEmail returns the entry and atomically removes it"
        <| async {
            let store = factory ()
            let pending = freshPending "team-consume"
            do! upsertOrFail "Upsert" store "grace@example.com" pending

            let! consumed = store.TryConsumeForEmail "grace@example.com"

            match okOrFail "TryConsumeForEmail" consumed with
            | None -> failtest "Expected Some entry on first consume"
            | Some entry -> Expect.equal entry.TeamId "team-consume" "consumed entry surfaces TeamId"

            // Second consume after atomic remove — None.
            let! secondConsume = store.TryConsumeForEmail "grace@example.com"
            let secondResult = okOrFail "second TryConsumeForEmail" secondConsume
            Expect.isNone secondResult "second consume finds nothing — atomic remove already happened"
        }

        testCaseAsync "TryConsumeForEmail returns None for an email with no entry"
        <| async {
            let store = factory ()

            let! result = store.TryConsumeForEmail "nobody@example.com"
            let consumed = okOrFail "TryConsumeForEmail" result
            Expect.isNone consumed "no match → None"
        }

        testCaseAsync "TryConsumeForEmail of an expired entry returns None and removes it"
        <| async {
            let store = factory ()
            let expired = expiredPending "team-expired"
            do! upsertOrFail "Upsert" store "hank@example.com" expired

            let! consumed = store.TryConsumeForEmail "hank@example.com"
            let consumedResult = okOrFail "TryConsumeForEmail" consumed
            Expect.isNone consumedResult "expired entry surfaces as None"

            // Entry should also be removed from the store as a side-
            // effect of the read-and-discard expiry path.
            let! listed = store.ListAll()
            let entries = okOrFail "ListAll" listed

            let stillThere = entries |> List.exists (fun (e, _) -> e = "hank@example.com")

            Expect.isFalse stillThere "expired entry was dropped from the store"
        }

        // ─── SweepExpired ────────────────────────────────────────

        testCaseAsync "SweepExpired removes past-expiry entries; returns count"
        <| async {
            let store = factory ()
            // Upsert itself opportunistically compacts on write, so to
            // stage genuinely-expired entries we write live ones first
            // then wait past their expiry. 200ms expiry + 400ms sleep
            // covers scheduler jitter.
            let near = {
                freshPending "team-sweep" with
                    ExpiresAt = DateTime.UtcNow.AddMilliseconds 200.0
            }

            do! upsertOrFail "Upsert near1" store "near1@example.com" near
            do! upsertOrFail "Upsert near2" store "near2@example.com" near

            let live = freshPending "team-sweep"
            do! upsertOrFail "Upsert live" store "live@example.com" live

            do! Async.Sleep 400

            let! swept = store.SweepExpired()
            let removed = okOrFail "SweepExpired" swept
            Expect.equal removed 2 "both near-expiry entries swept"

            let! listed = store.ListAll()
            let entries = okOrFail "ListAll" listed
            Expect.equal entries.Length 1 "only the live entry remains"
            let email, _ = entries.Head
            Expect.equal email "live@example.com" "live entry preserved"
        }

        testCaseAsync "SweepExpired is idempotent when nothing is expired"
        <| async {
            let store = factory ()
            let live = freshPending "team-noop-sweep"
            do! upsertOrFail "Upsert" store "ian@example.com" live

            let! first = store.SweepExpired()
            Expect.equal (okOrFail "first SweepExpired" first) 0 "nothing to sweep"

            let! second = store.SweepExpired()
            Expect.equal (okOrFail "second SweepExpired" second) 0 "still nothing"
        }
    ]

/// `storage` with its reads failing (the blob still exists): both seams,
/// writes forwarded.
let private unreadable (storage: IBlobStorage) =
    let cas = storage :?> IConditionalBlobStorage

    { new IBlobStorage with
        member _.CanComposeFrom = storage.CanComposeFrom
        member _.ComposeFrom(c, t, s) = storage.ComposeFrom(c, t, s)
        member _.Erase(c, p, policy, dryRun) = storage.Erase(c, p, policy, dryRun)
        member _.Upload(c, b, content) = storage.Upload(c, b, content)
        member _.Download(_, _) = async { return Error "backend 503" }
        member _.DownloadRange(c, b, o, l) = storage.DownloadRange(c, b, o, l)
        member _.Delete(c, b) = storage.Delete(c, b)
        member _.List(c, p) = storage.List(c, p)
        member _.Exists(c, b) = storage.Exists(c, b)
        member _.GetMetadata(c, b) = storage.GetMetadata(c, b)
      interface IConditionalBlobStorage with
          member _.DownloadWithETag(_, _) = async { return Error "backend 503" }

          member _.UploadWithETag(c, b, content, condition) =
              cas.UploadWithETag(c, b, content, condition)
    }

let private storageFailed label result =
    match result with
    | Error(PendingInviteStoreError.StorageFailed _) -> ()
    | other -> failtestf "%s: expected StorageFailed, got %A" label other

/// The document's observable behaviour over a backend, pinned for every
/// SDK store: `freshStorage` supplies an empty backend implementing both
/// `IBlobStorage` and `IConditionalBlobStorage`, `factory` builds the store
/// over it.
let documentTests (name: string) (freshStorage: unit -> IBlobStorage) (factory: IBlobStorage -> IPendingInviteStore) =

    let canonical = "pending-invites.json"

    testList $"{name} — the pending-invites document" [

        testCaseAsync "the bytes at rest for a populated map"
        <| async {
            let storage = freshStorage ()
            let store = factory storage

            let entry: PendingInviteByEmail = {
                TeamId = "team-a+b"
                Role = Admin
                ExpiresAt = DateTime(2099, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                InviterUserId = "alice@example.com"
                IssuedAt = DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc)
            }

            do! upsertOrFail "Upsert" store "Bob@Example.com" entry
            let! bytes = storage.Download(ScopeKey.PlatformContainer, canonical)

            Expect.equal
                (okOrFail "download" bytes |> Encoding.UTF8.GetString)
                """{"bob@example.com":{"TeamId":"team-a+b","Role":"Admin","ExpiresAt":"2099-01-02T03:04:05.0000000Z","InviterUserId":"alice@example.com","IssuedAt":"2026-11-09T08:00:00.0000000Z"}}"""
                "today's bytes"
        }

        testCaseAsync "a document that cannot be read is a refusal, never the empty map, and nothing is written"
        <| async {
            let storage = freshStorage ()
            do! upsertOrFail "seed" (factory storage) "seed@example.com" (freshPending "team-seed")
            let! before = storage.Download(ScopeKey.PlatformContainer, canonical)
            let store = factory (unreadable storage)

            let! listed = store.ListAll()
            storageFailed "ListAll" listed
            let! upserted = store.Upsert("new@example.com", freshPending "team-new")
            storageFailed "Upsert" upserted

            let! after = storage.Download(ScopeKey.PlatformContainer, canonical)
            Expect.equal (okOrFail "after" after) (okOrFail "before" before) "the document is unchanged"
        }

        testCaseAsync "an undecodable document is quarantined, left in place, and every operation refused"
        <| async {
            let storage = freshStorage ()
            let store = factory storage
            let! _ = storage.Upload(ScopeKey.PlatformContainer, canonical, Encoding.UTF8.GetBytes "{ not json")

            let! upserted = store.Upsert("victim@example.com", freshPending "team-corrupt")
            storageFailed "Upsert" upserted
            let! consumed = store.TryConsumeForEmail "victim@example.com"
            storageFailed "TryConsumeForEmail" consumed
            let! listed = store.ListAll()
            storageFailed "ListAll" listed

            let! names = storage.List(ScopeKey.PlatformContainer, canonical)
            Expect.isTrue (names |> List.exists (fun n -> n.Contains ".corrupt-")) "the bytes were copied aside"
            let! kept = storage.Download(ScopeKey.PlatformContainer, canonical)

            Expect.equal
                (okOrFail "canonical" kept |> Encoding.UTF8.GetString)
                "{ not json"
                "not healed to empty: the canonical document is untouched"
        }
    ]
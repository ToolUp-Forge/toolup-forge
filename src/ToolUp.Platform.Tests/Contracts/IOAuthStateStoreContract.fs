module ToolUp.Platform.Tests.Contracts.IOAuthStateStoreContract

open System
open Expecto
open ToolUp.Platform

// ─── IOAuthStateStore contract pack ───────────────────────────────
//
// Parametrised tests for any `IOAuthStateStore` implementation. The
// factory returns a fresh, empty store. Phase 10b ships only the
// in-memory default; a future distributed companion (Redis, mirrors
// `src/NotificationChannels/Redis/`) binds the same pack to validate
// the substrate contract.
//
// **What's covered:**
//   - `Save` round-trips an entry that `TryConsume` returns by token.
//   - `TryConsume` is single-use (second call returns `None`).
//   - `TryConsume` returns `None` for an unknown token.
//   - `Save` rejects duplicate tokens (collision = bug, fail loudly).
//   - `Cleanup` removes entries whose `CreatedAt` is older than the
//     TTL; recent entries survive.
//   - 10-min TTL is honoured by `TryConsume` itself (replay against
//     a stale entry returns `None`).
//
// **What's NOT covered (out of contract):**
//   - Cross-store data leakage (each test gets a fresh factory).
//   - Wall-clock-skew tolerance against distributed backends.
//   - Concurrent-access serialisation guarantees beyond
//     read-and-remove atomicity (any backend that satisfies the
//     pack is free to choose its own concurrency model).

let private mkEntry (token: string) (createdAt: DateTime) : OAuthFlowState = {
    Token = token
    FlowName = "test-flow"
    DataSourceId = "ds-1"
    ScopeId = "scope-1"
    Container = "team-scope-1"
    UserId = "user-1"
    CreatedAt = createdAt
    RedirectUri = "https://example.com/api/oauth/test-flow/callback"
    CodeVerifier = Some "abc123"
    // Phase 43.B — a store must round-trip the neutral correlation
    // key, not just the legacy `DataSourceId`.
    Correlation = Some(OAuthCorrelationKey.dataSource "ds-1")
}

let tests (name: string) (factory: unit -> IOAuthStateStore) =

    testList $"{name} — IOAuthStateStore contract" [

        testCaseAsync "Save then TryConsume returns the entry"
        <| async {
            let store = factory ()
            let entry = mkEntry "tok-roundtrip" DateTime.UtcNow

            match! store.Save entry with
            | Ok() -> ()
            | Error msg -> failtestf "Save failed: %s" msg

            let! consumed = store.TryConsume "tok-roundtrip"

            match consumed with
            | Some e ->
                Expect.equal e.Token "tok-roundtrip" "Token round-trips"
                Expect.equal e.FlowName "test-flow" "FlowName round-trips"
                Expect.equal e.DataSourceId "ds-1" "DataSourceId round-trips"
                Expect.equal e.UserId "user-1" "UserId round-trips"
                Expect.equal e.RedirectUri "https://example.com/api/oauth/test-flow/callback" "RedirectUri round-trips"
                Expect.equal e.CodeVerifier (Some "abc123") "CodeVerifier round-trips"
            | None -> failtest "TryConsume returned None for a freshly-saved entry"
        }

        testCaseAsync "TryConsume is single-use"
        <| async {
            let store = factory ()
            let entry = mkEntry "tok-single" DateTime.UtcNow

            match! store.Save entry with
            | Ok() -> ()
            | Error msg -> failtestf "Save failed: %s" msg

            let! first = store.TryConsume "tok-single"
            Expect.isSome first "First TryConsume returns the entry"

            let! second = store.TryConsume "tok-single"
            Expect.isNone second "Second TryConsume returns None (single-use semantics)"
        }

        testCaseAsync "TryConsume on unknown token returns None"
        <| async {
            let store = factory ()
            let! result = store.TryConsume "tok-unknown"
            Expect.isNone result "Unknown token returns None"
        }

        testCaseAsync "Save rejects duplicate token"
        <| async {
            let store = factory ()
            let entry1 = mkEntry "tok-dup" DateTime.UtcNow
            let entry2 = mkEntry "tok-dup" DateTime.UtcNow

            match! store.Save entry1 with
            | Ok() -> ()
            | Error msg -> failtestf "First Save failed: %s" msg

            match! store.Save entry2 with
            | Ok() -> failtest "Expected Error on duplicate token; got Ok"
            | Error _ -> ()
        }

        testCaseAsync "Cleanup removes entries older than the TTL"
        <| async {
            let store = factory ()

            // One stale (45 min old), one fresh (just created).
            let stale = mkEntry "tok-stale" (DateTime.UtcNow - TimeSpan.FromMinutes 45.0)
            let fresh = mkEntry "tok-fresh" DateTime.UtcNow

            match! store.Save stale with
            | Ok() -> ()
            | Error msg -> failtestf "Save stale failed: %s" msg

            match! store.Save fresh with
            | Ok() -> ()
            | Error msg -> failtestf "Save fresh failed: %s" msg

            let! removed = store.Cleanup(TimeSpan.FromMinutes 10.0)
            Expect.equal removed 1 "Cleanup reports 1 removal"

            // Stale should be gone; fresh should still consume.
            let! staleConsume = store.TryConsume "tok-stale"
            Expect.isNone staleConsume "Stale entry was removed by Cleanup"

            let! freshConsume = store.TryConsume "tok-fresh"
            Expect.isSome freshConsume "Fresh entry survived Cleanup"
        }

        testCaseAsync "TryConsume returns None for an expired entry (lazy eviction)"
        <| async {
            let store = factory ()

            // 30 min old > 10 min TTL — should be treated as expired.
            let expired = mkEntry "tok-expired" (DateTime.UtcNow - TimeSpan.FromMinutes 30.0)

            match! store.Save expired with
            | Ok() -> ()
            | Error msg -> failtestf "Save expired failed: %s" msg

            let! result = store.TryConsume "tok-expired"
            Expect.isNone result "Expired entry returns None on TryConsume"

            // And a second call still returns None — the expired
            // entry has been evicted, not just hidden.
            let! again = store.TryConsume "tok-expired"
            Expect.isNone again "Expired entry stays gone on subsequent TryConsume"
        }

        testCaseAsync "Cleanup on an empty store reports zero removals"
        <| async {
            let store = factory ()
            let! removed = store.Cleanup(TimeSpan.FromMinutes 10.0)
            Expect.equal removed 0 "Empty store reports 0 removals"
        }
    ]
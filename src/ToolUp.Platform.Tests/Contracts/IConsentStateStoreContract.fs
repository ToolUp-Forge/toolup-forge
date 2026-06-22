module ToolUp.Platform.Tests.Contracts.IConsentStateStoreContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Consent.ConsentStateStore

// ─── IConsentStateStore contract pack ────────────────────────────────
//
// Parametrised tests for any `IConsentStateStore` implementation
// (Phase 159). The factory returns a fresh store plus two distinct
// scope ids so per-scope-isolation assertions have non-colliding
// scopes and concurrent runs against shared substrate cannot interfere.
//
// Coverage targets the portable surface every implementation must
// satisfy through the interface alone:
//   * Upsert → Get round-trip (granted / denied / source survive).
//   * `Necessary` always-granted invariant.
//   * Idempotent upsert — re-upserting a subject bumps the version and
//     overwrites wholesale (consent is current-state, not append-log).
//   * Per-subject isolation (GP 4) — subject A's decision never leaks
//     into subject B's read.
//   * Per-scope isolation (GP 4) — the same subject in two scopes
//     keeps independent decisions.
//   * Get on an unknown subject → `None`.
//   * Withdraw moves categories Granted → Denied; `Necessary` is never
//     withdrawable; Withdraw on an unknown subject → `Error`.
//
// Restart-durability (a fresh store instance over the same substrate
// reads back a prior write) is impl-specific — it lives in the
// entity-backed InProcess binding, not the portable pack (the
// in-memory dev impl cannot satisfy it by design).

let private record (subject: string) (granted: Set<ConsentCategory>) (source: ConsentSource) : ConsentRecord =
    ConsentRecord.create subject granted Set.empty (DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero)) source

let tests (name: string) (factory: unit -> IConsentStateStore * string * string) =

    testList $"{name} — IConsentStateStore contract" [

        // ─── Upsert / Get round-trip ──────────────────────────────

        testCaseAsync "Upsert then Get round-trips the decision"
        <| async {
            let store, scope, _ = factory ()

            let input =
                record "subject-a" (Set.ofList [ Analytics; Marketing ]) BannerInteraction

            let! upserted = store.Upsert(scope, input)
            Expect.isOk upserted "Upsert succeeds"

            let! fetched = store.Get(scope, "subject-a")

            match fetched with
            | Some r ->
                Expect.isTrue (Set.contains Analytics r.Granted) "Analytics granted survives round-trip"
                Expect.isTrue (Set.contains Marketing r.Granted) "Marketing granted survives round-trip"
                Expect.equal r.Source BannerInteraction "source survives round-trip"
                Expect.equal r.Subject "subject-a" "subject survives round-trip"
            | None -> failtest "Get returned None after a successful Upsert"
        }

        testCaseAsync "Upsert always grants Necessary"
        <| async {
            let store, scope, _ = factory ()
            // Deliberately omit Necessary from the granted set — the
            // store / record builder must add it back.
            let input = record "subject-a" (Set.singleton Analytics) BannerInteraction

            let! _ = store.Upsert(scope, input)
            let! fetched = store.Get(scope, "subject-a")

            match fetched with
            | Some r -> Expect.isTrue (Set.contains Necessary r.Granted) "Necessary is always granted"
            | None -> failtest "Get returned None after Upsert"
        }

        testCaseAsync "first Upsert assigns version 1"
        <| async {
            let store, scope, _ = factory ()
            let! result = store.Upsert(scope, record "subject-a" (Set.singleton Analytics) BannerInteraction)

            match result with
            | Ok r -> Expect.equal r.Version 1 "first upsert is version 1"
            | Error e -> failtestf "Upsert failed: %s" e
        }

        testCaseAsync "re-Upsert bumps version and overwrites wholesale"
        <| async {
            let store, scope, _ = factory ()
            let! _ = store.Upsert(scope, record "subject-a" (Set.singleton Analytics) BannerInteraction)
            let! second = store.Upsert(scope, record "subject-a" (Set.singleton Marketing) BannerInteraction)

            match second with
            | Ok r ->
                Expect.equal r.Version 2 "second upsert is version 2"
                Expect.isTrue (Set.contains Marketing r.Granted) "new decision present"
                Expect.isFalse (Set.contains Analytics r.Granted) "prior decision overwritten wholesale"
            | Error e -> failtestf "Upsert failed: %s" e
        }

        // ─── Isolation (GP 4) ─────────────────────────────────────

        testCaseAsync "per-subject isolation — subject A's grant does not leak into subject B"
        <| async {
            let store, scope, _ = factory ()
            let! _ = store.Upsert(scope, record "subject-a" (Set.singleton Marketing) BannerInteraction)

            let! bFetched = store.Get(scope, "subject-b")
            Expect.isNone bFetched "subject B has no record after subject A's upsert"
        }

        testCaseAsync "per-scope isolation — same subject in two scopes is independent"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! _ = store.Upsert(scopeA, record "subject-a" (Set.singleton Marketing) BannerInteraction)
            let! _ = store.Upsert(scopeB, record "subject-a" (Set.singleton Analytics) BannerInteraction)

            let! a = store.Get(scopeA, "subject-a")
            let! b = store.Get(scopeB, "subject-a")

            match a, b with
            | Some ra, Some rb ->
                Expect.isTrue (Set.contains Marketing ra.Granted) "scope A keeps its own decision"
                Expect.isFalse (Set.contains Analytics ra.Granted) "scope A does not see scope B's decision"
                Expect.isTrue (Set.contains Analytics rb.Granted) "scope B keeps its own decision"
                Expect.isFalse (Set.contains Marketing rb.Granted) "scope B does not see scope A's decision"
            | _ -> failtest "both scopes should have a record for subject-a"
        }

        testCaseAsync "Get on an unknown subject returns None"
        <| async {
            let store, scope, _ = factory ()
            let! fetched = store.Get(scope, "never-stored")
            Expect.isNone fetched "unknown subject reads back None"
        }

        // ─── Withdraw ─────────────────────────────────────────────

        testCaseAsync "Withdraw moves a category from Granted to Denied"
        <| async {
            let store, scope, _ = factory ()
            let! _ = store.Upsert(scope, record "subject-a" (Set.ofList [ Analytics; Marketing ]) BannerInteraction)
            let! result = store.Withdraw(scope, "subject-a", [ Marketing ])

            match result with
            | Ok r ->
                Expect.isFalse (Set.contains Marketing r.Granted) "withdrawn category removed from Granted"
                Expect.isTrue (Set.contains Marketing r.Denied) "withdrawn category added to Denied"
                Expect.isTrue (Set.contains Analytics r.Granted) "non-withdrawn category still granted"
            | Error e -> failtestf "Withdraw failed: %s" e
        }

        testCaseAsync "Withdraw cannot withdraw Necessary"
        <| async {
            let store, scope, _ = factory ()
            let! _ = store.Upsert(scope, record "subject-a" (Set.singleton Analytics) BannerInteraction)
            let! result = store.Withdraw(scope, "subject-a", [ Necessary ])

            match result with
            | Ok r ->
                Expect.isTrue (Set.contains Necessary r.Granted) "Necessary stays granted"
                Expect.isFalse (Set.contains Necessary r.Denied) "Necessary never lands in Denied"
            | Error e -> failtestf "Withdraw failed: %s" e
        }

        testCaseAsync "Withdraw on an unknown subject returns Error"
        <| async {
            let store, scope, _ = factory ()
            let! result = store.Withdraw(scope, "never-stored", [ Marketing ])
            Expect.isError result "withdrawing from a subject with no record errors"
        }
    ]
module ToolUp.Platform.Tests.InProcess.AnonymousSessionMigratorTests

open Expecto
open ToolUp.Platform

// ─── Phase 66 Stream C.1 — IAnonymousSessionMigrator substrate tests ─
//
// Covers the substrate slice that shipped with C.1: the no-op default,
// the `MigrationSummary` algebra, and the `AnonymousSessionMigrator.compose`
// combinator (NotEligible-skip / InfrastructureFailed-bail /
// PartialFailure-fold). The remaining two D.6 bullets — trigger-detection
// logic and the SemaphoreSlim-per-userId double-migration guard — live in
// the middleware that the C.1 continuation will ship; they are not testable
// against the substrate slice and are deferred with it.

// ─── Fixtures ────────────────────────────────────────────────────────

let private aSession = "session-abc"
let private aUser = AuthenticatedUser "user-1"

let private summary (items: int) (bytes: int64) (modules: string list) : MigrationSummary = {
    ItemsMigrated = items
    BytesMigrated = bytes
    Modules = modules
}

/// A migrator that always returns a fixed result, ignoring its inputs.
let private stub (result: Result<MigrationSummary, MigrationError>) : IAnonymousSessionMigrator =
    { new IAnonymousSessionMigrator with
        member _.Migrate(_sid, _subject) = async { return result }
    }

/// A migrator that records how many times it was invoked (to prove the
/// `compose` short-circuit really stops calling later migrators).
let private recordingStub
    (calls: int ref)
    (result: Result<MigrationSummary, MigrationError>)
    : IAnonymousSessionMigrator =
    { new IAnonymousSessionMigrator with
        member _.Migrate(_sid, _subject) = async {
            incr calls
            return result
        }
    }

let private run (migrator: IAnonymousSessionMigrator) : Result<MigrationSummary, MigrationError> =
    migrator.Migrate(aSession, aUser) |> Async.RunSynchronously

// ─── NoOpAnonymousSessionMigrator ────────────────────────────────────

let private noOpTests =
    testList "NoOpAnonymousSessionMigrator" [
        test "Migrate (AuthenticatedUser) → Error (NotEligible \"no migrator wired\")" {
            let migrator = NoOpAnonymousSessionMigrator() :> IAnonymousSessionMigrator

            match run migrator with
            | Result.Error(MigrationError.NotEligible reason) ->
                Expect.equal reason "no migrator wired" "unwired default declines uniformly"
            | other -> failtestf "expected NotEligible, got %A" other
        }

        test "Migrate (TeamMember) → Error (NotEligible) — no special-case per subject shape" {
            let migrator = NoOpAnonymousSessionMigrator() :> IAnonymousSessionMigrator

            let result =
                migrator.Migrate(aSession, TeamMember("user-1", "team-9"))
                |> Async.RunSynchronously

            match result with
            | Result.Error(MigrationError.NotEligible _) -> ()
            | other -> failtestf "expected NotEligible, got %A" other
        }
    ]

// ─── MigrationSummary algebra ────────────────────────────────────────

let private summaryTests =
    testList "MigrationSummary" [
        test "empty is the zero/identity seed" {
            Expect.equal MigrationSummary.empty.ItemsMigrated 0 "no items"
            Expect.equal MigrationSummary.empty.BytesMigrated 0L "no bytes"
            Expect.isEmpty MigrationSummary.empty.Modules "no modules"
        }

        test "combine sums counts and concatenates module lists" {
            let a = summary 2 100L [ "Forms" ]
            let b = summary 3 250L [ "AI" ]
            let combined = MigrationSummary.combine a b

            Expect.equal combined.ItemsMigrated 5 "items summed"
            Expect.equal combined.BytesMigrated 350L "bytes summed"
            Expect.equal combined.Modules [ "Forms"; "AI" ] "module lists concatenated in order"
        }

        test "combine de-duplicates overlapping module names" {
            let a = summary 1 10L [ "Forms"; "AI" ]
            let b = summary 1 10L [ "AI"; "RAG" ]
            let combined = MigrationSummary.combine a b

            Expect.equal combined.Modules [ "Forms"; "AI"; "RAG" ] "overlapping module name appears once"
        }

        test "combine with empty is identity" {
            let a = summary 4 99L [ "Forms" ]
            Expect.equal (MigrationSummary.combine a MigrationSummary.empty) a "right identity"
            Expect.equal (MigrationSummary.combine MigrationSummary.empty a) a "left identity"
        }
    ]

// ─── AnonymousSessionMigrator.compose ────────────────────────────────

let private composeTests =
    testList "AnonymousSessionMigrator.compose" [
        test "empty list → no-op default (Error NotEligible \"no migrator wired\")" {
            let composed = AnonymousSessionMigrator.compose []

            match run composed with
            | Result.Error(MigrationError.NotEligible reason) ->
                Expect.equal reason "no migrator wired" "empty list folds to the unwired default"
            | other -> failtestf "expected NotEligible, got %A" other
        }

        test "single migrator → passthrough (its result, unchanged)" {
            let only = stub (Ok(summary 7 700L [ "Forms" ]))
            let composed = AnonymousSessionMigrator.compose [ only ]

            match run composed with
            | Ok s ->
                Expect.equal s.ItemsMigrated 7 "single migrator's summary passes through"
                Expect.equal s.Modules [ "Forms" ] "modules preserved"
            | other -> failtestf "expected Ok passthrough, got %A" other
        }

        test "many all-Ok → summaries accumulate" {
            let composed =
                AnonymousSessionMigrator.compose [
                    stub (Ok(summary 2 100L [ "Forms" ]))
                    stub (Ok(summary 3 200L [ "AI" ]))
                ]

            match run composed with
            | Ok s ->
                Expect.equal s.ItemsMigrated 5 "items summed across the chain"
                Expect.equal s.BytesMigrated 300L "bytes summed"
                Expect.equal s.Modules [ "Forms"; "AI" ] "contributing modules concatenated"
            | other -> failtestf "expected accumulated Ok, got %A" other
        }

        test "NotEligible in the chain is skipped — other migrators still contribute" {
            let composed =
                AnonymousSessionMigrator.compose [
                    stub (Ok(summary 2 100L [ "Forms" ]))
                    stub (Result.Error(MigrationError.NotEligible "no data for this module"))
                    stub (Ok(summary 4 50L [ "AI" ]))
                ]

            match run composed with
            | Ok s ->
                Expect.equal s.ItemsMigrated 6 "the declining migrator contributes nothing but does not abort"
                Expect.equal s.Modules [ "Forms"; "AI" ] "only contributing modules listed"
            | other -> failtestf "expected Ok with the NotEligible skipped, got %A" other
        }

        test "≥2 migrators all-NotEligible → Ok empty (chain collapses to no-op, not Error)" {
            // Documented asymmetry: 0 or 1 migrator surface the NotEligible
            // Error (empty→no-op default, single→passthrough), but a multi
            // chain where every migrator declines accumulates nothing and
            // returns Ok empty — no InfrastructureFailed ever set `bail`.
            let composed =
                AnonymousSessionMigrator.compose [
                    stub (Result.Error(MigrationError.NotEligible "a"))
                    stub (Result.Error(MigrationError.NotEligible "b"))
                ]

            match run composed with
            | Ok s -> Expect.equal s MigrationSummary.empty "all-decline multi-chain yields the empty summary"
            | other -> failtestf "expected Ok empty, got %A" other
        }

        test "InfrastructureFailed bails — later migrators are not invoked" {
            let laterCalls = ref 0

            let composed =
                AnonymousSessionMigrator.compose [
                    stub (Ok(summary 2 100L [ "Forms" ]))
                    stub (Result.Error(MigrationError.InfrastructureFailed "blob store down"))
                    recordingStub laterCalls (Ok(summary 99 99L [ "ShouldNotRun" ]))
                ]

            match run composed with
            | Result.Error(MigrationError.InfrastructureFailed msg) ->
                Expect.equal msg "blob store down" "the substrate failure surfaces verbatim"
                Expect.equal laterCalls.Value 0 "the migrator after the failure is never called"
            | other -> failtestf "expected InfrastructureFailed bail, got %A" other
        }

        test "PartialFailure folds preceding summary and surfaces failedItems/lastError" {
            let laterCalls = ref 0

            let composed =
                AnonymousSessionMigrator.compose [
                    stub (Ok(summary 2 100L [ "Forms" ]))
                    stub (Result.Error(MigrationError.PartialFailure(summary 3 200L [ "AI" ], 1, "row 4 rejected")))
                    recordingStub laterCalls (Ok(summary 99 99L [ "ShouldNotRun" ]))
                ]

            match run composed with
            | Result.Error(MigrationError.PartialFailure(combined, failedItems, lastError)) ->
                Expect.equal combined.ItemsMigrated 5 "preceding Ok (2) folds into the partial (3)"
                Expect.equal combined.Modules [ "Forms"; "AI" ] "both contributing modules carried in the partial"
                Expect.equal failedItems 1 "failed-item count surfaced unchanged"
                Expect.equal lastError "row 4 rejected" "last error surfaced unchanged"
                Expect.equal laterCalls.Value 0 "PartialFailure also bails — later migrators not invoked"
            | other -> failtestf "expected folded PartialFailure, got %A" other
        }
    ]

// ─── Aggregate ───────────────────────────────────────────────────────

let tests =
    testList "IAnonymousSessionMigrator (Phase 66 C.1 substrate)" [ noOpTests; summaryTests; composeTests ]
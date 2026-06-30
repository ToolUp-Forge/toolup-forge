module ToolUp.AI.Client.Tests.TenantLifecycleAdminUITests

open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform
open TenantLifecycleAdminUI

// ─── Phase 54e — tenant-lifecycle diagnostics admin MVU test ─────────
//
// Drives `TenantLifecycleAdminUI.update` through the panel's flow:
// init → submit-scope → summary-loaded (with a recorded run) → and the
// two terminal branches (no run recorded, transport error).
//
// Fable-side (not .NET Expecto) for the same reason as
// `PlatformAIKeysAdminUITests`: the module declares a module-level
// `let private tenantApi = Api.makeProxy<IPlatformTenantApi> …`, whose
// reflection-based proxy builder is shaped for Fable's runtime and
// raises under .NET reflection at static-init time. The Fable transpile
// + `node:test` path runs the code in its actual deployment runtime.
//
// View rendering (Feliz, React `useState`, JSDOM) is not exercised here.

let private hookOutcome (name: string) (result: LifecycleHookResult) (elapsedMs: int64) : LifecycleHookOutcome = {
    HookName = name
    Result = result
    ElapsedMs = elapsedMs
}

let private sampleSummary: LifecycleSummary = {
    ScopeId = "team-abc123"
    Phase = Deprovisioning
    Outcomes = [
        hookOutcome "EncryptionKeyLifecycle" LifecycleHookResult.Completed 12L
        hookOutcome "MembershipCacheLifecycle" (LifecycleHookResult.Skipped "no distributed cache configured") 1L
        hookOutcome "SubjectDataErasureLifecycle" (LifecycleHookResult.Failed "store briefly unreachable") 240L
    ]
    TotalElapsedMs = 253L
}

let tests =
    testList "TenantLifecycleAdminUI (Phase 54e)" [
        testCase "happy path: init → submit scope → summary loaded with a recorded run"
        <| fun _ ->
            // init — nothing queried, not loading, no banner.
            let m0, _ = init ()
            Expect.equal m0.QueriedScope None "init: no scope queried"
            Expect.equal m0.Summary None "init: no summary"
            Expect.isFalse m0.Loaded "init: not loaded"
            Expect.isFalse m0.Loading "init: not loading"
            Expect.equal m0.Error None "init: no error banner"

            // SubmitScope — records the (trimmed) scope, enters loading,
            // clears any stale summary / error.
            let m1, _ = update (SubmitScope "  team-abc123  ") m0
            Expect.equal m1.QueriedScope (Some "team-abc123") "submit: trimmed scope recorded"
            Expect.isTrue m1.Loading "submit: loading while the fetch is in flight"
            Expect.isFalse m1.Loaded "submit: not yet loaded"
            Expect.equal m1.Summary None "submit: summary cleared pending fetch"

            // SummaryLoaded (Ok (Some _)) — the durable last run is shown.
            let m2, _ = update (SummaryLoaded(Ok(Some sampleSummary))) m1
            Expect.equal m2.Summary (Some sampleSummary) "loaded: summary set"
            Expect.isTrue m2.Loaded "loaded: Loaded set"
            Expect.isFalse m2.Loading "loaded: Loading cleared"
            Expect.equal m2.Error None "loaded: no error banner"

            // The per-hook counts the view renders are derived from the
            // shared LifecycleSummary helpers.
            Expect.equal (LifecycleSummary.completedCount sampleSummary) 1 "one completed hook"
            Expect.equal (LifecycleSummary.skippedCount sampleSummary) 1 "one skipped hook"
            Expect.equal (LifecycleSummary.failedCount sampleSummary) 1 "one failed hook"

        testCase "no run recorded: SummaryLoaded (Ok None) yields the empty state"
        <| fun _ ->
            let m0, _ = init ()
            let m1, _ = update (SubmitScope "team-without-runs") m0
            let m2, _ = update (SummaryLoaded(Ok None)) m1

            Expect.equal m2.Summary None "no-run: summary stays None"
            Expect.isTrue m2.Loaded "no-run: Loaded set so the view shows the empty state, not the prompt"
            Expect.isFalse m2.Loading "no-run: Loading cleared"
            Expect.equal m2.Error None "no-run: no error banner"

            Expect.equal
                m2.QueriedScope
                (Some "team-without-runs")
                "no-run: queried scope retained for the empty-state message"

        testCase "error path: SummaryLoaded (Error _) surfaces a banner and clears loading"
        <| fun _ ->
            let m0, _ = init ()
            let m1, _ = update (SubmitScope "team-abc123") m0
            let m2, _ = update (SummaryLoaded(Error "platform admin role required")) m1

            Expect.equal m2.Error (Some "platform admin role required") "error: banner carries the server message"
            Expect.isFalse m2.Loading "error: Loading cleared"
            Expect.isTrue m2.Loaded "error: Loaded set"
            Expect.equal m2.Summary None "error: no summary shown"

            // DismissError clears the banner without touching the rest.
            let m3, _ = update DismissError m2
            Expect.equal m3.Error None "dismiss: banner cleared"

        testCase "blank scope submit is a no-op"
        <| fun _ ->
            let m0, _ = init ()
            let m1, _ = update (SubmitScope "   ") m0
            Expect.equal m1.QueriedScope None "blank submit: no scope recorded"
            Expect.isFalse m1.Loading "blank submit: no fetch started"
    ]
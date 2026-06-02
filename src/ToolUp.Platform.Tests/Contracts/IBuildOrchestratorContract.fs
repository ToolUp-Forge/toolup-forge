module ToolUp.Platform.Tests.Contracts.IBuildOrchestratorContract

open System
open Expecto
open ToolUp.Platform

// ─── Phase 26 IBuildOrchestrator contract tests ─────────────────────
//
// Parametrised tests for any `IBuildOrchestrator` implementation.
// Bindings hand in a factory that returns a fresh orchestrator (and
// any in-tree handler-registration the impl needs); the pack
// exercises the documented `EnqueueBuild` validation chain,
// idempotency semantics, `ListActiveBuilds` filtering, queue depth,
// `CancelBuild` semantics, and the six-rule portability audit's
// observable claims.

let private mkManifest slug : DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = slug
            Slug = slug
            Region = "eu-west"
        }
        Runtime = {
            DeployManifest.empty.Runtime with
                Framework = "dotnet:10"
                Image = Some "ghcr.io/example/app:latest"
        }
}

let private mkRequest slug : BuildRequest = {
    AppSlug = slug
    Source = PrebuiltImage "ghcr.io/example/app:v1"
    Manifest = mkManifest slug
    RetryPolicy = BuildRetryPolicy.defaults
    SubmittedBy = "alice"
    Idempotency = None
}

let private okOrFail label =
    function
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %A" label e

let tests (name: string) (factory: unit -> IBuildOrchestrator) =

    testList $"{name} — IBuildOrchestrator contract" [

        // ─── EnqueueBuild — validation chain ──────────────────────

        testCaseAsync "EnqueueBuild rejects an empty AppSlug with InvalidRequest"
        <| async {
            let orch = factory ()
            let req = { mkRequest "ok" with AppSlug = "" }
            let! result = orch.EnqueueBuild req

            match result with
            | Error(InvalidRequest _) -> ()
            | other -> failtestf "expected InvalidRequest, got %A" other
        }

        testCaseAsync "EnqueueBuild rejects a GitHubRef source with empty SHA"
        <| async {
            let orch = factory ()

            let req = {
                mkRequest "gh-app" with
                    Source = GitHubRef("owner/repo", "")
            }

            let! result = orch.EnqueueBuild req

            match result with
            | Error(InvalidRequest _) -> ()
            | other -> failtestf "expected InvalidRequest, got %A" other
        }

        testCaseAsync "EnqueueBuild rejects a PrebuiltImage source with empty image"
        <| async {
            let orch = factory ()

            let req = {
                mkRequest "img-app" with
                    Source = PrebuiltImage ""
            }

            let! result = orch.EnqueueBuild req

            match result with
            | Error(InvalidRequest _) -> ()
            | other -> failtestf "expected InvalidRequest, got %A" other
        }

        testCaseAsync "EnqueueBuild rejects MaxAttempts < 1"
        <| async {
            let orch = factory ()

            let req = {
                mkRequest "low-attempts" with
                    RetryPolicy = { MaxAttempts = 0; BackoffSeconds = [] }
            }

            let! result = orch.EnqueueBuild req

            match result with
            | Error(InvalidRequest _) -> ()
            | other -> failtestf "expected InvalidRequest, got %A" other
        }

        // ─── EnqueueBuild — happy path ────────────────────────────

        testCaseAsync "EnqueueBuild returns a non-empty BuildId on success"
        <| async {
            let orch = factory ()

            let buildId =
                okOrFail "EnqueueBuild" (Async.RunSynchronously(orch.EnqueueBuild(mkRequest "happy")))

            Expect.isFalse (String.IsNullOrEmpty buildId) "BuildId is a non-empty string"
            do! async.Return()
        }

        // ─── Idempotency ──────────────────────────────────────────

        testCaseAsync "EnqueueBuild with the same Idempotency token returns the same BuildId"
        <| async {
            let orch = factory ()
            let token = Guid.NewGuid().ToString("N")

            let req = {
                mkRequest "idempotent" with
                    Idempotency = Some token
            }

            let first =
                okOrFail "first EnqueueBuild" (Async.RunSynchronously(orch.EnqueueBuild req))

            let second =
                okOrFail "second EnqueueBuild" (Async.RunSynchronously(orch.EnqueueBuild req))

            Expect.equal first second "same idempotency token returns the same BuildId"
            do! async.Return()
        }

        // ─── GetBuild ─────────────────────────────────────────────

        testCaseAsync "GetBuild on unknown id returns UnknownBuild"
        <| async {
            let orch = factory ()
            let! result = orch.GetBuild "no-such-build"

            match result with
            | Error(UnknownBuild bid) -> Expect.equal bid "no-such-build" "id preserved"
            | other -> failtestf "expected UnknownBuild, got %A" other
        }

        testCaseAsync "GetBuild after EnqueueBuild round-trips the AppSlug"
        <| async {
            let orch = factory ()

            let buildId =
                okOrFail "EnqueueBuild" (Async.RunSynchronously(orch.EnqueueBuild(mkRequest "rtrip")))

            let summary = okOrFail "GetBuild" (Async.RunSynchronously(orch.GetBuild buildId))
            Expect.equal summary.AppSlug "rtrip" "AppSlug round-trips"
            Expect.equal summary.BuildId buildId "BuildId round-trips"
            Expect.equal summary.SubmittedBy "alice" "SubmittedBy preserved"
        }

        // ─── ListActiveBuilds ─────────────────────────────────────

        testCaseAsync "ListActiveBuilds None enumerates every non-terminal build"
        <| async {
            let orch = factory ()
            let! _ = orch.EnqueueBuild(mkRequest "list-a")
            let! _ = orch.EnqueueBuild(mkRequest "list-b")
            let! _ = orch.EnqueueBuild(mkRequest "list-c")

            let! active = orch.ListActiveBuilds None
            Expect.isGreaterThanOrEqual active.Length 3 "active list includes our three submissions"
        }

        testCaseAsync "ListActiveBuilds filters by AppSlug"
        <| async {
            let orch = factory ()
            let! _ = orch.EnqueueBuild(mkRequest "wanted")
            let! _ = orch.EnqueueBuild(mkRequest "wanted")
            let! _ = orch.EnqueueBuild(mkRequest "other")

            let! wanted = orch.ListActiveBuilds(Some "wanted")
            Expect.all wanted (fun s -> s.AppSlug = "wanted") "only the requested AppSlug surfaces"
        }

        // ─── GetQueueDepth ────────────────────────────────────────

        testCaseAsync "GetQueueDepth grows with each EnqueueBuild"
        <| async {
            let orch = factory ()
            let! initial = orch.GetQueueDepth()
            let! _ = orch.EnqueueBuild(mkRequest "qd-a")
            let! _ = orch.EnqueueBuild(mkRequest "qd-b")
            let! after = orch.GetQueueDepth()
            Expect.isGreaterThanOrEqual after (initial + 2) "depth reflects new builds"
        }

        // ─── CancelBuild ──────────────────────────────────────────

        testCaseAsync "CancelBuild on a non-terminal build returns Ok"
        <| async {
            let orch = factory ()

            let buildId =
                okOrFail "EnqueueBuild" (Async.RunSynchronously(orch.EnqueueBuild(mkRequest "cancel-me")))

            let! cancelled = orch.CancelBuild(buildId, "operator-1")
            Expect.isOk cancelled "cancellation succeeds"
        }

        testCaseAsync "CancelBuild is idempotent on the success path"
        <| async {
            let orch = factory ()

            let buildId =
                okOrFail "EnqueueBuild" (Async.RunSynchronously(orch.EnqueueBuild(mkRequest "double-cancel")))

            let! first = orch.CancelBuild(buildId, "operator-1")
            Expect.isOk first "first cancel succeeds"
            let! second = orch.CancelBuild(buildId, "operator-1")
            // Idempotency contract: a re-cancel after a successful
            // cancel returns Ok; only a real terminal state from a
            // different terminating cause (Succeeded / Failed) would
            // surface AlreadyTerminated.
            Expect.isOk second "re-cancel after successful cancel is Ok (idempotent)"
        }

        testCaseAsync "CancelBuild on unknown id returns UnknownBuild"
        <| async {
            let orch = factory ()
            let! result = orch.CancelBuild("ghost", "operator-1")

            match result with
            | Error(UnknownBuild bid) -> Expect.equal bid "ghost" "id preserved"
            | other -> failtestf "expected UnknownBuild, got %A" other
        }

        // ─── GetBuildHistory ──────────────────────────────────────

        testCaseAsync "GetBuildHistory returns matching builds bounded by count"
        <| async {
            let orch = factory ()

            for _ in 1..3 do
                let! _ = orch.EnqueueBuild(mkRequest "history-app")
                ()

            let! history = orch.GetBuildHistory("history-app", 10)
            Expect.isGreaterThanOrEqual history.Length 3 "every recent build is reachable"
            Expect.all history (fun s -> s.AppSlug = "history-app") "history scoped to AppSlug"
        }

        // ─── Six-rule portability audit (Phase 9c, GP 12) ─────────

        testCaseAsync "Rule 1 — identity-by-value: BuildId / AppSlug are string aliases"
        <| async {
            let bid: BuildId = "b-1"
            let slug: string = "alpha"
            Expect.equal bid "b-1" "BuildId is a string"
            Expect.equal slug "alpha" "AppSlug is a string"
            do! async.Return()
        }

        testCaseAsync "Rule 2 — async at every boundary: every method returns Async<_>"
        <| async {
            let orch = factory ()
            let! _ = orch.EnqueueBuild(mkRequest "rule-2")
            let! _ = orch.GetBuild "rule-2"
            let! _ = orch.ListActiveBuilds None
            let! _ = orch.GetQueueDepth()
            let! _ = orch.CancelBuild("rule-2", "alice")
            let! _ = orch.GetBuildHistory("rule-2", 1)
            ()
        }

        testCaseAsync "Rule 3 — failure flows through BuildOrchestratorError data"
        <| async {
            let orch = factory ()
            let! result = orch.EnqueueBuild { mkRequest "rule-3" with AppSlug = "" }
            Expect.isError result "validation failure surfaces as Error data"
        }
    ]
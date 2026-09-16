module ToolUp.Platform.Tests.InProcess.FileSecretStoreCancellationTests

open System
open System.IO
open System.Threading
open Expecto
open ToolUp.Platform.Secrets

// ─── FileSecretStore - cancellable, non-blocking reads (Phase 6k) ────
//
// The read path used to call `File.ReadAllText` synchronously inside an
// `async { }` block. Two consequences, both silent:
//
//   1. the calling thread-pool thread was BLOCKED for the whole duration
//      of a stalled filesystem call (network share, AV scanner, slow
//      disk), so the chat path's 10 s secret-resolve timeout could not
//      even schedule its continuation; and
//   2. no cancellation token reached the read, so cancelling the
//      surrounding workflow abandoned the caller without stopping the
//      I/O.
//
// Phase 6k made the read `File.ReadAllTextAsync(path, ct)` with the
// ambient `Async.CancellationToken`.
//
// HONEST SCOPE OF THIS FILE. Three of these five tests are go-red
// proofs of the rewrite: they exercise resolution order, precedence and
// the malformed-file fallback, and they fail if the async conversion
// changed any of it. The two cancellation tests are NOT. F# `Async`
// checks its cancellation token at workflow start and at every bind, so
// a cancelled `GetSecret` raises `OperationCanceledException` whether or
// not the token reaches the file read - the synchronous implementation
// would pass them too. They are kept as PINS on a property a later
// refactor could plausibly break (swallowing cancellation at the
// `ISecretStore` boundary and reporting the secret absent, which is
// indistinguishable from "no key configured" and is exactly the silent
// misdiagnosis this phase exists to remove), not as evidence that the
// read is cancellable.
//
// The two properties the conversion actually buys - a thread-pool
// thread released for the duration of a stalled read, and an
// in-progress read cancelled rather than merely abandoned - are not
// falsifiable at this level without a controllable slow filesystem.
// Every candidate probe (a very large file, a constrained thread pool)
// is either timing-flaky or a process-global side effect in a shared
// test binary. Recorded rather than faked.

let private freshDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-secret-cancel-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private writeSecrets (dir: string) (fileName: string) (json: string) =
    File.WriteAllText(Path.Combine(dir, fileName), json)

let tests =
    testList "FileSecretStoreCancellation" [

        testCase "a cancelled read raises rather than reporting the secret absent"
        <| fun () ->
            let dir = freshDir ()
            writeSecrets dir "secrets.json" """{ "ANTHROPIC_API_KEY": "sk-live" }"""

            let store = FileSecretStore.FileSecretStore(baseDir = dir) :> ISecretStore

            use cts = new CancellationTokenSource()
            cts.Cancel()

            // The distinction this test pins: a cancelled resolve must
            // NOT come back as `None`. `None` is indistinguishable from
            // "this deployment has no key configured", which is exactly
            // the misdiagnosis the phase is removing - the operator sees
            // a config error where the truth is a stalled filesystem.
            // See the file header on why this is a pin, not a go-red.
            let cancelled =
                try
                    Async.RunSynchronously(
                        store.GetSecret("_platform", "ANTHROPIC_API_KEY"),
                        cancellationToken = cts.Token
                    )
                    |> ignore

                    false
                with :? OperationCanceledException ->
                    true

            Expect.isTrue cancelled "a cancelled GetSecret surfaces cancellation, never a None"

        testCase "an uncancelled read still resolves the platform file"
        <| fun () ->
            let dir = freshDir ()
            writeSecrets dir "secrets.json" """{ "ANTHROPIC_API_KEY": "sk-live" }"""

            let store = FileSecretStore.FileSecretStore(baseDir = dir) :> ISecretStore

            let value =
                store.GetSecret("_platform", "ANTHROPIC_API_KEY") |> Async.RunSynchronously

            Expect.equal value (Some "sk-live") "the async read resolves what the sync read did"

        testCase "a malformed secrets file still degrades to the empty map"
        <| fun () ->
            // Every failure that is NOT cancellation keeps the historic
            // swallow-and-return-empty fallback. A half-written or
            // hand-edited secrets file must not take the process down.
            let dir = freshDir ()
            writeSecrets dir "secrets.json" "{ this is not json"

            let store = FileSecretStore.FileSecretStore(baseDir = dir) :> ISecretStore

            let value = store.GetSecret("_platform", "SOME_KEY") |> Async.RunSynchronously

            Expect.isNone value "malformed JSON reads as no secrets, not as a throw"

        testCase "ListKeys is cancellable on the same terms as GetSecret"
        <| fun () ->
            let dir = freshDir ()
            writeSecrets dir "secrets-team-a.json" """{ "K": "v" }"""

            let store = FileSecretStore.FileSecretStore(baseDir = dir) :> ISecretStore

            use cts = new CancellationTokenSource()
            cts.Cancel()

            let cancelled =
                try
                    Async.RunSynchronously(store.ListKeys "team-a", cancellationToken = cts.Token)
                    |> ignore

                    false
                with :? OperationCanceledException ->
                    true

            Expect.isTrue cancelled "ListKeys honours the ambient token too"

        testCase "the scoped read path resolves its per-scope file"
        <| fun () ->
            let dir = freshDir ()
            writeSecrets dir "secrets-team-a.json" """{ "TEAM_KEY": "team-value" }"""
            writeSecrets dir "secrets.json" """{ "PLATFORM_KEY": "platform-value" }"""

            let store = FileSecretStore.FileSecretStore(baseDir = dir) :> ISecretStore

            Expect.equal
                (store.GetSecret("team-a", "TEAM_KEY") |> Async.RunSynchronously)
                (Some "team-value")
                "the scope file contributes to its own scope"

            // Scope isolation is unchanged by the async conversion: the
            // platform file never leaks into a team scope.
            Expect.isNone
                (store.GetSecret("team-a", "PLATFORM_KEY") |> Async.RunSynchronously)
                "the platform file does not leak into a team scope"
    ]
module ToolUp.Platform.Tests.InProcess.FileSecretStoreAtomicityTests

open System
open System.IO
open Expecto
open ToolUp.Platform.Secrets

// ─── FileSecretStore — atomic read-modify-write ──────────────────────
//
// FileSecretStore.SetSecret / DeleteSecret persist a scope file with a
// loadFile → Map.add/remove → writeFile sequence. That whole sequence is
// serialised under the store's cacheLock, so two concurrent writes to the
// same scope file can never each read the old contents and each write back
// their own superset (last-writer-wins silently dropping a key — e.g. a
// just-rotated refresh token stranded by a racing access-token write). The
// write itself is temp-then-rename, so it is also crash-atomic.
//
// These tests drive genuine concurrency (Async.Parallel) at one scope file
// and assert no lost update; they fail if the RMW is un-serialised.

/// Fresh store over a unique temp directory, plus that directory so a test
/// can inspect the on-disk file. The `baseDir` ctor param keeps every
/// instance isolated — no cwd manipulation, safe to run in parallel.
let private freshStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-secret-atomic-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    (FileSecretStore.FileSecretStore(baseDir = dir) :> ISecretStore), dir

let private setOk (store: ISecretStore) scope key value = async {
    match! store.SetSecret(scope, key, value) with
    | Ok() -> ()
    | Error e -> failwithf "SetSecret %s/%s failed: %s" scope key e
}

let tests =
    testList "FileSecretStoreAtomicity" [
        testCaseAsync "N concurrent SetSecret calls with distinct keys all survive"
        <| async {
            let store, _ = freshStore ()
            let scope = "team-concurrent"
            let n = 40

            // Fire N writes at the same scope file concurrently. Without
            // a serialised RMW, several of these race on load-then-write
            // and lose one another's key.
            do!
                [ for i in 1..n -> setOk store scope $"KEY_{i}" $"value-{i}" ]
                |> Async.Parallel
                |> Async.Ignore

            let! keys = store.ListKeys scope

            Expect.equal (List.length keys) n "every concurrently-written key is persisted (no lost update)"

            for i in 1..n do
                let! v = store.GetSecret(scope, $"KEY_{i}")
                Expect.equal v (Some $"value-{i}") $"KEY_{i} holds its own value"
        }

        testCaseAsync "SetSecret racing DeleteSecret on different keys leaves both results consistent"
        <| async {
            let store, _ = freshStore ()
            let scope = "team-race"

            // Seed two keys.
            do! setOk store scope "KEEP" "keep-value"
            do! setOk store scope "DROP" "drop-value"

            // Concurrently add a third key and delete the first-seeded
            // one. Both mutations must land: neither the add resurrects
            // DROP nor the delete clobbers ADD.
            let deleteDrop = async {
                match! store.DeleteSecret(scope, "DROP") with
                | Ok() -> ()
                | Error e -> failwithf "DeleteSecret failed: %s" e
            }

            do!
                [ setOk store scope "ADD" "add-value"; deleteDrop ]
                |> Async.Parallel
                |> Async.Ignore

            let! keep = store.GetSecret(scope, "KEEP")
            let! drop = store.GetSecret(scope, "DROP")
            let! add = store.GetSecret(scope, "ADD")

            Expect.equal keep (Some "keep-value") "the untouched key survives"
            Expect.equal drop None "the deleted key is gone"
            Expect.equal add (Some "add-value") "the concurrently-added key survives"
        }

        testCaseAsync "many concurrent updates to the SAME key converge to one of the written values"
        <| async {
            let store, _ = freshStore ()
            let scope = "team-samekey"
            let n = 25

            do!
                [ for i in 1..n -> setOk store scope "SHARED" $"v{i}" ]
                |> Async.Parallel
                |> Async.Ignore

            let! keys = store.ListKeys scope
            Expect.equal keys [ "SHARED" ] "exactly one key exists after concurrent same-key writes"

            let! v = store.GetSecret(scope, "SHARED")

            match v with
            | Some value -> Expect.stringStarts value "v" "the surviving value is one of the writes"
            | None -> failwith "the shared key must hold some written value, not be dropped"
        }

        testCaseAsync "single-writer sequential behaviour is unchanged"
        <| async {
            let store, dir = freshStore ()
            let scope = "team-seq"

            do! setOk store scope "A" "1"
            do! setOk store scope "B" "2"

            match! store.DeleteSecret(scope, "A") with
            | Ok() -> ()
            | Error e -> failwithf "DeleteSecret failed: %s" e

            let! a = store.GetSecret(scope, "A")
            let! b = store.GetSecret(scope, "B")
            Expect.equal a None "deleted key is absent"
            Expect.equal b (Some "2") "remaining key retains its value"

            // The scope file is a single well-formed JSON object — the
            // temp-then-rename never leaves a torn or leftover file.
            let scopeFile = Path.Combine(dir, $"secrets-{scope}.json")
            Expect.isTrue (File.Exists scopeFile) "the scope file exists after writes"

            let leftovers = Directory.GetFiles(dir, $"secrets-{scope}.json.tmp-*")
            Expect.equal (Array.length leftovers) 0 "no temp files are left behind"
        }
    ]
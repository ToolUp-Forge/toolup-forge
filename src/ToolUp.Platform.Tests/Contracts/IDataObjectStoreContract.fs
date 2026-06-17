module ToolUp.Platform.Tests.Contracts.IDataObjectStoreContract

open System
open System.Text
open Expecto
open ToolUp.Platform

/// Contract test list for any `IDataObjectStore` implementation.
/// The factory produces a `(store, scopeA, scopeB)` triple where
/// `scopeA` and `scopeB` are isolated container names — used to
/// exercise the cross-scope isolation contract without leaking
/// between tests. Each test invokes the factory once for a clean
/// slate.
///
/// All `IDataObjectStore` implementations must satisfy these
/// assertions. Divergence is a portability bug, not a feature gap.
let tests (name: string) (factory: unit -> IDataObjectStore * string * string) =
    let bytesOf (s: string) = Encoding.UTF8.GetBytes s
    let textOf (b: byte[]) = Encoding.UTF8.GetString b

    let okOrFail label result =
        match result with
        | Ok v -> v
        | Error err -> failtestf "%s: expected Ok, got %A" label err

    let saveContent (store: IDataObjectStore) scope objectId (content: string) (policy: VersioningPolicy) = async {
        let! result = store.Save(scope, objectId, bytesOf content, "TestType", "alice@test", Map.empty, policy)

        return okOrFail "Save" result
    }

    testList $"{name} — IDataObjectStore contract" [

        testCaseAsync "Save then Get round-trips with Unversioned policy"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "obj1" "hello" Unversioned

            let obj, content =
                okOrFail "Get" (store.Get(scope, "obj1") |> Async.RunSynchronously)

            Expect.equal obj.Version 1 "Unversioned object stays at v1"
            Expect.equal (textOf content) "hello" "round-trip content matches"
            Expect.equal obj.Policy Unversioned "policy preserved"
            Expect.equal obj.CreatedBy "alice@test" "createdBy preserved"
            Expect.equal obj.DataType "TestType" "dataType preserved"
        }

        testCaseAsync "Versioned: 3 saves produce 3 versions"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "audit" "v1-content" Versioned
            let! _ = saveContent store scope "audit" "v2-content" Versioned
            let! _ = saveContent store scope "audit" "v3-content" Versioned

            let! versions = store.ListVersions(scope, "audit")
            Expect.equal versions.Length 3 "3 versions exist"

            let v1, c1 =
                okOrFail "v1" (store.GetVersion(scope, "audit", 1) |> Async.RunSynchronously)

            let v2, c2 =
                okOrFail "v2" (store.GetVersion(scope, "audit", 2) |> Async.RunSynchronously)

            let v3, c3 =
                okOrFail "v3" (store.GetVersion(scope, "audit", 3) |> Async.RunSynchronously)

            Expect.equal v1.Version 1 "v1.Version"
            Expect.equal v2.Version 2 "v2.Version"
            Expect.equal v3.Version 3 "v3.Version"
            Expect.equal (textOf c1) "v1-content" "v1 content preserved"
            Expect.equal (textOf c2) "v2-content" "v2 content preserved"
            Expect.equal (textOf c3) "v3-content" "v3 content preserved"
        }

        testCaseAsync "Get returns latest version"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "doc" "first" Versioned
            let! _ = saveContent store scope "doc" "second" Versioned

            let obj, content =
                okOrFail "Get" (store.Get(scope, "doc") |> Async.RunSynchronously)

            Expect.equal obj.Version 2 "Get returns latest"
            Expect.equal (textOf content) "second" "Get returns latest content"
        }

        testCaseAsync "GetContent returns bytes by content hash"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "doc" "payload-bytes" Unversioned

            let obj, _ = okOrFail "Get" (store.Get(scope, "doc") |> Async.RunSynchronously)

            let content =
                okOrFail "GetContent" (store.GetContent(scope, obj.ContentHash) |> Async.RunSynchronously)

            Expect.equal (textOf content) "payload-bytes" "GetContent returns the object's bytes by hash"
        }

        testCaseAsync "GetContent of an unknown hash returns an error"
        <| async {
            let store, scope, _ = factory ()
            let! result = store.GetContent(scope, "sha256-that-does-not-exist")

            match result with
            | Ok _ -> failtest "expected an error for a missing content hash"
            | Error _ -> ()
        }

        testCaseAsync "GetContent is scope-isolated"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! _ = saveContent store scopeA "a-only" "secret-a" Unversioned

            let objA, _ = okOrFail "Get" (store.Get(scopeA, "a-only") |> Async.RunSynchronously)

            // Content blobs are per-scope: the same hash resolved against
            // another scope must not surface scopeA's bytes (GP 4).
            let! crossScope = store.GetContent(scopeB, objA.ContentHash)

            match crossScope with
            | Ok _ -> failtest "scopeB must not read scopeA's content by hash (GP4)"
            | Error _ -> ()
        }

        testCaseAsync "Get / GetVersion of missing object returns NotFound"
        <| async {
            let store, scope, _ = factory ()

            match! store.Get(scope, "nope") with
            | Error NotFound -> ()
            | other -> failtestf "Expected NotFound; got %A" other

            match! store.GetVersion(scope, "nope", 1) with
            | Error NotFound -> ()
            | other -> failtestf "Expected NotFound for GetVersion; got %A" other
        }

        testCaseAsync "GetVersion of missing version on existing object returns VersionNotFound"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "doc" "v1" Versioned

            match! store.GetVersion(scope, "doc", 99) with
            | Error(VersionNotFound 99) -> ()
            | other -> failtestf "Expected VersionNotFound 99; got %A" other
        }

        testCaseAsync "Recover creates new version with original content"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "doc" "alpha" Versioned
            let! _ = saveContent store scope "doc" "beta" Versioned
            let! _ = saveContent store scope "doc" "gamma" Versioned

            let! recovered = store.Recover(scope, "doc", 1, "bob@test")
            let recoveredObj = okOrFail "Recover" recovered
            Expect.equal recoveredObj.Version 4 "Recover creates v4"
            Expect.equal recoveredObj.CreatedBy "bob@test" "recoverer recorded"

            Expect.equal
                (recoveredObj.Metadata.TryFind "_recovered_from")
                (Some "v1")
                "_recovered_from annotation present"

            let _, content =
                okOrFail "Get after recover" (store.Get(scope, "doc") |> Async.RunSynchronously)

            Expect.equal (textOf content) "alpha" "recovered content matches v1"

            let! versions = store.ListVersions(scope, "doc")
            Expect.equal versions.Length 4 "history is preserved (4 versions total)"
        }

        testCaseAsync "Recover preserves original CreatedBy on the immutable source version"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "doc" "v1" Versioned
            let! _ = saveContent store scope "doc" "v2" Versioned
            let! _ = store.Recover(scope, "doc", 1, "carol@test")

            let v1, _ =
                okOrFail "v1" (store.GetVersion(scope, "doc", 1) |> Async.RunSynchronously)

            Expect.equal v1.CreatedBy "alice@test" "v1.CreatedBy is the original author, not the recoverer"
        }

        testCaseAsync "StrictlyVersioned: Delete returns DeleteForbidden"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "audit" "immutable" StrictlyVersioned

            match! store.Delete(scope, "audit") with
            | Error DeleteForbidden -> ()
            | other -> failtestf "Expected DeleteForbidden; got %A" other

            // Object still exists after the failed delete attempt.
            let _, content =
                okOrFail "Get after rejected delete" (store.Get(scope, "audit") |> Async.RunSynchronously)

            Expect.equal (textOf content) "immutable" "content intact"
        }

        testCaseAsync "Versioned: Delete removes all versions"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "doc" "v1" Versioned
            let! _ = saveContent store scope "doc" "v2" Versioned

            match! store.Delete(scope, "doc") with
            | Ok() -> ()
            | Error err -> failtestf "Delete failed: %A" err

            match! store.Get(scope, "doc") with
            | Error NotFound -> ()
            | other -> failtestf "Expected NotFound after delete; got %A" other

            let! versions = store.ListVersions(scope, "doc")
            Expect.equal versions.Length 0 "no versions after delete"
        }

        testCaseAsync "Delete on missing object is idempotent"
        <| async {
            let store, scope, _ = factory ()

            match! store.Delete(scope, "ghost") with
            | Ok() -> ()
            | Error err -> failtestf "Idempotent delete should be Ok; got %A" err
        }

        testCaseAsync "Sticky policy: Save with mismatched policy returns PolicyMismatch"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "doc" "v1" Versioned

            let! conflict = store.Save(scope, "doc", bytesOf "v2", "T", "u", Map.empty, StrictlyVersioned)

            match conflict with
            | Error(PolicyMismatch(Versioned, StrictlyVersioned)) -> ()
            | other -> failtestf "Expected PolicyMismatch(Versioned, StrictlyVersioned); got %A" other
        }

        testCaseAsync "Cross-scope isolation: scope B cannot see scope A's objects"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! _ = saveContent store scopeA "secret" "team-a-only" Versioned

            match! store.Get(scopeB, "secret") with
            | Error NotFound -> ()
            | other -> failtestf "Expected NotFound in scope B; got %A" other

            let! versions = store.ListVersions(scopeB, "secret")
            Expect.equal versions.Length 0 "ListVersions in scope B is empty"

            let! objects = store.ListObjects(scopeB)
            Expect.equal objects.Length 0 "ListObjects in scope B is empty"
        }

        testCaseAsync "ListObjects returns latest version of each object"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "a" "a-v1" Versioned
            let! _ = saveContent store scope "a" "a-v2" Versioned
            let! _ = saveContent store scope "b" "b-v1" Unversioned

            let! objects = store.ListObjects(scope)
            Expect.equal objects.Length 2 "two distinct objects"

            let byId = objects |> List.map (fun o -> o.ObjectId, o.Version) |> Map.ofList
            Expect.equal (Map.find "a" byId) 2 "object a is at v2"
            Expect.equal (Map.find "b" byId) 1 "object b is at v1"
        }

        testCaseAsync "Content-addressable dedup: identical content shares ContentHash"
        <| async {
            let store, scope, _ = factory ()
            let! v1 = saveContent store scope "obj" "same content" Versioned
            let! v2 = saveContent store scope "obj" "same content" Versioned

            Expect.equal v1.ContentHash v2.ContentHash "same content => same ContentHash"
            Expect.notEqual v1.Version v2.Version "but different versions"

            // Both versions still readable independently.
            let _, c1 =
                okOrFail "v1" (store.GetVersion(scope, "obj", 1) |> Async.RunSynchronously)

            let _, c2 =
                okOrFail "v2" (store.GetVersion(scope, "obj", 2) |> Async.RunSynchronously)

            Expect.equal (textOf c1) "same content" "v1 content"
            Expect.equal (textOf c2) "same content" "v2 content"
        }

        testCaseAsync "Unversioned: subsequent saves overwrite v1"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "config" "first" Unversioned
            let! _ = saveContent store scope "config" "second" Unversioned

            let! versions = store.ListVersions(scope, "config")
            Expect.equal versions.Length 1 "still only one version"

            let _, content =
                okOrFail "Get" (store.Get(scope, "config") |> Async.RunSynchronously)

            Expect.equal (textOf content) "second" "latest content wins"
        }

        testCaseAsync "Purge wipes the entire scope"
        <| async {
            let store, scope, _ = factory ()
            let! _ = saveContent store scope "a" "alpha" Versioned
            let! _ = saveContent store scope "b" "beta" StrictlyVersioned
            let! _ = saveContent store scope "c" "gamma" Unversioned

            match! store.Purge scope with
            | Ok() -> ()
            | Error err -> failtestf "Purge failed: %A" err

            let! objects = store.ListObjects scope
            Expect.equal objects.Length 0 "scope empty after purge"

            // Purge bypasses StrictlyVersioned protection — see interface
            // docstring on `Purge` for rationale.
            match! store.Get(scope, "b") with
            | Error NotFound -> ()
            | other -> failtestf "StrictlyVersioned object should be gone after Purge; got %A" other
        }

        testCaseAsync "ObjectId '_content' is reserved"
        <| async {
            let store, scope, _ = factory ()

            let! result = store.Save(scope, "_content", bytesOf "x", "T", "u", Map.empty, Unversioned)

            match result with
            | Error(StorageFailure msg) -> Expect.stringContains msg "_content" "error mentions reserved id"
            | other -> failtestf "Expected reserved-id rejection; got %A" other
        }
    ]
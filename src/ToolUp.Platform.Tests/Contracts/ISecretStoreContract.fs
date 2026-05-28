module ToolUp.Platform.Tests.Contracts.ISecretStoreContract

open System
open Expecto
open ToolUp.Platform.Secrets

/// Contract test list for any writable `ISecretStore` implementation.
/// Factory produces a fresh, empty store per test. Tests use GUID-
/// suffixed scope identifiers so implementations that share underlying
/// state across factory invocations stay isolated.
///
/// Read-only stores (EnvironmentSecretStore) are a separate contract —
/// the write surface is deliberately restricted and the common
/// behaviour they share with writable stores is just `GetSecret`.
/// Running a read-only store against this pack will correctly fail
/// at the first write assertion.
let tests (name: string) (factory: unit -> ISecretStore) =
    let uniqueScope () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        "team-" + suffix

    testList $"{name} — ISecretStore contract" [
        testCaseAsync "GetSecret on missing key returns None"
        <| async {
            let store = factory ()
            let! v = store.GetSecret(uniqueScope (), "MISSING")
            Expect.isNone v "no key set yet"
        }

        testCaseAsync "SetSecret then GetSecret returns the stored value"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            match! store.SetSecret(scope, "API_KEY", "sk-abc123") with
            | Error e -> failtestf "SetSecret failed: %s" e
            | Ok() -> ()

            let! v = store.GetSecret(scope, "API_KEY")
            Expect.equal v (Some "sk-abc123") "value round-trips"
        }

        testCaseAsync "SetSecret overwrites a previous value"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            let! _ = store.SetSecret(scope, "K", "v1")
            let! _ = store.SetSecret(scope, "K", "v2")
            let! v = store.GetSecret(scope, "K")

            Expect.equal v (Some "v2") "latest write wins"
        }

        testCaseAsync "DeleteSecret removes a stored value"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            let! _ = store.SetSecret(scope, "TEMP", "secret")

            match! store.DeleteSecret(scope, "TEMP") with
            | Error e -> failtestf "Delete failed: %s" e
            | Ok() -> ()

            let! v = store.GetSecret(scope, "TEMP")
            Expect.isNone v "value gone after delete"
        }

        testCaseAsync "DeleteSecret is idempotent on missing keys"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            match! store.DeleteSecret(scope, "NOT_THERE") with
            | Ok() -> ()
            | Error e -> failtestf "Delete of missing key should be Ok; got Error: %s" e
        }

        testCaseAsync "ListKeys of an untouched scope returns []"
        <| async {
            let store = factory ()
            let! keys = store.ListKeys(uniqueScope ())
            Expect.isEmpty keys "no keys set"
        }

        testCaseAsync "ListKeys returns all and only the set keys"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            let! _ = store.SetSecret(scope, "A", "1")
            let! _ = store.SetSecret(scope, "B", "2")
            let! keys = store.ListKeys scope

            Expect.contains keys "A" "A present"
            Expect.contains keys "B" "B present"
            Expect.hasLength keys 2 "exactly the two set keys"
        }

        testCaseAsync "Scope isolation — SetSecret in A is invisible in B"
        <| async {
            let store = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()

            let! _ = store.SetSecret(scopeA, "PRIVATE", "shh")

            let! fromB = store.GetSecret(scopeB, "PRIVATE")
            Expect.isNone fromB "scope B doesn't see scope A's keys"

            let! keysFromB = store.ListKeys scopeB
            Expect.isEmpty keysFromB "scope B's key list is empty"
        }

        testCaseAsync "Scope isolation — DeleteSecret in A does not affect B"
        <| async {
            let store = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()

            let! _ = store.SetSecret(scopeA, "K", "vA")
            let! _ = store.SetSecret(scopeB, "K", "vB")

            let! _ = store.DeleteSecret(scopeA, "K")

            let! valueB = store.GetSecret(scopeB, "K")
            Expect.equal valueB (Some "vB") "scope B's value intact after A's delete"
        }
    ]
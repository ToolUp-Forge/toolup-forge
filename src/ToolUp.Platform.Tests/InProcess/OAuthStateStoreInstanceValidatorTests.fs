module ToolUp.Platform.Tests.InProcess.OAuthStateStoreInstanceValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (replicaCount: int) (escapeHatch: bool) : ServerConfig = {
    ServerConfig.defaults with
        ReplicaCount = replicaCount
        AcceptInMemoryOAuthStateInMultiInstance = escapeHatch
}

let private inMemoryStore () : IOAuthStateStore =
    InMemoryOAuthStateStore() :> IOAuthStateStore

/// Stand-in non-in-memory store — distinguishable by type. Proves the
/// validator is `InMemoryOAuthStateStore`-specific rather than blanket-
/// refusing every `IOAuthStateStore` under multi-instance (a real
/// distributed companion must NOT be refused).
let private alternativeStore () : IOAuthStateStore =
    { new IOAuthStateStore with
        member _.Save _ = async { return Result.Ok() }
        member _.TryConsume _ = async { return None }
        member _.Cleanup _ = async { return 0 }
    }

let private validate (config: ServerConfig) (store: IOAuthStateStore) : ValidationResult =
    let v =
        OAuthStateStoreInstanceValidator.OAuthStateStoreInstanceValidator(config, store) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "OAuth state store multi-instance validator" [

        test "in-memory store + ReplicaCount=1 → Ok (single-instance is the safe default)" {
            let result = validate (cfg 1 false) (inMemoryStore ())
            Expect.equal result Ok "single instance is the default safe path"
        }

        test "in-memory store + ReplicaCount=2 + no escape hatch → Error" {
            let result = validate (cfg 2 false) (inMemoryStore ())

            match result with
            | Error msg ->
                Expect.stringContains msg "in-memory" "names the offending store"
                Expect.stringContains msg "ReplicaCount = 2" "names the replica count"
                Expect.stringContains msg "state-mismatch" "explains the consequence"

                Expect.stringContains msg "AcceptInMemoryOAuthStateInMultiInstance" "documents the escape hatch"
            | other -> failtestf "expected Error, got %A" other
        }

        test "in-memory store + ReplicaCount=10 → Error (any N>1)" {
            let result = validate (cfg 10 false) (inMemoryStore ())

            match result with
            | Error _ -> ()
            | other -> failtestf "expected Error, got %A" other
        }

        test "in-memory store + ReplicaCount=2 + escape hatch → Ok" {
            let result = validate (cfg 2 true) (inMemoryStore ())
            Expect.equal result Ok "explicit opt-in passes"
        }

        test "alternative (distributed) store + ReplicaCount=10 → Ok (validator is impl-specific)" {
            let result = validate (cfg 10 false) (alternativeStore ())
            Expect.equal result Ok "a distributed IOAuthStateStore must not be refused"
        }

        test "Validator metadata is well-formed" {
            let v =
                OAuthStateStoreInstanceValidator.OAuthStateStoreInstanceValidator(cfg 1 false, inMemoryStore ())
                :> IConfigValidator

            Expect.equal v.Name "oauth-state-store-instance" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]
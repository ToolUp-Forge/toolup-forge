module ToolUp.Platform.Tests.InProcess.IdempotencyStoreInstanceValidatorTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Remoting.Server

let private cfg (replicaCount: int) : ServerConfig = {
    ServerConfig.defaults with
        ReplicaCount = replicaCount
}

/// A `ServiceCollection` with the in-memory default `IIdempotencyStore`
/// registered — the shape a scaled deployment lands in when it wires the
/// in-process default and surfaces it to DI for preflight visibility.
let private servicesWithInMemoryStore () : IServiceCollection =
    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IIdempotencyStore>(InMemoryIdempotencyStore()) |> ignore
    services

/// Stand-in distributed `IIdempotencyStore` — distinguishable by type.
/// Proves the validator is `InMemoryIdempotencyStore`-specific rather
/// than blanket-warning every registered `IIdempotencyStore` under
/// multi-instance (a real distributed companion must NOT warn).
let private alternativeStore () : IIdempotencyStore =
    { new IIdempotencyStore with
        member _.TryGet(_, _) = async { return None }
        member _.Store(_, _, _, _) = async { return () }
    }

let private servicesWithDistributedStore () : IServiceCollection =
    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IIdempotencyStore>(alternativeStore ()) |> ignore
    services

let private validate (config: ServerConfig) (services: IServiceCollection) : ValidationResult =
    let v =
        IdempotencyStoreInstanceValidator.IdempotencyStoreInstanceValidator(config, services) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Idempotency store multi-instance validator" [

        test "in-memory store + ReplicaCount=1 → Ok (single-instance is the safe default)" {
            let result = validate (cfg 1) (servicesWithInMemoryStore ())
            Expect.equal result Ok "single instance is the default safe path"
        }

        test "in-memory store + ReplicaCount=2 → Warning" {
            let result = validate (cfg 2) (servicesWithInMemoryStore ())

            match result with
            | Warning msg ->
                Expect.stringContains msg "in-memory" "names the offending store"
                Expect.stringContains msg "ReplicaCount = 2" "names the replica count"
                Expect.stringContains msg "re-executes the handler" "explains the consequence"
                Expect.stringContains msg "BlobIdempotencyStore" "points at the distributed remedy"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "in-memory store + ReplicaCount=10 → Warning (any N>1)" {
            let result = validate (cfg 10) (servicesWithInMemoryStore ())

            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %A" other
        }

        test "no store registered + ReplicaCount=10 → Ok (idempotency not wired ⇒ nothing to warn)" {
            let result = validate (cfg 10) (ServiceCollection() :> IServiceCollection)
            Expect.equal result Ok "an unwired idempotency substrate has no cross-instance hole"
        }

        test "distributed store + ReplicaCount=10 → Ok (validator is impl-specific)" {
            let result = validate (cfg 10) (servicesWithDistributedStore ())
            Expect.equal result Ok "a distributed IIdempotencyStore must not warn"
        }

        test "Validator metadata is well-formed" {
            let v =
                IdempotencyStoreInstanceValidator.IdempotencyStoreInstanceValidator(
                    cfg 1,
                    ServiceCollection() :> IServiceCollection
                )
                :> IConfigValidator

            Expect.equal v.Name "idempotency-store-instance" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]
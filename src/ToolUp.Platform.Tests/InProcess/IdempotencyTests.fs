module ToolUp.Platform.Tests.InProcess.IdempotencyTests

open System
open System.IO
open Expecto
open ToolUp.Remoting.Server
open ToolUp.Platform.Tests.Contracts

// ─── Phase 69f — idempotency-key residuals ────────────────────────────
//
//   * the `IIdempotencyStore` portability conformance pack bound to BOTH
//     default impls — the in-process `InMemoryIdempotencyStore` and the
//     distributed `BlobIdempotencyStore` (over a temp LocalFileStorage);
//   * family-agnostic + non-public `[<Idempotent>]` classification — the
//     tier-shared `ToolUp.Platform` mirror that Fable API records carry
//     is recognised identically to the server-tier marker;
//   * the dispatcher orders idempotency BEFORE rate-limit so a replay
//     never spends the budget (source-audit pin).

// ── Fixtures ────────────────────────────────────────────────────────

type private ServerIdempotentApi = {
    [<Idempotent>]
    Mutate: int -> Async<int>
    NonIdempotent: int -> Async<int>
}

type private MirrorIdempotentApi = {
    [<ToolUp.Platform.Idempotent>]
    Mutate: int -> Async<int>
    NonIdempotent: int -> Async<int>
}

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private serverSource (relative: string list) =
    File.ReadAllText(
        Path.Combine(
            repoRoot () :: "src" :: "ToolUp.Platform.Server" :: "Server" :: relative
            |> List.toArray
        )
    )

[<Tests>]
let tests =
    testList "Phase 69f — idempotency keys" [

        // ── Portability conformance, both default impls ──
        IIdempotencyStoreContract.tests "InMemoryIdempotencyStore" (fun () ->
            InMemoryIdempotencyStore() :> IIdempotencyStore)

        IIdempotencyStoreContract.tests "BlobIdempotencyStore" (fun () ->
            // Each factory call gets a fresh temp dir so runs don't alias.
            let tempDir =
                Path.Combine(Path.GetTempPath(), "toolup-idem-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory tempDir |> ignore

            let blob =
                LocalFileStorage.LocalFileStorage(tempDir) :> ToolUp.Platform.BlobStorage.IBlobStorage

            BlobIdempotencyStore(blob) :> IIdempotencyStore)

        // ── Classification ──
        test "server-family and mirror-family [<Idempotent>] classify identically" {
            let server = Idempotency.classify typeof<ServerIdempotentApi>
            let mirror = Idempotency.classify typeof<MirrorIdempotentApi>

            Expect.equal server (Set.ofList [ "Mutate" ]) "only the marked method is idempotent (server family)"

            Expect.equal
                mirror
                server
                "the ToolUp.Platform mirror classifies identically — a Fable API record's marker is not invisible"
        }

        // ── Dispatcher ordering (source pin) ──
        test "idempotency pre-flight precedes rate-limit (a replay must not spend the budget)" {
            let adapter = serverSource [ "Remoting"; "Giraffe"; "GiraffeAdapter.fs" ]
            let idemIdx = adapter.IndexOf "Phase 69f — idempotency pre-flight"
            let rateLimitIdx = adapter.IndexOf "Phase 69g — rate-limit pre-flight"

            Expect.isGreaterThan idemIdx -1 "the idempotency pre-flight must exist"
            Expect.isGreaterThan rateLimitIdx -1 "the rate-limit pre-flight must exist"
            Expect.isLessThan idemIdx rateLimitIdx "idempotency runs before rate-limit"
        }
    ]
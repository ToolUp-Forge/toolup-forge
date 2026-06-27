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

        // ── Phase 164 — TTL sweep job ──
        testCaseAsync "IdempotencySweepJob removes only TTL-expired entries; live entries still replay"
        <| async {
            let tempDir =
                Path.Combine(Path.GetTempPath(), "toolup-idem-sweep-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory tempDir |> ignore

            let blob =
                LocalFileStorage.LocalFileStorage(tempDir) :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store = BlobIdempotencyStore(blob)
            let istore = store :> IIdempotencyStore

            let resp: MemoisedResponse = {
                Body = [| 1uy; 2uy; 3uy |]
                StatusCode = 200
                ContentType = "application/json"
                RequestBodyHash = ""
            }

            // One live entry (+1h) and one already-expired entry (-1h); the
            // store stamps absolute expiry, so a negative TTL writes a
            // past-expiry envelope without waiting.
            do! istore.Store("live", "s", resp, TimeSpan.FromHours 1.0)
            do! istore.Store("expired", "s", resp, TimeSpan.FromHours -1.0)

            let! before = blob.List(BlobIdempotencyLayout.DefaultContainer, BlobIdempotencyLayout.BlobPrefix)
            Expect.equal before.Length 2 "both envelopes are on disk before the sweep"

            // Build a throwaway JobContext — the sweep ignores it (rule 4:
            // all state read from the store each run).
            let ctx: ToolUp.Platform.JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = BlobIdempotencyLayout.DefaultContainer
                AccessContext = ToolUp.Platform.AccessContext.unrestricted (ToolUp.Platform.AuthenticatedUser "_system")
                Attempt = 1
                Trigger = ToolUp.Platform.Trigger.Manual
                TriggerSource = ToolUp.Platform.TriggerSource.ScheduledManually "_system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = ""
                DeadLetterDestination = None
            }

            let! result = (IdempotencySweep.handler blob).Execute ctx
            Expect.equal result ToolUp.Platform.JobResult.Success "sweep completes successfully"

            // Only the expired blob is gone — proved by the on-disk count,
            // not a TryGet (which would itself lazily evict the expired one).
            let! after = blob.List(BlobIdempotencyLayout.DefaultContainer, BlobIdempotencyLayout.BlobPrefix)
            Expect.equal after.Length 1 "the sweep removed exactly the expired envelope"

            let! liveGet = istore.TryGet("live", "s")
            Expect.isSome liveGet "the live entry still replays after the sweep"

            let! expiredGet = istore.TryGet("expired", "s")
            Expect.isNone expiredGet "the expired entry is gone"
        }

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
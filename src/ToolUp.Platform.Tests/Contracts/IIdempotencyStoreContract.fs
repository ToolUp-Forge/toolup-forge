module ToolUp.Platform.Tests.Contracts.IIdempotencyStoreContract

open System
open Expecto
open ToolUp.Remoting.Server

// ─── Phase 69f — IIdempotencyStore portability conformance pack ────────
//
// Parametrised contract pack — any `IIdempotencyStore` implementation
// binds to it (the in-process default, the blob-backed default, or an
// external Redis / DynamoDB impl). Validates the six-portability-rule
// surface: identity-by-value (string keys), async at the boundary,
// stateless between calls, per-scope isolation (the security boundary),
// and TTL precision. `factory` returns a fresh store per run.

let private mkResponse (body: string) (status: int) : MemoisedResponse = {
    Body = System.Text.Encoding.UTF8.GetBytes body
    StatusCode = status
    ContentType = "application/json"
    RequestBodyHash = "hash-" + body
}

let private bodyText (r: MemoisedResponse) =
    System.Text.Encoding.UTF8.GetString r.Body

let tests (name: string) (factory: unit -> IIdempotencyStore) =
    testList (sprintf "IIdempotencyStore conformance — %s" name) [
        testCaseAsync
            "store then get returns the byte-identical memoised response"
            (async {
                let store = factory ()
                let response = mkResponse "first-call-body" 200
                do! store.Store("key-1", "subject:a|M", response, TimeSpan.FromMinutes 5.0)
                let! got = store.TryGet("key-1", "subject:a|M")

                match got with
                | Some r ->
                    Expect.equal (bodyText r) "first-call-body" "body round-trips"
                    Expect.equal r.StatusCode 200 "status round-trips"
                    Expect.equal r.ContentType "application/json" "content type round-trips"
                    Expect.equal r.RequestBodyHash "hash-first-call-body" "body hash round-trips"
                | None -> failtest "expected a memoised response"
            })

        testCaseAsync
            "absent key is a miss"
            (async {
                let store = factory ()
                let! got = store.TryGet("never-stored", "subject:a|M")
                Expect.isNone got "an unstored key returns None"
            })

        testCaseAsync
            "different key under the same scope is a distinct slot"
            (async {
                let store = factory ()
                do! store.Store("key-A", "subject:a|M", mkResponse "A" 200, TimeSpan.FromMinutes 5.0)
                let! got = store.TryGet("key-B", "subject:a|M")
                Expect.isNone got "a different key does not collide"
            })

        testCaseAsync
            "the same key under a different scope is a distinct slot (security boundary)"
            (async {
                let store = factory ()
                // Same client-supplied UUID, two different subjects — must
                // NOT leak one subject's response to another.
                do!
                    store.Store(
                        "shared-uuid",
                        "subject:alice|M",
                        mkResponse "alice-secret" 200,
                        TimeSpan.FromMinutes 5.0
                    )

                let! got = store.TryGet("shared-uuid", "subject:bob|M")
                Expect.isNone got "subject:bob must not see subject:alice's memoised response"
            })

        testCaseAsync
            "an expired entry reads as a miss (TTL precision)"
            (async {
                let store = factory ()
                // Already-expired TTL — the next read must not replay it.
                do! store.Store("key-ttl", "subject:a|M", mkResponse "stale" 200, TimeSpan.FromMilliseconds -1.0)
                let! got = store.TryGet("key-ttl", "subject:a|M")
                Expect.isNone got "an entry past its TTL is not replayed"
            })
    ]
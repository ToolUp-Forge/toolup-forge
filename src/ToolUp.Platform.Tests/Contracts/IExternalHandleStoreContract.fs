// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IExternalHandleStoreContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 324.D — IExternalHandleStore conformance pack ─────────────
//
// **This pack was not written from nothing.** Phase 320 already shipped a
// parameterised store contract, inline in
// `InProcess/ExternalCallbackTests.fs`, already bound against BOTH shipped
// implementations (`InMemoryExternalHandleStore` +
// `BlobExternalHandleStore`). Duplicating it here would have produced two
// bars that drift. So 324.D **moves and generalises** it, and the moves are
// the phase's actual content:
//
//   1. **It lives in `Contracts/` now**, so a companion store — a Redis
//      one, a Postgres one, a cloud-table one — binds `contractFor`
//      unmodified rather than copying assertions out of another phase's
//      in-process pack. That is the whole 9c discipline: the bar is a
//      library, not a precedent to imitate.
//   2. **Scope partitioning became a law every implementation must
//      honour.** Phase 320 asserted it once, against
//      `BlobExternalHandleStore` only, as a forged-partition test that
//      reaches into blob names — untranslatable to any other backend. The
//      backend-independent property underneath it (a record resolves under
//      the scope it was registered with, and a claim in one scope moves
//      nothing in another) is `lawScopePartitioned` below, and it now runs
//      against every binding. The blob-specific forgery test stays where
//      it is, because it tests that store's pointer indirection rather
//      than the seam.
//   3. **The callback-vs-poll race is asserted at the STORE level.** Phase
//      320 proved single-resolution end-to-end through the scheduler with a
//      placed interleave — the right test, and unavailable to a companion
//      store author, who has no scheduler to rendezvous with. `lawCallbackVsPoll`
//      is the same claim reduced to the gate itself, so a companion can run
//      it in isolation before wiring anything.
//   4. **Two clauses the 320 pack left implicit are now pinned** —
//      `Resolve` is non-destructive (a poll loop calls it repeatedly), and
//      `Register` overwrites rather than rejects a repeat handle id, which
//      is what the interface documents and therefore what a companion must
//      not "improve" on.
//   5. **`selfTests` proves the pack has teeth**, on the
//      `ComponentRegistryContract` precedent: four deliberately
//      non-conformant stores, each of which the pack MUST fail. A
//      conformance pack that has never been shown to reject anything is a
//      list of things that happened to be true.
//
// **Every law is a plain function that raises**, not a `testCase`, for the
// same reason the component-registry pack does it: `selfTests` can then run
// the identical code against a broken store and require the throw. A law
// asserted only inside a test body cannot be turned on a control.
//
// **Synchronous by `Async.RunSynchronously`**, matching the Phase 320 pack
// this replaces. The seam is `Async`-returning at every member (GP 12 rule
// 2) and the laws honour that; running each to completion inline is what
// lets a law be a `unit`-returning function `Expect.throws` can drive.

/// A fresh handle in `scope`, minted by `backend`. Public so a companion's
/// own bindings build handles the same way the pack does.
let handleFor (scope: string) (backend: string) : ExternalHandle = {
    HandleId = Guid.NewGuid()
    Backend = backend
    ScopeId = scope
    NativeRef = Guid.NewGuid().ToString "N"
    SubmittedAt = DateTime.UtcNow
}

let private run (work: Async<'T>) = Async.RunSynchronously work

// ── the laws (each raises on violation) ───────────────────────────────

/// Register then resolve returns the handle, the run id, and the secret
/// **hash** — never the cleartext.
let lawRegisterResolveRoundTrip (store: IExternalHandleStore) : unit =
    let handle = handleFor "team-alpha" "gpu-pool"
    let runId = Guid.NewGuid()
    let secret, hash = ExternalCallbackSecret.mint ()

    store.Register(handle, runId, hash) |> run

    match store.Resolve handle.HandleId |> run with
    | None -> failtest "the registered handle did not resolve"
    | Some record ->
        Expect.equal record.Handle.HandleId handle.HandleId "handle id"
        Expect.equal record.Handle.ScopeId handle.ScopeId "scope rides the handle"
        Expect.equal record.Handle.Backend handle.Backend "backend label"
        Expect.equal record.Handle.NativeRef handle.NativeRef "the backend's opaque token is echoed verbatim"
        Expect.equal record.JobRunId runId "job run id — the routing answer a callback needs"
        Expect.isFalse record.Terminal "a fresh registration is not terminal"
        Expect.isNone record.TerminalAt "and carries no terminal timestamp"

        // Asserted as "the stored value IS the hash of the secret and is
        // NOT the secret". A store that persisted the cleartext would pass
        // a "the field is populated" check.
        Expect.equal record.CallbackSecretHash (ExternalCallbackSecret.hash secret) "the stored value is the hash"
        Expect.notEqual record.CallbackSecretHash secret "the cleartext secret is NOT stored"

/// An unknown handle resolves to `None` — never a fabricated record.
let lawUnknownResolvesToNone (store: IExternalHandleStore) : unit =
    Expect.isNone (store.Resolve(Guid.NewGuid()) |> run) "an unregistered handle id resolves to None"

/// `Resolve` is non-destructive: the reconciliation poll calls it on every
/// tick, so a store that consumed the record on read would resolve a run
/// once and then report the handle unknown forever.
let lawResolveIsNonDestructive (store: IExternalHandleStore) : unit =
    let handle = handleFor "team-repeat" "gpu-pool"
    let _, hash = ExternalCallbackSecret.mint ()
    store.Register(handle, Guid.NewGuid(), hash) |> run

    let reads = List.init 5 (fun _ -> store.Resolve handle.HandleId |> run)

    Expect.isTrue (reads |> List.forall Option.isSome) "five consecutive resolves all return the record"

    Expect.equal
        (reads |> List.map (Option.map _.JobRunId) |> List.distinct |> List.length)
        1
        "and every read returns the same record — Resolve is a read, not a take"

/// `MarkTerminal` is a compare-and-set: the first caller wins, every
/// subsequent caller is refused, and the record records the claim.
let lawMarkTerminalExactlyOnce (store: IExternalHandleStore) : unit =
    let handle = handleFor "team-beta" "gpu-pool"
    let _, hash = ExternalCallbackSecret.mint ()
    store.Register(handle, Guid.NewGuid(), hash) |> run

    Expect.isTrue (store.MarkTerminal handle.HandleId |> run) "the first claim wins"
    Expect.isFalse (store.MarkTerminal handle.HandleId |> run) "the second claim is refused"
    Expect.isFalse (store.MarkTerminal handle.HandleId |> run) "and stays refused"

    match store.Resolve handle.HandleId |> run with
    | Some record ->
        Expect.isTrue record.Terminal "the record records the claim"
        Expect.isSome record.TerminalAt "and when it was won"
    | None -> failtest "the record vanished when it was claimed"

/// `MarkTerminal` on an unknown handle is `false` — "nothing to claim" is
/// not "somebody claimed it".
///
/// Load-bearing rather than a formality: the scheduler reads a `false` and
/// then asks `Resolve` whether the handle exists at all, because an
/// unregistered handle must fall back to the ungated path rather than
/// leave its run awaiting forever. A store answering `true` here breaks
/// the other side of that decision.
let lawMarkTerminalUnknownIsFalse (store: IExternalHandleStore) : unit =
    Expect.isFalse (store.MarkTerminal(Guid.NewGuid()) |> run) "an unknown handle cannot be claimed"

/// Thirty-two concurrent claimants produce exactly one winner. The CAS
/// under load, which is the only thing that distinguishes a conforming
/// implementation from a read-then-write one that passes every sequential
/// case above.
let lawConcurrentMarkTerminalSingleWinner (store: IExternalHandleStore) : unit =
    let handle = handleFor "team-gamma" "gpu-pool"
    let _, hash = ExternalCallbackSecret.mint ()
    store.Register(handle, Guid.NewGuid(), hash) |> run

    let winners =
        Array.init 32 (fun _ -> store.MarkTerminal handle.HandleId)
        |> Async.Parallel
        |> run
        |> Array.filter id
        |> Array.length

    Expect.equal winners 1 "exactly one of 32 concurrent claimants wins the terminal claim"

/// The callback-vs-poll race, at the gate rather than through the
/// scheduler: the two real claimants — a completion callback on a request
/// thread and the reconciliation poll on a scheduler tick — race for one
/// handle, and exactly one resolves it.
///
/// Twenty rounds, each with a freshly registered handle, so the assertion
/// is deterministic (exactly one winner per round) while the interleave
/// varies. This is the store-level reduction of Phase 320.D's end-to-end
/// placed interleave — available to a companion store author who has no
/// scheduler to rendezvous with.
let lawCallbackVsPoll (store: IExternalHandleStore) : unit =
    for round in 1..20 do
        let handle = handleFor (sprintf "team-race-%d" round) "gpu-pool"
        let _, hash = ExternalCallbackSecret.mint ()
        store.Register(handle, Guid.NewGuid(), hash) |> run

        let callback = store.MarkTerminal handle.HandleId
        let reconciliationPoll = store.MarkTerminal handle.HandleId

        let winners =
            [| callback; reconciliationPoll |]
            |> Async.Parallel
            |> run
            |> Array.filter id
            |> Array.length

        Expect.equal
            winners
            1
            (sprintf
                "round %d: the callback and the reconciliation poll must not BOTH resolve one unit of work — losing this race means two terminal rows, two JobCompleted events, and anything downstream that bills or ships on completion doing it twice"
                round)

        match store.Resolve handle.HandleId |> run with
        | Some record ->
            Expect.isTrue record.Terminal (sprintf "round %d: the winner's claim is recorded" round)
            Expect.isSome record.TerminalAt (sprintf "round %d: with the instant it was won" round)
        | None -> failtest (sprintf "round %d: the record vanished" round)

/// GP 4 — the store is scope-partitioned. Three properties, none of which
/// mention how any backend lays its records out:
///
///   * a record resolves carrying the scope it was registered with, never
///     another scope's;
///   * a terminal claim in one scope moves nothing in any other scope;
///   * two handles that differ only by scope are two records, not one.
///
/// This is the backend-independent core of the Phase 320 forged-partition
/// test, which could only ever run against the blob store because it wrote
/// blob names directly.
let lawScopePartitioned (store: IExternalHandleStore) : unit =
    let scopes = [ "team-one"; "team-two"; "team-three" ]

    let registered =
        scopes
        |> List.map (fun scope ->
            let handle = handleFor scope "gpu-pool"
            let runId = Guid.NewGuid()
            let _, hash = ExternalCallbackSecret.mint ()
            store.Register(handle, runId, hash) |> run
            scope, handle, runId)

    // Each record comes back under its OWN scope and its OWN run id.
    for scope, handle, runId in registered do
        match store.Resolve handle.HandleId |> run with
        | None -> failtest (sprintf "the handle registered under %s did not resolve" scope)
        | Some record ->
            Expect.equal
                record.Handle.ScopeId
                scope
                (sprintf "the record resolves under the scope it was registered with, not another's (%s)" scope)

            Expect.equal record.JobRunId runId (sprintf "and against its own run (%s)" scope)

    // Claiming the FIRST scope's handle must move nothing in the others.
    match registered with
    | (_, first, _) :: rest ->
        Expect.isTrue (store.MarkTerminal first.HandleId |> run) "the first scope's handle is claimable"

        for scope, handle, _ in rest do
            match store.Resolve handle.HandleId |> run with
            | None -> failtest (sprintf "%s's record vanished when another scope's handle was claimed" scope)
            | Some record ->
                Expect.isFalse
                    record.Terminal
                    (sprintf
                        "%s's handle is untouched by a claim in another scope — the gate is per handle, per scope"
                        scope)

            Expect.isTrue
                (store.MarkTerminal handle.HandleId |> run)
                (sprintf "and %s's handle is still independently claimable" scope)
    | [] -> failtest "precondition: at least one scope was registered"

/// `Register` **overwrites** a repeat handle id rather than rejecting it —
/// re-registering is one hand-off being re-recorded, not a second unit of
/// work — which means it also clears the terminal claim.
///
/// Pinned because it is the clause a companion author is most likely to
/// "improve" on by rejecting the second call, and because the reset is
/// observable: a re-registered handle is claimable again.
let lawReRegistrationOverwrites (store: IExternalHandleStore) : unit =
    let handle = handleFor "team-rereg" "gpu-pool"
    let firstRun = Guid.NewGuid()
    let _, firstHash = ExternalCallbackSecret.mint ()
    store.Register(handle, firstRun, firstHash) |> run
    Expect.isTrue (store.MarkTerminal handle.HandleId |> run) "the first registration is claimable"

    let secondRun = Guid.NewGuid()
    let _, secondHash = ExternalCallbackSecret.mint ()
    store.Register(handle, secondRun, secondHash) |> run

    match store.Resolve handle.HandleId |> run with
    | None -> failtest "a re-registered handle must resolve, not be rejected into nothing"
    | Some record ->
        Expect.equal record.JobRunId secondRun "the re-registration's run id replaces the first"
        Expect.equal record.CallbackSecretHash secondHash "and its secret hash"
        Expect.isFalse record.Terminal "the overwrite clears the terminal claim — Register writes a fresh record"
        Expect.isNone record.TerminalAt "including its timestamp"

    Expect.isTrue (store.MarkTerminal handle.HandleId |> run) "so the re-registered hand-off is claimable again"

/// `IsDistributed` is a **declaration**, not an inference: it describes how
/// the store was configured, so it answers identically before and after the
/// store has been used (GP 12 rule 4). The value itself differs per
/// implementation and each binding pins its own; what the law asserts is
/// that reading it is not a function of runtime state.
let lawIsDistributedIsDeclared (store: IExternalHandleStore) : unit =
    let beforeAnyUse = store.IsDistributed

    let handle = handleFor "team-declared" "gpu-pool"
    let _, hash = ExternalCallbackSecret.mint ()
    store.Register(handle, Guid.NewGuid(), hash) |> run
    store.MarkTerminal handle.HandleId |> run |> ignore

    Expect.equal
        store.IsDistributed
        beforeAnyUse
        "IsDistributed describes the composition, not the state — a store whose answer moves once it holds a claim cannot be reasoned about at compose time"

/// Every law, in the order a companion author is best served reading them:
/// the round-trip that catches a broken backend before anything subtle
/// runs, then the gate, then the concurrency, then isolation.
let laws: (string * (IExternalHandleStore -> unit)) list = [
    "register then resolve returns the handle, the run id and the secret HASH", lawRegisterResolveRoundTrip
    "an unknown handle resolves to None", lawUnknownResolvesToNone
    "Resolve is non-destructive — a poll loop reads it every tick", lawResolveIsNonDestructive
    "324.D — MarkTerminal returns true exactly once; every later caller is a no-op", lawMarkTerminalExactlyOnce
    "MarkTerminal on an unknown handle is false — 'nothing to claim' is not 'somebody claimed it'",
    lawMarkTerminalUnknownIsFalse
    "324.D — 32 concurrent MarkTerminal calls produce exactly one winner", lawConcurrentMarkTerminalSingleWinner
    "324.D — the callback and the reconciliation poll resolve exactly once (20 rounds)", lawCallbackVsPoll
    "324.D / GP 4 — scope-partitioned: a claim in one scope moves nothing in another", lawScopePartitioned
    "Register overwrites a repeat handle id (and clears its terminal claim)", lawReRegistrationOverwrites
    "IsDistributed is declared as data", lawIsDistributedIsDeclared
]

/// Bind any `IExternalHandleStore` implementation against the full bar.
///
/// `make` is called **per law**, so one law's registrations cannot leak
/// into another's — which matters for a store backed by shared
/// infrastructure, where a fixed handle id would make two laws contend for
/// reasons that have nothing to do with the contract. (Handle ids are
/// GUIDs anyway; this is the belt.)
let contractFor (name: string) (make: unit -> IExternalHandleStore) =
    testList name [ for label, law in laws -> testCase label <| fun _ -> law (make ()) ]

// ── the shipped bindings ──────────────────────────────────────────────

let private blobStore () =
    BlobExternalHandleStore(InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage) :> IExternalHandleStore

/// Both shipped implementations, against the identical bar. Binding TWO is
/// the point rather than a bonus: an interface that only one implementation
/// has ever satisfied is a description of that implementation (GP 12), and
/// the pack cannot tell a portable clause from an accidental one until a
/// second backend with a genuinely different atomicity primitive
/// (`ConcurrentDictionary.TryUpdate` vs an ETag conditional write) has run
/// through it.
let tests =
    testList "IExternalHandleStore — routing + authentication + exactly-once gating (Phase 324.D)" [
        contractFor "InMemoryExternalHandleStore (ConcurrentDictionary CAS)" (fun () ->
            InMemoryExternalHandleStore() :> IExternalHandleStore)

        contractFor "BlobExternalHandleStore (ETag conditional-write CAS)" blobStore
    ]

// ── self-test: the pack fails a non-conforming store ──────────────────
//
// Four stores, each breaking exactly one clause, each of which the pack
// MUST reject. The failure mode this guards against is a pack whose
// assertions are all true of anything — and it is not hypothetical: the
// interesting clause here (`MarkTerminal` atomicity) is invisible to every
// sequential case, so a store that reads the flag and then writes it
// passes eight of the ten laws above.

/// The non-conformance the whole seam exists to refuse: `MarkTerminal`
/// implemented as read-then-write, with an `Async.Sleep` standing in for
/// the I/O latency a real store has between the two. Sequentially correct;
/// under concurrency, everybody wins.
type private ReadThenWriteHandleStore() =
    let records =
        System.Collections.Concurrent.ConcurrentDictionary<Guid, ExternalHandleRecord>()

    interface IExternalHandleStore with
        member _.IsDistributed = false

        member _.Register(handle, jobRunId, callbackSecretHash) = async {
            records[handle.HandleId] <- {
                Handle = handle
                JobRunId = jobRunId
                CallbackSecretHash = callbackSecretHash
                Terminal = false
                RegisteredAt = DateTime.UtcNow
                TerminalAt = None
            }
        }

        member _.Resolve handleId = async {
            match records.TryGetValue handleId with
            | true, record -> return Some record
            | false, _ -> return None
        }

        member _.MarkTerminal handleId = async {
            match records.TryGetValue handleId with
            | false, _ -> return false
            | true, current ->
                if current.Terminal then
                    return false
                else
                    // The race window, made wide enough to be reliable
                    // rather than left to chance.
                    do! Async.Sleep 5

                    records[handleId] <- {
                        current with
                            Terminal = true
                            TerminalAt = Some DateTime.UtcNow
                    }

                    return true
        }

/// A store whose `Register` does not persist — the shape a misconfigured
/// backend (wrong container, silently swallowed write error) presents.
type private AmnesiacHandleStore() =
    interface IExternalHandleStore with
        member _.IsDistributed = false
        member _.Register(_handle, _jobRunId, _hash) = async { return () }
        member _.Resolve _handleId = async { return None }
        member _.MarkTerminal _handleId = async { return false }

/// A store that claims every handle, known or not — the inverse error, and
/// the dangerous one: it makes the scheduler believe an unregistered
/// handle was resolved by somebody else, so the run is never driven at all.
type private GreedyHandleStore() =
    let inner = InMemoryExternalHandleStore() :> IExternalHandleStore

    interface IExternalHandleStore with
        member _.IsDistributed = false
        member _.Register(handle, jobRunId, hash) = inner.Register(handle, jobRunId, hash)
        member _.Resolve handleId = inner.Resolve handleId

        member _.MarkTerminal handleId = async {
            let! _ = inner.MarkTerminal handleId
            return true
        }

/// A store that ignores scope — one flat table keyed by handle id, where
/// claiming any handle claims them all. The GP 4 violation.
type private ScopeBlindHandleStore() =
    let records =
        System.Collections.Concurrent.ConcurrentDictionary<Guid, ExternalHandleRecord>()

    let mutable anyClaimed = false

    interface IExternalHandleStore with
        member _.IsDistributed = false

        member _.Register(handle, jobRunId, callbackSecretHash) = async {
            records[handle.HandleId] <- {
                Handle = handle
                JobRunId = jobRunId
                CallbackSecretHash = callbackSecretHash
                Terminal = false
                RegisteredAt = DateTime.UtcNow
                TerminalAt = None
            }
        }

        member _.Resolve handleId = async {
            match records.TryGetValue handleId with
            | true, record -> return Some { record with Terminal = anyClaimed }
            | false, _ -> return None
        }

        member _.MarkTerminal handleId = async {
            if anyClaimed || not (records.ContainsKey handleId) then
                return false
            else
                anyClaimed <- true
                return true
        }

let selfTests =
    testList "IExternalHandleStore contract — self-test (the pack has teeth)" [

        testCase "a read-then-write MarkTerminal fails the concurrency law"
        <| fun _ ->
            // The clause every sequential case misses. Asserted against
            // BOTH concurrency laws, because the 2-claimant callback/poll
            // form is the one a companion author will actually hit in
            // production and the 32-claimant form is the one that makes it
            // reliably visible in a test.
            Expect.throws
                (fun () -> lawConcurrentMarkTerminalSingleWinner (ReadThenWriteHandleStore()))
                "32 concurrent claimants against a read-then-write gate must produce more than one winner, and the pack must say so"

            Expect.throws
                (fun () -> lawCallbackVsPoll (ReadThenWriteHandleStore()))
                "and the callback-vs-poll law must reject it too — that is the interleave the seam exists for"

        testCase
            "a read-then-write MarkTerminal still PASSES every sequential law — which is why the pack needs the concurrent ones"
        <| fun _ ->
            // Not a formality: this is the evidence that the two
            // concurrency laws are load-bearing rather than decorative. A
            // pack without them would certify this store.
            let store = ReadThenWriteHandleStore()
            lawRegisterResolveRoundTrip store
            lawMarkTerminalExactlyOnce store
            lawMarkTerminalUnknownIsFalse store
            lawReRegistrationOverwrites store

        testCase "a store whose Register does not persist fails the round-trip law"
        <| fun _ ->
            Expect.throws
                (fun () -> lawRegisterResolveRoundTrip (AmnesiacHandleStore()))
                "a hand-off that was not durably recorded must fail the pack, not be reported as registered"

        testCase "a store that claims every handle fails the unknown-handle law"
        <| fun _ ->
            Expect.throws
                (fun () -> lawMarkTerminalUnknownIsFalse (GreedyHandleStore()))
                "'nothing to claim' answered as 'somebody claimed it' leaves a run awaiting forever"

        testCase "a scope-blind store fails the GP 4 partition law"
        <| fun _ ->
            Expect.throws
                (fun () -> lawScopePartitioned (ScopeBlindHandleStore()))
                "one flat gate across every tenant must fail the pack"

        testCase "a scope-blind store still passes the single-scope laws — the partition law is what catches it"
        <| fun _ ->
            let store = ScopeBlindHandleStore()
            lawRegisterResolveRoundTrip store
            lawMarkTerminalUnknownIsFalse store
    ]
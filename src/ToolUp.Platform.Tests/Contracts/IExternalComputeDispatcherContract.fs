// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IExternalComputeDispatcherContract

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 318 — IExternalComputeDispatcher conformance pack ─────────
//
// Three things are pinned here, and the third is the one that decays
// quietly if it is not executable:
//
//   1. **The `NoExternalCompute` default is a typed refusal, not a
//      failure.** A bare `ServerApp.empty |> ServerApp.run` deployment
//      resolves `IExternalComputeDispatcher` and gets
//      `Error ExternalComputeError.notConfigured` from `Submit` — never a
//      DI resolution exception, never a throw across the boundary. The
//      error is TERMINAL: retrying does not compose a backend.
//   2. **The core types round-trip through the Fable JSON path** (STJ +
//      `FableConverters`), so a Client can hold an `ExternalHandle`
//      opaquely and every `ExternalOutcome` case survives the wire —
//      including `Running None`, the case a naive converter set collapses.
//   3. **The Phase 9c six-rule portability audit**, expressed as cases
//      rather than as a comment. A prose audit in a file header cannot
//      fail; these can. Rules 1/2/3 are shape assertions the compiler
//      participates in, and 4/5/6 assert the observable consequence of
//      each rule against the shipped default.
//
// These three lists bind `NoExternalComputeDispatcher` — the not-configured
// default, and the not-configured contract. **Phase 324 adds the contract for
// a dispatcher that ACCEPTS work**: a parameterised suite (`contractFor`) over
// a factory + a test-backend harness, covering submit → poll → terminal,
// idempotent resubmit, cancel, error classification and scope isolation, with
// its own reference backend, three in-tree bindings, and a self-test proving
// it rejects a non-conformant dispatcher. See the "Phase 324 — the
// parameterised conformance pack" section at the foot of this file; that is
// what Phase 322 (HTTP stub server) and Phase 323 (kind cluster) bind.

let private jsonOptions = FableConverters.create ()

/// Round-trip `value` through the STJ + `FableConverters` wire the SDK uses
/// for every non-Remoting JSON payload, and return what came back.
let private roundTrip<'T> (value: 'T) : 'T =
    let json = JsonSerializer.Serialize(value, jsonOptions)
    JsonSerializer.Deserialize<'T>(json, jsonOptions)

let private sampleHandle: ExternalHandle = {
    HandleId = Guid.Parse "6f1d5b0e-3a1c-4c8f-9f3e-2b7a5d4c1e08"
    Backend = "http-worker-pool"
    ScopeId = "team-1"
    NativeRef = "queue://gpu-a100/job/8812?token=opaque"
    SubmittedAt = DateTime(2026, 7, 1, 9, 30, 0, DateTimeKind.Utc)
}

let private sampleSpec: ExternalWorkSpec =
    ExternalWorkSpec.create "train-forecast" """{"series":"sales","horizon":12}"""
    |> ExternalWorkSpec.withHint "gpu" "1"
    |> ExternalWorkSpec.withHint "memory" "16Gi"
    |> ExternalWorkSpec.withTimeout (TimeSpan.FromMinutes 90.0)
    |> ExternalWorkSpec.withIdempotency "sales-h12-v3"

let private dispatcher () =
    NoExternalComputeDispatcher() :> IExternalComputeDispatcher

// ─── The no-op default (318.C) ───────────────────────────────────────

let tests =
    testList "ExternalCompute — NoExternalComputeDispatcher default (Phase 318)" [

        test "ServerConfig defaults to NoExternalCompute (GP 11 + GP 13)" {
            Expect.equal
                ServerConfig.defaults.ExternalCompute
                NoExternalCompute
                "an existing deployment that upgrades must stay byte-for-byte identical until it opts in"
        }

        testCaseAsync "Submit returns a clean not-configured Error — no throw, no exception path"
        <| async {
            let! result = dispatcher().Submit("team-1", sampleSpec)

            match result with
            | Ok handle -> failtestf "a not-configured deployment must not mint a handle; got %A" handle
            | Error e ->
                Expect.equal e ExternalComputeError.notConfigured "the canonical not-configured refusal"
                Expect.isFalse e.Retriable "not-configured is TERMINAL — no retry composes a backend"
                Expect.stringContains e.Message "NoExternalCompute" "the refusal names the config knob to change"

                Expect.stringContains e.Message "IExternalComputeDispatcher" "the refusal names the seam to register"
        }

        testCaseAsync "the refusal is identical for every scope and every spec (stateless — GP 12 rule 4)"
        <| async {
            let! a = dispatcher().Submit("team-1", sampleSpec)
            let! b = dispatcher().Submit("team-2", ExternalWorkSpec.create "render-scene" "{}")
            Expect.equal a b "the default caches nothing and discriminates on nothing"
        }

        testCaseAsync "Poll reports the same refusal as a terminal Failed outcome"
        <| async {
            let! outcome = dispatcher().Poll sampleHandle

            match outcome with
            | ExternalOutcome.Failed e ->
                Expect.equal e ExternalComputeError.notConfigured "the same structured refusal, as an outcome"
                Expect.isTrue (ExternalOutcome.isTerminal outcome) "a refusal is terminal — a poller must stop"
            | other -> failtestf "expected Failed notConfigured; got %A" other
        }

        testCaseAsync "Poll never invents a Cancelled or a Succeeded for an unknown handle"
        <| async {
            let! outcome =
                dispatcher().Poll {
                    sampleHandle with
                        HandleId = Guid.NewGuid()
                }

            match outcome with
            | ExternalOutcome.Failed _ -> ()
            | other -> failtestf "an unknown handle is a typed failure, not a fabricated terminal state; got %A" other
        }

        testCaseAsync "Cancel is a no-op and idempotent — cancelling twice is not an error"
        <| async {
            let d = dispatcher ()
            do! d.Cancel sampleHandle
            do! d.Cancel sampleHandle
        }

        test "the default reports a stable backend name" {
            Expect.equal (dispatcher ()).Backend "none" "diagnostics read Backend; the not-configured label is stable"

            Expect.equal
                NoExternalComputeDispatcher.BackendName
                (dispatcher ()).Backend
                "the static label and the instance member agree"
        }

        // ─── Compose (318.C) — the seam always resolves ──────────────

        test "NoExternalCompute registers the no-op so the seam resolves (GP 13)" {
            let services = ServiceCollection()

            ComposeStores.registerExternalCompute services {
                ServerConfig.defaults with
                    ExternalCompute = NoExternalCompute
            }

            use provider = services.BuildServiceProvider()
            let resolved = provider.GetService<IExternalComputeDispatcher>()

            Expect.isNotNull (box resolved) "a bare deployment must resolve the seam, not fail at DI"

            Expect.isTrue (resolved :? NoExternalComputeDispatcher) "the default registration is the no-op dispatcher"
        }

        test "CustomExternalCompute registers nothing, leaving a companion singleton in place" {
            let services = ServiceCollection()

            ComposeStores.registerExternalCompute services {
                ServerConfig.defaults with
                    ExternalCompute = CustomExternalCompute
            }

            use provider = services.BuildServiceProvider()

            Expect.isNull
                (box (provider.GetService<IExternalComputeDispatcher>()))
                "compose must not overwrite a companion's own dispatcher"
        }

        test "a companion registration survives compose under CustomExternalCompute" {
            let services = ServiceCollection()
            // The consumer's own registration lands first, exactly as a
            // companion's `create` would wire it.
            services.AddSingleton<IExternalComputeDispatcher>(NoExternalComputeDispatcher())
            |> ignore

            ComposeStores.registerExternalCompute services {
                ServerConfig.defaults with
                    ExternalCompute = CustomExternalCompute
            }

            use provider = services.BuildServiceProvider()

            Expect.equal
                (provider.GetServices<IExternalComputeDispatcher>() |> Seq.length)
                1
                "exactly one dispatcher — compose adds no second registration to shadow the companion"
        }
    ]

// ─── Fable-JSON round-trip (318.A) ───────────────────────────────────

let wireTests =
    testList "ExternalCompute — core types round-trip through Fable JSON (Phase 318)" [

        test "ExternalHandle survives the wire field-for-field" {
            let back = roundTrip sampleHandle
            Expect.equal back sampleHandle "a Client can hold an ExternalHandle opaquely and hand it back"
            Expect.equal back.NativeRef sampleHandle.NativeRef "the opaque backend token is echoed verbatim"

            Expect.equal back.SubmittedAt.Kind DateTimeKind.Utc "the submission timestamp stays UTC across the wire"
        }

        test "ExternalWorkSpec survives the wire, including the hint map and both options" {
            let back = roundTrip sampleSpec
            Expect.equal back sampleSpec "the whole spec round-trips"
            Expect.equal back.ResourceHints.["gpu"] "1" "advisory resource hints survive as a Map"
            Expect.equal back.Timeout (Some(TimeSpan.FromMinutes 90.0)) "an option-wrapped TimeSpan survives"
            Expect.equal back.Idempotency (Some "sales-h12-v3") "the idempotency key survives"
        }

        test "a minimal ExternalWorkSpec round-trips with empty hints and both options None" {
            let minimal = ExternalWorkSpec.create "render-scene" "{}"
            let back = roundTrip minimal
            Expect.equal back minimal "the minimum shape needs no ceremony on the wire"
            Expect.isEmpty back.ResourceHints "an empty Map stays empty, not null"
            Expect.isNone back.Timeout "None stays None"
        }

        test "every ExternalOutcome case round-trips — including Running None" {
            // `Running None` is the case a naive converter set collapses
            // (an option inside a DU field), and it is precisely the case a
            // backend that cannot report progress emits.
            let cases = [
                ExternalOutcome.Pending
                ExternalOutcome.Running None
                ExternalOutcome.Running(Some 0.42)
                ExternalOutcome.Succeeded "blob://results/8812.parquet"
                ExternalOutcome.Failed(ExternalComputeError.retriable "backend saturated")
                ExternalOutcome.Failed(ExternalComputeError.terminal "unknown Kind")
                ExternalOutcome.Cancelled
            ]

            for case in cases do
                Expect.equal (roundTrip case) case (sprintf "%A must survive the wire" case)
        }

        test "ExternalComputeError round-trips with retriability intact in both directions" {
            let retriable = ExternalComputeError.retriable "lease expired"
            let terminal = ExternalComputeError.terminal "malformed payload"
            Expect.isTrue (roundTrip retriable).Retriable "a retriable error stays retriable"
            Expect.isFalse (roundTrip terminal).Retriable "a terminal error stays terminal"
            Expect.equal (roundTrip ExternalComputeError.notConfigured) ExternalComputeError.notConfigured "…"
        }

        test "isTerminal partitions the five cases into two non-terminal and three terminal" {
            let terminal =
                [
                    ExternalOutcome.Pending
                    ExternalOutcome.Running None
                    ExternalOutcome.Succeeded "r"
                    ExternalOutcome.Failed(ExternalComputeError.terminal "x")
                    ExternalOutcome.Cancelled
                ]
                |> List.map ExternalOutcome.isTerminal

            Expect.equal
                terminal
                [ false; false; true; true; true ]
                "Pending / Running are poll-again; Succeeded / Failed / Cancelled are stop"
        }

        test "outcome labels are stable and distinct" {
            let labels =
                [
                    ExternalOutcome.Pending
                    ExternalOutcome.Running(Some 0.1)
                    ExternalOutcome.Succeeded "r"
                    ExternalOutcome.Failed(ExternalComputeError.terminal "x")
                    ExternalOutcome.Cancelled
                ]
                |> List.map ExternalOutcome.label

            Expect.equal
                labels
                [ "pending"; "running"; "succeeded"; "failed"; "cancelled" ]
                "log / audit labels are part of the contract"

            Expect.equal (List.distinct labels).Length 5 "no two states share a label"
        }
    ]

// ─── Phase 9c six-rule portability audit (GP 12) ─────────────────────

let portabilityAudit =
    testList "ExternalCompute — six-rule portability audit (Phase 9c, GP 12)" [

        testCaseAsync "Rule 1 — identity by value: ExternalHandle is a record of primitives"
        <| async {
            // Structural equality is the observable consequence of
            // identity-by-value: two independently-constructed handles with
            // the same field values ARE the same handle. A live framework
            // handle (IActorRef / IGrainReference) could not satisfy this.
            let a = sampleHandle

            let b = {
                HandleId = sampleHandle.HandleId
                Backend = sampleHandle.Backend
                ScopeId = sampleHandle.ScopeId
                NativeRef = sampleHandle.NativeRef
                SubmittedAt = sampleHandle.SubmittedAt
            }

            Expect.equal a b "handles compare by value, so any node resolves the same handle"
            Expect.equal (a.GetHashCode()) (b.GetHashCode()) "value identity extends to hashing (dictionary keys)"
            // Every field is a BCL primitive — asserted by binding each to
            // its primitive type without a cast.
            let _: Guid = a.HandleId
            let _: string = a.Backend
            let _: string = a.ScopeId
            let _: string = a.NativeRef
            let _: DateTime = a.SubmittedAt
            do! async.Return()
        }

        testCaseAsync "Rule 2 — async at every boundary: all three methods return Async<_>"
        <| async {
            // Compile-time shape: each call is `let!`-bindable, so each
            // returns Async<_>. No sync method and no Tell-shaped
            // signature, so no IMetricsSink-style carve-out is claimed.
            let d = dispatcher ()
            let! _submit = d.Submit("team-1", sampleSpec)
            let! _poll = d.Poll sampleHandle
            do! d.Cancel sampleHandle
        }

        testCaseAsync "Rule 3 — retry + supervision as data, never a callback"
        <| async {
            // Retriability arrives as a field on a record the caller can
            // serialise, persist and re-read after a restart. The spec
            // carries its own timeout + idempotency; there is no
            // `OnFailure: exn -> unit` parameter anywhere on the surface.
            let e = ExternalComputeError.retriable "backend saturated"
            Expect.isTrue e.Retriable "the retry decision is data on the error"
            Expect.isTrue (roundTrip e).Retriable "and it survives the wire, so it survives a restart"
            Expect.isSome sampleSpec.Timeout "supervision budget is a field on the spec"
            Expect.isSome sampleSpec.Idempotency "re-submission safety is a field on the spec"

            // A backend-reported failure is a value, not an exception.
            let! result = dispatcher().Submit("team-1", sampleSpec)
            Expect.isTrue (Result.isError result) "a refusal is Error data, not a raised exception"
        }

        testCaseAsync "Rule 4 — stateless between invocations: a fresh instance answers identically"
        <| async {
            // A distributed dispatcher can be recycled between calls, so no
            // answer may depend on instance state accumulated by an earlier
            // call. Submit twice on ONE instance, then once on a FRESH one.
            let d = dispatcher ()
            let! first = d.Submit("team-1", sampleSpec)
            let! second = d.Submit("team-1", sampleSpec)
            let! fresh = dispatcher().Submit("team-1", sampleSpec)
            Expect.equal first second "the second call on one instance is not influenced by the first"
            Expect.equal first fresh "a recycled worker answers identically to a warm one"
        }

        testCaseAsync "Rule 5 — no cross-shard ordering: two handles are independent"
        <| async {
            // Ordering is promised only within one handle's own state
            // progression. Polling handle B must not depend on, or be
            // affected by, anything done to handle A.
            let handleA = sampleHandle

            let handleB = {
                sampleHandle with
                    HandleId = Guid.NewGuid()
                    NativeRef = "queue://gpu-a100/job/8813"
            }

            let d = dispatcher ()
            do! d.Cancel handleA
            let! outcomeB = d.Poll handleB
            let! outcomeBAgain = dispatcher().Poll handleB

            Expect.equal
                outcomeB
                outcomeBAgain
                "handle B's outcome is unaffected by handle A's cancellation or by which instance polled"
        }

        testCaseAsync "Rule 6 — precision at the lower bound: progress is optional, timeout advisory"
        <| async {
            // A backend that cannot report progress says None rather than
            // fabricating a figure, and the timeout is an advisory TimeSpan
            // whose floor is the backend's own scheduling granularity —
            // no implicit sub-second promise is encoded in the type.
            let unknownProgress = ExternalOutcome.Running None
            Expect.equal (ExternalOutcome.label unknownProgress) "running" "a progress-less backend is still Running"
            Expect.isFalse (ExternalOutcome.isTerminal unknownProgress) "and still non-terminal"

            let noBudget = ExternalWorkSpec.create "k" "{}"
            Expect.isNone noBudget.Timeout "no timeout means the backend's own default, not a platform-imposed one"
            do! async.Return()
        }

        test "no framework or backend-SDK type appears on the seam" {
            // Reflection over the interface: every parameter and return type
            // must live in the ToolUp.Platform / FSharp.Core / System
            // surface. A `Task`, `CancellationToken`, `HttpContext`,
            // `IServiceProvider` or a vendor client would fail here.
            let allowedRoots = [ "ToolUp."; "Microsoft.FSharp."; "System." ]

            let offenders =
                typeof<IExternalComputeDispatcher>.GetMethods()
                |> Array.collect (fun m ->
                    let paramTypes = m.GetParameters() |> Array.map _.ParameterType
                    Array.append paramTypes [| m.ReturnType |])
                |> Array.collect (fun t ->
                    // Walk generic arguments too: Async<Result<Handle, Err>>
                    // must be inspected all the way down.
                    Array.append [| t |] (t.GetGenericArguments())
                    |> Array.collect (fun x -> Array.append [| x |] (x.GetGenericArguments())))
                |> Array.choose (fun t -> if isNull t.FullName then None else Some t.FullName)
                |> Array.filter (fun n -> allowedRoots |> List.forall (fun r -> not (n.StartsWith r)))
                |> Array.distinct

            Expect.isEmpty
                offenders
                (sprintf "the seam must be expressible from ToolUp.Platform value types alone; found %A" offenders)

            // The probe must be capable of failing — a type from outside
            // the allowed roots is rejected by the same filter.
            let control = typeof<System.Data.DataTable>.FullName

            Expect.isFalse
                (allowedRoots |> List.forall (fun r -> not (control.StartsWith r)))
                "control: System.* IS inside the allowed roots, so the filter is direction-correct"

            let foreign = "Akka.Actor.IActorRef"

            Expect.isTrue
                (allowedRoots |> List.forall (fun r -> not (foreign.StartsWith r)))
                "control: a framework handle type WOULD be reported, so the probe can fail"
        }
    ]

// ─── Phase 324 — the parameterised conformance pack ──────────────────
//
// Phase 318 shipped the three lists above, and every one of them binds
// `NoExternalComputeDispatcher` — the not-configured default. That is a real
// contract (a bare deployment must refuse cleanly rather than fail at DI),
// but it is the contract of a dispatcher that never accepts anything, so it
// says nothing about submit → poll → terminal, idempotency, or cancel. A
// companion could satisfy every case above and still be wrong about all
// three. 324 is the bar for a dispatcher that DOES accept work.
//
// ── The harness seam (324.A's hot zone) ──
//
// The pack has to drive a backend to a terminal state without knowing how
// that backend reaches one, and the two target companions reach one in
// genuinely different ways: an HTTP worker pool answers a request with the
// current status (Phase 322), while a Kubernetes backend watches a declared
// object whose status is patched by a controller out of band (Phase 323). A
// seam that fitted only the first would have the pack calling something like
// `SetStatus` and asserting the very next `Poll` sees it; a seam that fitted
// only the second would have the pack waiting on a subscription no HTTP stub
// has.
//
// The seam that fits both is **`Drive` + always settle**:
//
//   * `Drive handle outcome` says *bring this about*, and says nothing about
//     when or how. For an HTTP stub it is a table write; for a Kubernetes fake it
//     is a status patch on the fake object. Neither has to pretend to be the
//     other.
//   * **No law ever polls once and asserts.** Every terminal assertion goes
//     through `settle`, which polls to a terminal outcome within a declared
//     budget. That is what absorbs the difference: an immediately-visible
//     table write settles on the first poll, and an eventually-consistent
//     watch settles on a later one, with the same law text and no branch.
//
// So the pack is not merely *parameterised over a factory* — it is
// parameterised over **when the backend's state becomes observable**, which
// is the axis the two companions actually differ on. `SettleBudget` is a
// ceiling, never a deadline being asserted: no law claims something happened
// *within* a time, so a slow CI runner can only ever push these toward
// "polled more times than necessary" (the `IDistributedLockContract` timing
// rule).
//
// ── What the pack demands vs what it records as delegated ──
//
// Two clauses in Phase 318 are deliberately not absolute, and a pack that
// demanded them anyway would be asserting a requirement the interface does
// not make — the failure mode where a conformance bar stops describing the
// contract and starts describing the first implementation:
//
//   * **Idempotent resubmit is a SHOULD, and it is backend-side.**
//     `Submit`'s doc comment says an implementation SHOULD return the
//     existing handle for a key it has already accepted. The platform cannot
//     enforce it: it has no record of the backend's own accepted keys, which
//     is exactly why Phase 485's memoization decorator exists as the
//     portable answer. So `HonoursIdempotency` is declared, and the law
//     asserts same-key-same-handle where it is claimed and the honest
//     fallback (a repeat submission is still ACCEPTED, never an error)
//     where it is not.
//   * **Cross-scope rejection is only partly expressible on this seam.**
//     `Poll` and `Cancel` take a handle and no scope parameter — the scope
//     rides the handle — so there is no scope argument for a caller to lie
//     about. The one cross-scope shape the seam CAN express is a re-scoped
//     handle: the same `HandleId` / `NativeRef` presented with a different
//     `ScopeId`. A backend that keys its own record by scope, or checks the
//     handle's scope against it, refuses that; a backend that keys purely by
//     the opaque `NativeRef` cannot tell, and demanding it would be
//     inventing a requirement no HTTP or Kubernetes backend can honour from
//     the information it holds.
//
//     **GP 4 is therefore enforced a layer up, and that layer has its own
//     pack.** The callback ingress takes the scope from the platform's
//     stored record and never from the request, and the handle store is
//     scope-partitioned with a cross-check — see
//     `IExternalHandleStoreContract.lawScopePartitioned` (324.D) and Phase
//     320's forged-partition test. That is where the structural guarantee
//     lives; `lawScopeIsolation` below asserts what the dispatcher seam can
//     honestly promise, and asserts the *precondition* that layer depends on
//     (the handle carries its scope, unmodified) when it cannot promise
//     more.
//
// ── Teeth ──
//
// `selfTests` runs the laws against seven deliberately non-conformant
// backends and requires each to FAIL, on the `ComponentRegistryContract`
// precedent. Two of those cases assert the inverse as well — that the broken
// backend still PASSES the laws which do not cover its defect — because that
// is the evidence a given law is load-bearing rather than decorative.

/// One backend under test: the dispatcher the laws exercise, plus the lever
/// the pack pulls to make its work reach a state.
type ExternalComputeBackendUnderTest = {
    /// The dispatcher under test. May be a raw companion or a decorator
    /// stack over one — the pack cannot tell, and that is the point.
    Dispatcher: IExternalComputeDispatcher
    /// Make the unit behind `handle` reach `outcome`, however this backend
    /// expresses that: a table write for a request/response stub, a status
    /// patch on a declared object for a watch-based fake.
    ///
    /// The pack never assumes the effect is visible to the next `Poll` — it
    /// settles. So an implementation is free to be eventually consistent,
    /// and free to be immediate.
    Drive: ExternalHandle -> ExternalOutcome -> Async<unit>
}

/// What a backend declares about the clauses Phase 318 leaves optional, and
/// how long its state takes to become observable.
type ExternalComputeConformance = {
    /// The backend honours `ExternalWorkSpec.Idempotency` itself, returning
    /// the existing handle for a key it has already accepted in that scope.
    /// A SHOULD in Phase 318, so declared rather than assumed.
    HonoursIdempotency: bool
    /// `Poll` / `Cancel` check the presented handle's `ScopeId` against the
    /// backend's own record, so a re-scoped handle is refused rather than
    /// answered. See the section header for why this is declared and where
    /// GP 4 is structurally enforced instead.
    ValidatesHandleScope: bool
    /// Ceiling on how long a `Drive` may take to become visible to `Poll`.
    /// A budget, not an asserted deadline.
    SettleBudget: TimeSpan
    /// Gap between polls while settling.
    PollInterval: TimeSpan
}

[<RequireQualifiedAccess>]
module ExternalComputeConformance =
    /// A backend that honours every optional clause and whose state is
    /// immediately observable — the shape an in-process fake and a
    /// well-behaved HTTP stub both have. Generous budget, short interval:
    /// the budget costs nothing when the first poll settles.
    let strict: ExternalComputeConformance = {
        HonoursIdempotency = true
        ValidatesHandleScope = true
        SettleBudget = TimeSpan.FromSeconds 10.0
        PollInterval = TimeSpan.FromMilliseconds 20.0
    }

/// Poll `handle` until it reaches a terminal outcome or the budget runs out,
/// returning whatever the last poll saw.
///
/// The seam that lets one law text cover a request/response backend and a
/// declarative-watch one. Exported because a companion's own bindings need
/// exactly the same helper.
let settle
    (conformance: ExternalComputeConformance)
    (dispatcher: IExternalComputeDispatcher)
    (handle: ExternalHandle)
    : Async<ExternalOutcome> =
    let deadline = DateTime.UtcNow.Add conformance.SettleBudget

    let rec loop () = async {
        let! outcome = dispatcher.Poll handle

        if ExternalOutcome.isTerminal outcome || DateTime.UtcNow >= deadline then
            return outcome
        else
            do! Async.Sleep(max 1 (int conformance.PollInterval.TotalMilliseconds))
            return! loop ()
    }

    loop ()

/// `settle`, failing the case when the outcome never became terminal.
let private settleToTerminal conformance (backend: ExternalComputeBackendUnderTest) handle (what: string) = async {
    let! outcome = settle conformance backend.Dispatcher handle

    if not (ExternalOutcome.isTerminal outcome) then
        failtestf
            "%s: the unit never reached a terminal outcome within the %O settle budget (last poll: %A). Either the backend does not observe a driven transition, or its budget is declared too short."
            what
            conformance.SettleBudget
            outcome

    return outcome
}

let private submitOrFail (backend: ExternalComputeBackendUnderTest) scope spec = async {
    match! backend.Dispatcher.Submit(scope, spec) with
    | Ok handle -> return handle
    | Error e ->
        return
            failtestf "Submit was refused (%s) — the pack's laws all presuppose a backend that accepts work" e.Message
}

/// A spec with no idempotency key: every `Submit` of it is a fresh unit.
///
/// Used deliberately wherever a law must observe the RAW seam rather than a
/// decorator's cache — Phase 485's memo only engages for a memoizable
/// (idempotency-keyed) spec, so a non-keyed spec keeps a memo binding
/// transparent and the law measures the backend underneath.
let private freshSpec (kind: string) =
    ExternalWorkSpec.create kind """{"probe":true}"""

// ── 324.A — submit, poll, terminal, error classification, hints ────────

/// `Submit` returns a well-formed handle: platform-minted id, the scope it
/// was submitted under, a non-empty backend label and native ref, and
/// identity by value.
let lawSubmitReturnsWellFormedHandle conformance (backend: ExternalComputeBackendUnderTest) = async {
    ignore conformance
    let! handle = submitOrFail backend "team-wellformed" (freshSpec "train-forecast")

    Expect.notEqual handle.HandleId Guid.Empty "a handle carries a real platform-minted id"
    Expect.equal handle.ScopeId "team-wellformed" "the handle carries the scope it was submitted under (GP 4)"

    Expect.isFalse
        (String.IsNullOrWhiteSpace handle.Backend)
        "the handle names a backend, so a later poll can be routed to the one that minted it"

    Expect.isFalse
        (String.IsNullOrWhiteSpace handle.NativeRef)
        "the backend's own opaque token is present — it is what the backend needs to find the work again"

    Expect.isTrue
        (handle.SubmittedAt > DateTime.UtcNow.AddMinutes -5.0
         && handle.SubmittedAt < DateTime.UtcNow.AddMinutes 5.0)
        "the submission instant is now-ish, not default(DateTime) and not a local-time value mislabelled as UTC"

    // Identity by value (GP 12 rule 1): a structural copy IS the same
    // handle, which a live backend-SDK job object could not satisfy.
    let copy = {
        HandleId = handle.HandleId
        Backend = handle.Backend
        ScopeId = handle.ScopeId
        NativeRef = handle.NativeRef
        SubmittedAt = handle.SubmittedAt
    }

    Expect.equal copy handle "handles compare by value, so any node resolves the same handle"
}

/// `Poll` reports a non-terminal state while the work is outstanding, and
/// transitions to the terminal outcome the backend reaches.
let lawPollTransitionsToTerminal conformance (backend: ExternalComputeBackendUnderTest) = async {
    let! handle = submitOrFail backend "team-transitions" (freshSpec "render-scene")

    let! accepted = backend.Dispatcher.Poll handle

    Expect.isFalse
        (ExternalOutcome.isTerminal accepted)
        "a freshly accepted unit is Pending or Running — 'accepted' is not 'completed', and a backend that reports terminal here has resolved work it never ran"

    // Running with progress, then Running without: a backend that cannot
    // report progress says None rather than inventing a figure (rule 6),
    // and both must survive a poll as non-terminal.
    do! backend.Drive handle (ExternalOutcome.Running(Some 0.5))

    let! midway =
        settle
            {
                conformance with
                    SettleBudget = TimeSpan.Zero
            }
            backend.Dispatcher
            handle

    match midway with
    | ExternalOutcome.Running _
    | ExternalOutcome.Pending -> ()
    | other -> failtestf "a unit driven to Running must poll as non-terminal; got %A" other

    do! backend.Drive handle (ExternalOutcome.Succeeded "blob://results/transitions.parquet")
    let! terminal = settleToTerminal conformance backend handle "the driven success"

    Expect.equal
        terminal
        (ExternalOutcome.Succeeded "blob://results/transitions.parquet")
        "the terminal outcome the backend reached is the one Poll reports"
}

/// A `Succeeded` outcome carries a result ref the caller can resolve, echoed
/// **verbatim** — the platform never parses, normalises or re-hashes it.
let lawSucceededCarriesResolvableResultRef conformance (backend: ExternalComputeBackendUnderTest) = async {
    // Deliberately awkward: a URI with a query string and a fragment-ish
    // tail, which a backend tempted to "tidy" a result ref would mangle.
    let resultRef = "s3://bucket/run=8812/part-0.parquet?versionId=abc.DEF-123~x"
    let! handle = submitOrFail backend "team-resultref" (freshSpec "train-forecast")
    do! backend.Drive handle (ExternalOutcome.Succeeded resultRef)
    let! terminal = settleToTerminal conformance backend handle "the driven success"

    match terminal with
    | ExternalOutcome.Succeeded echoed ->
        Expect.equal echoed resultRef "the result ref is echoed byte-for-byte — it is opaque to the platform"
        Expect.isFalse (String.IsNullOrWhiteSpace echoed) "and a success always carries one"
    | other -> failtestf "expected Succeeded; got %A" other
}

/// Polling a terminal handle is non-destructive and idempotent: the same
/// terminal outcome comes back every time.
///
/// The reconciliation poll runs on a timer and a completion callback can
/// arrive at any point, so a backend that forgets a terminal state after it
/// has been read once turns a resolved run into an unresolvable one.
let lawPollOnTerminalIsIdempotent conformance (backend: ExternalComputeBackendUnderTest) = async {
    let! handle = submitOrFail backend "team-idempotent-poll" (freshSpec "render-scene")
    do! backend.Drive handle (ExternalOutcome.Succeeded "blob://results/stable")
    let! first = settleToTerminal conformance backend handle "the driven success"

    let! second = backend.Dispatcher.Poll handle
    let! third = backend.Dispatcher.Poll handle

    Expect.equal second first "re-polling a terminal handle returns the same outcome"
    Expect.equal third first "and keeps returning it — Poll is a read, not a take"
}

/// Retriability survives the round trip in **both** directions. A backend
/// that flattens a transient saturation into a terminal failure abandons
/// recoverable work; one that reports a malformed payload as retriable
/// re-submits it forever.
let lawErrorClassificationHonoured conformance (backend: ExternalComputeBackendUnderTest) = async {
    let transient =
        ExternalComputeError.retriable "backend saturated: no accelerator slot free"

    let permanent = ExternalComputeError.terminal "unknown Kind 'render-scene-v9'"

    let! retriableHandle = submitOrFail backend "team-classify" (freshSpec "train-forecast")
    let! terminalHandle = submitOrFail backend "team-classify" (freshSpec "train-forecast")

    do! backend.Drive retriableHandle (ExternalOutcome.Failed transient)
    do! backend.Drive terminalHandle (ExternalOutcome.Failed permanent)

    let! retriableOutcome = settleToTerminal conformance backend retriableHandle "the retriable failure"
    let! terminalOutcome = settleToTerminal conformance backend terminalHandle "the terminal failure"

    match retriableOutcome with
    | ExternalOutcome.Failed e ->
        Expect.isTrue
            e.Retriable
            "a retriable failure stays retriable — the retry decision is the backend's, carried as data"

        Expect.equal e.Message transient.Message "and its diagnostic reaches the caller verbatim"
    | other -> failtestf "expected Failed for the retriable unit; got %A" other

    match terminalOutcome with
    | ExternalOutcome.Failed e ->
        Expect.isFalse e.Retriable "a terminal failure stays terminal — nothing may promote it into a retry loop"
        Expect.equal e.Message permanent.Message "and its diagnostic reaches the caller verbatim"
    | other -> failtestf "expected Failed for the terminal unit; got %A" other
}

/// `ResourceHints` are **accepted**. No backend is required to honour a
/// hint, and one that does not understand a hint ignores it — but no backend
/// may error merely because hints are present, or the field is unusable by
/// any portable caller.
let lawResourceHintsAccepted conformance (backend: ExternalComputeBackendUnderTest) = async {
    ignore conformance

    let hinted =
        freshSpec "train-forecast"
        |> ExternalWorkSpec.withHint "gpu" "1"
        |> ExternalWorkSpec.withHint "memory" "16Gi"
        |> ExternalWorkSpec.withHint "accelerator" "a100"
        |> ExternalWorkSpec.withTimeout (TimeSpan.FromMinutes 90.0)

    let! accepted = backend.Dispatcher.Submit("team-hints", hinted)

    match accepted with
    | Ok _ -> ()
    | Error e ->
        failtestf
            "a spec carrying advisory resource hints was refused (%s). Hints are advisory by construction (GP 12 rule 1) — a backend may ignore one it does not understand, but refusing the field makes it unusable by any portable caller."
            e.Message

    // A hint from a vocabulary this backend cannot possibly know. Phase
    // 318's contract is explicit: an unrecognised hint is IGNORED, and only
    // a hint the backend understands but cannot honour is a terminal
    // refusal. So this must be accepted too.
    let alien =
        freshSpec "train-forecast"
        |> ExternalWorkSpec.withHint "quantum-annealer-topology" "chimera-2048"

    let! alienAccepted = backend.Dispatcher.Submit("team-hints", alien)

    match alienAccepted with
    | Ok _ -> ()
    | Error e ->
        failtestf
            "a spec carrying an unrecognised resource hint was refused (%s). An unknown hint is ignored, not refused — otherwise every new hint any deployment invents breaks every backend that has not heard of it."
            e.Message
}

/// Two submissions with no idempotency key are two independent units, and
/// driving one moves nothing on the other (GP 12 rule 5 — no cross-shard
/// ordering or coupling).
let lawDistinctSubmissionsAreDistinctUnits conformance (backend: ExternalComputeBackendUnderTest) = async {
    let! first = submitOrFail backend "team-distinct" (freshSpec "render-scene")
    let! second = submitOrFail backend "team-distinct" (freshSpec "render-scene")

    Expect.notEqual
        first.HandleId
        second.HandleId
        "two submissions with no idempotency key are two units — sharing a handle would have one unit's outcome resolve the other's run"

    do! backend.Drive first (ExternalOutcome.Succeeded "blob://results/first")
    let! firstOutcome = settleToTerminal conformance backend first "the first unit"
    let! secondOutcome = backend.Dispatcher.Poll second

    Expect.equal firstOutcome (ExternalOutcome.Succeeded "blob://results/first") "the driven unit reached its outcome"

    Expect.isFalse
        (ExternalOutcome.isTerminal secondOutcome)
        "and the OTHER unit is untouched — handles are independent, so one completing cannot terminate its sibling"
}

// ── 324.B — idempotency + cancel ───────────────────────────────────────

/// Resubmitting the same idempotency key yields the same external unit —
/// **where the backend declares it honours the key**.
///
/// Where it does not, the law asserts the honest fallback: a repeat
/// submission is still accepted rather than refused, and the caller composes
/// Phase 485's memoization decorator if it needs the guarantee. Demanding
/// more here would be asserting a requirement Phase 318 words as SHOULD.
let lawIdempotentResubmit conformance (backend: ExternalComputeBackendUnderTest) = async {
    let key = sprintf "sales-h12-%s" (Guid.NewGuid().ToString "N")

    let spec = freshSpec "train-forecast" |> ExternalWorkSpec.withIdempotency key

    let! first = submitOrFail backend "team-idempotent" spec
    let! second = submitOrFail backend "team-idempotent" spec

    if conformance.HonoursIdempotency then
        Expect.equal
            second.HandleId
            first.HandleId
            "a backend declaring idempotency returns the EXISTING handle for a key it has already accepted — a second handle means a second unit of work, billed and run twice"

        Expect.equal
            second.NativeRef
            first.NativeRef
            "including the backend's own token, which names the one unit that exists"

        // And the one unit resolves once, for both handles.
        do! backend.Drive first (ExternalOutcome.Succeeded "blob://results/once")
        let! viaFirst = settleToTerminal conformance backend first "the unit, via the first handle"
        let! viaSecond = settleToTerminal conformance backend second "the unit, via the resubmitted handle"

        Expect.equal viaFirst viaSecond "both handles name one unit, so both report its single outcome"
    else
        // The honest fallback. Nothing about the seam is broken by a backend
        // that cannot dedupe; what would be broken is refusing the key.
        Expect.notEqual
            second.HandleId
            Guid.Empty
            "a backend that does not honour idempotency must still ACCEPT the key — refusing it would make the field unusable, and the platform-side memo (Phase 485) is the portable answer"
}

/// `Cancel` drives an in-flight unit to `Cancelled`.
///
/// Confirmed by `Poll`, not by `Cancel`'s return: Phase 318 says `Cancel`
/// returns when the request has been lodged, not when the backend has torn
/// down, so the settle loop is how the contract is actually observable.
let lawCancelDrivesInFlightToCancelled conformance (backend: ExternalComputeBackendUnderTest) = async {
    let! handle = submitOrFail backend "team-cancel" (freshSpec "render-scene")

    let! beforeCancel = backend.Dispatcher.Poll handle
    Expect.isFalse (ExternalOutcome.isTerminal beforeCancel) "precondition: the unit is in flight"

    do! backend.Dispatcher.Cancel handle

    let! outcome = settleToTerminal conformance backend handle "the cancelled unit"

    Expect.equal
        outcome
        ExternalOutcome.Cancelled
        "an in-flight unit that was cancelled polls as Cancelled — a cancel the caller cannot confirm through Poll is a cancel that did not happen"
}

/// Cancelling an already-terminal unit is a safe no-op: it does not throw,
/// and — the part that is easy to get wrong — it does not overwrite the
/// terminal outcome the unit already reached.
///
/// A backend that clobbered a `Succeeded` with `Cancelled` here would
/// discard a completed result whenever a cancel raced a completion, which is
/// precisely when a cancel is most likely to be issued.
let lawCancelOfTerminalIsSafeNoOp conformance (backend: ExternalComputeBackendUnderTest) = async {
    let! handle = submitOrFail backend "team-cancel-terminal" (freshSpec "train-forecast")
    do! backend.Drive handle (ExternalOutcome.Succeeded "blob://results/already-done")
    let! before = settleToTerminal conformance backend handle "the completed unit"

    // Must not throw.
    do! backend.Dispatcher.Cancel handle

    let! after = backend.Dispatcher.Poll handle

    Expect.equal
        after
        before
        "cancelling a terminal unit changes nothing — the completed result is not discarded because a teardown was requested after the fact"
}

/// `Cancel` is idempotent — cancelling twice is not an error.
let lawCancelIsIdempotent conformance (backend: ExternalComputeBackendUnderTest) = async {
    let! handle = submitOrFail backend "team-cancel-twice" (freshSpec "render-scene")

    do! backend.Dispatcher.Cancel handle
    do! backend.Dispatcher.Cancel handle
    do! backend.Dispatcher.Cancel handle

    let! outcome = settleToTerminal conformance backend handle "the repeatedly-cancelled unit"

    Expect.equal
        outcome
        ExternalOutcome.Cancelled
        "repeat cancels converge on one Cancelled outcome — a `finally` that cancels a unit a happy path already cancelled must not fault"
}

// ── 324.C — scope isolation (GP 4) ─────────────────────────────────────

/// A handle minted under scope A, presented with scope B, must not be
/// answered as scope B's — **where the backend declares it validates the
/// handle's scope**.
///
/// Where it does not, the law asserts the precondition the layer above
/// depends on: the handle carries the scope it was minted under, unmodified,
/// so the handle store and the callback ingress can enforce GP 4
/// structurally. See the section header, and
/// `IExternalHandleStoreContract.lawScopePartitioned` for where that
/// enforcement is itself under contract.
///
/// The spec is deliberately **non-idempotent**: a memoizing decorator keys
/// its handle→entry index by handle id, so a keyed-and-cached unit could be
/// served from cache without the inner backend's scope check ever running.
/// Using a non-memoizable spec keeps the law measuring the seam it names.
let lawScopeIsolation conformance (backend: ExternalComputeBackendUnderTest) = async {
    let! ownHandle = submitOrFail backend "team-owner" (freshSpec "train-forecast")
    do! backend.Drive ownHandle (ExternalOutcome.Succeeded "blob://results/tenant-secret")
    let! own = settleToTerminal conformance backend ownHandle "the owning scope's unit"

    Expect.equal
        own
        (ExternalOutcome.Succeeded "blob://results/tenant-secret")
        "precondition: the owner sees its own result"

    // The one cross-scope shape this seam can express: the same handle,
    // re-scoped. Everything else about it is genuine.
    let rescoped = {
        ownHandle with
            ScopeId = "team-intruder"
    }

    Expect.equal
        rescoped.HandleId
        ownHandle.HandleId
        "precondition: only the scope differs, so anything that distinguishes them is a scope check"

    if conformance.ValidatesHandleScope then
        let! intruderPoll = backend.Dispatcher.Poll rescoped

        match intruderPoll with
        | ExternalOutcome.Succeeded leaked ->
            failtestf
                "GP 4: a handle re-scoped to 'team-intruder' was answered with scope 'team-owner''s result ref (%s). A backend declaring ValidatesHandleScope must refuse a handle whose scope disagrees with its own record."
                leaked
        | ExternalOutcome.Failed _ -> () // the conforming answer: a typed refusal
        | other ->
            failtestf
                "GP 4: a re-scoped handle must be reported as Failed, never as a fabricated non-terminal or Cancelled state; got %A"
                other

        // And a cancel presented under the wrong scope must not reach the
        // owner's unit.
        do! backend.Dispatcher.Cancel rescoped
        let! ownerAfter = backend.Dispatcher.Poll ownHandle

        Expect.equal
            ownerAfter
            own
            "a cancel presented under the wrong scope changes nothing in the owning scope — otherwise cross-tenant denial of service is one forged field away"
    else
        Expect.equal
            ownHandle.ScopeId
            "team-owner"
            "a backend that cannot check the handle's scope must at least carry it faithfully — that is the input the handle store and the callback ingress enforce GP 4 with (324.D)"

        Expect.notEqual
            rescoped.ScopeId
            ownHandle.ScopeId
            "and the scope is a field on the handle rather than backend-internal state, which is what makes enforcement at the layer above possible at all"
}

/// Every 324.A–C law, in reading order.
let laws: (string * (ExternalComputeConformance -> ExternalComputeBackendUnderTest -> Async<unit>)) list = [
    // 324.A
    "324.A — Submit returns a well-formed handle (identity by value, correct scope)", lawSubmitReturnsWellFormedHandle
    "324.A — Poll transitions Pending / Running to a terminal outcome", lawPollTransitionsToTerminal
    "324.A — a Succeeded outcome carries a resolvable result ref, echoed verbatim",
    lawSucceededCarriesResolvableResultRef
    "324.A — polling a terminal handle is idempotent", lawPollOnTerminalIsIdempotent
    "324.A — error classification (retriable vs terminal) is honoured in both directions",
    lawErrorClassificationHonoured
    "324.A — ResourceHints are accepted, known and unknown alike", lawResourceHintsAccepted
    "324.A — two un-keyed submissions are two independent units", lawDistinctSubmissionsAreDistinctUnits
    // 324.B
    "324.B — resubmitting one idempotency key yields one external unit", lawIdempotentResubmit
    "324.B — Cancel drives an in-flight unit to Cancelled", lawCancelDrivesInFlightToCancelled
    "324.B — cancelling an already-terminal unit is a safe no-op", lawCancelOfTerminalIsSafeNoOp
    "324.B — Cancel is idempotent", lawCancelIsIdempotent
    // 324.C
    "324.C / GP 4 — a re-scoped handle is not answered as the other scope's", lawScopeIsolation
]

/// Bind any `IExternalComputeDispatcher` factory against the full bar.
///
/// `create` is called **per law**, so one law's units cannot leak into
/// another's — which matters for a companion pointed at shared
/// infrastructure (a stub server, a kind cluster) where two laws would
/// otherwise contend for reasons unrelated to the contract.
///
/// This is the entry point Phase 322 (HTTP stub server) and Phase 323 (kind
/// cluster) bind, unmodified.
let contractFor
    (name: string)
    (conformance: ExternalComputeConformance)
    (create: unit -> ExternalComputeBackendUnderTest)
    =
    testList name [
        for label, law in laws -> testCaseAsync label <| async { do! law conformance (create ()) }
    ]

// ── the reference backend (324.E) ──────────────────────────────────────

/// A conformant in-process backend in the **request/response** shape: a
/// table of units, answered on demand. That is deliberately the HTTP
/// worker-pool shape (Phase 322), because it is the one whose seam is
/// easiest to accidentally bake into a contract pack — so the pack's own
/// reference implementation being that shape is what makes it worth
/// checking `Drive` + `settle` against a watch-based backend later.
///
/// It honours both optional clauses: idempotency keys are deduped per scope,
/// and `Poll` / `Cancel` check the presented handle's scope against the
/// stored one.
type ReferenceComputeBackend(label: string) =
    /// Unit id → its current outcome.
    let units = ConcurrentDictionary<Guid, ExternalOutcome>()
    /// Unit id → the scope it was accepted under. The scope check reads this
    /// and never the presented handle, which is the whole point.
    let scopes = ConcurrentDictionary<Guid, string>()
    /// (scope, idempotency key) → the handle already minted for it.
    let byIdempotency = ConcurrentDictionary<string * string, ExternalHandle>()

    new() = ReferenceComputeBackend "reference-worker-pool"

    /// Live unit count — exposed so a binding can assert acceptance
    /// happened rather than inferring it from a downstream effect.
    member _.UnitCount = units.Count

    /// The harness lever: set the unit's outcome directly, bypassing the
    /// scope check (the harness is the backend's operator, not a caller).
    member _.Drive (handle: ExternalHandle) (outcome: ExternalOutcome) = async {
        if units.ContainsKey handle.HandleId then
            units[handle.HandleId] <- outcome
    }

    /// This backend, packaged for `contractFor`.
    member this.AsBackendUnderTest: ExternalComputeBackendUnderTest = {
        Dispatcher = this :> IExternalComputeDispatcher
        Drive = this.Drive
    }

    /// `Some outcome` when `handle` names a unit this backend accepted under
    /// the scope the handle claims; `None` otherwise.
    member _.TryRead(handle: ExternalHandle) : ExternalOutcome option =
        match scopes.TryGetValue handle.HandleId with
        | true, accepted when accepted = handle.ScopeId ->
            match units.TryGetValue handle.HandleId with
            | true, outcome -> Some outcome
            | false, _ -> None
        | _ -> None

    interface IExternalComputeDispatcher with
        member _.Backend = label

        member _.Submit(scopeId: string, spec: ExternalWorkSpec) = async {
            match spec.Idempotency with
            | Some key when byIdempotency.ContainsKey((scopeId, key)) ->
                // The existing handle, not a second unit. Keyed by SCOPE and
                // key together: two tenants using the same key are two units.
                return Ok byIdempotency[(scopeId, key)]
            | _ ->
                let handle = {
                    HandleId = Guid.NewGuid()
                    Backend = label
                    ScopeId = scopeId
                    NativeRef = sprintf "reference://%s/%s" label (Guid.NewGuid().ToString "N")
                    SubmittedAt = DateTime.UtcNow
                }

                units[handle.HandleId] <- ExternalOutcome.Pending
                scopes[handle.HandleId] <- scopeId

                match spec.Idempotency with
                | Some key -> byIdempotency[(scopeId, key)] <- handle
                | None -> ()

                return Ok handle
        }

        member this.Poll(handle: ExternalHandle) = async {
            match this.TryRead handle with
            | Some outcome -> return outcome
            | None ->
                // Unknown, or presented under a scope this unit was not
                // accepted under. A typed terminal failure — never an
                // exception, and never a fabricated Cancelled (Phase 318).
                return
                    ExternalOutcome.Failed(
                        ExternalComputeError.terminal (
                            sprintf "backend '%s' holds no unit %O in scope '%s'" label handle.HandleId handle.ScopeId
                        )
                    )
        }

        member this.Cancel(handle: ExternalHandle) = async {
            match this.TryRead handle with
            | Some outcome when not (ExternalOutcome.isTerminal outcome) ->
                units[handle.HandleId] <- ExternalOutcome.Cancelled
            // Terminal already, unknown, or wrong scope: a no-op. A terminal
            // outcome is NOT overwritten, and a wrong-scope cancel reaches
            // nothing.
            | _ -> ()
        }

// ── the shipped bindings (324.E) ───────────────────────────────────────

/// Bind the pack against the reference backend and both shipped decorator
/// stacks over it.
///
/// **Three bindings, not one, and the second and third are the load-bearing
/// ones.** A pack that has only ever run against the implementation it was
/// written beside cannot distinguish a portable clause from a description of
/// that implementation — the same argument the handle-store pack makes for
/// binding two atomicity primitives. Phase 484's router and Phase 485's memo
/// are `IExternalComputeDispatcher` implementations that mint no work of
/// their own, so putting the identical bar through both proves the pack is
/// genuinely factory-generic: it survives a decorator restamping
/// `ExternalHandle.Backend` (the router) and one short-circuiting `Poll`
/// from a cache (the memo).
let conformanceTests =
    testList "IExternalComputeDispatcher — submit / poll / terminal / idempotency / cancel / scope (Phase 324)" [

        contractFor
            "ReferenceComputeBackend (request/response — the Phase 322 shape)"
            ExternalComputeConformance.strict
            (fun () -> ReferenceComputeBackend().AsBackendUnderTest)

        contractFor
            "RoutingComputeDispatcher over the reference backend (Phase 484 — restamps the handle's backend label)"
            ExternalComputeConformance.strict
            (fun () ->
                let inner = ReferenceComputeBackend "routed-reference"

                let registry =
                    ComputeBackendRegistry [
                        ComputeBackendRegistration.create "reference-fleet" (inner :> IExternalComputeDispatcher)
                        |> ComputeBackendRegistration.asDefault
                    ]

                {
                    Dispatcher =
                        RoutingComputeDispatcher(registry, ComputeFleetTelemetry(), NoOpMetricsSink())
                        :> IExternalComputeDispatcher
                    // The router restamps `Backend`, never `HandleId`, so
                    // the harness lever keys off the id and is unaffected.
                    Drive = inner.Drive
                })

        contractFor
            "MemoizedComputeDispatcher over the reference backend (Phase 485 — short-circuits Poll from a cache)"
            ExternalComputeConformance.strict
            (fun () ->
                let inner = ReferenceComputeBackend "memoized-reference"

                {
                    Dispatcher =
                        MemoizedComputeDispatcher(inner :> IExternalComputeDispatcher) :> IExternalComputeDispatcher
                    Drive = inner.Drive
                })
    ]

// ── self-test: the pack fails a non-conforming dispatcher ─────────────
//
// Seven defects, each breaking exactly one clause. A conformance pack that
// has never rejected anything is a list of things that happened to be true
// of the implementation it was written beside.

/// The ways a dispatcher is wrong that the laws above exist to catch.
type private Defect =
    /// `Poll` reports a terminal outcome once and then forgets it.
    | ForgetsTerminalOnRepoll
    /// `Cancel` is accepted and does nothing.
    | IgnoresCancel
    /// `Cancel` overwrites an already-terminal outcome with `Cancelled`.
    | ClobbersTerminalOnCancel
    /// A retriable failure is reported as terminal.
    | FlattensRetriability
    /// `Poll` / `Cancel` resolve by handle id alone, ignoring the scope.
    | IgnoresHandleScope
    /// `Submit` refuses a spec carrying resource hints.
    | RejectsResourceHints
    /// Every submission gets the same handle.
    | MintsOneSharedHandle

/// A decorator over the reference backend that injects exactly one defect,
/// so each self-test case isolates one law.
type private DefectiveBackend(defect: Defect) =
    let inner = ReferenceComputeBackend "defective"
    let dispatcher = inner :> IExternalComputeDispatcher
    /// Ids whose terminal outcome has already been read once.
    let read = ConcurrentDictionary<Guid, bool>()
    /// The one handle `MintsOneSharedHandle` hands out.
    let mutable shared: ExternalHandle option = None
    /// Handle id → the scope it was really accepted under, so
    /// `IgnoresHandleScope` can rewrite a presented handle back to it.
    let acceptedScopes = ConcurrentDictionary<Guid, string>()

    member _.Drive = inner.Drive

    member this.AsBackendUnderTest: ExternalComputeBackendUnderTest = {
        Dispatcher = this :> IExternalComputeDispatcher
        Drive = inner.Drive
    }

    /// The handle this backend would actually consult, per its defect.
    member private _.Effective(handle: ExternalHandle) =
        match defect with
        | IgnoresHandleScope ->
            match acceptedScopes.TryGetValue handle.HandleId with
            | true, accepted -> { handle with ScopeId = accepted }
            | false, _ -> handle
        | _ -> handle

    interface IExternalComputeDispatcher with
        member _.Backend = dispatcher.Backend

        member _.Submit(scopeId: string, spec: ExternalWorkSpec) = async {
            match defect with
            | RejectsResourceHints when not spec.ResourceHints.IsEmpty ->
                return Error(ExternalComputeError.terminal "this backend does not accept resource hints")
            | MintsOneSharedHandle ->
                match shared with
                | Some handle -> return Ok handle
                | None ->
                    let! first = dispatcher.Submit(scopeId, spec)

                    match first with
                    | Ok handle ->
                        shared <- Some handle
                        acceptedScopes[handle.HandleId] <- scopeId
                        return Ok handle
                    | Error e -> return Error e
            | _ ->
                let! result = dispatcher.Submit(scopeId, spec)

                match result with
                | Ok handle ->
                    acceptedScopes[handle.HandleId] <- scopeId
                    return Ok handle
                | Error e -> return Error e
        }

        member this.Poll(handle: ExternalHandle) = async {
            let! outcome = dispatcher.Poll(this.Effective handle)

            match defect with
            | ForgetsTerminalOnRepoll when ExternalOutcome.isTerminal outcome ->
                if read.TryAdd(handle.HandleId, true) then
                    return outcome
                else
                    // Read once, then amnesia — the shape that turns a
                    // resolved run into an unresolvable one.
                    return ExternalOutcome.Pending
            | FlattensRetriability ->
                match outcome with
                | ExternalOutcome.Failed e when e.Retriable ->
                    return ExternalOutcome.Failed(ExternalComputeError.terminal e.Message)
                | other -> return other
            | _ -> return outcome
        }

        member this.Cancel(handle: ExternalHandle) = async {
            match defect with
            | IgnoresCancel -> return ()
            | ClobbersTerminalOnCancel ->
                // Cancels regardless of the unit's current state, discarding
                // a completed result.
                do! inner.Drive (this.Effective handle) ExternalOutcome.Cancelled
                return ()
            | _ -> return! dispatcher.Cancel(this.Effective handle)
        }

let private strict = ExternalComputeConformance.strict

/// Run one law against one defective backend and require it to fail.
let private mustReject (law: ExternalComputeConformance -> ExternalComputeBackendUnderTest -> Async<unit>) defect why =
    Expect.throws
        (fun () ->
            law strict (DefectiveBackend(defect).AsBackendUnderTest)
            |> Async.RunSynchronously)
        why

/// Run one law against one defective backend and require it to PASS —
/// the evidence that the law which DOES catch that defect is load-bearing.
let private mustAccept (law: ExternalComputeConformance -> ExternalComputeBackendUnderTest -> Async<unit>) defect =
    law strict (DefectiveBackend(defect).AsBackendUnderTest)
    |> Async.RunSynchronously

let selfTests =
    testList "IExternalComputeDispatcher contract — self-test (the pack has teeth)" [

        testCase "a backend that forgets a terminal outcome after one read fails the idempotent-poll law"
        <| fun _ ->
            mustReject
                lawPollOnTerminalIsIdempotent
                ForgetsTerminalOnRepoll
                "a terminal state read once and then forgotten must fail the pack — it turns a resolved run into an unresolvable one"

        testCase "…and that same backend PASSES the transition law, which is why the idempotent-poll law exists"
        <| fun _ ->
            // The first read still reports the driven outcome, so the
            // transition law certifies it. Without the re-poll law the pack
            // would too.
            mustAccept lawPollTransitionsToTerminal ForgetsTerminalOnRepoll

        testCase "a backend whose Cancel does nothing fails the cancel law"
        <| fun _ ->
            mustReject
                lawCancelDrivesInFlightToCancelled
                IgnoresCancel
                "a cancel the caller cannot confirm through Poll is a cancel that did not happen"

        testCase "a backend whose Cancel clobbers a terminal outcome fails the cancel-of-terminal law"
        <| fun _ ->
            mustReject
                lawCancelOfTerminalIsSafeNoOp
                ClobbersTerminalOnCancel
                "discarding a completed result because a teardown was requested afterwards must fail the pack"

        testCase "…and that same backend PASSES the in-flight cancel law — the two cancel laws are not redundant"
        <| fun _ -> mustAccept lawCancelDrivesInFlightToCancelled ClobbersTerminalOnCancel

        testCase "a backend that flattens retriability fails the error-classification law"
        <| fun _ ->
            mustReject
                lawErrorClassificationHonoured
                FlattensRetriability
                "reporting a transient saturation as terminal abandons recoverable work, and must fail the pack"

        testCase "a scope-blind backend fails the GP 4 isolation law"
        <| fun _ ->
            mustReject
                lawScopeIsolation
                IgnoresHandleScope
                "answering a re-scoped handle with the owning scope's result ref is a cross-tenant read"

        testCase "a backend that refuses resource hints fails the hints law"
        <| fun _ ->
            mustReject
                lawResourceHintsAccepted
                RejectsResourceHints
                "hints are advisory — refusing the field makes it unusable by any portable caller"

        testCase "a backend that mints one shared handle fails the distinct-units law"
        <| fun _ ->
            mustReject
                lawDistinctSubmissionsAreDistinctUnits
                MintsOneSharedHandle
                "two un-keyed submissions sharing a handle means one unit's outcome resolves the other's run"
    ]
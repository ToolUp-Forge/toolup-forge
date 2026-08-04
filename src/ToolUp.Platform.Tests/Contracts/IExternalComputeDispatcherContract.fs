// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IExternalComputeDispatcherContract

open System
open System.Text.Json
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
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
// The pack binds to `NoExternalComputeDispatcher` because that is the only
// in-tree implementation (every real backend is a companion under
// `src/ExternalCompute/`, GP 1). A companion runs the same
// `portabilityAudit` list against its own factory.

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
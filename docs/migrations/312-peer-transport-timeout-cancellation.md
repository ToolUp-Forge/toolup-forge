# Migration — Phase 312: peer transport timeout + cancellation propagation

**Status:** additive for *callers* (every existing call site compiles and behaves identically). **One consumer action is required, and only for a consumer that IMPLEMENTS `IPeerClient`** — the two interface members gained an optional `CancellationToken`, which is source-compatible on the calling side and a signature change on the implementing side.

No `PeerError` case was added, so no exhaustive match over `PeerError` breaks.

## Why

`HttpPeerClient.send` called `httpClient.SendAsync request` with **no** `CancellationToken`, and the shared `HttpClient` composed in `PeerCompose` set no `Timeout` — so every outbound peer call inherited the BCL's 100 s default and nothing could abort one early.

`DefaultPeerFanout` therefore only *looked* like it cancelled. Its `cts.Cancel()` stopped the F# `Async` wrapper, so a `firstSuccess` / `quorum` / deadline policy returned promptly and the result map looked correct — while every peer it stopped awaiting kept a live socket on the shared client for up to 100 s. Latency was masked; connection-pool capacity was not reclaimed. On a wide federation graph that is the difference between an early return and an early return you can afford to make.

## What changed

### 1. `IPeerClient` — an optional `CancellationToken` on both members

```fsharp
// before
abstract Invoke:
    target: TargetPeer * contractId: string * methodName: string * payload: PeerWirePayload ->
        Async<Result<string, PeerError>>

// after
abstract Invoke:
    target: TargetPeer *
    contractId: string *
    methodName: string *
    payload: PeerWirePayload *
    ?cancellationToken: CancellationToken ->
        Async<Result<string, PeerError>>
```

`PollJob` gains the same parameter.

**Callers need no change** — `client.Invoke(target, contractId, methodName, payload)` compiles and behaves exactly as before (GP 11).

**Implementers must add the parameter.** If you have your own `IPeerClient` (a loopback stub, a recording decorator, an alternative transport):

```fsharp
// before
interface IPeerClient with
    member _.Invoke(target, contractId, methodName, payload) = …
    member _.PollJob(target, contractId, jobId) = …

// after — ignoring the token
interface IPeerClient with
    member _.Invoke(target, contractId, methodName, payload, ?_cancellationToken) = …
    member _.PollJob(target, contractId, jobId, ?_cancellationToken) = …

// after — a decorator that forwards it
interface IPeerClient with
    member _.Invoke(target, contractId, methodName, payload, ?cancellationToken) =
        match cancellationToken with
        | Some ct -> inner.Invoke(target, contractId, methodName, payload, ct)
        | None -> inner.Invoke(target, contractId, methodName, payload)
```

A stub that ignores the token is fine: the ambient token (below) is what the fan-out and the round orchestrator actually rely on, and an in-process stub has no socket to abort.

### 2. Two tokens reach the socket, and the ambient one is the load-bearing half

`HttpPeerClient` now issues every request under a token linked from three sources:

| Source | Who sets it | What it is for |
|---|---|---|
| `Async.CancellationToken` (ambient) | whoever started the workflow | **the reach.** `DefaultPeerFanout` starts each peer call with `Async.Start(work, cts.Token)` and `DefaultRoundOrchestrator` dispatches under the run's token, so their `Cancel()` now aborts the request instead of merely stopping the wait |
| the optional `cancellationToken` | the call site | a caller that *holds* a token without running under it — an ASP.NET handler's `HttpContext.RequestAborted`, say |
| `PeerTransportPolicy.CallTimeout` | the composition | the per-call deadline |

Nothing threads a token by hand between the fan-out and the transport: a `call` closure inherits the token by being *run under* it. That coupling is documented on `DefaultPeerFanout`.

### 3. `PeerTransportPolicy` — the per-call deadline as data

```fsharp
open System
open ToolUp.InterPlatform

app
|> PeerServerApp.withTransportPolicy (
    PeerTransportPolicy.defaults
    |> PeerTransportPolicy.withCallTimeout (TimeSpan.FromSeconds 5.0))
```

`PeerTransportPolicy.defaults` is a **100-second** deadline — deliberately the bound a `Timeout`-less `HttpClient` already imposed, so a deployment that upgrades and composes nothing keeps exactly the behaviour it had (GP 11). `PeerTransportPolicy.unbounded` removes the deadline; such a call is still fully cancellable, it just cannot time out.

**The deadline is per-request, not `HttpClient.Timeout`, and that is deliberate.** The composed client is shared with the capability handshake and the profile fetch, so a client-level timeout would silently re-bound two other call shapes; and a `HttpClient.Timeout` expiry raises the *same* `TaskCanceledException` a caller's own cancellation does, which would make the distinction below undecidable. The shared client is left on the BCL default.

### 4. The three non-answers are kept apart

| Outcome | How it surfaces | How to detect it |
|---|---|---|
| **Peer failure** | the receiver's structured `PeerError`, or `PeerTransport` carrying the underlying message | match the DU case, as before |
| **Timeout** — *our* deadline elapsed while the peer worked on | `Error (PeerTransport "peer call timed out after …")` | `PeerTransportOutcome.isTimeout` |
| **Cancellation** — the caller went away | the computation completes as **cancelled**; there is no `Ok` and no `Error` | ordinary F# cancellation (`Async.StartAsTask` yields a cancelled task) |

**Cancellation deliberately does not produce a value.** A cancelled call is not an answer, and one that returned `Error (PeerTransport "cancelled")` would be written into the fan-out's result slot and counted toward its quorum — an early return would then satisfy itself with the peers it had just abandoned. The fan-out map stays total the way it always did: a cut-short peer carries the existing `"peer not awaited …"` descriptor, because a cancelled child completes through the cancellation continuation and never writes a slot.

**Why a message prefix and not a new `PeerError` case.** A new case would break every exhaustive match over `PeerError` in every consumer — under the Phase 624 rule that `FS0025` is a build error, that is a compile break in each of them — for a distinction that is transport-local and never crosses the wire (a receiver has no idea the caller gave up). `PeerTransportOutcome.isTimeout` is the supported reader, so no consumer needs to know the wording.

### 5. `HttpPeerClient` gained a fourth constructor argument

`HttpPeerClient(httpClient, auth, localPeer, policy)`. The three-argument constructor survives as a secondary constructor defaulting to `PeerTransportPolicy.defaults` — an explicit overload rather than an optional argument, because F# folds an optional argument into one widened constructor and erases the narrower signature from the emitted public surface (the discipline `DefaultRoundOrchestrator` adopted at Phase 483).

## Binary-compatibility note

`api-baselines/InterPlatform.approved.txt` records three entries as removed-and-re-added: the two `IPeerClient` members (retyped with the optional token) and the `PeerServerApp` record constructor (widened by the new `TransportPolicy` field — as Phases 309 / 311 / 315 / 331 / 343 each widened it before). All three are **source-compatible**; recompile rather than binary-swap, which is the normal consumption route off the feed.

## Consumer action

1. If you implement `IPeerClient`, add the optional parameter to both members (§1).
2. Optionally, compose a tighter deadline with `PeerServerApp.withTransportPolicy` (§3).
3. If you branch on `PeerTransport` messages, consider `PeerTransportOutcome.isTimeout` to separate your own deadline from the peer's failure (§4). Note that a timeout is **not** a retry signal — the peer may still complete the call, which is the same reason the substrate never auto-retries a mutating call.

## Verification

- `dotnet fantomas` over the changed `.fs` files; `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 0 failures.
- New coverage (`InProcess/PeerTransportTimeoutTests.fs`, 9 cases): the deadline expiry aborts the request and classifies as a timeout; the poll leg is bounded on the same terms; a caller's cancellation aborts the in-flight request and completes as cancelled; an explicit token does the same; `FanoutPolicy.firstSuccess` cancels the hanging peer's request while the map stays total. Every cancellation claim is **measured** — the stub transport waits on the token it was handed and records, in a `finally`, whether that token fired — and each is paired with a control (a within-deadline peer, a genuine peer failure, the pre-312 constructor, `FanoutPolicy.all`) so "it was cancelled" cannot be satisfied by a transport that had started cancelling everything.
- Teeth confirmed by removing the propagation: 5 of the 9 cases go red, the 4 controls stay green.

## Rollback

Drop the token argument from `httpClient.SendAsync` / `ReadAsStringAsync` in `HttpPeerClient.send` and the transport reverts to pre-312 behaviour with no other change. `PeerTransportPolicy`, `PeerTransportOutcome`, `withTransportPolicy` and the optional interface parameter are additive surfaces; removing them requires reverting the implementer signatures too.

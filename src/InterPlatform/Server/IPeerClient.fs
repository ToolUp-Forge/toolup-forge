// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Threading

// ─── Layer 4 — initiator surface ─────────────────────────────────────
//
// `IPeerClient` is the *initiating* deployment's transport-level
// surface: it serialises a `PeerWirePayload` into a JSON-RPC request,
// posts it to the target peer's `/peer/v1/{contractId}` route, and
// deserialises the response into a result string or a `PeerError`. The
// typed proxy (`JsonRpcPeerClient.create<'TApi>`) is built on top of
// this — it reflects over the contract type, marshals positional
// arguments, and resolves `LongRunning` methods into `PeerJobHandle<'T>`
// whose `Poll` closure calls `PollJob` below.
//
// Six portability rules (GP 12):
//   1. Identity by value — `TargetPeer`, `PeerWirePayload`, `PeerJobId`
//      are records / primitives; no live socket handle is exposed.
//   2. Async at every boundary — both methods return `Async<_>`.
//   3. Retry / supervision as data — failure is `PeerError`; the caller
//      decides retry policy (the substrate does not auto-retry a
//      mutating call, which could double-execute).
//   4. Stateless between calls — the client carries no per-call state;
//      every `Invoke` is self-contained.
//   5. No cross-shard ordering — concurrent invokes to the same peer
//      carry no ordering promise.
//   6. Precision at the lower bound — `PollJob` is poll-driven; the
//      substrate makes no sub-request-latency promise for long-running
//      resolution.

/// Phase 312 — how long one outbound peer call may take. Policy as data
/// (GP 12 rule 3): the bound rides a record the composition sets once,
/// not a knob a call site remembers to pass.
///
/// **Why this is not `HttpClient.Timeout`.** The composed client is
/// shared by every outbound path (the contract transport, the capability
/// handshake, the profile fetch), so a client-level timeout is one bound
/// for three call shapes with different latency profiles. Worse, a
/// `HttpClient.Timeout` expiry surfaces as the *same*
/// `TaskCanceledException` a caller's own cancellation raises, so a
/// transport built on it cannot tell "this peer is slow" from "my caller
/// went away" — and reporting one as the other is precisely the
/// confusion this phase exists to remove. A per-call linked
/// `CancellationTokenSource` keeps the deadline a distinct source, which
/// is what makes the trichotomy below decidable.
type PeerTransportPolicy = {
    /// Per-call deadline, measured from the moment the request is built
    /// to the moment its body has been read. `None` = no deadline (the
    /// call is then bounded only by the underlying `HttpClient` and by
    /// caller cancellation).
    ///
    /// Precision at the lower bound (GP 12 rule 6): a best-effort
    /// wall-clock bound, not a real-time guarantee — the same contract
    /// `FanoutPolicy.Timeout` declares.
    CallTimeout: TimeSpan option
    /// Phase 339 — when `true`, an outbound peer URL is sent over
    /// whatever scheme it names, cleartext included. `false` (the
    /// default) admits `https://` anywhere and `http://` only to a
    /// loopback host.
    ///
    /// **This is an opt-OUT of a security default, and it is on the
    /// transport policy rather than on `PeerServerApp` deliberately.**
    /// The bearer token rides every outbound leg — contract invoke,
    /// job poll, capability handshake, profile fetch — and a peer
    /// `TargetPeer` can arrive from the registry at runtime rather than
    /// from the composition, so the posture has to travel with the
    /// transport, not with the compose record. One record, one place to
    /// read, every leg.
    AllowInsecureTransport: bool
}

[<RequireQualifiedAccess>]
module PeerTransportPolicy =
    /// A 100-second per-call deadline.
    ///
    /// **The number is deliberately the one already in force.** A
    /// `HttpClient` constructed without a `Timeout` uses 100 s, which is
    /// what every pre-312 peer call inherited, so a deployment that
    /// upgrades and composes nothing keeps exactly the bound it had
    /// (GP 11). What changes is that the bound is now *nameable* — a
    /// peer-topology tunable rather than a BCL default nobody chose —
    /// and that expiry is reported as a timeout rather than as an
    /// indistinguishable cancellation.
    let defaults: PeerTransportPolicy = {
        CallTimeout = Some(TimeSpan.FromSeconds 100.0)
        AllowInsecureTransport = false
    }

    /// No per-call deadline. For a federation whose calls are genuinely
    /// long-lived and bounded by something else (a receiver-side budget,
    /// a caller-held token). A call under this policy can still be
    /// cancelled — it just cannot time out.
    let unbounded: PeerTransportPolicy = { defaults with CallTimeout = None }

    /// Replace the per-call deadline.
    let withCallTimeout (timeout: TimeSpan) (policy: PeerTransportPolicy) : PeerTransportPolicy = {
        policy with
            CallTimeout = Some timeout
    }

    /// Phase 339 — restore the pre-339 posture: send to whatever scheme
    /// a peer's `BaseUrl` names, cleartext included. See
    /// `PeerTransportSecurity` for what this gives up.
    let allowInsecureTransport (policy: PeerTransportPolicy) : PeerTransportPolicy = {
        policy with
            AllowInsecureTransport = true
    }

/// Phase 312 — the transport's three non-answers, kept apart.
///
/// A peer call that produces no result did so for one of three reasons,
/// and collapsing them loses the only information a caller can act on:
///
///   * **Peer failure** — the receiver answered, badly, or the socket
///     broke. A structured `PeerError` from the wire, or
///     `PeerTransport` carrying the underlying message. Retrying may
///     help; it depends on the case.
///   * **Timeout** — *this* caller's deadline elapsed while the peer was
///     still working. `PeerTransport` carrying `TimeoutPrefix`,
///     recognised by `isTimeout`. The peer is not known to have failed —
///     it may well complete — so a retry risks double-execution exactly
///     as `IPeerClient`'s no-auto-retry rule warns.
///   * **Cancellation** — the caller went away. This is *not* an error
///     value at all: the workflow completes as CANCELLED, so a cancelled
///     call never fabricates an answer that could be counted toward a
///     quorum or folded into a round. See `IPeerClient` below.
///
/// The timeout marker is a message prefix rather than a new `PeerError`
/// case on purpose. A new case would break every exhaustive match over
/// `PeerError` in every consumer, for a distinction that is transport-
/// local and never crosses the wire (a receiver has no idea the caller
/// gave up). The prefix convention already exists in this substrate —
/// `IPeerFanout` marks a not-awaited peer the same way — and
/// `isTimeout` is the supported way to read it, so no caller has to
/// know the wording.
[<RequireQualifiedAccess>]
module PeerTransportOutcome =
    /// Stable prefix every deadline expiry carries. Public so a
    /// language-neutral log consumer can match it too.
    [<Literal>]
    let TimeoutPrefix = "peer call timed out"

    /// The `PeerError` a per-call deadline expiry produces.
    let timedOut (deadline: TimeSpan) : PeerError =
        // Invariant culture: the message is read by logs and by
        // `isTimeout`, not by an end user, so a decimal comma from an
        // operator's locale would be noise at best.
        let seconds =
            deadline.TotalSeconds.ToString("0.###", Globalization.CultureInfo.InvariantCulture)

        PeerTransport $"{TimeoutPrefix} after {seconds}s (PeerTransportPolicy.CallTimeout)"

    /// Did this error come from *our* deadline rather than from the
    /// peer? Callers that retry on a slow peer but not on a rejected one
    /// branch on this instead of scraping the message.
    let isTimeout (error: PeerError) : bool =
        match error with
        | PeerTransport message -> message.StartsWith(TimeoutPrefix, StringComparison.Ordinal)
        | _ -> false

/// Phase 339 — is this peer URL one a bearer token may be sent to?
///
/// **The token is the whole point.** Every outbound peer leg — the
/// contract invoke, the job poll, the capability handshake, the profile
/// fetch — mints a fresh HS256 bearer from `IPeerAuthProvider` and puts
/// it in an `Authorization` header. Over `http://` that header is
/// readable by anything on the path, and a peer token is not a session
/// cookie: it vouches for the *deployment*, so a single observation is
/// full peer impersonation until the signing key rotates. The URL was
/// previously interpolated verbatim with no scheme check at all, in all
/// four places.
///
/// The accept rule is the one `isAcceptableKeyFetchUrl` already applies
/// to OIDC key fetches, deliberately: **https anywhere, http to a
/// loopback host**. Loopback is not a courtesy — an in-repo suite, a
/// `dotnet run` inner loop and a two-container compose file all address
/// peers as `http://localhost:PORT`, and there is no attacker position on
/// the loopback interface that does not already have the process. Any
/// other cleartext host is refused.
///
/// **What this is NOT.** It is a check on where a token may be sent, not
/// a replacement for anything the platform does once it is sent there:
/// certificate-chain validation, hostname verification and revocation
/// stay entirely with `HttpClient`'s handler, and nothing in this
/// substrate disables, relaxes, or offers a knob to relax them. A
/// deployment that needs a private trust anchor installs it in the host's
/// certificate store, where it is auditable.
[<RequireQualifiedAccess>]
module PeerTransportSecurity =
    /// Stable prefix every cleartext refusal carries. Public so a
    /// language-neutral log consumer can match it, and so `isRefusal`
    /// below has one thing to key off. Same convention as
    /// `PeerTransportOutcome.TimeoutPrefix`, and for the same reason: a
    /// new `PeerError` case would break every exhaustive match in every
    /// consumer for a distinction that never crosses the wire.
    [<Literal>]
    let RefusalPrefix = "peer transport refused"

    /// Is this URL one a peer bearer token may travel to under `policy`?
    /// Pure and total — a malformed or relative URL is refused, since a
    /// string the substrate cannot classify is a string it cannot
    /// promise anything about.
    let isAcceptablePeerUrl (policy: PeerTransportPolicy) (url: string) : bool =
        if policy.AllowInsecureTransport then
            // The opt-out is total, including for a URL that does not
            // parse: pre-339 behaviour interpolated the string verbatim
            // and let `HttpClient` reject it, and this branch has to be
            // byte-for-byte that (GP 11).
            true
        else
            match Uri.TryCreate(url, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttps -> true
            | true, uri when uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback -> true
            | _ -> false

    /// The refusal wording, single-sourced so the transport, the
    /// handshake and the registry all say the same thing and name the
    /// same lever. Echoes the URL because it is operator-supplied
    /// configuration the operator has to go and fix — unlike a remote
    /// response body, which `PeerRemoteProfile.refusalMessage`
    /// deliberately does not echo.
    let refusalMessage (url: string) =
        $"{RefusalPrefix}: '{url}' is not https and is not a loopback host, so the peer bearer token minted for this call would travel in cleartext. A peer token vouches for the whole deployment, so one observation on the path is peer impersonation until the signing key rotates. Use an https:// BaseUrl for this peer; for a genuinely local dev peer use http://localhost, which is accepted as-is. PeerTransportPolicy.allowInsecureTransport (PeerServerApp.withInsecurePeerTransport) restores the pre-339 behaviour."

    /// The refusal as the `PeerError` an outbound call reports.
    let refused (url: string) : PeerError = PeerTransport(refusalMessage url)

    /// Did this error come from the cleartext refusal rather than from
    /// the peer or from a deadline? The supported way to read it — no
    /// caller has to know the wording.
    let isRefusal (error: PeerError) : bool =
        match error with
        | PeerTransport message -> message.StartsWith(RefusalPrefix, StringComparison.Ordinal)
        | _ -> false

    /// Gate one outbound URL. `Ok ()` ⇒ send it; `Error` ⇒ the call is
    /// refused *before* a token is minted, which is the difference
    /// between this and a check further down the stack.
    let check (policy: PeerTransportPolicy) (url: string) : Result<unit, PeerError> =
        if isAcceptablePeerUrl policy url then
            Ok()
        else
            Error(refused url)

type IPeerClient =
    /// Post a single peer call and await its immediate result. Used for
    /// `Immediate`-lifetime methods and for the first leg of a
    /// `LongRunning` call (which returns the backing `PeerJobId`,
    /// serialised). Returns the serialised method result, or a
    /// `PeerError` for transport, auth, version, or handler failures.
    ///
    /// **Cancellation (Phase 312).** Two tokens reach the socket and
    /// either aborts the request in flight rather than orphaning it:
    /// the workflow's own ambient token (`Async.CancellationToken` —
    /// this is the reach `IPeerFanout` and `IRoundOrchestrator` rely on,
    /// since both dispatch their peer calls under their own source), and
    /// the optional `cancellationToken`, for a caller that *holds* a
    /// token without running under it (an ASP.NET handler's
    /// `RequestAborted`, say). Either firing completes the computation
    /// as **cancelled** — not as `Ok`, not as `Error`. A cancelled call
    /// is not an answer, and returning one as a value would let it count
    /// toward a fan-out quorum or a round's fold.
    ///
    /// The parameter is optional so every pre-312 call site compiles
    /// unchanged (GP 11); omitting it leaves the ambient token as the
    /// only source, which is what pre-312 callers already had.
    abstract Invoke:
        target: TargetPeer *
        contractId: string *
        methodName: string *
        payload: PeerWirePayload *
        ?cancellationToken: CancellationToken ->
            Async<Result<string, PeerError>>

    /// Poll the target peer for the current status of a long-running
    /// call's backing job. `Completed` carries the serialised result
    /// string (the typed proxy deserialises it into `'T`); `Failed`
    /// carries the structured peer error; `Pending` means no terminal
    /// result yet. A transport-level failure of the poll itself is the
    /// outer `Error`.
    ///
    /// `cancellationToken` behaves exactly as it does on `Invoke` — a
    /// poll is an ordinary bounded request, and abandoning a poll loop
    /// should not leave its last request on the wire.
    abstract PollJob:
        target: TargetPeer * contractId: string * jobId: PeerJobId * ?cancellationToken: CancellationToken ->
            Async<Result<PeerJobStatus<string>, PeerError>>
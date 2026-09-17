// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Companions.Isolation

open System

// ─── Phase 687 — the native-boundary isolation seam ──────────────────
//
// A native companion (Tesseract + Leptonica behind `IOcrProvider`,
// SkiaSharp behind the asset store's `IDerivativeRenderer`, Verovio /
// Kuzu / Manifold behind their own wrappers) is memory-unsafe C/C++
// reached through P/Invoke. When it parses HOST-AUTHORED input that is
// a maintenance cost; when it parses UNTRUSTED bytes — an upload, a
// connector payload, a knowledge-base document — a parser bug in the
// native layer is in-process code execution, and nothing above the FFI
// helps once control has crossed it. Phase 515's content scan is
// upstream of this and complementary: a scan-clean, well-formed-looking
// file can still exploit a memory-safety bug in the parser that
// consumes it.
//
// This seam moves the native call OUT OF THE HOST PROCESS for exactly
// those paths. The contract is deliberately narrow — args + bytes in,
// bytes + typed error out — so that a worker crash is a value the
// caller matches on (`IsolationRefusal.WorkerCrashed`) rather than the
// host process disappearing. In-process remains the default for
// trusted input (GP 11 / GP 13): a deployment that composes nothing
// here pays nothing and changes nothing.
//
// What this seam bounds and what it does NOT: it bounds the blast
// radius of a native crash, the wall-clock a native parse may take,
// and — softly, see `IsolationLimits.MemoryCap` — the memory it may
// consume. It does not sandbox the worker's system access (the child
// runs as the same OS user on the same host); a deployment that needs
// that composes the worker inside a container or under an OS sandbox,
// which is the Phase 478 `Isolated` execution profile's territory, not
// this seam's.

/// One unit of work handed across the isolation boundary: the
/// entry point's arguments, and the bytes it parses. Both cross the
/// pipe verbatim; neither is interpreted by the seam.
type IsolatedRequest = {
    /// Free-form arguments the entry point reads (an input format, a
    /// language, a resource path). Strings only — anything richer is
    /// the entry point's own encoding to choose.
    Args: string list
    /// The bytes the native code will parse. Typically the untrusted
    /// payload itself.
    Input: byte[]
}

/// The unit of native work a companion exposes to the seam. One public
/// parameterless-constructible type per operation; the worker
/// instantiates it by name and calls `Invoke` once per request.
///
/// The type name is what crosses the pipe, so an entry point must be
/// public and must have a public parameterless constructor — the seam
/// cannot serialise a closure, and would not want to: the point is that
/// the worker rebuilds what it needs (a toolkit, an engine, a renderer)
/// from the request alone.
///
/// `Invoke` runs INSIDE the worker. A native crash there kills the
/// worker and surfaces to the host as `WorkerCrashed`; a managed
/// exception is caught by the worker and surfaces as `EntryFailed`;
/// `Error` is the entry point's own typed rejection (a parse the native
/// code refused cleanly) and surfaces as `EntryFailed` too, carrying the
/// message verbatim.
type IIsolatedEntryPoint =
    /// Do the work. Runs in the worker process for out-of-process
    /// isolation, or in the caller's process for the in-process default
    /// — the entry point cannot tell which, and must not care.
    abstract Invoke: request: IsolatedRequest -> Result<byte[], string>

/// Why an isolated call did not return a result. Discriminated so a
/// caller can route each one — a crash is a security event worth an
/// audit line, a timeout is a bounded-work outcome, an entry failure is
/// the companion's ordinary rejection.
[<RequireQualifiedAccess>]
type IsolationRefusal =
    /// The worker process died before answering — the native code
    /// crashed (access violation, abort, fail-fast) or was killed by
    /// the OS. `exitCode` is the process exit code; `diagnostic` is the
    /// tail of the worker's stderr, bounded, for the operator. The
    /// host process is unaffected, which is the whole point.
    | WorkerCrashed of exitCode: int * diagnostic: string
    /// The worker did not answer within `IsolationLimits.Timeout` and
    /// was killed.
    | TimedOut of limit: TimeSpan
    /// The worker exceeded `IsolationLimits.MemoryCap` and was killed.
    /// `observed` is whichever line tripped: when the kernel refused the
    /// commit that would have crossed the cap (a Windows Job Object
    /// limit) it is the peak commit charge the job recorded — at or just
    /// under the cap, by construction; when the host's resident-set
    /// sampler tripped first (always, off Windows; on Windows too when
    /// shared pages push the working set over a cap the private commit
    /// has not reached) it is the sample that did, above the cap.
    | MemoryCapExceeded of cap: int64 * observed: int64
    /// The entry point rejected the request (its own `Error`, or a
    /// managed exception it raised). Not a crash: the worker answered
    /// normally and this is the companion's typed rejection carried
    /// across the pipe.
    | EntryFailed of message: string
    /// The worker answered with bytes the protocol does not recognise,
    /// or ended the stream mid-frame while exiting cleanly. Usually a
    /// launcher override that started something other than the worker,
    /// or native code writing to the worker's stdout.
    | ProtocolViolation of detail: string
    /// No worker could be started: the launcher could not be resolved
    /// (no `dotnet` host, no `deps.json` / `runtimeconfig.json` beside
    /// the entry assembly) or the OS refused the start.
    | WorkerUnavailable of reason: string

[<RequireQualifiedAccess>]
module IsolationRefusal =
    /// Stable lowercase label for logs / audit payloads / metrics.
    let label =
        function
        | IsolationRefusal.WorkerCrashed _ -> "worker-crashed"
        | IsolationRefusal.TimedOut _ -> "timed-out"
        | IsolationRefusal.MemoryCapExceeded _ -> "memory-cap-exceeded"
        | IsolationRefusal.EntryFailed _ -> "entry-failed"
        | IsolationRefusal.ProtocolViolation _ -> "protocol-violation"
        | IsolationRefusal.WorkerUnavailable _ -> "worker-unavailable"

    /// One-line operator-facing description.
    let describe =
        function
        | IsolationRefusal.WorkerCrashed(exitCode, diagnostic) ->
            let tail =
                if String.IsNullOrWhiteSpace diagnostic then
                    ""
                else
                    $" — {diagnostic.Trim()}"

            $"the isolation worker crashed (exit code {exitCode}){tail}"
        | IsolationRefusal.TimedOut limit -> $"the isolation worker did not answer within {limit} and was killed"
        | IsolationRefusal.MemoryCapExceeded(cap, observed) ->
            $"the isolation worker exceeded its memory cap ({observed} bytes observed against a cap of {cap}) and was killed"
        | IsolationRefusal.EntryFailed message -> $"the entry point rejected the request: {message}"
        | IsolationRefusal.ProtocolViolation detail -> $"the isolation worker answered outside the protocol: {detail}"
        | IsolationRefusal.WorkerUnavailable reason -> $"no isolation worker could be started: {reason}"

/// The bounds an out-of-process call runs under.
type IsolationLimits = {
    /// Wall-clock bound on one call, worker start-up included. Past it
    /// the worker is killed and the call returns `TimedOut`.
    Timeout: TimeSpan
    /// Memory bound on the worker, in bytes. `None` = unbounded.
    ///
    /// **Three lines, and which one holds depends on the host.** On
    /// Windows the worker runs inside a Job Object whose per-process
    /// and job-wide commit limits are this figure: the KERNEL refuses
    /// the allocation that would cross it, in the allocating thread,
    /// so a native parser cannot overshoot — the recorded instance
    /// this guards against is a MusicXML `<forward>` with a duration
    /// of 2^31-1 that a native parser allocated against, unbounded, to
    /// 126 GB in-process. A host that cannot apply the job refuses the
    /// call (`WorkerUnavailable`) rather than run under the softer
    /// lines alone. Everywhere, the host also samples the worker's
    /// resident set every few milliseconds and kills it on the first
    /// sample over the cap (a poll, so a burst can overshoot between
    /// samples), and the worker's MANAGED heap is hard-limited to the
    /// same figure through the runtime's own `GCHeapHardLimit`. On a
    /// non-Windows host those two are the whole cap; a hard bound on
    /// native allocation there is the container runtime's cgroup.
    /// Set it comfortably above the runtime's own baseline (tens of
    /// megabytes) or every call is refused.
    MemoryCap: int64 option
}

[<RequireQualifiedAccess>]
module IsolationLimits =
    /// Thirty seconds, 512 MiB — generous enough for a large document,
    /// tight enough that a pathological one cannot hold a slot for
    /// long.
    let defaults = {
        Timeout = TimeSpan.FromSeconds 30.0
        MemoryCap = Some(512L * 1024L * 1024L)
    }

/// How the worker process is started. Resolved automatically from the
/// host's own `deps.json` / `runtimeconfig.json` (see
/// `ProcessIsolation.resolveLauncher`); overridable for a deployment
/// whose layout the resolver cannot see (a single-file publish, a
/// custom host).
type WorkerLauncher = {
    /// The executable to start — normally the `dotnet` muxer.
    FileName: string
    /// Its arguments, verbatim. The worker itself reads no arguments
    /// beyond the `--worker` flag; everything it needs arrives on stdin.
    Arguments: string list
}

/// Where a companion's native call runs. The composition-config value
/// the seam is selected by.
[<RequireQualifiedAccess>]
type IsolationMode =
    /// In the caller's process — the pre-687 behaviour, byte-for-byte.
    /// No timeout, no cap, no crash containment: native code that
    /// faults takes the host with it. Correct for trusted,
    /// host-authored input; wrong for anything a user uploaded.
    | InProcess
    /// In a sacrificial child process under `IsolationLimits`, with the
    /// worker resolved automatically.
    | OutOfProcess of IsolationLimits
    /// As `OutOfProcess`, with an explicit launcher instead of the
    /// resolved one.
    | OutOfProcessWith of IsolationLimits * WorkerLauncher

/// The seam. One method: run an entry point over a request, and answer
/// with its bytes or a typed refusal. Async at the boundary (GP 12 rule
/// 2); identity by value — the entry point crosses as a `Type`, whose
/// assembly-qualified name is what the pipe carries (rule 1).
type ICompanionIsolation =
    /// The mode this instance was composed with, so a caller (or a
    /// preflight) can read what it is actually getting.
    abstract Mode: IsolationMode
    /// Run `entry` — a public type implementing `IIsolatedEntryPoint`
    /// with a public parameterless constructor — over `request`.
    abstract Run: entry: Type * request: IsolatedRequest -> Async<Result<byte[], IsolationRefusal>>
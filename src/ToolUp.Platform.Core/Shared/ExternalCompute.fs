// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 318 — external-compute substrate: core types ─────────────────
//
// The missing abstraction: `IJobHandler.Execute` runs **in-process** on the
// web server, so there is no SDK concept of "run this on a machine other
// than the one serving the request". These types are that concept's value
// vocabulary — a submitted unit of work, an opaque handle to it, and the
// terminal outcome a poller reads.
//
// **The dispatcher brokers; it never runs.** A handler hands a
// `ExternalWorkSpec` to `IExternalComputeDispatcher.Submit`, receives an
// `ExternalHandle` immediately, and polls to a terminal `ExternalOutcome`.
// Nothing here executes heavy work, and nothing here describes how a
// backend schedules it — that is deliberately the backend's business.
//
// **No backend SDK types, by construction.** Every type below is BCL
// primitives + F# records/DUs, so the file ships in the Fable-packed source
// and a Client can hold an `ExternalHandle` opaquely (GP 10 + GP 12 rule 1).
// `NativeRef` is the one place a backend's own token lives, and it lives
// there as an opaque `string` the platform never parses.
//
// **Six portability rules (GP 12) — the type side.**
// 1. *Identity by value* — `ExternalHandle` is a record of `Guid` + strings
//    + `DateTime`; never a live job object, cluster reference, or SDK
//    client handle.
// 2. *Async at every boundary* — see `IExternalComputeDispatcher`
//    (Platform.Server); no type here carries a callback or a `Task`.
// 3. *Retry + supervision as data* — `ExternalWorkSpec.Timeout` and
//    `Idempotency` are fields, and a failure is a `ExternalComputeError`
//    record carrying `Retriable` as data. There is no `OnFailure` callback
//    and no exception path for a backend-reported failure.
// 4. *Stateless between invocations* — a spec carries its whole payload, so
//    a re-submitted spec needs nothing the dispatcher cached.
// 5. *No cross-shard ordering* — handles are independent; nothing here
//    promises submission order across two handles.
// 6. *Precision at the lower bound* — `Timeout` is a `TimeSpan` the backend
//    interprets as advisory-with-a-floor; `Running` progress is `float
//    option` precisely because a backend that cannot report progress says
//    `None` rather than inventing a number.

/// A structured external-compute failure. Retriability is **data**, not an
/// exception type (GP 12 rule 3) — a caller decides whether to re-submit by
/// reading `Retriable`, so the decision serialises across the wire and
/// survives a process restart.
type ExternalComputeError = {
    /// Operator-facing description of the failure. Backends should name
    /// themselves and the failing stage; never embed a credential.
    Message: string
    /// `true` when re-submitting the identical spec could plausibly
    /// succeed (transient backend saturation, a lease expiry, a network
    /// blip). `false` for a terminal refusal (a malformed payload, a
    /// backend that is not configured, an unknown `Kind`).
    Retriable: bool
}

module ExternalComputeError =
    /// A retriable failure — re-submitting the identical spec may succeed.
    let retriable (message: string) : ExternalComputeError = { Message = message; Retriable = true }

    /// A terminal failure — re-submitting the identical spec cannot help.
    let terminal (message: string) : ExternalComputeError = { Message = message; Retriable = false }

    /// The refusal a deployment with no external-compute backend composed
    /// returns from every `Submit` (GP 13 — the `NoExternalCompute`
    /// default). Terminal by construction: no amount of retrying composes
    /// a backend.
    let notConfigured: ExternalComputeError =
        terminal
            "No external-compute backend is configured. ServerConfig.ExternalCompute = NoExternalCompute (the default); set CustomExternalCompute and register an IExternalComputeDispatcher companion singleton to submit external work."

    /// One-line description for logs / audit payloads.
    let describe (error: ExternalComputeError) : string =
        if error.Retriable then
            sprintf "%s (retriable)" error.Message
        else
            sprintf "%s (terminal)" error.Message

/// An opaque handle to one unit of work accepted by an external compute
/// backend. Identity by value (GP 12 rule 1) — every field is a primitive,
/// so the same handle resolves from any node, survives a restart, and can
/// be persisted or handed to a Fable client without the client learning
/// anything about the backend.
type ExternalHandle = {
    /// Platform-minted identity for this submission. Stable for the life of
    /// the work; the value the platform keys its own records on.
    HandleId: Guid
    /// Stable identifier of the backend that accepted the work (the
    /// dispatcher's `Backend`, e.g. `"http-worker-pool"`). Recorded on the
    /// handle so a poll routes to the same backend that submitted.
    Backend: string
    /// Scope the work was submitted under (GP 4 — tenant isolation rides
    /// the handle, so a poll cannot read across tenants by construction).
    ScopeId: string
    /// The backend's own token for the work — a queue receipt, a job name,
    /// a URL fragment. **Opaque**: the platform stores and echoes it and
    /// never parses, validates, or derives meaning from it.
    NativeRef: string
    /// When the platform accepted the submission (UTC).
    SubmittedAt: DateTime
}

/// One unit of work to hand to an external compute backend. The payload is
/// **pre-serialised JSON** the backend understands: forge is the broker and
/// never interprets a domain payload, exactly as `IModelFitProvider` treats
/// an opaque spec.
type ExternalWorkSpec = {
    /// Backend-resolved work discriminator (e.g. `"train-forecast"`,
    /// `"render-scene"`). The backend maps `Kind` to whatever it runs; an
    /// unrecognised `Kind` is a terminal `ExternalComputeError`.
    Kind: string
    /// The work's own payload, already serialised to JSON by the caller.
    /// Opaque to the platform — never parsed, normalised, or re-hashed.
    Payload: string
    /// **Advisory** resource requests the backend interprets in its own
    /// vocabulary — e.g. `"gpu" -> "1"`, `"memory" -> "16Gi"`,
    /// `"accelerator" -> "a100"`. Deliberately a string map rather than a
    /// typed record: any typed shape would encode one scheduler's
    /// vocabulary and stop being portable (GP 12 rule 1). A backend that
    /// does not understand a hint ignores it; a backend that cannot honour
    /// a hint it *does* understand refuses with a terminal error rather
    /// than silently downgrading.
    ResourceHints: Map<string, string>
    /// Advisory wall-clock budget. `None` leaves the backend's own default
    /// in force. Precision is the backend's lower bound (GP 12 rule 6) — a
    /// backend whose scheduler ticks per minute documents that; no
    /// sub-second promise is implied.
    Timeout: TimeSpan option
    /// Caller-minted idempotency key. When `Some`, a backend that has
    /// already accepted this key for this scope returns the **existing**
    /// handle rather than starting the work twice. `None` means every
    /// `Submit` is a fresh submission.
    Idempotency: string option
}

module ExternalWorkSpec =
    /// A spec with no resource hints, no timeout, and no idempotency key —
    /// the minimum shape.
    let create (kind: string) (payload: string) : ExternalWorkSpec = {
        Kind = kind
        Payload = payload
        ResourceHints = Map.empty
        Timeout = None
        Idempotency = None
    }

    /// Add (or overwrite) one advisory resource hint.
    let withHint (key: string) (value: string) (spec: ExternalWorkSpec) : ExternalWorkSpec = {
        spec with
            ResourceHints = spec.ResourceHints |> Map.add key value
    }

    /// Declare an advisory wall-clock budget.
    let withTimeout (timeout: TimeSpan) (spec: ExternalWorkSpec) : ExternalWorkSpec = {
        spec with
            Timeout = Some timeout
    }

    /// Declare a caller-minted idempotency key, so a re-`Submit` of the
    /// identical spec returns the existing handle.
    let withIdempotency (key: string) (spec: ExternalWorkSpec) : ExternalWorkSpec = { spec with Idempotency = Some key }

/// The state of one submitted unit of work, as read by `Poll`.
/// `[<RequireQualifiedAccess>]` for namespace hygiene — `Pending` /
/// `Running` / `Succeeded` / `Failed` / `Cancelled` are already case names
/// on `JobStatus`, `IngestionStatus` and the remoting long-running envelope,
/// and an unqualified fifth set in the same namespace would shadow them
/// (the `DatasetTypes` / `CompanionCapability` precedent).
///
/// Three terminal cases (`Succeeded` / `Failed` / `Cancelled`) and two
/// non-terminal (`Pending` / `Running`) — see `ExternalOutcome.isTerminal`.
[<RequireQualifiedAccess>]
type ExternalOutcome =
    /// Accepted by the backend, not yet started (queued / awaiting a slot).
    | Pending
    /// Executing. `progress` is a `0.0 .. 1.0` fraction when the backend
    /// reports one, and `None` when it cannot — a backend never invents a
    /// number to fill the field.
    | Running of progress: float option
    /// Terminal success. `resultRef` is an opaque backend reference to the
    /// result (a blob key, an artefact URI, a content hash); the platform
    /// echoes it and never dereferences or parses it.
    | Succeeded of resultRef: string
    /// Terminal failure, carrying the structured error (retriability as
    /// data — GP 12 rule 3).
    | Failed of ExternalComputeError
    /// Terminal cancellation, whether requested via `Cancel` or decided by
    /// the backend (pre-emption, a timeout, an operator action).
    | Cancelled

module ExternalOutcome =
    /// `true` for the three terminal cases. A poller stops on terminal;
    /// `Pending` / `Running` mean poll again.
    let isTerminal =
        function
        | ExternalOutcome.Succeeded _
        | ExternalOutcome.Failed _
        | ExternalOutcome.Cancelled -> true
        | ExternalOutcome.Pending
        | ExternalOutcome.Running _ -> false

    /// Stable lowercase label for logs / audit payloads / dev panels.
    let label =
        function
        | ExternalOutcome.Pending -> "pending"
        | ExternalOutcome.Running _ -> "running"
        | ExternalOutcome.Succeeded _ -> "succeeded"
        | ExternalOutcome.Failed _ -> "failed"
        | ExternalOutcome.Cancelled -> "cancelled"

/// Phase 318 — selects the external-compute substrate
/// (`IExternalComputeDispatcher`). Default: `NoExternalCompute` — the
/// `NoExternalComputeDispatcher` is registered, so the seam always resolves
/// and every `Submit` returns `ExternalComputeError.notConfigured`; no
/// background service runs and no dependency is pulled (GP 13). Mirrors
/// `TelemetrySinkMode` / `DatasetStoreMode` (no-op default + custom
/// companion), not `ModelFittingMode` (binary enable) — the seam has a
/// meaningful, resolvable default and the "enabled" form is always a
/// companion.
type ExternalComputeMode =
    /// No backend composed (default). `IExternalComputeDispatcher` resolves
    /// to `NoExternalComputeDispatcher`, whose `Submit` returns a clean
    /// not-configured `Error`. A bare `ServerApp.empty |> ServerApp.run`
    /// builds + runs unchanged (GP 11 + GP 13).
    | NoExternalCompute
    /// A companion under `src/ExternalCompute/` (an HTTP worker pool, a
    /// container/batch backend, a future managed service) is registered in
    /// DI by the deployment; `compose` registers no default and leaves the
    /// consumer's `IExternalComputeDispatcher` singleton in place.
    | CustomExternalCompute
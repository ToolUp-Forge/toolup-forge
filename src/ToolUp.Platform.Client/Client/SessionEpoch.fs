// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SessionEpoch

open System
open System.Collections.Generic
open Fable.Core
open Fable.SimpleJson
open ProcessedDataTypes

// ─── Session-store reconciliation (Phase 6p) ────────────────────
//
// The server's `SessionFileStore` can be emptied underneath a live tab
// without anything on the client noticing: the ephemeral-store TTL
// evicts it after an idle gap, a dev `dotnet watch` cycle restarts the
// process, a scope container changes. The Elmish file list stays
// rendered, and the user learns about the loss only when a downstream
// module call returns "File 'X' not found in session. No files have been
// uploaded in this session." — by which point an interactive analysis is
// half-finished and the only recovery is refresh and re-upload.
//
// This module is the client half of the fix. It caches the epoch the
// server minted for the store it is talking to, compares it on the
// cheap occasions where a stale list would be discovered anyway (mount,
// tab refocus, SSE reconnect), and fans out ONE reconciliation event
// that every holder of file-derived state subscribes to.
//
// Sanctioned mutable global + listener registry — same precedent as
// `NavigationRequest`, `ModuleStateObserver` and `NotificationClient`.
// Per-tab singleton; the shell subscribes at boot for the lifetime of
// the React tree.
//
// GP 11 — a deployment that never evicts is unchanged apart from the
// one `GetSessionInfo` call on mount. Every other path here is driven by
// an event that only fires when something was genuinely lost: the
// focus/reconnect checks compare against a cached epoch and return
// without a dispatch when it matches, and the notification arm only runs
// when the server published one.

let private log = Logger.forCategory "client.session-epoch"

/// File-management proxy for the epoch reads. A second proxy rather than
/// a shared one is the established shape here (`FileManagerUI`,
/// `MappingDataManagerUI` and `SDK.Client` each make their own) — header
/// freshness is the `CsrfClient` request guard's job, not the proxy's.
let private fileApi: FileManagementApi =
    Api.makeProxy<FileManagementApi> (customOptions = UserSession.withRequestHeaders)

/// The epoch this tab believes it is talking to. `None` before the first
/// successful read — the pre-mount state, in which nothing can be
/// detected because there is nothing to compare against.
let mutable private cachedEpoch: Guid option = None

let private listeners = List<SessionStoreResetNotification -> unit>()

// `gate` guards every read/write of `listeners`, for the reason
// `NavigationRequest` documents: in the browser `lock` compiles to a
// plain call of the body and costs nothing, and on .NET it keeps the
// registry safe under Expecto's parallel runner.
let private gate = obj ()

/// The epoch this tab last observed, if any. Exposed for tests and for a
/// consumer that wants to render it in a diagnostics panel; the
/// reconciliation paths below do not require callers to read it.
let current () : Guid option = cachedEpoch

/// Drop the cached epoch. Called by the shell's `TeamSwitched` reset,
/// where the scope itself moved: the next store is legitimately a
/// different one, so comparing against the previous scope's epoch would
/// report a reset that did not happen. `LocaleSwitched` deliberately does
/// NOT call this — it changes the language, not the scope, and the store
/// on the other side of it is the same store.
let reset () : unit = cachedEpoch <- None

/// Subscribe to session-store resets. Returns a dispose thunk. Every
/// holder of file-derived client state — the shell's prefetched
/// processed data, the Data Manager's file list — subscribes here, so
/// the three detection paths below converge on one handler each rather
/// than each path having to know every holder.
let subscribe (callback: SessionStoreResetNotification -> unit) : unit -> unit =
    lock gate (fun () -> listeners.Add(callback))
    fun () -> lock gate (fun () -> listeners.Remove(callback) |> ignore)

/// Fan out to every subscriber against a snapshot, so a callback that
/// (un)subscribes during delivery cannot disturb the iteration. A
/// throwing subscriber is logged and skipped — one module failing to
/// clear its list must not stop the others clearing theirs.
let private fanOut (notice: SessionStoreResetNotification) =
    let snapshot = lock gate (fun () -> listeners.ToArray())

    for cb in snapshot do
        try
            cb notice
        with ex ->
            try
                log.Warn $"subscriber swallowed: {ex.Message}"
            with _ ->
                ()

/// Record an epoch read from the server and report whether it is a
/// RESET relative to what this tab was holding.
///
/// A first read is never a reset: there was no prior state to invalidate,
/// which is the same reason the server does not announce a fresh
/// first access. Only a cached epoch that differs from the observed one
/// means the store this tab was talking to is gone.
let private observe (epoch: Guid) : bool =
    let isReset =
        match cachedEpoch with
        | Some previous -> previous <> epoch
        | None -> false

    cachedEpoch <- Some epoch
    isReset

/// Is the tab currently in the foreground?
///
/// Read as a raw string rather than through the typed `VisibilityState`
/// enum, for the reason `Offline.SyncCoordinator` documents: the enum's
/// shape has moved between `Fable.Browser.Dom` majors, and this
/// comparison is the one thing that must not break when the binding is
/// bumped. The `typeof` guard keeps it honest in a non-DOM host (SSR
/// prerender, a .NET-side test), where it answers `false` rather than
/// throwing.
[<Emit("(typeof document !== 'undefined' && document !== null && document.visibilityState === 'visible')")>]
let documentVisible () : bool = jsNative

/// Apply the server's own `Platform.SessionStoreReset` notification.
/// The server publishes this on eviction-then-recreate, so the epoch it
/// carries is authoritative and no round trip is needed. Idempotent
/// against the polling paths: adopting `CurrentEpoch` here means a
/// focus-triggered check moments later observes a match and dispatches
/// nothing.
let applyServerNotice (notice: SessionStoreResetNotification) : unit =
    let alreadyKnown = cachedEpoch = Some notice.CurrentEpoch
    cachedEpoch <- Some notice.CurrentEpoch

    if not alreadyKnown then
        fanOut notice

/// Parse and apply a raw `SessionStoreResetKey` payload straight off the
/// notification stream. The parse lives here rather than at the shell's
/// subscription so the payload shape has exactly one reader — the same
/// reason the epoch cache is not a shell field.
///
/// A payload this client cannot parse is a server/client version skew,
/// not evidence that the store was reset: it is logged and dropped, and
/// the polling paths below still catch the real thing.
let applyServerNoticeJson (payloadJson: string) : unit =
    try
        Json.parseAs<SessionStoreResetNotification> payloadJson |> applyServerNotice
    with ex ->
        try
            log.Warn $"unparseable reset payload dropped: {ex.Message}"
        with _ ->
            ()

/// Read the server's epoch and reconcile against the cache. Returns the
/// reset notice when the store changed underneath this tab, `None`
/// otherwise — including on the first call, which merely seeds the cache.
///
/// Subscribers have already been fanned out to by the time this returns,
/// so a caller that only wants the side effect can ignore the result.
///
/// A failed read returns `None` and leaves the cache untouched: a
/// transient network failure is not evidence that the store was reset,
/// and guessing would clear the user's file list on a flaky connection.
let check () : Async<SessionStoreResetNotification option> = async {
    try
        let! info = fileApi.GetSessionInfo()
        let previous = cachedEpoch

        if observe info.Epoch then
            // The server could not have told us the reason — a restart
            // takes the channel down with it, and an eviction we learned
            // about by polling means the notification did not reach us.
            // Either way what the user needs is the same reconciliation.
            let notice: SessionStoreResetNotification = {
                Container = ""
                PreviousEpoch = previous
                CurrentEpoch = info.Epoch
                Reason = SessionStoreResetReasonProcessRestart
            }

            fanOut notice
            return Some notice
        else
            return None
    with ex ->
        try
            log.Warn $"epoch check failed: {ex.Message}"
        with _ ->
            ()

        return None
}

/// Seed the cache on mount without treating the first read as a reset.
/// Identical to `check` in effect — `observe` already returns `false` on
/// a `None` cache — and named separately so the boot call site reads as
/// what it is.
let prime () : Async<unit> = async {
    let! _ = check ()
    return ()
}

/// Raised by `preflight` when the epoch moved. Carries a user-facing
/// message rather than the server's per-file "File 'X' not found in
/// session", so a call site that already renders `ex.Message` surfaces
/// the cause of the failure instead of one of its symptoms.
exception SessionStoreResetException of string

/// Pre-flight guard for a data-consuming call — anything whose server
/// handler reads the session file store (a module's analysis API, a
/// content read, a reprocess).
///
/// Compares the epoch first; on a mismatch it fans out the reconciliation
/// and raises `SessionStoreResetException` INSTEAD of dispatching the
/// call, so the user's visible failure is the same toast the focus path
/// produces and never the Fable.Remoting "File 'X' not found" path. On a
/// match — the overwhelmingly common case — the work runs unchanged.
///
/// Consumers wrap the call rather than the proxy:
///
/// ```fsharp
/// Cmd.OfRemoting.call
///     (fun args -> SessionEpoch.preflight (myApi.RunPriceElasticity args))
///     args
///     Finished
///     (fun ex -> ApiError ex.Message)
/// ```
///
/// A failed epoch READ does not block the call: `check` swallows it and
/// reports no reset, so a flaky connection degrades to the pre-6p
/// behaviour (the call goes out and the server answers) rather than
/// locking the user out of their own data.
let preflight (work: Async<'T>) : Async<'T> = async {
    let! reset = check ()

    match reset with
    | Some _ ->
        return
            raise (
                SessionStoreResetException
                    "Server session was reset; please re-upload your files before running this again."
            )
    | None -> return! work
}
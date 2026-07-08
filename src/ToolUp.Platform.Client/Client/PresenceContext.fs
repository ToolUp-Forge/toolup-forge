// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PresenceContext

open Fable.Core
open Feliz
open ToolUp.Platform

// ─── Presence + soft-lock React context — Phase 442 ──────────────────
//
// The declarative client half of the presence + soft-lock substrate:
// module views read "who else is here" and "is this entity locked, and
// by whom" from a React context provided at the root of the view tree —
// the same idiomatic path as `ProcessedDataContext` /
// `LoadingIndicatorContext`. The DEPLOYMENT mounts `provider` (the SDK
// shell does not auto-mount it) and keeps the value fresh by subscribing
// to the reserved `_platform.presence` / `_platform.lock` notification
// keys (Phase 6a SSE bridge) and re-providing on each roster / lock
// change; module views never poll and never thread the state through
// props.
//
// The `useEntityLock` helper hook wraps the acquire-on-edit /
// release-on-blur lifecycle with auto-renew — the ergonomic path for a
// field that should take an advisory lock while focused. It is
// transport-parameterised (the consumer supplies how acquire / renew /
// release reach the server, typically a Remoting API over
// `IEntityLockStore`), so it stays neutral about the wire.
//
// Opt-in (GP 13): outside a provider the hooks return empty / no-lock,
// so an isolated render (tests, a deployment on `NoPresence`) degrades
// cleanly to "nobody here, nothing locked".

[<Emit("setInterval($0, $1)")>]
let private setInterval (cb: unit -> unit) (ms: int) : int = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval (handle: int) : unit = jsNative

/// The value the shell provides: the live scope roster plus the current
/// lock leases keyed by `EntityLockRef.toKey`. Both default empty so a
/// render outside a provider is well-defined.
type PresenceContextValue = {
    Peers: PresencePeer list
    Locks: Map<string, LockLease>
}

module PresenceContextValue =
    let empty: PresenceContextValue = { Peers = []; Locks = Map.empty }

    /// Build a value from a roster + lease list — the shape the shell's
    /// subscription folds each `_platform.*` event into before
    /// re-providing.
    let ofState (peers: PresencePeer list) (leases: LockLease list) : PresenceContextValue = {
        Peers = peers
        Locks = leases |> List.map (fun l -> EntityLockRef.toKey l.Ref, l) |> Map.ofList
    }

let private context =
    React.createContext<PresenceContextValue> (defaultValue = PresenceContextValue.empty)

/// Wrap children in a provider supplying the current presence + lock
/// state. Mounted by the deployment near the root of its view tree (the
/// SDK shell does not auto-mount it); module views read it via the hooks
/// below rather than threading it through props.
let provider (value: PresenceContextValue) (children: ReactElement) = context.Provider(value, children)

/// Declarative reads for module views. Each must be called from a React
/// component body (a `[<ReactComponent>]`-attributed function).
module Presence =
    /// Everyone currently present in the scope, sorted by the server.
    let usePeers () : PresencePeer list = (React.useContext context).Peers

    /// Present peers whose location is within `moduleId` — "who else is
    /// looking at this module".
    let usePeersAt (moduleId: string) : PresencePeer list =
        (React.useContext context).Peers
        |> List.filter (fun p -> p.Location.Module = moduleId)

    /// The live lock lease over `ref`, if any — surfaces the holder so a
    /// view can render "Bob is editing".
    let useLock (ref: EntityLockRef) : LockLease option =
        (React.useContext context).Locks |> Map.tryFind (EntityLockRef.toKey ref)

    /// `true` when `ref` is locked by someone *other* than `me` — the
    /// declarative "render this entity read-only" signal. Free or
    /// self-held ⇒ editable.
    let useIsReadOnly (ref: EntityLockRef) (me: string) : bool =
        match useLock ref with
        | Some lease -> lease.Holder <> me
        | None -> false

/// How the acquire-on-edit helper reaches the server. Wire each to the
/// deployment's lock endpoint (e.g. a Remoting API over
/// `IEntityLockStore`).
type LockTransport = {
    Acquire: EntityLockRef -> Async<LockOutcome>
    Renew: EntityLockRef -> Async<LockOutcome>
    Release: EntityLockRef -> Async<unit>
}

/// Handle returned by `useEntityLock`. `BeginEdit` acquires the lease
/// and, on success, starts the auto-renew timer; `EndEdit` releases and
/// stops it. `Outcome` is the latest acquire / renew result — `None`
/// before the first `BeginEdit`, `Some (HeldByOther _)` when another
/// user holds it (the caller renders read-only + the holder).
type EntityLockHandle = {
    BeginEdit: unit -> unit
    EndEdit: unit -> unit
    Outcome: LockOutcome option
}

/// Acquire-on-edit / release-on-blur with auto-renew. Call `BeginEdit`
/// when a field gains focus / edit starts and `EndEdit` on blur / save;
/// while held, the lease is renewed every `renewIntervalMs` so it never
/// lapses mid-edit. The lease is released automatically on unmount so a
/// navigated-away editor never strands a lock (it also lapses on its own
/// TTL server-side — no background sweeper needed, GP 13). Must be
/// called from a React component body.
let useEntityLock (transport: LockTransport) (ref: EntityLockRef) (renewIntervalMs: int) : EntityLockHandle =
    let outcome, setOutcome = React.useState (None: LockOutcome option)
    let timer = React.useRef (None: int option)

    let stopTimer () =
        match timer.current with
        | Some h ->
            clearInterval h
            timer.current <- None
        | None -> ()

    let beginEdit () =
        Async.StartImmediate(
            async {
                let! o = transport.Acquire ref
                setOutcome (Some o)

                match o with
                | LockOutcome.Acquired _ ->
                    stopTimer () // never stack two renew timers

                    let renew () =
                        Async.StartImmediate(
                            async {
                                let! r = transport.Renew ref
                                setOutcome (Some r)
                            }
                        )

                    timer.current <- Some(setInterval renew renewIntervalMs)
                | LockOutcome.HeldByOther _ -> () // did not get the lock — no renew
            }
        )

    let endEdit () =
        stopTimer ()
        Async.StartImmediate(transport.Release ref)
        setOutcome None

    // Release the timer (and, best-effort, the lease) on unmount — the
    // setup returns a cleanup thunk (the `unit -> unit -> unit`
    // useEffectOnce overload).
    React.useEffectOnce (fun () ->
        fun () ->
            stopTimer ()
            Async.StartImmediate(transport.Release ref))

    {
        BeginEdit = beginEdit
        EndEdit = endEdit
        Outcome = outcome
    }
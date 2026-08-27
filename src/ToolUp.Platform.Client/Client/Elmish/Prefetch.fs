// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

/// Status of a single prefetch — three terminal states: still loading,
/// successfully loaded, or failed with an exception.
[<RequireQualifiedAccess>]
type PrefetchStatus<'a> =
    | Pending
    | Loaded of 'a
    | Failed of exn

/// `Prefetch<'a>` — wraps an async-loaded value with explicit lifecycle.
/// Codifies the "load N config sources at startup, fire one re-init when
/// the last one resolves" pattern `SDK.Client` uses at boot
/// and on team-switch fan-in.
///
/// Replaces the manual `IsConfigsPending` / `IsFlagsPending` bookkeeping
/// fields with a single typed shape — combine with `Prefetch.onAllReady`
/// (or `onAllLoaded`) to fire a command iff every supplied gate has
/// transitioned out of `Pending`.
type Prefetch<'a> = { Status: PrefetchStatus<'a> }

[<RequireQualifiedAccess>]
module Prefetch =

    /// Empty prefetch — still loading.
    let none<'a> : Prefetch<'a> = { Status = PrefetchStatus.Pending }

    /// Mark a prefetch as successfully loaded.
    let loaded (value: 'a) : Prefetch<'a> = { Status = PrefetchStatus.Loaded value }

    /// Mark a prefetch as failed.
    let failed (ex: exn) : Prefetch<'a> = { Status = PrefetchStatus.Failed ex }

    /// Reset a prefetch back to `Pending`. Used at team-switch / tenant-
    /// switch boundaries where the underlying data must be re-loaded.
    let reset<'a> (_: Prefetch<'a>) : Prefetch<'a> = none

    /// `true` if the prefetch has resolved (either successfully or with
    /// failure); `false` if still pending.
    let isComplete (gate: Prefetch<'a>) =
        match gate.Status with
        | PrefetchStatus.Pending -> false
        | _ -> true

    /// `true` if the prefetch loaded successfully.
    let isLoaded (gate: Prefetch<'a>) =
        match gate.Status with
        | PrefetchStatus.Loaded _ -> true
        | _ -> false

    /// `true` if the prefetch failed.
    let isFailed (gate: Prefetch<'a>) =
        match gate.Status with
        | PrefetchStatus.Failed _ -> true
        | _ -> false

    /// Extract the loaded value, if any.
    let value (gate: Prefetch<'a>) =
        match gate.Status with
        | PrefetchStatus.Loaded v -> Some v
        | _ -> None

    /// Extract the failure exception, if any.
    let error (gate: Prefetch<'a>) =
        match gate.Status with
        | PrefetchStatus.Failed ex -> Some ex
        | _ -> None

    /// Fire `cmd` iff every supplied prefetch flag is `true`; otherwise
    /// emit `Cmd.none`. The caller supplies pre-computed readiness flags
    /// so the function stays Fable-safe (no reflection, no boxing) and
    /// avoids the heterogeneous-list footgun.
    ///
    /// Example with two prefetches of different payload types:
    ///
    /// ```fsharp
    /// | ConfigsLoaded configs ->
    ///     let model' = { model with Configs = Prefetch.loaded configs }
    ///     model',
    ///     Prefetch.onAllReady
    ///         [ Prefetch.isComplete model'.Configs
    ///           Prefetch.isComplete model'.Flags ]
    ///         (Cmd.ofMsg ReinitActiveModule)
    /// ```
    let onAllReady (readyFlags: bool list) (cmd: Cmd<'msg>) : Cmd<'msg> =
        if List.forall id readyFlags then cmd else Cmd.none

    /// As `onAllReady`, but the caller supplies `isLoaded` flags rather
    /// than `isComplete` flags. Use when the consumer can't recover from
    /// a prefetch failure and the re-init must wait for all-success.
    ///
    /// Example:
    ///
    /// ```fsharp
    /// Prefetch.onAllLoaded
    ///     [ Prefetch.isLoaded model'.Configs
    ///       Prefetch.isLoaded model'.Flags ]
    ///     (Cmd.ofMsg ReinitActiveModule)
    /// ```
    let onAllLoaded (loadedFlags: bool list) (cmd: Cmd<'msg>) : Cmd<'msg> =
        if List.forall id loadedFlags then cmd else Cmd.none

    // ─── 0.4.3 — stall surfacing ─────────────────────────────────
    //
    // `onAllReady` is silent by design: when not all flags are ready
    // it returns `Cmd.none`. That's correct for the success path, but
    // when a prefetch hangs indefinitely (config fetch times out, a
    // background load never resolves), the gate sits forever and the
    // consumer's module stays in its seed state with no operator
    // signal. `warnIfStalled` is the opt-in observability hook —
    // consumers call it at the same site they call `onAllReady`, and
    // when the gate has been pending past `stallThreshold` it emits
    // one warning per gate-name via `Prefetch.stallReporter` (defaults
    // to `eprintfn`; consumers can override to route into structured
    // logging).

    let private stalledWarned = System.Collections.Generic.HashSet<string>()

    let mutable private stallReporter: string -> float -> int -> int -> int list -> unit =
        fun gateName elapsedSeconds pendingCount totalCount pendingIndices ->
            try
                eprintfn
                    "[Prefetch.onAllReady] gate '%s' stalled — elapsed %.1fs, %d of %d prefetches still pending (indices: %A)"
                    gateName
                    elapsedSeconds
                    pendingCount
                    totalCount
                    pendingIndices
            with _ ->
                ()

    /// Override the default stall reporter. Useful for routing stall
    /// warnings into the consumer's `ErrorReporter` or a metrics sink
    /// rather than stderr. Set once at boot, before the first
    /// `warnIfStalled` call fires.
    let setStallReporter (reporter: string -> float -> int -> int -> int list -> unit) : unit =
        stallReporter <- reporter

    /// Reset the once-per-gate-name dedup so a stall warning can fire
    /// again. Useful at team-switch / tenant-switch boundaries where
    /// the consumer resets the gates and wants the new run to be able
    /// to surface its own stall warning. Pass `None` to clear every
    /// recorded gate name.
    let resetStallWarning (gateName: string option) : unit =
        match gateName with
        | Some name -> stalledWarned.Remove name |> ignore
        | None -> stalledWarned.Clear()

    /// Emit a one-shot stall warning when `gateStartedAt` is older
    /// than `stallThreshold` AND at least one of `readyFlags` is
    /// `false`. The warning fires at most once per `gateName` —
    /// repeated calls per Elmish update are de-duped.
    ///
    /// Pair with `onAllReady` at boot-time fan-in sites:
    ///
    /// ```fsharp
    /// | ConfigsLoaded configs ->
    ///     let model' = { model with Configs = Prefetch.loaded configs }
    ///     let flags = [
    ///         Prefetch.isComplete model'.Configs
    ///         Prefetch.isComplete model'.Flags
    ///     ]
    ///     Prefetch.warnIfStalled
    ///         "boot.configs+flags"
    ///         model'.BootStartedAt
    ///         (System.TimeSpan.FromSeconds 5.0)
    ///         flags
    ///     model', Prefetch.onAllReady flags (Cmd.ofMsg ReinitActiveModule)
    /// ```
    let warnIfStalled
        (gateName: string)
        (gateStartedAt: System.DateTime)
        (stallThreshold: System.TimeSpan)
        (readyFlags: bool list)
        : unit =
        if not (List.forall id readyFlags) then
            let elapsed = (System.DateTime.UtcNow - gateStartedAt).TotalSeconds

            if elapsed > stallThreshold.TotalSeconds && not (stalledWarned.Contains gateName) then
                stalledWarned.Add gateName |> ignore

                let pendingIndices =
                    readyFlags
                    |> List.mapi (fun i ready -> if ready then None else Some i)
                    |> List.choose id

                stallReporter gateName elapsed pendingIndices.Length readyFlags.Length pendingIndices
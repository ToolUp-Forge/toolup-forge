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
/// Codifies the "load N sources, fire one command when the last one
/// resolves" pattern. (The SDK shell's own boot gates are `Remote<unit>`
/// values, which carry the same three states.)
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
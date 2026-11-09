// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// The state of one asynchronous read, as a module's model holds it. One type
/// for every load a client module performs, so a failed load is a value the
/// view can describe (it carries its failure) and is never confused with an
/// empty answer.
///
/// Pure F#: no Feliz, no browser. It lives in the toolkit so
/// `StateViews.remote` can render it, and every client tier above the toolkit
/// sees the same type.
[<RequireQualifiedAccess>]
type Remote<'T, 'E> =
    /// Not asked for yet — the module has not got there, or the source is off.
    | NotAsked
    /// In flight, with nothing to show meanwhile.
    | Loading
    /// In flight again, still showing the previous answer (`Remote.reload`).
    | Reloading of 'T
    /// Answered.
    | Loaded of 'T
    /// Failed; the view describes the failure through the caller's catalog.
    | Failed of 'E

[<RequireQualifiedAccess>]
module Remote =

    /// `Ok` is `Loaded`, `Error` is `Failed` — the shape every response
    /// message folds into the model.
    let ofResult (result: Result<'T, 'E>) : Remote<'T, 'E> =
        match result with
        | Ok value -> Remote.Loaded value
        | Error error -> Remote.Failed error

    /// Transform the answer, keeping the state.
    let map (f: 'T -> 'U) (remote: Remote<'T, 'E>) : Remote<'U, 'E> =
        match remote with
        | Remote.NotAsked -> Remote.NotAsked
        | Remote.Loading -> Remote.Loading
        | Remote.Reloading value -> Remote.Reloading(f value)
        | Remote.Loaded value -> Remote.Loaded(f value)
        | Remote.Failed error -> Remote.Failed error

    /// Transform the failure, keeping the state.
    let mapError (f: 'E -> 'F) (remote: Remote<'T, 'E>) : Remote<'T, 'F> =
        match remote with
        | Remote.NotAsked -> Remote.NotAsked
        | Remote.Loading -> Remote.Loading
        | Remote.Reloading value -> Remote.Reloading value
        | Remote.Loaded value -> Remote.Loaded value
        | Remote.Failed error -> Remote.Failed(f error)

    /// Chain a dependent state off the answer. A reload in flight stays in
    /// flight: an answer derived from a stale value is itself stale.
    let bind (f: 'T -> Remote<'U, 'E>) (remote: Remote<'T, 'E>) : Remote<'U, 'E> =
        match remote with
        | Remote.NotAsked -> Remote.NotAsked
        | Remote.Loading -> Remote.Loading
        | Remote.Loaded value -> f value
        | Remote.Reloading value ->
            match f value with
            | Remote.Loaded next
            | Remote.Reloading next -> Remote.Reloading next
            | other -> other
        | Remote.Failed error -> Remote.Failed error

    /// The answer, current or stale, if there is one.
    let toOption (remote: Remote<'T, 'E>) : 'T option =
        match remote with
        | Remote.Loaded value
        | Remote.Reloading value -> Some value
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Failed _ -> None

    /// The answer, or `fallback` while there is none.
    let withDefault (fallback: 'T) (remote: Remote<'T, 'E>) : 'T =
        toOption remote |> Option.defaultValue fallback

    /// A request is in flight (with or without a previous answer on screen).
    let isLoading (remote: Remote<'T, 'E>) : bool =
        match remote with
        | Remote.Loading
        | Remote.Reloading _ -> true
        | Remote.NotAsked
        | Remote.Loaded _
        | Remote.Failed _ -> false

    /// The read has an outcome — answered or failed — rather than being
    /// unasked or in flight.
    let isSettled (remote: Remote<'T, 'E>) : bool =
        match remote with
        | Remote.Loaded _
        | Remote.Failed _ -> true
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> false

    /// Ask again, keeping the answer on screen while the request is in flight
    /// (`Reloading`); with no answer to keep it is plain `Loading`. A caller
    /// that wants the old answer cleared sets `Remote.Loading` instead.
    let reload (remote: Remote<'T, 'E>) : Remote<'T, 'E> =
        match remote with
        | Remote.Loaded value
        | Remote.Reloading value -> Remote.Reloading value
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Failed _ -> Remote.Loading

/// A message for the user about the outcome of something they did, whose
/// polarity is its case: `Info` reports a success or a neutral fact, `Problem`
/// a failure. Rendered by `StateViews.notice`; the text is already localised
/// by the caller's catalog.
[<RequireQualifiedAccess>]
type Notice =
    | Info of string
    | Problem of string

/// The strings `StateViews.notice` needs from the caller's catalog — the
/// toolkit carries no string of its own.
type NoticeMessages = {
    /// The dismiss button's tooltip and accessible name.
    Dismiss: string
}
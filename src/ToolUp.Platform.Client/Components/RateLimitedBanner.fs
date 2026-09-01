// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.RateLimitedBanner

open Feliz
open ToolUp.Platform

// ─── Feliz rate-limit banner ─────────────────────────────────────────
//
// Renders a typed `RateLimitedError` as a banner with a countdown +
// retry affordance. Consumers handle `ApiError.RateLimited` server
// responses by capturing the typed payload and passing it to
// `RateLimitedBanner.render` — the component manages its own
// countdown via `React.useEffect` + `setInterval` and dispatches
// `onRetry ()` once the cooldown elapses (or sooner if the consumer
// dismisses the banner manually).
//
// Phase 56 substrate; consumers opt in by handling the
// `ApiError.RateLimited` case explicitly. No auto-injection —
// modules choose UI shape (a global toast, an inline banner per
// route, a modal blocking further interaction).
//
// Usage:
//
//   match result with
//   | Error (RateLimitedFromApi rle) ->
//       RateLimitedBanner.render rle (fun () ->
//           // user clicked Retry after countdown elapsed
//           dispatch ReissueRequest)
//   | _ -> ...

[<ReactComponent>]
let RateLimitedBanner (err: RateLimitedError) (onRetry: unit -> unit) =
    // Phase 751 — a component, so it reads the catalog with the ordinary
    // hook; no `…With` variant is needed and no arity changes. `render`
    // below is a plain function but only DELEGATES to this component, so
    // the hook still runs at a component boundary.
    let msgs = (MessageCatalogProvider.useMessages ()).RateLimited
    let secondsLeft, setSecondsLeft = React.useState err.RetryAfterSeconds

    // Tick once per second. The effect re-runs whenever
    // `secondsLeft` changes (post-decrement) so a single setTimeout
    // chain drives the countdown — no stale-closure or
    // setInterval-cleanup overhead. Stops when `secondsLeft` reaches
    // 0 (the setTimeout branch becomes a no-op).
    React.useEffect (
        (fun () ->
            let timeoutId =
                if secondsLeft > 0 then
                    Fable.Core.JS.setTimeout (fun () -> setSecondsLeft (secondsLeft - 1)) 1000
                else
                    0

            (fun () ->
                if timeoutId <> 0 then
                    Fable.Core.JS.clearTimeout timeoutId)),
        [| box secondsLeft |]
    )

    let canRetry = secondsLeft <= 0

    // The DU cases stay wire-shaped; only the display label is localised.
    let windowLabel =
        match err.Window with
        | PerSecond -> msgs.Windows.PerSecond
        | PerMinute -> msgs.Windows.PerMinute
        | PerHour -> msgs.Windows.PerHour
        | PerDay -> msgs.Windows.PerDay
        | SlidingWindow _ -> msgs.Windows.Sliding

    Html.div [
        prop.className "toolup-rate-limited-banner"
        prop.role "alert"
        prop.children [
            Html.div [ prop.className "toolup-rate-limited-banner__heading"; prop.text msgs.Heading ]
            Html.div [
                prop.className "toolup-rate-limited-banner__body"
                prop.text (msgs.LimitExceeded err.Limit windowLabel)
            ]
            if canRetry then
                Html.button [
                    prop.className "toolup-rate-limited-banner__retry"
                    prop.text msgs.TryAgain
                    prop.onClick (fun _ -> onRetry ())
                ]
            else
                // The countdown's plural rule moved INTO the catalog with
                // the sentence. It used to be an English `"s"` appended at
                // the call site, which no translation could have reached.
                Html.div [
                    prop.className "toolup-rate-limited-banner__countdown"
                    prop.text (msgs.TryAgainIn secondsLeft)
                ]
        ]
    ]

/// Convenience render helper. Same shape as the component but
/// callable from a non-React context (e.g. inside a `view` function
/// that doesn't already wrap its body in a Feliz `[<ReactComponent>]`
/// boundary).
let render (err: RateLimitedError) (onRetry: unit -> unit) = RateLimitedBanner err onRetry
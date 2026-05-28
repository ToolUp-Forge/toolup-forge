// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Premium.PremiumHook

open Fable.Core
open Fable.SimpleHttp
open Fable.SimpleJson
open Feliz
open ToolUp.Platform

// ─── Phase 62 — `usePremium` React hook ───────────────────────────
//
// Module client code calls `usePremium ()` to react to the current
// user's premium status. Returns the `PremiumStatus` snapshot plus
// a `refresh` thunk that re-fetches from `/api/_platform/users/me/premium-status`.
//
// The current status reads via a one-shot GET; consumers wanting
// reactive auth-state updates wire a `useEffect` that re-runs
// `refresh` whenever the auth-context changes (out of v1 scope —
// `usePremium ()` returns the snapshot at first render). Anonymous
// callers always read as `NotPremium` (server returns it
// unconditionally when no `AccessContext` resolves).

let private statusEndpoint = "/api/_platform/users/me/premium-status"

let private fetchStatus () : Async<PremiumStatus> = async {
    try
        let! response =
            Http.request statusEndpoint
            |> Http.method GET
            |> Http.header (Headers.contentType "application/json")
            |> Http.send

        if response.statusCode = 200 then
            try
                return Json.parseAs<PremiumStatus> response.responseText
            with _ ->
                return NotPremium
        else
            return NotPremium
    with _ ->
        return NotPremium
}

/// React hook surface. Returns `(status, refresh)`. Status is
/// `NotPremium` until the first fetch resolves; `refresh` re-runs
/// the fetch on demand (typical use: after the operator-side grant
/// flow signals a change).
let usePremium () : PremiumStatus * (unit -> unit) =
    let status, setStatus = React.useState NotPremium

    let refresh () =
        async {
            let! fetched = fetchStatus ()
            setStatus fetched
        }
        |> Async.StartImmediate

    React.useEffectOnce refresh

    status, refresh
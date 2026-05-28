// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Premium.PremiumGate

open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 62 — server-side premium gating helpers ────────────────
//
// Module handlers wrap premium-only endpoints in these guards rather
// than re-reading the claim each time. `requirePremium` is the strict
// guard (returns `Error` for non-premium); `ifPremium` is the
// conditional that lets the handler render the non-premium fallback.

let private resolveUserClaims (ctx: HttpContext) : IUserClaims =
    match ctx.RequestServices.GetService(typeof<IUserClaims>) with
    | :? IUserClaims as svc -> svc
    | _ -> NoOpUserClaims() :> _

let private resolveUserId (ctx: HttpContext) : string =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac.UserId
    | _ -> "anonymous"

/// Strict premium guard. Returns `Ok ()` when the caller holds
/// `Premium` status; `Error "not-premium"` otherwise. Handlers that
/// want richer error responses (countdown to upgrade, custom prompt)
/// branch off this result.
let requirePremium (ctx: HttpContext) : Async<Result<unit, string>> = async {
    let claims = resolveUserClaims ctx
    let userId = resolveUserId ctx
    let! status = claims.GetPremiumStatus userId

    match status with
    | Premium _ -> return Ok()
    | NotPremium -> return Error "not-premium"
}

/// Conditional premium guard. Runs the supplied async when the
/// caller holds `Premium` status; returns `None` otherwise. Useful
/// when the premium path is additive (extra data in the response)
/// rather than gating the whole endpoint.
let ifPremium (ctx: HttpContext) (premiumWork: Async<'T>) : Async<'T option> = async {
    match! requirePremium ctx with
    | Ok() ->
        let! result = premiumWork
        return Some result
    | Error _ -> return None
}
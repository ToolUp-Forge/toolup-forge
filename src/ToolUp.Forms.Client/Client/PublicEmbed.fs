// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.PublicEmbed

open Browser.Dom
open Feliz
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.PublicFormApi
open ToolUp.Forms.PublicFormsClient
open ToolUp.Forms.FormRenderer

// ─── Phase 21b — Public-form embed entry ────────────────────────────
//
// Standalone Feliz component for the `/r/{token}` URL — what
// invited respondents see when they click their share link. No
// sidebar, no auth UI, no module switcher; just a branding header
// and the form. The host application's `Client.fs` renders this
// component instead of the full app shell when the URL path matches
// the embed route.
//
// State machine (React useState — text inputs follow the SDK's
// "transient state stays in React, dispatch on submit only" rule):
//
//   Loading → Schema loaded → user fills form → Submitting →
//     Submitted (thank-you) | Error (recoverable: bad input;
//     terminal: bad token / use-exhausted)

type private LoadState =
    | Loading
    | SchemaError of string
    | Ready of FormSchema
    | Submitting
    | SubmissionFailed of FormSchema * string
    | Submitted

/// Pull the token out of `/r/{token}` (or `?token=` if the host
/// rewrites). Returns `None` for any path that doesn't match —
/// callers should not render this component when the path doesn't
/// match anyway.
let extractToken (path: string) (search: string) : string option =
    let trimmed = path.TrimStart('/')

    if trimmed.StartsWith("r/") then
        let rest = trimmed.Substring 2

        if rest.Length > 0 then
            Some(
                match rest.IndexOf('/') with
                | -1 -> rest
                | i -> rest.Substring(0, i)
            )
        else
            None
    else
        // ?token=... fallback for proxies that strip path components
        let q = search.TrimStart('?')

        q.Split('&')
        |> Array.tryPick (fun kv ->
            let parts = kv.Split('=')

            if parts.Length = 2 && parts[0] = "token" then
                Some parts[1]
            else
                None)

/// Compose the human-readable error string from a `FormError`.
/// Phase 21e (L1) — bad-token / rate-limited cases collapse to a
/// single "this link is no longer valid" message. Distinguishing
/// expired / revoked / use-exhausted / rate-limited to the respondent
/// doesn't help them (they need to ask the survey owner for a new
/// link in any case) AND lets a token-guessing attacker use the
/// embed as a token-state oracle to narrow the search space for
/// high-value forms. Server-side audit log still distinguishes the
/// cases (`PublicFormApiHandler.toFormError`); only the client
/// surface collapses them.
[<Literal>]
let private invalidLinkMessage =
    "This link is no longer valid. Please ask the survey owner for a new link."

let private describeError (err: FormError) : string =
    match err with
    // Phase 21e (L1) — collapse every token-rejection case to one
    // string. `Unauthorised` covers (Malformed / InvalidSignature /
    // NotFound / schema-downgraded); `NotFound("token", _)` covers
    // (Expired / Revoked / UseLimitExceeded); `RateLimited` is the
    // new per-window admission denial. All four classes render
    // identically to the respondent.
    | FormError.Unauthorised
    | FormError.NotFound("token", _)
    | FormError.RateLimited -> invalidLinkMessage
    | FormError.NotFound(resource, id) -> sprintf "Could not find %s '%s'." resource id
    | FormError.ValidationFailed errors ->
        errors
        |> List.map (fun e -> sprintf "%s: %s" e.FieldKey e.Message)
        |> String.concat "; "
    | FormError.StorageFailed msg -> sprintf "Server error: %s" msg
    | FormError.TransitionDenied reason -> sprintf "Cannot proceed: %s" reason
    | FormError.InvalidTransition(state, ev) -> sprintf "Cannot apply '%s' from state '%s'." ev state
    | FormError.WorkflowNotFound id -> sprintf "Workflow '%s' is not configured." id
    // Phase 21d — workflow-action durability variants. Public
    // form submissions don't drive workflow transitions (transitions
    // run from the authenticated admin surface), so these cases are
    // effectively unreachable here; render a generic server-error
    // message rather than leaking action-engine internals.
    | FormError.GuardEvaluationFailed _
    | FormError.ActionFailed _
    | FormError.ActionPendingFromPriorAttempt _ -> "Server error while applying your response. Please try again."

[<ReactComponent>]
let PublicEmbed (appName: string) =
    let state, setState = React.useState Loading

    React.useEffectOnce (fun () ->
        let token = extractToken window.location.pathname window.location.search

        match token with
        | None -> setState (SchemaError "No share token in URL.")
        | Some t ->
            async {
                let! result = proxy.GetSchemaByToken t

                match result with
                | Ok schema -> setState (Ready schema)
                | Error err -> setState (SchemaError(describeError err))
            }
            |> Async.StartImmediate)

    let header =
        Html.div [
            prop.style [
                style.padding (length.em 1.0)
                style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                style.backgroundColor "#ffffff"
            ]
            prop.children [
                Html.h1 [
                    prop.style [ style.fontSize (length.em 1.25); style.margin 0 ]
                    prop.text appName
                ]
            ]
        ]

    let body =
        match state with
        | Loading ->
            Html.div [
                prop.style [ style.padding (length.em 2); style.textAlign.center ]
                prop.text "Loading form…"
            ]

        | SchemaError msg ->
            Html.div [
                prop.style [ style.padding (length.em 2); style.color "#b91c1c"; style.textAlign.center ]
                prop.text msg
            ]

        | Ready schema ->
            FormRenderer schema (fun values ->
                setState Submitting

                async {
                    let! result =
                        proxy.SubmitWithToken {
                            Token =
                                (extractToken window.location.pathname window.location.search
                                 |> Option.defaultValue "")
                            Values = values
                        }

                    match result with
                    | Ok _ -> setState Submitted
                    | Error err -> setState (SubmissionFailed(schema, describeError err))
                }
                |> Async.StartImmediate)

        | Submitting ->
            Html.div [
                prop.style [ style.padding (length.em 2); style.textAlign.center ]
                prop.text "Submitting your response…"
            ]

        | SubmissionFailed(schema, msg) ->
            Html.div [
                prop.style [ style.padding (length.em 2) ]
                prop.children [
                    Html.div [
                        prop.style [
                            style.color "#b91c1c"
                            style.padding (length.em 1)
                            style.borderRadius 4
                            style.backgroundColor "#fef2f2"
                            style.marginBottom (length.em 1)
                        ]
                        prop.text msg
                    ]
                    FormRenderer schema (fun values ->
                        setState Submitting

                        async {
                            let! result =
                                proxy.SubmitWithToken {
                                    Token =
                                        (extractToken window.location.pathname window.location.search
                                         |> Option.defaultValue "")
                                    Values = values
                                }

                            match result with
                            | Ok _ -> setState Submitted
                            | Error err -> setState (SubmissionFailed(schema, describeError err))
                        }
                        |> Async.StartImmediate)
                ]
            ]

        | Submitted ->
            Html.div [
                prop.style [ style.padding (length.em 2); style.textAlign.center; style.color "#15803d" ]
                prop.children [
                    Html.h2 [ prop.style [ style.marginBottom (length.em 0.5) ]; prop.text "Thank you" ]
                    Html.p [ prop.text "Your response has been recorded." ]
                ]
            ]

    Html.div [
        prop.style [ style.minHeight (length.vh 100); style.backgroundColor "#f9fafb" ]
        prop.children [ header; body ]
    ]

/// `true` when the current URL path matches the embed route.
/// Host applications check this in `Client.fs` before
/// `Client.run` and short-circuit to `PublicEmbed` rendering when
/// it returns `true`. `false` for any path that doesn't start with
/// `/r/` and doesn't carry a `?token=` query param.
let isEmbedUrl (path: string) (search: string) : bool = (extractToken path search).IsSome
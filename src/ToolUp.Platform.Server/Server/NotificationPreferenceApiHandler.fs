// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.NotificationPreferenceApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── INotificationPreferenceApi handler (Phase 441) ──────────────────
//
// The surface behind `NotificationPreferencesUI`. Resolves
// `INotificationPreferenceStore` and `AccessContext` from per-request
// DI (the `ServiceAccountApiHandler` idiom) and acts ONLY on the
// caller's own record in the caller's resolved scope: there is no
// admin read of another user's preferences here, so the gate is
// "signed in, with a persistent scope" rather than a role check.
//
// A machine caller (`ClaimBearer`) is refused: a service account has no
// inbox and a preference record for one would be a write nobody reads.
//
// The matrix's columns are the channel families the deployment can
// actually deliver on — derived from the registered `INotificationSink`
// singletons — so the UI never offers a "push" column to a deployment
// with no push sink.

/// The channel families with a registered sink, in matrix order.
let private deliverableChannels (ctx: HttpContext) : PreferenceChannel list =
    let registered =
        match ctx.RequestServices.GetService(typeof<seq<INotificationSink>>) with
        | :? seq<INotificationSink> as sinks -> sinks |> Seq.map _.Kind |> Seq.toList
        | _ -> []

    let family (kind: NotificationKind.SinkKind) =
        match kind with
        | NotificationKind.SinkKind.Email -> PreferenceChannel.Email
        | NotificationKind.SinkKind.Sms -> PreferenceChannel.Sms
        | NotificationKind.SinkKind.Push _ -> PreferenceChannel.Push

    let present = registered |> List.map family |> Set.ofList
    PreferenceChannel.all |> List.filter present.Contains

/// Build the per-request `INotificationPreferenceApi` over the
/// composed store and the deployment's declared categories.
let notificationPreferenceApi (config: ServerConfig) (ctx: HttpContext) : INotificationPreferenceApi =
    let store =
        match ctx.RequestServices.GetService(typeof<INotificationPreferenceStore>) with
        | :? INotificationPreferenceStore as s -> Some s
        | _ -> None

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ -> AccessContext.unrestricted (AnonymousSession "anonymous")

    /// The one gate every method runs through: store composed, caller
    /// signed in as a person, scope persistent.
    let authorised
        (f: INotificationPreferenceStore -> string -> string -> Async<Result<'T, string>>)
        : Async<Result<'T, string>> =
        async {
            match store with
            | None -> return Error "Notification preferences are not enabled in this deployment."
            | Some s ->
                match accessContext.Subject with
                | AnonymousSession _ -> return Error "Sign in to manage your notification preferences."
                | ClaimBearer _ -> return Error "Notification preferences are personal; a token credential has none."
                | _ ->
                    match AccessContext.configScope accessContext with
                    | None -> return Error "Notification preferences need a persistent scope. Sign in or join a team."
                    | Some scope -> return! f s scope.ScopeId accessContext.UserId
        }

    {
        GetMyPreferences =
            fun () ->
                authorised (fun s scopeId userId -> async {
                    match! s.GetPreferences(scopeId, userId) with
                    | Error e -> return Error e
                    | Ok preferences ->
                        return
                            Ok {
                                Categories = config.NotificationCategories
                                Channels = deliverableChannels ctx
                                Preferences = preferences
                            }
                })

        SaveMyPreferences =
            fun preferences ->
                authorised (fun s scopeId userId -> async {
                    match UserNotificationPreferences.validate preferences with
                    | Error e -> return Error e
                    | Ok valid -> return! s.SavePreferences(scopeId, userId, valid)
                })
    }
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 441 — notification preference + digest substrate ──────────
//
// Fable-safe shared types for per-user notification control: which
// outbound channels a user wants a category on, whether a category is
// delivered immediately, batched into a digest, or muted, and a
// quiet-hours window during which immediate sends are held back. The
// server-side seams (`INotificationPreferenceStore`, the send-path
// filter, the digest job) live in `ToolUp.Platform.Server`; this file
// carries only what crosses the wire or is rendered by the client.
//
// **Categories are declared by modules, never enumerated by the SDK
// (GP 1 / GP 9).** `NotificationCategory` is the declaration shape; a
// module registers its categories with `ServerModule.withNotificationCategories`
// and an app with `ServerApp.withNotificationCategory`. The SDK ships no
// built-in category, so an SDK-only deployment has an empty matrix.
//
// **Everything here is inert until `ServerConfig.NotificationPreferences`
// is `EnabledNotificationPreferences` (GP 13).** Declaring categories on
// a `NoNotificationPreferences` deployment appends to a list nobody
// reads; the default composition is byte-for-byte unchanged.

/// How often a user's digest for a category × channel is assembled.
/// The digest job sends a bucket once its period has elapsed since the
/// bucket's previous send (or at the job's next tick when it has never
/// been sent), so a `Daily` digest lands at most once per 24 h.
type DigestFrequency =
    | Hourly
    | Daily
    | Weekly

/// Helpers for `DigestFrequency`.
module DigestFrequency =
    /// The minimum interval between two digest sends of this frequency.
    let period (frequency: DigestFrequency) : TimeSpan =
        match frequency with
        | Hourly -> TimeSpan.FromHours 1.0
        | Daily -> TimeSpan.FromDays 1.0
        | Weekly -> TimeSpan.FromDays 7.0

    /// Stable lower-case token used in blob names and digest markers.
    let toWireString (frequency: DigestFrequency) : string =
        match frequency with
        | Hourly -> "hourly"
        | Daily -> "daily"
        | Weekly -> "weekly"

    /// Inverse of `toWireString`; `None` for an unknown token.
    let ofWireString (token: string) : DigestFrequency option =
        match token with
        | "hourly" -> Some Hourly
        | "daily" -> Some Daily
        | "weekly" -> Some Weekly
        | _ -> None

    /// Every frequency, in ascending period order — the UI's picker.
    let all: DigestFrequency list = [ Hourly; Daily; Weekly ]

/// What a user wants done with a category on a channel.
type DeliveryPreference =
    /// Send as published (subject to quiet hours).
    | Immediate
    /// Hold and batch into one digest send per period.
    | Digest of DigestFrequency
    /// Drop, audited. Non-suppressible categories ignore this.
    | Muted

/// The outbound channel family a preference applies to. Coarser than
/// `NotificationKind.SinkKind` on purpose: a user chooses "push" or
/// "no push", never "WebPush but not FCM" — the vendor variant is a
/// deployment fact, not a preference.
[<RequireQualifiedAccess>]
type PreferenceChannel =
    /// Transactional email (`Notification.TransactionalEmail`).
    | Email
    /// Transactional SMS (`Notification.TransactionalSms`).
    | Sms
    /// Mobile / web push (`Notification.MobilePush`).
    | Push

/// Helpers for `PreferenceChannel`.
module PreferenceChannel =
    /// Every channel family, in matrix-column order.
    let all: PreferenceChannel list = [ PreferenceChannel.Email; PreferenceChannel.Sms; PreferenceChannel.Push ]

    /// Stable token used in blob names and audit payloads.
    let toWireString (channel: PreferenceChannel) : string =
        match channel with
        | PreferenceChannel.Email -> "Email"
        | PreferenceChannel.Sms -> "Sms"
        | PreferenceChannel.Push -> "Push"

    /// The channel family a transactional notification is bound for;
    /// `None` for in-app / SSE kinds, which preferences never govern
    /// (Phase 6a: the SSE bridge is not an outbound channel).
    let ofNotification (notification: Notification) : PreferenceChannel option =
        match notification with
        | TransactionalEmail _ -> Some PreferenceChannel.Email
        | TransactionalSms _ -> Some PreferenceChannel.Sms
        | MobilePush _ -> Some PreferenceChannel.Push
        | _ -> None

/// A daily window during which `Immediate` sends are deferred to the
/// window's end. Expressed in minutes since local midnight in the
/// user's zone so the client needs no time-zone arithmetic: a window
/// whose `EndMinute` is at or before its `StartMinute` wraps midnight
/// (22:00–07:00 is `{ StartMinute = 1320; EndMinute = 420 }`).
type QuietHours = {
    /// Minutes since local midnight the window opens (0–1439).
    StartMinute: int
    /// Minutes since local midnight the window closes (0–1439).
    EndMinute: int
    /// IANA or Windows time-zone id the minutes are read in
    /// (`Europe/London`, `GMT Standard Time`). Resolved server-side; an
    /// unresolvable id disables the window rather than holding mail.
    TimeZoneId: string
}

/// Pure helpers for `QuietHours` — integer arithmetic only, so the
/// same code runs in the client (validation, preview) and the server
/// (the filter), and the zone lookup stays server-side.
module QuietHours =
    /// Minutes in one day.
    [<Literal>]
    let MinutesPerDay = 1440

    /// Structural validation: both minutes within the day and a
    /// non-empty zone id. Says nothing about whether the zone resolves.
    let validate (quiet: QuietHours) : Result<QuietHours, string> =
        if quiet.StartMinute < 0 || quiet.StartMinute >= MinutesPerDay then
            Error $"StartMinute must be within 0–{MinutesPerDay - 1}; got {quiet.StartMinute}."
        elif quiet.EndMinute < 0 || quiet.EndMinute >= MinutesPerDay then
            Error $"EndMinute must be within 0–{MinutesPerDay - 1}; got {quiet.EndMinute}."
        elif String.IsNullOrWhiteSpace quiet.TimeZoneId then
            Error "TimeZoneId is required."
        else
            Ok quiet

    /// Whether `localMinute` (minutes since local midnight) falls
    /// inside the window. A window with `EndMinute <= StartMinute`
    /// wraps midnight; one with equal bounds is treated as wrapping the
    /// whole day (always quiet) rather than as empty, because a user
    /// who set 09:00–09:00 asked for silence, not for nothing.
    let contains (quiet: QuietHours) (localMinute: int) : bool =
        if quiet.EndMinute > quiet.StartMinute then
            localMinute >= quiet.StartMinute && localMinute < quiet.EndMinute
        else
            localMinute >= quiet.StartMinute || localMinute < quiet.EndMinute

    /// Minutes from `localMinute` until the window closes, assuming
    /// `contains quiet localMinute` holds. Zero when it does not.
    let minutesUntilEnd (quiet: QuietHours) (localMinute: int) : int =
        if not (contains quiet localMinute) then
            0
        else
            let delta = quiet.EndMinute - localMinute

            if delta > 0 then delta else delta + MinutesPerDay

/// One cell of the category × channel matrix.
type CategoryChannelPreference = {
    /// The declared `NotificationCategory.Id`.
    Category: string
    /// The channel family the preference governs.
    Channel: PreferenceChannel
    /// What to do with the category on that channel.
    Delivery: DeliveryPreference
}

/// Everything one user has chosen. Absent cells mean `Immediate`, so
/// the empty record is the SDK's prior behaviour exactly (GP 11).
type UserNotificationPreferences = {
    /// Explicit cells; every category × channel not listed is `Immediate`.
    Channels: CategoryChannelPreference list
    /// Optional daily hold window applied to every `Immediate` send.
    QuietHours: QuietHours option
}

/// Helpers for `UserNotificationPreferences`.
module UserNotificationPreferences =
    /// No cells, no quiet hours — deliver everything as published.
    let empty: UserNotificationPreferences = { Channels = []; QuietHours = None }

    /// The delivery preference for a cell, `Immediate` when unset.
    let resolve
        (prefs: UserNotificationPreferences)
        (category: string)
        (channel: PreferenceChannel)
        : DeliveryPreference =
        prefs.Channels
        |> List.tryFind (fun c -> c.Category = category && c.Channel = channel)
        |> Option.map _.Delivery
        |> Option.defaultValue Immediate

    /// Set (or reset) one cell, replacing any prior value for the same
    /// category × channel. Setting `Immediate` removes the cell, so the
    /// stored record stays minimal.
    let setDelivery
        (category: string)
        (channel: PreferenceChannel)
        (delivery: DeliveryPreference)
        (prefs: UserNotificationPreferences)
        : UserNotificationPreferences =
        let others =
            prefs.Channels
            |> List.filter (fun c -> not (c.Category = category && c.Channel = channel))

        match delivery with
        | Immediate -> { prefs with Channels = others }
        | _ -> {
            prefs with
                Channels =
                    others
                    @ [
                        {
                            Category = category
                            Channel = channel
                            Delivery = delivery
                        }
                    ]
          }

    /// Structural validation of a record before it is stored: every
    /// quiet-hours window valid, no duplicate cells. Category ids are
    /// not checked against the declared set here — a cell for a
    /// category a module no longer declares is harmless (it is never
    /// consulted) and dropping it silently would lose a choice the user
    /// made.
    let validate (prefs: UserNotificationPreferences) : Result<UserNotificationPreferences, string> =
        let duplicates =
            prefs.Channels
            |> List.countBy (fun c -> c.Category, c.Channel)
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map fst

        if not (List.isEmpty duplicates) then
            let (category, channel) = List.head duplicates
            Error $"Duplicate preference for {category} × {PreferenceChannel.toWireString channel}."
        else
            match prefs.QuietHours with
            | None -> Ok prefs
            | Some quiet ->
                match QuietHours.validate quiet with
                | Ok _ -> Ok prefs
                | Error e -> Error e

/// A module-declared notification category. Declared at registration
/// (`ServerModule.withNotificationCategories`); the SDK never
/// enumerates categories itself. `Suppressible = false` marks the
/// transactional / security class — password resets, invites, consent
/// receipts — that a user cannot mute, digest, or defer.
type NotificationCategory = {
    /// Stable id a publisher tags sends with
    /// (`NotificationCategoryScope.publish`). Lower-case dotted, e.g.
    /// `"reports.subscription"`; unique across the deployment.
    Id: string
    /// Label shown in the preference matrix.
    DisplayName: string
    /// One sentence shown beside the label.
    Description: string
    /// `false` bypasses every preference — the category is always
    /// delivered immediately. Default for `create` is `true`.
    Suppressible: bool
}

/// Helpers for `NotificationCategory`.
module NotificationCategory =
    /// Whether `id` is a valid category id: non-empty, lower-case
    /// letters / digits / `.` / `-` / `_`, no leading or trailing dot.
    let isValidId (id: string) : bool =
        not (String.IsNullOrWhiteSpace id)
        && not (id.StartsWith ".")
        && not (id.EndsWith ".")
        && id
           |> Seq.forall (fun ch ->
               (ch >= 'a' && ch <= 'z')
               || (ch >= '0' && ch <= '9')
               || ch = '.'
               || ch = '-'
               || ch = '_')

    /// A suppressible category with the given id and label.
    let create (id: string) (displayName: string) : NotificationCategory = {
        Id = id
        DisplayName = displayName
        Description = ""
        Suppressible = true
    }

    /// A non-suppressible category — always delivered, never governed
    /// by a preference. Use for password reset, invite, security alerts.
    let nonSuppressible (id: string) (displayName: string) : NotificationCategory = {
        create id displayName with
            Suppressible = false
    }

    /// Attach the one-sentence description.
    let withDescription (description: string) (category: NotificationCategory) : NotificationCategory = {
        category with
            Description = description
    }

/// Why a pending item is being held.
[<RequireQualifiedAccess>]
type PendingDisposition =
    /// Batched into the next digest of this frequency.
    | ForDigest of DigestFrequency
    /// Held by quiet hours; released (sent individually) at this UTC instant.
    | DeferredUntil of DateTime

/// One held-back send for one recipient. The filter writes one per
/// affected recipient (the notification inside is already narrowed to
/// that single recipient); the digest job drains them.
type PendingNotification = {
    /// Unique per item; the blob name carries it.
    Id: Guid
    /// The recipient the item is held for.
    UserId: string
    /// The category the publisher tagged the send with.
    Category: string
    /// The channel family the send was bound for.
    Channel: PreferenceChannel
    /// The transactional notification, narrowed to `UserId` alone.
    Notification: Notification
    /// UTC instant the filter held it.
    QueuedAt: DateTime
    /// Digest bucket or release instant.
    Disposition: PendingDisposition
}

/// What the preference UI renders: the declared categories (the rows),
/// the channel families (the columns), and the caller's current record.
type NotificationPreferenceView = {
    /// Every declared category, suppressible or not — the UI shows a
    /// non-suppressible row as locked rather than hiding it, so the
    /// user can see what will always reach them.
    Categories: NotificationCategory list
    /// The channel families the deployment can deliver on.
    Channels: PreferenceChannel list
    /// The caller's stored record (`UserNotificationPreferences.empty`
    /// when nothing has been saved).
    Preferences: UserNotificationPreferences
}

/// Remoting contract for the built-in preference centre. Both methods
/// act on the CALLER's own record in the caller's resolved scope — there
/// is no admin read of another user's preferences on this surface.
type INotificationPreferenceApi = {
    /// The declared matrix plus the caller's current record.
    [<TenantScoped>]
    GetMyPreferences: unit -> Async<Result<NotificationPreferenceView, string>>

    /// Replace the caller's record. Validated with
    /// `UserNotificationPreferences.validate`; audited.
    [<TenantScoped>]
    [<Audit "Custom:NotificationPreferencesChanged">]
    SaveMyPreferences: UserNotificationPreferences -> Async<Result<unit, string>>
}

/// Route shape for `INotificationPreferenceApi`.
module NotificationPreferenceApi =
    /// Remoting endpoint prefix — the platform's default
    /// `/api/{type}/{method}` shape, stated explicitly for symmetry
    /// with `ServiceAccountApi.routeBuilder`.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"

/// Deployment-level knobs for the substrate. Carried inside
/// `EnabledNotificationPreferences` so a deployment that opts in
/// states its choices in one place.
type NotificationPreferenceSettings = {
    /// Five-field cron on which the digest job ticks. Each tick releases
    /// expired quiet-hours holds and sends every digest bucket whose
    /// period has elapsed. Default: every 15 minutes.
    DigestCron: string
    /// Cap on the items folded into one digest send; the remainder
    /// waits for the next period. Default: 50.
    MaxItemsPerDigest: int
    /// Subject line of a digest email; `{count}` is substituted.
    /// Default: `"Your notification digest ({count} items)"`.
    DigestEmailSubject: string
}

/// Helpers for `NotificationPreferenceSettings`.
module NotificationPreferenceSettings =
    /// The defaults a deployment gets from `EnabledNotificationPreferences
    /// NotificationPreferenceSettings.defaults`.
    let defaults: NotificationPreferenceSettings = {
        DigestCron = "*/15 * * * *"
        MaxItemsPerDigest = 50
        DigestEmailSubject = "Your notification digest ({count} items)"
    }

/// `ServerConfig.NotificationPreferences` — opt-in for the whole
/// substrate. Default `NoNotificationPreferences`: no store, no filter,
/// no digest job, no API route, and every declared category inert
/// (GP 11 + GP 13).
type NotificationPreferenceMode =
    /// Nothing registered; sends flow exactly as before Phase 441.
    | NoNotificationPreferences
    /// Register the blob-backed `INotificationPreferenceStore`, wrap the
    /// outbound channel in `NotificationPreferenceFilter`, register the
    /// `_platform.notifications.digest` job on the composed scheduler,
    /// and mount `INotificationPreferenceApi`.
    | EnabledNotificationPreferences of NotificationPreferenceSettings
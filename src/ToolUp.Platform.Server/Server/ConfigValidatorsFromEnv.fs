// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConfigValidators

open ToolUp.Platform.ConfigValidation

/// Build the `IConfigValidator list` for the reference-app posture.
/// Each validator's resolver is `None` when the consumer hasn't
/// wired that companion, `Some build` when they have — `fromEnv`
/// invokes each resolver and drops `None` results.
///
/// `oidcValidatorResolver` — typically `Some
/// ToolUp.AuthProviders.OidcAuthValidator.tryFromEnv`. The
/// companion's own `tryFromEnv` already returns `None` when
/// `TOOLUP_AUTH_MODE` isn't `oidc`, so callers wire it
/// unconditionally; the helper only invokes when the resolver is
/// `Some`.
///
/// `notificationValidator` — surfaced by
/// `NotificationChannel.fromEnv` as the third element of its
/// returned triple. `Some validator` when a distributed channel
/// (Redis) is active.
///
/// Order matches the reference composition root: OIDC first, then
/// notification.
let fromEnv
    (oidcValidatorResolver: (unit -> IConfigValidator option) option)
    (notificationValidator: IConfigValidator option)
    : IConfigValidator list =
    [
        match oidcValidatorResolver with
        | Some resolve -> yield! resolve () |> Option.toList
        | None -> ()
        yield! notificationValidator |> Option.toList
    ]
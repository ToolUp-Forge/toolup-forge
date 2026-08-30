// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleDirectoryHealth

open ToolUp.Platform
open ToolUp.Platform.HealthChecks

// ─── Google Workspace directory health probe ─────────────────────────
//
// A live authenticated round-trip against the configured Workspace
// domain, in the same spirit as the storage companions' live-list
// probes: it exercises the token exchange AND the Directory API call,
// so a rotated-away service-account key, a revoked domain-wide
// delegation, or a suspended impersonation subject all read as
// `Unhealthy` rather than as a silently empty typeahead.
//
// The probe deliberately goes through `IUserDirectory.SearchUsers`
// rather than reaching for the HTTP surface itself. That is the path
// the deployment actually serves, so the probe cannot pass while the
// served path fails — and it keeps the probe composable over any
// `IUserDirectory`, which is what makes it testable without a
// Workspace tenant.
//
// The probe query is two characters, deliberately: the companion
// short-circuits anything shorter to `Ok []` WITHOUT touching the
// network, so a one-character probe would report Healthy on a
// completely broken credential. A query that matches nobody is a fine
// probe — `Ok []` is a successful authenticated call.

[<Literal>]
let private probeQuery = "zz"

type GoogleDirectoryHealthCheck(directory: IUserDirectory) =
    interface IHealthCheck with
        member _.Name = "user_directory:google_workspace"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                match! directory.SearchUsers(probeQuery, 1) with
                | Ok _ -> return Healthy
                | Error message -> return Unhealthy message
            with ex ->
                return Unhealthy ex.Message
        }

/// Construct the readiness probe for a composed Google Workspace
/// directory. Pair it with the same `IUserDirectory` instance the
/// deployment serves from, so probe and runtime share one credential
/// and one token cache.
let create (directory: IUserDirectory) : IHealthCheck =
    GoogleDirectoryHealthCheck directory :> IHealthCheck
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.MediaHealthCheck

open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

// ─── Phase 88 — media library readiness probe ─────────────────────────
//
// A readiness probe over the backing `IBlobStorage`: if the store can be
// reached, the media library can serve. Stateless — reads fresh state on
// every `Check` (GP 12 rule 4).

type private Impl(storage: IBlobStorage) =
    interface IHealthCheck with
        member _.Name = "media_library"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! _ = storage.Exists("_platform", "media/_health/probe")
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

let create (storage: IBlobStorage) : IHealthCheck = Impl(storage) :> IHealthCheck
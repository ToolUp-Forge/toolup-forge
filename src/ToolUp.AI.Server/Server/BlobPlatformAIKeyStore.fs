// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.BlobPlatformAIKeyStore

open ToolUp.Platform.Secrets
open ToolUp.AI

// ─── Phase 70 — Default IPlatformAIKeyStore implementation ──────
//
// Backed by ISecretStore using two container conventions:
//
//   * Platform-scope keys → container `_platform-ai-keys`. Key
//     name = the provider id (e.g. "anthropic-claude").
//   * Team-scope keys → container `team-ai-keys-{teamId}`. Same
//     per-provider key name.
//
// Containers are dedicated to AI keys rather than shared with the
// existing `_platform` / `team-{teamId}` containers used by Phase
// 43.A BYOK. Keeping them separate makes:
//   - Audit easier (every entry in this container is an AI key).
//   - Bulk enumeration cheap (an admin "list configured providers"
//     read does not page through unrelated secrets).
//   - Container-level access control sharper (a deployment can grant
//     Platform Admin write access to `_platform-ai-keys` without
//     widening their access to the broader `_platform` container).

[<Literal>]
let platformContainer = "_platform-ai-keys"

let private teamContainer (teamId: string) = $"team-ai-keys-{teamId}"

/// Build a `IPlatformAIKeyStore` backed by the supplied secret
/// store. Containers are dedicated to this concern (see header).
let create (secretStore: ISecretStore) : IPlatformAIKeyStore =
    { new IPlatformAIKeyStore with
        member _.GetPlatformKey(providerId) =
            secretStore.GetSecret(platformContainer, providerId)

        member _.SetPlatformKey(providerId, apiKey) =
            secretStore.SetSecret(platformContainer, providerId, apiKey)

        member _.DeletePlatformKey(providerId) =
            secretStore.DeleteSecret(platformContainer, providerId)

        member _.HasPlatformKey(providerId) = async {
            let! v = secretStore.GetSecret(platformContainer, providerId)
            return v.IsSome
        }

        member _.GetTeamKey(teamId, providerId) =
            secretStore.GetSecret(teamContainer teamId, providerId)

        member _.SetTeamKey(teamId, providerId, apiKey) =
            secretStore.SetSecret(teamContainer teamId, providerId, apiKey)

        member _.DeleteTeamKey(teamId, providerId) =
            secretStore.DeleteSecret(teamContainer teamId, providerId)

        member _.HasTeamKey(teamId, providerId) = async {
            let! v = secretStore.GetSecret(teamContainer teamId, providerId)
            return v.IsSome
        }
    }
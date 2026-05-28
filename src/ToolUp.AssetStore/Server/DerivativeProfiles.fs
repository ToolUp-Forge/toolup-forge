// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

/// Per-deployment registry mapping `DerivativeProfileId` to the
/// list of `DerivativeSpec`s that profile generates. Built at
/// compose time from `AssetCompose.withDerivativeProfile` calls;
/// read per-request by `DefaultAssetStore.GetDerivative` when a
/// cache miss resolves the spec to render.
///
/// Operators register profiles declaratively at compose time;
/// the `_platform/assets/profiles.json` file is the
/// deployment-edit-without-recompile path — `AssetCompose.run`
/// merges any file-loaded profiles on top of compose-registered
/// ones (file wins on collision so ops can override a
/// hard-coded baseline without a redeploy).
type DerivativeProfileRegistry = private {
    profiles: Map<string, DerivativeSpec list>
}

module DerivativeProfileRegistry =

    let empty: DerivativeProfileRegistry = { profiles = Map.empty }

    /// Default `"web-default"` profile — covers the common case
    /// (responsive image set for a CMS / directory listing).
    /// Operators wanting only some of these registries replace
    /// the profile entirely rather than subtract from it.
    let webDefaultSpecs: DerivativeSpec list = [
        {
            Name = "thumbnail"
            MaxWidth = Some 240
            MaxHeight = Some 240
            Format = Jpeg
            Quality = 78
        }
        {
            Name = "medium"
            MaxWidth = Some 800
            MaxHeight = Some 800
            Format = Jpeg
            Quality = 82
        }
        {
            Name = "webp-medium"
            MaxWidth = Some 800
            MaxHeight = Some 800
            Format = Webp
            Quality = 80
        }
        {
            Name = "og"
            MaxWidth = Some 1200
            MaxHeight = Some 630
            Format = Jpeg
            Quality = 84
        }
    ]

    /// Registry seeded with `web-default`. The default
    /// `AssetCompose.create` uses this; `withDerivativeProfile`
    /// appends / overrides.
    let withWebDefault: DerivativeProfileRegistry = {
        profiles = Map.ofList [ DerivativeProfileId.value DerivativeProfileId.webDefault, webDefaultSpecs ]
    }

    let register
        (id: DerivativeProfileId)
        (specs: DerivativeSpec list)
        (registry: DerivativeProfileRegistry)
        : DerivativeProfileRegistry =
        {
            profiles = Map.add (DerivativeProfileId.value id) specs registry.profiles
        }

    /// Resolve a profile id to its derivative list. `None` for
    /// unknown ids — caller surfaces `UnknownDerivative`.
    let resolve (id: DerivativeProfileId) (registry: DerivativeProfileRegistry) : DerivativeSpec list option =
        Map.tryFind (DerivativeProfileId.value id) registry.profiles

    /// Resolve a (profile, derivative-name) pair to the spec.
    /// `None` when either is unknown.
    let resolveSpec
        (id: DerivativeProfileId)
        (derivativeName: string)
        (registry: DerivativeProfileRegistry)
        : DerivativeSpec option =
        registry
        |> resolve id
        |> Option.bind (List.tryFind (fun s -> s.Name = derivativeName))
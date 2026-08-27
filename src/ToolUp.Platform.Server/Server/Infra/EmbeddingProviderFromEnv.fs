// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.EmbeddingProviderEnv

open System
open ToolUp.Platform
open ToolUp.Platform.IEmbeddingProvider

/// Resolver for one `IEmbeddingProvider` companion. The consumer threads
/// in one entry per companion the deployment has wired (e.g. `{ Name =
/// "openai"; Resolve = fun () -> OpenAIEmbeddingProvider.fromEnv
/// secretStore }`). Keeps `ToolUp.Platform.Server` free of any direct
/// dependency on embedding-provider companion packages (substrate
/// cleanliness — companion packages exist only at the SDK boundary, per
/// `CLAUDE.md`), exactly as `SecretStore.fromEnv` and
/// `BlobStorageEnv.fromEnv` do for their families.
type EmbeddingProviderResolver = {
    /// Matched against `TOOLUP_EMBEDDING_PROVIDER` (case-insensitive).
    /// Common values: `"local"`, `"openai"`.
    Name: string
    /// The companion's `fromEnv` wrapped to `unit -> IEmbeddingProvider
    /// option`. Companions taking substrate arguments (an `ISecretStore`
    /// for the API-keyed ones, an `IBlobStorage` for the persistent
    /// local one) are wrapped in a closure at the call site — the same
    /// shape Azure's blob companion uses.
    ///
    /// `None` means "selected, but this deployment cannot build it" —
    /// `fromEnv` below warns and falls back to the composition root's
    /// own provider rather than booting with no embedder at all.
    Resolve: unit -> IEmbeddingProvider option
}

// Resolved through the Phase-696 `ConfigResolution` seam, so a manifest
// can declare which provider the deployment runs on. Absent a manifest
// the seam is the environment read it replaces (GP 11).

/// Build the deployment's `IEmbeddingProvider` from
/// `TOOLUP_EMBEDDING_PROVIDER`. Recognised values:
///
///   - unset (default) — `fallback ()`, the provider the composition
///     root would have constructed itself. **Nothing else happens**: no
///     resolver runs, no line is logged, no env var beyond this one is
///     read. An existing deployment that upgrades and sets nothing is
///     byte-for-byte what it was (GP 11), and a deployment that never
///     sets the cluster pays nothing for it (GP 13).
///   - any value matched by a `resolvers` entry — resolve from that
///     companion. Falls back to `fallback ()` with a warning when the
///     resolver returns `None`.
///   - unrecognised value — falls back to `fallback ()` with a warning
///     naming the recognised values.
///
/// The unset arm is deliberately SILENT, which is where this helper
/// differs from its `SecretStore` / `BlobStorageEnv` siblings: theirs
/// announce an SDK-owned default they construct themselves, whereas the
/// thing being preserved here is the consumer's own explicit
/// construction, which logged nothing. Announcing it would put a line in
/// the startup log of every deployment that adopted the helper and
/// changed nothing — the empty startup-log diff is the whole content of
/// the Phase 11.G env-var contract.
///
/// **Secrets are never read from the environment here or in a
/// companion's `fromEnv`.** An API-keyed provider reads its key through
/// `ISecretStore` at call time (the provider-authoring rule); this
/// cluster selects the provider and its non-secret parameters only.
let fromEnv
    (logger: ILogger)
    (resolvers: EmbeddingProviderResolver list)
    (fallback: unit -> IEmbeddingProvider)
    : IEmbeddingProvider =
    let describe (provider: IEmbeddingProvider) =
        $"{provider.ProviderId} / {provider.ModelId}, {provider.Dimensions} dimensions"

    let resolveSelected (resolver: EmbeddingProviderResolver) =
        match resolver.Resolve() with
        | Some provider ->
            logger.Info $"Embedding provider: {resolver.Name} ({describe provider})"
            provider
        | None ->
            logger.Warn
                $"TOOLUP_EMBEDDING_PROVIDER={resolver.Name} but the required configuration is not set. Falling back to the composition root's embedding provider."

            fallback ()

    let chosen =
        ConfigResolution.tryValue ConfigKeys.Names.embeddingProvider
        |> Option.map _.ToLowerInvariant()

    match chosen with
    | None -> fallback ()
    | Some other ->
        match
            resolvers
            |> List.tryFind (fun r -> r.Name.Equals(other, StringComparison.OrdinalIgnoreCase))
        with
        | Some resolver -> resolveSelected resolver
        | None ->
            let recognisedNames =
                match resolvers with
                | [] -> "(none — this deployment wired no embedding-provider resolvers)"
                | rs -> rs |> List.map _.Name |> String.concat ", "

            logger.Warn
                $"TOOLUP_EMBEDDING_PROVIDER={other} not recognised. Valid values: {recognisedNames}. Falling back to the composition root's embedding provider."

            fallback ()
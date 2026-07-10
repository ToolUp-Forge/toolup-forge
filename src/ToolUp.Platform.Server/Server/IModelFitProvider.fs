// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 449 — IModelFitProvider companion seam + registry ────────────
//
// The one execution binding for a fit (plan D9): a provider owns the
// modelling math, forge owns identity + gate comparison + audit. A
// provider is a companion (registered under `src/ModelProviders/`, the
// established provider-family convention), keyed by a stable `Kind`. The
// in-tree reference provider is a trivial deterministic fitter
// (mean/identity-class — explicitly **not** statistics) whose only job is
// to bind the contract pack and prove the seam.
//
// **Six portability rules (GP 12).**
// 1. *Identity by value* — `Kind` / `ProviderVersion` are strings; `Fit`
//    takes / returns value records (`FitRequest` / `FitOutcome`), never a
//    live handle.
// 2. *Async at every boundary* — `Fit` returns `Async<FitOutcome>`.
// 3. *Behaviour as data* — gates are `GateSpec` records on the request,
//    diagnostics are a `Map<string,float>` on the outcome; no caller
//    callback and no exception path for a gate.
// 4. *Stateless between calls* — a provider receives the whole request per
//    `Fit`; it caches nothing across calls (a distributed fit worker can
//    be recycled between runs).
// 5. *No cross-shard ordering* — each fit is independent; no ordering is
//    promised across fits.
// 6. *Precision at the lower bound* — `Seed: int64` + provider-declared
//    gates make reproducibility explicit; a provider whose fit is only
//    reproducible-to-tolerance documents that in its README.

/// A model-fit provider — the execution binding for one `Kind` of fit
/// (plan D9). Implemented by a companion under `src/ModelProviders/`;
/// composed into a deployment as an `IModelFitProvider` DI singleton and
/// indexed by `ModelFitProviderRegistry` at compose time.
type IModelFitProvider =
    /// Stable kind discriminator a `FitRequest.ProviderKind` resolves
    /// against. One provider per kind (the registry rejects duplicates at
    /// compose time).
    abstract Kind: string

    /// Provider version — part of a fit's composite identity (plan D5), so
    /// bumping it re-keys every subsequent fit. Follows the provider's own
    /// versioning policy.
    abstract ProviderVersion: string

    /// The diagnostic gate names this provider guarantees to populate in
    /// `FitOutcome.Diagnostics`. Advisory metadata — a consumer builds
    /// `GateSpec`s against these keys; the envelope still fails-closed on
    /// any requested gate whose diagnostic is absent at run time.
    abstract DeclareGates: unit -> string list

    /// Fit against the request's vintage + opaque spec, seeded by
    /// `request.Seed`. Returns the raw outcome — artifact reference +
    /// diagnostics + the provider's own (deterministic) `DurationMs` /
    /// `CostUnits`. The envelope overwrites `CompositeKey` + `GateVerdicts`
    /// with forge-owned values, so a provider need not compute them.
    ///
    /// **Determinism (plan D4).** A provider that declares itself
    /// deterministic MUST return an identical `FitOutcome` for an identical
    /// `FitRequest` — no wall-clock, no RNG outside the seed. The reference
    /// provider is fully deterministic.
    abstract Fit: request: FitRequest -> Async<FitOutcome>

/// Compose-time index of registered fit providers, keyed by `Kind`. Two
/// providers with the same `Kind` is a configuration error — the registry
/// throws at construction (mirrors the `INotificationSink` one-per-kind
/// rule, plan D2) so a deployment fails to start loudly rather than
/// dispatching to an arbitrary winner.
type ModelFitProviderRegistry(providers: IModelFitProvider list) =
    do
        let duplicates =
            providers
            |> List.groupBy (fun p -> p.Kind)
            |> List.filter (fun (_, group) -> List.length group > 1)
            |> List.map fst

        if not (List.isEmpty duplicates) then
            failwithf
                "Multiple IModelFitProvider implementations registered for kind(s): %s. Phase 449 permits one provider per kind."
                (String.concat ", " duplicates)

    let byKind = providers |> List.map (fun p -> p.Kind, p) |> Map.ofList

    /// Every registered provider, in registration order.
    member _.Providers: IModelFitProvider list = providers

    /// The registered kinds (one per provider, deduplication already
    /// enforced at construction).
    member _.Kinds: string list = providers |> List.map (fun p -> p.Kind)

    /// Resolve a provider by kind. `None` when no provider is registered
    /// for the kind — the envelope surfaces `ModelFitError.ProviderNotFound`.
    member _.TryResolve(kind: string) : IModelFitProvider option = Map.tryFind kind byKind
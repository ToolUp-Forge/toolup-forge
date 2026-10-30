// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Microsoft.Extensions.DependencyInjection

// ─── The helpers every composition root shares ──────────────────────────
//
// A compose builder does three things over and over: resolve an optional
// service, capture the registration it is about to decorate, and refuse a
// composition it cannot build. Each has one spelling here, so a builder
// says which of the three it is doing rather than restating how.

/// Why a composition was refused. A builder raises it through
/// `ServiceComposition.refuse` (as `CompositionRefused`) where it can only
/// fail, and returns it as data where its caller can act on it.
[<RequireQualifiedAccess>]
type CompositionRefusal =
    /// The builder needs a service no earlier step registered. `remedy`
    /// names the step that registers it.
    | MissingDependency of builder: string * service: string * remedy: string
    /// A declaration handed to the builder is invalid.
    | InvalidDeclaration of builder: string * reason: string
    /// The service is registered, but by an implementation the builder
    /// cannot extend.
    | UnsupportedImplementation of builder: string * service: string * found: string

[<RequireQualifiedAccess>]
module CompositionRefusal =
    /// One sentence naming the builder, what is wrong and what to do.
    let describe (refusal: CompositionRefusal) : string =
        match refusal with
        | CompositionRefusal.MissingDependency(builder, service, remedy) ->
            $"{builder}: no {service} is registered; {remedy}."
        | CompositionRefusal.InvalidDeclaration(builder, reason) -> $"{builder}: {reason}"
        | CompositionRefusal.UnsupportedImplementation(builder, service, found) ->
            $"{builder}: the composed {service} is {found}, which this builder cannot extend."

/// A composition refused as typed data: a host or a test matches the
/// `CompositionRefusal` rather than a message string.
exception CompositionRefused of refusal: CompositionRefusal with
    override this.Message = CompositionRefusal.describe this.refusal

/// One registration captured from a service collection: the descriptor, and
/// a resolver that builds what it registered.
type CapturedService<'T> = {
    Descriptor: ServiceDescriptor
    Resolve: IServiceProvider -> 'T
}

[<RequireQualifiedAccess>]
module ServiceComposition =
    /// The registered `'T`, or `None` when nothing (or something else) is
    /// registered under it.
    let tryService<'T> (sp: IServiceProvider) : 'T option =
        match sp.GetService(typeof<'T>) with
        | :? 'T as service -> Some service
        | _ -> None

    /// The last un-keyed registration of `'T` in the collection, whatever
    /// its shape (factory, instance or type), captured BEFORE a decorator
    /// replaces it. A decorator resolves the inner service through
    /// `Resolve`, never from the built provider, where `'T` is by then the
    /// decorator itself and the factory would recurse.
    let captureDescriptor<'T> (services: IServiceCollection) : CapturedService<'T> option =
        services
        |> Seq.filter (fun d -> d.ServiceType = typeof<'T> && not d.IsKeyedService)
        |> Seq.tryLast
        |> Option.map (fun descriptor ->
            let resolve: IServiceProvider -> 'T =
                if not (isNull (box descriptor.ImplementationFactory)) then
                    let factory = descriptor.ImplementationFactory
                    fun sp -> factory.Invoke sp :?> 'T
                elif not (isNull descriptor.ImplementationInstance) then
                    let instance = descriptor.ImplementationInstance :?> 'T
                    fun _ -> instance
                else
                    fun sp -> ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType) :?> 'T

            {
                Descriptor = descriptor
                Resolve = resolve
            })

    /// Refuse the composition: raise `refusal` as `CompositionRefused`.
    let refuse (refusal: CompositionRefusal) : 'a = raise (CompositionRefused refusal)
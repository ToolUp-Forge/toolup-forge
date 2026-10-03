// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 972 — the one place a provider's exception is recovered from the
/// wrapper an async boundary put around it.
///
/// `Async.AwaitTask` raises a faulted task's `AggregateException`, not the
/// exception inside it (measured on FSharp.Core 10.1: a task faulted with
/// `HttpRequestException` arrives as `AggregateException` whose inner is the
/// `HttpRequestException`; a CANCELLED task arrives as a direct
/// `TaskCanceledException`, and a `task { }` builder unwraps on its own). A
/// `with` branch that type-tests the provider's exception directly therefore
/// never fires on that path, and the failure falls to a generic branch — or
/// out of the function altogether. Reflection adds `TargetInvocationException`
/// for the same reason.
///
/// Match through `ProviderException` instead of `:?`:
///
///     match ex with
///     | ProviderException(rfe: RequestFailedException) when rfe.Status = 404 -> ...
///     | ProviderException(_: HttpRequestException) -> ...
///
/// The pattern sees through both wrappers (recursively, `AggregateException`
/// flattened) and answers the FIRST cause of the annotated type, so an
/// unwrapped exception matches exactly as `:?` would. ArchitectureFitness
/// fails a bare `:?` on a provider exception in any module that awaits a
/// `Task` through `Async.AwaitTask`.
module ToolUp.Platform.ProviderExceptions

#if !FABLE_COMPILER
open System
open System.Reflection

/// The exceptions `ex` is made of, with every `AggregateException` (flattened)
/// and `TargetInvocationException` wrapper removed. Never empty.
let rec causes (ex: exn) : exn list =
    match ex with
    | :? AggregateException as aggregate when aggregate.InnerExceptions.Count > 0 ->
        aggregate.Flatten().InnerExceptions |> Seq.toList |> List.collect causes
    | :? TargetInvocationException as invocation when not (isNull invocation.InnerException) ->
        causes invocation.InnerException
    | _ -> [ ex ]

/// The first cause of `ex` — the exception a provider actually threw.
let unwrap (ex: exn) : exn = List.head (causes ex)

/// `ex`'s first cause, as a total pattern: `| Unwrapped inner -> …`.
let (|Unwrapped|) (ex: exn) : exn = unwrap ex

/// `ex`'s first cause of type `'T`, seen through any async/reflection wrapper.
let (|ProviderException|_|) (ex: exn) : 'T option =
    causes ex
    |> List.tryPick (fun cause ->
        match box cause with
        | :? 'T as typed -> Some typed
        | _ -> None)
#endif
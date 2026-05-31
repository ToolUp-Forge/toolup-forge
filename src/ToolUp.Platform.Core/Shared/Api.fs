// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// Portions of this file were adapted from SAFE.Client.Utils, distributed
// under the MIT license (Copyright 2017 SAFE-Stack). When ToolUp.Platform
// replaced the SAFE.Client metapackage with direct dependencies on Fable,
// Elmish, Feliz, and Fable.Remoting, the DU surface actually consumed by
// the codebase (`ApiCall`, `RemoteData`) was re-homed here verbatim to
// preserve call-site syntax. See Shared/Api.fs in the original package
// for the full MIT license text.
//
// The proxy-building helpers (`Api.makeProxy` client, `Api.make` server)
// live in Client/Api.fs and Server/Api.fs respectively so that the core
// ToolUp.Platform.dll does not take a ToolUp.Remoting.Client or
// ToolUp.Remoting.Giraffe dependency. Each is injected into the consuming
// project via ToolUp.Platform.Client.props / .Server.props.

/// Used commonly to model asynchronous calls to the server (or any other external service) within an Elmish message instead of two separate messages e.g. `LoadData` and `DataLoaded`. `Start` represents the initial request (command); `Finished` contains the resultant data on the callback.
type ApiCall<'TStart, 'TFinished> =
    /// Represents an API call that should be initiated.
    | Start of 'TStart
    /// Represents the response of an API call.
    | Finished of 'TFinished

/// Typically used for data on a model that will be loaded from the server. This type represents some data which has either not yet started loading, is currently in the process of being loaded, or has been loaded and is available.
type RemoteData<'T> =
    /// The data has not yet started loading.
    | NotStarted
    /// The data is now being loaded. May already contain some data i.e. a refresh operation is in process.
    | Loading of 'T option
    /// The data is available.
    | Loaded of 'T

    /// Unwraps the Loaded value, or returns the supplied default value.
    member this.DefaultValue v = this.AsOption |> Option.defaultValue v

    /// Returns whether the `RemoteData<'T>` value has been loaded.
    member this.HasLoaded =
        match this with
        | NotStarted
        | Loading _ -> false
        | Loaded _ -> true

    /// Returns whether the `RemoteData<'T>` value has started loading or not.
    member this.HasStarted =
        match this with
        | NotStarted -> false
        | Loading _
        | Loaded _ -> true

    /// Returns whether the `RemoteData<'T>` value has been loaded.
    member this.HasData = Option.isSome this.AsOption

    /// Returns whether the `RemoteData<'T>` value is loading. This will return true for both first-time and refresh-style loads.
    member this.IsStillLoading =
        match this with
        | Loading _ -> true
        | NotStarted
        | Loaded _ -> false

    /// Returns whether the `RemoteData<'T>` value is refreshing itself i.e. Loading (Some _)
    member this.IsRefreshing =
        match this with
        | Loading(Some _) -> true
        | _ -> false

    /// Returns whether the `RemoteData<'T>` value has not yet started.
    member this.HasNotStarted =
        match this with
        | NotStarted -> true
        | Loading _
        | Loaded _ -> false

    /// Maps the underlying value of the remote data, when it exists, into another shape.
    member this.Map mapper =
        match this with
        | NotStarted -> NotStarted
        | Loaded value -> Loaded(mapper value)
        | Loading None -> Loading None
        | Loading(Some value) -> Loading(Some(mapper value))

    /// Verifies that a `RemoteData<'T>` value has some data loaded (may be Loading or Loaded), and that the data satisfies a given requirement.
    member this.Exists predicate =
        this.AsOption |> Option.exists predicate

    /// Like `map` but instead of mapping just the value into another type in the `Loading` or `Loaded` case, it will transform the value into potentially a different case of the `RemoteData<'T>` type.
    member this.Bind binder =
        match this.AsOption with
        | Some v -> binder v
        | None -> this

    /// Maps `Loaded` or `Loading Some` to `Some`, everything else to `None`.
    member this.AsOption =
        match this with
        | Loaded value
        | Loading(Some value) -> Some value
        | NotStarted
        | Loading None -> None

    /// Transitions to Loading, retaining existing data as needed.
    ///
    /// ```
    /// NotStarted | Loading None -> Loading None
    /// Loaded x | Loading (Some x) -> Loading (Some x)
    /// ```
    member this.StartLoading() = Loading this.AsOption

/// Contains utility functions on the `RemoteData` type.
module RemoteData =
    /// Maps `Loaded` to `Some`, everything else to `None`.
    let asOption (remote: RemoteData<'T>) = remote.AsOption

    /// Unwraps the Loaded value, or returns the default value.
    let defaultValue defaultValue (remote: RemoteData<'T>) = remote.DefaultValue defaultValue

    /// Returns whether the `RemoteData<'T>` value has been loaded.
    let hasLoaded (remote: RemoteData<'T>) = remote.HasLoaded

    /// Returns whether the `RemoteData<'T>` value has started loading or not.
    let hasStarted (remote: RemoteData<'T>) = remote.HasStarted

    /// Returns whether the `RemoteData<'T>` value has started loading.
    let hasNotStarted (remote: RemoteData<'T>) = remote.HasNotStarted

    /// Returns whether the `RemoteData<'T>` value is loading.
    let isLoading (remote: RemoteData<_>) = remote.IsStillLoading

    /// Returns whether the `RemoteData<'T>` value is refreshing.
    let isRefreshing (remote: RemoteData<_>) = remote.IsRefreshing

    /// Returns whether the `RemoteData<'T>` value has data.
    let hasData (remote: RemoteData<_>) = remote.HasData

    /// Maps the underlying value of the remote data, when it exists, into another shape
    let map mapper (remote: RemoteData<'T>) = remote.Map mapper

    /// Verifies that a `RemoteData<'T>` value is loaded, and that the data satisfies a given requirement.
    let exists predicate (remote: RemoteData<'T>) = remote.Exists predicate

    /// Like `map` but instead of mapping just the value into another type in the `Loaded` case, it will transform the value into potentially a different case of the `RemoteData<'T>` type.
    let bind binder (remote: RemoteData<'T>) = remote.Bind binder

    /// Transitions to Loading, retaining existing data as needed.
    /// `Loaded x -> Loading x`;
    /// `NotStarted -> Loading None`;
    /// `Loading x -> Loading x`;
    let startLoading (remote: RemoteData<'T>) = remote.StartLoading
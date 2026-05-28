// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Testing.ModuleHotReload

#if !FABLE_COMPILER
open System
open System.IO

// ─── .NET-only module hot-reload watcher ──────────────────────────────
//
// `FileSystemWatcher`-backed callback that fires whenever a module's
// `.Client.props` / `.Server.props` file changes. Eliminates the
// recompile-restart cycle for module-only edits in dev — the host
// (typically a `dotnet watch` loop) listens for the event and dispatches
// a synthetic `ModuleRegistered` Elmish msg that rebuilds the
// registered-module list without a process restart.
//
// Gated on `!FABLE_COMPILER` because `System.IO.FileSystemWatcher` is a
// .NET-only BCL type; Fable consumers extracting the source from the
// nupkg get the `#else` stub instead. Hosts that don't wire hot reload
// reference nothing and pay nothing at runtime.
//
// State-preservation rule: the host's `ModuleRegistered` handler
// retains every existing module's `Model`; only the affected module
// mutates. The watcher does not own that policy — it simply signals
// "this file changed, re-register".

/// Token returned by `watch`. Disposing it stops the underlying
/// `FileSystemWatcher` and frees the file-handle.
type WatcherHandle(watcher: FileSystemWatcher) =
    interface IDisposable with
        member _.Dispose() = watcher.Dispose()

/// Watch a directory (or single file's parent directory) for changes
/// to `.Client.props` / `.Server.props` files. The callback fires
/// with the full path of the changed file. Returns a `WatcherHandle`
/// that disposes the underlying `FileSystemWatcher`.
///
/// The watcher debounces by relying on the host callback to filter
/// duplicate `Changed` events (`FileSystemWatcher` fires twice for
/// most editors); a Map keyed by full-path + last-event-tick handles
/// it cleanly host-side.
let watch (directory: string) (onChanged: string -> unit) : WatcherHandle =
    let watcher = new FileSystemWatcher(directory)
    watcher.Filter <- "*.props"
    watcher.IncludeSubdirectories <- true
    watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName

    let handler (e: FileSystemEventArgs) =
        if e.FullPath.EndsWith(".Client.props") || e.FullPath.EndsWith(".Server.props") then
            onChanged e.FullPath

    watcher.Changed.Add handler
    watcher.Created.Add handler

    watcher.Renamed.Add(fun e ->
        if e.FullPath.EndsWith(".Client.props") || e.FullPath.EndsWith(".Server.props") then
            onChanged e.FullPath)

    watcher.EnableRaisingEvents <- true
    new WatcherHandle(watcher)

#else
// Fable-side stub — the .NET `FileSystemWatcher` surface is excluded
// when this module is extracted into a Fable consumer's project. Any
// Fable caller that imports it should not call `watch`; the
// unit-returning stub fails at the call site, not at module load.
let watch (_directory: string) (_onChanged: string -> unit) : unit =
    failwith "ModuleHotReload.watch is .NET-only (System.IO.FileSystemWatcher); do not call from a Fable client."
#endif
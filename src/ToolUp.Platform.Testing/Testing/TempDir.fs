// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

open System
open System.IO
open System.Threading

// ─── TempDir ──────────────────────────────────────────────────────────
//
// A fixture that creates a directory owns its deletion. `TempDir.create`
// makes `%TEMP%/toolup-<family>-<guid>` and deletes it, recursively, when
// disposed, so `use dir = TempDir.create "jobstore"` is the whole
// lifecycle. The family stays in the name on purpose: a census of the
// `toolup-*` directories a run left behind attributes each leak to the
// fixture family that made it, which is what the gate's leak check
// reports.
//
// Deletion retries a bounded number of times, because a file handle the
// code under test has not yet released (a SQLite pool, a file watcher, an
// antivirus scan) makes the first attempt fail on Windows. A directory
// already gone is not an error. Any other failure surfaces after the last
// attempt: a leak that is swallowed is a leak nobody fixes.

/// A directory under the system temp root that is deleted when disposed.
[<Sealed>]
type TempDir internal (family: string, path: string) =
    let mutable disposed = 0

    /// The fixture family the directory was created for.
    member _.Family = family

    /// The directory's full path. It exists until the value is disposed.
    member _.Path = path

    /// A path for `name` inside the directory (the file is not created).
    member _.File(name: string) = System.IO.Path.Combine(path, name)

    /// A sub-directory of this one, created now.
    member _.Sub(name: string) =
        Directory.CreateDirectory(System.IO.Path.Combine(path, name)).FullName

    override _.ToString() = path

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                TempDir.Delete path

    static member internal Delete(path: string) =
        let attempts = 10

        let rec attempt n =
            try
                if Directory.Exists path then
                    for file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) do
                        File.SetAttributes(file, FileAttributes.Normal)

                    Directory.Delete(path, true)
            with
            | :? DirectoryNotFoundException -> ()
            | :? IOException
            | :? UnauthorizedAccessException when n < attempts ->
                // A handle the code under test has not released yet: give
                // finalizers a chance to close it, back off, and retry.
                if n = 3 then
                    GC.Collect()
                    GC.WaitForPendingFinalizers()

                Thread.Sleep(25 * n)
                attempt (n + 1)

        attempt 1

/// Creating and scoping fixture directories.
[<RequireQualifiedAccess>]
module TempDir =
    /// The prefix every fixture directory carries under the temp root.
    [<Literal>]
    let Prefix = "toolup-"

    let private validFamily (family: string) =
        not (String.IsNullOrWhiteSpace family)
        && family
           |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '.' || c = '_')

    /// Create `%TEMP%/toolup-<family>-<guid>`. Dispose the value to delete
    /// it: `use dir = TempDir.create "jobstore"`. The family is a short
    /// name for the fixture (letters, digits, `-`, `.`, `_`).
    let create (family: string) : TempDir =
        if not (validFamily family) then
            invalidArg (nameof family) $"a temp-dir family is letters, digits, '-', '.' or '_'; got '{family}'"

        let path = Path.Combine(Path.GetTempPath(), $"{Prefix}{family}-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        new TempDir(family, path)

    let private runOwned = Collections.Concurrent.ConcurrentBag<TempDir>()

    let private deleteRunOwnedAtExit =
        lazy
            AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
                for dir in runOwned do
                    // Best effort: a directory still held here is left for
                    // the gate's temp census to name, never raised at exit.
                    // Delete by path, not by `Dispose`: a fixture that also
                    // disposed the directory early (and had it recreated by a
                    // late background write) must not make this a no-op.
                    try
                        TempDir.Delete dir.Path
                    with _ ->
                        ())

    /// Create a directory that lives as long as the test process and is
    /// deleted when it exits. For a fixture whose directory must outlive
    /// the case that made it: a contract pack's factory, which hands back a
    /// store and has nowhere to dispose it, or a scheduler fixture whose
    /// background jobs still write after the case's assertions are done.
    /// Prefer `create` with `use` wherever the case owns the lifetime.
    let forRun (family: string) : TempDir =
        let dir = create family
        deleteRunOwnedAtExit.Force()
        runOwned.Add dir
        dir

    /// Run an async body with a fresh directory, deleting it when the body
    /// finishes, faults or is cancelled. For bodies where a `use` binding
    /// would end before the work does.
    let withDir (family: string) (body: string -> Async<'T>) : Async<'T> = async {
        use dir = create family
        return! body dir.Path
    }

    /// The family a fixture directory name was created for, or `None` for
    /// a name `create` did not make. The inverse of `create`'s naming, so a
    /// census of the temp root attributes each directory to its family.
    let familyOf (directoryName: string) : string option =
        if not (directoryName.StartsWith Prefix) then
            None
        else
            let rest = directoryName.Substring Prefix.Length
            let cut = rest.LastIndexOf '-'

            if cut <= 0 then
                None
            else
                let guid = rest.Substring(cut + 1)

                if guid.Length = 32 && guid |> Seq.forall Char.IsAsciiHexDigit then
                    Some(rest.Substring(0, cut))
                else
                    None
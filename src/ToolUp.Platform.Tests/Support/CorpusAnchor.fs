// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Support.CorpusAnchor

// ─── Corpus-search anchoring for git worktrees ───────────────────────
//
// Two externally-resolved conformance corpora (the model-execution
// corpus and the federation-seam specification home) fall back to an
// upward directory search when their environment variable is unset.
// Anchoring that search at the RUNNING checkout has two failure modes,
// both observed 2026-08-27 across four independent sessions:
//
//   * a fresh linked git worktree at a short path has no corpus
//     anywhere above it, so the search finds nothing and the suite
//     fails — for a reason that is environmental, not a defect in the
//     code under test;
//   * WORSE — two worktrees of this repository sharing a parent
//     directory can see each other: the walk ascends to the common
//     parent and descends into the SIBLING worktree, picking up
//     whatever corpus checkout happens transiently to exist there. The
//     suite then certifies against a corpus chosen by which sibling was
//     mid-provision at the time — non-deterministic between runs.
//
// One rule, two halves, fixes both. The search is anchored at the
// repository's MAIN working tree (resolved via `git rev-parse
// --git-common-dir`), so a worktree of a checkout that sits inside a
// wider workspace resolves the same corpus the checkout itself would,
// with no environment variable; and the search never looks inside a
// DIFFERENT working tree of this same repository, so a sibling
// worktree's transient contents can never be mistaken for the corpus.
//
// Git being unavailable — or the checkout not being a git repository at
// all (a source tarball) — degrades to the old behaviour: the anchor is
// the running checkout and no foreign working tree is known. Resolution
// must never fail BECAUSE of this module; it only ever narrows or
// re-roots a search that already existed.

open System
open System.Diagnostics
open System.IO

/// Windows paths compare case-insensitively; everywhere else the
/// filesystem is honest about case.
let private pathComparison =
    if OperatingSystem.IsWindows() then
        StringComparison.OrdinalIgnoreCase
    else
        StringComparison.Ordinal

/// Full path with no trailing separator — the shape every comparison in
/// this module runs on.
let private normalize (path: string) =
    Path.TrimEndingDirectorySeparator(Path.GetFullPath path)

/// Is `path` equal to `root` or beneath it?
let isUnder (root: string) (path: string) =
    let root = normalize root
    let path = normalize path

    path.Equals(root, pathComparison)
    || path.StartsWith(root + string Path.DirectorySeparatorChar, pathComparison)

/// `git <arguments>` in `workingDir`; stdout trimmed, or `None` on any
/// failure — a missing git, a non-repo, a hung child. Never throws.
let private git (workingDir: string) (arguments: string) : string option =
    try
        let psi = ProcessStartInfo("git", arguments)
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use proc = Process.Start psi
        let output = proc.StandardOutput.ReadToEnd()

        if not (proc.WaitForExit 15000) then
            proc.Kill true
            None
        elif proc.ExitCode <> 0 then
            None
        else
            match output.Trim() with
            | "" -> None
            | trimmed -> Some trimmed
    with _ ->
        None

/// The repository's main working tree, when `checkoutRoot` is a LINKED
/// worktree of it. `None` when it IS the main tree, is not a git
/// checkout, or git is unavailable — every case in which there is no
/// better place to search from than `checkoutRoot` itself.
let mainWorkingTree (checkoutRoot: string) : string option =
    git checkoutRoot "rev-parse --git-common-dir"
    |> Option.bind (fun commonDir ->
        let full =
            if Path.IsPathRooted commonDir then
                normalize commonDir
            else
                normalize (Path.Combine(checkoutRoot, commonDir))

        // The common dir is the main tree's `.git` directory; a bare or
        // otherwise unusual layout names no main working tree this rule
        // can use.
        if Path.GetFileName full <> ".git" then
            None
        else
            match Path.GetDirectoryName full with
            | null -> None
            | main when normalize(main).Equals(normalize checkoutRoot, pathComparison) -> None
            | main -> Some(normalize main))

/// Every working tree of this repository other than the ones in `keep`
/// — the directories a corpus search must never look inside.
let foreignWorktrees (checkoutRoot: string) (keep: string list) : string list =
    let kept = keep |> List.map normalize

    match git checkoutRoot "worktree list --porcelain" with
    | None -> []
    | Some output ->
        output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            if line.StartsWith("worktree ", StringComparison.Ordinal) then
                Some(normalize (line.Substring "worktree ".Length))
            else
                None)
        |> Array.filter (fun wt -> kept |> List.forall (fun k -> not (wt.Equals(k, pathComparison))))
        |> Array.toList

/// The anchor a corpus search starts from and the working trees it must
/// refuse to enter, resolved together so the two cannot disagree about
/// which tree is "ours".
type Anchoring = {
    /// Where the upward search starts: the main working tree when the
    /// running checkout is a linked worktree, else the checkout itself.
    Anchor: string
    /// Working trees of this repository that are neither the running
    /// checkout nor the anchor. A candidate at or under any of these is
    /// some other session's transient state, never the corpus.
    Foreign: string list
}

let resolve (checkoutRoot: string) : Anchoring =
    let anchor =
        mainWorkingTree checkoutRoot |> Option.defaultValue (normalize checkoutRoot)

    {
        Anchor = anchor
        Foreign = foreignWorktrees checkoutRoot [ checkoutRoot; anchor ]
    }

/// Is `dir` inside a working tree the search must not read?
let excluded (anchoring: Anchoring) (dir: string) =
    anchoring.Foreign |> List.exists (fun foreign -> isUnder foreign dir)
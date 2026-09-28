// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 903 — the template gates evict the package folder the machine
/// actually uses.
///
/// **The bug.** `VerifyTemplates` and `VerifyPackagedModuleTemplate` both
/// restore their scratch-fed templates against a THROWAWAY package version
/// (`0.0.0-templategate`), and both used to wipe that version's cache
/// entries out of a GUESSED global-packages folder first — `NUGET_PACKAGES`
/// if set, else `~/.nuget/packages` — so a stale entry from a previous run
/// could never mask this run's source. The guess is narrower than NuGet's
/// own resolution: a `<config><add key="globalPackagesFolder" .../>` in
/// ANY nuget.config on NuGet's search path overrides both the env var and
/// the default, so a machine that sets it points the gate's eviction at a
/// folder NuGet itself is not reading from. The eviction step then no-ops
/// silently, restore resolves the PREVIOUS run's package from wherever
/// NuGet is really caching, and the gate reports green having verified
/// nothing about the tree it was told to check.
///
/// **Two candidate fixes, and why the second was chosen.** The narrower
/// fix is to ask NuGet directly (`dotnet nuget locals global-packages
/// --list`) rather than guess — honouring `NUGET_PACKAGES` first, exactly
/// as NuGet does, then falling back to the CLI's own answer. That closes
/// the wrong-folder class, but the folder it closes onto is still ONE
/// folder SHARED by every worktree on the machine: two worktrees running a
/// template gate at once pack and evict the SAME throwaway version in the
/// SAME shared cache, and can each evict the other mid-restore — exactly
/// the contention a several-worker campaign on one machine produces
/// (campaign-forge-27, 2026-09-28, six workers sharing this machine).
///
/// So the fix here removes the guess rather than sharpening it: each gate
/// run gets its OWN packages folder, scoped under the calling repository's
/// own `obj/` — already the home of `template-gate-feed` /
/// `packaged-module-template-feed`, and per-worktree by construction (a
/// worktree has its own `obj/`) — and the gate points `NUGET_PACKAGES` at
/// it for every child process it spawns. A private folder needs no
/// resolution logic at all, because there is nothing ambient to get
/// wrong, and two worktrees can never see — let alone evict — each
/// other's entries. "Ask NuGet" is still the right answer for a gate that
/// must read the machine's REAL shared cache; this one does not need to.
module ToolUp.Forge.TemplateGatePackages

open System.IO

/// The private, per-run global-packages folder a template gate points
/// `NUGET_PACKAGES` at, scoped under the calling repository's own `obj/`.
/// Pure — a function of the repo root only, so it needs no read of
/// `NUGET_PACKAGES` or any nuget.config to compute, and it never resolves
/// to a folder any OTHER repository checkout (or worktree of this one)
/// could also compute.
let privatePackagesFolder (repoRoot: string) : string =
    Path.Combine(repoRoot, "obj", "template-gate-packages")

/// What `evict` removed, and where — the gate prints both, so a reader can
/// tell a cold run (nothing removed) from a run that actually needed it.
type EvictionReport = {
    /// The private packages folder evicted.
    Folder: string
    /// The package ids (top-level subfolder names) that were present
    /// before the evict and are now gone, sorted for a stable report.
    /// NuGet lays a global-packages folder out as `<folder>/<id>/<version>/`,
    /// so a top-level subfolder name IS a package id.
    Removed: string list
}

/// Wipe the private packages folder and leave it present but empty. Safe
/// to delete wholesale — unlike a shared global-packages folder, nothing
/// but this gate's own restores ever writes here — so eviction needs no
/// per-version targeting the way the retired shared-folder logic did.
let evict (folder: string) : EvictionReport =
    let removed =
        if Directory.Exists folder then
            Directory.EnumerateDirectories folder
            |> Seq.map Path.GetFileName
            |> Seq.sort
            |> Seq.toList
        else
            []

    if Directory.Exists folder then
        Directory.Delete(folder, true)

    Directory.CreateDirectory folder |> ignore

    { Folder = folder; Removed = removed }
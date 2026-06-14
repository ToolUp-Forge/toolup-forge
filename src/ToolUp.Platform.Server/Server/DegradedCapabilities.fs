// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DegradedCapabilities

open System.Collections.Concurrent
open ToolUp.Platform

// ─── Phase 118 — degraded-capability registry ───────────────────────
//
// A place for compose-time / runtime "best-effort" wiring to register
// that it FAILED without crashing startup, so the silently-downgraded
// capability becomes observable on `/health`, `/dev/inspect`, and the
// HealthMonitorUI rather than disappearing into a `with _ -> ()` (GP 9).
//
// The motivating instance is
// `ComposeEncryption.wirePerScopeResolverToNotificationChannel`: a
// failed cross-silo subscribe means a crypto-shredded key keeps
// decrypting on every other silo until process restart — a GDPR-erasure
// hole with no log. The preflight validator covers the misconfiguration
// case; this covers a failed subscribe on a correctly-configured channel.
//
// **Always registered, zero footprint when empty (GP 13).** The
// singleton is composed unconditionally (best-effort sites need it
// present to register into), but it carries no cost when nothing is
// degraded: the `/health` writer skips the section entirely on `IsEmpty`,
// so the payload stays byte-for-byte identical to pre-Phase-118.
//
// **Single-instance scope.** The registry is in-memory and per-process —
// a degraded entry on one silo is not gossiped to others. That matches
// the data: an entry records that THIS instance failed to wire something
// at THIS boot. A second instance that wired cleanly correctly shows an
// empty set. Operators alerting on a non-empty set across the fleet catch
// the degraded instance.

/// Thread-safe registry of degraded capabilities. Registered as a DI
/// singleton by `compose` unconditionally; `Register` / `Clear` are the
/// write surface for best-effort sites, `Snapshot` / `IsEmpty` the read
/// surface for the health endpoints.
type DegradedCapabilityRegistry() =
    let entries = ConcurrentDictionary<string, DegradedCapability>()

    /// Register (or refresh) a degraded capability. Idempotent by
    /// `Capability` id: re-registering the same id updates
    /// reason/impact/remediation but PRESERVES the original
    /// `DegradedSince`, so the trail records when the degradation FIRST
    /// began, not the latest re-observation.
    member _.Register(entry: DegradedCapability) : unit =
        entries.AddOrUpdate(
            entry.Capability,
            entry,
            (fun _ existing -> {
                entry with
                    DegradedSince = existing.DegradedSince
            })
        )
        |> ignore

    /// Clear a degraded capability by id — e.g. a later successful
    /// (re)subscribe restored it. No-op when the id is not registered, so
    /// the happy-path "clear on success" call is free on a clean boot.
    member _.Clear(capability: string) : unit = entries.TryRemove capability |> ignore

    /// Current degraded set, oldest degradation first. Empty when nothing
    /// is degraded.
    member _.Snapshot() : DegradedCapability list =
        entries.Values |> Seq.sortBy _.DegradedSince |> List.ofSeq

    /// `true` when nothing is degraded — cheap guard for the `/health`
    /// writer to keep the payload byte-for-byte unchanged (GP 13).
    member _.IsEmpty: bool = entries.IsEmpty

/// `IDevDiagnosticsContributor` surfacing the degraded set as a
/// `"Degraded capabilities"` panel on `/dev/inspect`. Registered
/// unconditionally alongside the registry; contributes `[]` (rendered as
/// an empty array) on a healthy deployment. Cheap (one in-memory
/// dictionary read) per the contributor budget.
type DegradedCapabilitiesDiagnosticsContributor(registry: DegradedCapabilityRegistry) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async { return "Degraded capabilities", box (registry.Snapshot()) }
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open Microsoft.FSharp.Reflection

// ─── Phase 591 — taking a pin from a counterparty's published label ──
//
// The Phase 590 export is a hash-stamped canonical-JSON document a
// counterparty publishes; `PinnedPeerSurface` is the projection of that
// document this deployment holds and validates against. This module is
// the one road between the two, and it is deliberately the only place
// that reads a label:
//
// * **Verification happens once, at pinning, not at every preflight.** A
//   document whose stamp does not match a recomputation of its own
//   surface is not a stale pin, it is a corrupt or doctored one — and
//   the right treatment for that is to refuse to hold it, not to hold it
//   and report it forever. So `ofExportJson` returns a `Result` and a bad
//   document never becomes a pin at all.
// * **A format version this build cannot read is refused for the same
//   reason.** A label written under a newer `PeerSurfaceExport.FormatVersion`
//   may carry facets this descriptor vocabulary has no field for, and a
//   half-read label is worse than none: it would pass a trust check by
//   omission. The refusal names both versions so an operator upgrades
//   rather than guesses.
// * **Nothing here opens a socket.** A pin is taken from a document the
//   operator already has — a file, a registry entry, an out-of-band
//   exchange. A label fetched at boot is a label that changed after it
//   was read, which is exactly the property pinning exists to remove.
//
// **Facet names come from `PeerTrustPosture` itself, by reflection.** The
// pin carries the counterparty's posture as name/value data
// (`PinnedTrustFacet`) rather than as a second copy of the posture
// record, so a label can outlive the descriptor vocabulary it was taken
// under. Deriving the names from the record's own fields is what stops
// that flexibility becoming drift: there is no hand-written facet list to
// fall out of step with the surface, and a facet renamed on
// `PeerTrustPosture` renames here in the same commit.

/// Projects a counterparty's published `PeerSurface` / `PeerSurfaceExport`
/// into the `PinnedPeerSurface` the Phase 591 preflight validates against.
[<RequireQualifiedAccess>]
module FederationPin =

    /// Render one posture field's value as the pinned facet value.
    /// Booleans are lowercased so a requirement reads `"true"` rather
    /// than the BCL's `"True"` — the pinned vocabulary is a wire-ish
    /// surface an operator types by hand, not a .NET `ToString`.
    let private facetValue (raw: obj) : string =
        match raw with
        | :? bool as flag -> if flag then "true" else "false"
        | null -> ""
        | value -> string value

    /// Project a declared trust posture into its pinned facet list, with
    /// facet names taken from `PeerTrustPosture`'s own record fields.
    /// Sorted by facet name, so a pin's facet order never depends on
    /// declaration order.
    let trustFacets (posture: PeerTrustPosture) : PinnedTrustFacet list =
        FSharpType.GetRecordFields typeof<PeerTrustPosture>
        |> Array.map (fun field -> {
            Facet = field.Name
            Value = facetValue (field.GetValue posture)
        })
        |> Array.sortBy _.Facet
        |> Array.toList

    /// Pin an already-verified export envelope. Used directly when the
    /// deployment holds the counterparty's surface as a value — a
    /// co-owned instance, or an aggregate group's own derived surface
    /// (Phase 595), which exports through the identical path and
    /// therefore pins identically.
    let ofExport
        (counterpartyId: string)
        (source: string)
        (pinnedAt: DateTimeOffset)
        (export: PeerSurfaceExport)
        : PinnedPeerSurface =
        {
            CounterpartyId = counterpartyId
            Source = source
            FormatVersion = export.FormatVersion
            SurfaceHash = export.SurfaceHash
            PinnedAt = pinnedAt
            Serves =
                export.Surface.Serves.Contracts
                |> List.map (fun served -> {
                    ContractId = served.ContractId
                    Versions = served.Versions |> List.sort
                })
                |> List.sortBy _.ContractId
            TrustFacets = export.Surface.TrustPosture |> Option.map trustFacets |> Option.defaultValue []
            // Phase 642 — read fail-closed at PINNING time, not at every
            // preflight. `PeerSurface.dataVisibility` reads an absent,
            // empty or unrecognised declaration as `AggregatesOnly`, so a
            // label published before the facet existed pins as the
            // narrowest level and a label naming a level this build
            // cannot enforce pins as one it can. Normalising once, here,
            // is what lets `PinnedPeerSurface.DataVisibility` be a typed
            // fact rather than a claim every rule re-interprets.
            DataVisibility = PeerSurface.dataVisibility export.Surface
            // Phase 644 — normalised at PINNING time for the same reason,
            // through the same fail-closed reader: a label that omits the
            // member, carries `null`, or names a status this build does
            // not know pins as the grant it can actually be held to.
            TransitionAuthority = PeerSurface.transitionAuthority export.Surface
        }

    /// Pin a surface value directly — stamps it through
    /// `PeerSurface.export`, the same code path a published label goes
    /// through, so a pin taken this way is indistinguishable from one
    /// taken from the document.
    let ofSurface
        (counterpartyId: string)
        (source: string)
        (pinnedAt: DateTimeOffset)
        (surface: PeerSurface)
        : PinnedPeerSurface =
        ofExport counterpartyId source pinnedAt (PeerSurface.export surface)

    /// Verify and pin a counterparty's published export document: parse
    /// it, refuse a format version this build cannot read, recompute the
    /// surface hash and require it to match BOTH the document's own stamp
    /// and the hash the operator agreed out of band, then project.
    ///
    /// The out-of-band `expectedHash` is what makes this a *pin* rather
    /// than a *download*: the document's self-stamp only proves internal
    /// consistency, which a substituted document has too. Pass the
    /// document's own stamp when the exchange itself was the trusted
    /// channel.
    let ofExportJson
        (counterpartyId: string)
        (source: string)
        (expectedHash: string)
        (pinnedAt: DateTimeOffset)
        (exportJson: string)
        : Result<PinnedPeerSurface, string> =
        let parsed =
            try
                Ok(JsonRpc.deserialize<PeerSurfaceExport> exportJson)
            with ex ->
                Error
                    $"federation-pin: the published surface export for counterparty '{counterpartyId}' from {source} could not be parsed as a PeerSurfaceExport ({ex.Message}). Pin the document PeerSurface.exportJson produced, verbatim."

        parsed
        |> Result.bind (fun export ->
            if export.FormatVersion > PeerSurface.formatVersion then
                Error
                    $"federation-pin: the published surface export for counterparty '{counterpartyId}' from {source} declares FormatVersion {export.FormatVersion}, and this build reads at most {PeerSurface.formatVersion}. A label read at the wrong version is worse than none — a facet this vocabulary has no field for would pass a trust requirement by omission. Upgrade this deployment, or ask the counterparty for a label at a version both sides read."
            else
                let recomputed = (PeerSurface.export export.Surface).SurfaceHash

                if not (String.Equals(recomputed, export.SurfaceHash, StringComparison.OrdinalIgnoreCase)) then
                    Error
                        $"federation-pin: the published surface export for counterparty '{counterpartyId}' from {source} carries stamp {export.SurfaceHash}, but its own surface hashes to {recomputed}. The document is corrupt or was edited after it was stamped; it is not a stale pin and must not become one. Re-fetch it from the counterparty."
                elif not (String.Equals(recomputed, expectedHash, StringComparison.OrdinalIgnoreCase)) then
                    Error
                        $"federation-pin: the published surface export for counterparty '{counterpartyId}' from {source} hashes to {recomputed}, but the expected hash agreed out of band is {expectedHash}. The document is internally consistent, which a substituted document also is — so this mismatch means the label is not the one this deployment agreed to. Confirm the counterparty's current stamp out of band, then pin it."
                else
                    Ok(ofExport counterpartyId source pinnedAt export))
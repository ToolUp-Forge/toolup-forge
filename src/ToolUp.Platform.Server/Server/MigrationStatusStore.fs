// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.BlobStorage

// ─── Migration status store (Phase 10a) ──────────────────────────
//
// Storage layout
//   Container: always `_platform`.
//   Status blob: `migrations/{teamId}/{dataTypeId}.json`
//
// Mirrors the `_platform/data-sources/{scopeId}/...`,
// `_platform/jobs/{scopeId}/...` and `_platform/webhooks/{scopeId}/...`
// layouts so an operator sees one consistent shape under `_platform/`.
//
// The status blob is a *projection of progress*, never the authority
// on whether an object needs migrating. That authority is the
// `_schemaVersion` stamp on each object's own metadata, which is why
// a pass is resumable by construction: a process killed mid-pass
// leaves upgraded objects stamped and un-upgraded objects not, and the
// next pass recomputes the whole picture from the stamps. Losing this
// blob entirely costs the admin table its history and nothing else.

/// Read / write surface over the per-team, per-data-type migration
/// status blobs.
///
/// Portability audit (GP 12): identity by value (string team + data
/// type ids), async at every boundary, failure as `Result<_, string>`
/// data, stateless between calls (every method derives its answer from
/// its parameters plus `IBlobStorage`), single-team reads with no
/// cross-shard ordering claim, no precision surface.
type IMigrationStatusStore =
    /// Status for one (team, data type). `None` when no pass has ever
    /// been recorded for the pair.
    abstract Read: teamId: string * dataTypeId: string -> Async<MigrationStatus option>
    /// Persist a status record, overwriting any prior one for the same
    /// pair.
    abstract Write: status: MigrationStatus -> Async<Result<unit, string>>
    /// Every recorded status for one team.
    abstract ListForTeam: teamId: string -> Async<MigrationStatus list>
    /// Every recorded status across every team — the platform-operator
    /// view. Expensive on a large deployment (one blob read per
    /// recorded pair); not for a per-request path.
    abstract ListAll: unit -> Async<MigrationStatus list>

module MigrationStatusStore =

    [<Literal>]
    let private platformContainer = "_platform"

    [<Literal>]
    let private rootPrefix = "migrations/"

    /// A blob path segment must not be able to climb out of its
    /// prefix. Team ids come from `ITeamStore` and data-type ids from
    /// module registration, so a traversal attempt here would be a
    /// deployment defect rather than an attack — but the scope
    /// isolation this layout provides (GP 4) is only structural if a
    /// segment cannot contain a separator, so it is checked rather
    /// than assumed.
    let isSafeSegment (segment: string) : bool =
        not (System.String.IsNullOrWhiteSpace segment)
        && not (segment.Contains "/")
        && not (segment.Contains "\\")
        && segment <> "."
        && segment <> ".."

    let statusBlob (teamId: string) (dataTypeId: string) =
        $"%s{rootPrefix}%s{teamId}/%s{dataTypeId}.json"

    let teamPrefix (teamId: string) = $"%s{rootPrefix}%s{teamId}/"

    // `MigrationStatus` round-trips to the Fable admin UI, so use
    // `FableConverters` — the DU-aware shape `Fable.SimpleJson` reads
    // on the client. Same pattern as `DataSourceConfigStore`,
    // `WebhookRegistry`, `BlobJobStore`.
    module private Json =
        let private options = FableConverters.create ()

        let serialize (value: 'T) : byte[] =
            JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

        let tryDeserialize<'T> (bytes: byte[]) : 'T option =
            try
                Some(JsonSerializer.Deserialize<'T>(Encoding.UTF8.GetString bytes, options))
            with _ ->
                None

    /// Blob-backed default. Every read is prefix-scoped to one team;
    /// the store never widens a prefix during a scoped operation, so a
    /// cross-team read is structurally unreachable rather than filtered
    /// out afterwards (GP 4).
    type BlobMigrationStatusStore(storage: IBlobStorage) =

        let readBlob (name: string) = async {
            let! result = storage.Download(platformContainer, name)

            return
                match result with
                | Ok bytes -> Json.tryDeserialize<MigrationStatus> bytes
                | Error _ -> None
        }

        let readMany (names: string list) = async {
            let! results = names |> List.map readBlob |> Async.Parallel
            return results |> Array.choose id |> Array.toList
        }

        interface IMigrationStatusStore with

            member _.Read(teamId, dataTypeId) =
                if not (isSafeSegment teamId && isSafeSegment dataTypeId) then
                    async.Return None
                else
                    readBlob (statusBlob teamId dataTypeId)

            member _.Write(status) = async {
                if not (isSafeSegment status.TeamId && isSafeSegment status.DataTypeId) then
                    return
                        Error
                            $"Unsafe migration-status key: team '%s{status.TeamId}', data type '%s{status.DataTypeId}'."
                else
                    let! result =
                        storage.Upload(
                            platformContainer,
                            statusBlob status.TeamId status.DataTypeId,
                            Json.serialize status
                        )

                    return result |> Result.map ignore
            }

            member _.ListForTeam(teamId) = async {
                if not (isSafeSegment teamId) then
                    return []
                else
                    let! names = storage.List(platformContainer, teamPrefix teamId)
                    return! readMany names
            }

            member _.ListAll() = async {
                let! names = storage.List(platformContainer, rootPrefix)
                return! readMany names
            }

    /// Construct the blob-backed store over the deployment's resolved
    /// `IBlobStorage`.
    let create (storage: IBlobStorage) : IMigrationStatusStore =
        BlobMigrationStatusStore(storage) :> IMigrationStatusStore
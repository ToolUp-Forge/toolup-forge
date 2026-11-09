module ToolUp.Platform.Config.FeatureFlagStore

open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

// ─── Blob layout ─────────────────────────────────────────────────

let private platformContainer = ScopeKey.PlatformContainer

/// One blob per scope — the whole flag map for a scope lives in one
/// document. Hot reads (the three-layer walk in `FlagEvaluator`) are
/// three downloads, not N per layer. `FlagScope.slug` encodes the
/// scope kind (`user-`, `team-`, `_platform`) into the path, so
/// user-ids and team-ids that happen to match never collide.
let private blobName (scope: FlagScope) = $"flags/{FlagScope.slug scope}.json"

// ─── IFeatureFlagStore — blob-backed implementation ─────────────

/// Blob-backed `IFeatureFlagStore`: one document per scope over the
/// document store, so a flag document that cannot be read or decoded
/// REFUSES the read (`DocumentUnavailableException`) and the write
/// (`Error`) rather than reading as "no flags" and writing that back over
/// every other flag in the scope. No caching layer. Callers that hot-read the same flag on
/// every request should wrap this behind a short-TTL cache (and
/// usually will: `FlagEvaluator` runs per request).
///
/// Named class (rather than object expression) for consistency with
/// `BlobConfigStore` — the interface doesn't need generics here, but
/// matching the store family's shape keeps the copy-paste cost of the
/// next implementation low.
type BlobFeatureFlagStore(storage: IBlobStorage) =
    let docs =
        BlobDocumentStore<Map<string, FlagValue>>(
            storage,
            BlobCodec.json<Map<string, FlagValue>>,
            ConsoleLogger.ConsoleLogger() :> ILogger
        )

    let read (path: string) = async {
        let! loaded = docs.Read(ScopeKey.platform, DocPath.ofString path)
        return loaded |> Result.map (Option.defaultValue Map.empty)
    }

    let load (scope: FlagScope) = async {
        let! loaded = read (blobName scope)
        return DocumentUnavailable.orRaise loaded
    }

    let update (scope: FlagScope) (change: Map<string, FlagValue> -> BlobUpdate<Map<string, FlagValue>, unit>) =
        docs.Update(
            ScopeKey.platform,
            DocPath.ofString (blobName scope),
            (fun current -> change (defaultArg current Map.empty))
        )

    interface IFeatureFlagStore with
        member _.GetFlag(scope, key) = async {
            let! m = load scope
            return Map.tryFind key m
        }

        member _.ListFlags scope = load scope

        member _.SetFlag(scope, key, value) = async {
            let! written = update scope (fun m -> BlobUpdate.Write(Map.add key value m, ()))
            return written |> Result.mapError BlobMapStoreError.describe
        }

        member _.ClearFlag(scope, key) = async {
            let! cleared =
                update scope (fun m ->
                    if Map.containsKey key m then
                        BlobUpdate.Write(Map.remove key m, ())
                    else
                        BlobUpdate.Keep())

            return DocumentUnavailable.orRaise cleared
        }

        member _.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId.Value then
                return
                    Result.Ok {
                        HandlerName = "feature-flags"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op"
                    }
            else
                let docBlob = $"flags/{scopeId}.json"
                let! loaded = read docBlob

                match loaded with
                | Error e ->
                    return
                        Result.Error(
                            ErasureError.StoreUnreachable(
                                "feature-flags",
                                sprintf "flag document %s could not be read: %s" docBlob (BlobMapStoreError.describe e)
                            )
                        )
                | Ok flags ->

                    let namesSubject (v: FlagValue) =
                        match v with
                        | FlagValue.Bool _ -> false
                        | FlagValue.Variant(options, value) ->
                            value.Contains subjectUserId.Value
                            || (options |> List.exists (fun o -> o.Contains subjectUserId.Value))

                    let matchedKeys =
                        flags
                        |> Map.toList
                        |> List.choose (fun (k, v) -> if namesSubject v then Some k else None)

                    if dryRun || List.isEmpty matchedKeys then
                        let verb =
                            match policy with
                            | ErasurePolicy.HardDelete -> "removed"
                            | _ -> "redacted"

                        return
                            Result.Ok {
                                HandlerName = "feature-flags"
                                RecordsAffected = matchedKeys.Length
                                Note =
                                    Some(sprintf "%d flag(s) would be %s in scope %s" matchedKeys.Length verb scopeId)
                            }
                    else
                        let matchedSet = Set.ofList matchedKeys

                        let updated =
                            match policy with
                            | ErasurePolicy.HardDelete -> flags |> Map.filter (fun k _ -> not (matchedSet.Contains k))
                            | ErasurePolicy.Tombstone
                            | ErasurePolicy.RetainPerCompliance ->
                                flags
                                |> Map.map (fun k v ->
                                    if matchedSet.Contains k then
                                        FlagValue.Variant([], Erasure.TombstoneMarker)
                                    else
                                        v)

                        // Phase 863 — an erasure that did not land is not
                        // reported as one: a refused rewrite fails the handler.
                        // Phase 971 — so is a refused delete of a document every
                        // flag of which named the subject.
                        let! written =
                            if Map.isEmpty updated then
                                storage.Delete(platformContainer, docBlob)
                            else
                                async {
                                    match!
                                        storage.Upload(platformContainer, docBlob, PlatformJson.serialize updated)
                                    with
                                    | Ok _ -> return Ok()
                                    | Error storageError -> return Error storageError
                                }

                        match written with
                        | Error storageError ->
                            return
                                Result.Error(
                                    ErasureError.StoreUnreachable(
                                        "feature-flags",
                                        sprintf
                                            "flag document %s could not be rewritten or removed: %s"
                                            docBlob
                                            storageError
                                    )
                                )
                        | Ok() ->
                            let verb =
                                match policy with
                                | ErasurePolicy.HardDelete -> "removed"
                                | _ -> "redacted"

                            return
                                Result.Ok {
                                    HandlerName = "feature-flags"
                                    RecordsAffected = matchedKeys.Length
                                    Note = Some(sprintf "%d flag(s) %s in scope %s" matchedKeys.Length verb scopeId)
                                }
        }

/// Convenience factory — construct and upcast. Mirrors the `create`
/// pattern used by other stores in the SDK.
let create (storage: IBlobStorage) : IFeatureFlagStore =
    BlobFeatureFlagStore(storage) :> IFeatureFlagStore
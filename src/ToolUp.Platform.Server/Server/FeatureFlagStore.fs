module ToolUp.Platform.FeatureFlagStore

open System.Text
open Newtonsoft.Json
open ToolUp.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── JSON helpers ────────────────────────────────────────────────

/// Shared `FableJsonConverter` setup — same pattern as `ConfigStore`
/// and `BlobProviderProfile`. The converter handles F# records,
/// DUs, and `option` types losslessly, which matters here because
/// `FlagValue` is a DU that must round-trip through `Fable.SimpleJson`
/// on the client (the platform's non-Remoting serialisation rule).
module private Json =
    let private settings =
        let s = JsonSerializerSettings()
        s.Converters.Add(FableJsonConverter())
        s

    let serialize (m: Map<string, FlagValue>) : byte[] =
        JsonConvert.SerializeObject(m, settings) |> Encoding.UTF8.GetBytes

    let tryDeserialize (bytes: byte[]) : Map<string, FlagValue> option =
        try
            let json = Encoding.UTF8.GetString(bytes)
            Some(JsonConvert.DeserializeObject<Map<string, FlagValue>>(json, settings))
        with _ ->
            None

// ─── Blob layout ─────────────────────────────────────────────────

let private platformContainer = "_platform"

/// One blob per scope — the whole flag map for a scope lives in one
/// document. Hot reads (the three-layer walk in `FlagEvaluator`) are
/// three downloads, not N per layer. `FlagScope.slug` encodes the
/// scope kind (`user-`, `team-`, `_platform`) into the path, so
/// user-ids and team-ids that happen to match never collide.
let private blobName (scope: FlagScope) = $"flags/{FlagScope.slug scope}.json"

// ─── IFeatureFlagStore — blob-backed implementation ─────────────

/// Blob-backed `IFeatureFlagStore`. Thread-safe via the underlying
/// storage — no caching layer. Callers that hot-read the same flag on
/// every request should wrap this behind a short-TTL cache (and
/// usually will: `FlagEvaluator` runs per request).
///
/// Named class (rather than object expression) for consistency with
/// `BlobConfigStore` — the interface doesn't need generics here, but
/// matching the store family's shape keeps the copy-paste cost of the
/// next implementation low.
type BlobFeatureFlagStore(storage: IBlobStorage) =
    let load (scope: FlagScope) = async {
        let! result = storage.Download(platformContainer, blobName scope)

        match result with
        | Ok bytes -> return Json.tryDeserialize bytes |> Option.defaultValue Map.empty
        | Error _ -> return Map.empty
    }

    let save (scope: FlagScope) (m: Map<string, FlagValue>) = async {
        let! result = storage.Upload(platformContainer, blobName scope, Json.serialize m)
        return result |> Result.map (fun _ -> ())
    }

    interface IFeatureFlagStore with
        member _.GetFlag(scope, key) = async {
            let! m = load scope
            return Map.tryFind key m
        }

        member _.ListFlags scope = load scope

        member _.SetFlag(scope, key, value) = async {
            let! m = load scope
            return! save scope (Map.add key value m)
        }

        member _.ClearFlag(scope, key) = async {
            let! m = load scope

            if Map.containsKey key m then
                let! _ = save scope (Map.remove key m)
                return ()
        }

        member _.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "feature-flags"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op"
                    }
            else
                let docBlob = $"flags/{scopeId}.json"
                let! result = storage.Download(platformContainer, docBlob)

                let flags =
                    match result with
                    | Ok bytes -> Json.tryDeserialize bytes |> Option.defaultValue Map.empty
                    | Error _ -> Map.empty

                let namesSubject (v: FlagValue) =
                    match v with
                    | FlagValue.Bool _ -> false
                    | FlagValue.Variant(options, value) ->
                        value.Contains subjectUserId
                        || (options |> List.exists (fun o -> o.Contains subjectUserId))

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
                            Note = Some(sprintf "%d flag(s) would be %s in scope %s" matchedKeys.Length verb scopeId)
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

                    if Map.isEmpty updated then
                        let! _ = storage.Delete(platformContainer, docBlob)
                        ()
                    else
                        let! _ = storage.Upload(platformContainer, docBlob, Json.serialize updated)
                        ()

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
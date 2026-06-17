module ToolUp.Platform.DataCatalog

open ToolUp.Platform
open DataManagementTypes
open ToolUp.Platform.FileProcessor

// ─── DataCatalog ─────────────────────────────────────────────────
//
// In-process implementation of `IDataCatalog`. The registration set
// is fixed at construction time (modules are loaded once at server
// startup and never added later), so the catalog stores the
// `(moduleName, DataType) list` directly and answers queries by
// scanning it. No background refresh, no DI lookups per call.
//
// The catalog deduplicates by `Info.Id` for `ListTypes` — if two
// modules both declare a type with the same `Id`, the catalog
// surfaces it once with both module names in `GetProducers`.
//
// `ListObjects` delegates to `IDataObjectStore.ListObjects` and
// filters by `DataType`. The catalog has no opinion on the
// `IDataObjectStore` implementation — it composes through the
// interface, so a distributed object store would slot in without
// touching this file.

/// `(moduleName, DataType)` pairs accumulated by the compose flow,
/// one entry per data type each `ServerModule` registered.
type DataTypeRegistration = {
    ModuleName: string
    DataType: DataType
}

type DataCatalog(registrations: DataTypeRegistration list, objectStore: IDataObjectStore) =

    /// Index by `Info.Id` for O(1) schema and producer lookups.
    /// Multiple producers per id are preserved as a list.
    let byId = registrations |> List.groupBy (fun r -> r.DataType.Info.Id) |> Map.ofList

    interface IDataCatalog with
        member _.ListTypes() = async {
            // Preserve declaration order across modules; deduplicate
            // by `Id` keeping the first declaration's `Info`.
            let seen = System.Collections.Generic.HashSet<string>()

            let unique =
                registrations
                |> List.choose (fun r ->
                    if seen.Add r.DataType.Info.Id then
                        Some r.DataType.Info
                    else
                        None)

            return unique
        }

        member _.GetSchema(typeId) = async {
            return
                byId
                |> Map.tryFind typeId
                |> Option.bind (fun rs -> rs |> List.tryPick (fun r -> r.DataType.Info.Schema))
        }

        member _.GetProducers(typeId) = async {
            return
                byId
                |> Map.tryFind typeId
                |> Option.map (fun rs -> rs |> List.map (fun r -> r.ModuleName) |> List.distinct)
                |> Option.defaultValue []
        }

        member _.ListObjects(scopeId, typeId) = async {
            let! all = objectStore.ListObjects scopeId
            return all |> List.filter (fun obj -> obj.DataType = typeId)
        }

        member _.CountObjects(scopeId, typeId) = async {
            // Native fast-path when the store can count without
            // enumerating (GP 12); otherwise list-and-count — the same
            // result, paid for in full materialisation.
            match box objectStore with
            | :? IObjectCounter as counter -> return! counter.CountObjects(scopeId, typeId)
            | _ ->
                let! all = objectStore.ListObjects scopeId
                return all |> List.filter (fun obj -> obj.DataType = typeId) |> List.length
        }

        member _.GetSyntheticSample(typeId, count, seed) = async {
            // Schema lookup mirrors GetSchema's path — picks the first
            // registered schema for `typeId` across producers (catalog
            // contract is "first declaration wins" for cross-module
            // shared shapes).
            let schema =
                byId
                |> Map.tryFind typeId
                |> Option.bind (fun rs -> rs |> List.tryPick (fun r -> r.DataType.Info.Schema))

            // In-process cap is the generator's `Int32.MaxValue` ceiling;
            // the per-scope partner-sandbox cap is enforced by the gate
            // that fronts this method (the Phase 30d shielding layer),
            // not by the substrate generator. Keeps SyntheticSampleGenerator
            // testable in isolation and respects the SDK's "substrate
            // doesn't read config" rule.
            return SyntheticSampleGenerator.generate typeId schema count seed System.Int32.MaxValue
        }
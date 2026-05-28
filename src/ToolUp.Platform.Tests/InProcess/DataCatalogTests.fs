module ToolUp.Platform.Tests.InProcess.DataCatalogTests

open System
open ToolUp.Platform
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Tests.Contracts

/// Bind the `IDataCatalog` contract test pack to the in-process
/// `DataCatalog` implementation. Each factory call constructs a
/// fresh catalog over the supplied object store; scope ids are
/// GUID-suffixed to keep `ListObjects` assertions on different
/// tests from interfering when they share the same in-memory store
/// instance per call.
let tests =
    let factory (registrations: (string * DataType) list, objectStore: IDataObjectStore) =
        let catalogRegistrations: DataCatalog.DataTypeRegistration list =
            registrations |> List.map (fun (m, dt) -> { ModuleName = m; DataType = dt })

        let catalog =
            DataCatalog.DataCatalog(catalogRegistrations, objectStore) :> IDataCatalog

        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        catalog, "team-a-" + suffix, "team-b-" + suffix

    IDataCatalogContract.tests "DataCatalog" factory
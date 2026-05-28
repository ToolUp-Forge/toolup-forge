module ToolUp.Platform.Tests.InProcess.InMemoryDataSourceTests

open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

// ─── InMemoryDataSource — IDataSource contract binding ───────────
//
// Binds the `IDataSource` contract pack to the in-memory connector
// shipped in `InMemoryDataSource.fs`. Each factory call gets a
// fresh connector with an empty content map; the seeder forwarded
// to the contract pack lets tests populate it.

let tests =
    let factory () =
        let source = InMemoryDataSource.create ()
        let seeder (sourceId: DataSourceId) (table: string) (bytes: byte[]) = source.Seed(sourceId, table, bytes)
        source :> IDataSource, seeder

    IDataSourceContract.tests "InMemoryDataSource" factory
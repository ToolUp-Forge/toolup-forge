module ToolUp.Forms.Tests.InProcess.FormStoreTests

open System
open ToolUp.Platform.IEntityStore
open ToolUp.Forms.IFormStore
open ToolUp.Forms.FormStore
open ToolUp.Forms.Tests.InProcess.InMemoryEntityStore
open ToolUp.Forms.Tests.Contracts

/// Bind the IFormStore contract pack to the default FormStore impl
/// over the in-memory IEntityStore stub. Future distributed
/// companions bind the same pack against their own factory.
let tests =
    let factory () =
        let entityStore = InMemoryEntityStore() :> IEntityStore
        let store = FormStore(entityStore) :> IFormStore
        let scopeA = "team-a-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let scopeB = "team-b-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        store, scopeA, scopeB

    IFormStoreContract.tests "FormStore (in-memory)" factory
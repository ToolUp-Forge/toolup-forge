module ToolUp.Platform.Tests.InProcess.ModuleQueryBusTests

open ToolUp.Platform
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── InMemoryModuleQueryBus — IModuleQueryBus contract binding ───
//
// Binds the `IModuleQueryBus` contract pack to the Phase 6b shipped
// in-process implementation. The bus takes its registry as a
// constructor argument, so the contract factory builds the registry
// from the test's `(moduleName, handler) list` via the public
// `ModuleQueryBus.buildRegistry` helper.

let tests =
    let logger: ILogger = ConsoleLogger.ConsoleLogger()

    let factory (registrations: (string * ModuleQueryHandler) list) : IModuleQueryBus =
        let registry = ModuleQueryBus.buildRegistry registrations
        ModuleQueryBus.InMemoryModuleQueryBus(registry, logger, NoOpActivitySink() :> IActivitySink) :> IModuleQueryBus

    IModuleQueryBusContract.tests "InMemoryModuleQueryBus" factory